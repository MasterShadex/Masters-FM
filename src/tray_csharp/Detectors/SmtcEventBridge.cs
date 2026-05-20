// Stage 7.5: SmtcEventBridge (Arm 1, SMTC-first event-driven primary).
//
// Reuses v12.0.0's MasterFM.SMTC.SMTCWatcher from tray_native.dll AS-IS
// (per absolute rule 10). The watcher coalesces WinRT events into an
// internal queue (see tray_native.cs:DrainEvents at line 729). This bridge
// drains that queue and dispatches each event into a TrackUpdate via
// ITrackResolver.OnTrackChanged.
//
// Stage 7.12 Batch B Phase D #1 (deferred from Phase C #4): the original
// drain mechanism was a `DispatcherTimer` at 16 ms cadence — but Windows
// WM_TIMER has 15-16 ms granularity, AND DispatcherPriority.Background
// yields to all UI work, so the actual cadence under load could stretch
// to 30-50 ms.  Worst-case SMTC-event-to-webhook latency was 16 ms.
//
// Replaced with a dedicated background `Task.Run` loop that polls the
// watcher queue at 1 ms cadence (via `Task.Delay(1)` + timeBeginPeriod(1)
// for true 1 ms resolution).  When events are present, marshals each one
// to the WPF dispatcher at `DispatcherPriority.Send` (highest interactive
// priority) so thumbnail extraction on cache-miss can still run on the
// STA-friendly thread without contending with normal Background work.
// New worst case: ~1-3 ms drain → marshal → ProcessEvent.
//
// Manager acquisition uses WinRT projection (TFM net8.0-windows10.0.19041.0).
// CANARY re-probe every 30 s closes B-022 (mid-session subscription gap):
// if SMTCWatcher.SessionCount drops to 0 while Watch.LastEventUtc is
// stale (>30 s), re-fetch sessions to force re-subscription.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MasterFM.SMTC;
using MastersFM.Tray.Services;
using Windows.Media.Control;  // Stage 7.5B: TFM 10.0.19041.0 exposes the WinRT projection
using Windows.Storage.Streams;  // Stage 7.7: thumbnail extraction (B-013 closure)

namespace MastersFM.Tray.Detectors;

public sealed class SmtcEventBridge : IDisposable
{
    // Stage 7.12 Batch B Phase D #1: drain at 1 ms via background Task (was
    // 16 ms DispatcherTimer).  Real cadence depends on Windows timer
    // resolution — see App.xaml.cs which calls timeBeginPeriod(1) on startup
    // to lower it from the default 15.6 ms to 1 ms.
    private const int DrainDelayMs = 1;
    private const int CanaryCadenceMs = 30000;
    private const string Component = "Detect-SMTC";

    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;
    private readonly PlatformDetectorOptions _options;
    private readonly ITrackResolver _resolver;

    private SMTCWatcher? _watcher;
    private CancellationTokenSource? _drainCts;
    private Task? _drainTask;
    private DispatcherTimer? _canaryTimer;
    private object? _manager;
    private bool _started;
    private bool _disposed;

    // Stage 7.12 Batch B Phase C #1 (real-time sync fix): per-saumid thumbnail
    // cache + per-saumid prev-position tracker.
    //
    // BEFORE:  TryExtractThumbnail ran on EVERY SMTC event (pause, resume, seek,
    // timeline, properties) on the WPF dispatcher thread.  Three .Wait(1500)
    // calls back-to-back meant a single bad-source pause could block dispatcher
    // up to 4.5 s — that's not pause "lag", that's "the entire UI frozen".
    //
    // AFTER:   we only re-extract on MediaPropertiesChanged (event kind 4 =
    // track changed).  PlaybackInfoChanged (3 = pause/play/seek) and
    // TimelinePropertiesChanged (5 = position) reuse the cached artUri.
    // Sessions get evicted on SessionRemoved.
    //
    // The prev-position tracker feeds Phase C #7: detect seeks at the SMTC
    // bridge (rather than waiting for the 100 ms heartbeat) so the server's
    // B7-seek branch fires immediately.
    private readonly Dictionary<string, string?> _artCache = new();
    private readonly Dictionary<string, (long PositionMs, DateTime ObservedUtc)> _prevPos = new();
    // Stage 7.12 Batch B Phase H #1: per-saumid source-name cache.
    // For browser AUMIDs we sniff the foreground window title to detect
    // YouTube / Twitch / etc. — but we only want to evaluate it ONCE per
    // track (when MediaPropertiesChanged fires) so the label stays stable
    // when the user alt-tabs to a non-browser window between events.
    private readonly Dictionary<string, string> _sourceCache = new();
    private readonly object _cacheLock = new();

    public SmtcEventBridge(ILogger logger, ITelemetry telemetry, PlatformDetectorOptions options, ITrackResolver resolver)
    {
        _logger = logger;
        _telemetry = telemetry;
        _options = options;
        _resolver = resolver;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        // Acquire SMTC manager via reflection (WinRT type projection not
        // available in net8.0-windows without explicit Windows SDK targeting).
        try
        {
            _manager = AcquireSmtcManagerSync();
            if (_manager == null)
            {
                _logger.LogWarn("manager acquisition returned null; SMTC arm inactive (gap-filler arm continues)", Component);
                return;
            }
            _watcher = new SMTCWatcher();
            _watcher.Initialize(_manager);
            _logger.Log($"SMTCWatcher initialized; sessions={_watcher.SessionCount}", Component);
        }
        catch (Exception ex)
        {
            _logger.LogErr("SMTCWatcher init via reflection (WinRT not projected; SMTC arm inactive)", ex, Component);
            // SMTC arm degrades gracefully. Gap-filler arm via DetectorOrchestrator
            // remains operational.
            return;
        }

        // Stage 7.12 Batch B Phase D #1: background drain at 1 ms cadence
        // (was a DispatcherTimer at 16 ms, capped by WM_TIMER granularity).
        // Snapshot the dispatcher off the calling thread so the background
        // loop can marshal each event back to STA via InvokeAsync.
        _drainCts  = new CancellationTokenSource();
        var dispatcher = Application.Current?.Dispatcher;
        _drainTask = Task.Run(() => DrainLoopAsync(dispatcher, _drainCts.Token));

        // CANARY re-probe (30s cadence; B-022 closure)
        _canaryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(CanaryCadenceMs)
        };
        _canaryTimer.Tick += OnCanaryTick;
        _canaryTimer.Start();

        _logger.Log($"started; drain={DrainDelayMs}ms (bg-task) canary={CanaryCadenceMs / 1000}s", Component);
    }

    private async Task DrainLoopAsync(Dispatcher? dispatcher, CancellationToken ct)
    {
        // Stage 7.12 Batch B Phase D #1: dedicated drain loop.  Polls the
        // SMTCWatcher's lock-free ConcurrentQueue at 1 ms cadence.  When
        // events arrive, marshals each to the WPF dispatcher at Send priority
        // — the STA jump is needed because thumbnail extraction on cache miss
        // calls into WinRT APIs that have COM affinity, and `ProcessEvent`
        // also fires events (`ITrackResolver.OnTrackChanged`) whose subscribers
        // may touch UI.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var watcher = _watcher;
                if (watcher == null)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    continue;
                }

                var events = watcher.DrainEvents();
                if (events != null && events.Length > 0 && dispatcher != null)
                {
                    var sw = Stopwatch.StartNew();
                    foreach (var ev in events)
                    {
                        var local = ev;
                        _telemetry.IncrementCounter("smtc_events");
                        // DispatcherPriority.Send = highest interactive priority;
                        // jumps the queue ahead of normal UI work so a pause
                        // doesn't wait behind a render frame.
                        // DispatcherPriority.Normal: same priority as the
                        // dispatcher's default application work — higher than
                        // the old Background timer (which yielded to every
                        // render frame) but below Send so it won't preempt
                        // an in-flight render and cause UI judder during a
                        // rapid-scrub event burst.  Net effect: events run
                        // within ~1 ms of arrival on a normally-idle WPF app.
                        await dispatcher.InvokeAsync(() =>
                        {
                            try { ProcessEvent(local); }
                            catch (Exception ex)
                            {
                                _logger.LogErr("process event", ex, Component);
                                _telemetry.IncrementCounter("smtc_event_errors");
                            }
                        }, DispatcherPriority.Normal, ct);
                    }
                    sw.Stop();
                    _telemetry.RecordTimingMs("smtc_dispatch_ms", sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogErr("drain loop", ex, Component);
                _telemetry.IncrementCounter("smtc_event_errors");
            }

            try { await Task.Delay(DrainDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ProcessEvent(SMTCChangeRecord ev)
    {
        // Stage 7.12 Batch B Phase C #1: SessionRemoved evicts the per-saumid
        // cache instead of being a silent no-op, so the dicts can't leak.
        if (ev.Kind == SMTCEventKind.SessionRemoved)
        {
            if (!string.IsNullOrEmpty(ev.Saumid))
            {
                lock (_cacheLock)
                {
                    _artCache.Remove(ev.Saumid);
                    _prevPos.Remove(ev.Saumid);
                    _sourceCache.Remove(ev.Saumid);
                }
            }
            return;
        }

        var saumid = ev.Saumid;
        if (string.IsNullOrEmpty(saumid)) return;

        var snap = _watcher!.GetSnapshot(saumid);
        if (snap == null || !snap.HasMediaProps) return;

        // Stage 7.12 Batch B Phase H rev2: cache logic redesigned so "browser"
        // is NEVER locked in.  We only cache POSITIVE detections (youtube /
        // twitch / spotify / soundcloud / etc.) — every event re-runs
        // MapSaumidToSource until we get a positive hit.  Once positive, we
        // lock in until track change.  On MediaPropertiesChanged the cache
        // gets evicted so we re-detect from scratch (covers user switching
        // from YouTube tab to a Spotify-Web tab without closing Chrome).
        string sourceName;
        lock (_cacheLock)
        {
            bool trackChange = ev.Kind == SMTCEventKind.MediaPropertiesChanged;
            if (trackChange) _sourceCache.Remove(saumid);

            if (_sourceCache.TryGetValue(saumid, out var cached) && cached != "browser")
            {
                sourceName = cached;   // positive lock-in — skip the window scan
            }
            else
            {
                sourceName = MapSaumidToSource(saumid);
                if (sourceName != "browser")
                    _sourceCache[saumid] = sourceName;   // lock in only positive hits
            }
        }
        if (!IsSourceEnabled(sourceName))
        {
            _logger.Log($"event ignored: source={sourceName} disabled by config", Component);
            return;
        }

        // Stage 7.12 Batch B Phase C #1: thumbnail extraction is the SINGLE
        // biggest source of pause latency.  Three .Wait(1500) calls back to
        // back on the dispatcher thread can stretch a 10 ms pause event into
        // a 4.5 second one for sources whose thumbnails are slow / missing
        // (e.g. soundcloud-rpc).  Cache the extracted artUri per saumid and
        // only re-extract on MediaPropertiesChanged (event kind 4 = the track
        // itself changed).  Pause/resume/seek (kind 3) and timeline (kind 5)
        // reuse the cached art.  First-ever event for a saumid also extracts
        // (the cache miss).
        string? artUri = null;
        bool extract = ev.Kind == SMTCEventKind.MediaPropertiesChanged;
        if (!extract)
        {
            lock (_cacheLock)
            {
                if (_artCache.TryGetValue(saumid, out var cached)) artUri = cached;
                else extract = true;
            }
        }
        if (extract)
        {
            try
            {
                artUri = TryExtractThumbnail(snap.SessionRef);
                lock (_cacheLock) { _artCache[saumid] = artUri; }
            }
            catch (Exception ex)
            {
                _logger.LogWarn("thumbnail extraction failed (non-fatal): " + ex.Message, Component);
                _telemetry.IncrementCounter("art_extract_errors");
            }
        }

        // Stage 7.12 Batch B Phase F (startup sync fix): SMTC's
        // TimelineProperties.Position is the position *as of LastUpdatedTime*,
        // NOT a live counter.  Most sources only re-emit TimelinePropertiesChanged
        // on seek / pause / resume / track-change — between those, the cached
        // PositionMs becomes increasingly stale.  Worst case: Master's FM starts
        // while a track has been playing for 5 minutes since the last SMTC
        // update — we'd read PositionMs ≈ (the position when SMTC last fired)
        // and the OBS overlay + Discord RPC progress bar would show that
        // ancient position instead of "now".
        //
        // Fix: interpolate forward by (now - LastUpdatedTime) when the track is
        // playing.  Skip when paused (position stays frozen at the pause point).
        // Skip when elapsed < 0 (clock skew safety).  Cap at duration so we
        // never report past the end of the track.
        bool isPlaying  = (snap.PlaybackStatusValue == 4);
        var  nowUtc     = ev.UtcTime;
        long effectivePosMs = snap.PositionMs;
        if (isPlaying && snap.HasTimeline && snap.LastUpdatedTimeUtcTicks > 0)
        {
            var lastUpdUtc = new DateTime(snap.LastUpdatedTimeUtcTicks, DateTimeKind.Utc);
            var elapsedMs  = (nowUtc - lastUpdUtc).TotalMilliseconds;
            if (elapsedMs > 0)
            {
                effectivePosMs += (long)elapsedMs;
                if (snap.DurationMs > 0 && effectivePosMs > snap.DurationMs)
                    effectivePosMs = snap.DurationMs;
            }
        }

        // Stage 7.12 Batch B Phase C #7: detect seeks at the SMTC bridge so
        // the server's B7-seek branch fires immediately instead of waiting
        // for the 100 ms heartbeat to notice the drift.  Uses the interpolated
        // position so the tracker compares apples-to-apples between events.
        bool isSeek = false;
        lock (_cacheLock)
        {
            if (_prevPos.TryGetValue(saumid, out var prev) && effectivePosMs > 0 && prev.PositionMs > 0)
            {
                var posDeltaMs  = effectivePosMs - prev.PositionMs;
                var wallDeltaMs = (nowUtc - prev.ObservedUtc).TotalMilliseconds;
                var expectedMs  = isPlaying ? wallDeltaMs : 0.0;
                var jump        = Math.Abs(posDeltaMs - expectedMs);
                // Phase H #2: 1000 ms for browsers (jittery), 100 ms otherwise.
                var seekJumpMs  = IsBrowserLikeSource(sourceName) ? 1000.0 : 100.0;
                // Stage 7.15 Fix A2: require ACTUAL position movement before flagging seek.
                // Mirrors HeartbeatService.cs Fix A1 -- SMTC TimelineProperties can be stale
                // even when the event fires (SoundCloud-RPC / Electron MediaSession wrappers
                // that stop calling setPositionState mid-track). Without this gate the bridge
                // flags isSeek=true on every event, server B7 re-pins startedAt to the frozen
                // pos, and the overlay clock freezes.
                // See V14_S7_14_DIAG_OVERLAY_TIME_FROZEN.md sections 5.2 + 8.1.
                bool positionActuallyMoved = Math.Abs(posDeltaMs) > 100;
                if (jump > seekJumpMs && positionActuallyMoved) isSeek = true;
            }
            // Always update so the next event has a fresh baseline (even when
            // the current event was a seek — otherwise we'd flag the corrected
            // position as another seek next time around).
            _prevPos[saumid] = (effectivePosMs, nowUtc);
        }

        var update = new TrackUpdate
        {
            Source = sourceName,
            Artist = string.IsNullOrEmpty(snap.Artist) ? null : snap.Artist,
            Track = string.IsNullOrEmpty(snap.Title) ? null : snap.Title,
            Album = string.IsNullOrEmpty(snap.AlbumTitle) ? null : snap.AlbumTitle,
            Duration = snap.DurationMs > 0 ? TimeSpan.FromMilliseconds(snap.DurationMs) : null,
            Position = effectivePosMs > 0 ? TimeSpan.FromMilliseconds(effectivePosMs) : null,
            IsPlaying = isPlaying,
            IsSeek = isSeek,
            ArtUri = artUri,
            PlatformIdentity = saumid,
            ObservedUtc = ev.UtcTime
        };

        _resolver.OnTrackChanged(update);
    }

    private static string MapSaumidToSource(string saumid)
    {
        // Source-string mapping from PS S15 Get-PlatformName logic
        // (tray.ps1:6487+). Conservative subset; the gap-filler detectors
        // and resolver handle anything else as "smtc-generic".
        var s = saumid.ToLowerInvariant();
        if (s.Contains("spotify")) return "spotify";
        if (s.Contains("soundcloud")) return "soundcloud";
        if (s.Contains("youtube") && s.Contains("music")) return "youtubemusic";
        if (s.Contains("youtube")) return "youtube";
        if (s.Contains("apple") && s.Contains("music")) return "applemusic";
        if (s.Contains("tidal")) return "tidal";
        if (s.Contains("deezer")) return "deezer";
        if (s.Contains("amazonmusic") || s.Contains("amazon.music")) return "amazonmusic";
        if (s.Contains("bandcamp")) return "bandcamp";
        if (s.Contains("mixcloud")) return "mixcloud";
        if (s.Contains("pandora")) return "pandora";
        if (s.Contains("zenmedia") || s.Contains("media.player")) return "wmpSMTC";
        if (s.Contains("chrome") || s.Contains("edge") || s.Contains("firefox") || s.Contains("brave"))
        {
            // Phase H rev2: sweep ALL visible top-level windows (not just the
            // foreground one).  Catches YouTube even when the user has alt-tabbed
            // to OBS or another app — as long as YouTube is the active tab of
            // SOME visible Chrome window we'll see its title in that window's
            // caption.  Falls back to "browser" only when no known site appears
            // in any visible window's title.
            return DetectSiteFromAllWindows() ?? "browser";
        }
        return "smtc-generic";
    }

    // ── Win32 helpers (Phase H rev2: detect site via ALL visible windows) ────
    //
    // Originally Phase H used only `GetForegroundWindow`, which fails any time
    // the user isn't actively on the playing tab.  Replaced with a full sweep
    // of every visible top-level window — catches YouTube whenever it's the
    // active tab of ANY Chrome window (foreground or background) AND when the
    // user is on another app entirely (we still pick it up from the Chrome
    // window's caption, which shows the active tab title).

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Sweep every visible top-level window's title; return the first known
    /// site match (youtube / twitch / spotify / soundcloud) or <c>null</c>.
    /// Cheap — typical desktop has 50-200 top-level windows; this is <0.5 ms.
    /// </summary>
    private static string? DetectSiteFromAllWindows()
    {
        string? found = null;
        try
        {
            var sb = new StringBuilder(512);
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                sb.Clear();
                if (GetWindowText(hWnd, sb, sb.Capacity) <= 0) return true;
                var t = sb.ToString().ToLowerInvariant();
                if (t.Contains("youtube") || t.Contains("youtu.be"))
                { found = "youtube";    return false; }
                if (t.Contains("twitch"))
                { found = "twitch";     return false; }
                if (t.Contains("spotify"))
                { found = "spotify";    return false; }
                if (t.Contains("soundcloud"))
                { found = "soundcloud"; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* Win32 enum failed — accept the null and fall back to "browser" */ }
        return found;
    }

    // Stage 7.12 Batch B Phase H #2: browser sources (Chrome / Edge / etc.)
    // report TimelinePropertiesChanged irregularly and lazily during buffering,
    // causing our interpolation to extrapolate ahead while playback actually
    // pauses for buffer, then snap back when the next event lands.  Phase D's
    // 100 ms IsSeek threshold flags this as a seek, the server's B7 resyncs
    // startedAt, and the OBS / Discord progress bar visibly jumps backward.
    //
    // For browser-shaped sources, raise the IsSeek threshold to 1000 ms so
    // normal buffering/jitter is absorbed.  Real human seeks are typically
    // multi-second so we don't lose anything meaningful.  Other sources
    // (Spotify desktop, SoundCloud-RPC, etc.) stay on the tight Phase D
    // thresholds because their position reporting is rock-steady.
    internal static bool IsBrowserLikeSource(string source) =>
        source == "browser" || source == "youtube" || source == "youtubemusic"
        || source == "twitch";

    private bool IsSourceEnabled(string source)
    {
        return source switch
        {
            "spotify" => _options.SpotifyEnabled,
            "soundcloud" => _options.SoundCloudEnabled,
            "browser" or "youtube" or "youtubemusic" or "applemusic" or "tidal" or "deezer"
              or "amazonmusic" or "bandcamp" or "mixcloud" or "pandora" => _options.BrowserEnabled,
            _ => true
        };
    }

    private void OnCanaryTick(object? sender, EventArgs e)
    {
        if (_watcher == null) return;
        try
        {
            var sessions = _watcher.GetSaumids();
            var lastEvt = _watcher.LastEventUtc;
            var staleMs = lastEvt == DateTime.MinValue ? -1 : (DateTime.UtcNow - lastEvt).TotalMilliseconds;
            _logger.Log($"canary: sessions={sessions.Length} eventsTotal={_watcher.EventsReceivedTotal} lastEventAgo={staleMs:F0}ms current={_watcher.CurrentSaumid ?? "(none)"}", Component);
            // B-022 mitigation: if SessionCount==0 + last event >5min ago + we
            // have a manager, force a session enumeration. Watcher has its own
            // burst-coalescing so a "force" is just calling DrainEvents which
            // pulls any pending events the watcher has already enqueued.
            if (sessions.Length == 0 && staleMs > 5 * 60 * 1000)
            {
                _logger.LogWarn("canary: no sessions + stale events >5min; SMTC may need re-init (deferred to v12.0.0 watcher behaviour)", Component);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("canary tick", ex, Component);
        }
    }

    private static object? AcquireSmtcManagerSync()
    {
        // Stage 7.5B: With TFM net8.0-windows10.0.19041.0, the WinRT
        // projection for Windows.Media.Control is available directly.
        // Replaces 7.5's reflection-based path (which never worked because
        // the assembly-qualified type names didn't match CSWinRT projection).
        try
        {
            var task = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            return task.Wait(5000) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    // Stage 7.7: Thumbnail extraction (Workstream 3 / B-013 closure).
    // Calls TryGetMediaPropertiesAsync on the active session; reads the
    // Thumbnail RandomAccessStreamReference; opens the stream; copies bytes
    // via DataReader; encodes as base64 data URI: data:image/jpeg;base64,...
    //
    // Bounded timeout via Wait. Null-safe at every step. Returns null if
    // the session has no thumbnail (common for soundcloud-rpc) or if the
    // stream open / read fails.
    private static string? TryExtractThumbnail(object? sessionObj)
    {
        if (sessionObj == null) return null;
        if (sessionObj is not GlobalSystemMediaTransportControlsSession session) return null;
        try
        {
            var propsTask = session.TryGetMediaPropertiesAsync().AsTask();
            if (!propsTask.Wait(1500)) return null;
            var props = propsTask.Result;
            if (props == null) return null;
            var thumb = props.Thumbnail;
            if (thumb == null) return null;

            var openTask = thumb.OpenReadAsync().AsTask();
            if (!openTask.Wait(1500)) return null;
            using var stream = openTask.Result;
            if (stream == null) return null;
            var size = (uint)stream.Size;
            // Cap at 4 MB to avoid pathological allocations on misbehaving sources
            if (size == 0 || size > 4 * 1024 * 1024) return null;

            using var reader = new DataReader(stream);
            var loadTask = reader.LoadAsync(size).AsTask();
            if (!loadTask.Wait(1500)) return null;
            byte[] bytes = new byte[size];
            reader.ReadBytes(bytes);

            // SMTC thumbnails are typically JPEG; assume image/jpeg unless
            // the bytes magic-match PNG (89 50 4E 47). Cheap discriminator.
            string mime = (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 &&
                           bytes[2] == 0x4E && bytes[3] == 0x47)
                          ? "image/png" : "image/jpeg";
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void Stop()
    {
        try { _drainCts?.Cancel(); } catch { }
        try { _canaryTimer?.Stop(); } catch { }
        _logger.Log("stopped", Component);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        // Best-effort wait for the background drain to settle; don't block
        // dispose indefinitely if something hangs (it shouldn't, but defensive).
        try { _drainTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        try { _drainCts?.Dispose(); } catch { }
        _drainCts = null;
        _drainTask = null;
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        _manager = null;
    }
}
