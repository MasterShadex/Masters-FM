using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Config endpoints -- sub-stage 4.5.
/// Ports server.js config handlers (lines 1115-1166, 1362-1404, 1477-1514).
///
/// Endpoints:
///   GET  /overlay-config        -- returns cfg.overlay + liveAudioVisualizer injection
///   POST /save-overlay-config   -- replaces cfg.overlay, writes config.json, SSE broadcast
///   GET  /preview-config        -- returns in-memory preview or falls back to saved config
///   POST /preview-config        -- sets in-memory preview, SSE broadcast
///   POST /reload-config         -- reloads Discord RPC settings (stub; 4.10 completes)
///
/// Concurrency: ConfigFileLock serializes config writes. Broadcast always outside the lock.
/// </summary>
internal static class ConfigHandler
{
    private static readonly byte[] s_okBytes  = Encoding.UTF8.GetBytes("OK");
    private static readonly byte[] s_emptyObj = Encoding.UTF8.GetBytes("{}");
    // No-BOM UTF-8 for file writes (matches server.js which never writes a BOM)
    private static readonly Encoding s_utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/overlay-config",      GetOverlayConfigAsync);
        app.MapPost("/save-overlay-config", SaveOverlayConfigAsync);
        app.MapGet("/preview-config",      GetPreviewConfigAsync);
        app.MapPost("/preview-config",     PostPreviewConfigAsync);
        app.MapPost("/reload-config",      ReloadConfigAsync);
    }

    // ── GET /overlay-config ───────────────────────────────────────────────────
    // Mirrors server.js lines 1143-1166.
    // Reads cfg.overlay from disk fresh on every call (overlay polls this every 5s).
    // Injects cfg.liveAudioVisualizer (top-level bool) into the returned object.
    // Error path: returns 200 + {} (matches server.js catch block).
    private static async Task GetOverlayConfigAsync(
        HttpContext ctx, ServerState state, ILogger<Program> logger, CancellationToken ct)
    {
        byte[] responseBytes;
        try
        {
            var cfgPath = state.ConfigPath;
            var raw = await File.ReadAllTextAsync(cfgPath, Encoding.UTF8, ct);
            // Strip UTF-8 BOM if present (matches server.js .replace(/^﻿/, ''))
            if (raw.StartsWith('﻿')) raw = raw[1..];

            var cfg = JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();

            // Get cfg.overlay (or empty object)
            var overlayCfg = cfg["overlay"]?.DeepClone()?.AsObject() ?? new JsonObject();

            // Inject liveAudioVisualizer from top-level config (server.js lines 1155-1157)
            if (cfg["liveAudioVisualizer"] is JsonNode lav)
            {
                bool lavBool = lav.GetValueKind() == JsonValueKind.True
                    || (lav.GetValueKind() == JsonValueKind.Number && lav.GetValue<double>() != 0);
                overlayCfg["liveAudioVisualizer"] = JsonValue.Create(lavBool);
            }

            responseBytes = Encoding.UTF8.GetBytes(overlayCfg.ToJsonString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "overlay-config read error");
            responseBytes = s_emptyObj;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength = responseBytes.Length;
        await ctx.Response.Body.WriteAsync(responseBytes, ct);
    }

    // ── POST /save-overlay-config ─────────────────────────────────────────────
    // Mirrors server.js lines 1477-1514.
    // Replaces cfg.overlay with incoming body, writes full config.json back.
    // Clears previewConfig. SSE broadcasts event: overlay-config.
    // Note: server.js does cfg.overlay = overlay (straight replacement, not deep-merge).
    private static async Task SaveOverlayConfigAsync(
        HttpContext ctx, ServerState state, ILogger<Program> logger, CancellationToken ct)
    {
        // Read body
        string bodyText;
        try
        {
            using var reader = new System.IO.StreamReader(ctx.Request.Body, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            bodyText = await reader.ReadToEndAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "save-overlay-config body read error");
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Bad JSON"), ct);
            return;
        }

        // Parse overlay body
        JsonObject? overlay;
        try
        {
            var parsed = JsonNode.Parse(bodyText);
            if (parsed == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Body required"), ct);
                return;
            }
            overlay = parsed.AsObject();
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Bad JSON"), ct);
            return;
        }

        // Acquire config lock for file write
        await state.ConfigFileLock.WaitAsync(ct);
        string overlayJson;
        try
        {
            var cfgPath = state.ConfigPath;

            // Read current config
            JsonObject cfg;
            try
            {
                var raw = await File.ReadAllTextAsync(cfgPath, Encoding.UTF8, ct);
                if (raw.StartsWith('﻿')) raw = raw[1..];
                cfg = JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();
            }
            catch
            {
                cfg = new JsonObject();
            }

            // Replace cfg.overlay (server.js: cfg.overlay = overlay)
            cfg["overlay"] = overlay.DeepClone();

            // Serialize full config with 2-space indent (matches server.js JSON.stringify(cfg, null, 2))
            var cfgJson = cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            // Atomic write: write to temp file then rename
            var tmpPath = cfgPath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, cfgJson, s_utf8NoBom, ct);
            File.Move(tmpPath, cfgPath, overwrite: true);

            // Clear preview -- saved config is now authoritative
            state.PreviewConfig = null;

            logger.LogInformation("Overlay config saved to disk");
            overlayJson = overlay.ToJsonString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "save-overlay-config error");
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"Save error: {ex.Message}"), ct);
            return;
        }
        finally
        {
            state.ConfigFileLock.Release();
        }

        // Respond 200 OK (before broadcast -- mirrors server.js: res.end('OK') before sseClients loop)
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await ctx.Response.Body.WriteAsync(s_okBytes, ct);

        // Broadcast OUTSIDE the lock (server.js lines 1502-1507)
        // event: overlay-config\ndata: {JSON}\n\n
        state.Broadcast(overlayJson, "overlay-config");
    }

    // ── GET /preview-config ───────────────────────────────────────────────────
    // Mirrors server.js lines 1362-1379.
    // Returns in-memory previewConfig if set; falls back to cfg.overlay from disk.
    private static async Task GetPreviewConfigAsync(
        HttpContext ctx, ServerState state, ILogger<Program> logger, CancellationToken ct)
    {
        byte[] responseBytes;
        try
        {
            var preview = state.PreviewConfig;
            if (preview != null)
            {
                responseBytes = Encoding.UTF8.GetBytes(preview.ToJsonString());
            }
            else
            {
                // Fall back to saved overlay-config (same as GET /overlay-config)
                var cfgPath = state.ConfigPath;
                var raw = await File.ReadAllTextAsync(cfgPath, Encoding.UTF8, ct);
                if (raw.StartsWith('﻿')) raw = raw[1..];
                var cfg = JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();
                var overlay = cfg["overlay"]?.DeepClone()?.AsObject() ?? new JsonObject();
                responseBytes = Encoding.UTF8.GetBytes(overlay.ToJsonString());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "preview-config get error");
            responseBytes = s_emptyObj;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength = responseBytes.Length;
        await ctx.Response.Body.WriteAsync(responseBytes, ct);
    }

    // ── POST /preview-config ──────────────────────────────────────────────────
    // Mirrors server.js lines 1386-1404.
    // Sets in-memory previewConfig. SSE broadcasts event: preview-config.
    private static async Task PostPreviewConfigAsync(
        HttpContext ctx, ServerState state, ILogger<Program> logger, CancellationToken ct)
    {
        string bodyText;
        try
        {
            using var reader = new System.IO.StreamReader(ctx.Request.Body, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            bodyText = await reader.ReadToEndAsync(ct);
        }
        catch
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Bad JSON"), ct);
            return;
        }

        JsonObject preview;
        try
        {
            preview = JsonNode.Parse(bodyText)?.AsObject()
                ?? throw new JsonException("null body");
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Bad JSON"), ct);
            return;
        }

        state.PreviewConfig = preview;

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await ctx.Response.Body.WriteAsync(s_okBytes, ct);

        // Broadcast OUTSIDE any lock
        state.Broadcast(preview.ToJsonString(), "preview-config");
    }

    // ── POST /reload-config ───────────────────────────────────────────────────
    // Mirrors server.js lines 1115-1141.
    // Re-loads Discord RPC settings from config.json, then re-inits / destroys
    // the Discord client as needed.
    // Completed in sub-stage 4.10 (stub ID-12 from 4.5 removed).
    private static async Task ReloadConfigAsync(
        HttpContext ctx,
        ServerState state,
        DiscordRpcService discordRpcService,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        try
        {
            // Delegate fully to DiscordRpcService -- it handles file read + config diff.
            await discordRpcService.ReloadConfigAsync(ct);
            logger.LogInformation("reload-config: Discord RPC settings reloaded");

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.Body.WriteAsync(s_okBytes, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "reload-config error: {Message}", ex.Message);
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.Body.WriteAsync(
                Encoding.UTF8.GetBytes($"Config read error: {ex.Message}"), ct);
        }
    }
}
