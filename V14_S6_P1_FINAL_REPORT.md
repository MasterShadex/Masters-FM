# V14 Stage 6 Phase 1 -- Final Report

## Summary

Stage 6 scope determined as **A (DEFER)**. The V14 plan's "0 hours, deletion in Stage 7"
framing is correct, fully consistent with Phase 1 inventory + dependency map + risk analysis.
No execution sub-stages 6.1+ are recommended. Stage 6 should be marked as a 0-hour ceremonial
deletion folded into Stage 7's cutover commit. The natural next step is Stage 7 Phase 1 planning
for the tray.ps1 -> C# port.

## What tray_launcher is (plain English)

`tray_launcher.cs` is a 207-line C# WinExe source file that compiles to `MastersFM_Tray.exe`.
Its single job is to host PowerShell IN-PROCESS (via `System.Management.Automation` from the
Windows PowerShell 5.1 GAC) so `tray.ps1` runs INSIDE this exe. That gives Task Manager a single
"Master's FM" application row instead of a separate "Windows PowerShell" entry, and ensures
the v2.0.0 closure-scope fix (`AddScript(invocation, useLocalScope:false)`) keeps WinForms
menu-click handlers' closures alive for the full app lifetime. If `MastersFM_Tray.exe` is
missing at runtime, `launcher.cs` already has a tested fallback that spawns `powershell.exe`
directly with the same arguments.

## Recommended Stage 6 scope: A (DEFER)

Phase 1 confirms the V14 plan: tray_launcher.cs exists ONLY because tray.ps1 exists. Once
Stage 7 replaces tray.ps1 with a C# .NET 8 application, tray_launcher.cs has no remaining job.
Removing it before Stage 7 lands either:

- Causes a UX regression (Task Manager UX falls back to the powershell.exe row), OR
- Requires building a new host that does the same job, which Stage 7 will then obsolete

Both are net-negative. The literal V14 plan -- "delete during Stage 7 cutover, 0 hours
independent effort" -- is the correct path.

## Sub-stage breakdown

**For Scope A (recommended):** None. Stage 6 has no independent sub-stages.

**For Scope B (optional Stage-5-MINIMAL-style migration, NOT recommended):**
- 6.1: skeleton + dual-path `_full_rebuild.ps1` (3-5h)
- 6.2: build polish + 30-min listening-session validation (1-2h)
- Total: 4-7h. Net negative ROI because the migrated file gets deleted in Stage 7 anyway.
- See V14_S6_P1_DISSOLUTION_PLAN.md Scope B section for full spec.

## Open questions for user

Four questions in `V14_S6_P1_QUESTIONS.md`:

1. Q1: Confirm Scope A (defer) vs Scope B (tooling migration) -- recommendation: A
2. Q2: When does Stage 7 begin (NOW vs LATER vs BREAK) -- recommendation: LATER (ship v14.0.0 cumulative first)
3. Q3 (only if Q1=B): net48 vs net8.0-windows for migration target -- recommendation: net48
4. Q4 (only if Q1=A): add one-liner to memory.md noting Stage 6 = 0h -- recommendation: YES (in a separate follow-up turn)

## Risk summary (top 3)

1. **A-R1 (Low/Low):** V14 progress tracker may show Stage 6 as "0h pending" indefinitely.
   Mitigation: explicit memory.md note that Stage 6 is folded into Stage 7 cutover.
2. **B-R1 (Medium/High):** Closure-scope regression if Scope B is chosen and the move
   accidentally drops `useLocalScope:false`. The v1.9.9 -> v2.0.0 fix was non-trivial.
   Mitigation: comprehensive menu-click smoke test before signing off any Scope B sub-stage.
3. **B-R2 (Medium/Medium):** GAC SMA vs PS7 NuGet SMA mismatch if Scope B targets
   net8.0-windows. Mitigation: reject net8.0-windows; only consider net48 if Scope B is chosen.

Full matrix in `V14_S6_P1_RISKS.md`.

## Cross-Stage-7 interaction

Stage 7 makes Stage 6 obsolete. The V14 plan paragraph after the Stage 7 row reads:

> "the new C# tray exists alongside tray.ps1 in the same checkout but is built into a separate
> exe (`MastersFM_Tray_v14.exe` perhaps) for tester opt-in. After 7k validation, the cutover is
> a single commit deleting tray.ps1 and renaming."

Implications:
- Stage 7 sub-stages 7a-7j build a parallel C# tray (`MastersFM_Tray_v14.exe` or similar) while
  the existing `MastersFM_Tray.exe` (built from tray_launcher.cs) keeps working.
- 7k cutover is a single commit that:
  - Deletes `src\tray.ps1`
  - Deletes `src\tray_launcher.cs`
  - Removes the `[1d/5]` csc.exe block from `_full_rebuild.ps1`
  - Removes `GUID_COMP14` and the `MastersFM_Tray.exe` FILES entry from `build_msi.py`
  - Updates `src\launcher.cs` to spawn the new C# tray exe directly
  - Updates test scripts' process-name lists
  - Renames `MastersFM_Tray_v14.exe` -> `MastersFM_Tray.exe`
- The deletion of tray_launcher.cs in 7k is a 5-minute mechanical step inside a much bigger
  commit. Not worth a separate stage.

**Conclusion:** Stage 7 makes Stage 6 obsolete. Skip Stage 6, go directly to Stage 7.

## Phase 1 verification gate (8 gates)

1. All 4 main deliverables created: PASS (INVENTORY, DEPENDENCY_MAP, DISSOLUTION_PLAN, RISKS)
   plus LOG and QUESTIONS as bonus
2. NO source files modified: PASS (sha256 stable for tray.ps1, tray_native.cs, tray_launcher.cs, _full_rebuild.ps1)
3. NO builds run: PASS
4. NO tests run: PASS
5. NO memory.md changes: PASS
6. Server.exe state: NOT RUNNING at STEP 0 and STEP 5 (documented in LOG -- not regressed by
   this brief; was already absent at start)
7. Tray state: NOT RUNNING at STEP 0 and STEP 5 (same)
8. BOM/em-dash discipline maintained on all .md files: PASS (BOM=False, EmDash=False on all 6
   deliverables)

## Sworn statement

I confirm that:

- NO source files were modified in this brief
- NO builds were run
- NO tests were run
- NO memory.md changes were made
- Em-dash hard constraint respected (verified BOM=False, EmDash=False on every Phase 1 .md file)
- UTF-8 BOM constraint respected (no file has a BOM)
- server.exe was not running at STEP 0 and not running at STEP 5; this was documented at STEP 0
  and the user was alerted before continuing. The brief is read-only so no install-side
  effects could be introduced.
- All references searched are real file/line citations from the current repo state at the
  time of this brief
- Stage 6 scope determination (A) matches the V14 plan and is supported by inventory +
  dependency map + risk analysis

STAGE 6 PHASE 1 COMPLETE - Scope A recommended. Sub-stage breakdown: none. 4 open questions for user.
