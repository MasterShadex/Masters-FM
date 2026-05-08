// Stage 7.5: TrackUpdate record. Carries a single detection result from
// SmtcEventBridge (Arm 1) or one of the gap-filler IPlatformDetectors
// (Arm 2). Consumed by ITrackResolver (Arm 3) which dedups + caches +
// emits the webhook to server.exe.

namespace MastersFM.Tray.Detectors;

public sealed record TrackUpdate
{
    public required string Source { get; init; }
    public string? Artist { get; init; }
    public string? Track { get; init; }
    public string? Album { get; init; }
    public TimeSpan? Duration { get; init; }
    public TimeSpan? Position { get; init; }
    public bool IsPlaying { get; init; }
    public string? ArtUri { get; init; }
    public string? PlatformIdentity { get; init; }
    public DateTime ObservedUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Stable identity key for dedup + caching: source|||artist|||track.</summary>
    public string IdentityKey => string.Format("{0}|||{1}|||{2}",
        Source ?? string.Empty,
        Artist ?? string.Empty,
        Track ?? string.Empty);
}
