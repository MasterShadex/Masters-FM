# V14_S7_S7_7_PREFLIGHT.md

Stage 7.7 dialogs + heartbeat + art infrastructure pre-flight.

## Time budget

- Brief launch: 2026-05-08 ~15:58
- Realistic per brief: 2-6h Ruflo wall-clock
- Hard cap: 80h

## 0.1 -- 0.2 CWD + processes

CWD: `G:\Project Folder\Master FM\` confirmed.

Process state (clean slate):
- C# tray NOT running
- PS tray NOT running

## 0.3 sha256 baseline

5 protected files snapshotted in `V14_S7_S7_7_PROTECTED_BASELINE.md`.
Re-verified at STEP 14.

## 0.4 Repo state

- HEAD = `8f5b932`
- Stage 7.5C commits both present (`210cf58` + `8f5b932`)
- All prior Stage 7 commits preserved
- `v14.0.0-rc.1` tag at `44723fb` (untouched)

## 0.5 References reviewed

- Brief `CLAUDE_CODE_INSTRUCTIONS.md` (full)
- `V14_S7_REPLAN_MOCKUPS.md` Surfaces 04 / 05 / 06 / 09 / 10 / 11 (full)
- `V14_S7_REPLAN_DESIGN_LANGUAGE.md` (full)
- `V14_S7_S7_5C_FINAL_REPORT.md` (Q-RCW-2 follow-up reviewed)
- `src/tray.ps1` PATCH_HISTORY (lines 261-1445; 292 versions; ~339 KB)
- `src/tray_csharp/App.xaml`, `App.xaml.cs`, `MastersFM_Tray_v14.csproj`,
  `Detectors/TrackUpdate.cs` (full)

## 0.6 Workstream plan

**Workstream 1**: 6 dialog surfaces + IDialogService
- Surface 04 Welcome + Surface 10 About (embedded tab)
- Surface 05 Audio device (WinRT enumeration via `Windows.Devices.Enumeration`)
- Surface 06 Platforms (8 toggles, all-ON default on first-run)
- Surface 09 Setup wizard (3-step internal navigation)
- Surface 11 Error dialog
- IDialogService + DialogService

**Workstream 2**: DiagnosticHeartbeat expansion
- Add `gc` (GC.GetTotalMemory), `priv` (PrivateMemorySize64), `polls` (last/slowest detector poll-ms)

**Workstream 3**: Art extraction infrastructure
- SmtcEventBridge calls TryGetThumbnailAsync, encodes base64 data URI
- TrackResolver wires ArtLruCache.Touch
- B-013 empirical closure verified via cache stats

## 0.7 Default decisions

Per brief OPEN QUESTIONS PRE-EXECUTION:

- Q1 Setup wizard: single window with internal frame swap (cleaner UX)
- Q2 Stereo Mix banner: shown if not enabled in current Output device list
- Q3 About panel "show technical details" disclosure: visible by default
- Q4 Error dialog: info icon + brand purple, NOT destructive red
- Q5 Patch notes: 339 KB tray.ps1 form. Decision: extract to JSON for
  embedding. JSON form expected ~250-300 KB; over the 200 KB threshold
  in OPEN QUESTION 5 default. Alternative: subset to most-recent N
  versions to fit under 200 KB.
  **DECISION**: ship the FULL set as embedded resource. Even at 250-300
  KB, modern app dist tolerates it; load is one-shot at first dialog
  show; virtualization handles render perf. Document the deviation
  prominently if compressed JSON exceeds 200 KB.
- Q6 Thumbnail extraction graceful: try/catch + log + null ArtUri
- Q7 First-run delay: 200ms (default)

## Em-dash + BOM constraints

- All deliverable docs UTF-8 no BOM
- All authored content uses `--` instead of em-dash
- New C# / XAML files: UTF-8 no BOM

## sha256 / BOM verification cadence

- STEP 0.3 (now): baseline captured
- STEP 13: pre-commit verification
- STEP 14: post-commit final verification
