using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:686-696
internal sealed class SmtcSource : IArtSource
{
    private static readonly string[] s_browserPlatforms =
        { "soundcloud", "youtube", "deezer", "tidal", "apple music", "bandcamp", "mixcloud" };

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
