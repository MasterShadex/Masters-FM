// Stage 7.6 STEP 7: TrayMenuViewModel. Singleton data-context for the
// Surface 03 tray ContextMenu. Exposes now-playing passthrough, toggle
// states (Discord / AutoStart), a state-driven update label, and 10
// RelayCommands wired to IDialogService / toggle services / update service.
//
// Thread safety: Discord and AutoStart StateChanged events fire synchronously
// on the calling thread (Toggle() is sync; callers are on UI thread via menu
// click). UpdateCheckService.StateChanged may fire on a thread-pool thread
// (CheckNowAsync / DownloadAsync complete async); OnUpdateStateChanged
// marshals to the UI dispatcher.
//
// Stage 7.8C STEP 3: obs.pending_restart config field persists PendingRestart
// state across tray restarts. 60s polling timer auto-clears the suffix once
// OBS has stopped running (file-edit already applied; just need the reload).

using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MastersFM.Tray.Dialogs;
using MastersFM.Tray.Services;
using MastersFM.Tray.Update;

namespace MastersFM.Tray.ViewModels;

public sealed partial class TrayMenuViewModel : ObservableObject
{
    private readonly IDialogService _dialogService;
    private readonly IDiscordToggleService _discordService;
    private readonly IAutoStartService _autoStartService;
    private readonly IUpdateCheckService _updateService;
    private readonly ICustomizerLauncher _customizerLauncher;
    private readonly IObsService _obsService;
    private readonly IConfigService _configService;
    private readonly ILogger _logger;

    // Stage 7.8C STEP 3: 60s timer polls for OBS exit while in PendingRestart.
    private System.Threading.Timer? _obsPollTimer;

    // NowPlaying is exposed as a pass-through so XAML DataTemplates can
    // bind directly to its observable Artist, Track, ArtUri properties.
    public NowPlayingViewModel NowPlaying { get; }

    [ObservableProperty]
    private bool _isDiscordEnabled;

    [ObservableProperty]
    private bool _isAutoStartEnabled;

    [ObservableProperty]
    private string _updateLabel = "Check for updates";

    [ObservableProperty]
    private bool _isObsEnabled;

    [ObservableProperty]
    private string _obsLabel = "OBS overlay";

    [ObservableProperty]
    private string _obsTooltip = "Click to enable OBS integration";

    // Stage 7.8C: file-edit state machine (replaces ObsConnectionState-driven labels)
    private ObsToggleState _obsToggleState;

    /// <summary>
    /// Wired by MainWindow.OnLoaded so OBS toggle can show balloon tips without
    /// a direct UI reference in the ViewModel.
    /// </summary>
    internal Action<string, string>? ShowToast { get; set; }

    /// <summary>
    /// Set by MainWindow.OnLoaded so Quit / Restart commands can close the host
    /// window (dispose TaskbarIcon) before Application.Shutdown. With
    /// ShutdownMode=OnExplicitShutdown, Shutdown() does NOT fire OnClosing --
    /// explicit pre-close is required for clean NotifyIcon disposal.
    /// </summary>
    internal Action? CleanShutdown { get; set; }

    /// <summary>
    /// Set by MainWindow.OnLoaded so the ShowMenuCommand (fired on tray left-
    /// click via LeftClickCommand) can open the ContextMenu at the current
    /// cursor position without the ViewModel holding a UI reference.
    /// </summary>
    internal Action? OpenContextMenu { get; set; }

    public TrayMenuViewModel(
        NowPlayingViewModel nowPlaying,
        IDialogService dialogService,
        IDiscordToggleService discordService,
        IAutoStartService autoStartService,
        IUpdateCheckService updateService,
        ICustomizerLauncher customizerLauncher,
        IObsService obsService,
        IConfigService configService,
        ILogger logger)
    {
        NowPlaying = nowPlaying;
        _dialogService = dialogService;
        _discordService = discordService;
        _autoStartService = autoStartService;
        _updateService = updateService;
        _customizerLauncher = customizerLauncher;
        _obsService = obsService;
        _configService = configService;
        _logger = logger;

        // Snapshot initial toggle states
        _isDiscordEnabled = _discordService.IsEnabled;
        _isAutoStartEnabled = _autoStartService.IsEnabled;

        // Derive initial update label from current state
        _updateLabel = LabelForState(_updateService.CurrentState);

        // Stage 7.8C: init ObsToggleState from scene files (replaces WebSocket-state init)
        try
        {
            var initEditor = new ObsSceneFileEditor(_logger);
            _obsToggleState = initEditor.BrowserSourceExists()
                ? ObsToggleState.Added
                : ObsToggleState.NotAdded;
        }
        catch { _obsToggleState = ObsToggleState.NotAdded; }

        // Stage 7.8C STEP 3: override with PendingRestart if config says so AND OBS is
        // still running. If OBS has already exited since last tray session, the pending
        // change was already applied on OBS startup — BrowserSourceExists() above is
        // already correct, so clear the stale flag and keep the resolved state.
        try
        {
            if (_configService.GetValue<bool>("obs.pending_restart", false))
            {
                if (IsObsRunning())
                    _obsToggleState = ObsToggleState.PendingRestart;
                else
                    _configService.SetValue("obs.pending_restart", false); // stale flag — clear
            }
        }
        catch (Exception ex) { _logger.LogErr("obs.pending_restart init read", ex, "OBS"); }

        UpdateObsMenuFromToggleState();

        // Stage 7.8C STEP 3: start 60s OBS-exit polling timer. Fires only when in
        // PendingRestart; a guard at the top of the callback makes other firings a no-op.
        _obsPollTimer = new System.Threading.Timer(
            OnObsPollTick, null,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        // Subscribe to state changes
        _discordService.StateChanged += (_, enabled) => IsDiscordEnabled = enabled;
        _autoStartService.StateChanged += (_, enabled) => IsAutoStartEnabled = enabled;
        _updateService.StateChanged += OnUpdateStateChanged;
        // Stage 7.8C: WebSocket connection-state events no longer drive menu labels.
        // _obsService.ConnectionStateChanged += OnObsStateChanged;  // dead (Stage 7.8C)
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnUpdateStateChanged(object? sender, UpdateStateChangedEventArgs e)
    {
        var label = LabelForState(e.NewState);
        // UpdateCheckService may fire on a thread-pool thread; marshal to UI.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            UpdateLabel = label;
        else
            dispatcher.BeginInvoke(() => UpdateLabel = label);
    }

    private static string LabelForState(UpdateState state) => state switch
    {
        UpdateState.Checking    => "Checking...",
        UpdateState.Available   => "Download update",
        UpdateState.Downloading => "Downloading...",
        UpdateState.Ready       => "Install update",
        UpdateState.Installing  => "Installing...",
        _                       => "Check for updates"
    };

    // Stage 7.8: OBS state → menu label/tooltip/checkbox
    private void OnObsStateChanged(object? sender, ObsConnectionStateChangedEventArgs e)
    {
        var (label, tooltip, enabled) = LabelsForObsState(e.NewState);
        // ConnectionStateChanged fires on the thread-pool; marshal to UI dispatcher.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            IsObsEnabled = enabled;
            ObsLabel     = label;
            ObsTooltip   = tooltip;
        }
        else
        {
            dispatcher.BeginInvoke(() =>
            {
                IsObsEnabled = enabled;
                ObsLabel     = label;
                ObsTooltip   = tooltip;
            });
        }
    }

    private static (string label, string tooltip, bool enabled) LabelsForObsState(ObsConnectionState state) => state switch
    {
        ObsConnectionState.Disabled       => ("OBS overlay",                   "Click to enable OBS integration",          false),
        ObsConnectionState.Disconnected   => ("OBS overlay (offline)",          "OBS not running — will connect when OBS starts", true),
        ObsConnectionState.Connecting     => ("OBS overlay (connecting…)", "Connecting to OBS…",                  true),
        ObsConnectionState.Authenticating => ("OBS overlay (connecting…)", "Authenticating with OBS…",            true),
        ObsConnectionState.Connected      => ("OBS overlay (live)",             "Connected to OBS",                         true),
        ObsConnectionState.Error          => ("OBS overlay (error)",            "Connection error; retrying with backoff",  true),
        _                                 => ("OBS overlay",                   "Click to enable OBS integration",          false),
    };

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenPlatformDetectionAsync()
    {
        _logger.Log("TrayMenu: Platform detection", "Tray");
        try { await _dialogService.ShowPlatformsAsync(); }
        catch (Exception ex) { _logger.LogErr("ShowPlatformsAsync", ex, "Tray"); }
    }

    [RelayCommand]
    private async Task OpenAudioSourceAsync()
    {
        _logger.Log("TrayMenu: Audio source", "Tray");
        try { await _dialogService.ShowAudioDeviceAsync(); }
        catch (Exception ex) { _logger.LogErr("ShowAudioDeviceAsync", ex, "Tray"); }
    }

    [RelayCommand]
    private void OpenCustomizer()
    {
        _logger.Log("TrayMenu: Customize overlay", "Tray");
        try { _customizerLauncher.Launch(); }
        catch (Exception ex) { _logger.LogErr("CustomizerLauncher.Launch", ex, "Tray"); }
    }

    [RelayCommand]
    private void ToggleDiscord()
    {
        _logger.Log($"TrayMenu: Discord toggle -> {!_discordService.IsEnabled}", "Tray");
        try { _discordService.Toggle(); }
        catch (Exception ex) { _logger.LogErr("DiscordToggleService.Toggle", ex, "Tray"); }
    }

    [RelayCommand]
    private void ToggleAutoStart()
    {
        _logger.Log($"TrayMenu: AutoStart toggle -> {!_autoStartService.IsEnabled}", "Tray");
        try { _autoStartService.Toggle(); }
        catch (Exception ex) { _logger.LogErr("AutoStartService.Toggle", ex, "Tray"); }
    }

    // Stage 7.8C: file-edit-only OBS toggle (replaces Stage 7.8B WebSocket path).
    // WebSocket call sites (_obsService.ConnectAsync / AddBrowserSourceAsync /
    // RemoveBrowserSourceAsync) removed; WaitForObsConnectedAsync removed.
    // Stage 8.5 (post-rc.3) decides removal of dead WebSocket methods on ObsService.
    [RelayCommand]
    private Task ToggleObsAsync()
    {
        _logger.Log($"TrayMenu: OBS toggle (state={_obsToggleState})", "Tray");
        try
        {
            var editor = new ObsSceneFileEditor(_logger);
            const string CanonUrl = "http://localhost:4242/?renderer=webgl";
            const string CanonCss = "body { background-color: rgba(0,0,0,0) !important; margin: 0; overflow: hidden; }";

            if (_obsToggleState == ObsToggleState.Added)
            {
                // Toggle OFF: file-edit remove
                var removed = editor.RemoveBrowserSource();
                if (removed)
                {
                    if (IsObsRunning())
                    {
                        SetObsToggleState(ObsToggleState.PendingRestart);
                        ShowToast?.Invoke(
                            "Master's FM",
                            "OBS overlay will be removed next time you restart OBS.");
                    }
                    else
                    {
                        SetObsToggleState(ObsToggleState.NotAdded);
                    }
                }
            }
            else
            {
                // Toggle ON: file-edit add (idempotent; updates URL if mismatched)
                var added = editor.AddBrowserSource(CanonUrl, 1000, 200, 60, CanonCss);
                if (added || editor.BrowserSourceExists())
                {
                    if (IsObsRunning())
                    {
                        SetObsToggleState(ObsToggleState.PendingRestart);
                        ShowToast?.Invoke(
                            "Master's FM",
                            "OBS overlay added. Restart OBS to load it.");
                    }
                    else
                    {
                        SetObsToggleState(ObsToggleState.Added);
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogErr("OBS toggle", ex, "Tray"); }
        return Task.CompletedTask;
    }

    // Stage 7.8C: sets ObsToggleState + updates menu labels atomically.
    // Stage 7.8C STEP 3: also persists obs.pending_restart to config so the
    // suffix survives a tray restart (e.g. user restarts Master's FM before OBS).
    private void SetObsToggleState(ObsToggleState state)
    {
        _obsToggleState = state;
        try
        {
            _configService.SetValue("obs.pending_restart", state == ObsToggleState.PendingRestart);
        }
        catch (Exception ex) { _logger.LogErr("obs.pending_restart write", ex, "OBS"); }
        UpdateObsMenuFromToggleState();
    }

    private void UpdateObsMenuFromToggleState()
    {
        (var lbl, var tip, var enabled) = _obsToggleState switch
        {
            ObsToggleState.Added          => ("OBS overlay (added)",
                                             "Master's FM overlay is active; click to remove", true),
            ObsToggleState.PendingRestart => ("OBS overlay (restart OBS to apply)",
                                             "Changes take effect on next OBS restart", true),
            _                            => ("OBS overlay",
                                            "Click to add Master's FM to OBS", false),
        };
        ObsLabel    = lbl;
        ObsTooltip  = tip;
        IsObsEnabled = enabled;
    }

    private static bool IsObsRunning() =>
        Process.GetProcessesByName("obs64").Length > 0
        || Process.GetProcessesByName("obs32").Length > 0
        || Process.GetProcessesByName("obs").Length  > 0;

    // Stage 7.8C STEP 3: 60s poll — clear PendingRestart suffix when OBS has exited.
    // Fires on a thread-pool thread (System.Threading.Timer); marshals state change
    // to the UI dispatcher. Guard: no-op unless currently PendingRestart.
    private void OnObsPollTick(object? state)
    {
        if (_obsToggleState != ObsToggleState.PendingRestart) return;
        if (IsObsRunning()) return;

        // OBS has stopped — the file-edit changes have been applied on its last start.
        // Resolve final state from scene files and clear the pending flag.
        try
        {
            var editor = new ObsSceneFileEditor(_logger);
            var exists  = editor.BrowserSourceExists();
            var newState = exists ? ObsToggleState.Added : ObsToggleState.NotAdded;
            _logger.Log($"[OBS poll] OBS exited; resolving PendingRestart -> {newState}", "OBS");

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                SetObsToggleState(newState);
            else
                dispatcher.BeginInvoke(() => SetObsToggleState(newState));
        }
        catch (Exception ex) { _logger.LogErr("OBS poll-tick state resolve", ex, "OBS"); }
    }

    [RelayCommand]
    private async Task OpenPatchNotesAsync()
    {
        _logger.Log("TrayMenu: Patch notes", "Tray");
        try { await _dialogService.ShowWelcomeAsync(); }
        catch (Exception ex) { _logger.LogErr("ShowWelcomeAsync", ex, "Tray"); }
    }

    [RelayCommand]
    private void OpenLog()
    {
        _logger.Log("TrayMenu: View log", "Tray");
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MastersFM");
        try
        {
            if (Directory.Exists(logDir))
                Process.Start("explorer.exe", logDir);
        }
        catch (Exception ex) { _logger.LogErr("OpenLog explorer", ex, "Tray"); }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        var state = _updateService.CurrentState;
        _logger.Log($"TrayMenu: Check updates (state={state})", "Tray");
        try
        {
            // INTERRUPT #3 STEP 6 (Issue 3): show the update-progress overlay
            // before driving the state machine.  ShowUpdateProgressAsync uses
            // Show() (non-modal) and returns immediately; the window stays open
            // while download / install progress.  Repeat clicks bring the
            // existing window to the foreground.
            await _dialogService.ShowUpdateProgressAsync();

            switch (state)
            {
                case UpdateState.Idle:
                case UpdateState.Error:
                    await _updateService.CheckNowAsync();
                    break;
                case UpdateState.Available:
                    await _updateService.DownloadAsync();
                    break;
                case UpdateState.Ready:
                    await _updateService.InstallAsync();
                    break;
                // Checking / Downloading / Installing: window already visible above.
            }
        }
        catch (Exception ex) { _logger.LogErr("CheckUpdates", ex, "Tray"); }
    }

    [RelayCommand]
    private void RestartApp()
    {
        _logger.Log("TrayMenu: Restart Master's FM", "Tray");
        var exe = Environment.ProcessPath;
        try
        {
            if (exe != null && File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { _logger.LogErr("RestartApp spawn", ex, "Tray"); }
        InvokeCleanShutdown();
    }

    [RelayCommand]
    private void QuitApp()
    {
        _logger.Log("TrayMenu: Quit Master's FM", "Tray");
        InvokeCleanShutdown();
    }

    // INTERRUPT #3 STEP 2: Issue 7 -- tray left-click opens menu.
    // Called via LeftClickCommand on TaskbarIcon (MainWindow.xaml). The
    // delegate is wired in MainWindow.OnLoaded; no direct UI reference here.
    [RelayCommand]
    private void ShowMenu()
    {
        _logger.Log("TrayMenu: ShowMenu (left-click)", "Tray");
        OpenContextMenu?.Invoke();
    }

    // Calls the MainWindow-provided clean-shutdown delegate (close host window
    // first to dispose TaskbarIcon) then falls back to raw Shutdown if not set.
    private void InvokeCleanShutdown()
    {
        if (CleanShutdown != null)
            CleanShutdown();
        else
            Application.Current.Shutdown(0);
    }
}

/// <summary>Stage 7.8C: file-edit OBS toggle state machine.</summary>
public enum ObsToggleState
{
    /// <summary>No Master's FM source in OBS scene collections.</summary>
    NotAdded,
    /// <summary>Source added to scene collections; OBS reload not required (was closed).</summary>
    Added,
    /// <summary>Source added or removed while OBS was running; takes effect after OBS restart.</summary>
    PendingRestart,
}
