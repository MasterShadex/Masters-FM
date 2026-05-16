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
                if (drift > SeekThresholdMs)
                {
                    isSeek = true;
                    _logger.Log(
                        $"seek: drift={drift:F0}ms pos={posAdvanceMs:F0}ms expected={expectedMs:F0}ms",
                        Component);
                }
            }

            _lastPosition = track.Position;
            _lastTickUtc  = now;

            // Build a heartbeat copy of the current track with fresh timestamps
            // and the seek flag.  Uses C# record 'with' expression so all other
            // fields (Artist, Track, ArtUri, etc.) are preserved unchanged.
            var hb = track with { ObservedUtc = now, IsSeek = isSeek };

            // Stage 7.8B: route through TrackResolver with forcePositionRefresh=true
            // (bypasses dedup gate; increments webhook_heartbeat_sends in TrackResolver).
            _trackResolver.OnTrackChanged(hb, forcePositionRefresh: true);
            _telemetry.IncrementCounter("heartbeat_sends");
        }
        catch (Exception ex)
        {
            _logger.LogErr("HeartbeatService.OnTick", ex, Component);
        }
    }
}
