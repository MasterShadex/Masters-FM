using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Client-log relay endpoint -- sub-stage 4.7.
/// Ports server.js lines 1102-1113.
///
/// Endpoint:
///   POST /client-log  -- relay frontend log messages from overlay.html / customize.html
///
/// Behavior (exact mirror of server.js):
///   Reads body. Tries to parse as JSON and extract "level" + "msg" fields.
///   Logs: [OVERLAY/{LEVEL}] {msg}   (level defaults to "INFO" if absent)
///   On JSON parse failure: [OVERLAY/RAW] {body}
///   Always returns 204 No Content (fire-and-forget; never fails).
/// </summary>
internal static class ClientLogHandler
{
    // No-BOM UTF-8 for body reads -- consistent with project-wide BOM constraint
    private static readonly Encoding s_utf8NoBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void MapEndpoint(WebApplication app)
        => app.MapPost("/client-log", HandleAsync);

    private static async Task HandleAsync(
        HttpContext ctx,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, s_utf8NoBom,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var body = await reader.ReadToEndAsync(ct);

            // server.js:
            //   const { level, msg } = JSON.parse(body);
            //   flog(`[OVERLAY/${(level||'INFO').toUpperCase()}] ${msg}`);
            try
            {
                var node  = JsonNode.Parse(body)?.AsObject();
                var level = node?["level"]?.GetValue<string>();
                var msg   = node?["msg"]?.GetValue<string>() ?? body;
                var lvl   = string.IsNullOrEmpty(level) ? "INFO" : level.ToUpperInvariant();
                logger.LogInformation("[OVERLAY/{Level}] {Msg}", lvl, msg);
            }
            catch
            {
                // server.js: catch { flog(`[OVERLAY/RAW] ${body}`); }
                logger.LogInformation("[OVERLAY/RAW] {Body}", body);
            }
        }
        catch
        {
            // Fire-and-forget: never fail, even if body read throws
        }

        // server.js: res.writeHead(204); res.end();
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }
}
