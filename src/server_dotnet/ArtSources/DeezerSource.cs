using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:734-743
internal sealed class DeezerSource : IArtSource
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<DeezerSource> _logger;

    public DeezerSource(IHttpClientFactory factory, ILogger<DeezerSource> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public string Name => "deezer";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        try
        {
            // from server.js:736-737: q=artist:"a" track:"t"&limit=1
            var q = $"artist:\"{cleanedArtist}\" track:\"{cleanedTrack}\"";
            var url = $"https://api.deezer.com/search?q={HttpUtility.UrlEncode(q)}&limit=1";

            var json = await HttpHelpers.HttpsGetAsync(_factory, "art-generic", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return string.Empty;

            var node = JsonNode.Parse(json);
            // from server.js:740: json.data?.[0]?.album?.cover_xl || json.data?.[0]?.album?.cover_big
            var art = node?["data"]?[0]?["album"]?["cover_xl"]?.GetValue<string>()
                   ?? node?["data"]?[0]?["album"]?["cover_big"]?.GetValue<string>()
                   ?? string.Empty;

            // from server.js:741: isValidArt only (no isUrlAccessible)
            if (!TextNormalization.IsValidArt(art)) return string.Empty;

            _logger.LogDebug("DeezerSource: found art for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return art;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DeezerSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }
}
