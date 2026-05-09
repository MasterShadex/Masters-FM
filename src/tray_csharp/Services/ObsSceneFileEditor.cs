// Stage 7.8B STEP 4: ObsSceneFileEditor -- file-edit fallback for OBS < 28 or
// WebSocket-disabled scenarios. Mirrors tray.ps1:3805-4075 (Add-OBSBrowserSourceDirect).
//
// Reads and writes %APPDATA%\obs-studio\basic\scenes\*.json directly.
// OBS must be restarted to pick up changes when it is running.
// UTF-8 no-BOM output (NoBomUtf8) to match OBS scene-collection format.
//
// Stage 7.8D: AddBrowserSource returns AddBrowserSourceResult with UUID.
// New: RemoveBrowserSourceByUuid (UUID-targeted; protects user-added sources).
// New: ScanForBrowserSources returns metadata for all Master's FM entries.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MastersFM.Tray.Services;

// ── Result types ──────────────────────────────────────────────────────────────

/// <summary>Stage 7.8D: result of AddBrowserSource, carrying the UUID.</summary>
public sealed record AddBrowserSourceResult(bool Success, string? Uuid, string? Reason)
{
    public static AddBrowserSourceResult Ok(string uuid) =>
        new(true, uuid, null);
    public static AddBrowserSourceResult AlreadyPresent(string uuid) =>
        new(true, uuid, "already present (idempotent)");
    public static AddBrowserSourceResult ForeignSource(string uuid) =>
        new(false, uuid, "user-added Master's FM source exists; not modified");
    public static AddBrowserSourceResult Failed(string reason) =>
        new(false, null, reason);
}

/// <summary>Stage 7.8D: entry returned by ScanForBrowserSources.</summary>
public sealed record BrowserSourceScanResult(string Uuid, string CollectionPath, string Url);

// ── Editor ────────────────────────────────────────────────────────────────────

internal sealed class ObsSceneFileEditor
{
    private readonly ILogger _logger;
    private static readonly UTF8Encoding NoBomUtf8 = new(false);

    private static readonly JsonSerializerOptions WriteOpts =
        new() { WriteIndented = true };

    public ObsSceneFileEditor(ILogger logger) { _logger = logger; }

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if any scene collection on disk contains a "Master's FM" browser source.
    /// Stage 7.8C: used to initialise ObsToggleState at tray startup.
    /// </summary>
    public bool BrowserSourceExists()
    {
        var paths = GetSceneCollectionPaths();
        foreach (var path in paths)
        {
            try
            {
                var json = File.ReadAllText(path, NoBomUtf8);
                var root = JsonNode.Parse(json);
                var sources = root?["sources"]?.AsArray();
                if (sources == null) continue;
                foreach (var src in sources)
                {
                    if (src?["name"]?.GetValue<string>() == "Master's FM")
                        return true;
                }
            }
            catch { /* best-effort */ }
        }
        return false;
    }

    /// <summary>
    /// Stage 7.8D: returns metadata for every "Master's FM" browser source found
    /// across all scene collections. Used by ReconcileStateAsync to distinguish
    /// "ours" (UUID matches obs.tray_added_uuid) from foreign user-added sources.
    /// </summary>
    public List<BrowserSourceScanResult> ScanForBrowserSources()
    {
        var results = new List<BrowserSourceScanResult>();
        var paths = GetSceneCollectionPaths();
        foreach (var path in paths)
        {
            try
            {
                var json = File.ReadAllText(path, NoBomUtf8);
                var root = JsonNode.Parse(json);
                var sources = root?["sources"]?.AsArray();
                if (sources == null) continue;
                foreach (var src in sources)
                {
                    if (src?["name"]?.GetValue<string>() != "Master's FM") continue;
                    var uuid = src?["uuid"]?.GetValue<string>() ?? "";
                    var url  = src?["settings"]?["url"]?.GetValue<string>() ?? "";
                    results.Add(new BrowserSourceScanResult(uuid, path, url));
                }
            }
            catch (Exception ex)
            {
                _logger.LogErr($"[ObsSceneFileEditor] ScanForBrowserSources failed for {path}",
                    ex, "OBS");
            }
        }
        return results;
    }

    /// <summary>
    /// Stage 7.8D: Adds a Master's FM browser source to every scene collection.
    /// Returns AddBrowserSourceResult carrying the UUID (new or existing).
    /// Pass <paramref name="knownTrayUuid"/> so the editor can recognise "our" source
    /// and distinguish it from user-added sources with the same name.
    /// </summary>
    public AddBrowserSourceResult AddBrowserSource(
        string url, int width, int height, int fps, string css,
        string? knownTrayUuid = null)
    {
        WarnIfObsRunning();
        var paths = GetSceneCollectionPaths();
        if (paths.Length == 0)
        {
            _logger.LogWarn("[ObsSceneFileEditor] No scene-collection files found", "OBS");
            return AddBrowserSourceResult.Failed("no scene-collection files found");
        }

        // Generate ONE UUID for this add operation; reused across all collections
        // so that obs.tray_added_uuid maps to the same source in every collection.
        var generatedUuid = Guid.NewGuid().ToString();

        string? resultUuid = null;
        bool anyAdded      = false;
        bool anyPresent    = false;
        bool anyForeign    = false;
        string? foreignUuid = null;

        foreach (var path in paths)
        {
            try
            {
                var r = AddToCollection(path, url, width, height, fps, css,
                    generatedUuid, knownTrayUuid);
                switch (r.Kind)
                {
                    case CollectionAddKind.Added:
                        anyAdded = true;
                        resultUuid = r.Uuid;
                        _logger.Log(
                            $"[ObsSceneFileEditor] Added 'Master's FM' uuid={r.Uuid} to {path}",
                            "OBS");
                        break;
                    case CollectionAddKind.AlreadyPresent:
                        anyPresent = true;
                        resultUuid ??= r.Uuid;
                        break;
                    case CollectionAddKind.ForeignSource:
                        anyForeign = true;
                        foreignUuid ??= r.Uuid;
                        _logger.LogWarn(
                            $"[ObsSceneFileEditor] foreign source detected: name=Master's FM " +
                            $"uuid={r.Uuid} in {path}; not modified", "OBS");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogErr($"[ObsSceneFileEditor] AddBrowserSource failed for {path}",
                    ex, "OBS");
            }
        }

        if (anyAdded)
            return AddBrowserSourceResult.Ok(resultUuid!);
        if (anyPresent)
            return AddBrowserSourceResult.AlreadyPresent(resultUuid!);
        if (anyForeign)
            return AddBrowserSourceResult.ForeignSource(foreignUuid!);
        return AddBrowserSourceResult.Failed("no collections modified");
    }

    /// <summary>
    /// Removes the Master's FM browser source from every scene collection on disk
    /// by source NAME. Kept for compatibility — used by MastersFM_ObsCleanup.exe
    /// on uninstall (aggressive name-based nuke is intentional there).
    /// Returns true if at least one collection was modified.
    /// </summary>
    public bool RemoveBrowserSource()
    {
        WarnIfObsRunning();
        var paths = GetSceneCollectionPaths();
        if (paths.Length == 0) return false;

        bool anyModified = false;
        foreach (var path in paths)
        {
            try
            {
                if (RemoveFromCollection(path))
                {
                    _logger.Log($"[ObsSceneFileEditor] Removed 'Master's FM' from {path}", "OBS");
                    anyModified = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogErr($"[ObsSceneFileEditor] RemoveBrowserSource failed for {path}",
                    ex, "OBS");
            }
        }
        return anyModified;
    }

    /// <summary>
    /// Stage 7.8D: UUID-targeted remove. Removes ONLY sources whose uuid field
    /// exactly matches <paramref name="targetUuid"/>. Protects user-added sources
    /// with the same name but a different UUID. Idempotent (returns false if absent).
    /// </summary>
    public bool RemoveBrowserSourceByUuid(string targetUuid)
    {
        if (string.IsNullOrEmpty(targetUuid)) return false;
        WarnIfObsRunning();
        var paths = GetSceneCollectionPaths();
        if (paths.Length == 0) return false;

        bool anyModified = false;
        foreach (var path in paths)
        {
            try
            {
                if (RemoveFromCollectionByUuid(path, targetUuid))
                {
                    _logger.Log(
                        $"[ObsSceneFileEditor] Removed uuid={targetUuid} from {path}", "OBS");
                    anyModified = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogErr(
                    $"[ObsSceneFileEditor] RemoveBrowserSourceByUuid failed for {path}",
                    ex, "OBS");
            }
        }
        return anyModified;
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private void WarnIfObsRunning()
    {
        var obsRunning = Process.GetProcessesByName("obs64").Length > 0
                      || Process.GetProcessesByName("obs32").Length > 0
                      || Process.GetProcessesByName("obs").Length  > 0;
        if (obsRunning)
            _logger.LogWarn(
                "[ObsSceneFileEditor] OBS is running -- file-edit changes take effect after OBS restart",
                "OBS");
    }

    private static string[] GetSceneCollectionPaths()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio", "basic", "scenes");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.json");
    }

    // ── Internal result for AddToCollection ──────────────────────────────────

    private enum CollectionAddKind { Added, AlreadyPresent, ForeignSource, Skipped }
    private readonly record struct CollectionAddResult(CollectionAddKind Kind, string? Uuid);

    private CollectionAddResult AddToCollection(
        string path, string url, int width, int height, int fps, string css,
        string generatedUuid, string? knownTrayUuid)
    {
        var json = File.ReadAllText(path, NoBomUtf8);
        var root = JsonNode.Parse(json);
        if (root == null) return new(CollectionAddKind.Skipped, null);

        // Idempotent check: if Master's FM source already exists, check URL.
        // Stage 7.8C GAP-1 fix: update URL in-place (preserving UUID) if different.
        // Stage 7.8D: distinguish "ours" (UUID matches knownTrayUuid) from "foreign".
        var sources = root["sources"]?.AsArray();
        if (sources != null)
        {
            foreach (var src in sources)
            {
                if (src?["name"]?.GetValue<string>() != "Master's FM") continue;
                var existingUuid = src?["uuid"]?.GetValue<string>() ?? "";
                var existingUrl  = src?["settings"]?["url"]?.GetValue<string>() ?? "";

                if (string.Equals(existingUrl, url, StringComparison.Ordinal))
                    return new(CollectionAddKind.AlreadyPresent, existingUuid); // URL already matches

                // URL mismatch — is this "ours"?
                bool isOurs = !string.IsNullOrEmpty(knownTrayUuid)
                    && string.Equals(existingUuid, knownTrayUuid, StringComparison.Ordinal);

                if (isOurs)
                {
                    // Update URL in-place, preserve UUID (Stage 7.8C GAP-1)
                    if (src?["settings"] is JsonObject settings)
                        settings["url"] = url;
                    var updatedOutput = root.ToJsonString(WriteOpts);
                    _ = JsonNode.Parse(updatedOutput)
                        ?? throw new InvalidOperationException(
                            "JSON output parse-back failed (URL update)");
                    File.WriteAllText(path, updatedOutput, NoBomUtf8);
                    return new(CollectionAddKind.Added, existingUuid);
                }

                // URL differs and UUID doesn't match ours — foreign source
                return new(CollectionAddKind.ForeignSource, existingUuid);
            }
        }

        // Build new browser_source entry (matches tray.ps1:3905-3940 schema)
        var newSource = new JsonObject
        {
            ["versioned_id"]           = "browser_source",
            ["id"]                     = "browser_source",
            ["name"]                   = "Master's FM",
            ["uuid"]                   = generatedUuid,
            ["settings"]               = new JsonObject
            {
                ["url"]                    = url,
                ["width"]                  = width,
                ["height"]                 = height,
                ["fps"]                    = fps,
                ["fps_custom"]             = false,
                ["css"]                    = css,
                ["shutdown"]               = false,
                ["restart_when_active"]    = false,
                ["webpage_control_level"]  = 1
            },
            ["mixers"]                 = 0,
            ["monitoring_type"]        = 0,
            ["balance"]                = 0.5,
            ["enabled"]                = true,
            ["muted"]                  = false,
            ["push_to_mute_delay"]     = 0,
            ["push_to_talk_delay"]     = 0,
            ["deinterlace_field_order"] = 0,
            ["deinterlace_mode"]       = 0,
            ["audio_mixers"]           = 255,
            ["flags"]                  = 0,
            ["filter_order"]           = new JsonArray(),
            ["sync"]                   = 0,
            ["volume"]                 = 1.0
        };

        if (sources == null)
        {
            root["sources"] = new JsonArray { newSource };
        }
        else
        {
            sources.Add(newSource);
        }

        // Add scene_item to every scene (matches tray.ps1:3952-3998)
        var sceneOrder = root["scene_order"]?.AsArray();
        if (sceneOrder != null)
        {
            foreach (var sceneEntry in sceneOrder)
            {
                var sceneName = sceneEntry?["name"]?.GetValue<string>();
                if (sceneName == null) continue;

                var allSources = root["sources"]?.AsArray();
                if (allSources == null) continue;
                foreach (var src in allSources)
                {
                    if (src?["name"]?.GetValue<string>() != sceneName) continue;
                    if (src?["id"]?.GetValue<string>() != "scene") continue;

                    var items = src?["settings"]?["items"]?.AsArray();
                    if (items == null) continue;

                    long nextId = 1;
                    foreach (var item in items)
                    {
                        var idVal = item?["id"]?.GetValue<long>() ?? 0;
                        if (idVal >= nextId) nextId = idVal + 1;
                    }

                    items.Add(new JsonObject
                    {
                        ["name"]            = "Master's FM",
                        ["source_uuid"]     = generatedUuid,
                        ["visible"]         = true,
                        ["locked"]          = false,
                        ["pos"]             = new JsonObject { ["x"] = 0.0, ["y"] = 0.0 },
                        ["rot"]             = 0.0,
                        ["scale"]           = new JsonObject { ["x"] = 1.0, ["y"] = 1.0 },
                        ["align"]           = 5,
                        ["bounds_type"]     = 0,
                        ["bounds_align"]    = 0,
                        ["bounds"]          = new JsonObject { ["x"] = 0.0, ["y"] = 0.0 },
                        ["crop_top"]        = 0,
                        ["crop_right"]      = 0,
                        ["crop_bottom"]     = 0,
                        ["crop_left"]       = 0,
                        ["id"]              = nextId,
                        ["group_item_id"]   = 0,
                        ["scale_filter"]    = "disable",
                        ["blend_method"]    = "default",
                        ["blend_type"]      = 0,
                        ["show_transition"] = new JsonObject
                            { ["duration"] = 300, ["id"] = "fade_transition" },
                        ["hide_transition"] = new JsonObject
                            { ["duration"] = 300, ["id"] = "fade_transition" }
                    });
                }
            }
        }

        // Stage 7.8C GAP-2: validate parse-back before writing (Safety Floor S5)
        var addOutput = root.ToJsonString(WriteOpts);
        _ = JsonNode.Parse(addOutput)
            ?? throw new InvalidOperationException("JSON output parse-back failed (add)");
        File.WriteAllText(path, addOutput, NoBomUtf8);
        return new(CollectionAddKind.Added, generatedUuid);
    }

    private bool RemoveFromCollection(string path)
    {
        var json = File.ReadAllText(path, NoBomUtf8);
        var root = JsonNode.Parse(json);
        if (root == null) return false;

        bool modified = false;

        var sources = root["sources"]?.AsArray();
        if (sources != null)
        {
            string? sourceUuid = null;

            // Remove source from sources[]
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                if (sources[i]?["name"]?.GetValue<string>() == "Master's FM")
                {
                    sourceUuid = sources[i]?["uuid"]?.GetValue<string>();
                    sources.RemoveAt(i);
                    modified = true;
                }
            }

            // Remove scene_items referencing the source by UUID
            if (sourceUuid != null)
            {
                foreach (var src in sources)
                {
                    if (src?["id"]?.GetValue<string>() != "scene") continue;
                    var items = src["settings"]?["items"]?.AsArray();
                    if (items == null) continue;
                    for (int i = items.Count - 1; i >= 0; i--)
                    {
                        if (items[i]?["source_uuid"]?.GetValue<string>() == sourceUuid)
                        {
                            items.RemoveAt(i);
                            modified = true;
                        }
                    }
                }
            }
        }

        if (modified)
        {
            // Stage 7.8C GAP-2: validate parse-back before writing (Safety Floor S5)
            var removeOutput = root.ToJsonString(WriteOpts);
            _ = JsonNode.Parse(removeOutput)
                ?? throw new InvalidOperationException("JSON output parse-back failed (remove)");
            File.WriteAllText(path, removeOutput, NoBomUtf8);
        }

        return modified;
    }

    // Stage 7.8D: UUID-targeted remove helper
    private bool RemoveFromCollectionByUuid(string path, string targetUuid)
    {
        var json = File.ReadAllText(path, NoBomUtf8);
        var root = JsonNode.Parse(json);
        if (root == null) return false;

        bool modified = false;
        var sources = root["sources"]?.AsArray();
        if (sources == null) return false;

        // Remove ONLY the source whose uuid == targetUuid
        for (int i = sources.Count - 1; i >= 0; i--)
        {
            var srcUuid = sources[i]?["uuid"]?.GetValue<string>();
            if (!string.Equals(srcUuid, targetUuid, StringComparison.Ordinal)) continue;
            sources.RemoveAt(i);
            modified = true;
        }

        // Remove scene_items referencing the removed source by UUID
        if (modified)
        {
            foreach (var src in sources)
            {
                if (src?["id"]?.GetValue<string>() != "scene") continue;
                var items = src["settings"]?["items"]?.AsArray();
                if (items == null) continue;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i]?["source_uuid"]?.GetValue<string>() == targetUuid)
                    {
                        items.RemoveAt(i);
                    }
                }
            }

            // Stage 7.8C GAP-2: validate parse-back before writing (Safety Floor S5)
            var removeOutput = root.ToJsonString(WriteOpts);
            _ = JsonNode.Parse(removeOutput)
                ?? throw new InvalidOperationException(
                    "JSON output parse-back failed (remove by uuid)");
            File.WriteAllText(path, removeOutput, NoBomUtf8);
        }

        return modified;
    }
}
