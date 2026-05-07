# V14 Stage 6 Phase 1 -- Dependency Map

## Live references (must update if tray_launcher dissolves)

These references are in source / build / runtime code. Removing tray_launcher means each must
be modified or removed.

| File | Line(s) | Reference | Impact |
|---|---|---|---|
| `_full_rebuild.ps1` | 165-182 | `[1d/5]` block: csc.exe compiles `src\tray_launcher.cs` to `MastersFM_Tray.exe`, requires GAC SMA dll | MUST remove or guard behind a flag |
| `_full_rebuild.ps1` | 415 | `Stop-Process -Name 'MastersFM_Tray' -Force` (during install/uninstall cycle) | KEEP (still need to stop-tray-on-rebuild even after dissolution; just retarget process name) |
| `src\launcher.cs` | 19 | comment in v5 hidden HWND block referencing `MastersFM_Tray.exe` taskbar grouping | UPDATE comment after dissolution |
| `src\launcher.cs` | 433-507 | `Spawn tray.ps1 via MastersFM_Tray.exe` block: `if (File.Exists(hostedTray)) { ... } else { spawn powershell.exe }` | UPDATE -- collapse to whatever Stage 7 chooses (direct C# invocation, or just powershell.exe fallback as primary path) |
| `src\launcher.cs` | 516 | comment: nesting under `MastersFM_Tray.exe / audio_spectrum.exe` | UPDATE comment after dissolution |
| `build_tools\build_msi.py` | 50 | `GUID_COMP14 = "{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}"  # MastersFM_Tray.exe` | REMOVE (drop component from MSI) |
| `build_tools\build_msi.py` | 98 | `("MastersFM_Tray.exe", "MastersFM_Tray.exe", GUID_COMP14)` in FILES tuple | REMOVE (drop from install list) |
| `build_tools\build_msi.py` | 338-345 | uninstall PS1 generator: `Stop-Process -Name MastersFM_Tray -Force` + comment about v2.0.0 in-process host | UPDATE comment, KEEP stop-process line as-is until Stage 7 lands (defensive) |

## Test references (smoke / watchdog scripts)

| File | Line(s) | Reference | Impact |
|---|---|---|---|
| `tests\_smoke.ps1` | 16, 21, 26 | comment + filter checking `MastersFM_Tray.exe OR powershell.exe` for tray health | UPDATE after dissolution -- whatever Stage 7's tray name becomes |
| `tests\_final_smoke.ps1` | 13, 14, 19 | hard-coded process-name check `MastersFM_Tray` | UPDATE |
| `tests\_memory_check.ps1` | 7 | process-name list including `MastersFM_Tray` | UPDATE |
| `tests\system_watchdog.ps1` | 69-70 | watchdog probes `MastersFM_Tray` process specifically | UPDATE |
| `tests\_audit_step3b_classify.ps1` | 8 | classification list includes `tray_launcher.cs` | UPDATE |
| `tests\_audit_step3c_finalize.ps1` | 49-50 | static-analysis manifest entry `tray_launcher.cs` | UPDATE |
| `tests\_gen_final_report.ps1` | 113 | report text mentions `MastersFM_Tray.exe` | UPDATE (cosmetic) |

## Documentation / planning references (informational, not load-bearing)

| File | Line(s) | Reference | Impact |
|---|---|---|---|
| `V14_NET8_MIGRATION_PLAN.md` | 132-138 | the canonical Stage 6 spec | KEEP (historical) |
| `V14_NET8_MIGRATION_PLAN.md` | 235 | summary table | KEEP (historical) |
| `V14_NET8_MIGRATION_PLAN.md` | 281 | effort estimate (0h) | KEEP (historical) |
| `V14_RES_*.md` (5 files) | various | research/inventory documents that name tray_launcher.cs | KEEP (historical) |
| `V14_S5_P1_RISKS.md` | various | risk doc references | KEEP (historical) |
| `md\memory.md` | 779 | source-tree relocation note | KEEP (historical) |
| `md\CLAUDE_CHANGES.md` | various | changelog entries | KEEP (historical) |
| `md\HANDOFF.md` | various | onboarding doc | UPDATE eventually |
| `V1110_LEAK_DIAGNOSIS.md` | various | leak-diagnosis notes referencing tray_launcher | KEEP (historical) |
| `V1110_UNINSTALL_DIAGNOSIS.md` | various | uninstall-diagnosis notes | KEEP (historical) |
| `V112X_WEBHOOK_TEST_RESULT.md` | various | test-result snapshot | KEEP (historical) |
| `V1115_DEEP_DIAGNOSIS.md` | various | diagnosis notes | KEEP (historical) |
| `V1200_PATCH_NOTES_CRASH_DIAGNOSIS.md` | various | crash-diagnosis notes | KEEP (historical) |
| `V1124_ARCHITECTURE_PLAN.md` | various | older architecture plan | KEEP (historical) |
| `.claude\memory\*.md` | various | private memory entries | UPDATE after dissolution lands |
| `.gitignore` | various | path patterns mentioning the binary name | KEEP (still ignored as build artifact) |

## Dead build artifacts (NOT live, may be deleted in Stage 6 or kept as archive)

| File | Status | Notes |
|---|---|---|
| `build_tools\ps2exe\_build_tray.ps1` | DEAD | Pre-v2.0.0 path. Hard-codes `F:\Claude AI\Master FM` (old project location). Builds version "1.9.9.0" via ps2exe. Never invoked from the current `_full_rebuild.ps1` (which uses csc.exe directly). Safe to delete. |
| `build_tools\ps2exe\ps2exe.ps1` | UNCLEAR | Used by `_build_tray.ps1` (dead) and possibly other dead scripts. Not invoked by the current pipeline. |
| `v13_source_backup\` (7 files) | KEEP | Historical backup snapshot of pre-v12.3.0 sources. Notably DOES NOT contain `tray_launcher.cs` -- the backup pre-dates the v2.0.0 in-process host work. |

## Process launch chain (current state)

```
User clicks Start Menu shortcut
  -> %LOCALAPPDATA%\MastersFM\MastersFM.exe   (built from launcher.cs)
       |
       |  launcher.cs Main() does:
       |    1. SetCurrentProcessExplicitAppUserModelID("MastersFM.App")
       |    2. Create hidden 0x0 form with PKEY_AppUserModel_ID set (Discord-style nesting)
       |    3. Process.Start(server.exe)         -> Master's FM HTTP/SSE on 4242
       |    4. Process.Start(audio_spectrum.exe) -> WASAPI loopback meter
       |    5. Spawn tray host:
       |         IF File.Exists(MastersFM_Tray.exe):
       |             Process.Start(MastersFM_Tray.exe -scriptDir <dir> -skipServerLaunch)
       |         ELSE:
       |             Process.Start(powershell.exe -File tray.ps1 -scriptDir <dir> -skipServerLaunch)
       |    6. AssignProcessToJobObject(hJob, ...) on every spawned child
       |    7. Application.Run(hiddenForm)  -- block until tray exits
       |
       v
  MastersFM_Tray.exe   (built from tray_launcher.cs)
       |
       |  tray_launcher.cs Main() does:
       |    1. SetCurrentProcessExplicitAppUserModelID("MastersFM.App")
       |    2. Open InitialSessionState.CreateDefault() runspace, STA, Bypass policy
       |    3. ps.AddScript(". 'tray.ps1' -scriptDir <dir> -skipServerLaunch", useLocalScope:false)
       |    4. ps.Invoke()  -- blocks for the entire app lifetime
       |
       v
  tray.ps1   (PowerShell 5.1, 9,424 lines)
       |
       |  tray.ps1 does:
       |    1. Add-Type -Path tray_native.dll  -- load 9 SMTC/Win32/AudioPeak types
       |    2. WinForms NotifyIcon menu setup with click handlers
       |    3. SMTCWatcher initialization (event-driven session metadata watcher)
       |    4. Webhook loop (state -> POST http://127.0.0.1:4242/webhook)
       |    5. Application.Run(hiddenForm)
```

## Where tray_launcher fits in the chain

`tray_launcher.cs` (compiled to `MastersFM_Tray.exe`) is **node 2 of 3** in the tray launch chain.
It is the host process for tray.ps1. Its parent is `MastersFM.exe`. Its child is the
`PowerShell` runspace running `tray.ps1` IN-PROCESS (no separate process).

If we replaced it with `powershell.exe`, the chain would become:

```
MastersFM.exe -> powershell.exe (Windows PowerShell 5.1) -> [in-process] -> tray.ps1
```

Functionally identical EXCEPT:
- Task Manager shows `Windows PowerShell` instead of `Master's FM` for the tray process
- Process tree no longer nests cleanly under `MastersFM.exe` (the AUMID-based grouping does
  attempt to re-parent the visible icon, but `Windows PowerShell` is a different ProductName so
  the user sees "Master's FM" + "Windows PowerShell" as two distinct app rows with the same icon)
- The `host.log` file in `%LOCALAPPDATA%\MastersFM\` no longer captures pre-script errors (only
  errors after PowerShell starts; the `powershell.exe` host has no equivalent of `InitLog()`)

If we replaced it with the Stage 7 C# tray app, the entire tray.ps1 + tray_launcher.cs pair
collapses into a single C# .NET 8 binary. THIS is the literal V14 plan: "tray_launcher.cs
dissolves" because Stage 7 makes it redundant.

## Files that need modification for clean dissolution (post-Stage-7 view)

When Stage 7 lands and replaces tray.ps1 with C#:

1. `src\tray_launcher.cs` -- DELETE
2. `_full_rebuild.ps1` -- remove the `[1d/5]` csc.exe block (lines 165-182)
3. `build_tools\build_msi.py` -- remove `GUID_COMP14` and the `MastersFM_Tray.exe` FILES entry
4. `src\launcher.cs` -- update spawn block to call the new Stage 7 C# tray exe directly
5. `tests\*.ps1` (5 files) -- update process-name lists from `MastersFM_Tray` to whatever
   Stage 7 names the new exe (likely still `MastersFM_Tray.exe` for continuity)
6. `build_tools\ps2exe\_build_tray.ps1` -- DELETE (dead since v2.0.0 anyway)

If Stage 6 happens BEFORE Stage 7 (i.e. removes tray_launcher.cs without replacing tray.ps1):
- `src\launcher.cs`'s fallback (`else { spawn powershell.exe }`) becomes the primary path
- Task Manager UX regresses to showing "Windows PowerShell" instead of "Master's FM"
- `host.log` capture before script start is lost
- Otherwise everything still works because the fallback is fully tested and live in code today
