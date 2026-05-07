using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:746-757
internal sealed class ItunesSource : IArtSource
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<ItunesSource> _logger;

    public ItunesSource(IHttpClientFactory factory, ILogger<ItunesSource> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public string Name => "itunes";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        try
        {
            // from server.js:748: term=artist+track&media=music&limit=1
            var q = HttpUtility.UrlEncode($"{cleanedArtist} {cleanedTrack}");
            var url = $"https://itunes.apple.com/search?term={q}&media=music&limit=1";

            var json = await HttpHelpers.HttpsGetAsync(_factory, "art-generic", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return string.Empty;

            var node = JsonNode.Parse(json);
            // from server.js:751: results[0].artworkUrl100
            var art = node?["results"]?[0]?["artworkUrl100"]?.GetValue<string>() ?? string.Empty;

            // from server.js:752-755: isValidArt only, then upgrade 100x100bb -> 500x500bb
            if (!TextNormalization.IsValidArt(art)) return string.Empty;

            // from server.js:754: art.replace('100x100bb', '500x500bb')
            art = art.Replace("100x100bb", "500x500bb", StringComparison.Ordinal);

            _logger.LogDebug("ItunesSource: found art for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return art;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ItunesSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }
}
