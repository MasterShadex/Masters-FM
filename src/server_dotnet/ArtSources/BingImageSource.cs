using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:647-675 (searchWebImageArt)
// Absolute last resort. Always fires. 1.2-second hard deadline (server.js Promise.race).
// Uses Bing image search HTML -- the most fragile source.
// UA: "bing" named client (browser UA required -- Bing blocks bots).
internal sealed class BingImageSource : IArtSource
{
    // Primary extraction pattern (server.js:658)
    // Matches "murl":"https://..." in Bing's JSON image card blobs
    // Extension filter: jpg, jpeg, png, webp
    // FRAGILE: Bing changes HTML structure frequently
    private static readonly Regex s_murlPattern = new Regex(
        @"""murl"":""(https?:\\?/\\?/[^""]+?\.(?:jpg|jpeg|png|webp))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Fallback pattern (server.js:663)
    // Matches mediaurl= in anchor href query params
    private static readonly Regex s_mediaurlPattern = new Regex(
        @"mediaurl=([^&""]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Must be valid http(s) URL (server.js:668)
    private static readonly Regex s_httpCheck = new Regex(
        @"^https?://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<BingImageSource> _logger;

    public BingImageSource(IHttpClientFactory factory, ILogger<BingImageSource> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    public string Name => "bing-image";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        try
        {
            // from server.js:649: build search query with " album cover" suffix
            var q = $"{cleanedArtist} {cleanedTrack} album cover".Trim();
            if (string.IsNullOrEmpty(cleanedArtist) && string.IsNullOrEmpty(cleanedTrack))
                return string.Empty;

            // from server.js:651-654: 1.2s deadline (Promise.race timeout -- ID-25)
            using var bingCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, bingCts.Token);

            var searchUrl = $"https://www.bing.com/images/search?q={HttpUtility.UrlEncode(q)}&form=HDRSC2&first=1";

            string body;
            try
            {
                body = await HttpHelpers.HttpsGetAsync(_factory, "bing", searchUrl, _logger, linkedCts.Token);
            }
            catch (OperationCanceledException) when (bingCts.IsCancellationRequested)
            {
                _logger.LogDebug("BingImageSource: 1.2s deadline exceeded for {Artist} - {Track}", cleanedArtist, cleanedTrack);
                return string.Empty;
            }

            if (string.IsNullOrEmpty(body)) return string.Empty;

            // from server.js:658: primary extraction -- murl JSON field
            string imgUrl = string.Empty;
            var m = s_murlPattern.Match(body);
            if (m.Success)
            {
                // from server.js:659: unescape backslash-encoded slashes
                imgUrl = m.Groups[1].Value.Replace("\\/", "/", StringComparison.Ordinal);
            }

            // from server.js:663: fallback pattern -- mediaurl= param
            if (string.IsNullOrEmpty(imgUrl))
            {
                var m2 = s_mediaurlPattern.Match(body);
                if (m2.Success)
                    imgUrl = HttpUtility.UrlDecode(m2.Groups[1].Value);
            }

            if (string.IsNullOrEmpty(imgUrl))
            {
                _logger.LogWarning(
                    "BingImageSource: HTTP 200 received but neither murl nor mediaurl pattern matched " +
                    "for {Artist} - {Track}. Bing image search HTML structure may have changed. " +
                    @"Primary pattern: ""murl"":""(https?:\\/\\/[^""]+\.(?:jpg|jpeg|png|webp))""; " +
                    "Fallback: mediaurl=([^&\"]+). Response length: {Len} bytes",
                    cleanedArtist, cleanedTrack, body.Length);
                return string.Empty;
            }

            // from server.js:668: reject non-http URLs (data: URIs, oddly-encoded URLs)
            if (!s_httpCheck.IsMatch(imgUrl)) return string.Empty;

            // from server.js:669: isUrlAccessible check
            if (await HttpHelpers.IsUrlAccessibleAsync(_factory, "art-generic", imgUrl, _logger, ct))
            {
                _logger.LogDebug("BingImageSource: resolved for {Artist} - {Track}: {Url}", cleanedArtist, cleanedTrack, imgUrl.Length > 80 ? imgUrl[..80] + "..." : imgUrl);
                return imgUrl;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BingImageSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }
}
