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

        bool isNew;
        lock (_lock)
        {
            isNew = !string.Equals(key, _currentKey, StringComparison.Ordinal);
            if (isNew)
            {
                _current = update;
                _currentKey = key;
            }
            else
            {
                // Same identity; refresh state (position, isPlaying may change)
                _current = update;
            }
        }

        if (!isNew)
        {
            _telemetry.IncrementCounter("track_dedup_hits");
            return;
        }

        _telemetry.IncrementCounter("track_changes");
        _logger.Log($"new track: {update.Source} {update.Artist} - {update.Track}", Component);

        // Fire event outside lock
        try
        {
            TrackChanged?.Invoke(this, update);
        }
        catch (Exception ex)
        {
            _logger.LogErr("TrackChanged subscriber", ex, Component);
        }

        // Stage 7.8B: art prefetch fires in parallel with webhook (was sequential before).
        // Warms the ArtLruCache so art is ready when overlay requests it.
        if (!string.IsNullOrEmpty(update.ArtUri))
        {
            var artUri = update.ArtUri;
            _ = Task.Run(() =>
            {
                try { _artCache.Touch(artUri); }
                catch { /* best-effort */ }
            });
        }

        // Webhook (fire-and-forget; matches PS S15 fire-and-forget pattern)
        _ = _webhook.SendTrackUpdateAsync(update, CancellationToken.None);
    }
}
