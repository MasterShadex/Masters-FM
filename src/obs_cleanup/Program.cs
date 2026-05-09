// MastersFM_ObsCleanup.exe — Stage 7.8C STEP 4
//
// Launched during MSI uninstall when OBS is detected as running.
// Waits for OBS to exit (polls every 10 s, max 5 min), removes the
// "Master's FM" browser source from every OBS scene-collection JSON,
// then self-deletes via cmd /c timeout so the running EXE can be purged.
//
// Usage : MastersFM_ObsCleanup.exe [--dry-run]
// Exit  : 0 = success / no-op,  2 = fatal error

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MastersFM.ObsCleanup;

internal static class Program
{
    private const string FmSourceName = "Master's FM";
    private static readonly UTF8Encoding NoBomUtf8  = new(false);
    private static readonly JsonSerializerOptions WriteOpts =
        new() { WriteIndented = true };

    private static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MastersFM", "obs_cleanup.log");

    internal static void Main(string[] args)
    {
        bool dryRun = args.Any(a =>
            a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));

        Log($"MastersFM_ObsCleanup start (dry-run={dryRun} pid={Environment.ProcessId})");

        try
        {
            // Poll until OBS exits (max 30 × 10 s = 5 min)
            if (IsObsRunning())
            {
                Log("OBS running; waiting for exit (polls every 10 s, max 30 polls)");
                int polls = 0;
                while (IsObsRunning() && polls < 30)
                {
                    Thread.Sleep(10_000);
                    polls++;
                }

                if (IsObsRunning())
                    Log("WARNING: OBS still running after 5 min; proceeding anyway");
                else
                    Log($"OBS exited (after {polls} polls)");
            }
            else
            {
                Log("OBS not running at launch; proceeding immediately");
            }

            // Remove browser sources from all scene-collection files
            int modified = RemoveBrowserSources(dryRun);
            Log($"Cleanup complete: {modified} scene-collection file(s) modified" +
                (dryRun ? " [DRY-RUN — no writes]" : ""));
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Environment.Exit(2);
        }

        // Self-delete: spawn a detached cmd that waits 5 s then removes artifacts
        if (!dryRun)
            SelfDelete();

        Environment.Exit(0);
    }

    // ── OBS process check ─────────────────────────────────────────────────────

    private static bool IsObsRunning() =>
        Process.GetProcessesByName("obs64").Length > 0 ||
        Process.GetProcessesByName("obs32").Length > 0 ||
        Process.GetProcessesByName("obs").Length   > 0;

    // ── Scene-file cleanup ────────────────────────────────────────────────────

    private static int RemoveBrowserSources(bool dryRun)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio", "basic", "scenes");
        if (!Directory.Exists(dir)) return 0;

        int count = 0;
        foreach (var path in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                if (RemoveFromCollection(path, dryRun))
                {
                    Log($"  Removed '{FmSourceName}' from {path}");
                    count++;
                }
            }
            catch (Exception ex)
            {
                Log($"  WARN: {path}: {ex.Message}");
            }
        }
        return count;
    }

    private static bool RemoveFromCollection(string path, bool dryRun)
    {
        var json = File.ReadAllText(path, NoBomUtf8);
        var root = JsonNode.Parse(json);
        if (root == null) return false;

        bool modified = false;
        var sources = root["sources"]?.AsArray();
        if (sources == null) return false;

        // Remove source entry; capture UUID to clean scene_items
        string? uuid = null;
        for (int i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i]?["name"]?.GetValue<string>() != FmSourceName) continue;
            uuid = sources[i]?["uuid"]?.GetValue<string>();
            sources.RemoveAt(i);
            modified = true;
        }

        // Remove scene_items referencing the source by UUID
        if (uuid != null)
        {
            foreach (var src in sources)
            {
                if (src?["id"]?.GetValue<string>() != "scene") continue;
                var items = src["settings"]?["items"]?.AsArray();
                if (items == null) continue;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i]?["source_uuid"]?.GetValue<string>() == uuid)
                    {
                        items.RemoveAt(i);
                        modified = true;
                    }
                }
            }
        }

        if (modified && !dryRun)
        {
            var output = root.ToJsonString(WriteOpts);
            // Validate parse-back before writing (Safety Floor S5)
            _ = JsonNode.Parse(output)
                ?? throw new InvalidOperationException("JSON parse-back failed");
            File.WriteAllText(path, output, NoBomUtf8);
        }
        return modified;
    }

    // ── Self-delete ───────────────────────────────────────────────────────────

    private static void SelfDelete()
    {
        var exePath = Environment.ProcessPath;
        if (exePath == null || !File.Exists(exePath)) return;

        var dir      = Path.GetDirectoryName(exePath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(exePath);

        // Collect artifacts to delete (EXE + framework-dependent sidecar files)
        var candidates = new[]
        {
            exePath,
            Path.Combine(dir, baseName + ".dll"),
            Path.Combine(dir, baseName + ".runtimeconfig.json"),
            Path.Combine(dir, baseName + ".deps.json"),
        };

        var delParts = candidates
            .Where(File.Exists)
            .Select(f => $"del /q \"{f}\"");

        // Attempt to remove the cleanup dir (rmdir fails silently if non-empty)
        string delArgs = string.Join(" & ", delParts) +
                         $" & rmdir \"{dir}\" 2>nul";

        Log($"Scheduling self-delete: {delArgs}");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout 5 >nul & {delArgs}")
        {
            CreateNoWindow  = true,
            UseShellExecute = false,
        });
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    private static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        Console.WriteLine(line);
        try
        {
            var logDir = Path.GetDirectoryName(LogPath);
            if (logDir != null && !Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { /* best-effort */ }
    }
}
