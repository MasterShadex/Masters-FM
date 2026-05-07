# V14 Stage 5 Phase 1 Log

## Soak migration at brief start

- Prior soak job (Start-Job) died with previous PS host session -- expected behavior
- Server.exe health: PID 21628, 62.84 MB, started 2026-05-07 00:09:41 -- healthy
- Migration action: killed 3 duplicate soak processes from launch attempts (PIDs 33572, 10316, 23928)
- Duration changed from 24h to 6h per user request
- soak-monitor.ps1 updated: AddHours(24) -> AddHours(6), checkNum >= 24 -> >= 6
- New hidden background process: PID 22436, launched via Start-Process -WindowStyle Hidden -PassThru
- New soak log: C:\_SOAK_24H\soak-2026-05-07_02-07-12.log
- end_expected: 2026-05-07 08:07:12
- Brief start time: 2026-05-07 02:07:49

## STEP 1 -- tray_native source inventory findings

### Critical reframe: tray_native is NOT C++/CLI

The CLAUDE_CODE_INSTRUCTIONS.md brief assumed tray_native.dll was a C++/CLI mixed-mode
binary. This is INCORRECT. The actual source is:

  G:\Project Folder\Master FM\src\tray_native.cs  43,747 bytes  C# (pure managed)
  G:\Project Folder\Master FM\tray_native.dll      33,304 bytes  compiled output
  G:\Project Folder\Master FM\v13_source_backup\tray_native.cs  42,924 bytes  backup

tray_native.cs is pure C#. It has ZERO MSVC dependency. Build uses csc.exe from
.NET Framework 4.x, not MSVC or dotnet SDK.

### Types in tray_native.cs

1. MFM_Shell (static) -- P/Invoke shell32.dll
2. MFM_MenuNative (static) -- P/Invoke dwmapi/gdi32/user32
3. NativeMethods.GuiRes (static) -- P/Invoke user32 GetGuiResources
4. MasterFM.Win32Windows (static) -- EnumWindows + window title helpers
5. MasterFM.AudioPeak (static) -- Core Audio COM interop (IAudioMeterInformation)
6. MasterFM.SMTC.SMTCWatcher (class) -- event-driven SMTC watcher
7. MasterFM.SMTC.SMTCSessionSnapshot (data class)
8. MasterFM.SMTC.SMTCChangeRecord (data class)
9. MasterFM.SMTC.SMTCEventKind (enum)

### WinRT access strategy (key finding)

SMTCWatcher uses PURE REFLECTION to bind to WinRT types at runtime.
No compile-time Windows.winmd or CsWinRT reference is required.
tray.ps1 activates the WinRT types (ContentType=WindowsRuntime) and passes
the manager object to SMTCWatcher.Initialize(). SMTCWatcher reflects on whatever
type it receives -- fully runtime-bound.

### Build tooling

- Compiler: csc.exe at C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
- References: System.dll, System.Core.dll only
- Command: csc /nologo /target:library /out:tray_native.dll /reference:System.dll
             /reference:System.Core.dll src\tray_native.cs
- Output signed via build_tools\signing\_sign_msi.ps1 (same cert as MSI)

### PowerShell runtime

tray.ps1 runs in Windows PowerShell 5.1 (.NET Framework 4.x).
MastersFM_Tray.exe (compiled via csc.exe) launches tray.ps1.
tray.ps1 loads tray_native.dll via Add-Type -Path.
.NET Framework runtime loads the .NET Framework assembly -- compatible by design.

### Thumbnail extraction

Thumbnail extraction lives in tray.ps1 (NOT in tray_native.dll).
tray.ps1 drives a state machine (idle/opening/loading) using WinRT async via AsTask.
Bytes read from IRandomAccessStreamWithContentType via DataReader.
MIME detected: stream.ContentType, fallback magic bytes (0xFF 0xD8 0xFF = JPEG).
Final encoding: base64 data URI "data:image/png;base64,...".
No resize, no scaling -- raw bytes forwarded.

## STEP 5 -- Soak re-verification (end of brief)

- Time: 2026-05-07 02:16:41
- Soak PID 22436: alive (HasExited=False, CPU=0.22)
- Server PID 21628: 64.01 MB (delta from soak start: +1.17 MB in 9 min -- normal)
- Log tail: header only (check=01 fires at 03:07 AM -- sleeping in while loop)
- PASS: soak and server both healthy

## STEP 6 -- Verification gate

- 6 deliverables created: LOG, INVENTORY, CSWINRT_RESEARCH, PORT_PLAN, RISKS, QUESTIONS
- git status: no source files modified this brief (all M entries are pre-existing)
- No builds run
- Soak alive at both STEP 0 and STEP 5
- memory.md: not touched this brief
- BOM check: all 6 files -- BOM=False (UTF-8 no BOM)
- Em-dash check: 0 em-dashes across all 6 files
- GATE: ALL PASS
