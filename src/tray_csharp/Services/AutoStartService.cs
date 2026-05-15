// Stage 7.9: AutoStartService. Creates/deletes `Master's FM.lnk` in the
// user's Startup folder via WshShell COM dynamic dispatch (NOT direct
// IShellLink interop; see absolute rule 11 of brief). Matches PS S7
// approach at tray.ps1:3409-3530.
//
// Brief Q1 default: WindowStyle = 7 (minimized) per PS S7 line 3427.
// Lnk filename: "Master's FM.lnk" (with apostrophe; matches PS S7 line 3412).
// Target: tray exe; WorkingDirectory: tray exe's parent dir; Description:
// "Master's FM - Now Playing Overlay" (matches PS S7 line 3428).
//
// Bitdefender risk note (R12): IShellLink CLSID has a history of being
// flagged. WshShell COM activation is wrapped in try/catch; failure logs
// + degrades gracefully (Enable returns without crash; IsEnabled returns
// best-effort).

using System.Runtime.InteropServices;

namespace MastersFM.Tray.Services;

public sealed class AutoStartService : IAutoStartService
{
    private const string LnkFileName = "Master's FM.lnk";
    private const string Description = "Master's FM - Now Playing Overlay";
    private const int WindowStyleMinimized = 7;
    private const string Component = "AutoStart";

    private readonly ILogger _logger;
    private readonly string _lnkPath;

    public event EventHandler<bool>? StateChanged;

    public bool IsEnabled => File.Exists(_lnkPath);

    public AutoStartService(ILogger logger)
    {
        _logger = logger;
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _lnkPath = Path.Combine(startup, LnkFileName);
        _logger.Log($"AutoStartService initialized; lnk={_lnkPath}; initial state={IsEnabled}", Component);
    }

    public void Enable()
    {
        try
        {
            var exePath = ResolveAutoStartTarget();
            if (string.IsNullOrEmpty(exePath))
            {
                _logger.LogErr("Enable: cannot resolve launcher target", null, Component);
                return;
            }

            var workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            // Prefer the standalone .ico when present (crisper than the
            // exe-embedded resource). Falls back to the exe itself.
            var icoPath = Path.Combine(workingDir, "MastersFM.ico");
            var iconLocation = File.Exists(icoPath) ? $"{icoPath},0" : $"{exePath},0";

            // WshShell COM via Type.GetTypeFromProgID + Activator + dynamic
            // dispatch. Avoids importing IShellLink/IPersistFile interop
            // interfaces (per absolute rule 11).
            var wshType = Type.GetTypeFromProgID("WScript.Shell");
            if (wshType == null)
            {
                _logger.LogErr("Enable: WScript.Shell ProgID not found", null, Component);
                return;
            }

            dynamic? wsh = null;
            dynamic? shortcut = null;
            try
            {
                wsh = Activator.CreateInstance(wshType);
                if (wsh == null)
                {
                    _logger.LogErr("Enable: Activator.CreateInstance returned null for WScript.Shell", null, Component);
                    return;
                }
                shortcut = wsh.CreateShortcut(_lnkPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = workingDir;
                shortcut.WindowStyle = WindowStyleMinimized;
                shortcut.Description = Description;
                shortcut.IconLocation = iconLocation;
                shortcut.Save();
                _logger.Log($"enabled; lnk={_lnkPath} -> target={exePath}", Component);
                StateChanged?.Invoke(this, true);
            }
            finally
            {
                // Release COM so the handle doesn't leak.
                if (shortcut != null)
                {
                    try { Marshal.FinalReleaseComObject(shortcut); } catch { }
                }
                if (wsh != null)
                {
                    try { Marshal.FinalReleaseComObject(wsh); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("Enable WshShell COM", ex, Component);
            // Don't crash. Bitdefender / SRP / etc. may block COM activation;
            // degrade gracefully. State is best-effort; IsEnabled returns whatever
            // the file system reports.
        }
    }

    public void Disable()
    {
        try
        {
            if (File.Exists(_lnkPath))
            {
                File.Delete(_lnkPath);
                _logger.Log($"disabled; lnk removed: {_lnkPath}", Component);
            }
            else
            {
                _logger.Log("disabled (lnk was not present)", Component);
            }
            StateChanged?.Invoke(this, false);
        }
        catch (Exception ex)
        {
            _logger.LogErr("Disable .lnk delete", ex, Component);
        }
    }

    public void Toggle()
    {
        if (IsEnabled) Disable();
        else Enable();
    }

    public void Reconcile()
    {
        try
        {
            if (!IsEnabled) return;          // nothing on disk to reconcile
            var expected = ResolveAutoStartTarget();
            if (string.IsNullOrEmpty(expected)) return;

            var existing = ReadLnkTarget(_lnkPath);
            if (string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log($"reconcile: target current ({expected})", Component);
                return;
            }

            _logger.Log($"reconcile: target drift; was '{existing ?? "(unreadable)"}', rewriting to '{expected}'", Component);
            Enable();                        // recreates the .lnk with the correct target
        }
        catch (Exception ex)
        {
            _logger.LogErr("Reconcile", ex, Component);
        }
    }

    // Prefer the launcher (MastersFM.exe) which spawns server + tray together.
    // Falls back to the currently-running process for dev / unusual layouts.
    private string ResolveAutoStartTarget()
    {
        var current    = Environment.ProcessPath ?? string.Empty;
        var workingDir = Path.GetDirectoryName(current) ?? string.Empty;
        var launcher   = Path.Combine(workingDir, "MastersFM.exe");
        if (File.Exists(launcher) && !string.Equals(launcher, current, StringComparison.OrdinalIgnoreCase))
            return launcher;
        return current;
    }

    private string? ReadLnkTarget(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        var wshType = Type.GetTypeFromProgID("WScript.Shell");
        if (wshType == null) return null;

        dynamic? wsh = null;
        dynamic? shortcut = null;
        try
        {
            wsh = Activator.CreateInstance(wshType);
            if (wsh == null) return null;
            shortcut = wsh.CreateShortcut(lnkPath);
            return (string?)shortcut.TargetPath;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut != null) { try { Marshal.FinalReleaseComObject(shortcut); } catch { } }
            if (wsh != null)      { try { Marshal.FinalReleaseComObject(wsh);      } catch { } }
        }
    }
}
