using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:760-770
internal sealed class MusicBrainzSource : IArtSource
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<MusicBrainzSource> _logger;

    public MusicBrainzSource(IHttpClientFactory factory, ILogger<MusicBrainzSource> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public string Name => "musicbrainz";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        try
        {
            // from server.js:762-764: recording:{t}+artist:{a}&fmt=json&limit=1
            // Uses "musicbrainz" named client (MastersFM/1.7 UA per MusicBrainz rate-limit policy)
            var url = "https://musicbrainz.org/ws/2/recording/" +
                      $"?query=recording:{HttpUtility.UrlEncode(cleanedTrack)}" +
                      $"+artist:{HttpUtility.UrlEncode(cleanedArtist)}&fmt=json&limit=1";

            var json = await HttpHelpers.HttpsGetAsync(_factory, "musicbrainz", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return string.Empty;

            var node = JsonNode.Parse(json);
            // from server.js:767: recordings[0].releases[0].id
            var id = node?["recordings"]?[0]?["releases"]?[0]?["id"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(id)) return string.Empty;

            // from server.js:768: return coverartarchive URL directly -- no isValidArt, no isUrlAccessible
            var artUrl = $"https://coverartarchive.org/release/{id}/front";
            _logger.LogDebug("MusicBrainzSource: release MBID {Id} for {Artist} - {Track}", id, cleanedArtist, cleanedTrack);
            return artUrl;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MusicBrainzSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }
}
