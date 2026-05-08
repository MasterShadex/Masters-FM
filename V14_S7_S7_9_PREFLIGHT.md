# V14_S7_S7_9_PREFLIGHT.md

Stage 7.9 pre-flight (STEP 0 outputs).

## 0.1-0.2 CWD + processes
`G:\Project Folder\Master FM\` confirmed; no conflicting processes.

## 0.3 sha256 baseline
See `V14_S7_S7_9_PROTECTED_BASELINE.md`.

## 0.4 Repo state
HEAD = `c82d767` (Stage 7.2 memory.md APPEND). All Stage 7 commits
preserved (7.1, 7.1B, 7.3, 7.4, 7.2). Tag v14.0.0-rc.1 at 44723fb.

## 0.5 Reading status
- `tray.ps1:3409-3531` (S7 Auto-Start) read in full
- `tray.ps1:4389-4519` (S9 Discord toggle + helpers) read in full
- `tray.ps1:3533+` (Show-OverlayCustomizer) read; uses Process.Start
  customize.exe with NO URL params (PS spawns customize.exe self-contained)
- `src/tray_csharp/Update/SemVerComparer.cs` read (target of one-line fix)
- All re-plan deliverables + 7.2 FINAL_REPORT (downgrade-prompt edge case)

## 0.6 Default decisions
- Q1 WindowStyle=7 (minimized) for AutoStart .lnk: YES (matches PS S7:3427)
- Q2 DiscordToggleService server-running pre-validation: NO (decoupled)
- Q3 Customizer URL token: same as PS (no URL params; customize.exe self-contained)
- Q4 16th SemVer test case (same RC vs same RC == 0): YES (added)
