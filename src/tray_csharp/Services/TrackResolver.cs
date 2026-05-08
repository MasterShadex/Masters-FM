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

    public void OnTrackChanged(TrackUpdate update)
    {
        if (update == null) return;
        var key = update.IdentityKey;

        bool isNew;
        TrackUpdate prior;
        lock (_lock)
        {
            isNew = !string.Equals(key, _currentKey, StringComparison.Ordinal);
            prior = _current!;
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

        // Art cache (best-effort; non-blocking)
        if (!string.IsNullOrEmpty(update.ArtUri))
        {
            _artCache.Touch(update.ArtUri);
        }

        // Webhook (fire-and-forget; matches PS S15 fire-and-forget pattern)
        _ = _webhook.SendTrackUpdateAsync(update, CancellationToken.None);
    }
}
