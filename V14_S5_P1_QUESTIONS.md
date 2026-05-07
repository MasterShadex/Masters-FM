# V14 Stage 5 Phase 1 -- Open Questions

These questions require user input before Stage 5 sub-stages can be executed.
Answers determine the TFM choice, sub-stage scope, and hour estimates.

---

## Q1 -- PowerShell 7 migration (BLOCKING for full CsWinRT)

**Question**: Is tray.ps1 planned to migrate from Windows PowerShell 5.1 to
PowerShell 7 during Stage 7 (the tray.ps1 C# port), or earlier?

**Why it matters**: .NET 8 assemblies cannot be loaded by PowerShell 5.1
(.NET Framework 4.x). If tray.ps1 stays on PS5.1, tray_native.dll must target
netstandard2.0 or keep the csc.exe build. Full CsWinRT projections (compile-time
Windows.Media.Control bindings) require net8.0-windows10.0.x.

**Options**:

A. YES, Stage 7 ports tray.ps1 to PowerShell 7 or replaces it with a .NET 8
   process -- then Stage 5 can proceed with full CsWinRT (net8.0-windows10.0.19041.0).
   Stage 5 scope: all 5 sub-stages, ~24-31h, CsWinRT projections included.

B. NO, tray.ps1 stays PS5.1 indefinitely -- then Stage 5 must target
   netstandard2.0. WinRT reflection approach stays (no compile-time bindings).
   Stage 5 scope: 5.1 + 5.4 + optional 5.2 + 5.5, ~10-16h.

C. UNDECIDED -- proceed with netstandard2.0 now (safe), upgrade to net8.0-windows
   if/when Stage 7 is confirmed. Avoids blocking Stage 5 on Stage 7 planning.

**Recommendation**: Option C (netstandard2.0 now) is the lowest-risk path.
It moves tray_native off csc.exe immediately, adds dotnet SDK structure,
and keeps the path open for CsWinRT later without commitment.

---

## Q2 -- Thumbnail extraction migration

**Question**: Should the SMTC thumbnail extraction code move from tray.ps1 into
tray_native.dll as sub-stage 5.2?

**Why it matters**: The current PowerShell async state machine (~60 lines, three
states: idle/opening/loading) is the most complex PowerShell-side WinRT code.
Moving it into C# simplifies tray.ps1 and makes the logic easier to maintain.
However, it is a tray.ps1 behavioral change with rollback complexity.

**Options**:

A. YES -- move thumbnail extraction to tray_native (sub-stage 5.2). tray.ps1
   calls `[MasterFM.SMTC.SMTCThumbnail]::ExtractBytes(...)` synchronously.
   Reduces tray.ps1 complexity. Adds ~3-4h to Stage 5. Some behavioral risk (R2).

B. NO -- thumbnail stays in tray.ps1. Stage 5 scope narrows.
   tray.ps1 keeps the state machine. Faster Stage 5, lower risk.

**Recommendation**: YES if Q1 = A or C (planning for eventual .NET 8 path).
NO if Q1 = B (minimal migration, netstandard2.0 only).

---

## Q3 -- Stage 5 scope

**Question**: Given that tray_native is already pure C# (not C++/CLI as expected),
what is the actual desired outcome for Stage 5?

**Options**:

A. MINIMAL: Just move tray_native off csc.exe to dotnet build (netstandard2.0).
   Sub-stages: 5.1, 5.4, 5.5. ~6-12h. No behavioral changes to SMTC or tray.ps1.

B. STANDARD: Move to dotnet build + migrate thumbnail extraction to C# + clean
   up SMTCWatcher code quality. Sub-stages: 5.1, 5.2, 5.3, 5.4, 5.5. ~16h.

C. FULL: Standard + convert WinRT reflection to CsWinRT projections (requires
   net8.0-windows + PS7 path). Sub-stages: all 5, significantly expanded 5.3.
   ~24-31h. Requires Q1 = YES.

**Recommendation**: STANDARD (Option B) provides the most value at reasonable
cost without requiring PS7 commitment.

---

## Q4 -- Stage 4 soak result and v14.0.0 ship decision

**Question (pre-existing, not Stage 5 specific)**: The 6h soak is running
(ends 2026-05-07 08:07:12). After it completes:
- PASS (memory growth < 50MB, no crashes): Stage 4 ships, version bump to?
- FAIL: Investigate and fix before shipping Stage 4.

What version number should be assigned when Stage 4 (server.exe .NET 8 port) ships?
- v13.0.0 (conservative: major new runtime, new architecture)
- v14.0.0 (V14 plan's internal designation)

This does not block Stage 5 planning but determines what gets tagged before
Stage 5 begins execution.
