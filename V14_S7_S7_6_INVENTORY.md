# V14_S7_S7_6_INVENTORY.md

Stage 7.6 STEP 1 -- read-only source inventory.
This is the source-of-truth for STEPS 4-13. If the brief contradicts
this inventory, the inventory wins and the delta is noted in the final report.

---

## MainWindow.xaml -- ContextMenu structure

Current ContextMenu is declared inside `<tb:TaskbarIcon.ContextMenu>`:

```xml
<ContextMenu>
    <MenuItem Header="Quit"
              Click="OnQuitClicked" />
</ContextMenu>
```

**Item count: 1**
- Item 1: `Header="Quit"` click handler `OnQuitClicked`

No separators, no icons, no data bindings. The `TaskbarIcon` has
`NoLeftClickDelay="True"` and `ToolTipText="Master's FM v14 dev (skeleton)"`.

---

## MainWindow.xaml.cs -- event handlers

```
MainWindow(ILogger logger)  -- constructor; wires Loaded event
OnLoaded(object, RoutedEventArgs)  -- logs "TaskbarIcon initialized"
OnQuitClicked(object, RoutedEventArgs)  -- sets _allowClose=true; calls Close(); Application.Current.Shutdown(0)
OnClosing(CancelEventArgs)  -- e.Cancel=true unless _allowClose=true; disposes NotifyIcon on allowed close
```

`_allowClose` bool field prevents accidental WM_CLOSE from dismissing
the hidden host window. Mutex is released in `App.OnExit`, NOT in
`MainWindow.OnClosing` - verified at App.xaml.cs lines 349-360.

---

## App.xaml -- resource dictionary keys relevant to ContextMenu/MenuItem

Current resource dictionary keys (Stage 7.1B + 7.7):

| Key | Type | Value |
|---|---|---|
| `SystemAccentColorPrimaryBrush` | SolidColorBrush | `#FF9333EA` |
| `SystemAccentColorSecondaryBrush` | SolidColorBrush | `#FFC084FC` |
| `SystemAccentColorTertiaryBrush` | SolidColorBrush | `#FF6D28D9` |
| `SystemAccentColor` | Color | `#FF9333EA` |
| `SystemAccentColorPrimary` | Color | `#FF9333EA` |
| `SystemAccentColorSecondary` | Color | `#FFC084FC` |
| `SystemAccentColorTertiary` | Color | `#FF6D28D9` |
| `BoolToVis` | BooleanToVisibilityConverter | (7.7) |

Plus merged: `ui:ThemesDictionary Theme="Dark"` and `ui:ControlsDictionary`.

**No ContextMenu-specific resource keys yet.** Stage 7.6 STEP 11 will
add `TrayMenu*` brush keys.

---

## Telemetry.cs -- current public surface

**Storage:**
- `_counters: ConcurrentDictionary<string, long>` -- unbounded; updated via `AddOrUpdate`
- `_timings: ConcurrentDictionary<string, ConcurrentQueue<double>>` -- per-key sliding window, `TimingWindowSize = 100`

**Public methods on `ITelemetry` interface (concrete Telemetry class):**
```csharp
void IncrementCounter(string name, long delta = 1)
void RecordTimingMs(string name, double milliseconds)
void RecordEvent(string eventType, IReadOnlyDictionary<string, object>? tags = null)
IReadOnlyDictionary<string, long> SnapshotCounters()
```

**Concrete-only helper (NOT on interface):**
```csharp
string GetHeartbeatSummary()
// Returns: "events={smtc_events} polls={polls_per_min} webhooks={webhook_sends} cache={art_cache_hits}/{art_cache_misses} tracks={track_changes}"
```

The `GetHeartbeatSummary()` method is not on the ITelemetry interface;
`DiagnosticHeartbeat` casts to concrete `Telemetry` to access it (type
check at line 81).

`RecordTimingMs` enqueues into the per-key ConcurrentQueue and trims
head entries when `Count > TimingWindowSize` (100). **There is no
existing accessor to the timing data for snapshot purposes** -- STEP 3
adds `SnapshotTimingsP99()` to ITelemetry.

---

## ITelemetry.cs -- current interface signature

```csharp
public interface ITelemetry
{
    void IncrementCounter(string name, long delta = 1);
    void RecordTimingMs(string name, double milliseconds);
    void RecordEvent(string eventType, IReadOnlyDictionary<string, object>? tags = null);
    IReadOnlyDictionary<string, long> SnapshotCounters();
}
```

`SnapshotTimingsP99() → IReadOnlyDictionary<string, double>` is ABSENT.
STEP 3 adds it.

---

## DiagnosticHeartbeat.cs -- current heartbeat line format and stub poll-ms

**Cadence:** 60s DispatcherTimer (DispatcherPriority.Background)

**Current heartbeat line format (as constructed in lines 114-123):**
```
heartbeat: ws=XXX.XMB gc=XX.XMB priv=XXX.XMB threads=N handles=N ring=N events=N polls=N webhooks=N cache=N/N tracks=N polls=Nslow/Ndet
```

Example from 7.7 smoke:
```
heartbeat: ws=208.5MB gc=30.3MB priv=164.1MB threads=21 handles=2170 ring=0 events=28 polls=237 webhooks=0 cache=0/2 tracks=2 polls=0slow/0det
```

**Where the stub poll-ms lives (lines 93-112):**
```csharp
var counters2 = _telemetry.SnapshotCounters();
long slowTotal = 0;
int detCount = 0;
foreach (var kv in counters2)
{
    if (kv.Key.EndsWith("_slow_ticks", StringComparison.OrdinalIgnoreCase))
    {
        slowTotal += kv.Value;
        detCount++;
    }
}
string pollsStub = $"polls={slowTotal}slow/{detCount}det";
```

STEP 4 replaces this stub with real P99 data from
`_telemetry.SnapshotTimingsP99()`.

**Proposed new heartbeat line format (STEP 4 target):**
```
heartbeat: ws=XXX.XMB gc=XX.XMB priv=XXX.XMB threads=N handles=N ring=N | events=N polls=N webhooks=N cache=N/N tracks=N | osu=XX.Xms vlc=XX.Xms wmp=XX.Xms webhook=XX.Xms smtc=XX.Xms
```

---

## IDialogService.cs -- current method list

```csharp
Task ShowWelcomeAsync(bool showAboutTab = false);
Task<AudioDeviceResult?> ShowAudioDeviceAsync();
Task ShowPlatformsAsync();
Task<bool> ShowSetupWizardAsync();
Task ShowErrorAsync(string title, string message, Exception? ex = null);
```

`AudioDeviceResult` is a sealed record with `DeviceId`, `DisplayName`, `IsDefault`.

All marshal to UI thread via `Application.Current.Dispatcher`. Use
`ShowDialog()` (blocking modal). DI registration: windows as Transient,
VMs as Singleton.

---

## DialogService.cs -- current implementations

Pattern for each Show*Async:
1. `ShowOnDispatcherAsync(Action)` / `ShowOnDispatcherAsync<T>(Func<T>)` marshal helper
2. Resolve window as transient via `_services.GetRequiredService<TWindow>()`
3. Resolve VM as singleton via `_services.GetRequiredService<TViewModel>()`
4. Set `window.DataContext = vm` and `window.Owner = Application.Current.MainWindow`
5. Call `window.ShowDialog()` (blocking)
6. For AudioDevice: `var result = vm.PendingResult; return result`
7. For SetupWizard: `return vm.Completed`
8. Log show + close at INFO

---

## NowPlayingViewModel.cs -- observables and TrackChanged subscription

**Observable properties (CommunityToolkit.Mvvm [ObservableProperty]):**
- `Source (string?)`
- `Artist (string?)`
- `Track (string?)`
- `Duration (TimeSpan?)`
- `Position (TimeSpan?)`
- `ArtUri (string?)` -- data URI from SMTC thumbnail (B-013 closure)
- `IsPlaying (bool)`

**Subscription:** `_resolver.TrackChanged += OnTrackChanged` in constructor.
Also reads `_resolver.CurrentTrack` at construction for initial state.

**Thread safety:** `OnTrackChanged` marshals to WPF dispatcher via
`Application.Current?.Dispatcher.BeginInvoke` if not on UI thread.

---

## UpdateCheckService.cs -- StateChanged + UpdateState enum

**Event:**
```csharp
event EventHandler<UpdateStateChangedEventArgs>? StateChanged;
```
`UpdateStateChangedEventArgs`: OldState, NewState, ProgressPercent?, Detail?

**UpdateState enum (7 values):**
```
Idle, Checking, Available, Downloading, Ready, Installing, Error
```

**Key methods (per brief STEP 9.4 state -> label mapping):**
- `CheckNowAsync()` -- Idle -> Checking -> Available/Idle/Error
- `DownloadAsync()` -- Available -> Downloading -> Ready (or back on failure)
- `InstallAsync()` -- Ready -> Installing -> Application.Current.Shutdown(0)
- `Cancel()` -- any -> Idle

**Valid CheckUpdatesCommand transitions per STEP 9.4:**
- Idle or Error -> `CheckNowAsync()`
- Available -> `DownloadAsync()`
- Ready -> `InstallAsync()`
- Otherwise -> no-op

---

## DiscordToggleService.cs -- StateChanged + IsEnabled

```csharp
bool IsEnabled { get; }  // reads config "discord_rpc.enabled" (default true)
void Toggle();           // flips current state
event EventHandler<bool>? StateChanged;  // args = new state (bool)
```

**Config key:** `discord_rpc.enabled` (bool)
**Default when missing:** `true`
**External change detection:** subscribes to `_config.Changed`; fires
`StateChanged` when the config key changes from outside.

---

## AutoStartService.cs -- StateChanged + IsEnabled

```csharp
bool IsEnabled { get; }  // File.Exists(_lnkPath) for "Master's FM.lnk"
void Toggle();           // calls Enable() or Disable()
event EventHandler<bool>? StateChanged;  // args = new state (bool)
```

**Lnk path:** `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Master's FM.lnk`
**Note:** IAutoStartService does NOT have a `ToggleAsync()` method --
it has synchronous `Toggle()`. Brief STEP 9.2 says `await _autoStartService.ToggleAsync()`;
actual API is sync `Toggle()`. Brief-vs-source delta: use `Toggle()` (sync).

---

## ICustomizerLauncher.cs -- invocation signature

```csharp
bool Launch();   // spawns customize.exe; returns true on success
void Close();    // terminates spawned process
bool IsRunning { get; }
```

`Launch()` is synchronous. Brief STEP 8 says `_customizerLauncher.Launch()`
(synchronous) which matches. `CustomizerLauncher.ResolveCustomizerExe()`
resolves path from `Environment.ProcessPath` parent dir.

---

## RecordTimingMs call sites (STEP 4.3 verification)

| Site | Key used | Brief's expected key | Match? |
|---|---|---|---|
| `DetectorOrchestrator` line 77 | `osu_poll_ms` (via `det.Name="osu"`) | `detector.osu.poll_ms` | **Key mismatch** |
| `DetectorOrchestrator` line 77 | `vlc_poll_ms` (via `det.Name="vlc"`) | `detector.vlc.poll_ms` | **Key mismatch** |
| `DetectorOrchestrator` line 77 | `wmp-legacy_poll_ms` (via `det.Name="wmp-legacy"`) | `detector.wmp.poll_ms` | **Key mismatch** |
| `WebhookClient` line 50 | `webhook_latency_ms` | `webhook.send_ms` | **Key mismatch** |
| `SmtcEventBridge` | ABSENT | `smtc.dispatch_ms` | **Missing** |

**Brief-vs-source delta (Q-TIMING-KEYS):** The brief's heartbeat key names
(`detector.osu.poll_ms`, `detector.vlc.poll_ms`, `detector.wmp.poll_ms`,
`webhook.send_ms`) differ from the actual keys stored in `_timings`
(`osu_poll_ms`, `vlc_poll_ms`, `wmp-legacy_poll_ms`, `webhook_latency_ms`).

Resolution per STEP 1 rule "inventory wins": DiagnosticHeartbeat STEP 4
will look up the ACTUAL key names. The heartbeat label will use short
aliases (`osu`, `vlc`, `wmp`, `webhook`) that map to the actual keys.

For `smtc.dispatch_ms`: SmtcEventBridge has NO RecordTimingMs call.
STEP 4.3 will add timing instrumentation to SmtcEventBridge.
The key used will be `smtc_dispatch_ms` (matching the existing `_poll_ms`
underscore naming pattern).

---

## Surface 03 mockup vs STEP 7.1 locked list delta

**Surface 03 mockup items (V14_S7_REPLAN_MOCKUPS.md):**
1. Header row: "Master's FM * v14.0.0" (app header, not in locked list)
2. Platform Detection
3. Audio Source
4. Customize Overlay
5. Discord Rich Presence [toggle]
6. Start on Login [toggle]
7. **OBS Overlay Added [toggle]** -- mockup shows as IS-ENABLED toggle
8. **Live Audio Visualizer [toggle]** -- in mockup, NOT in locked list
9. Patch Notes
10. View Log
11. Check for Updates (state-driven label)
12. Restart Master's FM
13. Quit Master's FM

**STEP 7.1 locked list changes vs mockup:**
- Replaces header row with **now-playing row DataTemplate** (Artist + Track + 24x24 art thumbnail)
- Removes "Live Audio Visualizer" toggle entirely (not in locked list for 7.6)
- Changes OBS row to `IsEnabled=false` placeholder (deferred to 7.8)
- Sentence case applied to all labels
- 12 items total (plus separators)

**Label text from mockup vs sentence case (STEP 11.3):**
- "Platform Detection" -> "Platform detection"
- "Audio Source" -> "Audio source"
- "Customize Overlay" -> "Customize overlay"
- "Start on Login" -> "Start on login"
- "Patch Notes" -> "Patch notes"
- "View Log" -> "View log"
- "Check for Updates" -> "Check for updates"
- "Discord", "OBS", "Restart", "Quit" -> unchanged (design language carve-out)

---

## App.xaml.cs DI registrations relevant to STEP 7

TrayMenuViewModel is NOT yet registered. Stage 7.6 STEP 7 adds it.
MainWindow injection: constructor takes `ILogger logger` only.
After 7.6, MainWindow will also need `IDialogService`, `IDiscordToggleService`,
`IAutoStartService`, `IUpdateCheckService`, `ICustomizerLauncher`, `NowPlayingViewModel`,
`TrayMenuViewModel` -- per STEP 7/8/9 wiring.

Actually per brief STEP 7.3: TrayMenuViewModel is a new singleton;
the ContextMenu DataContext = TrayMenuViewModel. The ContextMenu is
declared in XAML so TrayMenuViewModel is bound via the DataContext
chain, NOT injected into MainWindow constructor directly. MainWindow
needs the TrayMenuViewModel instance to set the DataContext on the
ContextMenu or pass it via DataContext=. Since XAML's ContextMenu can't
easily receive DI-resolved objects via XAML alone, the approach will be
to have MainWindow take TrayMenuViewModel via DI (constructor) and set
`NotifyIcon.ContextMenu.DataContext = _trayMenuViewModel` in OnLoaded.

---

End of inventory.
