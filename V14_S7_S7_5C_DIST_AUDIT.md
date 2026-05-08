# V14_S7_S7_5C_DIST_AUDIT.md

Stage 7.5C Workstream 2 -- distribution size composition audit.

Inventory taken 2026-05-08 09:01 from
`G:\Project Folder\Master FM\dist\tray_csharp_release\`. The dist
shipped from 7.5B's TFM-upgrade build (08:20:00).

---

## 1. Full file inventory by size (descending)

| Rank | File | Size (MB) | Size (KB) | Category |
|---:|---|---:|---:|---|
| 1 | `Microsoft.Windows.SDK.NET.dll` | 23.725 | 24,294.5 | WinRT projection |
| 2 | `Wpf.Ui.dll` | 6.758 | 6,920.0 | WPF-UI control library |
| 3 | `System.Private.Windows.Core.dll` | 1.129 | 1,156.0 | WPF runtime |
| 4 | `System.Drawing.Common.dll` | 1.090 | 1,116.0 | WPF runtime / icon support |
| 5 | `WinRT.Runtime.dll` | 0.504 | 516.5 | WinRT runtime |
| 6 | `H.NotifyIcon.dll` | 0.453 | 464.0 | Tray icon (core) |
| 7 | `CommunityToolkit.Mvvm.dll` | 0.363 | 372.0 | MVVM helper |
| 8 | `MastersFM_Tray_v14.dll` | 0.285 | 292.0 | App code (managed) |
| 9 | `H.NotifyIcon.Wpf.dll` | 0.277 | 284.0 | Tray icon (WPF integration) |
| 10 | `Microsoft.Extensions.DependencyInjection.dll` | 0.195 | 200.0 | DI container |
| 11 | `MastersFM_Tray_v14.exe` | 0.156 | 160.0 | App entry point |
| 12 | `Microsoft.Extensions.DependencyInjection.Abstractions.dll` | 0.125 | 128.0 | DI abstractions |
| 13 | `Microsoft.Win32.SystemEvents.dll` | 0.082 | 84.0 | WPF support |
| 14 | `tray_native.dll` | 0.062 | 64.0 | tray_native (preserved across briefs) |
| 15 | `MastersFM_Tray_v14.pdb` | 0.052 | 53.5 | Debug symbols (app) |
| 16 | `H.GeneratedIcons.System.Drawing.dll` | 0.035 | 36.0 | Tray icon (codegen) |
| 17 | `Wpf.Ui.Abstractions.dll` | 0.020 | 20.0 | WPF-UI abstractions |
| 18 | `tray_native.pdb` | 0.013 | 13.5 | Debug symbols (native) |
| 19 | `MastersFM_Tray_v14.deps.json` | 0.008 | 8.0 | Runtime config |
| 20 | `MastersFM_Tray_v14.runtimeconfig.json` | 0.001 | 0.6 | Runtime config |

**Total: 35.33 MB across 20 files.**

Subdirectory check: none. Localization satellite assemblies: none.
XML doc files: none. The dist is flat.

---

## 2. Composition breakdown by category

| Category | Files | Size (MB) | % of total |
|---|---:|---:|---:|
| **WinRT projection** | 1 | 23.725 | 67.1% |
| **WPF-UI library** | 2 | 6.778 | 19.2% |
| **WPF runtime** | 3 | 2.301 | 6.5% |
| **WinRT runtime** | 1 | 0.504 | 1.4% |
| **Tray icon (H.NotifyIcon family)** | 3 | 0.765 | 2.2% |
| **MVVM (CommunityToolkit)** | 1 | 0.363 | 1.0% |
| **DI (Microsoft.Extensions)** | 2 | 0.320 | 0.9% |
| **App code (managed dll + exe)** | 2 | 0.441 | 1.2% |
| **tray_native.dll** | 1 | 0.062 | 0.2% |
| **Debug symbols (PDBs)** | 2 | 0.065 | 0.2% |
| **Runtime config (json)** | 2 | 0.009 | 0.0% |
| TOTAL | 20 | 35.33 | 100% |

The CSWinRT projection (`Microsoft.Windows.SDK.NET.dll`) alone is
67% of the dist. Without it the SMTC arm cannot work (WinRT call
sites need the projection).

WPF-UI (the framework choice in question for Workstream 2) is 19%
of the dist -- 6.78 MB across `Wpf.Ui.dll` (6.76) +
`Wpf.Ui.Abstractions.dll` (0.02).

WPF runtime overhead (`System.Private.Windows.Core.dll` +
`System.Drawing.Common.dll` + `Microsoft.Win32.SystemEvents.dll`)
is ~2.3 MB; this stays under any WPF-based framework (WPF-UI vs
ModernWpf vs roll-your-own raw WPF).

---

## 3. Bundling waste identified

### 3.1 Debug symbols shipped (PDBs)

Two PDBs ship: `MastersFM_Tray_v14.pdb` (54 KB) +
`tray_native.pdb` (14 KB) = 67 KB total. Trivial absolute size; but
they are debug symbols which:
- Aid post-mortem analysis (so there is a case for keeping them)
- Increase trust if Bitdefender heuristics inspect them
- Do NOT compromise security (PDBs do not contain source)

**Recommendation: KEEP.** Cost is negligible (0.07 MB / 0.2%). The
diagnostic value during incident response (e.g., reading a managed
stack frame from a customer-shipped crash) is real. PowerShell
`tray_native.pdb` precedent is to ship the same. Stripping would
save 67 KB on a 35 MB dist (0.2%).

If desired anyway, build-script change at `MastersFM_Tray_v14.csproj`
add `<DebugType>none</DebugType>` to Release. Out of locked-list for
this brief.

### 3.2 Localization satellite assemblies

None present. Nothing to trim.

### 3.3 XML documentation files

None present. Nothing to trim.

### 3.4 Duplicate DLLs

None. Inventory checked file-by-file.

### 3.5 Test / sample binaries from NuGets

None. WPF-UI's NuGet does not bundle the Gallery sample; only
`Wpf.Ui.dll` + `Wpf.Ui.Abstractions.dll` shipped.

### 3.6 Unused NuGet pieces

`Microsoft.Extensions.DependencyInjection.Abstractions.dll` (128 KB)
is required by `Microsoft.Extensions.DependencyInjection.dll`;
no waste.

`H.GeneratedIcons.System.Drawing.dll` (36 KB) is generated icon code
required by `H.NotifyIcon.Wpf`. The H.NotifyIcon family ships three
files because the icon bridging is split across abstractions,
generated code, and the WPF binding. Not waste; structural.

`Microsoft.Win32.SystemEvents.dll` (84 KB) is a transitive of
`System.Drawing.Common`. The `SystemEvents.UserPreferenceChanged`
event is genuinely used by `WPF-UI` for Mica accent updates and by
`System.Drawing.Common` for screen-DPI refresh. Required.

**Conclusion: zero meaningful bundling waste in the dist.** The
35.33 MB is structural; the only trim path is changing dependencies.

---

## 4. Realistic savings WITHOUT changing framework

| Action | Savings | Note |
|---|---:|---|
| Strip both PDBs | 0.07 MB | Diagnostic loss; trivial size win |
| Strip XML/locale | 0 MB | Nothing to strip |
| Trim WinRT projection | 0 MB | Required for SMTC; no sane trim path |
| Trim WPF runtime | 0 MB | Required for WPF; no sane trim path |
| **Total** | **~0.07 MB** | **0.2% of dist** |

Without changing framework, there is nothing meaningful to trim. The
dist is at floor for the current architecture.

---

## 5. Realistic savings BY changing framework

### 5.1 Replace WPF-UI with ModernWpf

ModernWpf NuGet ships a single `ModernWpf.dll` typically in the
1-2 MB range (vs WPF-UI's 6.78 MB).

**Estimated savings: ~5 MB (4-6 MB realistic range).** Dist would
drop from 35.33 to ~30 MB (~85% of current). Still 23.7 MB
CSWinRT-dominated.

**Migration cost**: Reworking `App.xaml` resource dictionaries,
re-styling the brand-purple `#9333EA` accent override, retesting
the `UpdateProgressWindow` (the only XAML window so far that uses
WPF-UI controls), validating Mica/Acrylic feature parity. Estimate
8-16 hours one-time.

### 5.2 Replace WPF-UI with raw WPF + custom styles

Removes both `Wpf.Ui.dll` (6.76 MB) and `Wpf.Ui.Abstractions.dll`
(0.02 MB) entirely.

**Estimated savings: ~6.78 MB.** Dist would drop from 35.33 to
~28.55 MB (~81% of current). Still 23.7 MB CSWinRT-dominated.

**Migration cost**: Build a styling layer from scratch -- focus
rings, hover, pressed, disabled states for every control type the
app uses (Buttons, ToggleButtons, ProgressBars, Sliders, ComboBoxes,
TextBoxes), produce the brand-purple palette manually, replicate
the few WPF-UI surfaces actually used. Per `V14_S7_REPLAN_WPF_LOCK.md`
section 2.2 ("Roll-your-own"), 3-5 weeks. Realistic 60-120 hours.

### 5.3 Replace WPF-UI with Hardcodet's Wpf only (no Fluent layer)

Strip WPF-UI entirely; keep Mica/Acrylic out; ship plain WPF default
chrome.

**Estimated savings: ~6.78 MB.** Same as 5.2 in numbers, lower
implementation cost (no styling layer needed) but loses the design
ambition. Tray-app aesthetic regresses to "Windows 7 default chrome."
Off-brief because Stage 7's design ambition is "GUI has to be good."

**Migration cost**: ~4 hours (delete WPF-UI references, accept stock
chrome). NOT recommended for design reasons.

---

## 6. Recommendation summary

The dist is structurally close to floor. The only meaningful size
lever is the WPF-UI framework choice, which would save 5-7 MB
(15-20% of dist) at the cost of:

- ModernWpf: 8-16h migration; design fidelity slightly reduced
- raw WPF: 60-120h migration; design fidelity manual to recreate
- ditching design ambition: 4h; design quality drops materially

The CSWinRT projection (24 MB) is a fixed cost of any .NET 8 +
SMTC-via-WinRT-projection approach. Path to remove it would be
back to PS S15-style reflection (not allowed in 7.5B's locked-list,
and the reflection path provably did not work even after 7.5's
attempt).

**See `V14_S7_S7_5C_FRAMEWORK_RECOMMENDATION.md` for the framework
choice analysis combining design fit + dist size + maintenance
considerations.** This document is the size-only side of the
analysis.

---

## 7. Comparison: 7.5 (pre-TFM) vs 7.5B / 7.5C (post-TFM)

| Metric | Stage 7.5 (pre-TFM) | Stage 7.5B / 7.5C (post-TFM) | Delta |
|---|---:|---:|---:|
| Total dist | ~11.03 MB | 35.33 MB | +24.3 MB |
| `Microsoft.Windows.SDK.NET.dll` | 0 | 23.73 MB | +23.73 MB |
| `Wpf.Ui.dll` | 6.76 MB | 6.76 MB | 0 |
| WinRT.Runtime.dll | 1.0 MB | 0.50 MB | -0.50 MB (different version pulled in) |
| `tray_native.dll` | 64 KB | 64 KB | 0 |

The 7.5 -> 7.5B size jump was almost entirely the `Microsoft.Windows.SDK.NET.dll`
projection assembly. The TFM upgrade enabled SMTC arm at the cost of
24 MB of projection metadata. There is no obvious way to deliver
SMTC-via-WinRT-projection cheaper than this; the projection assembly
is the .NET 8 + Windows SDK contract.

Pre-TFM 7.5 dist was 11 MB but had no working SMTC arm (the WinRT
type wasn't projected; reflection-based lookup returned null). The
+24 MB was the cost of making the architecture actually work.

---

## 8. Sanity check vs `V14_S7_REPLAN_WPF_LOCK.md` projection

That document (Stage 7 RE-PLAN, pre-TFM) projected:
> Distribution shape: ~5-10 MB across ~10-15 files in
> `dist/tray_csharp_release/`.

Reality: 35.33 MB across 20 files. Overshoot factor: 3-7x.

The miss is entirely on the CSWinRT projection assembly which the
RE-PLAN authors did not anticipate (TFM upgrade was a 7.5B-time
decision). The non-WinRT portion of the dist (~11.6 MB) is within
the original projection's upper bound.

---

End of dist audit.
