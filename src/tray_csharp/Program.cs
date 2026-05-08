// Stage 7.1 skeleton -- entry point.
// Bootstrap order mirrors tray.ps1 lines 1-249 (V14_S7_P1_TRAY_INVENTORY.md S1):
//   1. Logger init (so any subsequent crash is captured)
//   2. Single-instance mutex
//   3. AUMID via MFM_Shell from tray_native.dll
//   4. AppDomain + Application exception hooks
//   5. WinForms HighDpi + visual styles
//   6. Run TrayApp via Application.Run
//
// The single-instance mutex name "Global\MastersFM_SingleInstance" is shared with the
// legacy PS tray. During Stage 7 dev parallel period this means PS tray and C# tray
// cannot run simultaneously, which is the correct behavior: only one tray runs at a
// time regardless of build.

using System.Threading;
using System.Windows.Forms;

namespace MastersFM.Tray;

internal static class Program
{
    private const string MutexName = @"Global\MastersFM_SingleInstance";

    [STAThread]
    private static int Main()
    {
        Logger.EarlyLog("MastersFM_Tray_v14 starting (Stage 7.1 skeleton)");
        Logger.EarlyLog($"PID={Environment.ProcessId} OS={Environment.OSVersion} CLR={Environment.Version}");
        Logger.EarlyLog($"BaseDir={AppContext.BaseDirectory}");

        Mutex? mutex = null;
        bool gotMutex = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name: MutexName);
            try
            {
                gotMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Previous owner exited without releasing. We own it now.
                gotMutex = true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogErr("mutex acquisition", ex);
            return 1;
        }

        if (!gotMutex)
        {
            Logger.Log("Single-instance mutex held by another tray (PS tray or C# tray); exiting cleanly with code 0.");
            try { mutex?.Dispose(); } catch { }
            return 0;
        }

        // AUMID via tray_native.dll's MFM_Shell. Identical behavior to PS tray bootstrap
        // (tray.ps1:35). Non-fatal if it fails -- the tray still runs without grouped
        // taskbar identity.
        try
        {
            MFM_Shell.SetCurrentProcessExplicitAppUserModelID("MastersFM.App");
            Logger.Log("AUMID set via MFM_Shell (tray_native.dll)");
        }
        catch (Exception ex)
        {
            Logger.LogErr("AUMID set via MFM_Shell", ex);
        }

        // Exception hooks installed BEFORE Application.Run so subsequent crashes are captured.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                Logger.Log(string.Format(
                    "!! AppDomain UnhandledException: {0}: {1}{2}{3}",
                    ex?.GetType().FullName ?? "(unknown)",
                    ex?.Message ?? "(null)",
                    Environment.NewLine,
                    ex?.StackTrace ?? "(no stack)"));
            }
            catch { }
        };
        Application.ThreadException += (_, e) =>
        {
            try
            {
                Logger.Log(string.Format(
                    "!! Application.ThreadException: {0}: {1}{2}{3}",
                    e.Exception.GetType().FullName,
                    e.Exception.Message,
                    Environment.NewLine,
                    e.Exception.StackTrace ?? "(no stack)"));
            }
            catch { }
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Logger.Log("Exception hooks installed (AppDomain + WinForms ThreadException)");

        // WinForms application configuration.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        int exitCode;
        try
        {
            using var app = new TrayApp();
            Logger.Log("TrayApp created; entering Application.Run");
            Application.Run(app);
            Logger.Log("Application.Run returned cleanly; exiting code 0");
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Logger.LogErr("Application.Run", ex);
            exitCode = 1;
        }
        finally
        {
            try { mutex?.ReleaseMutex(); } catch { }
            try { mutex?.Dispose(); } catch { }
        }

        return exitCode;
    }
}
