using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:760-770
//
// Stage 7.12 Batch B Phase I #2 + #4: skip for non-music sources, request
// top 5 candidates, similarity-gate to ≥ 0.75 BEFORE doing the (relatively
// expensive) CoverArtArchive redirect resolution.  The CAA redirect resolver
// (added in Batch B DIAG-10 for Discord) is unchanged.
internal sealed class MusicBrainzSource : IArtSource
{
    private const double SimilarityThreshold = 0.75;
    private const int    CandidateLimit      = 5;

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
        var sourceLower = (source ?? string.Empty).ToLowerInvariant();
        if (sourceLower == "youtube" || sourceLower == "twitch") return string.Empty;

        try
        {
            var url = "https://musicbrainz.org/ws/2/recording/" +
                      $"?query=recording:{HttpUtility.UrlEncode(cleanedTrack)}" +
                      $"+artist:{HttpUtility.UrlEncode(cleanedArtist)}&fmt=json&limit={CandidateLimit}";

            var json = await HttpHelpers.HttpsGetAsync(_factory, "musicbrainz", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return string.Empty;

            var recordings = JsonNode.Parse(json)?["recordings"]?.AsArray();
            if (recordings == null || recordings.Count == 0) return string.Empty;

            var queryLine = $"{cleanedArtist} {cleanedTrack}";
            double bestScore = 0.0;
            string bestId    = string.Empty;
            string bestLine  = string.Empty;
            foreach (var rec in recordings)
            {
                if (rec == null) continue;
                var hitTitle  = rec["title"]?.GetValue<string>() ?? string.Empty;
                var hitArtist = rec["artist-credit"]?[0]?["name"]?.GetValue<string>() ?? string.Empty;
                var releaseId = rec["releases"]?[0]?["id"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrEmpty(releaseId)) continue;

                var hitLine = $"{hitArtist} {hitTitle}";
                var score   = TextSimilarity.Dice(queryLine, hitLine);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId    = releaseId;
                    bestLine  = hitLine;
                }
            }

            if (bestScore < SimilarityThreshold || string.IsNullOrEmpty(bestId))
            {
                _logger.LogDebug(
                    "MusicBrainzSource: best candidate '{Hit}' scored {Score:F2} for query '{Query}' — below {Thr:F2} threshold, rejecting",
                    bestLine, bestScore, queryLine, SimilarityThreshold);
                return string.Empty;
            }

            // Stage 7.12 Batch B (DIAG-10 fix): resolve the CoverArtArchive
            // 307 redirect ourselves and return the final archive.org URL.
            // Discord's media proxy doesn't follow multi-hop redirects.
            var cacheUrl = $"https://coverartarchive.org/release/{bestId}/front";
            var finalUrl = await ResolveRedirectAsync(cacheUrl, ct);
            _logger.LogDebug(
                "MusicBrainzSource: accepted '{Hit}' at score {Score:F2}, MBID {Id} -> {Url}",
                bestLine, bestScore, bestId, finalUrl);
            return finalUrl;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MusicBrainzSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }

    private async Task<string> ResolveRedirectAsync(string startUrl, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect      = true,
                MaxAutomaticRedirections = 5,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MastersFM/1.7 (cover-art-redirect-resolver)");

            using var req  = new HttpRequestMessage(HttpMethod.Head, startUrl);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            var final = resp.RequestMessage?.RequestUri?.ToString();
            if (!resp.IsSuccessStatusCode || string.IsNullOrEmpty(final))
                return string.Empty;
            return final;
        }
        catch
        {
            return string.Empty;
        }
    }
}
