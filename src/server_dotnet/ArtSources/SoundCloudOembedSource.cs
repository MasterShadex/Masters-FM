using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:722-731 (SC oEmbed block in resolveArtwork)
// Fires when originUrl contains "soundcloud.com".
// Uses public oEmbed endpoint -- no client_id required (ID-23).
// Cascade position: between WebhookArtSource and DeezerSource (server.js order).
internal sealed class SoundCloudOembedSource : IArtSource
{
    // t500x500 upgrade pattern (server.js:727)
    private static readonly Regex s_upgradePattern = new Regex(
        @"-large|-t300x300|-t200x200",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<SoundCloudOembedSource> _logger;

    public SoundCloudOembedSource(IHttpClientFactory factory, ILogger<SoundCloudOembedSource> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    public string Name => "soundcloud-oembed";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        try
        {
            // from server.js:722: only fires when originUrl contains soundcloud.com
            if (string.IsNullOrEmpty(originUrl) ||
                !originUrl.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            // from server.js:724: public oEmbed endpoint
            var url  = $"https://soundcloud.com/oembed?format=json&url={HttpUtility.UrlEncode(originUrl)}";
            var json = await HttpHelpers.HttpsGetAsync(_factory, "art-generic", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return string.Empty;

            JsonNode? node;
            try { node = JsonNode.Parse(json); }
            catch
            {
                _logger.LogWarning(
                    "SoundCloudOembedSource: HTTP 200 received but JSON parse failed for originUrl {Url}. " +
                    "SoundCloud oEmbed response format may have changed. Response length: {Len}",
                    originUrl, json.Length);
                return string.Empty;
            }

            // from server.js:726: data.thumbnail_url
            var thumbUrl = node?["thumbnail_url"]?.GetValue<string>() ?? string.Empty;
            if (!TextNormalization.IsValidArt(thumbUrl))
            {
                _logger.LogWarning(
                    "SoundCloudOembedSource: oEmbed JSON parsed but thumbnail_url missing or invalid " +
                    "for originUrl {Url}. Field may have been renamed. Response length: {Len}",
                    originUrl, json.Length);
                return string.Empty;
            }

            // from server.js:727: upgrade size suffix
            var upgraded = s_upgradePattern.Replace(thumbUrl, "-t500x500");

            // from server.js:728: isUrlAccessible check
            if (await HttpHelpers.IsUrlAccessibleAsync(_factory, "art-generic", upgraded, _logger, ct))
            {
                _logger.LogDebug("SoundCloudOembedSource: resolved for {Url}", originUrl);
                return upgraded;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoundCloudOembedSource failed for originUrl={Url}", originUrl);
            return string.Empty;
        }
    }
}
