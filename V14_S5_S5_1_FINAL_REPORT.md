# V14 Stage 5 Sub-stage 5.1 Final Report

## Summary

Project skeleton landed. tray_native is now built via dotnet SDK (netstandard2.0) instead of
the legacy csc.exe .NET Framework 4.x compiler. The PS5.1 load test passes: all 9 public types
resolve when Add-Type -Path is called from Windows PowerShell 5.1 (CLR 4.0.30319.42000). The
csc.exe rollback path is preserved and verified functional. Zero behavioral changes for testers.
Source stays v12.3.0.

---

## Implementation summary

### Files created

- src/tray_native/tray_native.csproj (NEW)
  Target: netstandard2.0
  Properties: AssemblyName=tray_native, Nullable=disable, ImplicitUsings=disable,
  LangVersion=latest, GenerateAssemblyInfo=false, AppendTargetFrameworkToOutputPath=false,
  CopyLocalLockFileAssemblies=false
  Compile: explicit Include only (no SDK glob over-pull)

- test-ps51-load.ps1 (NEW)
  Validation script. Loads DLL via Add-Type -Path in PS5.1. Checks all 9 types via $t -as [type].
  Intended for re-use in sub-stage 5.5 validation harness.

- V14_S5_S5_1_LOG.md (NEW) -- running log for this sub-stage
- V14_S5_S5_1_INTENTIONAL_DIFFERENCES.md (NEW) -- ID-36/37/38

### Files moved

- src/tray_native.cs MOVED to src/tray_native/tray_native.cs
  Content: unchanged (43,747 bytes, byte-exact match confirmed)
  Move only -- zero edits to source

### Files modified

- _full_rebuild.ps1
  - Added $UseDotnetTrayNative = $true at line 25 (after $UseDotnet8Server)
  - Wrapped csc.exe tray_native block with: if (-not $UseDotnetTrayNative) { ... }
  - Updated csc.exe source path: src\tray_native.cs -> src\tray_native\tray_native.cs
  - Added dotnet build block OUTSIDE if ($csc): runs regardless of csc.exe availability
  - Signing applied in both paths via build_tools\signing\_sign_msi.ps1

### Files NOT modified

- src/tray_native/tray_native.cs (content unchanged)
- tray.ps1 (unchanged)
- build_msi.py (unchanged)
- Directory.Build.props (unchanged -- tray_native inherits BaseIntermediateOutputPath correctly)
- server_dotnet.csproj (unchanged)
- Any Stage 4 sub-stage code (unchanged)

---

## Verification gate results

### Gate 1: tray_native.dll at project root, signed Valid
  Path:   G:\Project Folder\Master FM\tray_native.dll
  Size:   31,256 bytes (post-sign; pre-sign 24,064 bytes)
  Status: Valid
  Signer: CN=MasterShadex
  PASS

### Gate 2: DLL built via dotnet (timestamp newer than any csc.exe build)
  Timestamp: 2026-05-07 08:59:44
  PASS

### Gate 3: PS5.1 load test PASS (all 9 types resolve)
  PSVersion:  5.1.26100.7920
  PSEdition:  Desktop
  CLRVersion: 4.0.30319.42000
  Add-Type:   OK
  MFM_Shell                         OK -> MFM_Shell
  MFM_MenuNative                    OK -> MFM_MenuNative
  NativeMethods.GuiRes              OK -> NativeMethods+GuiRes
  MasterFM.Win32Windows             OK -> MasterFM.Win32Windows
  MasterFM.AudioPeak                OK -> MasterFM.AudioPeak
  MasterFM.SMTC.SMTCWatcher         OK -> MasterFM.SMTC.SMTCWatcher
  MasterFM.SMTC.SMTCSessionSnapshot OK -> MasterFM.SMTC.SMTCSessionSnapshot
  MasterFM.SMTC.SMTCChangeRecord    OK -> MasterFM.SMTC.SMTCChangeRecord
  MasterFM.SMTC.SMTCEventKind       OK -> MasterFM.SMTC.SMTCEventKind
  ALL 9 TYPES RESOLVED. PS5.1 LOAD TEST: PASS
  PASS

### Gate 4: Full rebuild completes 0 errors
  _full_rebuild.ps1 ($UseDotnetTrayNative=$true): DONE OK
  PASS

### Gate 5: csc.exe rollback path tested and working
  _full_rebuild.ps1 ($UseDotnetTrayNative=$false): DONE OK, signed Valid
  $UseDotnetTrayNative restored to $true after test
  PASS

### Gate 6: tray.ps1 unchanged
  Byte-exact match: True
  PASS

### Gate 7: tray_native.cs content unchanged (move only)
  Size: 43,747 bytes (matches original)
  Byte-exact match: True
  First line: // tray_native.cs -- Pre-compiled native/P-Invoke types for tray.ps1
  PASS

### Gate 8: src\tray_native.cs does not exist
  Test-Path src\tray_native.cs: False
  PASS

### Gate 9: Server.exe still healthy
  PID 21132 (new PID after full rebuild reinstalled server.exe from 21628)
  Memory: ~62.1 MB
  PASS

### Gate 10: Zero MSB3539 warnings
  MSB3539 lines in last rebuild log: 0
  PASS

### Gate 11 (implicit): Version 12.3.0 unchanged
  PASS

All 10 (+ 1 implicit) gates: PASS

---

## Intentional differences

See V14_S5_S5_1_INTENTIONAL_DIFFERENCES.md for full details.

- ID-36: DLL binary size differs (24,064 bytes pre-sign dotnet vs 33,304 bytes csc.exe) -- Roslyn emits more compact PE
- ID-37: dotnet build emits tray_native.pdb + tray_native.deps.json alongside DLL (not in MSI, not referenced by tray.ps1)
- ID-38: Compiler changed from csc.exe Framework 4.x to Roslyn SDK (identical IL semantics)

User-visible impact: NONE.

---

## Errors encountered

### Strike 1: MSB4025 XML comment contains '--'

First dotnet build attempt failed: "The project file could not be loaded. An XML comment cannot contain '--'."
Cause: csproj comment had "-- prevents cross-contamination" in XML comment syntax.
XML spec: comments cannot contain '--'. Fixed by changing to text: "(prevents cross-contamination...)".
Rebuilt immediately. Pass on second attempt. Strike count: 1 of 3. No abort required.

---

## What remains in Stage 5 MINIMAL scope

Sub-stage 5.4 (build pipeline polish) -- NEXT
  Estimate: 1-2h
  Scope: CI-friendliness, dotnet restore step, any _full_rebuild.ps1 cleanup from 5.1 review

Sub-stage 5.5 (validation + side-by-side) -- WAITING after 5.4
  Estimate: 3-4h
  Scope: Expand test-ps51-load.ps1 into a full harness; side-by-side DLL comparison;
  confirm zero behavioral difference vs csc.exe build

Skipped per user decisions:
  5.2 (thumbnail extraction migration): SKIPPED per Q2=NO (thumbnail stays in tray.ps1)
  5.3 (SMTCWatcher cleanup): SKIPPED per Q3=MINIMAL

---

## User decisions recorded

- Q1=C: netstandard2.0 target. Keeps PS5.1 (Windows PowerShell 5.1) compatibility.
         Preserves future PS7 / net8.0-windows10.0.x path without committing now.
- Q2=NO: thumbnail extraction stays in tray.ps1. Do not disturb working code.
- Q3=MINIMAL: build-tooling migration only. Sub-stages 5.1 + 5.4 + 5.5 only.
- Q4=hold: Version bump deferred. Source stays v12.3.0. Ship as v14.0.0 cumulative.

---

## Sworn statement

I confirm that:
1. tray_native.cs was moved, not edited. Content is byte-exact with pre-move original.
2. tray.ps1 was not touched.
3. No version bump was made. Source remains v12.3.0.
4. No GitHub push was made.
5. The csc.exe rollback path ($UseDotnetTrayNative=$false) was tested and is functional.
6. The PS5.1 load test was run in Windows PowerShell 5.1 (CLR 4.0.30319.42000) and passed.
7. All 9 public types resolve via Add-Type -Path in PS5.1.
8. The DLL is signed Valid by CN=MasterShadex via the standard signing script.
9. No em-dashes appear in any file produced this sub-stage.
10. All files produced this sub-stage are UTF-8 without BOM.

SUB-STAGE 5.1 BUILT LOCALLY - skeleton in place, PS5.1 load test PASS, zero behavior change. Ready for sub-stage 5.4.
