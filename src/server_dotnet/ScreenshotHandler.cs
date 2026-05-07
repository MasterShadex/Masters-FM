using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Screenshot endpoints -- sub-stage 4.8.
/// Ports server.js lines 1207-1279.
///
/// Endpoints:
///   GET  /screenshot          -- initiates SSE capture request, waits up to 2500ms
///   POST /screenshot-response -- overlay.html delivers PNG bytes to fulfill pending request
///
/// Flow:
///   GET /screenshot
///     1. 503 if no SSE clients connected
///     2. Allocate requestId + TCS; register in ServerState (409 if already pending -- ID-15)
///     3. Broadcast "event: capture\ndata: {requestId}\n\n" to all SSE clients
///     4. Await TCS with 2500ms linked CancellationTokenSource
///     5a. TCS resolved -> 200 + image/png + bytes
///     5b. Timeout         -> 504 + text/plain + "Overlay did not respond within 2500 ms"
///     5c. TCS faulted     -> 502 + text/plain + error message
///
///   POST /screenshot-response
///     Body: JSON {"requestId":"N", "png":"data:image/png;base64,..."}
///           or  {"requestId":"N", "error":"..."}
///     1. Body size cap: 10 MB
///     2. Parse JSON
///     3. No pending      -> 409 "No screenshot pending"
///     4. requestId mismatch -> 409 "requestId mismatch (stale response)"
///     5. j.error present -> fail TCS; 200 "ok (forwarded error)"
///     6. j.png bad format -> fail TCS; 200 "ok (forwarded bad-format error)"
///     7. j.png valid     -> resolve TCS with bytes; 200 "ok"
///     Parse error        -> fail TCS (any pending); 400 "parse error: ..."
///
/// Headers per server.js:
///   Cache-Control: no-cache on all /screenshot responses except 503
///   (CORS headers added globally by Middleware 1)
/// </summary>
internal static class ScreenshotHandler
{
    // No-BOM UTF-8 for body reads (project-wide constraint)
    private static readonly Encoding s_utf8NoBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // Regex for valid PNG data URI (server.js: /^data:image\/png;base64,(.+)$/)
    private static readonly Regex s_pngDataUri = new Regex(
        @"^data:image/png;base64,(.+)$",
        RegexOptions.Compiled | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1));

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/screenshot",          HandleRequestAsync);
        app.MapPost("/screenshot-response", HandleResponseAsync);
    }

    // ── GET /screenshot ───────────────────────────────────────────────────────
    // Mirrors server.js lines 1209-1240.
    private static async Task HandleRequestAsync(
        HttpContext ctx,
        ServerState state,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // server.js: if (sseClients.size === 0) -> 503 text/plain, NO Cache-Control
        if (state.ClientCount == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.ContentType = "text/plain";
            var body503 = s_utf8NoBom.GetBytes("No overlay connected (sseClients.size === 0)");
            ctx.Response.ContentLength = body503.Length;
            await ctx.Response.Body.WriteAsync(body503, ct);
            return;
        }

        // server.js: const requestId = String(_ssScreenshot.counter++) -- "0", "1", ...
        var requestId = state.NextScreenshotRequestId();
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new ScreenshotPending { RequestId = requestId, Tcs = tcs };

        // ID-15: reject if already pending (server.js replaces -- we use 409 to prevent state leak)
        if (!state.TryRegisterScreenshot(pending))
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            ctx.Response.ContentType = "text/plain";
            var body409 = s_utf8NoBom.GetBytes("Screenshot already in progress");
            ctx.Response.ContentLength = body409.Length;
            await ctx.Response.Body.WriteAsync(body409, ct);
            return;
        }

        try
        {
            // server.js: const payload = `event: capture\ndata: ${JSON.stringify({ requestId })}\n\n`
            // requestId is already a string; JSON.stringify({requestId:"0"}) = {"requestId":"0"}
            state.Broadcast("{\"requestId\":\"" + requestId + "\"}", "capture");

            logger.LogInformation("Screenshot requested (requestId={RequestId})", requestId);

            // Wait up to 2500ms. Link with request CT so client disconnect also cancels.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            byte[] bytes;
            try
            {
                bytes = await tcs.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // Timeout -- server.js: done(504, 'text/plain', 'Overlay did not respond within 2500 ms')
                logger.LogWarning("Screenshot timeout (requestId={RequestId})", requestId);
                ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                var body504 = s_utf8NoBom.GetBytes("Overlay did not respond within 2500 ms");
                ctx.Response.ContentLength = body504.Length;
                await ctx.Response.Body.WriteAsync(body504, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                // Request cancelled by client disconnect -- not a server error
                logger.LogInformation("Screenshot cancelled (requestId={RequestId})", requestId);
                return;
            }
            catch (Exception ex)
            {
                // TCS faulted -- overlay reported error or bad payload
                // server.js: done(502, 'text/plain', String(msg))
                logger.LogWarning("Screenshot failed (requestId={RequestId}): {Msg}", requestId, ex.Message);
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.Headers["Cache-Control"] = "no-cache";
                    var body502 = s_utf8NoBom.GetBytes(ex.Message);
                    ctx.Response.ContentLength = body502.Length;
                    await ctx.Response.Body.WriteAsync(body502, ct);
                }
                return;
            }

            // server.js: done(200, 'image/png', pngBuffer)
            logger.LogInformation("Screenshot captured (requestId={RequestId}) bytes={Len}",
                requestId, bytes.Length);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "image/png";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes, ct);
        }
        finally
        {
            // Always clear the pending slot to prevent state leak on any exit path
            state.ClearScreenshotIfMatch(requestId);
        }
    }

    // ── POST /screenshot-response ─────────────────────────────────────────────
    // Mirrors server.js lines 1242-1278.
    private static async Task HandleResponseAsync(
        HttpContext ctx,
        ServerState state,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Body size cap: 10MB (server.js has no cap; we add one to prevent abuse)
        const long MaxBodyBytes = 10L * 1024 * 1024;
        if (ctx.Request.ContentLength > MaxBodyBytes)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.Body.WriteAsync(s_utf8NoBom.GetBytes("Payload too large"), ct);
            return;
        }

        // Read body
        string bodyText;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, s_utf8NoBom,
                detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
            bodyText = await reader.ReadToEndAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "screenshot-response body read error");
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(s_utf8NoBom.GetBytes("read error"), ct);
            return;
        }

        // Parse JSON -- on failure, fault the current pending (any) and return 400
        // server.js: catch(e) { if (pending) pending.fail(...); res.writeHead(400); }
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(bodyText)?.AsObject();
        }
        catch (JsonException ex)
        {
            logger.LogWarning("screenshot-response JSON parse error: {Msg}", ex.Message);
            var orphan = state.TryTakeAnyScreenshot();
            orphan?.Tcs.TrySetException(new Exception("Server-side parse error: " + ex.Message));
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(
                s_utf8NoBom.GetBytes("parse error: " + ex.Message), ct);
            return;
        }

        if (json == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(s_utf8NoBom.GetBytes("parse error: null body"), ct);
            return;
        }

        // Extract requestId (server.js: j.requestId -- string comparison)
        var requestId = json["requestId"]?.GetValue<string>() ?? "";

        // Take the pending slot -- distinguishes no-pending from requestId mismatch
        var pending = state.TryTakeScreenshot(requestId, out bool hadPending);
        if (pending == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            ctx.Response.ContentType = "text/plain";
            var msg409 = hadPending
                ? "requestId mismatch (stale response)"
                : "No screenshot pending";
            await ctx.Response.Body.WriteAsync(s_utf8NoBom.GetBytes(msg409), ct);
            return;
        }

        // server.js: if (j.error) { pending.fail(j.error); res.writeHead(200); res.end('ok (forwarded error)'); }
        var error = json["error"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(error))
        {
            pending.Tcs.TrySetException(new Exception(error));
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.Body.WriteAsync(s_utf8NoBom.GetBytes("ok (forwarded error)"), ct);
            return;
        }

        // server.js: const m = String(j.png || '').match(/^data:image\/png;base64,(.+)$/)
        var pngField = json["png"]?.GetValue<string>() ?? "";
        var match = s_pngDataUri.Match(pngField);
        if (!match.Success)
        {
            // server.js: pending.fail('Bad PNG payload (no data:image/png prefix)')
            pending.Tcs.TrySetException(
                new Exception("Bad PNG payload (no data:image/png prefix)"));
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.Body.WriteAsync(
                s_utf8NoBom.GetBytes("ok (forwarded bad-format error)"), ct);
            return;
        }

        // Decode base64 and resolve TCS
        try
        {
            var pngBytes = Convert.FromBase64String(match.Groups[1].Value);
            pending.Tcs.TrySetResult(pngBytes);
            logger.LogInformation("Screenshot response received (requestId={RequestId}) bytes={Len}",
                requestId, pngBytes.Length);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.Body.WriteAsync(s_utf8NoBom.GetBytes("ok"), ct);
        }
        catch (Exception ex)
        {
            // server.js: catch (e) { pending.fail(...); res.writeHead(400); }
            pending.Tcs.TrySetException(ex);
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(
                s_utf8NoBom.GetBytes("parse error: " + ex.Message), ct);
        }
    }
}
