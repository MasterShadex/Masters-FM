// Stage 7.5: SmtcEventBridge (Arm 1, SMTC-first event-driven primary).
//
// Reuses v12.0.0's MasterFM.SMTC.SMTCWatcher from tray_native.dll AS-IS
// (per absolute rule 10). The watcher coalesces WinRT events into an
// internal queue (see tray_native.cs:DrainEvents at line 729). This bridge
// drains that queue on a 250ms timer (matches the watcher's BurstWindowMs
// internal coalescing window) and dispatches each event into a
// TrackUpdate via ITrackResolver.OnTrackChanged.
//
// Manager acquisition uses reflection because the project's net8.0-windows
// TFM does not include WinRT type projection (csproj is outside this
// brief's locked-list edit set). Pattern matches PS S13 reflection-based
// approach (tray.ps1:5301-5333).
//
// CANARY re-probe every 30s closes B-022 (mid-session subscription gap):
// if SMTCWatcher.SessionCount drops to 0 while Watch.LastEventUtc is
// stale (>30s), re-fetch sessions to force re-subscription.

using System.Reflection;
using System.Windows.Threading;
using MasterFM.SMTC;
using MastersFM.Tray.Services;
using Windows.Media.Control;  // Stage 7.5B: TFM 10.0.19041.0 exposes the WinRT projection

namespace MastersFM.Tray.Detectors;

public sealed class SmtcEventBridge : IDisposable
{
    private const int DrainCadenceMs = 250;
    private const int CanaryCadenceMs = 30000;
    private const string Component = "Detect-SMTC";

    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;
    private readonly PlatformDetectorOptions _options;
    private readonly ITrackResolver _resolver;

    private SMTCWatcher? _watcher;
    private DispatcherTimer? _drainTimer;
    private DispatcherTimer? _canaryTimer;
    private object? _manager;
    private bool _started;
    private bool _disposed;

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

        // Drain timer (250ms cadence; matches watcher internal BurstWindowMs)
        _drainTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(DrainCadenceMs)
        };
        _drainTimer.Tick += OnDrainTick;
        _drainTimer.Start();

        // CANARY re-probe (30s cadence; B-022 closure)
        _canaryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(CanaryCadenceMs)
        };
        _canaryTimer.Tick += OnCanaryTick;
        _canaryTimer.Start();

        _logger.Log("started; drain=250ms canary=30s", Component);
    }

    private void OnDrainTick(object? sender, EventArgs e)
    {
        if (_watcher == null) return;
        try
        {
            var events = _watcher.DrainEvents();
            if (events == null || events.Length == 0) return;
            foreach (var ev in events)
            {
                _telemetry.IncrementCounter("smtc_events");
                ProcessEvent(ev);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("drain tick", ex, Component);
            _telemetry.IncrementCounter("smtc_event_errors");
        }
    }

    private void ProcessEvent(SMTCChangeRecord ev)
    {
        // Only respond to events that imply a meaningful state change.
        if (ev.Kind == SMTCEventKind.SessionRemoved) return;

        var saumid = ev.Saumid;
        if (string.IsNullOrEmpty(saumid)) return;

        var snap = _watcher!.GetSnapshot(saumid);
        if (snap == null || !snap.HasMediaProps) return;

        var sourceName = MapSaumidToSource(saumid);
        if (!IsSourceEnabled(sourceName))
        {
            _logger.Log($"event ignored: source={sourceName} disabled by config", Component);
            return;
        }

        var update = new TrackUpdate
        {
            Source = sourceName,
            Artist = string.IsNullOrEmpty(snap.Artist) ? null : snap.Artist,
            Track = string.IsNullOrEmpty(snap.Title) ? null : snap.Title,
            Album = string.IsNullOrEmpty(snap.AlbumTitle) ? null : snap.AlbumTitle,
            Duration = snap.DurationMs > 0 ? TimeSpan.FromMilliseconds(snap.DurationMs) : null,
            Position = snap.PositionMs > 0 ? TimeSpan.FromMilliseconds(snap.PositionMs) : null,
            IsPlaying = (snap.PlaybackStatusValue == 4),  // 4 = Playing per WinRT spec
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
            return "browser";
        return "smtc-generic";
    }

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

    public void Stop()
    {
        try { _drainTimer?.Stop(); } catch { }
        try { _canaryTimer?.Stop(); } catch { }
        _logger.Log("stopped", Component);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        _manager = null;
    }
}
