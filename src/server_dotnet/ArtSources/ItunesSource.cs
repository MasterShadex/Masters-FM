using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:746-757
//
// Stage 7.12 Batch B Phase I #2 + #4: same fixes as DeezerSource — skip for
// non-music sources, request top 5 candidates, similarity-gate to ≥ 0.75.
// iTunes is even more lenient than Deezer (no artist:/track: operators),
// so this was the worst offender for "first wrong result wins".
internal sealed class ItunesSource : IArtSource
{
    private const double SimilarityThreshold = 0.75;
    private const int    CandidateLimit      = 5;

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
        var sourceLower = (source ?? string.Empty).ToLowerInvariant();
        if (sourceLower == "youtube" || sourceLower == "twitch") return string.Empty;

        try
        {
            var q   = HttpUtility.UrlEncode($"{cleanedArtist} {cleanedTrack}");
            var url = $"https://itunes.apple.com/search?term={q}&media=music&limit={CandidateLimit}";

            var json = await HttpHelpers.HttpsGetAsync(_factory, "art-generic", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return string.Empty;

            var results = JsonNode.Parse(json)?["results"]?.AsArray();
            if (results == null || results.Count == 0) return string.Empty;

            var queryLine = $"{cleanedArtist} {cleanedTrack}";
            double bestScore = 0.0;
            string bestArt   = string.Empty;
            string bestLine  = string.Empty;
            foreach (var item in results)
            {
                if (item == null) continue;
                var hitArtist = item["artistName"]?.GetValue<string>() ?? string.Empty;
                var hitTitle  = item["trackName"]?.GetValue<string>()  ?? string.Empty;
                var hitArt    = item["artworkUrl100"]?.GetValue<string>() ?? string.Empty;
                if (!TextNormalization.IsValidArt(hitArt)) continue;

                var hitLine = $"{hitArtist} {hitTitle}";
                var score   = TextSimilarity.Dice(queryLine, hitLine);
                if (score > bestScore)
                {
                    bestScore = score;
                    // server.js:754 upgrade — 100x100bb → 500x500bb
                    bestArt   = hitArt.Replace("100x100bb", "500x500bb", StringComparison.Ordinal);
                    bestLine  = hitLine;
                }
            }

            if (bestScore < SimilarityThreshold)
            {
                _logger.LogDebug(
                    "ItunesSource: best candidate '{Hit}' scored {Score:F2} for query '{Query}' — below {Thr:F2} threshold, rejecting",
                    bestLine, bestScore, queryLine, SimilarityThreshold);
                return string.Empty;
            }

            _logger.LogDebug(
                "ItunesSource: accepted '{Hit}' at score {Score:F2} for query '{Query}'",
                bestLine, bestScore, queryLine);
            return bestArt;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ItunesSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }
}
