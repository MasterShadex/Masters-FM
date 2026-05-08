// Stage 7.9: IAutoStartService. Manages the `Master's FM.lnk` shortcut in
// the user's Startup folder via WshShell COM dynamic dispatch. Matches PS
// S7 approach (tray.ps1:3409-3530) -- proven, no need to redesign.

namespace MastersFM.Tray.Services;

public interface IAutoStartService
{
    /// <summary>True when `Master's FM.lnk` exists in the user's Startup folder.</summary>
    bool IsEnabled { get; }

    /// <summary>Creates the .lnk via WshShell COM. Logs success or failure; does not crash on COM errors.</summary>
    void Enable();

    /// <summary>Deletes the .lnk if present. Logs.</summary>
    void Disable();

    /// <summary>Convenience: flips current state.</summary>
    void Toggle();

    /// <summary>Fires after Enable/Disable. Tray menu in 7.6 will subscribe.</summary>
    event EventHandler<bool>? StateChanged;
}
