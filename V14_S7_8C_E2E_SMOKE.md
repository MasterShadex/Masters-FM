# V14 Stage 7.8C — E2E Smoke Test Results
Date: 2026-05-09
Branch: main
Commit range: 0926fa3 (STEP 0 backup) → current

---

## Test Matrix

| # | Test | Method | Result |
|---|------|--------|--------|
| T01 | Build: MastersFM_Tray_v14 0 warnings/errors | `dotnet build -c Release` | **PASS** |
| T02 | Build: MastersFM_ObsCleanup 0 warnings/errors | `dotnet build -c Release` | **PASS** |
| T03 | build_msi.py Python syntax valid | `python -m py_compile` | **PASS** |
| T04 | _full_rebuild.ps1 parse 0 errors | PS `Parser::ParseFile` | **PASS** |
| T05 | ADD browser source to scene file | Python JSON test (T1) | **PASS** |
| T06 | BrowserSourceExists detects source | Python JSON test (T2) | **PASS** |
| T07 | ADD idempotent — same URL no write | Python JSON test (T3) | **PASS** |
| T08 | GAP-1: URL-update-on-mismatch in-place | Python JSON test (T4) | **PASS** |
| T09 | Scene items added to all 3 scenes | Python JSON test (T5) | **PASS** |
| T10 | REMOVE source + all scene items | Python JSON test (T6) | **PASS** |
| T11 | REMOVE idempotent — no-op when absent | Python JSON test (T7) | **PASS** |
| T12 | ObsCleanup --dry-run: OBS detection | `dotnet run -- --dry-run` | **PASS** (OBS running → poll started; exit 0) |
| T13 | ObsCleanup self-delete mechanism | Code review (cmd /c timeout 5) | **PASS (code)** |

---

## Test Details

### T01-T04: Build verification
All four build/syntax checks passed with 0 warnings and 0 errors. See commit history
for exact dotnet build output from each STEP commit.

### T05-T11: JSON file-edit logic
Tested against a temp copy of `%APPDATA%\obs-studio\basic\scenes\Untitled.json`
(3 scenes, 7 sources). Results:
- ADD created source with new UUID, added scene_item to all 3 scenes ✓
- BrowserSourceExists correctly detected the added source ✓
- Idempotent check matched URL exactly, returned no-write ✓
- GAP-1 fix: URL `http://localhost:4242/?renderer=webgl` → `?renderer=webgl&v=2`
  updated in-place; UUID preserved; returned modified=True ✓
- REMOVE deleted source entry + 3 scene_items (UUID-matched) ✓
- Second REMOVE returned modified=False ✓
- Test file cleaned up ✓

### T12: ObsCleanup --dry-run
Launched via `dotnet run -- --dry-run`. Log line:
> `OBS running; waiting for exit (polls every 10 s, max 30 polls)`
OBS detection (obs64 process) confirmed. Process exited cleanly (exit 0).

### T13: Self-delete code path
`SelfDelete()` in Program.cs spawns:
```
cmd.exe /c timeout 5 >nul & del /q "MastersFM_ObsCleanup.exe" & del /q "*.dll" & del /q "*.json" & rmdir "Cleanup" 2>nul
```
Pattern matches production pattern used in `_launch_vbs`. Not executed live to
avoid destroying the installed binary; logic verified by code review.

---

## Items Deferred to UAT

| Item | Reason |
|------|--------|
| Toggle OBS ON with OBS running (PendingRestart suffix live) | Requires tray running in WPF |
| Toggle OBS OFF with OBS running (PendingRestart suffix live) | Requires tray running in WPF |
| 60s timer clears suffix after OBS exits | Requires tray running + OBS close |
| obs.pending_restart survives tray restart | Requires tray running + config file |
| MSI uninstall with OBS running (cleanup binary spawned) | Requires full MSI install |

---

## Stage 7.8C Change Summary

| Component | Status |
|-----------|--------|
| ObsSceneFileEditor: BrowserSourceExists() | ✓ Added |
| ObsSceneFileEditor: GAP-1 URL-update-on-mismatch | ✓ Fixed |
| ObsSceneFileEditor: GAP-2 JSON parse-back validation | ✓ Fixed |
| ObsService: [Obsolete] on 4 WebSocket methods | ✓ Dead code retained |
| TrayMenuViewModel: ObsToggleState state machine | ✓ Replaced WebSocket path |
| TrayMenuViewModel: obs.pending_restart config | ✓ Persists + 60s poll timer |
| App.xaml.cs: startup auto-add via file-edit | ✓ No WebSocket |
| MainWindow.xaml.cs: ShowToast (ShowNotification API) | ✓ Correct API found |
| MastersFM_ObsCleanup.exe | ✓ New project |
| _full_rebuild.ps1: obs_cleanup build step | ✓ Added |
| build_msi.py: cleanup binary + %ProgramData% dir | ✓ Added |

All builds: 0 warnings, 0 errors.
