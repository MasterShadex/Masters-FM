// Stage 7.3: Application code-behind. ILogger + ITelemetry registered in DI.
// EarlyLog (static fallback) used for pre-DI bootstrap; injected ILogger
// used for everything after container build. DiagnosticHeartbeat started
// after MainWindow shown; stopped during OnExit.

using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MastersFM.Tray.Services;

namespace MastersFM.Tray;

public partial class App : Application
{
    private const string MutexName = @"Global\MastersFM_SingleInstance";

    private IServiceProvider? _services;
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private ILogger? _logger;
    private DiagnosticHeartbeat? _heartbeat;
    private IConfigService? _configService;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Pre-DI bootstrap logging via static fallback (Logger.EarlyLog).
        Logger.EarlyLog("Application.OnStartup begin");
        Logger.EarlyLog($"PID={Environment.ProcessId} OS={Environment.OSVersion.VersionString} CLR={Environment.Version}");
        Logger.EarlyLog($"BaseDir={AppContext.BaseDirectory}");

        // -- Single-instance mutex (shared with PS tray and prior tray skeletons) --
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
            Logger.EarlyLogErr("Single-instance mutex acquisition", ex);
            Shutdown(0);
            return;
        }

        if (!gotMutex)
        {
            Logger.EarlyLog("Single-instance mutex held by another tray (PS tray or C# tray); exiting cleanly with code 0.");
            Shutdown(0);
            return;
        }

        _ownsMutex = true;
        Logger.EarlyLog("Single-instance mutex acquired");

        // -- AUMID via tray_native.dll --
        try
        {
            MFM_Shell.SetCurrentProcessExplicitAppUserModelID("MastersFM.App");
            Logger.EarlyLog("AUMID set via MFM_Shell (tray_native.dll)");
        }
        catch (Exception ex)
        {
            Logger.EarlyLogErr("AUMID set via MFM_Shell", ex);
        }

        // -- Exception hooks (WPF analogues) --
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Logger.EarlyLog("Exception hooks installed (AppDomain + Dispatcher + TaskScheduler)");

        // -- DI container build --
        var collection = new ServiceCollection();
        collection.AddSingleton<ILogger, Logger>();
        collection.AddSingleton<ITelemetry, NullTelemetry>();
        collection.AddSingleton<IConfigService, ConfigService>();
        collection.AddSingleton<SlowTickWatchdog>();
        collection.AddSingleton<DiagnosticHeartbeat>();
        collection.AddSingleton<MainWindow>();
        _services = collection.BuildServiceProvider();

        _logger = _services.GetRequiredService<ILogger>();
        _logger.Log("DI container built (ILogger, ITelemetry=NullTelemetry, IConfigService, SlowTickWatchdog, DiagnosticHeartbeat, MainWindow registered)", "Bootstrap");

        // -- Resolve ConfigService and read welcome-seen flag (the one
        // behaviorally-active config in 7.4; full UI handler in 7.7) --
        _configService = _services.GetRequiredService<IConfigService>();
        var welcomeSeen = _configService.GetWelcomeSeen();
        _logger.Log($"welcome-seen={welcomeSeen}", "Bootstrap");
        _configService.Changed += OnConfigChanged;

        // -- Resolve and show the hidden MainWindow (host for the TaskbarIcon) --
        var mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        _logger.Log("MainWindow shown", "Bootstrap");

        // -- Start the diagnostic heartbeat (60s cadence) --
        _heartbeat = _services.GetRequiredService<DiagnosticHeartbeat>();
        _heartbeat.Start();

        _logger.Log("Application.OnStartup completed", "Bootstrap");

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Log("Application.OnExit begin", "Bootstrap");

        try
        {
            _heartbeat?.Stop();
        }
        catch (Exception ex)
        {
            _logger?.LogErr("DiagnosticHeartbeat.Stop", ex, "Bootstrap");
        }

        try
        {
            if (_services is IDisposable disposable)
            {
                disposable.Dispose();
                _logger?.Log("DI container disposed", "Bootstrap");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogErr("DI container disposal", ex, "Bootstrap");
        }

        try
        {
            if (_ownsMutex && _singleInstanceMutex != null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _logger?.Log("Single-instance mutex released", "Bootstrap");
            }
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogErr("Single-instance mutex release", ex, "Bootstrap");
        }

        _logger?.Log($"Application.OnExit completed; exit code = {e.ApplicationExitCode}", "Bootstrap");

        base.OnExit(e);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            if (_logger != null)
            {
                _logger.LogErr("AppDomain.UnhandledException (terminating=" + e.IsTerminating + ")", ex, "Bootstrap");
            }
            else
            {
                Logger.EarlyLogErr("AppDomain.UnhandledException (terminating=" + e.IsTerminating + ")", ex);
            }
        }
        else
        {
            var msg = $"AppDomain.UnhandledException with non-Exception object: {e.ExceptionObject}";
            if (_logger != null) { _logger.Log(msg, "Bootstrap"); } else { Logger.EarlyLog(msg); }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogErr("Dispatcher.UnhandledException", e.Exception, "Bootstrap");
        // Mark as handled to prevent the dispatcher from terminating; one
        // badness is preferable to the whole tray dying.
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogErr("TaskScheduler.UnobservedTaskException", e.Exception, "Bootstrap");
        e.SetObserved();
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        // 7.4 visibility-only handler; sub-stages add real handlers (e.g.,
        // 7.5 detection re-toggles platforms when platforms.* changes).
        _logger?.Log($"config changed: keyPath={e.KeyPath ?? "(whole-file)"}", "Bootstrap");
    }
}
