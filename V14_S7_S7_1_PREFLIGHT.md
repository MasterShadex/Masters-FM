# V14_S7_S7_1_PREFLIGHT.md

Stage 7.1 (C# tray skeleton) -- pre-flight summary.

## STEP 0 results

### 0.1 Working directory
`G:\Project Folder\Master FM\` confirmed.

### 0.2 Process state
Pre-cleanup: server, MastersFM, MastersFM_Tray, audio_spectrum all RUNNING (post-prior-rebuild
state). Cleanly stopped via `Stop-Process -Force`. All target processes confirmed
NOT_RUNNING after cleanup. No `MastersFM_Tray_v14` running (expected; doesn't exist yet).
Build paths unblocked.

### 0.3 Protected sha256 baseline
Captured in `V14_S7_S7_1_PROTECTED_BASELINE.md`. Five files match the Phase 1 baseline
exactly (no changes since Phase 1 ended). The only protected file expected to change in
this brief is `md/memory.md` per STEP 7 APPEND.

### 0.4 Repo state

| Position | Commit | Subject |
|---|---|---|
| HEAD | `27c0d9d` | v14.0.0-rc.1: memory.md CURRENT STATE deferral note |
| HEAD~1 | `a76ab4e` | v14.0.0-rc.1: final report - Push deferred section |
| HEAD~2 | `44723fb` | v14.0.0-rc.1: memory.md APPEND |
| HEAD~3 | `02da91a` | v14.0.0-rc.1: Stages 1-3 + 4 + 5 cumulative migration |
| HEAD~4 | `3cb9eed` | v12.0.1 - memory.md + final report |

Tag `v14.0.0-rc.1` (annotated, object SHA `4fcb899`) -> commit `44723fb`. UNCHANGED.

git status: 4 modified, 0 deleted, 122 untracked (Phase 1 deliverables + prior-state
files; consistent with post-Phase-1 entry state). Modified files:
- `.claude/scheduled_tasks.lock` (denylist)
- `.claude/settings.json` (denylist)
- `CLAUDE.md` (denylist; Ruflo-rewrite, NOT V14)
- `version.json` (Addition A safety; remains pinned to v12.0.1 on main)

### 0.5 Mandatory reads -- status

All in working memory from prior sessions in this conversation:

- `md/memory.md` -- read post-deferral state
- `V14_S7_P1_FINAL_REPORT.md` -- written this conversation
- `V14_S7_P1_TRAY_INVENTORY.md` -- written this conversation; S1 Bootstrap/Init detail
  (lines 1-249 of tray.ps1) plus S2-S10 project layout proposals
- `V14_S7_P1_SUBSTAGE_BREAKDOWN.md` 7.1 entry: skeleton scope confirmed (12-18h, HIGH
  confidence)
- `V14_S7_P1_VALIDATION_DESIGN.md` 7.1 contract: pre-conditions, active work, pass
  criteria, halt triggers
- `V14_S7_P1_UI_FRAMEWORK_SURVEY.md` -- WinForms section
- `_full_rebuild.ps1` build flag pattern: `$UseDotnet8Server`, `$UseDotnet8Launcher`,
  `$UseDotnet8AudioSpectrum`, `$UseDotnet8Customize`, `$UseDotnet8Bootstrapper`,
  `$UseDotnetTrayNative` -- header comment block at lines 7-14, individual flags 19-37,
  build blocks scattered throughout
- `src/launcher.csproj` (this brief): pattern reference for net8.0-windows + WinForms +
  R2R + framework-dependent x64 + ApplicationIcon
- `src/server_dotnet/server_dotnet.csproj` (read in earlier conversation): pattern
  reference for ASP.NET Core minimal-API; less directly applicable but informs csproj
  discipline
- `src/Directory.Build.props`: BaseIntermediateOutputPath = ..\obj\$(MSBuildProjectName)\
  -- inherited automatically by tray_csharp project

### 0.6 Locked decisions from Phase 1 review (per brief PROLOGUE)

- Strategy C (parallel sibling project) confirmed
- WinForms confirmed (net8.0-windows; uses System.Windows.Forms)
- Strict sequential sub-stages: 7.1 -> 7.2 -> 7.3 -> 7.4 -> [STAGE 7.5 PHASE 1 RESEARCH] -> 7.5 -> ... -> 7.10
- Opt-in Orken-only parallel period; testers stay on PS tray until 7.10 cutover
- Memory target: 50-80 MB steady, no growth past plateau (validated at 7.10)
- Cumulate Stage 7 + Stage 8 ship as v14.0.0 stable
- Detection layer (7.5) gets Phase 1 research brief AFTER 7.4; do NOT pre-design in 7.1

### 0.7 Pre-execution open questions (defaults applied)

Per brief OPEN QUESTIONS PRE-EXECUTION:

1. **Q1 (tray_native.dll reference for AUMID):** DEFAULT applied -- TRY referencing
   `tray_native.dll` (netstandard2.0) from net8.0-windows skeleton; if it works, use
   `MFM_Shell.SetCurrentProcessExplicitAppUserModelID` for parity. If not, fall back to
   direct Shell32 P/Invoke. Will document outcome in final report.

2. **Q2 (overlay.log truncation):** DEFAULT applied -- append-only with `[TRAY-CS]`
   prefix. PS tray's truncation behavior would conflict during dev parallel period.
   Will document in final report.

3. **Q3 (tray icon):** DEFAULT applied -- reuse existing `assets/MastersFM.ico` (the
   actual filename in this project; brief said `tray.ico` but the file is `MastersFM.ico`).
   Tooltip text "Master's FM v14 dev (skeleton)" is the visual differentiator. Will
   document in final report.

### 0.8 Phase 7.1 budget tracker

Hard cap: 24h.
Started: 2026-05-08 02:36.
Per-step estimates (per brief TIME BUDGET):

| Step | Estimate |
|---|---|
| Pre-flight + reading | 1-2h |
| STEP 1: project skeleton + Program.cs + TrayApp.cs + Logger.cs | 3-5h |
| STEP 2: build flag wiring + verification | 1-2h |
| STEP 3: regression build (flag off) | 0.5h |
| STEP 4: build with flag on | 0.5h |
| STEP 5: skeleton smoke + 30-min light-touch | 1h (mostly wall-clock) |
| STEP 6-9: documentation + git + verification | 2-3h |
| Buffer | 2-3h |

### 0.9 Pre-flight verification at this gate

- Working dir: G:\Project Folder\Master FM\ ✓
- All app processes stopped: ✓
- Protected sha256 baseline captured: ✓ (matches Phase 1 baseline)
- Repo state: clean from RC1 deferral + Phase 1; HEAD=27c0d9d; tag v14.0.0-rc.1 -> 44723fb intact ✓
- Mandatory context loaded (prior session memory + new reads of launcher.csproj +
  Directory.Build.props): ✓
- Locked decisions from Phase 1 review: noted ✓
- Pre-execution defaults applied: noted ✓
- No source modifications yet: ✓
- No builds, tests, commits: ✓

Proceeding to STEP 1: project skeleton creation.
