using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Art source contract. Returns string.Empty on any failure or no-result.
/// MUST NOT throw -- exceptions break the cascade.
/// </summary>
internal interface IArtSource
{
    /// <summary>Source identifier for logging (e.g. "deezer", "itunes", "musicbrainz").</summary>
    string Name { get; }

    /// <summary>
    /// Try to find art for the given cleaned artist + track.
    /// Returns string.Empty if not found or any error occurred.
    /// </summary>
    Task<string> TryGetArtAsync(
        string cleanedArtist,
        string cleanedTrack,
        string? webhookArt,
        string? originUrl,
        string? source,
        CancellationToken ct);
}
