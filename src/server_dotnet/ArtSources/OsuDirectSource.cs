using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// 4.9b placeholder for server.js:707-710 (searchOsuArt -- full implementation deferred to 4.9d)
// Returns webhookArt if it is already an osu! CDN URL, preserving cascade position.
// The real searchOsuArt() scraper will replace this in sub-stage 4.9d.
internal sealed class OsuDirectSource : IArtSource
{
    private readonly ILogger<OsuDirectSource> _logger;

    public OsuDirectSource(ILogger<OsuDirectSource> logger) => _logger = logger;

    public string Name => "osu-direct";

    public Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(webhookArt)) return Task.FromResult(string.Empty);

        var sourceLower = (source ?? string.Empty).ToLowerInvariant();
        if (!sourceLower.Contains("osu", StringComparison.Ordinal)) return Task.FromResult(string.Empty);

        // Passthrough only if webhookArt is already an osu! CDN URL
        if (webhookArt.Contains("assets.ppy.sh", StringComparison.OrdinalIgnoreCase) ||
            webhookArt.Contains("b.ppy.sh", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("OsuDirectSource: returning existing osu! CDN art");
            return Task.FromResult(webhookArt);
        }

        return Task.FromResult(string.Empty);
    }
}
