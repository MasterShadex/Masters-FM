// Stage 7.4: ConfigService. JsonNode round-trip (no strongly-typed records),
// 1 s cache, atomic write-temp-then-rename, FileSystemWatcher with debounced
// echo handling, dual-flag welcome-seen accessor.
//
// Path: %APPDATA%\MastersFM\config.json (ROAMING; matches PS tray
// tray.ps1:1456-1459, NOT %LOCALAPPDATA% as the brief incorrectly stated).
// PS source is canonical for round-trip parity.
//
// Encoding: UTF-8 no BOM via new UTF8Encoding(false). Critical for PS round-trip
// per absolute rule 3 of brief.

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MastersFM.Tray.Services;

public sealed class ConfigService : IConfigService, IDisposable
{
    private const int CacheTtlMs = 1000;
    private const int WatcherDebounceMs = 200;
    private const string Component = "Config";

    private static readonly Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions _jsonWriteOpts = new()
    {
        WriteIndented = true,
        // .NET 8 requires an explicit TypeInfoResolver when the options are
        // marked read-only (which happens after first use of a static-readonly
        // instance). DefaultJsonTypeInfoResolver provides reflection-based
        // resolution for arbitrary types passed to JsonValue.Create<T>.
        // Without this, ToJsonString throws InvalidOperationException on the
        // first SetValue call. (Latent 7.4 ConfigService bug surfaced during
        // 7.2 smoke when update.lastChecked persistence first exercised the
        // C# write path; 7.4 verified the write path by code review only,
        // missing this runtime-only failure mode.)
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private readonly ILogger _logger;
    private readonly string _configPath;
    private readonly string _configDir;
    private readonly object _lock = new();
    private readonly FileSystemWatcher? _watcher;

    private JsonNode? _cachedRoot;
    private DateTime _cacheTimestamp = DateTime.MinValue;
    private DateTime _lastOwnWriteUtc = DateTime.MinValue;

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public ConfigService(ILogger logger)
    {
        _logger = logger;

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _configDir = Path.Combine(roaming, "MastersFM");
        _configPath = Path.Combine(_configDir, "config.json");

        try
        {
            Directory.CreateDirectory(_configDir);
        }
        catch (Exception ex)
        {
            _logger.LogErr($"ConfigService directory create at {_configDir}", ex, Component);
        }

        // Initial read attempt (warms the cache if file exists)
        try
        {
            ReadFromDisk_NoLock();
            if (_cachedRoot == null)
            {
                _logger.Log($"no config file at {_configPath}; defaults will apply", Component);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("ConfigService initial read", ex, Component);
        }

        try
        {
            _watcher = new FileSystemWatcher(_configDir, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;
        }
        catch (Exception ex)
        {
            _logger.LogErr("ConfigService FileSystemWatcher init", ex, Component);
        }

        _logger.Log($"ConfigService initialized; path={_configPath}, ttl={CacheTtlMs}ms", Component);
    }

    public JsonNode? GetRoot()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_cachedRoot != null && (now - _cacheTimestamp).TotalMilliseconds < CacheTtlMs)
            {
                return _cachedRoot;
            }
            ReadFromDisk_NoLock();
            return _cachedRoot;
        }
    }

    public T? GetValue<T>(string keyPath, T? defaultValue = default)
    {
        if (string.IsNullOrEmpty(keyPath)) return defaultValue;

        var root = GetRoot();
        if (root == null) return defaultValue;

        var segments = keyPath.Split('.');
        JsonNode? cursor = root;
        foreach (var seg in segments)
        {
            if (cursor is JsonObject obj && obj.TryGetPropertyValue(seg, out var child))
            {
                cursor = child;
            }
            else
            {
                return defaultValue;
            }
        }

        if (cursor == null) return defaultValue;

        try
        {
            // System.Text.Json's GetValue<T> on JsonValue handles bool/string/int/long/double/float.
            if (cursor is JsonValue value)
            {
                return value.GetValue<T>();
            }
            // Caller asked for a complex type (e.g., JsonObject, array) -- not the
            // primitive-T contract this method advertises. Return default.
            return defaultValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"GetValue<{typeof(T).Name}> at '{keyPath}' type-mismatch: {ex.Message}", Component);
            return defaultValue;
        }
    }

    public void SetValue<T>(string keyPath, T value)
    {
        if (string.IsNullOrEmpty(keyPath)) return;

        lock (_lock)
        {
            // Force a fresh read so we don't lose external changes between cache TTL and our write
            ReadFromDisk_NoLock();
            var root = _cachedRoot as JsonObject ?? new JsonObject();

            var segments = keyPath.Split('.');
            JsonObject cursor = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                if (cursor.TryGetPropertyValue(seg, out var child) && child is JsonObject childObj)
                {
                    cursor = childObj;
                }
                else
                {
                    var newChild = new JsonObject();
                    // If a non-object existed there, overwrite it (rare; user did SetValue
                    // on a path that conflicts with an existing leaf).
                    cursor[seg] = newChild;
                    cursor = newChild;
                }
            }

            var terminal = segments[segments.Length - 1];
            cursor[terminal] = JsonValue.Create(value);

            _cachedRoot = root;
            _cacheTimestamp = DateTime.UtcNow;

            try
            {
                WriteAtomic_NoLock(root);
                _logger.Log($"set {keyPath} = {FormatValue(value)}; persisted", Component);
            }
            catch (Exception ex)
            {
                _logger.LogErr($"SetValue atomic write at '{keyPath}'", ex, Component);
                return;
            }
        }

        // Fire event outside the lock to avoid deadlocks in subscribers.
        Changed?.Invoke(this, new ConfigChangedEventArgs(keyPath));
    }

    public bool GetWelcomeSeen()
    {
        // Mirrors tray.ps1:1623-1636 dual-flag logic.
        // Hotfix 7.10: normalize stored version before comparison.
        // PS tray writes "v14.0.0-rc.1" (v-prefix); GetCurrentAppVersion()
        // returns "14.0.0-rc.1" (no v). Ordinal compare without normalization
        // always returned false, causing the wizard to show on every launch.
        var seenVersion = GetValue<string>("welcome_seen_version");
        if (string.IsNullOrEmpty(seenVersion))
        {
            // Legacy bool-only with no version: treat as 'seen v0' -> force re-show.
            return false;
        }
        var currentVersion = GetCurrentAppVersion();
        return string.Equals(NormalizeVersion(seenVersion), currentVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips leading "v"/"V" prefix and "+build.metadata" suffix so that
    /// "v14.0.0-rc.1" and "14.0.0-rc.1+stage7.x" both normalize to "14.0.0-rc.1".
    /// </summary>
    private static string NormalizeVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return v;
        // Strip leading 'v' or 'V' written by the PS tray.
        if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V')) v = v.Substring(1);
        // Strip '+build.metadata' from InformationalVersion.
        var plus = v.IndexOf('+');
        return plus >= 0 ? v.Substring(0, plus) : v;
    }

    public void SetWelcomeSeen(bool seen)
    {
        if (seen)
        {
            SetValue<string>("welcome_seen_version", GetCurrentAppVersion());
            // Legacy bool flag for backward compatibility with very old PS tray builds.
            SetValue<bool>("welcome_seen", true);
        }
        else
        {
            // Remove both flags so legacy bool doesn't linger.
            lock (_lock)
            {
                ReadFromDisk_NoLock();
                if (_cachedRoot is JsonObject root)
                {
                    root.Remove("welcome_seen_version");
                    root.Remove("welcome_seen");
                    _cachedRoot = root;
                    _cacheTimestamp = DateTime.UtcNow;
                    try
                    {
                        WriteAtomic_NoLock(root);
                        _logger.Log("welcome-seen cleared (both flags removed); persisted", Component);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogErr("SetWelcomeSeen(false) atomic write", ex, Component);
                        return;
                    }
                }
            }
            Changed?.Invoke(this, new ConfigChangedEventArgs("welcome_seen_version"));
        }
    }

    public void Reload()
    {
        lock (_lock)
        {
            _cachedRoot = null;
            _cacheTimestamp = DateTime.MinValue;
        }
        _logger.Log("Reload: cache invalidated", Component);
    }

    public void Dispose()
    {
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileSystemEvent;
                _watcher.Created -= OnFileSystemEvent;
                _watcher.Renamed -= OnFileSystemEvent;
                _watcher.Dispose();
            }
        }
        catch
        {
            // best-effort
        }
    }

    // --- private helpers --------------------------------------------------------

    private void ReadFromDisk_NoLock()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _cachedRoot = null;
                _cacheTimestamp = DateTime.UtcNow;
                return;
            }
            var text = File.ReadAllText(_configPath, _utf8NoBom);
            // System.Text.Json handles BOM transparently; no need for explicit strip.
            if (string.IsNullOrWhiteSpace(text))
            {
                _cachedRoot = null;
            }
            else
            {
                _cachedRoot = JsonNode.Parse(text);
            }
            _cacheTimestamp = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"config parse failed; using cached or null: {ex.Message}", Component);
            // Don't poison the cache with bad data; leave _cachedRoot as it was.
        }
    }

    private void WriteAtomic_NoLock(JsonNode root)
    {
        var json = root.ToJsonString(_jsonWriteOpts);
        var tempPath = _configPath + ".tmp";
        File.WriteAllText(tempPath, json, _utf8NoBom);
        try
        {
            File.Move(tempPath, _configPath, overwrite: true);
        }
        catch
        {
            // Cleanup temp on failure
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
        _lastOwnWriteUtc = DateTime.UtcNow;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        // Debounce echo from own atomic-write
        var sinceOwnWrite = DateTime.UtcNow - _lastOwnWriteUtc;
        if (sinceOwnWrite.TotalMilliseconds < WatcherDebounceMs)
        {
            return;
        }

        lock (_lock)
        {
            _cachedRoot = null;
            _cacheTimestamp = DateTime.MinValue;
        }
        _logger.Log("external change detected; cache invalidated", Component);
        Changed?.Invoke(this, new ConfigChangedEventArgs(null));
    }

    private static string GetCurrentAppVersion()
    {
        // Use AssemblyInformationalVersion (e.g., "14.0.0-rc.1+stage7.1B.skeleton")
        // stripped to the SemVer prefix before "+". Falls back to AssemblyVersion if missing.
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
        var ver = asm.GetName().Version;
        return ver != null ? ver.ToString() : "0.0.0";
    }

    private static string FormatValue<T>(T value)
    {
        if (value == null) return "(null)";
        var s = value.ToString() ?? "(null)";
        // Truncate long values in log output
        return s.Length > 80 ? s.Substring(0, 77) + "..." : s;
    }
}
