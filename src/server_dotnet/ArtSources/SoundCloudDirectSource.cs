using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// 4.9b placeholder for server.js:700-703 (searchSoundCloudArt -- full implementation deferred to 4.9d)
// Returns webhookArt if it is already a SoundCloud CDN URL, preserving cascade position.
// The real searchSoundCloudArt() scraper (client_id-based) will replace this in sub-stage 4.9d.
internal sealed class SoundCloudDirectSource : IArtSource
{
    private readonly ILogger<SoundCloudDirectSource> _logger;

    public SoundCloudDirectSource(ILogger<SoundCloudDirectSource> logger) => _logger = logger;

    public string Name => "soundcloud-direct";

    public Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(webhookArt)) return Task.FromResult(string.Empty);

        var sourceLower = (source ?? string.Empty).ToLowerInvariant();
        if (sourceLower != "soundcloud") return Task.FromResult(string.Empty);

        // Passthrough only if webhookArt is already a SoundCloud CDN URL
        if (webhookArt.Contains("i1.sndcdn.com", StringComparison.OrdinalIgnoreCase) ||
            webhookArt.Contains("i5.sndcdn.com", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("SoundCloudDirectSource: returning existing SC CDN art");
            return Task.FromResult(webhookArt);
        }

        return Task.FromResult(string.Empty);
    }
}
