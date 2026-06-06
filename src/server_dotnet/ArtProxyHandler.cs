using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Art proxy endpoint -- sub-stage 4.7.
/// Ports server.js lines 1169-1190.
///
/// Endpoint:
///   GET /art  -- proxy current track art (HTTPS URL or data: URI)
///
/// Behavior (exact mirror of server.js):
///   currentTrack null or trackArt empty  -> 404, no body
///   trackArt starts with "data:"         -> decode base64, return binary + detected MIME
///   else (HTTPS URL)                     -> stream-proxy via named HttpClient "art-proxy"
///
/// Headers set per server.js:
///   Content-Type  : from upstream response, or detected MIME, or "image/jpeg" fallback
///   Cache-Control : no-store
///   (CORS headers added globally by Middleware 1 in Program.cs)
///
/// Streaming: ResponseHeadersRead + CopyToAsync -- no full-buffer of image data (ID-14).
/// </summary>
internal static class ArtProxyHandler
{
    public static void MapEndpoint(WebApplication app)
        => app.MapGet("/art", HandleAsync);

    private static async Task HandleAsync(
        HttpContext ctx,
        ServerState state,
        IHttpClientFactory httpFactory,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var track = state.CurrentTrack;
        // server.js served currentTrack.trackArt unconditionally. v14 Phase 4 fix:
        // prefer the resolved HTTPS cover (trackArtHttps) -- the SAME field the overlay's
        // bestArt uses to DECIDE which art to show -- falling back to trackArt only when
        // there is no HTTPS art. Without this, browser sources (YouTube via Chrome) stream
        // the SMTC data: URI here (often the Chrome app icon) while the overlay believes it
        // is showing the real thumbnail, so OBS displays the wrong image. Sources with no
        // HTTPS art (osu!, Twitch, pure-SMTC) fall back to trackArt exactly as before.
        var httpsArt   = track?["trackArtHttps"]?.GetValue<string>() ?? "";
        var primaryArt = track?["trackArt"]?.GetValue<string>() ?? "";
        var artUrl = !string.IsNullOrEmpty(httpsArt) ? httpsArt : primaryArt;

        if (string.IsNullOrEmpty(artUrl))
        {
            // server.js: if (!art) { res.writeHead(404); res.end(); return; }
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (artUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            await HandleDataUriAsync(artUrl, ctx, logger, ct);
        }
        else
        {
            await HandleHttpProxyAsync(artUrl, ctx, httpFactory, logger, ct);
        }
    }

    // ── data: URI path ────────────────────────────────────────────────────────
    // Mirrors server.js lines 1173-1179:
    //   const comma = art.indexOf(',');
    //   const mime  = (art.slice(5, comma).split(';')[0]) || 'image/jpeg';
    //   const buf   = Buffer.from(art.slice(comma + 1), 'base64');
    //   res.writeHead(200, { 'Content-Type': mime, 'Cache-Control': 'no-store', ... });
    //   res.end(buf);
    //
    // Notes:
    // - MIME fallback is 'image/jpeg' (not 'image/png') -- per server.js exact
    // - Always base64-decode (server.js has no isBase64 check)
    private static async Task HandleDataUriAsync(
        string dataUri, HttpContext ctx, ILogger<Program> logger, CancellationToken ct)
    {
        try
        {
            var commaIdx = dataUri.IndexOf(',');
            if (commaIdx < 0)
            {
                logger.LogWarning("/art data-uri error: no comma in data URI");
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            // meta = art.slice(5, comma) -- everything between "data:" and first comma
            // e.g. "data:image/jpeg;base64,..." -> meta = "image/jpeg;base64"
            var meta = dataUri.Substring(5, commaIdx - 5);
            // JS: .split(';')[0] takes the MIME type, stripping ";base64" etc.
            var mime = meta.Split(';')[0];
            if (string.IsNullOrEmpty(mime)) mime = "image/jpeg";

            // server.js always decodes as base64 (Buffer.from(..., 'base64'))
            var payload = dataUri.Substring(commaIdx + 1);
            var bytes = Convert.FromBase64String(payload);

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = mime;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            // server.js: catch (e) { flog(`/art data-uri error: ${e.message}`); res.writeHead(500); res.end(); }
            logger.LogWarning(ex, "/art data-uri error: {Message}", ex.Message);
            if (!ctx.Response.HasStarted)
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    // ── HTTPS proxy path ──────────────────────────────────────────────────────
    // Mirrors server.js lines 1181-1187 (logic); uses streaming rather than arrayBuffer (ID-14).
    //
    // server.js:
    //   fetch(art)
    //     .then(r => r.arrayBuffer().then(ab => ({ ct: r.headers.get('content-type') || 'image/jpeg', ab })))
    //     .then(({ ct, ab }) => {
    //       res.writeHead(200, { 'Content-Type': ct, 'Cache-Control': 'no-store', ... });
    //       res.end(Buffer.from(ab));
    //     })
    //     .catch(e => { flog(`/art proxy error: ...`); res.writeHead(502); res.end(); });
    //
    // Content-Type: from upstream header, fallback 'image/jpeg' (matches server.js || 'image/jpeg').
    // Error before headers sent -> 502 (same as server.js catch path).
    // Error mid-stream (headers committed) -> log only; connection truncation is the signal.
    private static async Task HandleHttpProxyAsync(
        string url, HttpContext ctx, IHttpClientFactory factory,
        ILogger<Program> logger, CancellationToken ct)
    {
        HttpResponseMessage? upstreamResp = null;
        try
        {
            var client = factory.CreateClient("art-proxy");
            // ResponseHeadersRead: get headers immediately without buffering body
            upstreamResp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "/art proxy error: {Message}", ex.Message);
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (upstreamResp)
        {
            // server.js: r.headers.get('content-type') || 'image/jpeg'
            var contentType = upstreamResp.Content.Headers.ContentType?.ToString() ?? "image/jpeg";

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers["Cache-Control"] = "no-store";

            // R8: disable Kestrel response buffering so chunks flow immediately to client
            var bufFeat = ctx.Features.Get<IHttpResponseBodyFeature>();
            bufFeat?.DisableBuffering();

            try
            {
                await using var stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
                await stream.CopyToAsync(ctx.Response.Body, ct);
            }
            catch (Exception ex)
            {
                // Headers already committed -- cannot change status code at this point.
                // Log and allow the truncated response to signal failure to the client.
                logger.LogWarning(ex, "/art stream error for {Url}", url);
            }
        }
    }
}
