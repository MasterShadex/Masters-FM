// INTERRUPT #3 STEP 5 (Issues 1+8): HeartbeatService.
//
// Problem: TrackResolver.OnTrackChanged dedups by identity key (TrackResolver.cs:64-68)
// and drops all same-track updates.  The sole SendTrackUpdateAsync call site
// (TrackResolver.cs:90) is unreachable for position / pause changes on the
// current track.  As a result, server.exe never receives positionMs / isPaused /
// seek updates after the initial new-track event.
//
// Fix: a 2-second DispatcherTimer that reads TrackResolver.CurrentTrack and calls
// WebhookClient.SendTrackUpdateAsync directly, bypassing the dedup gate entirely.
//
// Seek detection: compares the position reported by the last tick to the position
// reported by this tick, accounting for wall-clock elapsed and pause state.
// If the drift exceeds SeekThresholdMs (3000ms) the update is flagged IsSeek=true.

using System.Windows.Threading;
using MastersFM.Tray.Detectors;

namespace MastersFM.Tray.Services;

public sealed class HeartbeatService : IHeartbeatService
{
    private const string Component = "Heartbeat";
    // Stage 7.12 Batch B Phase D #3: 100 ms → 50 ms.  With timeBeginPeriod(1)
    // raising the OS timer resolution to 1 ms (App.xaml.cs), 50 ms is the
    // sweet spot — half the prior worst-case miss latency while still under
    // 0.2 % CPU.  TrackResolver is state-aware so most ticks dedup away at
    // the server's BroadcastIfChanged.
    private const double IntervalSeconds = 0.05;
    // Stage 7.12 Batch B Phase D #4: 400 ms → 200 ms.  Tighter scrub
    // detection — the smallest seek the user can feel is ~150 ms.
    private const double SeekThresholdMs = 200.0;

    private readonly ITrackResolver _trackResolver;
    // Stage 7.8B: _webhook removed; heartbeat now routes through _trackResolver.OnTrackChanged
    // with forcePositionRefresh=true, keeping telemetry clean and dedup gate architecture intact.
    private readonly ITelemetry _telemetry;
    private readonly ILogger _logger;

    private DispatcherTimer? _timer;
    private TimeSpan? _lastPosition;
    private DateTime _lastTickUtc;
    private bool _started;
    // v14 Phase 7: send-gate state. The 50ms tick keeps running for seek detection, but the
    // webhook SEND is throttled to real edges + a 1s keepalive (see OnTick) so a steadily-playing
    // track no longer floods ~16-20 webhooks/sec.
    private DateTime? _lastSentUtc;
    private bool _lastSentIsPlaying;
    private string _lastSentKey = string.Empty;

    public HeartbeatService(
        ITrackResolver trackResolver,
        ITelemetry telemetry,
        ILogger logger)
    {
        _trackResolver = trackResolver;
        _telemetry = telemetry;
        _logger = logger;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _lastTickUtc = DateTime.UtcNow;

        // Must run on the WPF dispatcher thread; create the timer on the
        // calling thread (App.OnStartup, which is the dispatcher thread).
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(IntervalSeconds)
        };
        _timer.Tick += OnTick;
        _timer.Start();
        _logger.Log(
            $"HeartbeatService started (interval={IntervalSeconds}s, seekThreshold={SeekThresholdMs}ms)",
            Component);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        _logger.Log("HeartbeatService stopped", Component);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            var now = DateTime.UtcNow;
            var track = _trackResolver.CurrentTrack;

            if (track == null)
            {
                // No active track; clear seek-detection state.
                _lastPosition = null;
                _lastTickUtc = now;
                return;
            }

            // v14 run-ahead fix: interpolate a LIVE position from the last SMTC snapshot anchor
            // so the reported position advances smoothly between sparse SMTC events (YouTube/Chrome
            // emit TimelineProperties only on play/pause/seek). Without this the tray re-ships a
            // FROZEN cached position, the server free-runs startedAt, and the overlay clock drifts
            // AHEAD of reality. Only while playing; never past the track duration. A fresh SMTC
            // event resets the anchor, so a real seek still produces a genuine position jump.
            if (track.IsPlaying && track.AnchorPositionMs.HasValue && track.AnchorUpdatedUtc.HasValue)
            {
                double anchorElapsedMs = (now - track.AnchorUpdatedUtc.Value).TotalMilliseconds;
                if (anchorElapsedMs > 0)
                {
                    long liveMs = track.AnchorPositionMs.Value + (long)anchorElapsedMs;
                    if (track.Duration.HasValue && liveMs > track.Duration.Value.TotalMilliseconds)
                        liveMs = (long)track.Duration.Value.TotalMilliseconds;
                    track = track with { Position = TimeSpan.FromMilliseconds(liveMs) };
                }
            }

            // Seek detection: compare position advance to wall-clock advance.
            bool isSeek = false;
            if (_lastPosition.HasValue && track.Position.HasValue)
            {
                var wallElapsedMs = (now - _lastTickUtc).TotalMilliseconds;
                var posAdvanceMs  = (track.Position.Value - _lastPosition.Value).TotalMilliseconds;
                // When playing, position should advance ~= wall elapsed.
                // When paused, position should not advance (~= 0).
                var expectedMs    = track.IsPlaying ? wallElapsedMs : 0.0;
                var drift         = Math.Abs(posAdvanceMs - expectedMs);
                // Stage 7.12 Batch B Phase H #2: browser sources (YouTube via
                // Chrome, etc.) report TimelineProperties irregularly; up to
                // 800 ms of jitter during buffering is normal.  Use 1000 ms
                // for them; 200 ms (Phase D #4) for rock-steady desktop sources.
                var seekThresholdForSource = SmtcEventBridge.IsBrowserLikeSource(track.Source)
                    ? 1000.0
                    : SeekThresholdMs;
                // Stage 7.15 Fix A1: require ACTUAL position movement before flagging seek.
                // SoundCloud-RPC (Electron MediaSession) emits stale TimelineProperties where
                // reported position freezes while wall clock advances; without this gate the
                // drift check fires isSeek=true on every tick, causing server B7 to re-pin
                // startedAt to the frozen position, freezing the overlay clock.
                // See V14_S7_14_DIAG_OVERLAY_TIME_FROZEN.md sections 5.1 + 8.1.
                bool positionActuallyMoved = Math.Abs(posAdvanceMs) > 100;
                if (drift > seekThresholdForSource && positionActuallyMoved)
                {
                    isSeek = true;
                    _logger.Log(
                        $"seek: drift={drift:F0}ms pos={posAdvanceMs:F0}ms expected={expectedMs:F0}ms thr={seekThresholdForSource:F0}",
                        Component);
                }
            }

            _lastPosition = track.Position;
            _lastTickUtc  = now;

            // Build a heartbeat copy of the current track with fresh timestamps
            // and the seek flag.  Uses C# record 'with' expression so all other
            // fields (Artist, Track, ArtUri, etc.) are preserved unchanged.
            var hb = track with { ObservedUtc = now, IsSeek = isSeek };

            // v14 Phase 7: gate the SEND by change-detection + a 1s keepalive. The 50ms tick still
            // runs the seek detection above, but a steadily-playing track no longer POSTs ~16-20
            // webhooks/sec (the old unconditional send). That flood drove the server B7/B10 startedAt
            // re-pins which churned the SSE the overlay sees and made the overlay clock track slightly
            // fast on stale sources (e.g. SoundCloud-RPC). Send on a real edge -- seek, play/pause
            // change, or track change -- or at most once per second to keep the server position fresh.
            // Seeks/pause/track-changes are never dropped; the server BroadcastIfChanged still dedups.
            bool shouldSend = isSeek
                || !_lastSentUtc.HasValue
                || (now - _lastSentUtc.Value).TotalMilliseconds >= 1000.0
                || track.IsPlaying != _lastSentIsPlaying
                || track.IdentityKey != _lastSentKey;
            if (shouldSend)
            {
                // Stage 7.8B: route through TrackResolver with forcePositionRefresh=true
                // (bypasses dedup gate; increments webhook_heartbeat_sends in TrackResolver).
                _trackResolver.OnTrackChanged(hb, forcePositionRefresh: true);
                _telemetry.IncrementCounter("heartbeat_sends");
                _lastSentUtc = now;
                _lastSentIsPlaying = track.IsPlaying;
                _lastSentKey = track.IdentityKey;
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("HeartbeatService.OnTick", ex, Component);
        }
    }
}
