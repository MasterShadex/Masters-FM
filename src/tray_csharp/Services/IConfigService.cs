// Stage 7.4: IConfigService interface. ConfigService implements; DI registers
// as singleton. JsonNode round-trip pattern (NOT strongly-typed records) per
// absolute rule 4 of Stage 7.4 brief: preserves unknown fields including
// underscore-prefixed metadata (lesson from Stage 4.5).
//
// Future sub-stages (7.5 detection, 7.6 menu, 7.7 dialogs) consume this
// interface for all config reads/writes. 7.4 enforces only welcome-seen
// (the simplest end-to-end test); other keys are preserved in schema but
// not enforced until their owning sub-stage lands.

using System.Text.Json.Nodes;

namespace MastersFM.Tray.Services;

public interface IConfigService
{
    /// <summary>
    /// Returns the entire config JsonNode (or null if file missing/empty/parse-failed).
    /// Subscribers should treat this as read-only; mutations go through SetValue.
    /// Caller is free to walk the JsonNode tree directly for keys not yet exposed
    /// via dedicated accessors.
    /// </summary>
    JsonNode? GetRoot();

    /// <summary>
    /// Reads a value at a dot-separated key path. Returns defaultValue if any
    /// segment is missing/null or the terminal node is wrong type. T must be
    /// string / int / long / bool / double / float (other types throw).
    /// </summary>
    T? GetValue<T>(string keyPath, T? defaultValue = default);

    /// <summary>
    /// Sets a value at a dot-separated key path. Creates intermediate objects
    /// as needed. Triggers atomic write (write-temp-then-rename) and fires
    /// the Changed event.
    /// </summary>
    void SetValue<T>(string keyPath, T value);

    /// <summary>
    /// Welcome-seen accessor. Honours the PS tray's dual-flag pattern: returns
    /// true ONLY if `welcome_seen_version` equals the current app version.
    /// Legacy `welcome_seen=true` without version returns false (forces re-show
    /// on first version bump after upgrade), matching tray.ps1:1623-1636.
    /// </summary>
    bool GetWelcomeSeen();

    /// <summary>
    /// Sets welcome-seen to true for the current app version (writes
    /// `welcome_seen_version`) or removes the flag for false (deletes the key).
    /// Legacy `welcome_seen` bool is also written for backward compatibility
    /// with older PS tray builds.
    /// </summary>
    void SetWelcomeSeen(bool seen);

    /// <summary>
    /// Forces cache invalidation. Next read pulls from disk.
    /// </summary>
    void Reload();

    /// <summary>
    /// Fires when the config file changes externally (FileSystemWatcher) or
    /// when SetValue is called internally. Args.KeyPath is null for
    /// whole-file changes (external writes); set to the dot-path for
    /// internal SetValue calls.
    /// </summary>
    event EventHandler<ConfigChangedEventArgs>? Changed;
}

public sealed class ConfigChangedEventArgs : EventArgs
{
    public string? KeyPath { get; }

    public ConfigChangedEventArgs(string? keyPath)
    {
        KeyPath = keyPath;
    }
}
