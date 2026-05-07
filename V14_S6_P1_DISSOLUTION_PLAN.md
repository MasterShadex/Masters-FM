# V14 Stage 6 Phase 1 -- Dissolution Plan

## Stage 6 actual scope

**Recommended scope: A (DEFER) with optional Scope B (TOOLING-ONLY MIGRATION) detour.**

The V14 plan is unambiguous: Stage 6 is 0 hours of effort and is a single ceremonial deletion at
the end of Stage 7. The original framing is correct: `tray_launcher.cs` exists ONLY because
`tray.ps1` exists; once Stage 7 replaces `tray.ps1` with C#, `tray_launcher.cs` has no remaining
job. Removing it before Stage 7 lands would either:

- Cause a UX regression (Task Manager shows "Windows PowerShell" instead of "Master's FM"),
  because the fallback path in `launcher.cs` lines 466-478 directly spawns `powershell.exe`, OR
- Require building a NEW host that does the same job tray_launcher.cs does today, which is
  pointless because Stage 7 will obsolete that new host immediately.

The Stage 5 Phase 1 reframe ("tray_native is pure C#, not C++/CLI") changed the SCOPE of Stage 5
in a productive direction. The Stage 6 Phase 1 reframe is the OPPOSITE direction: scope is
already at minimum (0h), and the plan is correct as-written. Phase 1 confirms Phase 0.

## Three plausible scopes (with recommendation)

### Scope A -- DEFER (recommended)

**Action:** Mark Stage 6 as a 0-hour placeholder. Skip to Stage 7. Delete `tray_launcher.cs` and
its build/install/test references as part of the Stage 7 cutover commit.

**Sub-stages:** none. Stage 6 is just a row in the V14 progress tracker.

**Total hours:** 0h independent + ~0.5h folded into Stage 7 cutover (the actual deletion ceremony)

**Pros:**
- Matches the V14 plan exactly
- Zero risk
- Zero UX regression
- Maintains the host.log diagnostic capability until Stage 7's replacement covers it
- No build pipeline disruption

**Cons:**
- Stage 6 has no observable progress signal -- it's "done" only when Stage 7 lands
- The csc.exe build dependency for MastersFM_Tray.exe stays alive longer

### Scope B -- TOOLING-ONLY MIGRATION (optional Stage-5-style detour)

**Action:** Migrate `tray_launcher.cs` from csc.exe + GAC SMA to dotnet build with one of:
  - **B1:** `net48` target (still .NET Framework 4.8, same SMA dependency, but built via dotnet
           tooling instead of raw csc.exe). Removes csc.exe from the build chain for this exe.
  - **B2:** `net8.0-windows` target with `Microsoft.PowerShell.SDK` NuGet (~80 MB redistributable).
           Brings tray_launcher.cs into the same .NET 8 world as launcher.cs / customize.cs.

Behavior is unchanged. Stage 7 still deletes the file later. This is the same risk profile as
sub-stage 5.1 (tray_native csc.exe -> dotnet) but without the netstandard2.0 cross-host
compatibility win, because tray_launcher.cs is already a self-contained exe (not a library
loaded into PS5.1).

**Sub-stages (if pursued):**

| Sub | Description | Hours |
|---|---|---|
| 6.1 | Create `src\tray_launcher\tray_launcher.csproj`, move source, dual-path `_full_rebuild.ps1` ($UseDotnetTrayLauncher flag), verify MSI still ships, smoke test launches | 3-5 |
| 6.2 | Validation: build polish, confirm Task Manager grouping unchanged, run a 30-min listening session like sub-stage 5.5 | 1-2 |

**Total hours:** 4-7h (mirrors Stage 5 MINIMAL pattern)

**Pros:**
- Removes the LAST csc.exe dependency for an executable in the build pipeline
- Brings build pipeline closer to "all dotnet" state (Stage 8 polish becomes simpler)
- Same tooling discipline as Stage 5 sub-stages (proven safe pattern)
- Discoverable progress signal (a sub-stage 6.1 + 6.2 ships, even if Stage 7 still deletes the file)

**Cons:**
- 4-7h of work that the V14 plan budgets at 0h
- Stage 7 will delete the file, making this work disposable
- B1 keeps the GAC SMA dependency (no net win for distribution)
- B2 adds 80 MB of Microsoft.PowerShell.SDK NuGet redistributable (net loss)
- Risk of breaking the v2.0.0 in-process closure-scope fix during the move

### Scope C -- ACTIVE COMPLEX (rejected)

**Action:** Replace MastersFM_Tray.exe with something else (e.g. fold its function directly into
launcher.cs / MastersFM.exe so there is no separate tray host process at all).

**Why rejected:** The two-process design is intentional. `launcher.cs` keeps a 0x0 hidden form
alive specifically to act as the "app root" for shell grouping; merging tray.ps1 into the same
process would create a single hybrid that is harder to restart cleanly (the v9.6.0 BelowNormal
priority class on the tray host depends on it being a separate process). Also: Stage 7 replaces
tray.ps1 entirely with C#, at which point launcher.cs CAN absorb the tray app's functionality if
desired. So the right time for any consolidation is Stage 7+, not Stage 6.

## Recommended execution order

1. **Skip Stage 6 as an independent stage.** Mark it 0h in the V14 tracker. Move on to Stage 7
   planning.
2. **In Stage 7's cutover commit (last sub-stage of Stage 7),** include the deletion ceremony:
   - Remove `src\tray_launcher.cs`
   - Remove the `[1d/5]` block from `_full_rebuild.ps1` (lines 165-182)
   - Remove `GUID_COMP14` and the FILES entry from `build_tools\build_msi.py`
   - Update `src\launcher.cs` spawn block to point at the Stage 7 C# tray exe instead of
     MastersFM_Tray.exe
   - Update test scripts' process-name lists
   - Delete dead `build_tools\ps2exe\_build_tray.ps1` (was already dead since v2.0.0)
3. **Validation:** Stage 7 cutover already requires a full validation cycle. The deletion is
   verified by Stage 7's tests passing without the file present. No separate Stage 6 validation.

## If user opts for Scope B anyway (sub-stage outline)

Only if the user prefers progress signal over plan fidelity:

### Sub-stage 6.1 -- skeleton + dual-path build

- Create `src\tray_launcher\tray_launcher.csproj`
- Choose target: `net48` (recommended; minimal change) or `net8.0-windows` (reject due to 80 MB
  Microsoft.PowerShell.SDK overhead)
- Move (do not edit) `src\tray_launcher.cs` -> `src\tray_launcher\tray_launcher.cs`
- Add `$UseDotnetTrayLauncher` flag to `_full_rebuild.ps1` (default `$true`)
- Wrap existing csc.exe block with `if (-not $UseDotnetTrayLauncher) { ... }`
- Add new `dotnet build` block outside `if ($csc)`
- Verify MastersFM_Tray.exe still:
  - Lands at project root with same name
  - Has working VersionInfo (ProductName=Master's FM, FileVersion=5.0.0.0)
  - Launches cleanly when invoked by launcher.cs
- 30-min listening session like sub-stage 5.5
- Hours: 3-5

### Sub-stage 6.2 -- build polish + final validation

- Header comment for the new `$UseDotnetTrayLauncher` flag
- Verify csc.exe rollback path works
- Memory + handle stability over 30+ min listening session
- Validation checklist (PASS/FAIL/DEFERRED)
- Hours: 1-2

### Total Scope B hours: 4-7h

## Cross-Stage-7 dependency

Stage 7's "tray.ps1 -> C# .NET 8" is the largest remaining stage (V14 plan budgets 400-700h
realistic, 900h worst-case -- 5-10x v12.0.0). Stage 7 sub-stages are itemized 7a-7k in the V14
plan. The tray_launcher.cs deletion belongs in 7k (cutover) per the plan.

The Phase 1 finding here is consistent: there is no productive work in Stage 6 that doesn't
belong elsewhere. Either defer (Scope A) or accept the cost of disposable tooling work
(Scope B) -- those are the only two real choices.
