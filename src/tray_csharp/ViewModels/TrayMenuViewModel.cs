// Stage 7.6 STEP 7: TrayMenuViewModel. Singleton data-context for the
// Surface 03 tray ContextMenu. Exposes now-playing passthrough, toggle
// states (Discord / AutoStart), a state-driven update label, and 10
// RelayCommands wired to IDialogService / toggle services / update service.
//
// Stage 7.8C: OBS toggle state machine + file-edit-only path.
// Stage 7.8D: intent vs. reality state machine. obs.intent config field
// separates user intent ("on"|"off") from on-disk OBS reality. UUID tracking
// via obs.tray_added_uuid prevents stomping user-added sources. ReconcileStateAsync
// is the single source of truth; 5s startup direct-add replaced by reconcile timer.

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

    // Stage 7.8D: reconcile timer (replaces Stage 7.8C 60s OBS-exit poll timer).
    private System.Threading.Timer? _obsReconcileTimer;

    // Stage 7.8D: file-edit state machine (intent × on-disk reality).
    private ObsToggleState _obsToggleState;

    public NowPlayingViewModel NowPlaying { get; }

    [ObservableProperty] private bool _isDiscordEnabled;
    [ObservableProperty] private bool _isAutoStartEnabled;
    [ObservableProperty] private string _updateLabel = "Check for updates";
    [ObservableProperty] private string _obsLabel    = "OBS overlay";
    [ObservableProperty] private string _obsTooltip  = "Click to enable OBS integration";
    [ObservableProperty] private string _nowPlayingHeaderText = "v14.0.0 - ready";

    /// <summary>Stage 7.8D: computed from _obsToggleState; fires OnPropertyChanged explicitly.</summary>
    public bool IsObsEnabled =>
        _obsToggleState == ObsToggleState.Added ||
        _obsToggleState == ObsToggleState.PendingRestart;

    internal Action<string, string>? ShowToast { get; set; }
    internal Action? CleanShutdown { get; set; }
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

        _isDiscordEnabled  = _discordService.IsEnabled;
        _isAutoStartEnabled = _autoStartService.IsEnabled;
        _updateLabel = LabelForState(_updateService.CurrentState);

        // Stage 7.8D: migrate obs.enabled (Stage 7.8B/C) → obs.intent.
        // Run migration if obs.intent is absent (empty = not set).
        // obs.pending_restart: ignored on read going forward (derived from disk + process state).
        try
        {
            var existingIntent = _configService.GetValue<string>("obs.intent", "") ?? "";
            if (string.IsNullOrEmpty(existingIntent))
            {
                var wasEnabled = _configService.GetValue<bool>("obs.enabled", false);
                _configService.SetValue("obs.intent", wasEnabled ? "on" : "off");
                _logger.Log(
                    $"[ObsState] migrated obs.enabled={wasEnabled} → obs.intent={(wasEnabled ? "on" : "off")}",
                    "OBS");
                // Leave obs.enabled as tombstone (Stage 8.5 will clean it)
            }
        }
        catch (Exception ex) { _logger.LogErr("obs.intent migration", ex, "OBS"); }

        // Initial state: sync read of intent + scene file (before first reconcile)
        try
        {
            var intent = _configService.GetValue<string>("obs.intent", "off") ?? "off";
            var trayUuid = _configService.GetValue<string>("obs.tray_added_uuid", "") ?? "";
            var editor = new ObsSceneFileEditor(_logger);
            var sources = editor.ScanForBrowserSources();
            var oursPresent = !string.IsNullOrEmpty(trayUuid)
                && sources.Any(s => string.Equals(s.Uuid, trayUuid, StringComparison.Ordinal));
            _obsToggleState = (intent == "on" && oursPresent)
                ? ObsToggleState.Added
                : ObsToggleState.NotAdded;
        }
        catch { _obsToggleState = ObsToggleState.NotAdded; }

        SetObsToggleState(_obsToggleState, LabelForObsState(_obsToggleState));

        // Stage 7.12 Batch B DIAG-09: reconcile timer interval lowered from
        // 60 s → 5 s.  The slow poll left a 0-60 s window between OBS closing
        // and reopening where the JSON file still reflected OBS's last
        // (clobbered) autosave instead of our overlay entry.  Combined with
        // the event-driven Process.Exited watcher attached in
        // EnsureObsExitWatcher (called from every reconcile), OBS exits now
        // trigger a near-instant rewrite of the scene file.
        _obsReconcileTimer = new System.Threading.Timer(
            _ => _ = ReconcileAsync(),
            null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        _discordService.StateChanged  += (_, enabled) => IsDiscordEnabled  = enabled;
        _autoStartService.StateChanged += (_, enabled) => IsAutoStartEnabled = enabled;
        _updateService.StateChanged   += OnUpdateStateChanged;

        // Stage 7.7B STEP 5: keep header subtitle current when track changes.
        NowPlaying.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.Artist) ||
                e.PropertyName == nameof(NowPlayingViewModel.Track))
                UpdateNowPlayingHeaderText();
        };
    }

    // ── Now-playing header ────────────────────────────────────────────────────

    /// <summary>
    /// Updates NowPlayingHeaderText from the current NowPlaying state.
    /// Shows "Artist - Track" when playing; falls back to version/status line.
    /// </summary>
    private void UpdateNowPlayingHeaderText()
    {
        var artist = NowPlaying.Artist;
        var track  = NowPlaying.Track;
        var text   = string.IsNullOrWhiteSpace(artist)
            ? "v14.0.0 - ready"
            : string.IsNullOrWhiteSpace(track)
                ? artist
                : $"{artist} - {track}";
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            NowPlayingHeaderText = text;
        else
            dispatcher.BeginInvoke(() => NowPlayingHeaderText = text);
    }

    // ── Update state ─────────────────────────────────────────────────────────

    private void OnUpdateStateChanged(object? sender, UpdateStateChangedEventArgs e)
    {
        var label = LabelForState(e.NewState);
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

    // Stage 7.8D: intent-driven toggle. Sets obs.intent then reconciles.
    [RelayCommand]
    private async Task ToggleObsAsync()
    {
        try
        {
            var currentIntent = _configService.GetValue<string>("obs.intent", "off") ?? "off";
            var newIntent = currentIntent == "on" ? "off" : "on";
            _configService.SetValue("obs.intent", newIntent);
            _logger.Log($"[ObsState] toggle: intent {currentIntent} → {newIntent}", "OBS");
            await ReconcileAsync();
        }
        catch (Exception ex) { _logger.LogErr("[ObsState] toggle failed", ex, "OBS"); }
    }

    // ── Stage 7.8D: OBS state machine ────────────────────────────────────────

    /// <summary>
    /// Single source of truth for OBS toggle state. Reads obs.intent + scene files
    /// + OBS process state and derives the correct ObsToggleState. Fires AutoAdd
    /// or AutoRemove as needed. Safe to call from any thread.
    /// </summary>
    private Task ReconcileAsync()
    {
        try
        {
            var intent   = _configService.GetValue<string>("obs.intent", "off") ?? "off";
            var trayUuid = _configService.GetValue<string>("obs.tray_added_uuid", "") ?? "";

            var editor  = new ObsSceneFileEditor(_logger);
            var sources = editor.ScanForBrowserSources();

            var ours    = sources.FirstOrDefault(s =>
                !string.IsNullOrEmpty(trayUuid) &&
                string.Equals(s.Uuid, trayUuid, StringComparison.Ordinal));
            var foreign = sources.FirstOrDefault(s =>
                string.IsNullOrEmpty(trayUuid) ||
                !string.Equals(s.Uuid, trayUuid, StringComparison.Ordinal));

            bool oursPresent    = ours != null;
            bool foreignPresent = foreign != null;
            bool obsRunning     = IsObsRunning();

            bool fileChangedAfterObsStart = false;
            if (obsRunning && oursPresent && ours != null)
            {
                var obsStart = GetObsProcessStartTimeUtc();
                if (obsStart.HasValue)
                {
                    var fileMtime = File.GetLastWriteTimeUtc(ours.CollectionPath);
                    fileChangedAfterObsStart = fileMtime > obsStart.Value;
                }
            }

            if (foreignPresent && !oursPresent)
                _logger.LogWarn($"[ObsState] foreign source detected: uuid={foreign!.Uuid}; not modified", "OBS");

            ObsToggleState newState;
            string newLabel;
            bool needAdd    = false;
            bool needRemove = false;

            if (intent == "on")
            {
                if (oursPresent)
                {
                    if (obsRunning && fileChangedAfterObsStart)
                    {
                        newState = ObsToggleState.PendingRestart;
                        newLabel = "OBS overlay (restart OBS to apply)";
                    }
                    else
                    {
                        newState = ObsToggleState.Added;
                        newLabel = "OBS overlay (added)";
                    }
                }
                else if (foreignPresent)
                {
                    // Foreign source present — skip add, log warning
                    newState = ObsToggleState.NotAdded;
                    newLabel = "OBS overlay";
                }
                else if (!string.IsNullOrEmpty(trayUuid) && obsRunning)
                {
                    // DIAG-09 fix: we previously added our source, but the
                    // running OBS process auto-saves its in-memory scene back
                    // to the JSON every ~30-60 s, clobbering our file edit.
                    // Re-adding while OBS is running just lets OBS clobber us
                    // again on its next autosave (and churns the UUID stored
                    // in obs.tray_added_uuid).
                    //
                    // Stay in PendingRestart and DO NOT re-write the file —
                    // the next scheduled reconcile after OBS closes will pick
                    // up the "obs not running" branch below and write a stable
                    // copy that survives the next OBS launch.
                    newState = ObsToggleState.PendingRestart;
                    newLabel = "OBS overlay (restart OBS to apply)";
                }
                else
                {
                    // Either first-run (no prior UUID) or OBS is closed so a
                    // file edit will survive. Safe to add.
                    newState = ObsToggleState.NotAdded;
                    newLabel = "OBS overlay (adding…)";
                    needAdd  = true;
                }
            }
            else // intent == "off"
            {
                if (oursPresent)
                {
                    newState   = ObsToggleState.NotAdded;
                    newLabel   = "OBS overlay (removing…)";
                    needRemove = true;
                }
                else
                {
                    newState = ObsToggleState.NotAdded;
                    newLabel = "OBS overlay";
                }
            }

            _logger.Log(
                $"[ObsState] reconcile: intent={intent} ours={oursPresent} foreign={foreignPresent} " +
                $"obs={(obsRunning ? "running" : "stopped")} fileNewer={fileChangedAfterObsStart} " +
                $"→ {newState}", "OBS");

            SetObsToggleState(newState, newLabel);

            if (needAdd)    _ = Task.Run(AutoAddAsync);
            if (needRemove) _ = Task.Run(() => AutoRemoveAsync(trayUuid));

            // DIAG-09: hook into the running OBS process so its exit triggers
            // an immediate reconcile (no 5-second timer wait).  Called every
            // reconcile so a freshly-started OBS process gets re-watched.
            EnsureObsExitWatcher();
        }
        catch (Exception ex) { _logger.LogErr("[ObsState] ReconcileAsync", ex, "OBS"); }
        return Task.CompletedTask;
    }

    private async Task AutoAddAsync()
    {
        try
        {
            var intent = _configService.GetValue<string>("obs.intent", "off") ?? "off";
            if (intent != "on") return; // intent changed since reconcile fired

            var trayUuid = _configService.GetValue<string>("obs.tray_added_uuid", "") ?? "";
            var editor   = new ObsSceneFileEditor(_logger);
            const string url = "http://localhost:4242/?renderer=webgl";
            const string css = "body { background-color: rgba(0,0,0,0) !important; margin: 0; overflow: hidden; }";

            var result = editor.AddBrowserSource(url, 1000, 200, 60, css, trayUuid);
            if (result.Success && result.Uuid != null)
            {
                _configService.SetValue("obs.tray_added_uuid", result.Uuid);
                _logger.Log($"[ObsState] AutoAdd succeeded: uuid={result.Uuid}", "OBS");
            }
            else
            {
                _logger.LogWarn($"[ObsState] AutoAdd: {result.Reason}", "OBS");
            }

            await ReconcileAsync(); // update state after add
        }
        catch (Exception ex) { _logger.LogErr("[ObsState] AutoAddAsync", ex, "OBS"); }
    }

    private async Task AutoRemoveAsync(string uuid)
    {
        try
        {
            if (string.IsNullOrEmpty(uuid)) return;
            var editor  = new ObsSceneFileEditor(_logger);
            var removed = editor.RemoveBrowserSourceByUuid(uuid);
            if (removed)
                _logger.Log($"[ObsState] AutoRemove succeeded: uuid={uuid}", "OBS");
            else
                _logger.LogWarn($"[ObsState] AutoRemove: uuid={uuid} not found (already absent)", "OBS");

            await ReconcileAsync(); // update state after remove
        }
        catch (Exception ex) { _logger.LogErr("[ObsState] AutoRemoveAsync", ex, "OBS"); }
    }

    /// <summary>
    /// Stage 7.8D: sets state + label atomically; fires OnPropertyChanged for both
    /// ObsLabel (via [ObservableProperty] setter) and IsObsEnabled (computed — explicit).
    /// Safe to call from any thread; marshals to UI dispatcher as needed.
    /// </summary>
    private void SetObsToggleState(ObsToggleState newState, string newLabel)
    {
        void Update()
        {
            var prev = _obsToggleState;
            if (prev == newState && ObsLabel == newLabel) return;
            _obsToggleState = newState;
            ObsLabel    = newLabel; // [ObservableProperty] fires OnPropertyChanged(ObsLabel)
            ObsTooltip  = newState switch
            {
                ObsToggleState.Added          => "Master's FM overlay is active; click to remove",
                ObsToggleState.PendingRestart => "Changes take effect on next OBS restart",
                _                             => "Click to add Master's FM to OBS"
            };
            OnPropertyChanged(nameof(IsObsEnabled)); // computed — must fire explicitly
            _logger.Log($"[ObsState] transition: {prev} → {newState}", "OBS");
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Update();
        else
            dispatcher.Invoke(Update);
    }

    private static string LabelForObsState(ObsToggleState s) => s switch
    {
        ObsToggleState.Added          => "OBS overlay (added)",
        ObsToggleState.PendingRestart => "OBS overlay (restart OBS to apply)",
        _                             => "OBS overlay"
    };

    private static bool IsObsRunning() =>
        Process.GetProcessesByName("obs64").Length > 0
        || Process.GetProcessesByName("obs32").Length > 0
        || Process.GetProcessesByName("obs").Length  > 0;

    private static DateTime? GetObsProcessStartTimeUtc()
    {
        var procs = Process.GetProcessesByName("obs64")
            .Concat(Process.GetProcessesByName("obs32"))
            .Concat(Process.GetProcessesByName("obs"))
            .ToArray();
        if (procs.Length == 0) return null;
        try { return procs[0].StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    // ── DIAG-09 Process.Exited watcher ────────────────────────────────────────
    // When the user enables the overlay while OBS is running, we hold in
    // PendingRestart waiting for OBS to close.  Polling every 5 s catches
    // most exits, but the window between OBS closing and the user reopening
    // it can be much shorter.  Subscribing to Process.Exited fires
    // immediately, so the file is rewritten with our entry before OBS reads
    // it again.

    private Process? _watchedObsProcess;
    private readonly object _watchedObsLock = new();

    private void EnsureObsExitWatcher()
    {
        try
        {
            lock (_watchedObsLock)
            {
                // Already watching a live process — keep it.
                if (_watchedObsProcess != null)
                {
                    try { if (!_watchedObsProcess.HasExited) return; }
                    catch { /* fell out from under us */ }
                    try { _watchedObsProcess.Dispose(); } catch { }
                    _watchedObsProcess = null;
                }

                var obs = Process.GetProcessesByName("obs64").FirstOrDefault()
                       ?? Process.GetProcessesByName("obs32").FirstOrDefault()
                       ?? Process.GetProcessesByName("obs").FirstOrDefault();
                if (obs == null) return;

                try { obs.EnableRaisingEvents = true; }
                catch
                {
                    // Some processes refuse — fall back to timer polling.
                    try { obs.Dispose(); } catch { }
                    return;
                }
                obs.Exited += OnObsExited;
                _watchedObsProcess = obs;
                _logger.Log($"[ObsState] watching OBS PID={obs.Id} for exit", "OBS");
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("[ObsState] EnsureObsExitWatcher", ex, "OBS");
        }
    }

    private void OnObsExited(object? sender, EventArgs e)
    {
        try
        {
            _logger.Log("[ObsState] OBS process exited; firing immediate reconcile", "OBS");
            lock (_watchedObsLock)
            {
                try { _watchedObsProcess?.Dispose(); } catch { }
                _watchedObsProcess = null;
            }
            _ = Task.Run(() => ReconcileAsync());
        }
        catch (Exception ex)
        {
            _logger.LogErr("[ObsState] OnObsExited", ex, "OBS");
        }
    }

    // ── Other commands ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenPatchNotesAsync()
    {
        _logger.Log("TrayMenu: Patch notes", "Tray");
        try { await _dialogService.ShowWelcomeAsync(showAboutTab: true); }
        catch (Exception ex) { _logger.LogErr("ShowWelcomeAsync", ex, "Tray"); }
    }

    [RelayCommand]
    private void OpenLog()
    {
        _logger.Log("TrayMenu: View log", "Tray");
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MastersFM", "overlay.log");
        try
        {
            // Stage 7.12 Issue 7: open the log file directly (default .log handler,
            // typically Notepad) rather than opening the containing folder.
            if (File.Exists(logPath))
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            else
            {
                // Log file does not exist yet -- fall back to opening the folder.
                var logDir = Path.GetDirectoryName(logPath)!;
                if (Directory.Exists(logDir))
                    Process.Start("explorer.exe", logDir);
            }
        }
        catch (Exception ex) { _logger.LogErr("OpenLog", ex, "Tray"); }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        var state = _updateService.CurrentState;
        _logger.Log($"TrayMenu: Check updates (state={state})", "Tray");
        try
        {
            await _dialogService.ShowUpdateProgressAsync();
            switch (state)
            {
                case UpdateState.Ready:
                    // MSI already downloaded + SHA256/Authenticode-verified -> install it.
                    await _updateService.InstallAsync();
                    break;

                case UpdateState.Checking:
                case UpdateState.Downloading:
                case UpdateState.Installing:
                    // An operation is already running -- just surface the progress
                    // window; never restart or disturb an in-flight transfer.
                    break;

                case UpdateState.Available:
                    // "Download update": ALWAYS re-read the live manifest first so a
                    // superseded or removed release (e.g. a deleted build the app had
                    // latched onto) is never downloaded. Only fetch if the freshly
                    // read manifest STILL advertises an update.
                    if (await _updateService.ForceCheckNowAsync() == UpdateState.Available)
                        await _updateService.DownloadAsync();
                    break;

                default: // Idle, Error -> "Check for updates": re-read the manifest, fresh, every press.
                    await _updateService.ForceCheckNowAsync();
                    break;
            }
        }
        catch (Exception ex) { _logger.LogErr("CheckUpdates", ex, "Tray"); }
    }

    [RelayCommand]
    private void RestartApp()
    {
        _logger.Log("TrayMenu: Restart Master's FM", "Tray");

        // Stage 7.12 Batch A: the tray runs INSIDE the launcher's Job Object,
        // and the launcher holds a Global\MastersFM_Launcher named mutex.
        // Just respawning Environment.ProcessPath (the tray exe) creates a
        // second tray without a server/audio_spectrum and leaves the user
        // with a half-working app.
        //
        // Correct sequence:
        //   1. Spawn a DETACHED cmd helper via UseShellExecute=true so it's
        //      a child of Explorer.exe, not us — survives our Job Object's
        //      cascade kill when we shut down.
        //   2. Helper waits 3 s (giving the launcher time to detect our exit,
        //      kill server + audio_spectrum, and release the launcher mutex).
        //   3. Helper then starts MastersFM.exe — the launcher claims the
        //      now-released mutex and respawns the whole process tree
        //      (server.exe, audio_spectrum.exe, the tray) fresh.
        //   4. We call InvokeCleanShutdown so the launcher notices we exited.
        var trayExe    = Environment.ProcessPath ?? string.Empty;
        var workingDir = Path.GetDirectoryName(trayExe) ?? string.Empty;
        var launcher   = Path.Combine(workingDir, "MastersFM.exe");

        if (string.IsNullOrEmpty(launcher) || !File.Exists(launcher))
        {
            _logger.LogErr($"RestartApp: launcher not found at '{launcher}'; falling back to tray-only respawn", null, "Tray");
            try
            {
                if (File.Exists(trayExe))
                    Process.Start(new ProcessStartInfo(trayExe) { UseShellExecute = true });
            }
            catch (Exception ex) { _logger.LogErr("RestartApp fallback spawn", ex, "Tray"); }
            InvokeCleanShutdown();
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c timeout /t 3 /nobreak >nul & start \"\" \"{launcher}\"",
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            _logger.Log($"RestartApp: queued delayed relaunch (T+3s) -> {launcher}", "Tray");
        }
        catch (Exception ex)
        {
            _logger.LogErr("RestartApp helper spawn", ex, "Tray");
        }

        InvokeCleanShutdown();
    }

    [RelayCommand]
    private void QuitApp()
    {
        _logger.Log("TrayMenu: Quit Master's FM", "Tray");
        InvokeCleanShutdown();
    }

    // INTERRUPT #3 STEP 2: Issue 7 -- tray left-click opens menu.
    [RelayCommand]
    private void ShowMenu()
    {
        _logger.Log("TrayMenu: ShowMenu (left-click)", "Tray");
        OpenContextMenu?.Invoke();
    }

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
