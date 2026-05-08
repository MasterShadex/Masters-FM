// Stage 7.1B: Application code-behind. Replaces Stage 7.1's Program.cs
// (WinForms entry). Owns single-instance mutex, AUMID, exception hooks,
// DI container lifetime. WPF dispatcher manages the message pump; no
// explicit Application.Run call needed (App.xaml's <Application> root
// drives it).

using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace MastersFM.Tray;

public partial class App : Application
{
    private const string MutexName = @"Global\MastersFM_SingleInstance";

    private IServiceProvider? _services;
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        Logger.EarlyLog("Application.OnStartup begin");
        Logger.EarlyLog($"PID={Environment.ProcessId} OS={Environment.OSVersion.VersionString} CLR={Environment.Version}");
        Logger.EarlyLog($"BaseDir={AppContext.BaseDirectory}");

        // -- Single-instance mutex (shared with PS tray and Stage 7.1 WinForms tray) --
        // Same mutex name as tray.ps1:62. AbandonedMutexException is treated as
        // "we got the mutex" -- the previous owner died without releasing it.
        bool gotMutex = false;
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, name: MutexName);
            try
            {
                gotMutex = _singleInstanceMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                gotMutex = true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogErr("Single-instance mutex acquisition", ex);
            // If we cannot create/acquire the mutex object at all, exit cleanly
            // to avoid running a second tray side-by-side.
            Shutdown(0);
            return;
        }

        if (!gotMutex)
        {
            Logger.Log("Single-instance mutex held by another tray (PS tray or C# tray); exiting cleanly with code 0.");
            Shutdown(0);
            return;
        }

        _ownsMutex = true;
        Logger.Log("Single-instance mutex acquired");

        // -- AUMID via tray_native.dll (preserved from Stage 7.1 / Q1 default) --
        try
        {
            MFM_Shell.SetCurrentProcessExplicitAppUserModelID("MastersFM.App");
            Logger.Log("AUMID set via MFM_Shell (tray_native.dll)");
        }
        catch (Exception ex)
        {
            Logger.LogErr("AUMID set via MFM_Shell", ex);
        }

        // -- Exception hooks (WPF analogues of WinForms patterns) --
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Logger.Log("Exception hooks installed (AppDomain + Dispatcher + TaskScheduler)");

        // -- DI container --
        var collection = new ServiceCollection();
        collection.AddSingleton<MainWindow>();
        // Future sub-stages (7.3 logging proper, 7.5 detection, etc.) register
        // additional services here.
        _services = collection.BuildServiceProvider();
        Logger.Log("DI container built");

        // -- Resolve and show the hidden MainWindow (host for the TaskbarIcon) --
        var mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        // Window is Visibility=Hidden + off-screen; Show() registers it with the
        // dispatcher so it can host the TaskbarIcon and receive messages.

        Logger.Log("Application.OnStartup completed; MainWindow shown");

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Log("Application.OnExit begin");

        try
        {
            if (_services is IDisposable disposable)
            {
                disposable.Dispose();
                Logger.Log("DI container disposed");
            }
        }
        catch (Exception ex)
        {
            Logger.LogErr("DI container disposal", ex);
        }

        try
        {
            if (_ownsMutex && _singleInstanceMutex != null)
            {
                _singleInstanceMutex.ReleaseMutex();
                Logger.Log("Single-instance mutex released");
            }
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogErr("Single-instance mutex release", ex);
        }

        Logger.Log($"Application.OnExit completed; exit code = {e.ApplicationExitCode}");

        base.OnExit(e);
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Logger.LogErr("AppDomain.UnhandledException (terminating=" + e.IsTerminating + ")", ex);
        }
        else
        {
            Logger.Log($"AppDomain.UnhandledException with non-Exception object: {e.ExceptionObject}");
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.LogErr("Dispatcher.UnhandledException", e.Exception);
        // Mark as handled to prevent the dispatcher from terminating; the user
        // should see one badness rather than the whole tray dying.
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.LogErr("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
