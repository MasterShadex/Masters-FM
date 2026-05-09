# V14 Stage 7.8D — Final Report
Date: 2026-05-09  
Version: v14.0.0-rc.2 (unchanged; no version bump)  
HEAD at STEP 9: see commit log below

---

## Summary

Stage 7.8D fixed the operator-reported OBS overlay toggle bug: toggling OFF appeared to work
(menu cleared checkmark, source was removed from scene file) but the source was silently
re-added 5 seconds after every tray restart, and persisted across restarts.

Root cause was Mode D: `obs.enabled` in config was never written `false` by the toggle,
so the 5s startup auto-add in `App.xaml.cs` always fired and re-added the source.

The fix replaces the `obs.enabled` truth-conflation with a proper intent field (`obs.intent`),
adds UUID tracking (`obs.tray_added_uuid`), and makes `ReconcileAsync` the single source of
truth that derives state from intent × on-disk reality on every 60s tick (5s initial).

---

## Commits

| Hash | Step | Description |
|------|------|-------------|
| `c4e3f70` | STEP 0 | Checkpoint backup + pre-conditions verified |
| `270bc5f` | STEP 1 | Diagnosed OBS state machine bug (Mode D primary) |
| `f23c4e1` | STEP 2 | ObsSceneFileEditor: UUID tracking + scan + targeted-remove |
| `c768a2e` | STEP 3 | TrayMenuViewModel: intent-vs-reality state machine |
| `0e35e98` | STEP 4 | MainWindow.xaml binding verification (no XAML changes needed) |
| `1ac615c` | STEP 5 | App.xaml.cs: removed Stage 7.8C 5s startup auto-add block |
| `ef8d9e7` | STEP 6 | E2E smoke test report (9/9 PASS) |
| `b525546` | STEP 7 | Dual-build + SHA256 protected-file recheck |
| `2ef8084` | STEP 8 | memory.md APPEND |

---

## Files Changed

| File | Change |
|------|--------|
| `src/tray_csharp/Services/ObsSceneFileEditor.cs` | UUID-returning `AddBrowserSource`, `ScanForBrowserSources`, `RemoveBrowserSourceByUuid`, `AddBrowserSourceResult`/`BrowserSourceScanResult` records |
| `src/tray_csharp/Services/ObsService.cs` | `FallbackAdd()` return-type fix (`AddBrowserSourceResult` not `bool`) |
| `src/tray_csharp/ViewModels/TrayMenuViewModel.cs` | Full OBS state machine rewrite; `ReconcileAsync`, `AutoAddAsync`, `AutoRemoveAsync`, intent migration, `obs.tray_added_uuid` write |
| `src/tray_csharp/App.xaml.cs` | Removed 20-line Stage 7.8C 5s startup auto-add block |

**Protected files: UNCHANGED** — overlay.html, customize.html, tray.ps1, tray_native.cs, launcher.cs, server.js

---

## Root Cause Analysis

Three overlapping defects closed by Stage 7.8D:

### RCA-1 (PRIMARY): `obs.enabled` conflated intent with reality
`obs.enabled` was written `true` by `ObsService.ConnectAsync()` but never written `false`
by `ToggleObsAsync()`. Result: `obs.enabled` was permanently `true` once set.

**Fix:** `obs.intent` ("on"|"off") is written by every toggle. Migration on first 7.8D
launch: `obs.enabled=true` → `obs.intent="on"`. `obs.enabled` left as tombstone.

### RCA-2: 5s startup auto-add bypassed toggle intent
`App.xaml.cs` read `obs.enabled` (always `true`) and fired `AddBrowserSource` 5s after
every tray launch, undoing any toggle-OFF the user performed.

**Fix:** Block removed entirely from `App.xaml.cs`. `ReconcileAsync` is the sole auto-add path.

### RCA-3: No ground-truth reconciliation
After auto-add fired, `_obsToggleState` stayed `NotAdded`; no mechanism detected the scene
file changed. On restart, constructor re-read the file and picked up stale state.

**Fix:** `ReconcileAsync` reads both `obs.intent` AND current scene file on every tick, derives
the correct `ObsToggleState`, and fires `AutoAddAsync`/`AutoRemoveAsync` as needed.

---

## Architecture (Stage 7.8D Design)

### Config fields
| Field | Stage | Role |
|-------|-------|------|
| `obs.intent` | **7.8D NEW** | User intent: `"on"` or `"off"` |
| `obs.tray_added_uuid` | **7.8D NEW** | UUID of source added by this tray; protects foreign sources |
| `obs.enabled` | 7.8B tombstone | Left as-is after migration; ignored by 7.8D code |
| `obs.pending_restart` | 7.8C tombstone | No longer written; `PendingRestart` derived from mtime heuristic |

### State machine (ReconcileAsync truth table)
| intent | oursPresent | OBS running | fileNewer | → State |
|--------|-------------|-------------|-----------|---------|
| on | true | true | true | PendingRestart |
| on | true | * | false | Added |
| on | false | * | * | NotAdded + needAdd |
| on | false (foreign present) | * | * | NotAdded (ForeignSource — protected) |
| off | true | * | * | NotAdded + needRemove |
| off | false | * | * | NotAdded |

### Key code paths
- **Migration**: constructor reads `obs.intent`; if empty, reads `obs.enabled` → writes `obs.intent`
- **Timer**: `System.Threading.Timer(ReconcileAsync, 5s initial, 60s recurring)`
- **AutoAddAsync**: calls `AddBrowserSource(url, ..., knownTrayUuid)`; writes `obs.tray_added_uuid`; calls `ReconcileAsync` after
- **AutoRemoveAsync**: calls `RemoveBrowserSourceByUuid(uuid)`; calls `ReconcileAsync` after
- **SetObsToggleState**: dispatcher-safe; fires `OnPropertyChanged(nameof(IsObsEnabled))` explicitly for computed property

---

## Test Results

### E2E Smoke (live, installed build, 2026-05-09)

| # | Scenario | Log Evidence | Result |
|---|----------|-------------|--------|
| T1 | Config migration obs.enabled→obs.intent | `migrated obs.enabled=True → obs.intent=on` | **PASS** |
| T2 | AutoAdd on startup (no source) | `AutoAdd succeeded: uuid=38bbeccf...` | **PASS** |
| T3 | PendingRestart (OBS running, fileNewer) | `reconcile: ... → PendingRestart` | **PASS** |
| T4 | 60s poll maintains state | 3× consecutive PendingRestart ticks | **PASS** |
| T5 | Toggle-OFF: UUID-targeted remove | `AutoRemove succeeded: uuid=38bbeccf...` | **PASS** |
| T6 | Toggle-ON re-add | `AutoAdd succeeded: uuid=55bd9591...` | **PASS** |
| R1 | 5s auto-add absent from log | 0 matches for "auto-add complete" | **PASS** |
| R2 | obs.intent written (not obs.enabled) | obs.enabled stays `true` tombstone | **PASS** |
| R3 | Remove by UUID confirmed | log shows uuid= in AutoRemove | **PASS** |

**9/9 PASS**

---

## Build Verification

| Binary | Warnings | Errors |
|--------|----------|--------|
| MastersFM_Tray_v14 (`-c Release`) | 0 | 0 |
| MastersFM_ObsCleanup (`-c Release`) | 0 | 0 |

---

## Absolute Rules Compliance

| Rule | Status |
|------|--------|
| No protected file edits | ✓ Confirmed by `git diff HEAD` (empty) |
| No version bump | ✓ v14.0.0-rc.2 unchanged |
| No new NuGets | ✓ No .csproj changes |
| No push / no tag | ✓ Not pushed; 113 commits ahead of origin/main |
| No XAML changes needed | ✓ Bindings verified correct in STEP 4 |
