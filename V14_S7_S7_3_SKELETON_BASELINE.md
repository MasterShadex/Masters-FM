# V14_S7_S7_3_SKELETON_BASELINE.md

WPF empty-skeleton baseline locked at Stage 7.3 30-min soak (2026-05-08).
This is the reference baseline against which Stage 7.5 (detection load)
and Stage 7.10 (final ship) will measure regression.

---

## Process

- Build: `dist\tray_csharp_release\MastersFM_Tray_v14.exe`
- Locked NuGet stack: H.NotifyIcon.Wpf 2.3.2, WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection 9.0.15
- Mode: empty skeleton (Quit-only menu; no detection load; ITelemetry=NullTelemetry)
- Process: PID 17836, launched 2026-05-08 05:33:11, stopped via Quit menu click 06:05:27 (32.3 min runtime)

---

## Soak data (29 heartbeats; 60s cadence)

| Time | WS (MB) | Threads | Handles | Ring |
|---|---:|---:|---:|---:|
| 05:36:12 | 112.5 | 11 | 963 | 7 |
| 05:37:12 | 112.6 | 9 | 961 | 8 |
| 05:38:12 | 112.7 | 10 | 961 | 9 |
| 05:39:12 | 112.8 | 10 | 961 | 10 |
| **05:40:12** | **133.0** | **19** | **1523** | **11** |
| 05:41:12 | 132.9 | 12 | 1516 | 12 |
| 05:42:12 | 132.9 | 11 | 1514 | 13 |
| 05:43:12 | 132.9 | 11 | 1511 | 14 |
| 05:44:12 | 133.0 | 12 | 1514 | 15 |
| 05:45:12 | 133.1 | 11 | 1507 | 16 |
| 05:46:12 | 133.1 | 13 | 1510 | 17 |
| 05:47:12 | 133.2 | 14 | 1513 | 18 |
| 05:48:12 | 133.2 | 11 | 1507 | 19 |
| 05:49:12 | 133.2 | 11 | 1507 | 20 |
| 05:50:12 | 133.2 | 10 | 1504 | 20 |
| 05:51:12 | 133.2 | 10 | 1504 | 20 |
| 05:52:12 | 133.2 | 10 | 1504 | 20 |
| 05:53:12 | 133.2 | 10 | 1504 | 20 |
| 05:54:12 | 133.2 | 10 | 1504 | 20 |
| 05:55:12 | 133.2 | 11 | 1507 | 20 |
| 05:56:12 | 133.3 | 11 | 1507 | 20 |
| 05:57:12 | 133.3 | 11 | 1507 | 20 |
| 05:58:12 | 133.3 | 10 | 1504 | 20 |
| 05:59:12 | 133.3 | 11 | 1507 | 20 |
| 06:00:12 | 133.3 | 11 | 1507 | 20 |
| 06:01:12 | 133.3 | 10 | 1504 | 20 |
| 06:02:12 | 133.4 | 11 | 1507 | 20 |
| 06:03:12 | 133.5 | 11 | 1507 | 20 |
| 06:04:12 | 134.4 | 11 | 1507 | 20 |
| 06:05:12 | 134.5 | 10 | 1504 | 20 |

Note: heartbeat data starts at t+3min (05:36:12) because the 7.3 PS-tray
uninstall sequence triggered by the parallel STEP 4.4 full-rebuild
regression check truncated overlay.log at 05:35:25, wiping heartbeats
from t+1 and t+2 minute. Process metrics from `Get-Process` confirm
PID 17836 baseline at t+0 (05:33:11): WS=111.55 MB, Threads=15, Handles=969.

---

## Plateau identification

The skeleton transitions from initial-load state to plateau between
t+6min and t+7min:

- **Initial-load phase (t+0 to t+6min):** WS climbs slowly 111.5 -> 112.8 MB; threads/handles stable at ~10/961.
- **Transition event (t+7min, ~05:40:12):** WS jumps to 133.0 MB; threads spike to 19 (GC threadpool burst); handles jump to 1523. Likely a Gen2 GC + WPF resource finalization event.
- **Plateau phase (t+7min onwards):** WS oscillates 132.9-134.5 MB; threads back to 9-14 band; handles 1504-1516 oscillation around 1507 plateau.

**Plateau characteristics:**

| Property | Value |
|---|---|
| Working set plateau | ~133-134 MB |
| Time to plateau | ~7 minutes from launch |
| Post-plateau growth rate | +1.6 MB over 23 min = ~4.2 MB/h (negligible) |
| Handle band (post-plateau) | 1504 - 1516 (oscillation +/- 6 of plateau ~1507) |
| Thread band (post-plateau) | 9 - 14 (mostly 10-11; brief spikes during GC) |
| Ring buffer cap | 20 lines (held at cap from t+12min onwards) |

---

## Pass criteria (brief STEP 7.5)

| Criterion | Target | Actual | Result |
|---|---|---|:---:|
| WS total growth t=0 -> t=30 | < 100 MB | 21.9 MB (peak 134.5; baseline 111.55) | PASS |
| WS final value | < 200 MB | 134.5 MB | PASS |
| Thread count: oscillates within +/- 5 of plateau | YES | 9-14 (post-plateau); +/-3 mostly | PASS |
| Handle count: oscillates within +/- 50 of plateau | YES | 1504-1516; +/-6 of plateau | PASS |
| Plateau identifiable | YES | At t+7min, ~133 MB WS | PASS |
| Ring buffer cap holds at 20 | YES | Cap reached at t+12min, held | PASS |
| No log spam | YES | Only structured heartbeats + bootstrap; no error/warn lines | PASS |
| No crashes | YES | Process clean | PASS |

**ALL CRITERIA PASS.** Stage 7.3 closes 7.1B's open observation O-1 with
real plateau data.

---

## Reference baseline for downstream stages

When Stage 7.5 (detection load) or Stage 7.10 (final ship) measure
regression, compare against this baseline:

- **Acceptable WS plateau under detection load (7.5):** target 50-80 MB
  ABOVE this empty-skeleton plateau; full-load skeleton expected to land
  at ~180-220 MB plateau (matching FINAL ship target of 50-80 MB
  steady-state DELTA from empty per Q7-A).
- **Acceptable WS growth rate (7.10 24h soak):** <5 MB/h post-plateau
  (this baseline shows ~4.2 MB/h with no work; detection load should not
  push higher than 10 MB/h after stabilization).
- **Handle plateau under load:** target <2000 (room for detection
  pipeline + telemetry counters + per-platform detector handles).
- **Thread plateau:** target <30 threads (current empty plateau ~11;
  detection adds maybe 2-5 threads for SMTC watcher + gap-filler timers).

If 7.5 / 7.10 measures exceed these targets without justification:
HALT and replan.

---

## Closes 7.1B observations

- **O-1 (WS/handles growth during 5-min idle):** CLOSED. The growth
  observed at 7.1B's t+5min sample (WS 111->142 MB) was the
  initial-load-to-plateau transition, NOT a leak. 7.3 30-min soak shows
  the transition completes around t+7min and plateau holds for the
  remaining 23 minutes.

- **O-2 (WM_QUIT bypasses OnExit):** CLOSED. STEP 6 Quit-menu UIA
  click test produced FULL OnExit log sequence including
  DiagnosticHeartbeat stopped, DI container disposed, mutex released,
  exit code 0. See `V14_S7_S7_3_SMOKE.md` STEP 6 section.

- **O-3 (STEP 4 full-rebuild regression deferred):** CLOSED. STEP 4.4
  full rebuild with `$UseDotnet8TrayCs=$false` ran cleanly: PS tray +
  server + audio_spectrum + customize + launcher + MSI all built and
  Signed Valid. `=== REBUILD DONE OK ===`.

---

End of skeleton baseline.
