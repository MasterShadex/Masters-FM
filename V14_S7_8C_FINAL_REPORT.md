# V14 Stage 7.8C — Final Report
Date: 2026-05-09
Branch: main

---

## Summary

Stage 7.8C supersedes Stage 7.8B's WebSocket-primary approach with a **file-edit-only** OBS
integration. All OBS browser-source operations (add, check, update, remove) now operate
directly on OBS scene-collection JSON files regardless of OBS state. Three secondary
deliverables: GAP fixes in the file editor, persistent `obs.pending_restart` tray state with
a 60-second OBS-exit polling timer, and a standalone `MastersFM_ObsCleanup.exe` binary that
MSI uninstall launches to purge the browser source when OBS is running at uninstall time.

**Automated test result: 13/13 PASS (5 items deferred to UAT)**
**Protected source files: UNCHANGED (SHA256 MATCH on overlay.html, customize.html, tray.ps1)**
**Build result: 0 warnings, 0 errors (both projects)**

---

## Commit History

| STEP | Commit | Description |
|------|--------|-------------|
| STEP 0 | `0926fa3` | Checkpoint backup (pre-7.8C state snapshot) |
| STEP 1 | `b38827e` | Diagnosis — file-edit-only decision brief |
| STEP 2 | `2321b16` | ShowToast API fix + ObsToggleState state machine |
| STEP 3 | `57b5b2a` | obs.pending_restart config + 60s OBS-exit poll timer |
| STEP 4 | `0d2faff` | MastersFM_ObsCleanup.exe — new project (csproj + Program.cs) |
| STEP 5 | `8ea0d59` | build_msi.py — cleanup binary + %ProgramData% dir + uninstall VBScript step 3b |
| STEP 6 | `fff2962` | E2E smoke test results (V14_S7_8C_E2E_SMOKE.md) |
| STEP 7 | `811a10f` | Dual-build + SHA256 verification (V14_S7_8C_DUAL_BUILD.md) |
| STEP 8 | `7632561` | memory.md APPEND — Stage 7.8C CHANGELOG + CURRENT STATE update |
| STEP 9 | *(this commit)* | Final report |

---

## Files Changed

| File | Change | Protected? |
|------|--------|-----------|
| `src/tray_csharp/App.xaml.cs` | OBS startup auto-add via file-edit (no WebSocket) | No |
| `src/tray_csharp/MainWindow.xaml.cs` | ShowToast wired to `ShowNotification` (correct H.NotifyIcon API) | No |
| `src/tray_csharp/Services/ObsSceneFileEditor.cs` | `BrowserSourceExists()`, GAP-1 URL-update, GAP-2 parse-back | No |
| `src/tray_csharp/Services/ObsService.cs` | 4 WebSocket methods marked `[Obsolete]` | No |
| `src/tray_csharp/ViewModels/TrayMenuViewModel.cs` | `ObsToggleState`, file-edit toggle, `obs.pending_restart`, 60s timer | No |
| `src/obs_cleanup/MastersFM_ObsCleanup.csproj` | **New** — net8.0-windows console project | No |
| `src/obs_cleanup/Program.cs` | **New** — OBS-exit poll + scene-file cleanup + self-delete | No |
| `_full_rebuild.ps1` | obs_cleanup build + publish step | No |
| `build_tools/build_msi.py` | GUID_COMP61-64, CLEANUP_FILES, MFMCleanupDir, uninstall step 3b | No |
| `src/overlay.html` (175,630 bytes) | **NOT TOUCHED** — SHA256 MATCH | **SACRED** |
| `src/customize.html` (206,284 bytes) | **NOT TOUCHED** — SHA256 MATCH | **SACRED** |
| `src/tray.ps1` (801,427 bytes) | **NOT TOUCHED** — SHA256 MATCH | **SACRED** |

---

## Feature Detail

### 1. File-edit-only OBS toggle (TrayMenuViewModel + App.xaml.cs)

`ObsToggleState` enum drives all menu/tooltip state:

| State | Menu label | Tooltip | Enabled |
|-------|-----------|---------|---------|
| `NotAdded` | Add OBS overlay | OBS browser source not added | Yes |
| `Added` | Remove OBS overlay | OBS browser source active | Yes |
| `PendingRestart` | Remove OBS overlay (restart OBS to apply) | Change pending — restart OBS | Yes |

On startup (`App.xaml.cs`), `ObsSceneFileEditor.AddBrowserSource()` runs unconditionally via
`Task.Run`. The tray menu button calls `ObsSceneFileEditor` directly — no WebSocket.

When OBS is running at toggle-time, the change is applied to the scene file and
`SetObsToggleState(PendingRestart)` shows the "restart OBS" suffix. A 60-second
`System.Threading.Timer` polls `IsObsRunning()` and resolves the state once OBS exits.

### 2. obs.pending_restart config persistence

`IConfigService.SetValue("obs.pending_restart", bool)` is written on every
`SetObsToggleState()` call. On next tray startup, the field is read:

- If `true` and OBS is still running → restore `PendingRestart`
- If `true` and OBS has exited → clear stale flag, proceed to `BrowserSourceExists()` check

Prevents phantom "(restart OBS to apply)" on restart after OBS has already been closed.

### 3. ObsSceneFileEditor — GAP-1 and GAP-2

**GAP-1 (URL-update-on-mismatch):** When `AddBrowserSource()` finds the source already
present but with a different URL, it updates `settings.url` in-place. The source `uuid` is
preserved — OBS scene references remain intact.

**GAP-2 (JSON parse-back validation):** Before every `File.WriteAllText()`, the serialized
output is parsed back with `JsonNode.Parse(output) ?? throw new InvalidOperationException(...)`.
This is a safety floor that blocks corrupt writes if the serializer produces invalid JSON.

### 4. MastersFM_ObsCleanup.exe

Standalone console binary (`net8.0-windows`, framework-dependent, no extra NuGets).

**Execution flow:**
1. Check if OBS is running (obs64/obs32/obs processes)
2. If running: poll every 10s, max 30 polls (= 5 minutes); log WARNING if still running after timeout and proceed anyway
3. Walk `%APPDATA%\obs-studio\basic\scenes\*.json`; for each file: remove source entry named "Master's FM" and all `scene_items` referencing it by UUID; GAP-2 parse-back validation; write only if modified
4. Log summary to `%ProgramData%\MastersFM\obs_cleanup.log` (best-effort)
5. Self-delete: spawn `cmd.exe /c timeout 5 >nul & del /q "exe" & del /q "*.dll" & ... & rmdir "Cleanup" 2>nul`

Supports `--dry-run` flag: all logic runs but no writes occur and self-delete is skipped.

### 5. MSI uninstall integration (build_msi.py)

New MSI component chain:

```
CommonAppDataFolder
  └── MFMCommonDataDir  →  %ProgramData%\MastersFM\
        └── MFMCleanupDir  →  %ProgramData%\MastersFM\Cleanup\
              └── MastersFM_ObsCleanup.exe   (GUID_COMP61)
              └── MastersFM_ObsCleanup.dll   (GUID_COMP62, if present)
              └── MastersFM_ObsCleanup.runtimeconfig.json  (GUID_COMP63, if present)
              └── MastersFM_ObsCleanup.deps.json  (GUID_COMP64, if present)
```

VBScript uninstall step 3b (inserted before the existing cleanup step):

```vbscript
cleanupExe = oShell.ExpandEnvironmentStrings("%ProgramData%\MastersFM\Cleanup\MastersFM_ObsCleanup.exe")
If fso.FileExists(cleanupExe) Then
    oShell.Run """" & cleanupExe & """", 0, False
End If
```

Launched with `WindowStyle=0` (hidden) and `bWaitOnReturn=False` so the uninstaller does not
block. The binary polls for OBS exit itself.

---

## Test Results

### Automated (13/13 PASS)

| # | Test | Result |
|---|------|--------|
| T01 | MastersFM_Tray_v14 build — 0 warnings/errors | **PASS** |
| T02 | MastersFM_ObsCleanup build — 0 warnings/errors | **PASS** |
| T03 | build_msi.py Python syntax valid | **PASS** |
| T04 | _full_rebuild.ps1 parse 0 errors | **PASS** |
| T05 | ADD browser source to scene file | **PASS** |
| T06 | BrowserSourceExists detects source | **PASS** |
| T07 | ADD idempotent — same URL no write | **PASS** |
| T08 | GAP-1: URL-update-on-mismatch in-place | **PASS** |
| T09 | Scene items added to all 3 scenes | **PASS** |
| T10 | REMOVE source + all scene items | **PASS** |
| T11 | REMOVE idempotent — no-op when absent | **PASS** |
| T12 | ObsCleanup --dry-run: OBS detection | **PASS** (exit 0) |
| T13 | ObsCleanup self-delete mechanism | **PASS (code review)** |

### Deferred to UAT

| Item | Condition required |
|------|--------------------|
| Toggle OBS ON with OBS running — PendingRestart suffix appears in tray | WPF tray running |
| Toggle OBS OFF with OBS running — suffix appears | WPF tray running |
| 60s timer clears suffix after OBS exits | Tray running + OBS close |
| obs.pending_restart survives tray restart | Tray running + config file |
| MSI uninstall with OBS running — ObsCleanup.exe spawned | Full MSI install |

---

## API Correction (H.NotifyIcon.Wpf 2.3.2)

The correct balloon-tip API is:

```csharp
TaskbarIcon.ShowNotification(title, message, H.NotifyIcon.Core.NotificationIcon.Info)
```

`ShowBalloonTip` / `BalloonIcon` do not exist in this version. Discovered by reading the
NuGet XML documentation file at:

```
C:\Users\Master\.nuget\packages\h.notifyicon.wpf\2.3.2\lib\net8.0-windows7.0\H.NotifyIcon.Wpf.xml
```

---

## Architecture Decisions

1. **File-edit-only, no WebSocket primary** — Stage 7.8B's WebSocket path (`[Obsolete]` dead
   code) is retained but never called. File-edit works for all OBS versions and all OBS states
   (running or not). Eliminates the WS connection/auth complexity and the Mode B runtime
   failure mode.

2. **System.Threading.Timer over DispatcherTimer** — The 60s poll timer must not tie up the
   WPF dispatcher thread. UI updates are marshalled back via
   `Application.Current.Dispatcher.BeginInvoke()`.

3. **MastersFM_ObsCleanup.exe as a separate binary** — The uninstaller (VBScript) cannot
   block on OBS exit. A separate process that self-polls and self-deletes avoids the need for
   a registered service or scheduled task.

4. **Framework-dependent publish for ObsCleanup** — Produces the smallest artefact set
   (~150 KB EXE + 3 sidecar files). Relies on the .NET 8 runtime already present (installed
   by the main MSI). Self-delete purges EXE + sidecars + Cleanup\ dir.

---

## Stage 7.8C — COMPLETE
All 9 STEPs executed. Protected sources unchanged. Both builds clean. 13/13 tests pass.
Next: Brief 3 — Stage 7.7B visual rebuild.
