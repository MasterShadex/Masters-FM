# V14 Stage 5 Sub-stage 5.1 Log

## Session context

- Date: 2026-05-07
- Brief: Sub-stage 5.1 -- project skeleton for tray_native (MINIMAL scope)
- Scope: netstandard2.0 csproj, move src/tray_native.cs, dual-path _full_rebuild.ps1, PS5.1 load test
- Hard cap: 6h
- Strike count at start: 0

## STEP 0 -- Prep

### 0a. Backup

Backup created at: C:\_BACKUPS_v12\Master FM_v14_stage_5_1_pre_2026-05-07_00-02\
Checkpoint label: CHECKPOINT_v12_3_0_S4_11

### 0b. Context read

- CLAUDE_CODE_INSTRUCTIONS.md read (full)
- V14_S5_P1_TRAY_NATIVE_INVENTORY.md read
- V14_S5_P1_PORT_PLAN.md read (Section 2, sub-stage 5.1)
- V14_S5_P1_RISKS.md read (R5, R7, R9)
- V14_S5_P1_CSWINRT_RESEARCH.md read (Section 5)
- src/tray_native.cs read (first+last 50 lines) -- 43,747 bytes, pure C#, no MSVC
- _full_rebuild.ps1 read -- tray_native csc.exe block located inside if ($csc) at approx lines 177-194
- Directory.Build.props read -- BaseIntermediateOutputPath=..\obj\$(MSBuildProjectName)\ confirmed

### 0b. Critical reframe (from Stage 5 Phase 1 inventory)

tray_native.cs is PURE C# -- not C++/CLI as CLAUDE_CODE_INSTRUCTIONS.md originally assumed.
Build uses csc.exe (Framework 4.x compiler), not MSVC. Zero MSVC dependency.
WinRT access is pure reflection at runtime -- no compile-time Windows.winmd required.
netstandard2.0 fully compatible with PS5.1 (.NET Framework 4.x, CLR 4.0.30319.42000).

### 0c. Baseline verify

git status: existing M entries (pre-existing changes from previous sub-stages).
Server.exe: healthy (PID 21628, ~62 MB at brief start -- soak survivor).

### 0d. Server health

PID 21628 alive, ~62-67 MB -- within expected post-soak range.

### 0e. Log and INTENTIONAL_DIFFERENCES files

This file (V14_S5_S5_1_LOG.md) created.
V14_S5_S5_1_INTENTIONAL_DIFFERENCES.md created.

## STEP 1 -- Create project skeleton

### 1.1 Create directory

src/tray_native/ created via New-Item.

### 1.2 Create tray_native.csproj

File written: src/tray_native/tray_native.csproj
Target: netstandard2.0
Key properties:
  AssemblyName=tray_native
  Nullable=disable
  ImplicitUsings=disable
  LangVersion=latest
  GenerateAssemblyInfo=false
  AppendTargetFrameworkToOutputPath=false
  CopyLocalLockFileAssemblies=false

### STRIKE 1 -- MSB4025 XML comment contains '--'

First build attempt failed:
  error MSB4025: The project file could not be loaded. An XML comment cannot contain '--'.

Cause: csproj XML comment on line 34 contained "-- prevents cross-contamination", which is
illegal in XML (comments cannot contain '--').

Fix: Changed "--" to "(prevents cross-contamination with other projects in src\)".
Rebuilt immediately after fix. Strike count: 1 of 3.

### 1.3 Move tray_native.cs

Move-Item src/tray_native.cs -> src/tray_native/tray_native.cs
Verified: src/tray_native.cs does NOT exist after move.
Verified: src/tray_native/tray_native.cs exists, 43,747 bytes (byte-exact match with original).
First line: // tray_native.cs -- Pre-compiled native/P-Invoke types for tray.ps1
Content unchanged -- move only, zero edits.

### 1.4 Build via dotnet

Command: dotnet build tray_native.csproj -c Release --nologo -o "$root"
Result: exit 0, 0 errors, 0 warnings
Output: tray_native.dll, tray_native.pdb, tray_native.deps.json to project root

### 1.5 DLL output verification (pre-sign)

tray_native.dll: 24,064 bytes, timestamp ~08:59:44 (newer than any csc.exe build)
Note: Size differs from csc.exe output (33,304 bytes pre-sign). See INTENTIONAL_DIFFERENCES ID-36.

## STEP 2 -- Update _full_rebuild.ps1

### 2.1 Add $UseDotnetTrayNative flag

Added at line 25 (after $UseDotnet8Server):
  $UseDotnetTrayNative = $true   # Stage 5.1 migration flag

### 2.2 Wrap csc.exe block

Existing csc.exe tray_native section wrapped with: if (-not $UseDotnetTrayNative) { ... }
csc.exe source path updated: src\tray_native.cs -> src\tray_native\tray_native.cs
csc.exe block preserved intact -- rollback path functional.

### Secondary fix -- L line outside if block

Initial edit placed L "[1d3/5] Building tray_native.dll..." BEFORE the if ($UseDotnetTrayNative) guard.
This would have logged the message even when $UseDotnetTrayNative=$false.
Fix: moved L line inside the if block. Not counted as a strike (not a build failure).

### 2.3 Add dotnet build path

New block added OUTSIDE if ($csc) (so it runs regardless of csc.exe availability):
  if ($UseDotnetTrayNative) { ... dotnet build ... -o "$root" ... signing ... }
Signing: same signing script as csc.exe path (build_tools\signing\_sign_msi.ps1 called with tray_native.dll).

### 2.4 Signing verified

Signing script finds tray_native.dll at project root (same path as before).
Post-sign size: 31,256 bytes. Status: Valid. Signer: CN=MasterShadex.

### 2.5 Full rebuild test (dotnet path)

_full_rebuild.ps1 with $UseDotnetTrayNative=$true:
  tray_native.dll OK (dotnet build, netstandard2.0)
  Signing: Status=Valid, Subject=CN=MasterShadex
  All other components built normally.
  Result: DONE OK

### 2.6 csc.exe rollback path test

$UseDotnetTrayNative changed to $false, _full_rebuild.ps1 run:
  tray_native.dll OK (csc.exe rollback)
  Signing: Status=Valid, Subject=CN=MasterShadex
  Result: DONE OK

$UseDotnetTrayNative restored to $true, second full rebuild run:
  Result: DONE OK

## STEP 3 -- PowerShell 5.1 load test

### 3.1 Load test script

test-ps51-load.ps1 created at project root.
Loads tray_native.dll via Add-Type -Path.
Checks all 9 types via $t -as [type].

### 3.2 PS5.1 load test execution

Command: C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File test-ps51-load.ps1

PSVersion:  5.1.26100.7920
PSEdition:  Desktop
CLRVersion: 4.0.30319.42000
DLL path:   G:\Project Folder\Master FM\tray_native.dll
DLL size:   31256 bytes
DLL time:   2026-05-07 08:59:44

Loading tray_native.dll via Add-Type -Path...
Add-Type: OK

Type resolution results:
  OK:   MFM_Shell -> MFM_Shell
  OK:   MFM_MenuNative -> MFM_MenuNative
  OK:   NativeMethods.GuiRes -> NativeMethods+GuiRes
  OK:   MasterFM.Win32Windows -> MasterFM.Win32Windows
  OK:   MasterFM.AudioPeak -> MasterFM.AudioPeak
  OK:   MasterFM.SMTC.SMTCWatcher -> MasterFM.SMTC.SMTCWatcher
  OK:   MasterFM.SMTC.SMTCSessionSnapshot -> MasterFM.SMTC.SMTCSessionSnapshot
  OK:   MasterFM.SMTC.SMTCChangeRecord -> MasterFM.SMTC.SMTCChangeRecord
  OK:   MasterFM.SMTC.SMTCEventKind -> MasterFM.SMTC.SMTCEventKind

ALL 9 TYPES RESOLVED.
PS5.1 LOAD TEST: PASS

## STEP 4 -- Verification gate

### Gate 1: tray_native.dll at project root, signed Valid
  Path: G:\Project Folder\Master FM\tray_native.dll
  Size: 31,256 bytes (post-sign)
  Status: Valid
  Signer: CN=MasterShadex
  PASS

### Gate 2: DLL built via dotnet (timestamp newer than csc.exe build)
  Timestamp: 2026-05-07 08:59:44
  PASS

### Gate 3: PS5.1 load test PASS (all 9 types resolve)
  PSVersion: 5.1.26100.7920
  CLRVersion: 4.0.30319.42000
  All 9 types: OK
  PASS

### Gate 4: Full rebuild completes 0 errors
  _full_rebuild.ps1 with $UseDotnetTrayNative=$true: DONE OK
  PASS

### Gate 5: csc.exe rollback path tested and working
  $UseDotnetTrayNative=$false: signed Valid DLL produced
  PASS

### Gate 6: tray.ps1 unchanged
  Byte-exact match True (no modifications)
  PASS

### Gate 7: tray_native.cs CONTENT unchanged
  Size: 43,747 bytes (matches original)
  Byte-exact match: True
  First line: // tray_native.cs -- Pre-compiled native/P-Invoke types for tray.ps1
  src/tray_native.cs exists: False (moved correctly)
  PASS

### Gate 8: src\tray_native.cs does not exist
  exists=False (must be False)
  PASS

### Gate 9: Server.exe still healthy
  PID 21132 (new PID after full rebuild reinstalled server.exe)
  Memory: ~62.1 MB
  PASS

### Gate 10: Zero MSB3539 warnings
  MSB3539/warning lines in last rebuild log: 0
  PASS

### Version: 12.3.0 (must be 12.3.0)
  PASS

## Strike summary

- Strike 1: MSB4025 XML comment contains '--' in tray_native.csproj (fixed immediately)
- Strike 2: unused (not reached)
- Strike 3: unused (not reached)

Final strike count: 1 of 3. No abort required.

## Soak result (from Stage 5 Phase 1 brief, earlier session)

6h soak: PASS
  Memory growth: +3.94 MB (gate <50 MB)
  Threads: 0 delta
  Handles: 0 delta
  Server PID 21628 continuous from soak start through sub-stage 5.1
