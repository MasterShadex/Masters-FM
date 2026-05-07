using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:772-776 (late SMTC fallback in resolveArtwork)
// Late-cascade fallback: returns SMTC data: URI when ALL API/scraping sources fail.
// Different from SmtcSource (4.9b):
//   SmtcSource: fires EARLY (before webhook art), only for browser platforms
//   SmtcFallbackSource: fires LATE (after MusicBrainz), for ANY source
// Cascade position: after MusicBrainzSource, before YouTubeSource (server.js:772-776).
internal sealed class SmtcFallbackSource : IArtSource
{
    private readonly ILogger<SmtcFallbackSource> _logger;

    public SmtcFallbackSource(ILogger<SmtcFallbackSource> logger) => _logger = logger;

    public string Name => "smtc-fallback";

    public Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        // from server.js:773: return smtcThumb (data: URI) as last-resort fallback
        // smtcThumb = webhookArt that starts with 'data:image/'
        // No platform check here (unlike SmtcSource) -- covers non-browser sources too
        if (!string.IsNullOrEmpty(webhookArt) &&
            webhookArt.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("SmtcFallbackSource: using SMTC data: URI as last-resort fallback");
            return Task.FromResult(webhookArt);
        }

        return Task.FromResult(string.Empty);
    }
}
