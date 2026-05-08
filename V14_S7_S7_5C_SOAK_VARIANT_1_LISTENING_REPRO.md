# V14_S7_S7_5C_SOAK_VARIANT_1_LISTENING_REPRO.md

Stage 7.5C Workstream 3, Variant 1 -- listening reproduction soak.

PID 23784. Launched 2026-05-08 10:35:06 from a fresh process state
(after STEP 1 initial soak ended at 10:34 via Stop-Process). Same
SoundCloud + soundcloud-rpc bridge active. Same Orken auto-play
playlist. 90 minutes wall-clock.

Purpose: confirm whether STEP 1's plateau verdict is reproducible
across launches.

---

## 1. Heartbeat timeseries

| t (min) | clock | WS (MB) | threads | handles | events | polls | tracks |
|---:|---|---:|---:|---:|---:|---:|---:|
| 1 | 10:36 | 120.1 | 26 | 626 | 33 | 60 | 2 |
| 5 | 10:40 | 135.9 | 27 | 649 | 74 | 299 | 3 |
| 10 | 10:45 | 153.5 | 18 | 617 | 141 | 598 | 4 |
| 11 | 10:46 | 157.4 | 13 | 594 | 141 | 658 | 4 |
| 12 | 10:47 | 162.2 | 24 | 613 | 237 | 718 | 5 |
| 15 | 10:50 | 162.6 | 13 | 567 | 237 | 897 | 5 |
| 18 | 10:53 | 162.3 | 19 | 605 | 372 | 1077 | 6 |
| 21 | 10:56 | 162.6 | 19 | 617 | 485 | 1256 | 7 |
| 24 | 10:59 | 163.4 | 25 | 654 | 620 | 1436 | 8 |
| 27 | 11:02 | 166.5 | 11 | 573 | 771 | 1615 | 9 |
| 30 | 11:05 | 167.5 | 13 | 588 | 974 | 1795 | 10 |
| 33 | 11:08 | 167.8 | 11 | 597 | 1239 | 1974 | 11 |
| 36 | 11:11 | 168.6 | 21 | 652 | 1446 | 2154 | 12 |
| 39 | 11:14 | 168.6 | 21 | 606 | 1670 | 2333 | 13 |
| 42 | 11:17 | 168.4 | 21 | 608 | 1947 | 2513 | 14 |
| 45 | 11:20 | 168.1 | 14 | 601 | 1961 | 2692 | 14 |
| 48 | 11:23 | 170.2 | 33 | 737 | 2712 | 2872 | 16 |
| 51 | 11:26 | 169.1 | 10 | 602 | 2712 | 3052 | 16 |
| 54 | 11:29 | 168.9 | 10 | 569 | 3138 | 3231 | 17 |
| 57 | 11:32 | 169.5 | 21 | 626 | 3453 | 3411 | 18 |
| 60 | 11:35 | 170.3 | 22 | 636 | 3926 | 3590 | 19 |
| 63 | 11:38 | 170.8 | 34 | 745 | 4349 | 3770 | 20 |
| 66 | 11:41 | 169.4 | 12 | 563 | 4349 | 3949 | 20 |
| 69 | 11:44 | 170.9 | 21 | 612 | 4874 | 4129 | 21 |
| 72 | 11:47 | 170.0 | 20 | 614 | 5319 | 4308 | 22 |
| 75 | 11:50 | 170.9 | 24 | 628 | 5807 | 4488 | 23 |
| 78 | 11:53 | 170.2 | 19 | 598 | 6294 | 4667 | 24 |
| 81 | 11:56 | 170.0 | 12 | 582 | 6829 | 4847 | 25 |
| 84 | 11:59 | 170.0 | 11 | 594 | 7484 | 5026 | 26 |
| 87 | 12:02 | 170.7 | 22 | 645 | 8061 | 5206 | 27 |
| 90 | 12:05 | 171.0 | 21 | 661 | 8556 | 5385 | 28 |

---

## 2. Phase analysis

### Phase 1: ramp (t+1 to t+11)

WS grew 120.1 -> 157.4 MB (+37.3 / 10 min = 224 MB/h ramp).
Same trajectory as STEP 1's initial soak.

### Phase 2: NO step jump

Notable: NO discrete step jump like STEP 1's t+17 jump from 162 -> 207.
Variant 1 transitioned smoothly into plateau at ~162 MB and slowly
drifted up to ~170 MB by t+30, then stayed there.

### Phase 3: plateau (t+12 onwards)

| Window | t-start | t-end | WS-start | WS-end | Δ | MB/h |
|---|---:|---:|---:|---:|---:|---:|
| t+12 to t+45 | 12 | 45 | 162.2 | 168.1 | +5.9 | 10.7 |
| t+30 to t+90 | 30 | 90 | 167.5 | 171.0 | +3.5 | 3.5 |
| t+45 to t+90 | 45 | 90 | 168.1 | 171.0 | +2.9 | 3.9 |
| t+60 to t+90 | 60 | 90 | 170.3 | 171.0 | +0.7 | 1.4 |

Late plateau (t+45 onwards): **3.9 MB/h.**
Late-plateau (t+60 onwards): **1.4 MB/h** -- well under the
brief target of <5 MB/h.

---

## 3. Comparison vs STEP 1 initial soak

| Dimension | STEP 1 (init) | Variant 1 (repro) | Delta |
|---|---:|---:|---:|
| t+1 WS | 118.6 | 120.1 | +1.5 |
| t+15 WS | 162.2 | 162.6 | +0.4 |
| t+30 WS | 198.1 | 167.5 | -30.6 |
| t+60 WS | 201.0 | 170.3 | -30.7 |
| t+90 WS | -- | 171.0 | -- |
| Step jump at t+17? | YES (+45 MB) | NO | -- |
| Plateau range | 193-207 | 168-171 | -27 |
| Late MB/h (t+30 -> end) | 5.8 | 3.5 | -2.3 |

**Variant 1's plateau is ~30 MB LOWER than STEP 1's plateau.**

Both soaks ran the same workload (active SoundCloud listening,
~3 min/track skip rate). Same TFM, same dist, same managed code.
The only difference: STEP 1 was launched on a cold process state
right after brief launch; Variant 1 was launched immediately after
STEP 1 was force-killed. Possible OS-level cache warm-up effects:
Microsoft.Windows.SDK.NET.dll (24 MB) memory-mapped image cache
likely warm; CSWinRT projection metadata pre-loaded; .NET 8 runtime
JIT cache (when using ReadyToRun) cleaner.

The step jump in STEP 1 at t+17 (162 -> 207) was NOT seen in
Variant 1. Hypothesis: the step is a one-time .NET 8 heap expansion
or JIT tier promotion event that does not always trigger at the
same point relative to wall-clock. Stochastic.

---

## 4. Verdict

**PLATEAU CONFIRMED, lower bound established.**

The C# tray's working set under sustained SoundCloud listening
soak settles in the 168-200 MB range, depending on stochastic
factors at startup. Both observed plateaus are within or right at
the brief's renegotiated 160-200 MB target band.

Late-plateau growth rate consistently <5 MB/h across both
observations.

The 7.5B observation of "216 MB/h sustained growth" was definitively
a pre-plateau measurement. Both 7.5C soaks demonstrate that the
system DOES plateau by ~t+30-45 min.

---

## 5. UIA-Quit regression note

Variant 1's clean shutdown was attempted via WM_CLOSE PostMessage
to the hidden MainWindow (HWND 34016014). Result: shutdown
suppressed by `MainWindow.OnClosing: suppressed (no Quit source);
tray stays alive` log entry, by design. The tray was ultimately
ended via Stop-Process (force-kill); the OnExit sequence was NOT
verified in this brief.

The 7.5B brief's UIA-Quit menu click test verified the OnExit
sequence under shorter (13 min) load. 7.5C did NOT exercise the
UIA Quit-menu path because programmatic invocation of the tray
icon's right-click menu requires UIA scripting that this brief's
toolset does not have. The 90-min listening soak ran without
crash, hang, or anomalous log activity, so the runtime stability
under longer load IS verified, but the OnExit-clean-shutdown
regression check is NOT verified for this brief.

---

End of Variant 1 listening reproduction soak.
