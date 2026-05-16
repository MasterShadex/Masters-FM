using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:686-696
internal sealed class SmtcSource : IArtSource
{
    // Stage 7.12 Batch B Phase I #1: added "spotify", "applemusic", "twitch", and
    // the umbrella "browser" name.  Spotify (desktop AND Spotify Web in Chrome)
    // publishes the actual album art via SMTC — ignoring it forced us through
    // Deezer/iTunes searches that returned the wrong VERSION of a song (single
    // vs album vs remix → wrong cover).  Apple Music desktop is the same.
    // Adding "twitch" prevents wrong music-DB matches from racing past the
    // SMTC thumbnail (or empty fallback) for Twitch streams.  "browser" covers
    // the case where the Phase H tab-title heuristic couldn't pin down a site.
    private static readonly string[] s_browserPlatforms =
    {
        "soundcloud", "youtube", "youtubemusic",
        "spotify", "applemusic", "apple music",
        "deezer", "tidal", "bandcamp", "mixcloud",
        "twitch", "browser",
    };

    private readonly ILogger<SmtcSource> _logger;

    public SmtcSource(ILogger<SmtcSource> logger) => _logger = logger;

    public string Name => "smtc";

    public Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        // Data-URI thumbnails from the tray (SMTC) for browser-platform sources
        // are authoritative -- return immediately (server.js:686-696)
        if (string.IsNullOrEmpty(webhookArt) || !webhookArt.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(string.Empty);

        var sourceLower = (source ?? string.Empty).ToLowerInvariant();
        foreach (var platform in s_browserPlatforms)
        {
            if (sourceLower.Contains(platform, StringComparison.Ordinal))
            {
                _logger.LogDebug("SmtcSource: SMTC thumbnail (browser source: {Source})", source);
                return Task.FromResult(webhookArt);
            }
        }

        return Task.FromResult(string.Empty);
    }
}
