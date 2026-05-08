# V14_S7_S7_2_PREFLIGHT.md

Stage 7.2 pre-flight (STEP 0 outputs).

## 0.1 CWD
`G:\Project Folder\Master FM\` confirmed.

## 0.2 Process check
No conflicting processes running.

## 0.3 sha256 baseline
See `V14_S7_S7_2_PROTECTED_BASELINE.md`. memory.md at post-7.4-APPEND state.

## 0.4 Repo state
HEAD = `3818760` (Stage 7.4 memory.md APPEND). All prior Stage 7 commits
preserved (7.1, 7.1B, 7.3, 7.4). Tag `v14.0.0-rc.1` at `44723fb`.
`src/tray_csharp/Update/` dir with `.gitkeep` placeholder (will be
populated and .gitkeep deleted in 7.2).

## 0.5 Reading status
- `tray.ps1:5187-5798` (S12) read in full; spec captured in
  `V14_S7_S7_2_STATE_MACHINE_SPEC.md`
- All re-plan deliverables read (FINAL_REPORT, MOCKUPS Surface 07, 
  DESIGN_LANGUAGE sections 1-3, RISKS R6, SUBSTAGE_BREAKDOWN 7.2 entry)
- 7.4 deliverables read (FINAL_REPORT, CONFIG_SCHEMA)
- 7.3 ILogger / IConfigService DI dependencies confirmed

## 0.6 Locked decisions reaffirmed
- Q1: Surface 07 redesign in 7.2 (UpdateProgressWindow lands)
- Q2: Tray menu label only; NO balloon/toast for update notifications
- Q3: Strict pre-release rejection regex; no opt-in flag
- All prior locks (WPF + WPF-UI + H.NotifyIcon.Wpf + CommunityToolkit.Mvvm
  + Microsoft.Extensions.DependencyInjection + brand-purple #9333EA)

## 0.7 Default decisions
- Self-test runs every startup (R6 monitoring; negligible noise)
- UpdateProgressWindow modeless (matches PS S12 behaviour)
- "What's New" plain text "see GitHub releases page for v{version}"
- Cancel during Downloading deletes partial download (no resume)
- No `GetLastNVersionsChecked()` debug accessor (out of 7.2 scope)

## 0.8 Budget posture
24-34h realistic, 48h hard cap. Lessons from 7.1B-7.4 (XML escaping,
implicit usings, pack-URI icons, ConfigService schema mapping)
prevent equivalent strikes; targeting first-attempt success on most
paths. R6 closure tests + mocked HTTP tests are the highest-risk
items.
