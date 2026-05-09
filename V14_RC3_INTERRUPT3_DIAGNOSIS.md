# V14_RC3_INTERRUPT3_DIAGNOSIS.md
Stage 7.10 INTERRUPT #3: pre-fix diagnostic
Date: 2026-05-09

---

## 1. Audit A heartbeat findings -- confirmation

All four Audit A gap findings hold in the current source tree.

### 1a. HeartbeatService is absent

No file matching `*HeartbeatService*` or `*PositionHeartbeat*` exists in
`src/tray_csharp`. The only heartbeat class is `DiagnosticHeartbeat.cs`,
confirmed in Section 2 of Audit A to write exclusively to the log file with
no HTTP call. No dedicated periodic-position-webhook class was added between
Audit A and this read.

### 1b. TrackResolver.CurrentTrack getter exists (confirmed)

`src/tray_csharp/Services/TrackResolver.cs:26-29`

```
public TrackUpdate? CurrentTrack
{
    get { lock (_lock) return _current; }
}
```

The lock-guarded getter is present and accessible. A new HeartbeatService
can call it without touching the dedup path.

### 1c. Dedup gate at TrackResolver.cs:64-68 confirmed

`src/tray_csharp/Services/TrackResolver.cs:64-68`

```
if (!isNew)
{
    _telemetry.IncrementCounter("track_dedup_hits");
    return;
}
```

Any `OnTrackChanged` call carrying the same `IdentityKey` (source|||artist|||track)
as `_currentKey` hits this early-return. The webhook line at
`TrackResolver.cs:90` (`_ = _webhook.SendTrackUpdateAsync(update, CancellationToken.None)`)
is unreachable for same-track position or pause updates. A heartbeat MUST
bypass this gate by calling `_webhook.SendTrackUpdateAsync` directly, or a
second send path must be introduced.

### 1d. `seek` absent from `TrackUpdate.cs`

`src/tray_csharp/Detectors/TrackUpdate.cs:1-27` -- full field list:
`Source`, `Artist`, `Track`, `Album`, `Duration`, `Position`, `IsPlaying`,
`ArtUri`, `PlatformIdentity`, `ObservedUtc`, `Metadata`. No `IsSeek` or
`Seek` property exists. Adding `bool IsSeek { get; init; }` to this record
is required before `BuildJsonPayload` can emit the `seek` field.

### 1e. `seek` absent from `BuildJsonPayload`

`src/tray_csharp/Services/WebhookClient.cs:84-104`

The `JsonObject` constructed in `BuildJsonPayload` contains: `source`,
`artist`, `track`, `album`, `duration`, `positionMs`, `isPaused`,
`trackArt`, `tray`, `observedUtc`. The `seek` field is absent -- not
written. The server's `data["seek"]` read will always resolve falsy for
v14 webhooks until this field is added.

### 1f. Only call site for `SendTrackUpdateAsync`

`src/tray_csharp/Services/TrackResolver.cs:90`

`_ = _webhook.SendTrackUpdateAsync(update, CancellationToken.None);`

This is the sole call site in the codebase. No other class calls
`SendTrackUpdateAsync` directly. A heartbeat must add a second call site.

---

## 2. Issue 7 -- Tray left-click (root cause)

### 2a. `LeftClickCommand` absent from `TaskbarIcon` in MainWindow.xaml

`src/tray_csharp/MainWindow.xaml:22-27` -- the full `TaskbarIcon` element:

```xml
<tb:TaskbarIcon x:Name="NotifyIcon"
                IconSource="pack://application:,,,/MastersFM_Tray_v14;component/assets/MastersFM.ico"
                ToolTipText="Master's FM v14"
                NoLeftClickDelay="True">
```

`LeftClickCommand`, `TrayLeftMouseDownCommand`, `TrayMouseDoubleClickCommand`,
and any equivalent click-handling attribute are absent. The element has only
`x:Name`, `IconSource`, `ToolTipText`, and `NoLeftClickDelay`. `NoLeftClickDelay="True"`
is present (suppresses the 500ms delay before the left-click fires) but there
is no command for it to fire.

### 2b. `ShowMenuCommand` absent from TrayMenuViewModel.cs

`src/tray_csharp/ViewModels/TrayMenuViewModel.cs:158-292` -- all commands
defined via `[RelayCommand]`:
`OpenPlatformDetectionAsync`, `OpenAudioSourceAsync`, `OpenCustomizer`,
`ToggleDiscord`, `ToggleAutoStart`, `ToggleObsAsync`, `OpenPatchNotesAsync`,
`OpenLog`, `CheckUpdatesAsync`, `RestartApp`, `QuitApp`.

No `ShowMenu`, `ShowContextMenu`, `OpenMenu`, or equivalent command is
defined. The ContextMenu open-by-left-click behavior requires either a
`LeftClickCommand` on the `TaskbarIcon` element wired to a new
`ShowMenuCommand`, or use of the `Hardcodet.Wpf.TaskbarNotification`
library's built-in `ShowContextMenuOnClick` property (not currently set).

### 2c. `NotifyIcon` field name for fix wiring

The `TaskbarIcon` is declared with `x:Name="NotifyIcon"` at
`MainWindow.xaml:22`. In code-behind the field is accessed as `NotifyIcon`
(confirmed by `MainWindow.xaml.cs:39`, `MainWindow.xaml.cs:66`,
`MainWindow.xaml.cs:97`). The fix should add `LeftClickCommand="{Binding ShowMenuCommand}"`
to the `TaskbarIcon` element at `MainWindow.xaml:22` and add the corresponding
`[RelayCommand] ShowMenu()` method to `TrayMenuViewModel.cs`.

---

## 3. Issue 5 -- Windows not draggable (root cause)

All 5 dialog XAML files declare `WindowStyle="None"` (confirmed below). WPF
windows with `WindowStyle="None"` lose the OS chrome title bar and therefore
lose the native OS drag handle. `MouseLeftButtonDown` calling `DragMove()`
must be wired manually on the custom title-bar element. None of the 5
code-behinds contain any `DragMove`, `MouseLeftButtonDown`, or mouse-drag
handler.

| Dialog | File | WindowStyle=None (line) | Title-bar element (Grid Row 0) | x:Name on title bar | DragMove present |
|---|---|---|---|---|---|
| WelcomeWindow | Dialogs/WelcomeWindow.xaml | line 14 | `<Grid Grid.Row="0" Margin="0,0,0,16">` at line 25 | None | No |
| AudioDeviceWindow | Dialogs/AudioDeviceWindow.xaml | line 13 | `<Grid Grid.Row="0" Margin="0,0,0,8">` at line 24 | None | No |
| PlatformsWindow | Dialogs/PlatformsWindow.xaml | line 13 | `<Grid Grid.Row="0" Margin="0,0,0,16">` at line 22 | None | No |
| SetupWizardWindow | Dialogs/SetupWizardWindow.xaml | line 14 | `<Grid Grid.Row="0" Margin="0,0,0,16">` at line 24 | None | No |
| ErrorDialogWindow | Dialogs/ErrorDialogWindow.xaml | line 14 | `<Grid Grid.Row="0" Margin="0,0,0,12">` at line 25 | None | No |

None of the 5 code-behinds contain `DragMove` or `MouseLeftButtonDown`:
`WelcomeWindow.xaml.cs`, `AudioDeviceWindow.xaml.cs`, `PlatformsWindow.xaml.cs`,
`SetupWizardWindow.xaml.cs`, `ErrorDialogWindow.xaml.cs` -- all confirmed read,
none contain those strings.

Fix target for each: add `MouseLeftButtonDown="OnTitleBarDrag"` to the
`<Grid Grid.Row="0">` element in each XAML and add a shared handler (or
individual handlers) in each code-behind calling `DragMove()`. Since no
`x:Name` is present on any title-bar Grid, the XAML edit must add
`MouseLeftButtonDown` as an event attribute on the existing Grid element.
No rename of the element is needed; the event attribute is sufficient.

`SetupWizardWindow.xaml:11` also has `WindowStartupLocation="CenterScreen"`
(first-run wizard is not owner-parented). The other four all use
`WindowStartupLocation="CenterOwner"` (WelcomeWindow.xaml:11,
AudioDeviceWindow.xaml:9, PlatformsWindow.xaml:9, ErrorDialogWindow.xaml:10).

---

## 4. Issue 4 -- Dialogs wrong monitor (root cause)

All 4 non-wizard dialogs are shown with `window.Owner = Application.Current.MainWindow`
set before `window.ShowDialog()`.

| Dialog | Owner set | File:line | WindowStartupLocation | File:line |
|---|---|---|---|---|
| WelcomeWindow | `window.Owner = Application.Current.MainWindow` | DialogService.cs:40 | `CenterOwner` | WelcomeWindow.xaml:11 |
| AudioDeviceWindow | `window.Owner = Application.Current.MainWindow` | DialogService.cs:54 | `CenterOwner` | AudioDeviceWindow.xaml:9 |
| PlatformsWindow | `window.Owner = Application.Current.MainWindow` | DialogService.cs:73 | `CenterOwner` | PlatformsWindow.xaml:9 |
| ErrorDialogWindow | `window.Owner = Application.Current.MainWindow` | DialogService.cs:105 | `CenterOwner` | ErrorDialogWindow.xaml:10 |
| SetupWizardWindow | `window.Owner = Application.Current.MainWindow` | DialogService.cs:88 | `CenterScreen` | SetupWizardWindow.xaml:11 |

The Owner IS set in all five cases. `WindowStartupLocation="CenterOwner"` on
four dialogs means WPF centers the dialog relative to the owner window.

The root cause is that `Application.Current.MainWindow` is the hidden host
window at `Top=-1000, Left=-1000` (`MainWindow.xaml:13-14`). WPF resolves
`CenterOwner` relative to the owner's screen position. A hidden window at
coordinates (-1000, -1000) is positioned off the primary monitor. WPF
computes the center of the owner as (-1000 + Width/2, -1000 + Height/2)
where `Width=0, Height=0` (`MainWindow.xaml:11-12`). The result is
approximately (-1000, -1000) -- off-screen, likely clamped to the primary
monitor's nearest edge or to (0,0) depending on the WPF DPI context and
monitor configuration.

`SetupWizardWindow` uses `CenterScreen` (not `CenterOwner`) and would
correctly center on the primary monitor. The four `CenterOwner` dialogs will
misbehave on any multi-monitor configuration where the primary monitor is not
the leftmost/topmost display.

Fix: change `window.Owner = Application.Current.MainWindow` to resolve the
correct "active" window, or change `WindowStartupLocation` to `CenterScreen`
for each dialog, or compute the active monitor and set `Left`/`Top` manually.
The simplest fix that matches the PS tray's behavior (which always opened
dialogs near the tray icon) is `CenterScreen` for all four dialogs, replacing
the `CenterOwner` + hidden-host strategy.

---

## 5. Issue 3 -- Check for Updates no overlay (root cause)

**Failure mode: A -- wiring gap.**

### 5a. UpdateProgressWindow.xaml is present

`src/tray_csharp/Update/UpdateProgressWindow.xaml` exists (confirmed read).
The XAML is complete and well-formed with a progress bar, status text,
Authenticode badge, and four action buttons. `UpdateProgressWindow.xaml.cs`
exists and wires `DataContext = viewModel` in the constructor at
`UpdateProgressWindow.xaml.cs:17`.

`UpdateCheckViewModel.cs` exists (confirmed read) -- 168 lines, full state
machine with `[RelayCommand]` commands, Dispatcher marshalling, and computed
properties for all XAML bindings.

`UpdateProgressWindow` is registered in DI as transient at `App.xaml.cs:114`:
`collection.AddTransient<UpdateProgressWindow>();`

### 5b. No code shows UpdateProgressWindow

The entire `TrayMenuViewModel.CheckUpdatesAsync` command at
`TrayMenuViewModel.cs:237-260` contains:

```
await _updateService.CheckNowAsync();  // or DownloadAsync / InstallAsync
```

There is no call to any `IDialogService` method, no call to
`_services.GetRequiredService<UpdateProgressWindow>()`, no `ShowDialog()`,
and no `Show()`. The command drives the state machine but never opens
the `UpdateProgressWindow`. The `UpdateProgressWindow` and
`UpdateCheckViewModel` are unreachable from any user-facing code path.

### 5c. IDialogService has no ShowUpdateProgressAsync method

`src/tray_csharp/Dialogs/IDialogService.cs:11-30` declares five methods:
`ShowWelcomeAsync`, `ShowAudioDeviceAsync`, `ShowPlatformsAsync`,
`ShowSetupWizardAsync`, `ShowErrorAsync`. No `ShowUpdateProgressAsync` or
equivalent exists. `DialogService.cs` implements only those five methods
(confirmed read, 127 lines).

### 5d. Menu click call chain

```
User click on "Check for updates" menu item
  -> MainWindow.xaml:114 Command="{Binding CheckUpdatesCommand}"
  -> TrayMenuViewModel.CheckUpdatesAsync() at TrayMenuViewModel.cs:237
  -> _updateService.CheckNowAsync() at TrayMenuViewModel.cs:248
  -> UpdateCheckService.CheckNowAsync() fires HTTP fetch, transitions state
  -> UpdateStateChangedEventArgs fires to TrayMenuViewModel.OnUpdateStateChanged
     at TrayMenuViewModel.cs:103
  -> UpdateLabel property updated at TrayMenuViewModel.cs:109/111
  -> Menu item header text updates (state-driven label)
  -> [END -- UpdateProgressWindow.Show() is never called]
```

The `UpdateCheckViewModel.StateChanged` subscription at
`UpdateCheckViewModel.cs:75` (`service.StateChanged += OnStateChanged`) is
also correctly wired -- but `UpdateCheckViewModel` is a singleton that is
never exposed to any window. The window that would display it
(`UpdateProgressWindow`) is never shown.

Fix: add `ShowUpdateProgressAsync()` to `IDialogService` and
`DialogService`, register `UpdateProgressWindow` resolution in
`DialogService`, and call `ShowUpdateProgressAsync()` from
`TrayMenuViewModel.CheckUpdatesAsync()` instead of (or alongside) the
direct `_updateService` method calls.

---

## 6. Issues 1+8 baseline confirmation (TrackUpdate.cs + WebhookClient.cs)

### 6a. `IsSeek` absent from TrackUpdate.cs

`src/tray_csharp/Detectors/TrackUpdate.cs:9-27` -- confirmed full record
field list contains no `IsSeek`, `Seek`, or seek-related property.
The `IdentityKey` computed property at `TrackUpdate.cs:23-26` is the only
non-storage member. `IsSeek` does not exist. Pre-fix baseline confirmed.

### 6b. `seek` absent from `BuildJsonPayload`

`src/tray_csharp/Services/WebhookClient.cs:91-103` -- the `JsonObject`
contains exactly 10 keys: `source`, `artist`, `track`, `album`, `duration`,
`positionMs`, `isPaused`, `trackArt`, `tray`, `observedUtc`. The `seek`
key is absent. Pre-fix baseline confirmed.

### 6c. Only call site for `SendTrackUpdateAsync`

`src/tray_csharp/Services/TrackResolver.cs:90`

`_ = _webhook.SendTrackUpdateAsync(update, CancellationToken.None);`

This line is inside `OnTrackChanged`, below the dedup gate at
`TrackResolver.cs:64-68`. It is the only call to `SendTrackUpdateAsync`
in the codebase. No other class calls this method.

---

## 7. Deviations from brief's Architectural Decisions

### Issue 4 (dialogs wrong monitor)

Audit A Section 4 did not cover this issue. The diagnosis above shows that
`Owner` IS set on all dialogs (DialogService.cs:40, 54, 73, 88, 105), so the
naive reading "Owner is never set" is incorrect. The actual cause is that the
owner is the hidden host window at (-1000, -1000) with Width=0 Height=0, which
makes `CenterOwner` behave identically to off-screen placement. This is a
subtler bug than a missing Owner assignment.

Fix implication: the recommended fix (change four dialogs from `CenterOwner`
to `CenterScreen`) does not require touching DialogService.cs or any Owner
assignment. It requires only `WindowStartupLocation` changes in four XAML
files. This is simpler than the "set Owner to active monitor window" approach
but does not replicate the PS tray's near-icon dialog placement. Acceptable
for rc.3 scope.

### Issue 3 (no update overlay)

The fix requires a new `IDialogService` method and a `DialogService`
implementation addition. This adds a 6th method to the interface defined at
`IDialogService.cs`. The interface is not marked sealed or frozen; adding to
it is straightforward. However, `UpdateProgressWindow.xaml.cs:14-18` already
takes `UpdateCheckViewModel` in its constructor -- so the DI transient
registration at `App.xaml.cs:114` already injects the correct VM
automatically. `DialogService` needs only to call
`_services.GetRequiredService<UpdateProgressWindow>()` and `window.ShowDialog()`.

All other architectural decisions from Audit A Section 4 (heartbeat in
TrackResolver, 2 s cadence, bypass dedup gate, DispatcherTimer, seek field
addition) remain confirmed viable with no contradicting evidence found.

---

## 8. Open questions surfaced

**OQ-7: UpdateProgressWindow -- modal or non-modal?**
`ShowDialog()` is modal and blocks until the window closes. The update
progress flow includes long-running async operations (download). Showing the
window modally while `DownloadAsync` runs on the background HttpClient is
correct WPF pattern (download is async; the modal window can update its
progress bar via `StateChanged`). However, if the user closes the window
mid-download, `UpdateProgressWindow.xaml.cs:21-30` prevents close during
Downloading/Installing states. Confirm that the fix uses `ShowDialog()` (not
`Show()`) and that the `IDialogService.ShowUpdateProgressAsync` signature
should return `Task` (not `Task<bool>`) since Install triggers shutdown
rather than a result.

**OQ-8: SetupWizardWindow drag behavior**
`SetupWizardWindow.xaml` uses `WindowStartupLocation="CenterScreen"` (not
`CenterOwner`). It is unaffected by the Issue 4 wrong-monitor bug but is
still non-draggable (Issue 5). Its header Grid at `SetupWizardWindow.xaml:24`
has no `x:Name` and no drag wiring. Drag fix applies equally to all 5 dialogs
including the wizard.

**OQ-9: UpdateProgressWindow WindowStartupLocation**
`UpdateProgressWindow.xaml:14` uses `WindowStartupLocation="CenterScreen"`.
This means the update window will appear on the primary monitor regardless of
which monitor the user is working on. Given Issue 4's fix direction
(switch all dialogs to `CenterScreen`), this is consistent. No change
needed for `UpdateProgressWindow.xaml` itself, but the question remains
whether the project wants a near-tray-icon dialog style for the update
window in a future stage.

**OQ-10: `LeftClickCommand` vs. `ShowContextMenuOnClick` for Issue 7**
The `Hardcodet.Wpf.TaskbarNotification` library supports a built-in property
`TaskbarIcon.ShowContextMenuOnClick` (or `TaskbarIcon.LeftClickCommand`
depending on the library version in use). If the library version registered
in the project supports `ShowContextMenuOnClick="True"`, that is a 1-line
XAML fix with no ViewModel change required. If it requires a `LeftClickCommand`,
a new `ShowMenuCommand` on `TrayMenuViewModel` must programmatically open the
`ContextMenu`. The fix shape depends on the Hardcodet library API available
in this build. The `tb:` namespace is declared as
`xmlns:tb="http://www.hardcodet.net/taskbar"` at `MainWindow.xaml:9`. The
library version should be checked in the `.csproj` before choosing the fix
path.
