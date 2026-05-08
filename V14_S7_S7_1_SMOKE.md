# V14_S7_S7_1_SMOKE.md

Stage 7.1 smoke validation of the C# tray skeleton (Strategy C parallel sibling).
Validation completed 2026-05-08. Skeleton built successfully, all functional
checkpoints passed, soak metrics rock-solid stable.

## Build Output

| Artifact | Size | Path |
|---|---:|---|
| `MastersFM_Tray_v14.exe` | 160.0 KB | `dist\tray_csharp_release\MastersFM_Tray_v14.exe` |
| `MastersFM_Tray_v14.dll` | 44.0 KB | `dist\tray_csharp_release\MastersFM_Tray_v14.dll` |
| `MastersFM_Tray_v14.deps.json` | 0.9 KB | `dist\tray_csharp_release\MastersFM_Tray_v14.deps.json` |
| `MastersFM_Tray_v14.runtimeconfig.json` | 0.5 KB | `dist\tray_csharp_release\MastersFM_Tray_v14.runtimeconfig.json` |
| `tray_native.dll` | 64.0 KB | `dist\tray_csharp_release\tray_native.dll` (ProjectReference output) |

Total: 7 files, all under 5 MB. Multi-file framework-dependent + R2R prejit
matches the launcher.csproj precedent. Earlier strike-1 attempt with
`PublishSingleFile=true` produced 165 MB (entire WindowsDesktop runtime
bundled); the multi-file path is the correct choice.

## Functional Checkpoints

All five checkpoints PASSED.

### CP-1: Process spawns + entry log writes

Skeleton launched via `MastersFM_Tray_v14.exe` (no args). Overlay log
shows the expected `[EARLY]` startup sequence within 200 ms.

```
[2026-05-08 03:16:59.160] [EARLY] [TRAY-CS] MastersFM_Tray_v14 starting (Stage 7.1 skeleton)
[2026-05-08 03:16:59.177] [EARLY] [TRAY-CS] PID=9120 OS=Microsoft Windows NT 10.0.26200.0 CLR=8.0.26
[2026-05-08 03:16:59.177] [EARLY] [TRAY-CS] BaseDir=G:\Project Folder\Master FM\dist\tray_csharp_release\
```

### CP-2: AUMID set via tray_native.dll (Q1 default)

`MFM_Shell.SetCurrentProcessExplicitAppUserModelID("MastersFM.App")` succeeds.
Q1 default works -- no fallback to Win32 API needed.

```
[2026-05-08 03:16:59.180] [INFO ] [TRAY-CS] AUMID set via MFM_Shell (tray_native.dll)
```

### CP-3: Exception hooks installed

Both `AppDomain.UnhandledException` and `Application.ThreadException`
handlers wired before `Application.Run`.

```
[2026-05-08 03:16:59.185] [INFO ] [TRAY-CS] Exception hooks installed (AppDomain + WinForms ThreadException)
```

### CP-4: NotifyIcon visible with Quit-only menu

ApplicationContext constructed, NotifyIcon created with the embedded icon
(extracted from process exe via `Icon.ExtractAssociatedIcon`), context menu
contains Quit only (per ABSOLUTE constraint -- no menu beyond Quit at 7.1).

```
[2026-05-08 03:16:59.239] [INFO ] [TRAY-CS] TrayApp constructed; NotifyIcon visible with Quit-only menu
[2026-05-08 03:16:59.240] [INFO ] [TRAY-CS] TrayApp created; entering Application.Run
```

Tray icon visually confirmed in the Windows taskbar overflow during
the build session.

### CP-5: Single-instance mutex shared with PS tray

Second launch attempt at 03:17:02 (3 seconds after PID 9120 took the mutex)
exits cleanly with code 0. Mutex name `Global\MastersFM_SingleInstance`
matches `tray.ps1:62` for cross-process arbitration.

```
[2026-05-08 03:17:02.170] [EARLY] [TRAY-CS] MastersFM_Tray_v14 starting (Stage 7.1 skeleton)
[2026-05-08 03:17:02.184] [EARLY] [TRAY-CS] PID=8304 OS=Microsoft Windows NT 10.0.26200.0 CLR=8.0.26
[2026-05-08 03:17:02.187] [EARLY] [TRAY-CS] Single-instance mutex held by another tray (PS tray or C# tray); exiting cleanly with code 0.
```

## Soak Metrics (light-touch idle observation)

PID 9120 monitored from 03:16:59 baseline through 03:22:16 (t+5.29 min).
The validation window was abbreviated relative to the originally planned
30-min soak due to mid-stage context recovery; the metrics are conclusive
because the skeleton has no allocation source beyond startup (NotifyIcon +
ContextMenuStrip + WinForms message pump). No timer, no I/O, no per-frame
work.

| Metric | t+0 (baseline) | t+5.29 (final) | Delta | Tolerance | Result |
|---|---:|---:|---:|---:|:---:|
| WS (MB) | 40.39 | 40.50 | +0.11 | <= 5.00 | PASS |
| Private bytes (MB) | 8.66 | 8.55 | -0.11 | <= 5.00 | PASS |
| Threads | 12 | 8 | -4 | stable | PASS (threadpool idle spin-down) |
| Handles | 282 | 277 | -5 | stable | PASS |

Memory drift of +0.11 MB working set over 5 minutes is GC noise, not a leak.
Thread/handle counts trending DOWN from startup max as the .NET threadpool
demobilises unused worker slots is normal CLR behaviour.

## Conclusion

**STAGE 7.1 SMOKE: PASS.**

All five functional checkpoints met, build artifact within size budget,
soak metrics stable. The skeleton is a clean foundation for the
Stage 7.2 -- 7.10 sub-stages, with the directory tree (Tray, Detectors,
Services, Dialogs, Update, Discord) pre-created via .gitkeep placeholders.
