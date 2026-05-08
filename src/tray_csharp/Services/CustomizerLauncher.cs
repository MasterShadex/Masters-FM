// Stage 7.9: CustomizerLauncher. ProcessStartInfo for customize.exe (NOT
// ShellExecute; per absolute rule 13 of brief). The customize.exe expects
// no URL parameters -- it's a self-contained WebView2 host that reads its
// own config (PS S10 line 3544 calls Start-Process with no args).
//
// Resolves customize.exe path from Environment.ProcessPath's parent dir
// (next to the tray exe). Working directory is set to the install dir so
// WebView2Loader.dll + managed WebView2 DLLs resolve correctly.

using System.Diagnostics;

namespace MastersFM.Tray.Services;

public sealed class CustomizerLauncher : ICustomizerLauncher
{
    private const string CustomizerExeName = "customize.exe";
    private const string Component = "Customizer";

    private readonly ILogger _logger;
    private readonly IConfigService _config;
    private Process? _customizerProcess;

    public bool IsRunning => _customizerProcess != null && !_customizerProcess.HasExited;

    public CustomizerLauncher(ILogger logger, IConfigService config)
    {
        _logger = logger;
        _config = config;
        _logger.Log("launcher ready", Component);
    }

    public bool Launch()
    {
        try
        {
            var exePath = ResolveCustomizerExe();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                _logger.LogWarn($"customize.exe not found at expected path: '{exePath}'", Component);
                return false;
            }

            var workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            _customizerProcess = Process.Start(psi);
            var pid = _customizerProcess?.Id ?? -1;
            _logger.Log($"launched; pid={pid} exe={exePath}", Component);
            return _customizerProcess != null;
        }
        catch (Exception ex)
        {
            _logger.LogErr("Launch customize.exe", ex, Component);
            return false;
        }
    }

    public void Close()
    {
        try
        {
            if (_customizerProcess == null) return;
            if (!_customizerProcess.HasExited)
            {
                if (!_customizerProcess.CloseMainWindow())
                {
                    // Graceful close failed; force kill after brief grace period
                    if (!_customizerProcess.WaitForExit(2000))
                    {
                        _customizerProcess.Kill();
                        _logger.Log($"force-killed pid={_customizerProcess.Id}", Component);
                    }
                    else
                    {
                        _logger.Log($"closed gracefully pid={_customizerProcess.Id}", Component);
                    }
                }
                else
                {
                    _logger.Log($"closed pid={_customizerProcess.Id}", Component);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("Close customize.exe", ex, Component);
        }
        finally
        {
            try { _customizerProcess?.Dispose(); } catch { }
            _customizerProcess = null;
        }
    }

    /// <summary>
    /// Internal helper exposed for dry-run smoke testing per brief STEP 6.5.
    /// Returns the resolved customize.exe path without actually spawning the
    /// process. Smoke can verify the path resolution logic is correct without
    /// opening a visible window during automated testing.
    /// </summary>
    public string? ResolveCustomizerExe()
    {
        var trayExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(trayExe)) return null;
        var dir = Path.GetDirectoryName(trayExe);
        if (string.IsNullOrEmpty(dir)) return null;
        return Path.Combine(dir, CustomizerExeName);
    }
}
