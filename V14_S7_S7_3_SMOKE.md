# V14_S7_S7_3_SMOKE.md

Stage 7.3 smoke validation. Three test sets: STEP 5 functional smoke,
STEP 6 Quit-menu OnExit verification (closes O-2), STEP 7 30-min soak
(closes O-1 with locked baseline data).

---

## STEP 5: Functional smoke

Launched `dist\tray_csharp_release\MastersFM_Tray_v14.exe` (PID 17836,
2026-05-08 05:33:11). Verified within 5 seconds.

| Checkpoint | Status | Evidence |
|---|:---:|---|
| Tray icon visible | PASS | UIA found icon at (1298, 1056) post-launch |
| Tooltip text matches 7.1B | PASS | "Master's FM v14 dev (skeleton)" -- UIA Name match used in STEP 6 |
| Right-click shows Quit menu | PASS | UIA found "Quit" MenuItem in STEP 6 |
| `[Bootstrap]` component-tagged entries | PASS | See log excerpt below |
| `[Tray]` component-tagged entries | PASS | MainWindow.Loaded entry tagged `[Tray]` |
| `[Diagnostic]` component-tagged entries | PASS | DiagnosticHeartbeat start + heartbeats tagged `[Diagnostic]` |
| EarlyLog (pre-DI) entries unchanged from 7.1B | PASS | First 6 entries are `[EARLY]` level; no component tag (preserved 7.1B format) |

Launch log excerpt (PID 17836):
```
[2026-05-08 05:33:11.959] [EARLY] [TRAY-CS] Application.OnStartup begin
[2026-05-08 05:33:11.965] [EARLY] [TRAY-CS] PID=17836 OS=Microsoft Windows NT 10.0.26200.0 CLR=8.0.26
[2026-05-08 05:33:11.965] [EARLY] [TRAY-CS] BaseDir=G:\Project Folder\Master FM\dist\tray_csharp_release\
[2026-05-08 05:33:11.967] [EARLY] [TRAY-CS] Single-instance mutex acquired
[2026-05-08 05:33:11.968] [EARLY] [TRAY-CS] AUMID set via MFM_Shell (tray_native.dll)
[2026-05-08 05:33:11.969] [EARLY] [TRAY-CS] Exception hooks installed (AppDomain + Dispatcher + TaskScheduler)
[2026-05-08 05:33:11.975] [INFO ] [TRAY-CS] [Bootstrap] DI container built (ILogger, ITelemetry=NullTelemetry, SlowTickWatchdog, DiagnosticHeartbeat, MainWindow registered)
[2026-05-08 05:33:12.196] [INFO ] [TRAY-CS] [Tray] MainWindow.Loaded: TaskbarIcon initialized; tray visible
[2026-05-08 05:33:12.208] [INFO ] [TRAY-CS] [Bootstrap] MainWindow shown
[2026-05-08 05:33:12.210] [INFO ] [TRAY-CS] [Diagnostic] DiagnosticHeartbeat started (cadence 60s; mode=skeleton)
[2026-05-08 05:33:12.210] [INFO ] [TRAY-CS] [Bootstrap] Application.OnStartup completed
```

Time from launch to tray-visible: ~0.25 s (matches 7.1B baseline).

### Behavioral preservation vs 7.1B

| Property | 7.1B | 7.3 | Status |
|---|---|---|---|
| overlay.log path | `%LOCALAPPDATA%\MastersFM\overlay.log` | (same) | PRESERVED |
| `[TRAY-CS]` prefix | yes | yes | PRESERVED |
| UTF-8 no BOM | yes | yes | PRESERVED |
| Append-only with thread-safe lock | yes | yes | PRESERVED |
| EarlyLog format (pre-DI) | `[ts] [EARLY] [TRAY-CS] msg` | (same) | PRESERVED |
| Logger callsites | `Logger.Log(msg)` static | `_logger.Log(msg, "Component")` instance | EVOLVED (refactor; same observable output without component) |

No behavioral regression. Per absolute rule 10, refactor preserved
behavior exactly; component tag is an addition, not a modification.

### Telemetry NullTelemetry verified

Heartbeat lines show `counters=0` consistently across all 29 heartbeat
samples, confirming `NullTelemetry.SnapshotCounters()` returns empty
dictionary. No real counters in 7.3 -- 7.5 will swap impl.

---

## STEP 6: Quit-menu OnExit verification (closes O-2)

End-to-end UIA-driven user-flow Quit click test executed at 06:05:27
(t+32.27 min from launch).

### Procedure

1. Used `System.Windows.Automation.AutomationElement.RootElement.FindFirst`
   with `NameProperty == "Master's FM v14 dev (skeleton)"` to locate the
   tray icon. Found at screen coordinates (1298, 1056), 32x48 pixels.
2. `SetCursorPos` to icon center; `mouse_event(RIGHTDOWN)` then
   `mouse_event(RIGHTUP)` to simulate a real right-click.
3. Waited 800 ms for the WPF context menu to render.
4. Used UIA again with `NameProperty == "Quit"` to find the Quit
   `MenuItem`; obtained its `InvokePattern`; called `Invoke()`.
5. Waited 2 seconds for graceful shutdown.

### Result: PASS

`Get-Process -Id 17836` returned null after the wait -> process exited.

### FULL OnExit log sequence captured

```
[2026-05-08 06:05:27.963] [INFO ] [TRAY-CS] [Tray] Quit clicked; calling Application.Current.Shutdown()
[2026-05-08 06:05:27.965] [INFO ] [TRAY-CS] [Tray] MainWindow.OnClosing: TaskbarIcon disposed
[2026-05-08 06:05:27.969] [INFO ] [TRAY-CS] [Bootstrap] Application.OnExit begin
[2026-05-08 06:05:27.969] [INFO ] [TRAY-CS] [Diagnostic] DiagnosticHeartbeat stopped
[2026-05-08 06:05:27.969] [INFO ] [TRAY-CS] [Bootstrap] DI container disposed
[2026-05-08 06:05:27.969] [INFO ] [TRAY-CS] [Bootstrap] Single-instance mutex released
[2026-05-08 06:05:27.969] [INFO ] [TRAY-CS] [Bootstrap] Application.OnExit completed; exit code = 0
```

All 7 expected lines fired. Note that `DiagnosticHeartbeat stopped`
logs as `[Diagnostic]` (the heartbeat's own component identity) rather
than `[Bootstrap]` as the brief STEP 6.6 sample showed -- this is a
cosmetic difference; the OnExit sequence runs correctly. The brief's
sample is illustrative; my heartbeat's `Stop()` method tags itself
with its origin component ("Diagnostic") which is more precise.

**Closes 7.1B observation O-2.** WPF user-flow Quit menu click DOES
fire `Application.OnExit` cleanly. The bypass that affected WM_QUIT
under `ShutdownMode=OnExplicitShutdown` does NOT affect the
`Application.Current.Shutdown(0)` path which is what the Quit
menu invokes.

---

## STEP 7: 30-min skeleton soak (closes O-1)

Full data + analysis in `V14_S7_S7_3_SKELETON_BASELINE.md`. Summary:

| Pass criterion | Result |
|---|:---:|
| WS total growth < 100 MB | PASS (21.9 MB) |
| WS final < 200 MB | PASS (134.5 MB) |
| Thread band stable +/- 5 of plateau | PASS (9-14 band; mostly 10-11) |
| Handle band stable +/- 50 of plateau | PASS (1504-1516 band; +/- 6 of plateau ~1507) |
| Plateau identifiable | PASS (transition at t+7min; plateau ~133 MB; held 23 min) |
| Ring buffer cap holds at 20 | PASS (cap reached t+12min, held) |
| No log spam | PASS (only structured heartbeats + bootstrap; 0 errors / warnings) |
| No crashes | PASS |

29 heartbeat entries captured at 60s cadence. Note: t+1 and t+2 minute
heartbeats lost when STEP 4.4 parallel `_full_rebuild.ps1` regression
check triggered the PS tray's uninstall mode which truncates
overlay.log. The skeleton's metrics from `Get-Process` confirm baseline
state at t+0 was preserved (PID 17836 process state intact through the
truncation). Future briefs should sequence rebuild regression BEFORE
soak rather than parallel.

**Closes 7.1B observation O-1** with real data: the WS/handles growth
observed at 7.1B's t+5min sample was the WPF initial-load-to-plateau
transition, NOT a leak. The plateau holds at ~133 MB for at least 23
minutes with negligible drift (~4.2 MB/h).

---

## STEP 4.4 background regression check (closes O-3)

`_full_rebuild.ps1` ran in background during the soak with
`$UseDotnet8TrayCs = $false` (the default). Result: `=== REBUILD DONE
OK ===`. exit=0. PS tray + server + audio_spectrum + customize +
launcher + MSI all built; MSI signed Valid (CN=MasterShadex).

**Closes 7.1B observation O-3.**

Side-effect: the rebuild's `[3/5] Stopping tray + server` and `[5/5]
Installing MSI` steps included a PS tray uninstall sequence which
truncated overlay.log at 05:35:25 (wiping pre-truncation [TRAY-CS]
entries from PID 17836). Skeleton process state was unaffected; only
log evidence was lost for t+1 and t+2 minute. Documented as a
sequencing lesson for future briefs.

---

## Three-strike ledger

**0 strikes consumed.** First-attempt build PASS, first-attempt smoke
PASS, first-attempt soak PASS, first-attempt UIA Quit click PASS.

This is unusual for a refactor + new-services brief; lessons carried
forward from 7.1B (XML comment escaping, implicit usings, pack-URI
icon resources) prevented the equivalent strikes from re-occurring.

---

## Open observations

None new. All three 7.1B open observations (O-1, O-2, O-3) closed by
this brief.

---

End of smoke.
