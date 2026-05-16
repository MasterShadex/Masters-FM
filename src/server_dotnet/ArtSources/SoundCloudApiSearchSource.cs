// Stage 7.12 Batch B Phase I #5: SoundCloud API search.
//
// Most SoundCloud content is user uploads that are NOT in Deezer / iTunes /
// MusicBrainz, so when the SMTC thumbnail is missing or rejected by Discord
// the cascade used to fall through to Bing-image-search (or worse, return
// nothing).  This source uses SoundCloud's own api-v2 search endpoint —
// authenticated via the scraped client_id from SoundCloudClientIdCache —
// to find the exact track and return its CDN artwork URL.
//
// Triggers only for source=="soundcloud".  Returns the t500x500 upgraded
// artwork URL on a similarity-matched hit; returns string.Empty on miss
// or auth failure.  On 401/403 it invalidates the cached client_id so the
// next call re-scrapes — covers the case where SoundCloud rotated the key.

using System;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

internal sealed class SoundCloudApiSearchSource : IArtSource
{
    private const double SimilarityThreshold = 0.70;   // SC user titles can be noisy
    private const int    CandidateLimit      = 10;

    // Upgrade -large / -t300x300 / -t200x200 to -t500x500 (mirrors SC oEmbed pattern).
    private static readonly Regex s_artworkUpgrade = new Regex(
        @"-large|-t300x300|-t200x200",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly IHttpClientFactory          _factory;
    private readonly SoundCloudClientIdCache     _clientIds;
    private readonly ILogger<SoundCloudApiSearchSource> _logger;

    public SoundCloudApiSearchSource(
        IHttpClientFactory factory,
        SoundCloudClientIdCache clientIds,
        ILogger<SoundCloudApiSearchSource> logger)
    {
        _factory   = factory;
        _clientIds = clientIds;
        _logger    = logger;
    }

    public string Name => "soundcloud-api";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        var sourceLower = (source ?? string.Empty).ToLowerInvariant();
        if (sourceLower != "soundcloud") return string.Empty;
        if (string.IsNullOrWhiteSpace(cleanedTrack)) return string.Empty;

        try
        {
            // Try with current client_id; on auth failure, invalidate and retry once.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var clientId = await _clientIds.GetAsync(ct);
                if (string.IsNullOrEmpty(clientId))
                {
                    _logger.LogDebug("SoundCloudApiSearchSource: no client_id available");
                    return string.Empty;
                }

                var q   = HttpUtility.UrlEncode($"{cleanedArtist} {cleanedTrack}".Trim());
                var url = "https://api-v2.soundcloud.com/search/tracks" +
                          $"?q={q}&client_id={clientId}&limit={CandidateLimit}";

                var client = _factory.CreateClient("art-generic");
                using var req  = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

                if (resp.StatusCode == HttpStatusCode.Unauthorized ||
                    resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    _logger.LogInformation(
                        "SoundCloudApiSearchSource: HTTP {Status} — invalidating client_id and retrying",
                        (int)resp.StatusCode);
                    _clientIds.Invalidate();
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "SoundCloudApiSearchSource: HTTP {Status} for {Artist} - {Track}",
                        (int)resp.StatusCode, cleanedArtist, cleanedTrack);
                    return string.Empty;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrEmpty(json)) return string.Empty;

                return PickBestArtwork(json, cleanedArtist, cleanedTrack);
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoundCloudApiSearchSource failed for {Artist} - {Track}",
                cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }

    private string PickBestArtwork(string json, string cleanedArtist, string cleanedTrack)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return string.Empty; }

        var collection = root?["collection"]?.AsArray();
        if (collection == null || collection.Count == 0) return string.Empty;

        var queryLine = $"{cleanedArtist} {cleanedTrack}";
        double bestScore = 0.0;
        string bestArt   = string.Empty;
        string bestLine  = string.Empty;
        foreach (var t in collection)
        {
            if (t == null) continue;
            var hitTitle  = t["title"]?.GetValue<string>() ?? string.Empty;
            var hitArtist = t["user"]?["username"]?.GetValue<string>() ?? string.Empty;
            var hitArt    = t["artwork_url"]?.GetValue<string>() ?? string.Empty;
            // SoundCloud tracks without explicit artwork fall back to user avatar —
            // skip those, they're never the right album art.
            if (string.IsNullOrEmpty(hitArt)) continue;
            if (!TextNormalization.IsValidArt(hitArt)) continue;

            var hitLine = $"{hitArtist} {hitTitle}";
            var score   = TextSimilarity.Dice(queryLine, hitLine);
            if (score > bestScore)
            {
                bestScore = score;
                bestArt   = s_artworkUpgrade.Replace(hitArt, "-t500x500");
                bestLine  = hitLine;
            }
        }

        if (bestScore < SimilarityThreshold)
        {
            _logger.LogDebug(
                "SoundCloudApiSearchSource: best candidate '{Hit}' scored {Score:F2} for query '{Query}' — below {Thr:F2}, rejecting",
                bestLine, bestScore, queryLine, SimilarityThreshold);
            return string.Empty;
        }

        _logger.LogDebug(
            "SoundCloudApiSearchSource: accepted '{Hit}' at score {Score:F2}",
            bestLine, bestScore);
        return bestArt;
    }
}
