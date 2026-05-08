# V14_S7_S7_3_PREFLIGHT.md

Stage 7.3 pre-flight (STEP 0 outputs).

## 0.1 Working directory

`G:\Project Folder\Master FM\` -- confirmed.

## 0.2 Process check

No conflicting processes running:
- `server` not running
- `MastersFM_Tray` not running
- `MastersFM_Tray_v14` not running
- `audio_spectrum` not running
- `customize` not running

## 0.3 sha256 baseline

See `V14_S7_S7_3_PROTECTED_BASELINE.md`. All 5 protected files
baselined; `md\memory.md` at post-Stage-7.1B-APPEND state.

## 0.4 Repo state

| Item | Status |
|---|---|
| HEAD | `75b38cf` -- Stage 7.1B memory.md APPEND |
| HEAD-1 | `b7eca5b` -- Stage 7.1B WPF skeleton |
| HEAD-2 | `5fd9c8a` -- Stage 7.1 memory.md APPEND |
| HEAD-3 | `f7bb96e` -- Stage 7.1 WinForms skeleton |
| Tag `v14.0.0-rc.1` | `44723fb` (intact, NOT pushed) |
| Stage 7.1 commits (`f7bb96e` + `5fd9c8a`) | preserved (DO NOT amend) |
| Stage 7.1B commits (`b7eca5b` + `75b38cf`) | the 7.3 foundation |
| `version.json` | modified-unstaged (RC1 deferral state; carry-forward) |
| `src/tray_csharp/` | EXISTS with 7.1B WPF skeleton; 7.3 will refactor Logger.cs + add Services/* |

## 0.5 Reading status

| Required | File | Status |
|---|---|---|
| Yes | `memory.md` | Read (post-7.1B APPEND state) |
| Yes | `V14_S7_REPLAN_FINAL_REPORT.md` | Read |
| Yes | `V14_S7_REPLAN_DETECTION_REDESIGN.md` 8.1 arm 4 | Read (telemetry pipeline spec) |
| Yes | `V14_S7_REPLAN_SUBSTAGE_BREAKDOWN.md` 7.3 entry | Read |
| Yes | `V14_S7_S7_1B_FINAL_REPORT.md` | Read (especially O-1/O-2/O-3 to be closed) |
| Yes | `src/tray.ps1:8612-8754` (S16 patterns) | Cited in SlowTickWatchdog (200ms threshold) |
| Yes | `src/tray_csharp/Logger.cs` | Read (pre-refactor reference) |
| Yes | `src/tray_csharp/App.xaml.cs` | Read (DI insertion target) |
| Yes | `src/tray_csharp/MainWindow.xaml.cs` | Read (callsite update target) |

## 0.6 Default decisions

Per brief OPEN QUESTIONS PRE-EXECUTION:

1. **Logger startup-truncate-if-stale at 5 minutes** -- Default
   preserved (no change in 7.3); will revisit if soak shows
   unexpected mid-soak truncation. Note: STEP 4.4 parallel rebuild
   triggered an external truncation via PS tray's uninstall mode,
   but that's NOT the Logger's own truncation logic.

2. **DiagnosticHeartbeat skeleton-only mode marker** -- Default
   `mode=skeleton` string included in heartbeat lines. Lets log
   readers distinguish 7.3-era heartbeats from 7.5-era ones.

3. **SnapshotRingBuffer max-return parameter** -- Default no-param;
   always returns all 20 entries. Simpler API.

4. **Component segment capitalization** -- Default as-passed;
   PascalCase convention emerging in this brief: "Bootstrap",
   "Tray", "Diagnostic".

## 0.7 Budget posture

Active work budget: 6-10h realistic, 14h hard cap. Brief launched
during user vacation cycle; 50% Claude Max headroom at session
start (carry forward). Halt at 80% headroom; halt on SAFETY FLOOR.
