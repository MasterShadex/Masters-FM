using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:713-718
internal sealed class WebhookArtSource : IArtSource
{
    // /-large|-t300x300|-t200x200/ -> -t500x500 (server.js:714)
    private static readonly Regex s_upgradePattern = new Regex(
        @"-large|-t300x300|-t200x200",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<WebhookArtSource> _logger;

    public WebhookArtSource(IHttpClientFactory factory, ILogger<WebhookArtSource> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public string Name => "webhook";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        try
        {
            // Skip data: URIs -- those are SMTC thumbnails handled by SmtcSource (server.js:713 !smtcThumb)
            if (string.IsNullOrEmpty(webhookArt) ||
                webhookArt.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            if (!TextNormalization.IsValidArt(webhookArt)) return string.Empty;

            var upgraded = s_upgradePattern.Replace(webhookArt, "-t500x500");

            // Try upgraded URL first (server.js:715)
            if (await HttpHelpers.IsUrlAccessibleAsync(_factory, "art-generic", upgraded, _logger, ct))
            {
                _logger.LogDebug("WebhookArtSource: upgraded URL accessible");
                return upgraded;
            }

            // Fall back to original if upgrade changed the URL and original accessible (server.js:716-717)
            if (upgraded != webhookArt &&
                await HttpHelpers.IsUrlAccessibleAsync(_factory, "art-generic", webhookArt, _logger, ct))
            {
                _logger.LogDebug("WebhookArtSource: original URL accessible (upgrade failed)");
                return webhookArt;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebhookArtSource failed");
            return string.Empty;
        }
    }
}
