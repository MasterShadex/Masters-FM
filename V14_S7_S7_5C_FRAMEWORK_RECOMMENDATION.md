# V14_S7_S7_5C_FRAMEWORK_RECOMMENDATION.md

Stage 7.5C Workstream 2 -- framework choice analysis. **Document
only; NO execution per brief absolute rule 9.**

Re-evaluates the 7.1B locked stack (WPF + WPF-UI 4.3.0 +
H.NotifyIcon.Wpf 2.3.2 + CommunityToolkit.Mvvm 8.4.2 +
Microsoft.Extensions.DependencyInjection 9.0.15) against the post-7.5B
reality of a 35.34 MB dist size and the design ambition documented
in `V14_S7_REPLAN_WPF_LOCK.md`.

---

## 1. The actual question

The 7.5B-time complaint is "35 MB dist is too large." The 7.5C
analysis must answer: **does switching frameworks meaningfully
reduce dist size, and is the migration cost justified?**

Spoiler from the dist audit (see `V14_S7_S7_5C_DIST_AUDIT.md`): the
single biggest line is `Microsoft.Windows.SDK.NET.dll` at 23.7 MB
(67% of dist). This file is the .NET 8 + Windows SDK CSWinRT
projection. It is REQUIRED for SMTC arm to function. It does not
depend on framework choice (WPF-UI vs ModernWpf vs raw WPF all need
it equally).

**The maximum addressable dist savings via framework swap is
~6.78 MB** (the WPF-UI portion: `Wpf.Ui.dll` + `Wpf.Ui.Abstractions.dll`).
That is 19% of dist; it brings 35.34 MB -> ~28.55 MB.

---

## 2. Side-by-side comparison

### 2.1 Alternative A: stay with WPF-UI (status quo)

| Dimension | Detail |
|---|---|
| Dist size contribution | 6.78 MB |
| Dist (total) | 35.34 MB |
| Migration cost | 0 hours |
| Design language | Win11 Fluent (most polished available) |
| Theme override | First-class via resource dictionaries; brand purple `#9333EA` already overridden |
| Maintenance | Active in 2025-2026 (lepoco/wpfui repo); MIT |
| Maintainer risk | Single maintainer (lepoco) + community contributors |
| Stage 7 surfaces using it | UpdateProgressWindow (Surface 07; ships in 7.2); App.xaml resource overrides; future Surfaces 1-6 to land in 7.6/7.7/7.8 |
| Loss if removed | Loses Mica/Acrylic backgrounds, Fluent System Icons, accent-aware controls, Win11-native control templates |
| Open dist gain | None (status quo) |

### 2.2 Alternative B: ModernWpf

| Dimension | Detail |
|---|---|
| Dist size contribution | ~1-2 MB (depending on version) |
| Dist (total, projected) | ~30-31 MB |
| Migration cost | 8-16 hours (rework App.xaml resources, retest UpdateProgressWindow, validate Mica/Acrylic substitution) |
| Design language | Win10/Win11 transitional Fluent; less polished than WPF-UI |
| Theme override | Resource dictionaries; less ergonomic than WPF-UI's |
| Maintenance | Maintained as of 2026-01-10 per re-plan survey; MIT |
| Maintainer risk | Smaller community than WPF-UI |
| Loss vs WPF-UI | Mica/Acrylic feature parity is partial; some controls less polished; aesthetic regresses ~half a Fluent generation |
| Dist gain | ~5 MB / 14% of dist |

### 2.3 Alternative C: roll-your-own raw WPF + custom styles

| Dimension | Detail |
|---|---|
| Dist size contribution | 0 MB |
| Dist (total, projected) | ~28.55 MB |
| Migration cost | 60-120 hours (build full styling layer; per-control hover/pressed/focus/disabled states; brand palette; replicate WPF-UI surface in custom XAML) |
| Design language | Bespoke; whatever Stage 7's design system specifies |
| Theme override | All theming is OURS; no library to fight |
| Maintenance | Zero external risk |
| Maintainer risk | None (ourselves) |
| Loss vs WPF-UI | Loses out-of-box polish; gains absolute control |
| Dist gain | ~6.78 MB / 19% of dist |

### 2.4 Alternative D: ditch the design ambition (default WPF chrome)

| Dimension | Detail |
|---|---|
| Dist size contribution | 0 MB |
| Dist (total, projected) | ~28.55 MB |
| Migration cost | 4 hours (delete WPF-UI references; ship stock chrome) |
| Design language | Windows 7-era default WPF chrome |
| Theme override | None |
| Maintenance | None needed |
| Maintainer risk | None |
| Loss vs WPF-UI | Substantial. Aesthetic regresses materially. Brief "GUI has to be good" is breached. |
| Dist gain | ~6.78 MB / 19% of dist |

---

## 3. Cost-benefit ratio

| Alternative | Dist saving | Migration hours | $ per MB saved (assume $50/h) |
|---|---:|---:|---:|
| A: WPF-UI | 0 MB | 0 h | -- |
| B: ModernWpf | 5 MB | 12 h (mid) | $120/MB |
| C: raw WPF | 6.78 MB | 90 h (mid) | $664/MB |
| D: ditch design | 6.78 MB | 4 h | $30/MB |

The cost-benefit math says Alternative D is cheapest per MB if dist
size were the only consideration. But that ignores design ambition,
which is a brief-level requirement.

---

## 4. The 35 MB question reframed

The 7.5B handoff documented +24 MB from CSWinRT projection as
"unavoidable cost of SMTC-via-WinRT-projection." That was correct.
Stage 7.5C confirms via dist audit:

- 24 MB CSWinRT (unavoidable; 67% of dist)
- 6.78 MB WPF-UI (the only meaningful framework lever; 19% of dist)
- 4.5 MB everything else (WPF runtime + tray icon + DI + MVVM + app code + small)

If 35 MB is "too large for a tray app," then framework choice is
NOT the main lever. The main lever is the SMTC architecture itself.
Two paths exist there but BOTH are out of 7.5C scope:

**Path 1: revert to PS-S15-style reflection-based WinRT access.**
Removes the CSWinRT projection entirely. Saves ~24 MB. Cost:
re-architecting back to the pre-7.5B failed state (reflection
returned null even with TFM upgraded; Stage 7.5B Strike 1
documented this conclusively).

**Path 2: accept that 35 MB IS the cost of native .NET 8 + WinRT
projection access.** Note that:

- v12.0.1 PS tray dist is comparable (PS itself bundled into
  install ~10-15 MB; tray.ps1 ~10000 lines + tray_native.dll +
  PowerShell runtime support)
- v14 .NET 8 tray's 35 MB on a multi-MB MSI for an app that ships
  Audio Spectrum + Server.exe + WebView2 + Discord RPC is not
  outsized
- Per `V14_S7_S7_5B_FINAL_REPORT.md` (already authored by 7.5B):
  **"Tradeoff accepted: SMTC arm operational outweighs size cost."**
  That tradeoff is still correct.

---

## 5. Recommendation

**STAY WITH WPF-UI (Alternative A).**

### Reasoning

1. **Dist size is not the binding constraint.** The 24 MB CSWinRT
   projection dominates; framework choice can save at most 6.78 MB
   (19% of dist). The remaining 28.55 MB is structural to .NET 8 +
   SMTC + WPF.

2. **Migration cost vs savings is poor.** Alternative B (ModernWpf)
   saves 5 MB at 12h migration cost. Alternative C (raw WPF) saves
   6.78 MB at 60-120h. Alternative D (ditch design) saves 6.78 MB
   at 4h but breaches the "GUI has to be good" brief.

3. **Design fit favours WPF-UI.** Per `V14_S7_REPLAN_WPF_LOCK.md`
   section 2.2, WPF-UI is the closest match to "Win11 Fluent +
   Discord/Spotify dense-dark blend." That re-plan analysis, done
   with deliberate framework survey, still holds.

4. **The 7.5B observed leak is NOT the framework's fault.** Per
   `V14_S7_S7_5C_RCW_AUDIT.md`, the 216 MB/h growth source is
   not pinpointed in this brief. WPF-UI is one of multiple
   suspects but not implicated by direct evidence. Switching
   frameworks before identifying the leak's actual source could
   simply move the symptom without fixing it.

5. **Maintenance / maintainer risk is acceptable.** WPF-UI is
   actively maintained as of 2025-2026; MIT-licensed; pinned to
   4.3.0 (a known-good version) at 7.1B. Pinning is the appropriate
   risk mitigation; replacing the library would add NEW maintainer
   risk for ModernWpf (smaller community) or raw-WPF (longer-term
   internal maintenance).

6. **Brief 2 has higher-leverage targets.** If 7.5C's 60-90 min
   soak confirms the leak, the next investigation should focus on:
   - DiagnosticHeartbeat instrumentation expansion (managed-heap
     vs unmanaged-WS separation)
   - Finalizer queue dynamics (`GC.WaitForPendingFinalizers` test)
   - Active-vs-idle soak comparison (Workstream 3)
   - PS tray-vs-C#-tray comparison on same hardware
   - Possibly framework-replacement test (substitute ModernWpf
     temporarily, observe whether leak persists; this is a
     diagnostic, not a permanent migration)

   None of these require committing to a framework swap up front.

### Conditional alternative

**IF Brief 2's investigation surfaces evidence that WPF-UI is
implicated in the leak** (e.g., observable allocation patterns
correlate with WPF-UI's resource pump), THEN evaluating ModernWpf
or roll-your-own becomes a valid Brief 2+ target. Until that
evidence exists, switching is premature.

---

## 6. Hidden cost not in the cost-benefit table

Switching frameworks mid-stage carries non-obvious costs:

- **Designer mode loss**: WPF-UI ships designer-time previewing
  for its Fluent controls. Replacing means losing the in-IDE
  preview experience for any surfaces yet to be authored
  (Surfaces 1-6 land in 7.6/7.7/7.8).
- **Brand consistency**: the brand-purple `#9333EA` accent override
  is already wired to WPF-UI's `AccentColors` resource dictionary.
  Re-wiring to ModernWpf (different accent system) or raw WPF
  (manual everywhere) is not zero-cost.
- **Future surfaces**: 7.6/7.7/7.8 will author 4-5 new XAML
  surfaces. If they assume WPF-UI controls (NavigationView,
  Card, InfoBar, ContentDialog, ProgressRing) and we then
  switch frameworks, those surfaces would need re-implementation.
  Cost compounds.
- **Documentation drift**: `V14_S7_REPLAN_WPF_LOCK.md`,
  `V14_S7_S7_1B_FINAL_REPORT.md`, and other artifacts cite WPF-UI
  by name. Switching introduces documentation lag.

These are real costs not captured in raw "MB saved per migration
hour" math.

---

## 7. Sanity check vs `V14_S7_REPLAN_WPF_LOCK.md`

That document recommended the current stack with the analysis:

> WPF + `H.NotifyIcon.Wpf` + `WPF-UI` + `CommunityToolkit.Mvvm` +
> `Microsoft.Extensions.DependencyInjection`, with pragmatic MVVM.

The 7.5C analysis CONFIRMS this recommendation under the new dist
size data. Distribution shape miss (5-10 MB projected vs 35 MB
actual) is entirely on `Microsoft.Windows.SDK.NET.dll`, which is
a TFM-upgrade artifact orthogonal to framework choice. The WPF-UI
portion of the dist (6.78 MB) is INSIDE the original projection's
upper bound (5-10 MB combined; non-WPF-UI WPF runtime + NuGets
account for ~4.6 MB, WPF-UI accounts for the rest).

---

## 8. Open questions for Orken (accumulated)

- **Q-FW-1**: Does Orken want a one-time "WPF-UI as diagnostic
  isolation test" Brief 2 task to substitute ModernWpf and
  re-soak, observing whether the 216 MB/h growth changes?
  Default: defer until 7.5C STEP 1 completes; if leak reproduces
  AND no obvious in-arch fix surfaces, this becomes a useful
  Brief 2 task.

- **Q-FW-2**: Does Orken want the dist size pursued via the only
  remaining material lever (Path 1 reverse-CSWinRT)? Default:
  NO. The Stage 7.5B-time finding ("reflection-based path
  provably did not work even after 7.5's attempt") rules this out
  unless a new approach is identified.

- **Q-FW-3**: Is there a hard MB ceiling for the v14 ship that
  35.34 MB exceeds? Default: NO; 35 MB is comparable to the
  v12.0.1 install footprint when PowerShell runtime support and
  WebView2 are counted.

---

## 9. Conclusion

**WPF-UI stays.** The 35.34 MB dist is structural; framework
choice cannot meaningfully reduce it. Migration cost vs savings
is poor across all alternatives. The 216 MB/h leak is not WPF-UI-
implicated by the available evidence; switching frameworks would
be a guess, not a fix.

Brief 2's leak investigation should expand the locked-list to
allow DiagnosticHeartbeat instrumentation, GC test calls, and
possibly a framework-substitution diagnostic test (NOT a permanent
switch). Workstream 3 of THIS brief contributes to that diagnosis
via active-vs-idle soak comparison.

NO framework switch executed in 7.5C. Document concludes here.
