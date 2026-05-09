// Stage 7.8B STEP 4: ObsSceneFileEditor -- file-edit fallback for OBS < 28 or
// WebSocket-disabled scenarios. Mirrors tray.ps1:3805-4075 (Add-OBSBrowserSourceDirect).
//
// Reads and writes %APPDATA%\obs-studio\basic\scenes\*.json directly.
// OBS must be restarted to pick up changes when it is running.
// UTF-8 no-BOM output (NoBomUtf8) to match OBS scene-collection format.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MastersFM.Tray.Services;

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
    /// Adds a Master's FM browser source to every scene collection on disk.
    /// Idempotent: no-op for collections whose URL already matches; updates URL
    /// in-place (preserving UUID) if a source with a different URL is found.
    /// Returns true if at least one collection was modified.
    /// </summary>
    public bool AddBrowserSource(string url, int width, int height, int fps, string css)
    {
        WarnIfObsRunning();
        var paths = GetSceneCollectionPaths();
        if (paths.Length == 0)
        {
            _logger.LogWarn("[ObsSceneFileEditor] No scene-collection files found", "OBS");
            return false;
        }

        bool anyModified = false;
        foreach (var path in paths)
        {
            try
            {
                if (AddToCollection(path, url, width, height, fps, css))
                {
                    _logger.Log($"[ObsSceneFileEditor] Added 'Master's FM' to {path}", "OBS");
                    anyModified = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogErr($"[ObsSceneFileEditor] Failed for {path}", ex, "OBS");
            }
        }
        return anyModified;
    }

    /// <summary>
    /// Removes the Master's FM browser source from every scene collection on disk.
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
                _logger.LogErr($"[ObsSceneFileEditor] Failed for {path}", ex, "OBS");
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

    private bool AddToCollection(string path, string url, int width, int height,
        int fps, string css)
    {
        var json = File.ReadAllText(path, NoBomUtf8);
        var root = JsonNode.Parse(json);
        if (root == null) return false;

        // Idempotent check: if Master's FM source already exists, check URL.
        // Stage 7.8C GAP-1 fix: update URL in-place (preserving UUID) if different.
        var sources = root["sources"]?.AsArray();
        if (sources != null)
        {
            foreach (var src in sources)
            {
                if (src?["name"]?.GetValue<string>() != "Master's FM") continue;
                var existingUrl = src?["settings"]?["url"]?.GetValue<string>();
                if (string.Equals(existingUrl, url, StringComparison.Ordinal))
                    return false; // URL already matches -- true no-op
                // URL mismatch: update in-place, preserve UUID and all other fields
                if (src?["settings"] is JsonObject settings)
                    settings["url"] = url;
                var updatedOutput = root.ToJsonString(WriteOpts);
                // Stage 7.8C GAP-2: validate parse-back before write
                _ = JsonNode.Parse(updatedOutput)
                    ?? throw new InvalidOperationException("JSON output parse-back failed (URL update)");
                File.WriteAllText(path, updatedOutput, NoBomUtf8);
                return true;
            }
        }

        // Build new browser_source entry (matches tray.ps1:3905-3940 schema)
        var sourceUuid = Guid.NewGuid().ToString();
        var newSource = new JsonObject
        {
            ["versioned_id"]           = "browser_source",
            ["id"]                     = "browser_source",
            ["name"]                   = "Master's FM",
            ["uuid"]                   = sourceUuid,
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

                // Find the matching "scene" source and append item to its items list
                var allSources = root["sources"]?.AsArray();
                if (allSources == null) continue;
                foreach (var src in allSources)
                {
                    if (src?["name"]?.GetValue<string>() != sceneName) continue;
                    if (src?["id"]?.GetValue<string>() != "scene") continue;

                    var items = src?["settings"]?["items"]?.AsArray();
                    if (items == null) continue;

                    // Next scene-item id = max existing + 1
                    long nextId = 1;
                    foreach (var item in items)
                    {
                        var idVal = item?["id"]?.GetValue<long>() ?? 0;
                        if (idVal >= nextId) nextId = idVal + 1;
                    }

                    items.Add(new JsonObject
                    {
                        ["name"]            = "Master's FM",
                        ["source_uuid"]     = sourceUuid,
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
        return true;
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
}
