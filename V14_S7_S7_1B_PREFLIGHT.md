# V14_S7_S7_1B_PREFLIGHT.md

Stage 7.1B pre-flight. Captures STEP 0 outputs.

## 0.1 Working directory

`G:\Project Folder\Master FM\` -- confirmed.

## 0.2 Process check

No tray / server / build processes running:
- `server` not running
- `MastersFM_Tray` not running
- `MastersFM_Tray_v14` not running
- `audio_spectrum` not running
- `customize` not running

Safe to proceed without disrupting any running instance.

## 0.3 sha256 baseline

Captured in `V14_S7_S7_1B_PROTECTED_BASELINE.md`. All 5 protected
files baselined; `md\memory.md` is at post-Stage-7.1-APPEND state
(matches re-plan baseline; re-plan was read-only).

## 0.4 Repo state

| Item | Status |
|---|---|
| HEAD | `5fd9c8a` -- Stage 7.1 memory.md APPEND |
| HEAD-1 | `f7bb96e` -- Stage 7.1 skeleton + build flag + audit docs |
| Tag `v14.0.0-rc.1` | `44723fb` -- intact, NOT pushed |
| 7.1 commits | `f7bb96e` + `5fd9c8a` PRESENT (must NOT be amended/deleted per Q-SUBSTAGE-1) |
| `version.json` | modified-unstaged (RC1 deferral state; carry-forward) |
| `src/tray_csharp/` | EXISTS with WinForms skeleton (will be REPLACED in STEP 2) |
| `dist/tray_csharp_release/` | may contain stale WinForms artifacts from prior 7.1 runs (will be overwritten by STEP 5 publish) |

Stage 7.1 src/tray_csharp/ tree (current):
- `MastersFM_Tray_v14.csproj` (2,114 bytes; WinForms; will be REPLACED)
- `Program.cs` (4,499 bytes; WinForms; will be DELETED)
- `TrayApp.cs` (2,610 bytes; WinForms; will be DELETED)
- `Logger.cs` (3,844 bytes; pure POCO; will be PRESERVED unchanged)
- 6 `.gitkeep` files in placeholder dirs (will be PRESERVED unchanged)

## 0.5 Reading status

| Required | File | Status |
|---|---|---|
| Yes | `memory.md` | Read (post-7.1 APPEND state confirmed; sha256 unchanged) |
| Yes | `V14_S7_REPLAN_FINAL_REPORT.md` | Read |
| Yes | `V14_S7_REPLAN_WPF_LOCK.md` | Read (NuGet stack + reasoning) |
| Yes | `V14_S7_REPLAN_DESIGN_LANGUAGE.md` | Read (brand purple `#9333EA`; supporting `#C084FC` / `#6D28D9`) |
| Yes | `V14_S7_REPLAN_SKELETON_PLAN.md` | Read (the spec for this brief) |
| Yes | `V14_S7_S7_1_FINAL_REPORT.md` | Read (the WinForms 7.1 being replaced) |
| Yes | `src/tray_csharp/Program.cs` | Read (mutex + AUMID + exception-hook patterns) |
| Yes | `src/tray_csharp/TrayApp.cs` | Read (tooltip "Master's FM v14 dev (skeleton)" at line 21; Quit menu pattern) |
| Yes | `src/tray_csharp/Logger.cs` | Read (PRESERVED unchanged; pure POCO; no WinForms refs; preservation check PASS) |
| Yes | `src/tray_csharp/MastersFM_Tray_v14.csproj` | Read (WinForms version; will be REPLACED) |
| Yes | `src/launcher.csproj` | Read (multi-file publish precedent) |
| Yes | `_full_rebuild.ps1` flag block + publish block | Located (lines 38-46 flag, lines 253-272 publish) |

## 0.6 Logger.cs preservation check (per STEP 2.8)

`src/tray_csharp/Logger.cs` reviewed:
- Pure POCO; uses only `System.Collections.Generic`, `System.Text`, `System.IO`, `System.Diagnostics`, `System`.
- NO WinForms reference (no `using System.Windows.Forms`).
- NO XAML / WPF reference.
- `[TRAY-CS]` log prefix preserved.
- UTF-8 no BOM enforced via `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`.
- Ring buffer `Queue<string>` cap 20 placeholder for 7.3.

Preservation check: PASS. Logger.cs ports unchanged to WPF.

## 0.7 Default decisions

Per brief OPEN QUESTIONS PRE-EXECUTION (lines 520-530):

1. **Topmost on MainWindow**: Default YES (`Topmost="False"` explicit; defensive against any inheritance from WPF-UI styles).
2. **App.xaml resource override scope**: Default override only the brand-accent palette (`#9333EA` accent + `#C084FC` bright + `#6D28D9` deep per design language section 2.2). Other WPF-UI Fluent Dark colors stay default; per-surface tokens land in 7.6+.
3. **AUMID DllImport verification**: Default YES; verify AUMID call succeeds and logs success line.
4. **Build pipeline assertion printing pinned NuGet versions**: Default YES; add `Write-Host` line in `_full_rebuild.ps1` publish block that emits the four pinned versions for build-log audit.

## 0.8 Budget posture

Active work budget: 12-18h realistic, 24h hard cap. User flagged
50% Claude Max headroom at brief launch. STEPS 0-1 consumed
estimated ~10-15 min so far (~1% of total budget).

Halt at 80% headroom; halt at any SAFETY FLOOR trigger; halt on
three-strike per task.
