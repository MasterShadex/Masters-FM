using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:604-639 (searchOsuArt)
// Fires when source.toLowerCase().includes('osu').
// Replaces OsuDirectSource (4.9b CDN-passthrough placeholder) at cascade position 3 (ID-24).
// Uses osu! beatmapsets search API (returns JSON with Accept: application/json).
internal sealed class OsuScraperSource : IArtSource
{
    // HTML fallback pattern (server.js:621)
    // Matches first beatmapset ID in HTML response
    private static readonly Regex s_beatmapIdPattern = new Regex(
        @"/beatmapsets/(\d+)",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<OsuScraperSource> _logger;

    public OsuScraperSource(IHttpClientFactory factory, ILogger<OsuScraperSource> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    public string Name => "osu-scraper";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        try
        {
            // from server.js:707: only fires for osu! source tracks
            var sourceLower = (source ?? string.Empty).ToLowerInvariant();
            if (!sourceLower.Contains("osu", StringComparison.Ordinal)) return string.Empty;

            // from server.js:606-607: build search query
            var q = $"{cleanedArtist} {cleanedTrack}".Trim();
            if (string.IsNullOrEmpty(q)) return string.Empty;

            // from server.js:611-613: beatmapsets search API (JSON response via Accept header)
            var url = $"https://osu.ppy.sh/beatmapsets/search?q={HttpUtility.UrlEncode(q)}&s=any";

            // Must use a custom request to send Accept: application/json header
            // HttpsGetAsync uses named client UA; we need to add Accept header
            string? beatmapId = null;

            var client = _factory.CreateClient("art-generic");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "application/json");

            string body;
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
                using var resp = await client.SendAsync(req, linked.Token);
                body = resp.IsSuccessStatusCode
                    ? await resp.Content.ReadAsStringAsync(linked.Token)
                    : string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OsuScraperSource: request failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
                return string.Empty;
            }

            if (string.IsNullOrEmpty(body)) return string.Empty;

            // from server.js:617-623: JSON primary parse, HTML regex fallback
            try
            {
                var node = JsonNode.Parse(body);
                var idVal = node?["beatmapsets"]?[0]?["id"];
                if (idVal != null)
                    beatmapId = idVal.ToString();
            }
            catch
            {
                // HTML fallback (server.js:621)
                var m = s_beatmapIdPattern.Match(body);
                if (m.Success) beatmapId = m.Groups[1].Value;
            }

            if (beatmapId == null)
            {
                _logger.LogWarning(
                    "OsuScraperSource: HTTP 200 received but no beatmapset ID found for {Artist} - {Track}. " +
                    "osu! API JSON structure may have changed. " +
                    "JSON path: beatmapsets[0].id; HTML fallback: /beatmapsets/(\\d+). Response length: {Len}",
                    cleanedArtist, cleanedTrack, body.Length);
                return string.Empty;
            }

            // from server.js:626-633: try CDN URLs in order, return first accessible
            var candidates = new[]
            {
                $"https://assets.ppy.sh/beatmaps/{beatmapId}/covers/cover@2x.jpg",
                $"https://assets.ppy.sh/beatmaps/{beatmapId}/covers/cover.jpg",
                $"https://b.ppy.sh/thumb/{beatmapId}l.jpg",
            };

            foreach (var candidate in candidates)
            {
                if (await HttpHelpers.IsUrlAccessibleAsync(_factory, "art-generic", candidate, _logger, ct))
                {
                    _logger.LogDebug("OsuScraperSource: beatmapset={Id} -> {Url}", beatmapId, candidate);
                    return candidate;
                }
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OsuScraperSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }
}
