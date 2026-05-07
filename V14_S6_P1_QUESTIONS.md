# V14 Stage 6 Phase 1 -- Open Questions

Phase 1 found that the V14 plan's "Stage 6 = 0 hours" framing is correct. Two questions
require user decisions before any execution brief is written.

---

## Q1. Confirm Stage 6 scope

The V14 plan says Stage 6 is 0 hours and the deletion belongs in Stage 7's cutover commit.
Phase 1 inventory + dependency map + risk analysis all agree.

Three options to choose from:

- **Q1=A (DEFER, recommended).** Skip Stage 6 entirely. Mark it as "0h ceremonial-deletion in
  Stage 7 cutover" in memory.md. Move on to Stage 7 planning. Total active hours: 0h.

- **Q1=B (TOOLING MIGRATION).** Same Stage-5-MINIMAL-style migration of `tray_launcher.cs` from
  csc.exe to dotnet build. Recommended target net48 (rejecting net8.0-windows because of the
  80 MB Microsoft.PowerShell.SDK redistributable). Total: 4-7h. The migrated file gets deleted
  in Stage 7 anyway, so this work is disposable. The only real win is removing csc.exe from
  the build pipeline for one more component.

- **Q1=C (ACTIVE COMPLEX).** Rejected by Phase 1 -- see DISSOLUTION_PLAN Scope C rationale.

**Phase 1 recommendation: Q1=A.**

---

## Q2. When does Stage 7 begin?

If Q1=A, Stage 6 has no execution work. The natural next step is Stage 7 Phase 1 (planning) for
the tray.ps1 -> C# port. Stage 7 is the largest remaining stage (V14 plan: 400-700h realistic).

- **Q2=NOW.** Begin Stage 7 Phase 1 immediately after this brief lands.
- **Q2=LATER.** User wants to ship v14.0.0 first (Stage 4 Phase 2 + Stage 5 MINIMAL cumulative)
  before opening Stage 7. v14.0.0 includes:
  - .NET 8 server (Stage 4)
  - Discord RPC port (Stage 4.10)
  - tray_native dotnet build path (Stage 5.1)
- **Q2=BREAK.** Take a break from V14 work; pick up Stage 7 in a future session. Tray_launcher
  stays as-is.

**Phase 1 recommendation: Q2=LATER.** Ship v14.0.0 cumulative first so the project has a
discrete milestone and rollback point before opening the largest stage.

---

## Q3 (only if Q1=B). Net48 vs net8.0-windows for tooling migration?

Only relevant if user picks Q1=B.

- **Q3=net48.** Minimal change. Same SMA (System.Management.Automation) dependency from GAC.
  Same runtime semantics as today. Pin GAC SMA reference path in csproj. RECOMMENDED if Q1=B.

- **Q3=net8.0-windows.** Adds Microsoft.PowerShell.SDK NuGet (~80 MB). Brings tray_launcher.cs
  into the same net8.0 world as launcher.cs/customize.cs. Risks: PS7 SMA semantics differ from
  PS5.1 in subtle ways (parameter binding, error stream handling, formatter defaults). REJECT
  unless we ALSO want to migrate the in-process tray host from PS5.1 to PS7, which is a
  Stage 7-class decision.

**Phase 1 recommendation: Q3=net48 (only if Q1=B).**

---

## Q4 (only if Q1=A). Should this brief append a one-liner to memory.md?

The brief is READ-ONLY by rule, so this brief does not append. But after the brief lands, a
one-line note in memory.md ("Stage 6 = 0h ceremonial deletion in Stage 7 cutover per V14 plan
and S6_P1 finding") would prevent future-Ruflo from re-doing this Phase 1 research.

- **Q4=YES.** Add the one-liner in a separate small follow-up turn after Phase 1 review.
- **Q4=NO.** Leave memory.md untouched; future-Ruflo can re-read S6_P1_FINAL_REPORT.md.

**Phase 1 recommendation: Q4=YES (in a separate turn after Phase 1 review).**
