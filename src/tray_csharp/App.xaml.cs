// Stage 7.3: Application code-behind. ILogger + ITelemetry registered in DI.
// EarlyLog (static fallback) used for pre-DI bootstrap; injected ILogger
// used for everything after container build. DiagnosticHeartbeat started
// after MainWindow shown; stopped during OnExit.

using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MastersFM.Tray.Detectors;
using MastersFM.Tray.Dialogs;
using MastersFM.Tray.Diagnostics;
using MastersFM.Tray.Services;
using MastersFM.Tray.Update;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray;

public partial class App : Application
{
    private const string MutexName = @"Global\MastersFM_SingleInstance";

    private IServiceProvider? _services;
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private ILogger? _logger;
    private DiagnosticHeartbeat? _heartbeat;
    private IHeartbeatService? _heartbeatService; // INTERRUPT #3 STEP 5 (Issues 1+8)
    private IConfigService? _configService;
    private IUpdateCheckService? _updateService;
    private IDiscordToggleService? _discordService;
    private IAutoStartService? _autoStartService;
    private ICustomizerLauncher? _customizerLauncher;
    private IObsService? _obsService;
    private SmtcEventBridge? _smtcBridge;
    private DetectorOrchestrator? _detectorOrchestrator;
    private IDialogService? _dialogService;

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
        // Stage 7.5: Real Telemetry replaces NullTelemetry default. NullTelemetry
        // class remains in the codebase as a useful test-double but is no
        // longer the registered ITelemetry implementation.
        collection.AddSingleton<ITelemetry, Telemetry>();
        collection.AddSingleton<IConfigService, ConfigService>();
        collection.AddSingleton<SlowTickWatchdog>();
        collection.AddSingleton<DiagnosticHeartbeat>();
        collection.AddSingleton<IHeartbeatService, HeartbeatService>(); // INTERRUPT #3 STEP 5
        // Stage 7.2: Update-check stack.
        collection.AddSingleton<HttpClient>(_ =>
        {
            var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            hc.DefaultRequestHeaders.UserAgent.ParseAdd("MastersFM/14.0.0-rc.1 (UpdateCheck)");
            return hc;
        });
        collection.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        collection.AddSingleton<UpdateCheckViewModel>();
        collection.AddTransient<UpdateProgressWindow>();
        // Stage 7.9: Discord toggle + AutoStart + Customizer launcher.
        collection.AddSingleton<IDiscordToggleService, DiscordToggleService>();
        collection.AddSingleton<IAutoStartService, AutoStartService>();
        collection.AddSingleton<ICustomizerLauncher, CustomizerLauncher>();
        // Stage 7.8: OBS connection monitoring service.
        collection.AddSingleton<IObsService, ObsService>();
        // Stage 7.5: Detection layer (Option B+C hybrid).
        collection.AddSingleton<PlatformDetectorOptions>();
        collection.AddSingleton<ArtLruCache>();
        collection.AddSingleton<WebhookClient>();
        collection.AddSingleton<ITrackResolver, TrackResolver>();
        collection.AddSingleton<IPlatformDetector, OsuDetector>();
        collection.AddSingleton<IPlatformDetector, VlcHttpDetector>();
        collection.AddSingleton<IPlatformDetector, WmpLegacyDetector>();
        collection.AddSingleton<DetectorOrchestrator>();
        collection.AddSingleton<SmtcEventBridge>();
        collection.AddSingleton<NowPlayingViewModel>();
        collection.AddSingleton<MainWindow>();

        // Stage 7.7: Dialogs (Welcome / Audio / Platforms / SetupWizard / Error).
        // 6 ViewModels as singletons (state can persist across show/hide).
        // 5 Dialog Windows as transient (each show creates a fresh instance).
        // IDialogService as singleton (uses IServiceProvider to resolve).
        collection.AddSingleton<WelcomeViewModel>();
        collection.AddSingleton<AudioDeviceViewModel>();
        collection.AddSingleton<PlatformsViewModel>();
        collection.AddSingleton<SetupWizardViewModel>();
        collection.AddSingleton<AboutViewModel>();
        collection.AddSingleton<ErrorDialogViewModel>();
        collection.AddTransient<WelcomeWindow>();
        collection.AddTransient<AudioDeviceWindow>();
        collection.AddTransient<PlatformsWindow>();
        collection.AddTransient<SetupWizardWindow>();
        collection.AddTransient<ErrorDialogWindow>();
        collection.AddSingleton<IDialogService, DialogService>();
        // Stage 7.6: TrayMenuViewModel singleton — data-context for Surface 03 ContextMenu.
        // Registered after all dependencies (NowPlayingViewModel, IDialogService,
        // IDiscordToggleService, IAutoStartService, IUpdateCheckService, ICustomizerLauncher).
        // DI resolves singletons lazily at first GetRequiredService call so registration
        // order does not matter for resolution, only for code readability.
        collection.AddSingleton<TrayMenuViewModel>();

        _services = collection.BuildServiceProvider();

        _logger = _services.GetRequiredService<ILogger>();
        _logger.Log("DI container built (ILogger, ITelemetry, IConfigService, SlowTickWatchdog, DiagnosticHeartbeat, HttpClient, IUpdateCheckService, UpdateCheckViewModel, UpdateProgressWindow, IDiscordToggleService, IAutoStartService, ICustomizerLauncher, IObsService, MainWindow registered)", "Bootstrap");

        // -- Stage 7.2: R6 closure self-test (11 SemVer/regex synthetic cases) --
        var semVerFailures = SemVerComparer.RunSelfTest((level, msg) =>
        {
            if (level == "TEST") _logger.Log(msg, "Update");
        });
        if (semVerFailures > 0)
        {
            _logger.LogErr($"R6 closure FAILED: {semVerFailures} SemVerComparer test cases failed", null, "Update");
            // Brief absolute rule 5: HALT recommended on R6 failure. We log
            // hard but don't crash the app -- the skeleton stays alive so the
            // operator can see the failure in the log. Future strict mode
            // could exit here.
        }

#if DEBUG
        // -- Stage 7.6 STEP 3: P99 self-test (debug builds only) --
        try { Telemetry.SelfTestP99(); _logger.Log("SnapshotTimingsP99 self-test PASS", "Bootstrap"); }
        catch (Exception ex) { _logger.LogErr("SnapshotTimingsP99 self-test FAIL", ex, "Bootstrap"); }
#endif

        // -- Resolve ConfigService and read welcome-seen flag (the one
        // behaviorally-active config in 7.4; full UI handler in 7.7) --
        _configService = _services.GetRequiredService<IConfigService>();
        var welcomeSeen = _configService.GetWelcomeSeen();
        _logger.Log($"welcome-seen={welcomeSeen}", "Bootstrap");
        _configService.Changed += OnConfigChanged;

        // -- Stage 7.2: schedule startup update check if last check is older
        // than 6 hours (per brief STEP 4.9 cadence) --
        _updateService = _services.GetRequiredService<IUpdateCheckService>();
        var lastChecked = _updateService.LastCheckedUtc;
        var sinceLast = lastChecked.HasValue ? (DateTime.UtcNow - lastChecked.Value) : TimeSpan.MaxValue;
        if (sinceLast >= TimeSpan.FromHours(6))
        {
            var lastDisplay = lastChecked.HasValue ? lastChecked.Value.ToString("o") : "(never)";
            _logger.Log($"startup check scheduled (last={lastDisplay})", "Update");
            // fire-and-forget; runs in background
            _ = Task.Run(async () =>
            {
                try { await _updateService.CheckNowAsync(); }
                catch (Exception ex) { _logger.LogErr("startup check fire-and-forget", ex, "Update"); }
            });
        }
        else
        {
            _logger.Log($"startup check skipped; last check {(int)sinceLast.TotalMinutes} min ago (threshold 6h)", "Update");
        }

        // -- Stage 7.9: Resolve Discord/AutoStart/Customizer services and log
        // initial states. Tray menu UI integration is 7.6's job; 7.9 just
        // initializes them so they're ready for that wiring. --
        _discordService = _services.GetRequiredService<IDiscordToggleService>();
        _autoStartService = _services.GetRequiredService<IAutoStartService>();
        _customizerLauncher = _services.GetRequiredService<ICustomizerLauncher>();
        _logger.Log($"initial state={_discordService.IsEnabled}", "Discord");
        _logger.Log($"initial state={_autoStartService.IsEnabled}", "AutoStart");
        // Customizer launcher: dry-run path resolution check (no spawn during
        // smoke per brief absolute rule).
        if (_customizerLauncher is CustomizerLauncher concrete)
        {
            var resolved = concrete.ResolveCustomizerExe();
            _logger.Log($"customize.exe path resolved to: {resolved ?? "(null)"} (exists={(resolved != null && File.Exists(resolved))})", "Customizer");
        }

        // -- Stage 7.8: OBS service startup --
        _obsService = _services.GetRequiredService<IObsService>();
        _obsService.Start();

        // Stage 7.8B STEP 5: 5s startup auto-add (mirrors tray.ps1:5162-5177)
        var obsForAutoAdd = _obsService;
        var logForAutoAdd = _logger;
        var autoAddEnabled = _configService?.GetValue<bool>("obs.auto_add", true) ?? true;
        if (obsForAutoAdd.IsEnabled && autoAddEnabled)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                try
                {
                    var result = await obsForAutoAdd.AddBrowserSourceAsync();
                    if (!result.Success)
                        logForAutoAdd.LogWarn($"OBS startup auto-add failed: {result.ErrorMessage}", "Bootstrap");
                    else
                        logForAutoAdd.Log($"OBS startup auto-add ok via {result.Method}", "Bootstrap");
                }
                catch (Exception ex) { logForAutoAdd.LogErr("OBS startup auto-add", ex, "Bootstrap"); }
            });
        }

        // -- Stage 7.5: Detection layer startup --
        // Resolve NowPlayingViewModel so it subscribes to ITrackResolver.TrackChanged
        // before any TrackUpdate event fires.
        _ = _services.GetRequiredService<NowPlayingViewModel>();
        _smtcBridge = _services.GetRequiredService<SmtcEventBridge>();
        _detectorOrchestrator = _services.GetRequiredService<DetectorOrchestrator>();
        _smtcBridge.Start();
        _detectorOrchestrator.Start();
        _logger.Log("all arms started; SMTC events live (or inactive if WinRT not projected); gap-fillers polling at 1s cadence", "Detect");

        // -- Resolve and show the hidden MainWindow (host for the TaskbarIcon) --
        var mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        _logger.Log("MainWindow shown", "Bootstrap");

        // -- Start the diagnostic heartbeat (60s cadence) --
        _heartbeat = _services.GetRequiredService<DiagnosticHeartbeat>();
        _heartbeat.Start();

        // -- INTERRUPT #3 STEP 5 (Issues 1+8): start position heartbeat (2s cadence) --
        _heartbeatService = _services.GetRequiredService<IHeartbeatService>();
        _heartbeatService.Start();

        // -- Stage 7.7: First-run check + dialog service ----------------
        _dialogService = _services.GetRequiredService<IDialogService>();
        _logger.Log("DialogService initialized; 5 dialogs registered (Welcome / Audio / Platforms / SetupWizard / Error)", "Bootstrap");
        ScheduleFirstRunCheck();

        _logger.Log("Application.OnStartup completed", "Bootstrap");

        // Stage 7.6 STEP 2: optional dialog smoke mode (--smoke-dialogs arg).
        // Runs dialog cycle smoke test, writes results, then exits. Only active
        // when explicitly requested; has no effect on normal tray launch.
        var args = Environment.GetCommandLineArgs();
        bool smokeMode = args.Any(a => string.Equals(a, "--smoke-dialogs", StringComparison.OrdinalIgnoreCase));
        if (smokeMode && _services != null && _logger != null)
        {
            var smokeLabel = args.Any(a => a.Contains("REGRESSION", StringComparison.OrdinalIgnoreCase))
                ? "REGRESSION" : "BASELINE";
            var smokeOutput = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MastersFM",
                $"dialog_smoke_{smokeLabel}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            _logger.Log($"SMOKE MODE: label={smokeLabel} output={smokeOutput}", "Bootstrap");
            var runner = new DialogSmokeRunner(_services, _logger);
            // Fire-and-forget on dispatcher background; app stays alive until
            // RunAsync calls Application.Current.Shutdown(0).
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(async () =>
                {
                    try { await runner.RunAsync(smokeOutput); }
                    catch (Exception ex) { _logger?.LogErr("smoke runner", ex, "Bootstrap"); }
                    finally { Application.Current.Shutdown(0); }
                }));
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// Stage 7.7 first-run check. Reads welcome_seen + welcome_seen_version
    /// from config. If not seen for the current version, schedule SetupWizard
    /// show after 200ms delay (let MainWindow / TaskbarIcon settle first).
    /// Per Q-MOCK-06a=A2, all-platforms-ON default is enforced BEFORE the
    /// wizard is shown.
    /// </summary>
    private void ScheduleFirstRunCheck()
    {
        try
        {
            if (_configService == null) return;
            bool seen = _configService.GetWelcomeSeen();
            if (seen)
            {
                _logger?.Log("first-run check: welcome_seen=true; skipping setup wizard", "Bootstrap");
                return;
            }

            // Enforce all-platforms-ON default before the wizard is shown
            // (per brief absolute rule 15).
            EnforceAllPlatformsOnDefault();

            // Defer 200ms so MainWindow / TaskbarIcon settle (per OPEN
            // QUESTION 7 default).
            var delay = TimeSpan.FromMilliseconds(200);
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try
                {
                    if (_dialogService == null) return;
                    var completed = await _dialogService.ShowSetupWizardAsync();
                    _logger?.Log("setup wizard returned completed=" + completed, "Bootstrap");
                }
                catch (Exception ex)
                {
                    _logger?.LogErr("setup wizard show", ex, "Bootstrap");
                }
            };
            timer.Start();
            _logger?.Log("first-run check: scheduling setup wizard in " + delay.TotalMilliseconds + "ms", "Bootstrap");
        }
        catch (Exception ex)
        {
            _logger?.LogErr("first-run check", ex, "Bootstrap");
        }
    }

    private void EnforceAllPlatformsOnDefault()
    {
        if (_configService == null) return;
        // Stage 7.7 absolute rule 15: per Q-MOCK-06a=A2, all-platforms-ON
        // default on first-run. Write all 8 platform toggles to true ONLY
        // if no value is currently set; respect any user override that
        // somehow already exists.
        string[] platformIds = new[] {
            "soundcloud", "spotify", "browser", "smtcGeneric",
            "win11Media", "osu", "vlc", "wmpLegacy"
        };
        foreach (var pid in platformIds)
        {
            try
            {
                var key = "platforms." + pid + ".enabled";
                var current = _configService.GetValue<bool?>(key);
                if (current == null)
                {
                    _configService.SetValue(key, true);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarn("platform default enforcement " + pid + ": " + ex.Message, "Bootstrap");
            }
        }
        _logger?.Log("all-platforms-ON default enforced (8 platforms; respects existing user values)", "Bootstrap");
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

        // INTERRUPT #3 STEP 5: stop position heartbeat before container disposal
        try { _heartbeatService?.Stop(); }
        catch (Exception ex) { _logger?.LogErr("HeartbeatService.Stop", ex, "Bootstrap"); }

        // Stage 7.5: stop detection arms before container disposal
        try { _detectorOrchestrator?.Stop(); } catch (Exception ex) { _logger?.LogErr("DetectorOrchestrator.Stop", ex, "Bootstrap"); }
        try { _smtcBridge?.Stop(); } catch (Exception ex) { _logger?.LogErr("SmtcEventBridge.Stop", ex, "Bootstrap"); }
        // Stage 7.8: stop OBS service
        try { _obsService?.Stop(); } catch (Exception ex) { _logger?.LogErr("ObsService.Stop", ex, "Bootstrap"); }

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
        // Stage 7.7: surface to user via Error dialog (Surface 11). Wrap in
        // try/catch so a dialog show failure doesn't recurse.
        try
        {
            _dialogService?.ShowErrorAsync(
                "Master's FM ran into a problem",
                e.Exception?.Message ?? "An unexpected error occurred. Master's FM is still running.",
                e.Exception);
        }
        catch (Exception ex)
        {
            _logger?.LogErr("error dialog show after dispatcher exception", ex, "Bootstrap");
        }
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
