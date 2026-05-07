# V14 Stage 6 Phase 1 -- Risk Analysis

## Risks per scope

### Scope A (DEFER) risks

**A-R1 (Low / Low):** V14 progress tracker shows Stage 6 as "0h pending" indefinitely until
Stage 7 lands, which may give a false sense of "nothing is happening." Mitigation: explicit
note in memory.md that Stage 6 is a 0-hour ceremonial-deletion-during-Stage-7 placeholder.

**A-R2 (Low / Low):** csc.exe build dependency stays alive for `MastersFM_Tray.exe` until
Stage 7 cutover. Builds on machines without csc.exe will skip MastersFM_Tray.exe and fall back
to the powershell.exe path (already tested live). Mitigation: none needed -- this is the
documented current state.

**A-R3 (Negligible / Low):** Stage 7 may take longer than expected (V14 plan: 400-700h), so the
"deferred deletion" sits in the codebase for many months. Mitigation: leave a TODO comment
referencing this Phase 1 doc near the tray_launcher.cs build step.

### Scope B (TOOLING MIGRATION) risks

**B-R1 (Medium / High):** The v2.0.0 closure-scope fix
(`ps.AddScript(invocation, useLocalScope:false)`) is the entire reason this file exists. If the
move to the new project shape introduces any subtle change in how the runspace's
`InitialSessionState` is configured, the WinForms menu-click handlers can lose closure resolution
silently. Symptom: clicks do nothing. The v1.9.9 -> v2.0.0 fix took the original author hours
to debug. Mitigation: comprehensive smoke test that ACTUALLY clicks every tray menu item; do not
ship Scope B without verifying menus work.

**B-R2 (Medium / Medium):** GAC `System.Management.Automation.dll` (Windows PowerShell 5.1) has a
specific assembly identity (`v4.0_3.0.0.0__31bf3856ad364e35`). Some dotnet build configurations
resolve a different SMA via NuGet (`Microsoft.PowerShell.SDK`) which is .NET-Core-targeted PS7.
Mixing the two in the same process is a known source of crashes (PS7 SMA cannot host PS5.1
runspaces transparently). Mitigation: hard-pin the reference to the GAC path in the csproj if
Scope B B1 (net48) is chosen; reject B2 (net8.0-windows) outright unless we want to migrate the
in-process script host to PS7 too, which is a Stage 7-class decision.

**B-R3 (Medium / Medium):** STA apartment, ExecutionPolicy.Bypass, and
`Runspace.DefaultRunspace = rs` are all critical for WinForms+PowerShell interop. dotnet build
defaults may produce a binary that initializes these differently. Mitigation: identical csproj
properties to current csc.exe args, plus runtime smoke test confirming a tray menu click works.

**B-R4 (Low / Medium):** Authenticode signing pipeline. Currently MastersFM_Tray.exe is NOT
signed independently in `_full_rebuild.ps1` (only customize/audio_spectrum/server/tray_native.dll
get signed). Scope B might tempt us to "fix this while we're here" by adding signing -- which
introduces a new failure mode (signing fails -> build fails). Mitigation: do not add signing in
Scope B unless explicitly asked.

**B-R5 (Low / Low):** Stage 7 may need MastersFM_Tray.exe to remain available during a parallel
opt-in test phase (V14 plan paragraph: "the new C# tray exists alongside tray.ps1 in the same
checkout but is built into a separate exe (`MastersFM_Tray_v14.exe` perhaps) for tester opt-in").
Scope B's renamed/moved csproj may complicate this parallel-build scenario. Mitigation: design
Scope B's csproj to be parallel-friendly from the start; keep output filename `MastersFM_Tray.exe`.

**B-R6 (Negligible / Low):** Build-time dependency on `Microsoft.NET.Sdk` (the dotnet SDK) is
already required for the project (Stages 1, 2, 3, 4, 5 all use it). No NEW dependency
introduced.

### Scope C (ACTIVE COMPLEX) risks

Rejected at the plan level (see V14_S6_P1_DISSOLUTION_PLAN.md). All Scope C risks are
academic.

## Risk matrix

| Risk | Likelihood | Severity | Score | Status |
|---|---|---|---|---|
| A-R1 (tracker confusion) | Low | Low | 1 | accept |
| A-R2 (csc.exe persistence) | Low | Low | 1 | accept |
| A-R3 (long Stage-7 timeline) | Negligible | Low | 0 | accept |
| B-R1 (closure-scope regression) | Medium | High | 6 | DESIGN-OUT |
| B-R2 (SMA mismatch) | Medium | Medium | 4 | DESIGN-OUT (reject B2) |
| B-R3 (runspace init drift) | Medium | Medium | 4 | DESIGN-OUT |
| B-R4 (signing pipeline accident) | Low | Medium | 2 | accept |
| B-R5 (Stage 7 parallel-build) | Low | Low | 1 | accept |
| B-R6 (SDK dep) | Negligible | Low | 0 | accept |

Score = Likelihood (1=Negligible, 2=Low, 3=Medium, 4=High) x Severity (1=Low, 2=Medium, 3=High).
Anything >=4 marked "DESIGN-OUT": must be addressed in Scope B's csproj design before any move.

## Cross-Stage-7 interaction (the key open question)

Stage 7 sub-stage outline from V14 plan (sub-stages 7a-7k):
- 7a-7j: parallel C# tray app development alongside live tray.ps1
- 7k: cutover commit deleting tray.ps1

The plan explicitly says (paragraph after Stage 7 row):
> "the new C# tray exists alongside tray.ps1 in the same checkout but is built into a separate
> exe (`MastersFM_Tray_v14.exe` perhaps) for tester opt-in. After 7k validation, the cutover is
> a single commit deleting tray.ps1 and renaming."

Implications:
- During 7a-7j, MastersFM_Tray.exe (the tray_launcher.cs-built exe) MUST keep working
- The Stage 7 C# app builds to a different exe name (`MastersFM_Tray_v14.exe`) so there is no
  conflict
- 7k cutover deletes tray.ps1 AND renames the v14 exe to MastersFM_Tray.exe AND removes
  tray_launcher.cs

So tray_launcher.cs cannot be deleted before 7k. It can be MIGRATED in tooling (Scope B) any
time before 7k, but the migration buys nothing post-7k since the file is deleted anyway.

**Conclusion:** Scope A is correct. Scope B is a theoretical option but has negative ROI
because the migrated file gets deleted within (potentially many but) finite weeks anyway.

## Open questions

See `V14_S6_P1_QUESTIONS.md` for items requiring user input. Two main ones:

1. Confirm Scope A (defer) vs Scope B (tooling migration). The plan says A; Phase 1 confirms A.
2. If A: when does Stage 7 begin?

## Mitigation summary (top 3 risks)

1. **B-R1 (Medium/High closure-scope regression):** if Scope B is chosen, mandatory smoke test
   that clicks every tray menu item before signing off the sub-stage. Use Stage 5.5's listening-
   session pattern but with menu-click validation added.
2. **B-R2 (Medium/Medium SMA mismatch):** reject Scope B2 (net8.0-windows). Only consider B1
   (net48) with the GAC SMA path hard-pinned in the csproj.
3. **A-R1 (Low/Low tracker confusion):** add a one-line note in memory.md after Phase 1 lands
   stating "Stage 6 = 0h ceremonial deletion in Stage 7 cutover, per V14 plan and S6_P1 finding."
