using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// UTF-8 without BOM -- server.js writes preset files as plain UTF-8 (no BOM)
// and ReadAllBytes returns them as-is. Using Encoding.UTF8 in .NET adds a BOM
// which breaks JSON.parse in the browser. Use s_utf8NoBom for all file writes.


namespace MastersFM.Server;

/// <summary>
/// Preset management endpoints -- sub-stage 4.5 (4.6 scope folded in).
/// Ports server.js handlers (lines 1407-1475).
///
/// Endpoints:
///   GET    /list-presets             -- sorted array of preset names
///   POST   /save-preset              -- write preset file
///   GET    /load-preset?name=...     -- read preset file
///   DELETE /delete-preset?name=...  -- delete preset file
///
/// Name sanitization mirrors server.js exactly:
///   - Regex.Replace(name, @"[^a-zA-Z0-9 _\-]", "")
///   - Trim()
///   - Substring(0, 64) on save (not on load/delete)
///   - Empty after sanitize -> 400 Invalid name
/// </summary>
internal static class PresetHandler
{
    private static readonly byte[] s_okBytes   = Encoding.UTF8.GetBytes("OK");
    private static readonly byte[] s_notFound  = Encoding.UTF8.GetBytes("Not found");
    // No-BOM UTF-8 for file writes (matches server.js which never writes a BOM)
    private static readonly Encoding s_utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // Matches server.js: /[^a-zA-Z0-9 _\-]/g
    private static readonly Regex s_nameStrip = new Regex(@"[^a-zA-Z0-9 _\-]",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/list-presets",    ListPresetsAsync);
        app.MapPost("/save-preset",    SavePresetAsync);
        app.MapGet("/load-preset",     LoadPresetAsync);
        app.MapDelete("/delete-preset", DeletePresetAsync);
    }

    /// <summary>
    /// Sanitize preset name per server.js lines 1440 / 1456 / 1468.
    /// forSave=true enforces 64-char max; forSave=false allows any length (load/delete).
    /// Returns null when result is empty.
    /// </summary>
    public static string? SanitizeName(string? raw, bool forSave = true)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var stripped = s_nameStrip.Replace(raw, "");
        var trimmed  = stripped.Trim();
        if (trimmed.Length == 0) return null;
        if (forSave && trimmed.Length > 64) trimmed = trimmed.Substring(0, 64);
        return trimmed;
    }

    // ── GET /list-presets ─────────────────────────────────────────────────────
    // Mirrors server.js lines 1407-1418.
    // Returns sorted JSON array. Returns [] on any error (no dir, empty, etc.).
    private static async Task ListPresetsAsync(
        HttpContext ctx, ServerState state, ILogger<Program> logger, CancellationToken ct)
    {
        byte[] responseBytes;
        try
        {
            var dir = state.PresetsDir;
            Directory.CreateDirectory(dir); // ensure exists (mirrors server.js getPresetsDir mkdirSync)

            var names = Directory.EnumerateFiles(dir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n, StringComparer.Ordinal)  // locale sort -- matches server.js localeCompare
                .ToArray();

            responseBytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "list-presets error");
            responseBytes = Encoding.UTF8.GetBytes("[]");
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength = responseBytes.Length;
        await ctx.Response.Body.WriteAsync(responseBytes, ct);
    }

    // ── POST /save-preset ─────────────────────────────────────────────────────
    // Mirrors server.js lines 1421-1450.
    // Body: { "name": "...", "config": {...} }
    // Returns: { "saved": "<sanitized name>" }
    private static async Task SavePresetAsync(
        HttpContext ctx, ServerState state, ILogger<Program> logger, CancellationToken ct)
    {
        // Read body
        string bodyText;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            bodyText = await reader.ReadToEndAsync(ct);
        }
        catch
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Bad JSON"), ct);
            return;
        }

        // Parse body -- separate try for bad JSON (server.js PHASE B fix pattern)
        JsonObject? parsed;
        try
        {
            parsed = JsonNode.Parse(bodyText)?.AsObject();
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Bad JSON"), ct);
            return;
        }

        if (parsed == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Bad JSON"), ct);
            return;
        }

        // Validate name (server.js: if (!name) -> 400 Name required)
        var rawName = (string?)parsed["name"];
        if (string.IsNullOrEmpty(rawName))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Name required"), ct);
            return;
        }

        // Validate config presence (server.js: if (config === undefined || config === null) -> 400)
        if (parsed["config"] == null || !parsed.ContainsKey("config"))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Config required"), ct);
            return;
        }

        // Sanitize name (with 64-char max -- save path)
        var safeName = SanitizeName(rawName, forSave: true);
        if (safeName == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("Invalid name"), ct);
            return;
        }

        // Write preset file with 2-space indent (server.js: JSON.stringify(config, null, 2))
        try
        {
            var dir = state.PresetsDir;
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, safeName + ".json");
            var configNode = parsed["config"]!;
            var configJson = configNode.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, configJson, s_utf8NoBom, ct);
            logger.LogInformation("Preset saved: {Name}", safeName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "save-preset write error: {Name}", safeName);
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(ex.Message), ct);
            return;
        }

        // Return { "saved": "<safeName>" }
        var responseJson = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { saved = safeName }));
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength = responseJson.Length;
        await ctx.Response.Body.WriteAsync(responseJson, ct);
    }

    // ── GET /load-preset?name=... ─────────────────────────────────────────────
    // Mirrors server.js lines 1453-1463.
    // Returns raw file content (preserves JSON.stringify(config, null, 2) formatting).
    // 404 "Not found" if file missing.
    private static async Task LoadPresetAsync(
        HttpContext ctx, ServerState state, ILogger<Program> logger, CancellationToken ct)
    {
        var rawName = ctx.Request.Query["name"].ToString();
        var safeName = SanitizeName(rawName, forSave: false); // no 64-char cap on load

        if (safeName != null)
        {
            var filePath = Path.Combine(state.PresetsDir, safeName + ".json");
            try
            {
                var data = await File.ReadAllBytesAsync(filePath, ct);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength = data.Length;
                await ctx.Response.Body.WriteAsync(data, ct);
                return;
            }
            catch (FileNotFoundException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "load-preset error: {Name}", safeName);
            }
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.Body.WriteAsync(s_notFound, ct);
    }

    // ── DELETE /delete-preset?name=... ────────────────────────────────────────
    // Mirrors server.js lines 1466-1475.
    // Returns 200 "OK" on success, 404 "Not found" if file missing.
    private static async Task DeletePresetAsync(
        HttpContext ctx, ServerState state, ILogger<Program> logger, CancellationToken ct)
    {
        var rawName  = ctx.Request.Query["name"].ToString();
        var safeName = SanitizeName(rawName, forSave: false);

        if (safeName != null)
        {
            var filePath = Path.Combine(state.PresetsDir, safeName + ".json");
            try
            {
                File.Delete(filePath);
                if (!File.Exists(filePath))
                {
                    logger.LogInformation("Preset deleted: {Name}", safeName);
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    await ctx.Response.Body.WriteAsync(s_okBytes, ct);
                    return;
                }
            }
            catch (FileNotFoundException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "delete-preset error: {Name}", safeName);
            }
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.Body.WriteAsync(s_notFound, ct);
    }
}
