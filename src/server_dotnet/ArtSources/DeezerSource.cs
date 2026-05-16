using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:734-743
//
// Stage 7.12 Batch B Phase I #2 + #4:
//   - Skip when source is non-music (youtube / twitch) — these don't belong
//     in a music catalog and Deezer's lenient match for unrelated titles
//     was the dominant source of "wrong album art" reports.
//   - Request top 5 candidates instead of taking the literal first one;
//     score each via Sørensen-Dice on bigrams and accept only matches
//     scoring ≥ 0.75 against "artist track".  Returns empty if nothing
//     clears the bar — the cascade then falls through to iTunes / MB.
internal sealed class DeezerSource : IArtSource
{
    private const double SimilarityThreshold = 0.75;
    private const int    CandidateLimit      = 5;

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
        // Phase I #2: don't search music DBs for non-music platforms.
        var sourceLower = (source ?? string.Empty).ToLowerInvariant();
        if (sourceLower == "youtube" || sourceLower == "twitch") return string.Empty;

        try
        {
            var q   = $"artist:\"{cleanedArtist}\" track:\"{cleanedTrack}\"";
            var url = $"https://api.deezer.com/search?q={HttpUtility.UrlEncode(q)}&limit={CandidateLimit}";

            var json = await HttpHelpers.HttpsGetAsync(_factory, "art-generic", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return string.Empty;

            var data = JsonNode.Parse(json)?["data"]?.AsArray();
            if (data == null || data.Count == 0) return string.Empty;

            // Phase I #4: score each candidate; pick the best, gate at threshold.
            var queryLine = $"{cleanedArtist} {cleanedTrack}";
            double bestScore = 0.0;
            string bestArt   = string.Empty;
            string bestLine  = string.Empty;
            foreach (var item in data)
            {
                if (item == null) continue;
                var hitArtist = item["artist"]?["name"]?.GetValue<string>() ?? string.Empty;
                var hitTitle  = item["title"]?.GetValue<string>()           ?? string.Empty;
                var hitArt    = item["album"]?["cover_xl"]?.GetValue<string>()
                             ?? item["album"]?["cover_big"]?.GetValue<string>()
                             ?? string.Empty;
                if (!TextNormalization.IsValidArt(hitArt)) continue;

                var hitLine = $"{hitArtist} {hitTitle}";
                var score   = TextSimilarity.Dice(queryLine, hitLine);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestArt   = hitArt;
                    bestLine  = hitLine;
                }
            }

            if (bestScore < SimilarityThreshold)
            {
                _logger.LogDebug(
                    "DeezerSource: best candidate '{Hit}' scored {Score:F2} for query '{Query}' — below {Thr:F2} threshold, rejecting",
                    bestLine, bestScore, queryLine, SimilarityThreshold);
                return string.Empty;
            }

            _logger.LogDebug(
                "DeezerSource: accepted '{Hit}' at score {Score:F2} for query '{Query}'",
                bestLine, bestScore, queryLine);
            return bestArt;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DeezerSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }
}
