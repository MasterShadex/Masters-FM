using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:575-598 (searchYouTubeArt)
// Fires when source.toLowerCase().includes('youtube').
// Scrapes YouTube search results for videoId, then returns thumbnail URL.
// UA: browser UA from "bing" named client (ID-26: reuses existing browser UA).
internal sealed class YouTubeSource : IArtSource
{
    // videoId extraction pattern (server.js:585)
    // Matches first videoId in ytInitialData embedded JSON
    // Length: exactly 11 chars, alphanumeric + underscore + hyphen
    private static readonly Regex s_videoIdPattern = new Regex(
        @"""videoId"":""([A-Za-z0-9_\-]{11})""",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<YouTubeSource> _logger;

    public YouTubeSource(IHttpClientFactory factory, ILogger<YouTubeSource> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    public string Name => "youtube";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        try
        {
            // from server.js:783: only fires for YouTube source tracks
            var sourceLower = (source ?? string.Empty).ToLowerInvariant();
            if (!sourceLower.Contains("youtube", StringComparison.Ordinal)) return string.Empty;

            // from server.js:577: build search query
            var q = $"{cleanedArtist} {cleanedTrack}".Trim();
            if (string.IsNullOrEmpty(q)) return string.Empty;

            // from server.js:579-582: search YouTube results page
            // Uses "bing" named client (browser UA -- YouTube blocks non-browser UAs)
            var searchUrl = $"https://www.youtube.com/results?search_query={HttpUtility.UrlEncode(q)}";
            var body = await HttpHelpers.HttpsGetAsync(_factory, "bing", searchUrl, _logger, ct);
            if (string.IsNullOrEmpty(body)) return string.Empty;

            // from server.js:585: extract first videoId from ytInitialData
            var m = s_videoIdPattern.Match(body);
            if (!m.Success)
            {
                _logger.LogWarning(
                    "YouTubeSource: HTTP 200 received but no videoId match for {Artist} - {Track}. " +
                    "YouTube page structure may have changed. " +
                    @"Pattern: ""videoId"":""([A-Za-z0-9_\-]{{11}})"". Response length: {Len}",
                    cleanedArtist, cleanedTrack, body.Length);
                return string.Empty;
            }

            var vid = m.Groups[1].Value;

            // from server.js:589-592: try hqdefault then mqdefault
            var hq = $"https://img.youtube.com/vi/{vid}/hqdefault.jpg";
            if (await HttpHelpers.IsUrlAccessibleAsync(_factory, "art-generic", hq, _logger, ct))
            {
                _logger.LogDebug("YouTubeSource: videoId={Vid} -> hqdefault", vid);
                return hq;
            }

            var mq = $"https://img.youtube.com/vi/{vid}/mqdefault.jpg";
            if (await HttpHelpers.IsUrlAccessibleAsync(_factory, "art-generic", mq, _logger, ct))
            {
                _logger.LogDebug("YouTubeSource: videoId={Vid} -> mqdefault", vid);
                return mq;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "YouTubeSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }
}
