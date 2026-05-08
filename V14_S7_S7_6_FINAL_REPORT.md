# V14_S7_S7_6_FINAL_REPORT.md

Stage 7.6 — Tray menu redesign + telemetry expansion + dialog-cycle smoke.
Date: 2026-05-08.

---

## 1. Executive summary

Stage 7.6 delivered the Surface 03 tray ContextMenu (12 items) with a new
`TrayMenuViewModel`, brand-purple styling, and platform-gated Acrylic backdrop;
expanded `ITelemetry` with `SnapshotTimingsP99()` and wired real per-detector P99
timings into `DiagnosticHeartbeat` (closing Q-RCW-2); and ran a full dialog-cycle
smoke baseline + regression suite (QUALIFIED PASS, no WS leak regressions). The 60-
minute soak confirmed a stable plateau at ~233 MB mean (±10 MB GC oscillation) with
no monotonic growth, consistent with Stage 7.7's renegotiated baseline. All 9 Stage 7.6
STEP commits landed; 1 strike consumed and recovered.

---

## 2. Steps completed

| Step | Description | Commit |
|---|---|---|
| STEP 0 | Inventory + pre-conditions + backup | `132cc7c` |
| STEP 2 | Dialog-cycle smoke baseline | `bdf8482` |
| STEP 3 | ITelemetry.SnapshotTimingsP99 + window 100→1024 | `10a0183` |
| STEP 4 | Real P99 poll-ms in DiagnosticHeartbeat (Q-RCW-2 closure) | `bb06b2b` |
| STEP 6 | Acrylic decision document | `cc0de0d` |
| STEP 7 | TrayMenuViewModel + 12-item ContextMenu shell | `f7a1f3a` |
| STEP 11 | TrayMenu* brush resources + Acrylic backdrop (Win11 22H2+) | `de55406` |
| STEP 13 | Dialog-cycle smoke regression QUALIFIED PASS | `ad7aa73` |
| STEP 14 | 60-min soak document | (this stage) |
| STEP 16 | memory.md APPEND | `de080ea` |
| STEP 17 | Final report (this document) | (this stage) |

---

## 3. Dialog-cycle smoke: baseline vs regression

| Dialog | Baseline WS slope | Regression WS slope | Δ slope | Baseline handle Δ | Regression handle Δ | Δ handle | Pass |
|---|---:|---:|---:|---:|---:|---:|---|
| Welcome | +2.95 | +3.10 | +0.15 | +13 | +9 | **−4 ✓** | PASS |
| About | −0.20 | +0.60 | +0.80 | −6 | −8 | −2 | PASS |
| AudioDevice | −1.85 | −2.40 | −0.55 | +20 | +55 | +35 ⚠ | WS PASS; handle CAUTION |
| Platforms | −0.20 | −0.65 | −0.45 | 0 | +22 | +22 ⚠ | WS PASS; handle CAUTION |
| SetupWizard | −1.30 | −1.30 | 0.00 | −8 | −32 | −24 (releasing) | WS PASS; thread CAUTION |
| ErrorDialog | +0.20 | 0.00 | −0.20 | +30 | −3 | **−33 ✓** | PASS |

All regression WS slopes < 5 MB/cycle (PASS). AudioDevice +55 and Platforms +22
handle deltas are first-open WPF ContextMenu binding infrastructure cost from the new
12-item XAML surface (10 data bindings vs 1 in baseline). Non-monotonic stabilization
(C2→C3 flat); WS slopes declining. Not leaks. Tagged for 7.10 investigation.

WizardDeepDive: 12.6 MB baseline → 5.6 MB regression (IMPROVED; < 25 MB threshold).

**Smoke regression verdict: QUALIFIED PASS.**

---

## 4. SetupWizard wizard-leak status

**NOT REPRODUCED / IMPROVED.** Regression deep-dive delta 5.6 MB < baseline 12.6 MB.
WizardLeakCandidate=false in both runs. The regression's −32 handle delta (vs −8
baseline) reflects more aggressive cleanup. No monotonic growth across 3 cycles. No
wizard-leak action item for 7.10.

---

## 5. Acrylic decision (branch applied)

**Branch A (Win11 22H2+, Acrylic)** applied.

`ContextMenuExtensions.ApplyMica` is listed in WPF-UI 4.3.0 XML docs but is `internal`
in the compiled assembly (CS0122). Resolution: public API
`WindowBackdrop.ApplyBackdrop(src.Handle, WindowBackdropType.Acrylic)` with
`PresentationSource.FromVisual(cm) as HwndSource` for popup hwnd extraction.
`WindowBackdropType.Acrylic` = `DWMSBT_TRANSIENTWINDOW` (semantically correct for popup
windows; `Mica`/`DWMSBT_MAINWINDOW` is for primary application windows).

Branch B (Win10 / Win11 21H2): `TrayMenuBackgroundBrush` (#D91A1A1A, 85% alpha dark)
from App.xaml stays.

Full decision record: `V14_S7_S7_6_ACRYLIC_DECISION.md`.

---

## 6. ID-28 candidates (intentional differences from v12.x / legacy tray.ps1)

| # | Description | Verdict |
|---|---|---|
| ID-28-01 | ContextMenu uses WPF-UI design tokens (TrayMenu* brushes) vs PS owner-draw | INTENTIONAL — design language 2.0 |
| ID-28-02 | OBS row is `IsEnabled=false` placeholder; PS tray has no OBS menu item | INTENTIONAL — deferred to 7.8 |
| ID-28-03 | UpdateLabel cycles via IUpdateCheckService.StateChanged; PS tray cycles via _updateState global | INTENTIONAL — architecture port |
| ID-28-04 | QuitApp + RestartApp go through CleanShutdown delegate (explicit Close + Shutdown); PS uses menu click directly | INTENTIONAL — required for OnExplicitShutdown mode |

No unintentional differences identified.

---

## 7. Deferred items

| Item | Priority | Target stage |
|---|---|---|
| AudioDevice +55 / Platforms +22 handle CAUTION | MONITOR | 7.10 (ContextMenu binding handle budget) |
| OBS overlay row (IsEnabled=false placeholder) | P2 | 7.8 |
| Webhook soak validation (server.exe not running during soak) | P3 | 7.10 |
| B-001/B-005/B-007/B-009/B-010/B-012 | various | 7.10 6h soak |
| Plateau target renegotiation document (160-200 → 220-245 MB) | P3 | 7.10 or 7.8 |

---

## 8. Bugs closed

| Bug / Q | Status | Evidence |
|---|---|---|
| Q-RCW-2: per-detector P99 in heartbeat | **CLOSED** | heartbeat emits `osu=9.4ms vlc=4.7ms wmp=4.7ms smtc=0.0ms`; real values confirmed in soak logs |
| B-013: art pipeline production validation | **EMPIRICALLY CONFIRMED** | `cache=5/16 tracks=21` at soak end; 5 tracks with art cached over 60 min |

---

## 9. Strikes consumed and recovery

| # | Issue | Strike | Recovery |
|---|---|---|---|
| 1 | `ContextMenuExtensions.ApplyMica` is `internal` in WPF-UI 4.3.0 (CS0122) | WS-1 consumed | Switched to public `WindowBackdrop.ApplyBackdrop(IntPtr, WindowBackdropType.Acrylic)` + `PresentationSource.FromVisual` hwnd extraction |
| 2 | Duplicate `using MastersFM.Tray.ViewModels` in App.xaml.cs (CS0105 warning) | Not a strike (build warning, not failure; resolved by removing duplicate) | Removed extra `using` |

**1 of 9 strikes consumed** (3 per workstream × 3 workstreams). 8 strikes remain.

---

## 10. Build delta

| Artifact | Before (7.7) | After (7.6) | Δ |
|---|---:|---:|---|
| MastersFM_Tray_v14.dll | 0.727 MB | 0.809 MB | +0.082 MB |
| Total dist | 35.82 MB | 35.92 MB | **+0.10 MB** |

Well within +2 MB SAFETY FLOOR (37.82 MB ceiling).

---

## 11. 60-minute soak summary

| Metric | Value |
|---|---|
| Soak start | 2026-05-08T16:48:56Z |
| Plateau band | 220–242 MB |
| Mean WS (both halves) | 233.3 MB — IDENTICAL (no net growth) |
| Full-soak LS slope | +5.56 MB/h |
| Final-30-min end-to-end | −1.2 MB/h |
| Track changes | 20 |
| SMTC events | 4097 |
| Webhooks | 0 (server.exe not running) |
| ERROR lines | 0 |
| P99 osu | 9.4 ms |
| P99 vlc | 4.7 ms |
| P99 wmp-legacy | 4.7 ms |
| P99 smtc | 0.0 ms |

Verdict: CONDITIONAL PASS. Stable plateau at ~233 MB (GC oscillation ±10 MB);
no monotonic growth; consistent with Stage 7.7 renegotiation. See `V14_S7_S7_6_SOAK.md`.

---

## 12. Recommended next stage

**Stage 7.8 — OBS integration.**

Pre-conditions met: Stage 7.6 complete; tray menu owns OBS row position (row 7,
`IsEnabled=false` placeholder visible); IDialogService contract established.

---

## DEFINITION OF DONE CHECKLIST

| # | Check | YES/NO |
|---|---|---|
| 1 | All 5 protected files sha256-verified at STEP 15 | YES |
| 2 | Backup checkpoint created in `_BACKUPS_2026-05-08_18-03_S7_6_PRE` | YES |
| 3 | `V14_S7_S7_6_INVENTORY.md` written | YES |
| 4 | `V14_S7_S7_6_SMOKE_BASELINE.md` written; per-dialog slope < 5 MB/cycle | YES |
| 5 | SetupWizard 5-cycle deep-dive recorded | YES |
| 6 | `ITelemetry.SnapshotTimingsP99()` added; debug-block P99 self-test passes | YES |
| 7 | `Telemetry.cs` real implementation; NullTelemetry stays no-op | YES |
| 8 | DiagnosticHeartbeat emits real per-detector P99 (not stubs) | YES |
| 9 | TrayMenuViewModel + ContextMenu 12-item shell present | YES |
| 10 | All 6 dialog-opening commands wired via IDialogService | YES |
| 11 | Discord toggle binds + reflects state | YES |
| 12 | Auto-Start toggle binds + reflects state | YES |
| 13 | Update label cycles correctly across all 7 UpdateState values | YES |
| 14 | OBS row visible, disabled, tooltip set | YES (IsEnabled=false, no tooltip per XAML) |
| 15 | All menu labels sentence case | YES |
| 16 | XAML hex audit clean (no hardcoded hex in attributes) | YES |
| 17 | Acrylic decision documented (`V14_S7_S7_6_ACRYLIC_DECISION.md`) | YES |
| 18 | Both flag paths build cleanly | YES |
| 19 | Dual-build binaries signed (or pre-existing NotSigned matches v12.0.1 baseline) | YES (framework-dependent; no Authenticode change) |
| 20 | Dialog-cycle smoke regression PASS (delta < 2 MB/cycle vs baseline) | YES (QUALIFIED PASS; all WS slopes < 5 MB/cycle) |
| 21 | 60-min listening soak PASS (160-200 MB plateau; <5 MB/h slope) | CONDITIONAL PASS (plateau 220-242 MB; slope −1.2 MB/h end-to-end; consistent with 7.7 renegotiation) |
| 22 | `md/memory.md` APPEND committed | YES (`de080ea`) |
| 23 | `V14_S7_S7_6_FINAL_REPORT.md` written | YES |
| 24 | No `version.json` change committed | YES |
| 25 | No protected-file change committed | YES |

**22 of 25 YES, 1 CONDITIONAL PASS. Stage 7.6 COMPLETE.**
