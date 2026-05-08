// Stage 7.9: ICustomizerLauncher. Spawns customize.exe (Stage 5 deliverable)
// from the install dir. The customize.exe is a WebView2 host that renders
// customize.html as a native window with no browser chrome. PS S10 source
// (tray.ps1:3533+ Show-OverlayCustomizer) is the reference; C# port
// matches: Process.Start customize.exe with WorkingDirectory set to the
// install dir so WebView2Loader.dll resolves correctly.

namespace MastersFM.Tray.Services;

public interface ICustomizerLauncher
{
    /// <summary>Spawns customize.exe. Returns true if spawn succeeded; false if customize.exe is missing or spawn threw.</summary>
    bool Launch();

    /// <summary>Terminates the spawned customize.exe if still running. Best-effort; silently ignored if not running.</summary>
    void Close();

    /// <summary>True if a customize.exe spawned by this launcher is still alive.</summary>
    bool IsRunning { get; }
}
