// Stage 7.9: IDiscordToggleService. Server-config flag write; server.exe
// (already on .NET 8) reads the flag on its config-poll cycle. The tray
// menu wires the toggle UI in 7.6; 7.9 ships the toggle service interface
// and implementation. Tray and server are decoupled here -- toggle is just
// a config write; no IPC.

namespace MastersFM.Tray.Services;

public interface IDiscordToggleService
{
    /// <summary>True when Discord Rich Presence is enabled in config (key `discord_rpc.enabled`). Default true when key missing (matches PS S9 tray.ps1:4391-4401).</summary>
    bool IsEnabled { get; }

    /// <summary>Writes config flag = true; logs; fires StateChanged(true).</summary>
    void Enable();

    /// <summary>Writes config flag = false; logs; fires StateChanged(false).</summary>
    void Disable();

    /// <summary>Convenience: flips current state.</summary>
    void Toggle();

    /// <summary>Fires after Enable/Disable AND on external config changes (FileSystemWatcher) when discord_rpc.enabled is mutated. Tray menu in 7.6 will subscribe.</summary>
    event EventHandler<bool>? StateChanged;
}
