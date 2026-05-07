# V14 Stage 6 Phase 1 -- tray_launcher Inventory

## What tray_launcher actually is

`tray_launcher.cs` is a 207-line, ~8.4 KB C# Windows-Forms-host source file that compiles to
`MastersFM_Tray.exe`. It is NOT a wrapper, a shim, dead code, or a launcher in the
"start-something-else" sense. It is an active runtime component that hosts PowerShell IN-PROCESS
via `System.Management.Automation` so `tray.ps1` runs INSIDE this exe rather than spawning a
separate `powershell.exe` child. Its three jobs:

1. Set `AppUserModelID` to "MastersFM.App" so Windows shell groups all Master's FM processes
   (server.exe, MastersFM_Tray.exe, audio_spectrum.exe) under one Task Manager / taskbar row.
2. Open a `Runspace` with `InitialSessionState.CreateDefault()` (full cmdlet set), STA apartment,
   `ExecutionPolicy.Bypass`, and dot-source `tray.ps1` in the runspace's GLOBAL scope via
   `ps.AddScript(invocation, useLocalScope:false)`. This is the v2.0.0 fix for the v1.9.9 menu-
   click regression where pipeline-child scope killed scriptblock closures after pipeline
   completion.
3. Block the main thread on `Application.Run` (implicit -- the runspace's WinForms message loop
   keeps the process alive) and capture all errors to `%LOCALAPPDATA%\MastersFM\host.log`.

If `MastersFM_Tray.exe` is missing at runtime, `MastersFM.exe` (built from `launcher.cs`) has an
explicit fallback: it spawns `powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass
-NonInteractive -File tray.ps1` directly. Behavior is identical except Task Manager shows a
"Windows PowerShell" row instead of a unified "Master's FM" group.

## Source file location and language

- Path: `G:\Project Folder\Master FM\src\tray_launcher.cs`
- Size: 8,381 bytes (207 lines)
- Last modified: 2026-04-22 02:36
- Language: pure C# (.NET Framework 4.x target)
- Compiler: `csc.exe` from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- Required reference: `System.Management.Automation.dll` from GAC at
  `C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll`
- Other refs: `System.dll`, `System.Core.dll`
- Output target: `winexe` (no console window) with `assets\MastersFM.ico` as `win32icon`

The reference to `System.Management.Automation` from the Windows PowerShell 5.1 GAC means the
process is .NET Framework 4.x at runtime (the GAC SMA assembly cannot load into .NET 8 without
the `Microsoft.PowerShell.SDK` NuGet package and ~80 MB of redistributables).

## Binary characteristics (current build)

- Path: `G:\Project Folder\Master FM\MastersFM_Tray.exe`
- Size: 20,992 bytes
- Last modified: 2026-05-07 09:21:28 (last `_full_rebuild.ps1` run from sub-stage 5.4+5.5)
- Authenticode: NOT independently signed at the EXE level (build pipeline does not sign
  MastersFM_Tray.exe -- only customize.exe, audio_spectrum.exe, server.exe, tray_native.dll, and
  the MSI itself get signed in `_full_rebuild.ps1`)
- VersionInfo:
  - InternalName     : MastersFM_Tray.exe
  - OriginalFilename : MastersFM_Tray.exe
  - FileVersion      : 5.0.0.0 (set via `[assembly: AssemblyFileVersion("5.0.0.0")]` in source)
  - ProductVersion   : 5.0.0.0
  - ProductName      : Master's FM (from `[assembly: AssemblyProduct("Master's FM")]`)
  - CompanyName      : MasterShadex
  - FileDescription  : Master's FM (from `[assembly: AssemblyTitle("Master's FM")]`)

## Public API surface

Single entry point: `MastersFMTray.Main(string[] args)` returning `int`. Marked `[STAThread]` so
WinForms can pump messages.

Args are forwarded verbatim to the dot-sourced `tray.ps1` script via the `invocation` string
builder. `launcher.cs` invokes it with `-scriptDir "<dir>" -skipServerLaunch`. Switches starting
with `-` and not containing spaces pass through unquoted; everything else gets single-quoted with
`'`-doubling for PowerShell escaping.

No exported P/Invoke surface beyond the single shell32 import:
```
[DllImport("shell32.dll", PreserveSig = false)]
static extern void SetCurrentProcessExplicitAppUserModelID(string AppID);
```

## Runtime behavior (when invoked)

1. Call `SetCurrentProcessExplicitAppUserModelID("MastersFM.App")`. Capture any exception (cannot
   be retried -- once-per-process Win32 contract).
2. Init `host.log` in `%LOCALAPPDATA%\MastersFM\` (truncates per launch).
3. Compute `tray = AppDomain.CurrentDomain.BaseDirectory + "tray.ps1"` and verify it exists. If
   missing, log "FATAL: tray.ps1 not found" and return exit 2.
4. Build the dot-source invocation string (`. 'tray.ps1' arg1 arg2 ...`).
5. `RunspaceFactory.CreateRunspace(iss)` with `InitialSessionState.CreateDefault()`,
   `ApartmentState.STA`, `ThreadOptions.UseCurrentThread`, `ExecutionPolicy.Bypass`.
6. `Runspace.DefaultRunspace = rs` so any later scriptblock invocation (e.g. WinForms
   `Add_Click` callbacks) can find the runspace implicitly.
7. `ps.AddScript(invocation, false)` -- the `false` for `useLocalScope` is THE v2.0.0 fix.
8. `ps.Invoke()` -- this blocks for the entire app lifetime because `tray.ps1` enters
   `Application.Run` on its own thread (WinForms message loop).
9. On exit, drain `ps.Streams.Error` to host.log, return 0 on success.

Exit codes: 0 = clean, 1 = host exception, 2 = tray.ps1 missing.

## Historical context (why does it exist?)

From the source comments:

> v1.9.9 used `ps.AddCommand(tray.ps1)`. That runs the script in a pipeline-child scope.
> Functions defined inside tray.ps1 land in that child scope, and scriptblocks registered as
> WinForms click handlers -- which PowerShell binds to the script's SessionState -- could not
> resolve functions like Show-OverlayCustomizer once the pipeline was "done" with the top-level
> code. Menu clicks did nothing.
>
> v2.0.0 runs tray.ps1 via AddScript(scriptText, useLocalScope: false). That dot-sources the
> .ps1, so every function / variable lands in the runspace's GLOBAL scope. Closures stay valid
> forever, regardless of pipeline state, because the script's own global SessionState lives as
> long as the runspace itself. The runspace is kept alive for the entire app lifetime
> (Application.Run blocks this thread).

So the file exists specifically to solve a closure-scope problem for the WinForms click handlers
in `tray.ps1`. There is also a build_tools artifact `_build_tray.ps1` (Apr 21, 924 bytes) that
references an obsolete ps2exe-based pipeline pointing at `F:\Claude AI\Master FM\tray.ps1` and
claiming version 1.9.9.0 -- that file is the LEGACY pre-v2.0.0 build path, no longer invoked
from `_full_rebuild.ps1`. Dead build script, alive functionality moved to csc.exe + tray_launcher.cs.

## Original V14 plan language for Stage 6

From `V14_NET8_MIGRATION_PLAN.md` lines 132-138:

```
### Stage 6 -- `tray_launcher.cs` dissolves (no migration; deletion in Stage 7)

- Migrates: Nothing. R1 explicitly notes `tray_launcher.cs` "dissolves -- its only job is to host
  PowerShell, and if tray.ps1 is replaced, this shim is simply removed."
- Why now (placeholder): Documenting the deletion. The `Microsoft.PowerShell.SDK` NuGet (R4)
  would only be needed if we kept tray.ps1 at this stage; we don't.
- Effort: 0 hours. Deletion is part of Stage 7's cutover.
- Validation: None -- deletion is verified by Stage 7 working without it.
- Rollback: N/A.
```

From line 235 of the same plan:

```
| Stage 6 (tray_launcher dissolves) | internal -- no ship |
```

From line 281 (effort table):

```
| Stage 6 (tray_launcher deletion) | 0h | 0h | 0h |
```

The V14 plan is unambiguous: Stage 6 has no independent existence. It is a single line of
ceremony at the end of Stage 7 ("delete src/tray_launcher.cs and the build step that compiles
it"). No migration, no validation, no rollback path. Total budgeted effort: 0 hours.
