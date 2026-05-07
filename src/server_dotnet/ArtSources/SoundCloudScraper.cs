using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:488-519 (getSoundCloudClientId)
// Scrapes SoundCloud's homepage JS bundle for the current client_id.
// 6-hour TTL; invalidated on 401/403 from api-v2.soundcloud.com.
// Single-flight: SemaphoreSlim(1,1) prevents multiple concurrent scrapes.
internal sealed class SoundCloudClientIdCache
{
    // Script URL pattern from SoundCloud homepage HTML (server.js:501)
    // src="(https://a-v2.sndcdn.com/assets/[hex-]-[hex].js)"
    private static readonly Regex s_scriptPattern = new Regex(
        @"src=""(https://a-v2\.sndcdn\.com/assets/[0-9a-f\-]+\.js)""",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // client_id extraction pattern (server.js:506)
    // Matches: client_id: "ABC..." or client_id = "ABC..." or client_id:"ABC..."
    // Character class: [a-zA-Z0-9], exactly 32 chars (ID-27: server.js uses uppercase-capable class)
    private static readonly Regex s_clientIdPattern = new Regex(
        @"client_id\s*[:=]\s*[""']([a-zA-Z0-9]{32})[""']",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly SemaphoreSlim _scrapeLock = new SemaphoreSlim(1, 1);
    private string? _clientId;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private const int TtlHours = 6; // server.js:493: SC_CLIENTID_TTL = 6 * 60 * 60 * 1000

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<SoundCloudClientIdCache> _logger;

    public SoundCloudClientIdCache(IHttpClientFactory factory, ILogger<SoundCloudClientIdCache> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Returns current client_id. Scrapes on first call or after expiry.
    /// Single-flight: only one scrape runs even under concurrent callers.
    /// Returns null if scrape failed.
    /// </summary>
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        // Fast path: valid cached value
        if (!string.IsNullOrEmpty(_clientId) && DateTimeOffset.UtcNow < _expiresAt)
            return _clientId;

        // Slow path: need to scrape (single-flight)
        await _scrapeLock.WaitAsync(ct);
        try
        {
            // Double-check inside lock (another waiter may have refreshed)
            if (!string.IsNullOrEmpty(_clientId) && DateTimeOffset.UtcNow < _expiresAt)
                return _clientId;

            var scraped = await ScrapeAsync(ct);
            if (!string.IsNullOrEmpty(scraped))
            {
                _clientId  = scraped;
                _expiresAt = DateTimeOffset.UtcNow.AddHours(TtlHours);
                _logger.LogInformation(
                    "SoundCloudClientIdCache: scraped new client_id (len={Len}), expires {Expires}",
                    scraped.Length, _expiresAt);
            }
            return _clientId;
        }
        finally
        {
            _scrapeLock.Release();
        }
    }

    /// <summary>
    /// Invalidate cached client_id. Call on 401/403 from api-v2.soundcloud.com.
    /// Next GetAsync() will re-scrape.
    /// </summary>
    public void Invalidate()
    {
        _clientId  = null;
        _expiresAt = DateTimeOffset.MinValue;
        _logger.LogInformation("SoundCloudClientIdCache: invalidated (will re-scrape on next GetAsync)");
    }

    // from server.js:499-518
    private async Task<string?> ScrapeAsync(CancellationToken ct)
    {
        try
        {
            // Step 1: fetch SoundCloud homepage to find asset script URLs (server.js:500)
            var html = await HttpHelpers.HttpsGetAsync(_factory, "art-generic", "https://soundcloud.com/", _logger, ct);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("SoundCloudClientIdCache: homepage fetch returned empty");
                return null;
            }

            // Step 2: extract script URLs matching a-v2.sndcdn.com/assets/*.js (server.js:501)
            var scriptMatches = s_scriptPattern.Matches(html);
            if (scriptMatches.Count == 0)
            {
                _logger.LogWarning(
                    "SoundCloudClientIdCache: no asset scripts found in homepage HTML ({Len} bytes). " +
                    "SoundCloud HTML structure may have changed. Pattern: src=\"(https://a-v2.sndcdn.com/assets/[hex]*.js)\"",
                    html.Length);
                return null;
            }

            // Step 3: iterate scripts in REVERSE (last script is most likely to have client_id, server.js:503)
            for (int i = scriptMatches.Count - 1; i >= 0; i--)
            {
                var scriptUrl = scriptMatches[i].Groups[1].Value;
                try
                {
                    var js = await HttpHelpers.HttpsGetAsync(_factory, "art-generic", scriptUrl, _logger, ct);
                    if (string.IsNullOrEmpty(js)) continue;

                    // Step 4: extract client_id from JS bundle (server.js:506)
                    var m = s_clientIdPattern.Match(js);
                    if (m.Success)
                        return m.Groups[1].Value;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SoundCloudClientIdCache: fetch {Url} threw", scriptUrl);
                }
            }

            // All scripts checked, none had client_id -- HTML structure may have changed
            _logger.LogWarning(
                "SoundCloudClientIdCache: scraped {Count} script(s) but none contained client_id. " +
                "SoundCloud's JS bundling structure may have changed. " +
                "Pattern: client_id\\s*[:=]\\s*[\"']([a-zA-Z0-9]{{32}})[\"']",
                scriptMatches.Count);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SoundCloudClientIdCache: scrape threw unexpectedly");
            return null;
        }
    }
}
