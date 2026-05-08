# V14_S7_S7_7_DIALOG_INVENTORY.md

Stage 7.7 STEP 1 deliverable. Source-cited inventory of the 6 dialog
surfaces (Surface 04 / 05 / 06 / 09 / 10 / 11).

---

## Surface 04 -- Welcome / Patch Notes (`tray.ps1:1644-2191`)

**Function**: `Show-WelcomeDialog` at `tray.ps1:1644`.

**Form geometry**: 780x760 fixed (line 1651). Borderless (line 1653).
Background ARGB(255, 10, 3, 22) (line 1654). TopMost (line 1655).
ShowInTaskbar (line 1656).

**Patch notes data source**: `$script:PATCH_HISTORY` array at
`tray.ps1:261-1445`. 292 version entries; 1185 lines; 339 KB raw size.
Each entry has Version, Date, Notes[]. Each note has Tag and Text.
Tags observed: `NEW`, `FIXED`, `IMPROVED`, `ADDED`, `REMOVED`,
`PRESERVED`, `BREAKING` (rare).

**Performance critical**: PS pre-v12.0.1 took 10-13 seconds to render
the welcome dialog because it created 683 individual WinForms Label
controls in a foreach loop. v12.0.1 (per `tray.ps1:297` patch entry)
replaced with owner-draw scrollable panel: pre-flatten PATCH_HISTORY
into a row layout array (text measurements only, no controls), Paint
event handler renders ONLY the rows that intersect the clip rectangle
on each WM_PAINT.

**WPF port equivalent**: `VirtualizingStackPanel` (default in `ListBox`
/ `ListView`). One-liner instead of 80 lines of paint code.

**`-Manual` parameter**: switch parameter at `tray.ps1:1645`.
Differentiates first-run-from-setup-wizard path (no manual) from
manual open from tray menu (manual=$true).

**First-run countdown**: per mockup, first-run path has auto-dismiss
countdown (5 seconds default per Q-MOCK-04c). PS implementation:
`tray.ps1` source uses tickTimer for the countdown in welcome dialog.

**Welcome-seen tracking**: legacy `welcome_seen` bool flag plus
authoritative `welcome_seen_version` string in config (per 7.4 schema).
On first run with new version, re-show; otherwise skip.

**WPF port plan**: single window `WelcomeWindow.xaml` with TabControl:
- "What's New" tab: virtualized ListBox bound to ObservableCollection<PatchNoteEntry>
- "About" tab: AboutViewModel content (Surface 10 embedded per Q-MOCK-10a=A)

Patch notes loaded from embedded JSON resource `patch_notes.json`
(extracted from tray.ps1 PATCH_HISTORY at brief-start time; 332 KB).

---

## Surface 05 -- Audio device dialog (`tray.ps1:2192-3114`)

**Function**: located via section break at `tray.ps1:2192` (922
lines total).

**Backends per mockup**: WASAPI / MME / WDM-KS / ASIO. PS implementation
uses NAudio for enumeration (CoreAudioApi.MMDeviceEnumerator).

**Constraint**: NAudio is NOT in the C# tray's NuGet stack (per
ABSOLUTE RULE 4 no new NuGets). Use **Windows.Devices.Enumeration**
WinRT API (already projected via TFM 19041). Specifically
`Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(DeviceClass.AudioRender)` for
output devices and `DeviceClass.AudioCapture` for input devices.

**Device row data**: Name, Id, IsDefault, IsAsio, IsStereoMix, etc.

**ASIO**: WinRT does not expose ASIO devices directly. ASIO is a
proprietary driver class that uses dedicated APIs not in WinRT.
**Decision**: hide ASIO tab if WinRT enumeration finds no ASIO
backend (Q-MOCK-05a default). For 7.7, the ASIO tab will be hidden
(0 devices) since WinRT doesn't surface ASIO; this matches the
mockup's Q-MOCK-05a default ("hide tab if device count zero").

**Stereo Mix banner**: Q-MOCK-05b default = show banner if Stereo
Mix device exists but is disabled. WinRT
`DeviceInformation.IsEnabled` indicates state. Banner has
"Open Windows Sound" deep-link button.

**Refresh button**: per Q-MOCK-05c keep in footer.

**Selection persistence**: writes to config `audio.outputDevice` (or
similar key per 7.4 schema).

---

## Surface 06 -- Platforms dialog (`tray.ps1:3115-3408`)

**Section**: `tray.ps1:3115` (293 lines).

**Platforms list** (verified against PS source):
1. SoundCloud (browser tab + soundcloud-rpc bridge)
2. Spotify (desktop app via SMTC)
3. Browser (generic web tabs)
4. SMTC-generic (Windows 10/11 Media; catches anything publishing
   to SMTC)
5. Win11 Media Player (Win11 Media app)
6. osu! (gap-filler via window title)
7. VLC (gap-filler via HTTP control)
8. WMP-legacy (gap-filler via window title; consolidates older WMP
   detection paths)

**Total: 8 platforms.**

**PS first-run defaults**: SoundCloud / Spotify / Browser / SMTC ON;
osu / WMP / VLC OFF (per `V14_S7_REPLAN_MOCKUPS.md` Surface 06 line
426 "Reset to defaults").

**7.7 first-run defaults per Q-MOCK-06a=A2**: ALL ON. Implementation:
PlatformsViewModel reads from config; if `welcome_seen=false` (first
run), writes all toggles=true to config BEFORE showing the dialog.

**Per-row layout per mockup**: 56-DIP each. 32-DIP brand icon (left),
two-line label `--type-body` + `--type-caption` `--text-tertiary`,
trailing WPF-UI ToggleSwitch.

**No master "Disable all" toggle** (Q-MOCK-06c default).

**No "Reset to defaults" button per 7.7**: the reset action is the
all-ON enforcement on first run. Subsequent edits respect user choice;
no separate reset button needed.

**Helper text concise** (Q-MOCK-06b default).

---

## Surface 09 -- Setup wizard (`tray.ps1:1638` `Show-SetupDialog`)

**Function**: `tray.ps1:1638` is currently a stub (`return $null`)
because Last.fm was removed (per `tray.ps1:1639` comment "Last.fm
removed"). The wizard logic is implicit: PS tray's first-run path
shows Welcome dialog (if not seen) -> Audio dialog (if not configured)
-> Platforms dialog (if not configured), each as a separate modal.

**7.7 implementation per Q-MOCK-09b=A**: orientation-first sequence:
Welcome -> Audio -> Platforms.

**Single-window approach** (Q-MOCK-09a default): `SetupWizardWindow`
with internal Frame/ContentControl that swaps between three embedded
views (re-skinned variants of Welcome / Audio / Platforms).

**Step indicator**: "Step X / 3" chip in `--bg-elevated-2` per mockup.

**Forward / Back navigation**: handled by SetupWizardViewModel.

**Completion**: writes `welcome_seen=true` + `welcome_seen_version=14.0.0-rc.1`
to config, closes wizard, returns true.

**Skip / Cancel**: writes defaults (all-ON platforms) but does not
mark welcome_seen=true. Returns false. Tray icon initializes
regardless.

---

## Surface 10 -- About panel (within Welcome)

**Source today**: NONE. PS tray has no dedicated About surface.
Version is shown in tray menu header (`tray.ps1:4700`) and Welcome
dialog header (`tray.ps1:1646-1647`).

**7.7 implementation per Q-MOCK-10a=A**: embedded as a tab inside
`WelcomeWindow.xaml`. NOT a separate window in 7.7 (matches mockup
default).

**Content**:
- Brand logo (96 DIP) + version + build date
- Author / credit (Created by Orken; orken.ae)
- .NET 8 runtime version (from `Environment.Version`)
- Discord RPC client_id (per Q-MOCK-10b default visible)
- Authenticode CN (read from current process exe via
  `X509Certificate.CreateFromSignedFile(Process.GetCurrentProcess().MainModule.FileName)`)
- Action row: GitHub link, Report bug link, Open data folder button
  (opens %LOCALAPPDATA%\MastersFM\)

---

## Surface 11 -- Error / crash dialog

**Source today** (~10 sites in `tray.ps1`):
- `tray.ps1:5122` "Master's FM - Error" balloon (server stop failure)
- `tray.ps1:4806` "Restart failed" balloon
- Various `try`/`catch` that bubble to `LogErr` only

**7.7 implementation**: `ErrorDialogWindow.xaml` consolidates these.
Q-MOCK-11a default: info icon + brand purple (NOT destructive red).

**Content**:
- Title: "Master's FM ran into a problem"
- Message: human-readable summary (e.g., "Couldn't reach update server")
- Expander: "Show technical details" with stack trace
- Footer: "Copy details" + "Close" buttons (NO Submit Feedback per
  Q-MOCK-11b default)

**Wired in App.xaml.cs**: DispatcherUnhandledException handler shows
this dialog on UI thread crashes (instead of just logging).

---

## File count

| Category | Count |
|---|---:|
| Dialog windows (XAML + code-behind) | 5 windows x 2 = 10 files |
| ViewModels | 6 files |
| IDialogService + DialogService | 2 files |
| Embedded resource | 1 file (patch_notes.json) |
| Modifications | 5 files (App.xaml, App.xaml.cs, csproj, DiagnosticHeartbeat.cs, SmtcEventBridge.cs, TrackResolver.cs) |

**Total NEW: 19 files. MODIFY: 5-6 files.**
