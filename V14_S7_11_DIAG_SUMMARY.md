# Stage 7.11 -- Diagnosis Summary

## Issues by priority (operator + Ruflo recommendation)

| # | Issue | Root cause (one line) | Fix complexity | Recommended priority |
|---|---|---|---|---|
| 6 | Patch Notes opens Setup Wizard | `OpenPatchNotesCommand` calls `ShowWelcomeAsync()` instead of a dedicated patch notes dialog | trivial (1-line ViewModel fix) | P0 |
| 7 | View Log opens folder | `OpenLog()` calls `Process.Start("explorer.exe", logDir)` (directory) instead of opening the log file directly | trivial (2-line fix) | P0 |
| 8 | Check for Updates wrong monitor | `ShowUpdateProgressAsync` in DialogService calls `_updateWindow.Show()` without `PositionDialogOnCursorMonitor` (the ONLY dialog missing it) | small (1-line DialogService fix) | P0 |
| 1 | Left-click tray wrong monitor | `ShowMenuCommand` uses `PlacementMode.Mouse` which reads stale WPF input position from hidden zero-size host window; right-click uses native Win32 `GetCursorPos()` | small (~5-10 lines MainWindow.xaml.cs) | P0 |
| 2 | Tray menu missing icons | `MenuItem.Icon` blocks absent for 4 items (Discord, Start on login, View log, Restart); 4 new PathGeometry resources needed in Icons.xaml | small (~30 lines Icons.xaml + XAML wiring) | P0 |
| 4 | KS/ASIO tab missing/hidden | KS: XAML tab defined, always visible, no enumeration (static placeholder only); ASIO: XAML tab defined but `HasAsio` always false -- remove Visibility gating to show informational message | trivial (1-line ASIO fix; KS tab already visible after issue 3 fix) | P0 (ASIO) / P1 (KS enumeration) |
| 3 | Alignment/layout bugs | B (confirmed): toast `Opacity=0` does not collapse 45px layout row; C (confirmed): footer no min gap between status text and Reset; A (tab truncation): partially inconclusive, requires live width measurement | B+C trivial; A small | P1 |
| 9 | OBS toggle doesn't stick | OBS (running) auto-saves scene file, overwriting tray's file-edit; reconcile re-adds on each 60s tick creating infinite loop; source lost permanently after OBS restart | medium (state machine fix: don't re-add while OBS running) | P0 (critical functional -- must fix before publish) |
| 10 | Discord RPC broken | Tray writes config flag but does NOT call `/reload-config`; server's DiscordRpcService only reloads via that endpoint; config change may not propagate; also depends on Discord being running | small (1-2 lines -- add /reload-config POST from tray) | P1 |
| 5 | Customize Overlay unchanged | Out of scope per all Stage 7.x briefs; v12 browser UI retained intentionally; already documented in known issues | large (full web UI rebuild) | P2 (scope decision) |

---

## Recommended fix sequence

### Batch A -- P0 trivial/small fixes (target: 1 brief, per-fix operator verification gates)

These are all clean, isolated fixes. Each takes 5-30 minutes of Ruflo time. Each can be verified in ~2 minutes by the operator on the installed build.

1. **Issue 6 -- Patch Notes wiring** (trivial)
   - TrayMenuViewModel: `OpenPatchNotesAsync` calls `_dialogService.ShowWelcomeAsync()` -- redirect to correct target (new dialog or release notes URL)
   - _Depends on: decision whether "Patch Notes" should show WelcomeWindow's About tab, or open a separate notes dialog_

2. **Issue 7 -- View Log target** (trivial)
   - TrayMenuViewModel: `OpenLog()` replace `Process.Start("explorer.exe", logDir)` with `Process.Start("explorer.exe", $"/select,\"{logFilePath}\"")`

3. **Issue 8 -- Check for Updates monitor placement** (small)
   - DialogService: `ShowUpdateProgressAsync` add `PositionDialogOnCursorMonitor(_updateWindow)` before `_updateWindow.Show()`

4. **Issue 1 -- Left-click menu monitor placement** (small)
   - MainWindow.xaml.cs: `OpenContextMenu` delegate -- replace `PlacementMode.Mouse` with cursor-position-based placement using `System.Windows.Forms.Cursor.Position` + `Screen.FromPoint` (same pattern as DialogService)

5. **Issue 2 -- Tray menu missing icons** (small)
   - Icons.xaml: add 4 new PathGeometry resources (Discord, Startup/Login, LogFile, Restart)
   - MainWindow.xaml: wire `<MenuItem.Icon>` blocks for the 4 missing items

6. **Issue 4 (ASIO only) -- ASIO tab always hidden** (trivial)
   - AudioDeviceWindow.xaml: remove `Visibility="{Binding HasAsio, ...}"` from ASIO TabItem (change to `Visibility="Visible"`)

### Batch B -- P0 critical functional (target: 1 brief, more complex)

**Issue 9 -- OBS toggle state machine fix** (medium)
- Reconcile: when `intent=on`, `obs=running`, `ours=False` → stay in PendingRestart, do NOT re-add
- Only re-add when `obs=NOT running` (safe to write without OBS overwriting)
- Fix UUID churn: preserve existing UUID in PendingRestart, don't generate new UUID on each reconcile tick
- Optional: detect OBS process exit and trigger immediate reconcile (add to OBS service state monitoring)

This MUST be resolved before rc.3 publishes. It is the most critical functional gap.

### Batch C -- P1 medium fixes (target: 1-2 briefs)

**Issue 3 -- Alignment/layout sweep**
- Defect B (toast height): `ToastBanner.Visibility = Collapsed` by default; code-behind sets Visible before animation, Collapsed after fade-out
- Defect C (footer gap): Add `Margin="12,0,0,0"` to Reset button or `MaxWidth` on StatusText
- Defect A (tab truncation): Requires live width measurement first; then likely `HorizontalAlignment="Left"` on tab TextBlock + `MinWidth` on TabItem

**Issue 10 -- Discord RPC toggle propagation**
- DiscordToggleService: after `SetValue(EnabledKey, value)`, add POST to `/reload-config`
- Investigate whether server.exe has an independent config watcher (read `server_dotnet/ConfigService.cs`)
- Confirm with operator that Discord was actually running during their test

**Issue 4 (KS) -- KS tab enumeration (OPTIONAL)**
- If operator wants real KS device listing: implement KS P/Invoke enumeration in AudioDeviceService, wire to KsDevices collection in AudioDeviceViewModel
- If not: keep existing static placeholder message (tab is visible after DIAG_03 fix resolves truncation)

### Batch D -- P2 scope decision (operator only)

**Issue 5 -- Customize Overlay visual rebuild**
- Operator decides: rebuild customize.html for v14 GA (Stage 7.12) OR keep v12 UI for v14 release (defer to v14.1.0)
- No code action pending operator decision

---

## Verification protocol per fix

For each fix in Batch A and B:
1. Ruflo lands the fix in its own commit
2. Ruflo runs `_full_rebuild.ps1` + installs MSI
3. Ruflo prints exact verification instruction to operator
4. Operator manually tests the specific issue on installed build
5. Operator replies PASS or FAIL in chat
6. PASS → next fix begins; FAIL → Ruflo investigates, no new fix until current one PASSes

This replaces structural-verification-as-proxy. Each operator verification takes ~2-5 minutes.

---

## Soak schedule

Soak only runs AFTER all Batch A + B fixes have operator-confirmed PASS. Current soak pass thresholds apply.

---

## GitHub rc.3 draft status

The current rc.3 draft on GitHub was built from commit `0a2ce62` (before any of these 10 fixes). After all fixes land, a new MSI build is required. Decision: either update the GitHub release with the new MSI (same tag, updated artifact), OR rebuild as rc.4 with a new tag. Recommend: rebuild as rc.4 to maintain clean tag-per-build hygiene.

---

## Diagnosis completeness

| # | Issue | Diagnosis file | Root cause status |
|---|---|---|---|
| 1 | Left-click wrong monitor | DIAG_01 | Confirmed |
| 2 | Tray menu missing icons | DIAG_02 | Confirmed |
| 3 | Alignment/layout bugs | DIAG_03 | Partially confirmed (B+C confirmed; A partially inconclusive) |
| 4 | KS/ASIO missing | DIAG_04 | Confirmed |
| 5 | Customize Overlay unchanged | DIAG_05 | Confirmed (not a bug; scope decision) |
| 6 | Patch Notes opens Setup Wizard | DIAG_06 | Confirmed |
| 7 | View Log opens folder | DIAG_07 | Confirmed |
| 8 | Check for Updates wrong monitor | DIAG_08 | Confirmed |
| 9 | OBS toggle doesn't stick | DIAG_09 | Confirmed (live log evidence) |
| 10 | Discord RPC broken | DIAG_10 | Partially confirmed (config propagation gap confirmed; Discord running state requires operator confirmation) |
