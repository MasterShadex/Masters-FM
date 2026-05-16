// Stage 7.5: TrackResolver. Consumes TrackUpdate from both detection
// arms; dedups by identity key (source|||artist|||track); updates art
// LRU; fires webhook; emits TrackChanged event for ViewModel/UI consumers.
//
// All state mutations happen under a single lock to keep external
// observers serialized. Webhook + art writes happen OUTSIDE the lock to
// avoid blocking the calling detector.

using MastersFM.Tray.Detectors;

namespace MastersFM.Tray.Services;

public sealed class TrackResolver : ITrackResolver
{
    private const string Component = "TrackResolver";

    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;
    private readonly ArtLruCache _artCache;
    private readonly WebhookClient _webhook;

    private readonly object _lock = new();
    private TrackUpdate? _current;
    private string? _currentKey;

    public TrackUpdate? CurrentTrack
    {
        get { lock (_lock) return _current; }
    }

    public event EventHandler<TrackUpdate>? TrackChanged;

    public TrackResolver(ILogger logger, ITelemetry telemetry, ArtLruCache artCache, WebhookClient webhook)
    {
        _logger = logger;
        _telemetry = telemetry;
        _artCache = artCache;
        _webhook = webhook;
    }

    public void OnTrackChanged(TrackUpdate update, bool forcePositionRefresh = false)
    {
        if (update == null) return;
        var key = update.IdentityKey;

        // Stage 7.8B: forcePositionRefresh=true bypasses dedup gate (heartbeat path).
        if (forcePositionRefresh)
        {
            lock (_lock) { _current = update; }
            _ = _webhook.SendTrackUpdateAsync(update, CancellationToken.None);
            _telemetry.IncrementCounter("webhook_heartbeat_sends");
            return;
        }

        // Stage 7.12 Batch B (real-time sync): state-aware dedup.  Identity-only
        // dedup (the original behaviour) silently dropped every pause / resume /
        // seek webhook the SMTC bridge produced on the current track — forcing
        // those updates to wait for the 1 s heartbeat tick.  Now we send the
        // webhook IMMEDIATELY when either:
        //   - identity changed (new track), OR
        //   - IsPlaying flipped (pause/resume), OR
        //   - position jumped beyond expected wall-clock drift (seek).
        bool isNew;
        bool stateChanged = false;
        TrackUpdate? prev;
        lock (_lock)
        {
            prev   = _current;
            isNew  = !string.Equals(key, _currentKey, StringComparison.Ordinal);
            if (isNew)
            {
                _current    = update;
                _currentKey = key;
            }
            else
            {
                // Detect within-track state changes before we overwrite _current.
                if (prev != null)
                {
                    if (prev.IsPlaying != update.IsPlaying) stateChanged = true;

                    if (!stateChanged
                        && prev.Position.HasValue
                        && update.Position.HasValue)
                    {
                        var posDeltaMs  = (update.Position.Value - prev.Position.Value).TotalMilliseconds;
                        var wallDeltaMs = (update.ObservedUtc - prev.ObservedUtc).TotalMilliseconds;
                        var expectedMs  = update.IsPlaying ? wallDeltaMs : 0.0;
                        // Stage 7.12 Batch B Phase D #4: 250 ms → 100 ms.  With
                        // the 50 ms heartbeat (Phase D #3) the wall-clock delta
                        // between adjacent updates is small enough that 100 ms
                        // of unexplained drift is genuinely a scrub.  Catches
                        // 100-200 ms in-bar scrubs that 250 ms missed.
                        var jump = Math.Abs(posDeltaMs - expectedMs);
                        if (jump > 100.0) stateChanged = true;
                    }
                }
                _current = update;
            }
        }

        if (isNew)
        {
            _telemetry.IncrementCounter("track_changes");
            _logger.Log($"new track: {update.Source} {update.Artist} - {update.Track}", Component);

            try { TrackChanged?.Invoke(this, update); }
            catch (Exception ex) { _logger.LogErr("TrackChanged subscriber", ex, Component); }

            // Art prefetch in parallel with webhook.
            if (!string.IsNullOrEmpty(update.ArtUri))
            {
                var artUri = update.ArtUri;
                _ = Task.Run(() => { try { _artCache.Touch(artUri); } catch { } });
            }

            _ = _webhook.SendTrackUpdateAsync(update, CancellationToken.None);
            return;
        }

        if (stateChanged)
        {
            _telemetry.IncrementCounter("webhook_state_change_sends");
            // Fire-and-forget — sync flows the same way as a new track from here.
            _ = _webhook.SendTrackUpdateAsync(update, CancellationToken.None);
            return;
        }

        _telemetry.IncrementCounter("track_dedup_hits");
    }
}
