// Stage 7.3: Application code-behind. ILogger + ITelemetry registered in DI.
// EarlyLog (static fallback) used for pre-DI bootstrap; injected ILogger
// used for everything after container build. DiagnosticHeartbeat started
// after MainWindow shown; stopped during OnExit.

using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
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

    // Stage 7.12 Batch B Phase D #2: lift Windows system timer resolution
    // from the default 15.6 ms down to 1 ms.  Without this, `Task.Delay(1)`
    // in the SMTC bridge (Phase D #1) and the 50 ms heartbeat (Phase D #3)
    // would actually fire every 15.6 ms — the SMTC drain would be no better
    // than before.  Modern media apps (Discord, Spotify, web browsers) all
    // call timeBeginPeriod(1) for the same reason.  Cost is ~0.05 % extra
    // OS interrupts; we already idle at 0.1 % CPU.
    //
    // Always paired with timeEndPeriod(1) in OnExit so we don't leave the
    // system timer raised after process death (Win10+ auto-cleans on
    // process exit anyway, but be explicit).
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);
    private bool _highResTimerSet;

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

    // -- Stage 7.7B: Reduced-motion detection (STEP 2 / S2.1) --
    // True when Windows "Animate controls and elements inside windows" is off.
    // Checked once at startup; dialogs read this to skip transitions that would
    // disturb accessibility users. Duration resources are overridden to zero
    // in OnStartup when this is true.
    public static bool IsReducedMotion { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Stage 7.12 Batch B Phase D #2: high-resolution OS timer.  MUST be
        // the first thing in OnStartup so all downstream timer-based code
        // (DispatcherTimer, Task.Delay, Thread.Sleep) gets 1 ms granularity.
        try
        {
            if (TimeBeginPeriod(1) == 0) _highResTimerSet = true;
        }
        catch { /* winmm.dll unavailable — fall back to default 15.6 ms */ }

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

        // -- Stage 7.7B: Reduced-motion detection (STEP 2 / S2.1) --
        IsReducedMotion = DetectReducedMotion();
        if (IsReducedMotion)
        {
            Resources["StandardDuration"]  = new Duration(TimeSpan.Zero);
            Resources["FastDuration"]      = new Duration(TimeSpan.Zero);
            Resources["SlowDuration"]      = new Duration(TimeSpan.Zero);
            Resources["PressDuration"]     = new Duration(TimeSpan.Zero);
            Resources["AccentBarDuration"] = new Duration(TimeSpan.Zero);
            Logger.EarlyLog("ReducedMotion=true: animation durations overridden to instant");
        }
        else
        {
            Logger.EarlyLog("ReducedMotion=false: animation durations use design-system defaults");
        }

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
            hc.DefaultRequestHeaders.UserAgent.ParseAdd("MastersFM/14.0.0 (UpdateCheck)");
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
        // Phase O: bridge from the tray's device dialog → audio_spectrum's
        // /set-device HTTP endpoint + config keys it reads on bootstrap.
        collection.AddSingleton<AudioBackendBridge>();
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

        // Stage 7.12 Batch A: repair stale shortcut targets from earlier builds
        // that pointed the .lnk at the tray host instead of MastersFM.exe.
        _autoStartService.Reconcile();

        // Default autostart ON for v14.0.0+. Uses a fresh flag key
        // (autostart_defaulted_v14_0_0) so it re-applies once for every user,
        // including those who upgraded from rc3 and never got a working
        // shortcut on the rc3-keyed generation.  Flag written once;
        // subsequent runs honour the user's explicit choice.
        // Stage 7.18 Task A: bumped from autostart_defaulted_v14rc3 to
        // autostart_defaulted_v14_0_0 because the rc3 generation flag
        // short-circuited the default-on logic on the v14.0.0 install for
        // operators upgrading from rc3 == fresh installs came up with
        // Start-on-login UNCHECKED.
        try
        {
            var defaulted = _configService?.GetValue<bool>("autostart_defaulted_v14_0_0", false) ?? false;
            if (!defaulted)
            {
                if (!_autoStartService.IsEnabled)
                {
                    _autoStartService.Enable();
                    _logger.Log("AutoStart defaulted ON (v14.0.0 first run)", "AutoStart");
                }
                _configService?.SetValue("autostart_defaulted_v14_0_0", true);
            }
        }
        catch (Exception ex) { _logger.LogErr("AutoStart default-on", ex, "AutoStart"); }

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

        // Stage 7.8D: 5s direct-add removed. TrayMenuViewModel.ReconcileAsync
        // (60s timer, fires immediately on first tick) is the single source of
        // truth for intent-vs-reality reconciliation. obs.enabled is never read
        // here; obs.intent is read by ReconcileAsync on its first tick.

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

        // -- Stage 7.7B: WelcomeWindow first-run hero (STEP 3 / S3.1) --
        // Show WelcomeWindow once on first launch. Uses a separate flag
        // (app.first_run_shown) so it is decoupled from the old welcome_seen
        // gate. DialogResult semantics:
        //   true  -> Get Started -> ScheduleFirstRunCheck -> shows SetupWizard
        //   false -> Skip Setup  -> mark welcome_seen; wizard does not auto-show
        bool firstRunShown = _configService?.GetValue<bool>("app.first_run_shown", false) ?? false;
        _logger.Log($"app.first_run_shown={firstRunShown}", "Bootstrap");
        if (!firstRunShown)
        {
            _logger.Log("first_run_shown=false: showing Welcome hero", "Bootstrap");
            var welcome = _services.GetRequiredService<WelcomeWindow>();
            bool? getStarted = welcome.ShowDialog();
            _configService?.SetValue("app.first_run_shown", true);
            _logger.Log($"Welcome closed (getStarted={getStarted})", "Bootstrap");
            if (getStarted == true)
            {
                // User chose Get Started: welcome_seen is still false,
                // so ScheduleFirstRunCheck will show the Setup Wizard.
                ScheduleFirstRunCheck();
            }
            else
            {
                // User chose Skip Setup (or closed via X): suppress wizard.
                _configService?.SetWelcomeSeen(true);
                _logger.Log("Skip Setup: welcome_seen marked true, wizard suppressed", "Bootstrap");
            }
        }
        else
        {
            // Subsequent launches: existing check handles wizard re-show if needed.
            ScheduleFirstRunCheck();
        }

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

        // Stage 7.12 Batch B Phase D #2: undo high-resolution timer.  Windows 10+
        // auto-cleans on process exit, but be explicit so we don't leave the
        // global timer raised if the process somehow lingers.
        if (_highResTimerSet)
        {
            try { TimeEndPeriod(1); } catch { }
            _highResTimerSet = false;
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

    /// <summary>
    /// Stage 7.7B: Returns true if the Windows accessibility "Animate controls
    /// and elements inside windows" setting is off, or if SPI_GETCLIENTAREAANIMATION
    /// reports animations disabled. Also checks UserPreferencesMask bit 1
    /// (UPM_ANIMATION) as a belt-and-suspenders fallback.
    /// </summary>
    private static bool DetectReducedMotion()
    {
        if (!System.Windows.SystemParameters.ClientAreaAnimation)
            return true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Desktop");
            var mask = key?.GetValue("UserPreferencesMask") as byte[];
            // Bit 1 of byte 0 (0x02) = UPM_ANIMATION; cleared means animations off.
            if (mask is { Length: > 0 } && (mask[0] & 0x02) == 0)
                return true;
        }
        catch { /* registry read failure: assume motion enabled */ }
        return false;
    }
}
