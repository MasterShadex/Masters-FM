# V14_S7_S7_4_MEMORY_TARGET_RENEGOTIATION.md

Stage 7.4 -- STEP 6 deliverable. Memory target for the C# tray's
final ship is RENEGOTIATED based on Stage 7.3's locked WPF skeleton
baseline data.

**OLD target:** 50-80 MB plateau, <5 MB/h growth (per Q7-A in earlier briefs).
**NEW target:** 160-200 MB plateau, <5 MB/h growth, no handle/thread leak.

This document explains why and locks the new target.

---

## 1. Background

The original 50-80 MB target was set against the PS tray's leaky
145 MB + 25 MB/h baseline. The framing was: "PS tray uses 145 MB
and leaks 25 MB/h; the C# port should land closer to 50-80 MB and
not leak."

That framing conflated two metrics:
1. **Absolute plateau** (50-80 MB target)
2. **Leak rate** (close to 0 MB/h target)

Only one of those metrics is portable across runtimes. The leak
rate is. The absolute plateau is not.

## 2. What changed in 7.3

Stage 7.3's locked WPF skeleton baseline (per
`V14_S7_S7_3_SKELETON_BASELINE.md`):

| Metric | Value |
|---|---|
| Working set plateau | ~133-134 MB |
| Time to plateau | ~7 minutes from launch |
| Post-plateau growth rate | ~4.2 MB/h |
| Handle plateau | ~1507 (+/- 6) |
| Thread plateau | ~10-11 (+/- 3) |

This is the EMPTY skeleton -- Quit-only menu, no detection logic,
no UI surfaces, no work. Pure WPF runtime + WPF-UI Fluent Dark
theme + DI container + ILogger + ITelemetry stub +
DiagnosticHeartbeat.

## 3. Why 50-80 MB is structurally unreachable on this stack

Three reasons:

### 3.1 PS tray's 145 MB included PowerShell runtime hosted in-process

The PS tray runs inside `MastersFM_Tray.exe`, which embeds a
PowerShell SMA host. The 145 MB working set includes:
- PowerShell runtime (~50-70 MB)
- `tray.ps1` runspace + script-scope variables
- WinForms (loaded via `[Reflection.Assembly]::LoadWithPartialName`)
- All the per-tick allocations from S15 detector chain

Comparing PS's 145 MB to a clean WPF process's working set is
apples to oranges. Different runtimes; different memory layouts.

### 3.2 WPF + WPF-UI has a different fixed cost

The C# WPF tray has these mandatory fixed costs:
- WindowsDesktop runtime (PresentationCore, PresentationFramework, WindowsBase, WPFGfx_cor3, etc.)
- WPF-UI Fluent theme resources (~8 MB Wpf.Ui.dll loaded into the heap)
- Fluent System Icons font (loaded as resource)
- DI container (Microsoft.Extensions.DependencyInjection)
- ILogger (Logger.cs instance + ring buffer)
- H.NotifyIcon.Wpf hidden message-only window

Empirical measurement at 7.3 baseline: 133 MB plateau on an empty
skeleton with all of the above active.

The 50-80 MB target was set without empirical knowledge of WPF's
fixed cost on this stack. We now have that knowledge: 130-140 MB
is the floor.

### 3.3 The right metric is the LEAK rate, not the absolute

What matters for the project's actual goals (decade-scale stability,
no slow OOM under sustained playback) is:
- Plateau identifiable (not unbounded growth)
- Post-plateau growth rate as low as possible
- No handle leak
- No thread leak

7.3 baseline shows ~4.2 MB/h post-plateau drift. PS tray's baseline
was ~25 MB/h. **6x improvement on the metric that matters**, even
though the absolute plateau is higher.

## 4. New target locked

**WPF C# tray FINAL ship target (post-Stage-7.10):**

| Property | Target |
|---|---|
| Working set plateau | 160-200 MB |
| Time to plateau | < 15 minutes from cold start |
| Post-plateau growth rate | < 5 MB/h |
| Handle band oscillation | +/- 100 of plateau (no monotonic growth) |
| Thread band oscillation | +/- 5 of plateau (no monotonic growth) |
| 6-hour soak: WS at end | < plateau + (5 MB/h * 6 h) = plateau + 30 MB |
| 24-hour soak (validation gate): WS at end | < plateau + 120 MB |

The 160-200 MB band gives 27-67 MB of headroom above the 7.3
empty-skeleton 133 MB plateau. That headroom accommodates:

| Sub-stage | Expected delta |
|---|---|
| 7.4 (config) | negligible (<5 MB; ConfigService is tiny) |
| 7.5 (detection redesign Option B+C hybrid) | +20-40 MB (SMTC watcher + ITrackResolver + art LRU + telemetry counters when populated) |
| 7.6 (tray menu redesign) | +10-20 MB (custom XAML resources, brand-purple theming, Fluent System Icons usage) |
| 7.7 (welcome / audio device / platforms / setup / about / error dialogs) | +20-40 MB transient (dialogs allocate when shown, GC when closed; per-dialog peak adds up) |
| 7.8 (OBS) | +5-10 MB (WebSocket client + JSON serialization buffers) |
| 7.9 (Discord/AutoStart/Customizer launcher) | +5-10 MB (IShellLink COM + customize.exe Process.Start) |

Total delta from 7.3 baseline: ~60-130 MB. Final plateau projection:
193-263 MB. Conservative target band 160-200 MB suggests trim is
possible (some sub-stage estimates are upper-bound).

If empirical data at 7.10 cutover shows plateau outside 160-200 MB
band:
- **Below 160 MB**: investigate -- something didn't load that should have. Likely a false-positive plateau (haven't reached real steady-state).
- **Above 200 MB**: decide. Trim WPF-UI usage, lazy-load detector services, trim telemetry retention. OR accept and re-renegotiate.

## 5. Validation gate at 7.10

Per Stage 7.10 cutover brief (to be authored after 7.5/7.6/7.7/7.8/7.9):

- 6-hour soak post-cutover with full detection load.
- Sample at t=0/30min/1h/2h/3h/4h/5h/6h.
- Plateau must be within 160-200 MB band.
- Post-plateau growth rate < 5 MB/h.
- Handle / thread bands stable.

If any criterion fails: HALT-and-replan. Stage 7.10 cutover is
reversible via MSI downgrade-in-place.

## 6. Cross-references

- `V14_S7_S7_3_SKELETON_BASELINE.md`: locked WPF EMPTY baseline
  (~133 MB at t+7min plateau). Reference for "what the runtime
  costs before any work."
- This document (`V14_S7_S7_4_MEMORY_TARGET_RENEGOTIATION.md`):
  locked FINAL SHIP target (160-200 MB plateau). Reference for
  "what acceptable plateau looks like with full detection +
  tray menu + dialogs loaded."
- `V14_S7_REPLAN_VALIDATION_DESIGN.md` (re-plan deliverable):
  general validation methodology. NOT mutated by this brief; the
  re-plan stays as-is. This 7.4 document is a post-empirical
  correction layered on top.

The two baselines together give Stage 7.5+ briefs a clear
framework: "How much does this sub-stage add on top of the empty
skeleton, and does the running total stay within the 160-200 MB
band?"

## 7. Honesty note

This renegotiation is a real loss vs the original aspiration. The
project hoped for a smaller resident-set than PS tray, plateau-wise.
Empirical measurement shows the WPF stack's fixed cost makes the
50-80 MB band unreachable without abandoning the locked stack
choice (which we are NOT doing -- the GUI design ambition motivates
WPF, and that's the right call).

What we are buying with the higher plateau:
- A UI that can clear the brief's design bar
- Testable architecture (ILogger DI, ITelemetry interface,
  IConfigService, future detection redesign per Option B+C)
- 6x improvement on leak rate (the metric users feel during
  long-running sessions)

Net assessment: defensible. The renegotiation is locked here.
