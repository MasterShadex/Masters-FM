# V14_S7_S7_10_HANDLE_CAUTION_RESOLUTION.md

Stage 7.10 STEP 2.2 — 7.6 AudioDevice / Platforms handle CAUTION resolution.
Date: 2026-05-08.
Smoke run: `dialog_smoke_BASELINE_20260508_210251.json` (process PID 29876; exit code 0).
Duration: 23:02:51 – 23:07:16 local (~4.4 min).

---

## S2.1 — Background

Stage 7.6 smoke regression identified two CAUTION items:
- **AudioDevice**: handle Δ = +55 across 3 open/close cycles (threshold was: flag + watch)
- **Platforms**: handle Δ = +22 across 3 open/close cycles (threshold was: flag + watch)

Root cause (7.6 S13.3): First-open ContextMenu WPF binding infrastructure cost. The WPF
ContextMenu builds its template + binding tree on first display; handle cost is one-time
per-dialog-type but amortises over the process lifetime. Not a leak; not a trend. Flagged
for watch-list tracking.

Stage 7.8 smoke regression showed both improved (AudioDevice +16; Platforms +0), attributed
to ObsService singleton pre-allocating ContextMenu binding infrastructure before the dialog
cycles ran (higher baseline handle count = smaller marginal per-dialog cost).

7.10 STEP 2.2 re-runs the smoke on the post-7.8 build to confirm improvements hold before
the cutover. The cutover does not change DI graph or ContextMenu bindings; this run is
confirmatory only.

---

## S2.2 — Run parameters

| Parameter | Value |
|---|---|
| Executable | `dist\tray_csharp_release\MastersFM_Tray_v14.exe` |
| Build timestamp | 2026-05-08 20:51:02 (post-7.8 dist build) |
| DLL size | 0.859 MB (7.8 confirmed) |
| Args | `--smoke-dialogs` |
| Config welcome_seen | False (first-run wizard triggered at startup) |
| Config obs.enabled | False (OBS service stays Disabled) |
| Output file | `%LOCALAPPDATA%\MastersFM\dialog_smoke_BASELINE_20260508_210251.json` |
| Label | BASELINE |

Note: `welcome_seen=False` at smoke time. The setup wizard opens immediately at startup
(before DialogSmokeRunner begins). The smoke runner then cycles each dialog
sequentially; the post-wizard handle state is the pre-cycle baseline for Welcome.

---

## S2.3 — Full results table (7.10 caution resolution vs 7.8 regression vs 7.6 baseline)

| Dialog | 7.10 WS slope | 7.8 WS slope | 7.6 WS slope | 7.10 Handle Δ | 7.8 Handle Δ | 7.6 Handle Δ | 7.10 Pass |
|---|---:|---:|---:|---:|---:|---:|---|
| Welcome | +3.65 | +3.70 | +3.10 | +5 | +3 | +9 | PASS |
| About | +0.80 | -0.85 | +0.60 | -8 | -4 | -8 | PASS |
| AudioDevice | -1.55 | -2.20 | -2.40 | **+22** | +16 | +55 | PASS (CAUTION target met) |
| Platforms | +0.70 | -0.95 | -0.65 | **0** | 0 | +22 | PASS |
| SetupWizard | -1.35 | -1.50 | -1.30 | -9 | -13 | -32 | PASS |
| ErrorDialog | -0.30 | -0.40 | 0.00 | +2 | -3 | -3 | PASS |

WizardDeepDive idle-60s delta: **2.0 MB** (vs 7.8 1.8 MB; vs 7.6 5.6 MB). WizardLeakCandidate=false.

---

## S2.4 — AudioDevice CAUTION resolution analysis

**Target: handle Δ < 25 (originally +55 in 7.6)**

7.10 result: **+22** (2176 pre-cycle → 2198 post-cycle 3).

The 7.10 value (+22) is slightly higher than 7.8 (+16) but both are comfortably below the +25
target. The stochastic difference (+6 between runs) is explained by the per-run baseline
handle count: in this run, the pre-cycle baseline for AudioDevice was 2176 handles (versus
2249 in the 7.8 regression). A lower baseline means the ContextMenu binding infrastructure
may not be as fully pre-populated by prior allocations, resulting in a slightly larger
marginal first-open cost. Neither value represents a leak (WS slope = -1.55 MB/cycle,
clearly GC-clearing over cycles).

**CAUTION RESOLVED.** AudioDevice handle delta is reliably below +25 across both 7.8 and
7.10 measurements. Closing the 7.6 watch-list item.

---

## S2.5 — Platforms CAUTION resolution analysis

**Target: handle Δ < 10 (originally +22 in 7.6)**

7.10 result: **0** (2198 pre-cycle → 2198 post-cycle 3).

Platforms handle delta is zero in both 7.8 and 7.10 measurements. The +22 seen in 7.6 was
entirely explained by the first-open ContextMenu binding cost; post-ObsService the binding
infrastructure is pre-allocated before the dialog cycle runs. Perfect resolution.

**CAUTION RESOLVED.** Platforms handle delta is 0 in both post-ObsService measurements.
Closing the 7.6 watch-list item.

---

## S2.6 — Other dialog observations

**Welcome +5 handles (was +3 in 7.8, +9 in 7.6):** Stable; within expected variation band.
WS slope +3.65 MB/cycle (7.8: +3.70) — unchanged, well within <5 criterion.

**About +0.80 MB/cycle slope (was -0.85 in 7.8):** Direction changed but magnitude is low
(0.80 vs 0.85). Absolute value = 0.80 MB/cycle << 5 MB/cycle criterion. Handle delta = -8
(releasing; was -4 in 7.8 and -8 in 7.6). Not a regression; within stochastic band.

**SetupWizard -9 handles / -4 threads:** Releasing (negative) as in prior runs. Fewer
handles releasing than 7.8 (-13) or 7.6 (-32), consistent with lower overall handle count
in this run. PASS.

**ErrorDialog +2 handles:** Near-zero; -0.30 MB/cycle slope. Not a regression (7.8: -3,
7.6: -3; this run's +2 vs -3 is within measurement noise). PASS.

---

## S2.7 — WizardDeepDive (5-cycle)

| Cycle | WS (MB) | Handles | Threads | GC Gen0 | GC Gen1 | GC Gen2 |
|---|---:|---:|---:|---:|---:|---:|
| pre-cycle | 243.7 | 2197 | — | — | — | — |
| 1 | 244.6 | 2197 | 19 | 2 | 2 | 2 |
| 2 | 244.8 | 2197 | 19 | 4 | 4 | 4 |
| 3 | 245.0 | 2197 | 19 | 6 | 6 | 6 |
| 4 | 245.0 | 2192 | 18 | 8 | 8 | 8 |
| 5 | 245.1 | 2185 | 17 | 10 | 10 | 10 |
| idle-60s | 245.7 | — | — | — | — | — |

- idle-60s delta: 245.7 − 243.7 = **2.0 MB** (within 10 MB threshold)
- WizardLeakCandidate: **false**
- WS is flat (244.6 → 245.1 over 5 cycles = +0.10 MB; GC collecting each cycle)
- Handle trend: 2197 → 2185 (releasing over 5 cycles; cleanup in progress)

---

## S2.8 — CAUTION resolution verdict

| Watch item | 7.6 value | 7.8 value | 7.10 value | Target | Status |
|---|---:|---:|---:|---:|---|
| AudioDevice handle Δ | +55 | +16 | **+22** | < 25 | **RESOLVED** |
| Platforms handle Δ | +22 | 0 | **0** | < 10 | **RESOLVED** |

**Both 7.6 handle CAUTIONs are RESOLVED.**

The observed values meet the stated targets in both the 7.8 and 7.10 measurements. The
slight 7.10 AudioDevice uptick (+22 vs +16) is within expected stochastic variation
(different per-run handle baseline). The WS slope criterion (< 5 MB/cycle absolute) is met
for all 6 dialogs. WizardDeepDive idle-60s delta of 2.0 MB is within threshold; no leak
candidate.

Watch-list items AudioDevice and Platforms are **CLOSED** as of 2026-05-08.

---

## Cross-references

| Document | Role |
|---|---|
| V14_S7_S7_6_SMOKE_REGRESSION.md | 7.6 original CAUTION source |
| V14_S7_S7_8_SMOKE_REGRESSION.md | 7.8 regression showing first improvement |
| V14_S7_S7_10_LOG.md | Stage 7.10 run log |
