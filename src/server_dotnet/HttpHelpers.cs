using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

/// <summary>
/// HTTP helpers for the art cascade and duration resolver.
/// Ports server.js:379-409 (httpsGet, isUrlAccessible).
///
/// ID-16: HttpsGetAsync absorbs all errors into string.Empty (server.js httpsGet rejects;
/// each JS call site wraps in try/catch. C# absorbs at helper level for cleaner call sites.)
/// ID-17: Named HttpClients supply per-source User-Agent headers; musicbrainz uses
/// MastersFM/1.7 per MusicBrainz rate-limit policy (server.js sends Mozilla/5.0).
/// </summary>
internal static class HttpHelpers
{
    /// <summary>
    /// HTTPS GET using a named HttpClient. Returns the response body as a string on success;
    /// returns <see cref="string.Empty"/> on any error (network, timeout, non-2xx status).
    ///
    /// Mirrors server.js:379-393 (httpsGet):
    ///   - Default UA + optional extraHeaders: handled via named HttpClient pre-configuration
    ///   - rejectUnauthorized: false: HttpClientHandler.ServerCertificateCustomValidationCallback
    ///   - setTimeout(5000): named HttpClient Timeout = 5s
    ///   - Redirect follow: AllowAutoRedirect = true, MaxAutomaticRedirections = 5
    ///   - Error/timeout: resolves to empty (server.js rejects; C# absorbs -- ID-16)
    ///
    /// from server.js:379-393
    /// </summary>
    public static async Task<string> HttpsGetAsync(
        IHttpClientFactory factory,
        string clientName,
        string url,
        ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            var client = factory.CreateClient(clientName);
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return string.Empty;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout (not caller-requested cancel) -- return empty, don't rethrow
            logger.LogDebug("HttpsGetAsync timeout for {Url}", url);
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            // Caller-requested cancellation -- rethrow so the caller can handle shutdown/disconnect
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "HttpsGetAsync failed for {Url}", url);
            return string.Empty;
        }
    }

    /// <summary>
    /// HEAD request to verify a URL is accessible. Returns true if the response status
    /// is 2xx (200-299); returns false on any error, timeout, or non-success status.
    /// Never throws.
    ///
    /// Mirrors server.js:395-409 (isUrlAccessible):
    ///   - method: 'HEAD'
    ///   - rejectUnauthorized: false (via named HttpClient)
    ///   - setTimeout(4000): enforced via per-call CancellationTokenSource(4000ms)
    ///   - On error/timeout: resolve(false) → returns false, never throws
    ///   - server.js returns true for 200-399; C# with AllowAutoRedirect=true returns true
    ///     for the final 2xx after following any redirects (IsSuccessStatusCode).
    ///
    /// from server.js:395-409
    /// </summary>
    public static async Task<bool> IsUrlAccessibleAsync(
        IHttpClientFactory factory,
        string clientName,
        string url,
        ILogger logger,
        CancellationToken ct = default)
    {
        // 4-second hard timeout per server.js:405 req.setTimeout(4000, ...)
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var client = factory.CreateClient(clientName);
            using var req  = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await client.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            // Network error, timeout, or invalid URL -- mirrors server.js resolve(false)
            logger.LogDebug("IsUrlAccessibleAsync: not accessible: {Url}", url);
            return false;
        }
    }
}
