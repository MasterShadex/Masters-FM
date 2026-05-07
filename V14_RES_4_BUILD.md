# V14 Researcher 4 — Build Pipeline & Deployment

## Current Build Pipeline

The entire build is orchestrated by `_full_rebuild.ps1`. No `.sln`, `.csproj`, or `.wixproj` files exist — all compilation is done via bare `csc.exe` invocations, and the MSI is assembled by a hand-rolled Python script using the Windows Installer API directly.

### Step 1 — `pkg` build → `server.exe`
`npx pkg src\server.js --targets node18-win-x64 --output dist\server.exe`
Bundles the Node.js server into a self-contained Windows x64 executable with an embedded Node 18 runtime. Output is copied to the project root.

### Step 1b — `MastersFM.exe` (C# launcher)
Compiled by `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (falls back to x86). Target: `winexe` (no console window). References: `System.Windows.Forms.dll`, `System.Drawing.dll`. Source: `src\launcher.cs`. Creates a hidden WinForms window, spawns child processes (server.exe, MastersFM_Tray.exe, audio_spectrum.exe), manages a Windows Job Object so children die when the launcher exits, and sets the AppUserModelID for Task Manager grouping.

### Step 1c — `customize.exe` (WebView2 settings window)
Same `csc.exe` invocation, target `winexe`. References: `System.dll`, `System.Drawing.dll`, `System.Windows.Forms.dll`, `System.Core.dll`, plus the two managed WebView2 DLLs (`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll`) from the project root. Source: `src\customize.cs`. Hosts the `customize.html` settings page in a native WebView2 window.

### Step 1d — `MastersFM_Tray.exe` (PowerShell in-process host)
Compiled by `csc.exe`, target `winexe`. References: `System.dll`, `System.Core.dll`, plus `System.Management.Automation.dll` from the Windows GAC at `C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\`. Source: `src\tray_launcher.cs`. Hosts PowerShell in-process via SMA so `tray.ps1` runs inside a branded exe instead of a raw `powershell.exe` child.

### Step 1d2 — `audio_spectrum.exe` (WASAPI loopback + FFT)
Delegated to `build_tools\ps2exe\_build_spectrum.ps1` because NAudio.Core 2.x targets `netstandard2.0` and requires the `netstandard.dll` facade shim from `.NET Framework 4.7.2+` (found under `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\`). The script invokes `csc.exe` with explicit `/reference` paths to `NAudio.Core.dll`, `NAudio.Wasapi.dll`, `NAudio.WinMM.dll`, `NAudio.Asio.dll`, and `netstandard.dll`. Source: `src\audio_spectrum.cs`.

### Step 1d3 — `tray_native.dll` (pre-compiled P/Invoke types)
Compiled by `csc.exe`, target `library`. Source: `src\tray_native.cs`. This DLL contains the P/Invoke and COM definitions needed by `tray.ps1`, replacing 5 inline `Add-Type` / `csc.exe` calls that each took 10–25 seconds at tray startup. The DLL is signed immediately after compilation.

### Step 1e — `server.exe` rebrand
Uses the vendored `build_tools\resedit\` (pure-JavaScript PE resource editor, `jet2jet/resedit-js-cli`) to overwrite the VersionInfo and icon inside the `pkg`-bundled `server.exe`. `rcedit` is explicitly NOT used here — it corrupts the `pkg` overlay. A size delta check (< 200 KB) guards against silent overlay corruption.

### Step 2 — MSI build
`python build_tools\build_msi.py`. No WiX. The script calls `msi.dll` directly via Python `ctypes` to:
1. Invoke `makecab.exe` (Windows built-in) with an LZX DDF to create `Data1.cab`
2. Call `MsiOpenDatabase` → create all MSI tables manually → populate with `INSERT` statements
3. Embed the CAB as a `_Streams` entry inside the MSI
Output: `Master's FM Install\MastersFM_Setup.msi`

### Step 2b — MSI signing
`build_tools\signing\_sign_msi.ps1 -MsiPath <msi>`. Locates a self-signed code-signing cert (`CN=MasterShadex, O=MasterShadex`) in `Cert:\CurrentUser\My` (creates one if absent), then uses `signtool.exe` (Windows SDK) with SHA-256 + DigiCert timestamp server. Falls back to PowerShell `Set-AuthenticodeSignature` if `signtool.exe` is not found. Same script is called earlier to sign `tray_native.dll`.

### Step 2c — `version.json` generation
PowerShell reads `$script:APP_VERSION` from `src\tray.ps1`, SHA-256 hashes the signed MSI, and writes `version.json` to the project root. This file drives the in-app auto-updater.

### Steps 3–5 — Local install cycle
Stops running processes, uninstalls the previous version via `msiexec /x`, installs the new MSI via `msiexec /i /qn`, and launches `MastersFM.exe`.

### Desktop bundle
Copies the versioned MSI, `INSTALL.bat`, and `MastersFM_publisher.cer` into `%USERPROFILE%\Desktop\MastersFM_Installer\` for distribution to friends.

---

## Current Output Artifacts

| File | Role | How produced |
|---|---|---|
| `server.exe` | Node.js 18 server (Last.fm API proxy, SSE, overlay HTTP) | `pkg` + `resedit` rebrand |
| `MastersFM.exe` | C# WinForms launcher / process host / app root | `csc.exe` from `src\launcher.cs` |
| `MastersFM_Tray.exe` | C# PowerShell in-process host (replaces raw powershell.exe) | `csc.exe` from `src\tray_launcher.cs` via SMA GAC DLL |
| `customize.exe` | C# WebView2 settings window | `csc.exe` from `src\customize.cs` |
| `audio_spectrum.exe` | WASAPI loopback capture + FFT + SSE server | `csc.exe` from `src\audio_spectrum.cs` via `_build_spectrum.ps1` |
| `tray_native.dll` | Pre-compiled P/Invoke + COM types for `tray.ps1` | `csc.exe` from `src\tray_native.cs` |
| `tray.ps1` | Main tray logic (SMTC, scrobbling, OBS, menu) | Shipped as source, runs in MastersFM_Tray.exe |
| `NAudio.Core/Wasapi/WinMM/Asio.dll` | Audio capture backends | Vendored NuGet (netstandard2.0) |
| `Microsoft.Web.WebView2.*.dll` + `WebView2Loader.dll` | WebView2 managed + native bridge | Vendored NuGet |
| `Microsoft.Win32.Registry.dll` + System.* polyfill DLLs (6 files) | netstandard2.0 facades for NAudio.Asio HKLM reads | Vendored NuGet |
| `config_default.json` | Factory overlay/settings defaults | Source file |
| `overlay.html`, `customize.html`, `update.html`, `discord_rpc.js` | Web UI assets | Source files |
| `MastersFM.ico` | App icon | Asset |
| `Master's FM Install\MastersFM_Setup.msi` | Final installable package | `build_msi.py` via `msi.dll` ctypes |
| `build_tools\signing\MastersFM_publisher.cer` | Public cert for friends' machines | `_sign_msi.ps1` auto-creates |

**MSI install structure:** All files land flat in `%LOCALAPPDATA%\MastersFM\`. There is no subdirectory structure inside the install folder.

---

## .NET 8 Build Changes Required

| Current | .NET 8 equivalent | Impact |
|---|---|---|
| `csc.exe` (Framework v4.0.30319) — bare command-line invocation | `dotnet build` or `dotnet publish` with `.csproj` files | Each C# source file needs a `.csproj`. Compiler changes from the .NET Framework compiler to Roslyn via the .NET 8 SDK toolchain. |
| Target framework: implicit `net461` (Framework 4.x) | `<TargetFramework>net8.0-windows</TargetFramework>` | `-windows` TFM required for `System.Windows.Forms` (WinForms), `System.Drawing`, and COM interop. WinForms and Drawing are opt-in on .NET 8 via `<UseWindowsForms>true</UseWindowsForms>`. |
| `System.Management.Automation.dll` from Windows GAC | NuGet package `Microsoft.PowerShell.SDK` (7.x) or `System.Management.Automation` (7.x) | The GAC path only exists on machines where Windows PowerShell 5.1 is installed. .NET 8 cannot load .NET Framework GAC assemblies. `MastersFM_Tray.exe` will need a `PackageReference` to `Microsoft.PowerShell.SDK`. This is the highest-complexity dependency change. |
| NAudio 2.x (`netstandard2.0`) + manual `netstandard.dll` facade shim | NAudio 2.x natively supported under .NET 8 (no facade shim needed) | The `netstandard.dll` reference in `_build_spectrum.ps1` can be removed. The `audio_spectrum.exe` build script is simplified. |
| `System.Windows.Forms` and `System.Drawing` from GAC | Included as NuGet-backed platform assemblies in `net8.0-windows` | No extra reference steps needed; the SDK resolves them automatically from the project file. |
| WebView2 managed DLLs vendored manually in project root | `PackageReference` to `Microsoft.Web.WebView2` NuGet | Simpler — MSI still needs to bundle `WebView2Loader.dll` (native runtime bridge) unless WebView2 Runtime is pre-installed on target machines. |
| `dotnet publish` deployment model: not used | `dotnet publish -r win-x64 --self-contained false` (framework-dependent) OR `--self-contained true` | See discussion below. |
| No `.csproj` files — all arguments passed inline to `csc.exe` | Four `.csproj` files needed: `launcher.csproj`, `tray_launcher.csproj`, `customize.csproj`, `audio_spectrum.csproj` + `tray_native.csproj` (library) | One-time creation effort. |

### Self-contained vs framework-dependent

**Framework-dependent (recommended for this app):** Requires `.NET 8 Desktop Runtime` on the user's machine. The runtime is ~55 MB to download but is shared across all .NET 8 apps and auto-updated by Windows Update. MSI size stays small (no embedded runtime). The user install story adds one dependency.

**Self-contained:** Bundles the .NET 8 runtime inside the MSI. Adds ~150–200 MB to the install. Users need no pre-installed runtime. However, the `pkg`-bundled Node.js server.exe is already ~75 MB, so total install would grow substantially.

**Recommendation:** Framework-dependent. Add a prerequisite check to `INSTALL.bat` or an MSI launch condition that verifies `.NET 8 Desktop Runtime (x64)` is installed and downloads it if not, similar to the WebView2 bootstrapper pattern. The Microsoft-hosted winget/direct-download URL for .NET 8 Desktop Runtime is stable and suitable for bundling in `INSTALL.bat`.

### Single-file executable

`dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true` is feasible for the three C# exes (`MastersFM.exe`, `customize.exe`, `audio_spectrum.exe`). However:
- `MastersFM_Tray.exe` embeds PowerShell SDK — single-file + PowerShell has known issues with assembly loading and `[Console]` / runspace init that can cause startup failures. This one should be left as a standard publish output (DLL + host exe).
- `tray_native.dll` must remain a `.dll` because `tray.ps1` loads it via `Add-Type -Path`.
- Single-file mode is not necessary for the MSI packaging approach since all files are already bundled in the CAB.

### AOT vs ReadyToRun vs JIT

- **JIT (default):** Correct choice. The launcher and tray host do WinForms + COM interop + P/Invoke, all of which have known AOT incompatibilities.
- **ReadyToRun (`<PublishReadyToRun>true</PublishReadyToRun>`):** Recommended for `MastersFM.exe` and `audio_spectrum.exe`. Precompiles to native code at publish time; cold start latency drops ~30–50%. Compatible with all P/Invoke and COM usage.
- **AOT (NativeAOT):** Not viable. `System.Management.Automation` (PowerShell SDK) is not AOT-compatible. WinForms has limited AOT support. COM interop via `ComImport` works in AOT but `Marshal.ReleaseComObject` patterns need review. Avoid for this app.

### Runtime Identifier (RID)

All C# targets should publish with `-r win-x64`. This is consistent with the existing `pkg --targets node18-win-x64` and the NAudio DLLs (which ship Windows x64 native code for WASAPI). The MSI already installs to `%LOCALAPPDATA%\MastersFM` and the file list in `build_msi.py` is flat — RID-specific runtime files from a self-contained publish would need to be added to the `FILES` list and assigned new component GUIDs.

---

## MSI / Packaging Changes

### What changes in `build_msi.py`

The Python `msi.dll`-ctypes approach is fully compatible with .NET 8 — the MSI only cares about which files are present, not how they were compiled. The required changes are:

1. **New or changed files in the `FILES` list:**
   - If `dotnet publish` is used (framework-dependent, non-single-file), the output directory for each `.csproj` will contain the target exe plus any additional managed DLLs the project pulls in. Each new DLL needs a new component GUID entry in `FILES`.
   - `MastersFM_Tray.exe` with `Microsoft.PowerShell.SDK` will produce a large dependency tree of PowerShell assemblies (20–40 DLLs). These all need to be bundled in the MSI and added to `FILES`. This is the most significant packaging change.
   - Alternatively, the PowerShell SDK assemblies could be published as a single-file exe to reduce the file count at the cost of startup time.

2. **`netstandard.dll` polyfill DLLs (GUID_COMP20–COMP26):** Several may no longer be needed under .NET 8 (native in-box). Audit `dotnet publish` output and remove obsolete entries.

3. **No WiX upgrade needed.** The build does not use WiX at all. The `build_msi.py` ctypes approach is agnostic to .NET version.

4. **CAB size increase:** Expect the CAB to grow from current size by ~5–10 MB for ReadyToRun pre-compiled native images, plus the PowerShell SDK DLLs if bundled.

5. **`GUID_COMP` additions:** Every new DLL in the publish output requires a new `GUID_COMP` constant and a new `FILES` entry. The naming convention and sequential approach already in use scales without structural changes.

### `INSTALL.bat` additions

Add a `.NET 8 Desktop Runtime` prerequisite check before step 4 (find and run MSI):

```bat
:: Check for .NET 8 Desktop Runtime
dotnet --list-runtimes 2>nul | findstr /C:"Microsoft.WindowsDesktop.App 8." >nul 2>&1
if %errorlevel% NEQ 0 (
    echo [prereq] .NET 8 Desktop Runtime not found - downloading installer...
    :: Download and run the .NET 8 Desktop Runtime x64 installer
    curl -L "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe" -o "%TEMP%\dotnet8_runtime.exe"
    "%TEMP%\dotnet8_runtime.exe" /install /quiet /norestart
)
```

---

## Signing Changes

No changes required to the signing approach:

- `build_tools\signing\_sign_msi.ps1` uses `signtool.exe` (Windows SDK) or `Set-AuthenticodeSignature`. Both work identically on .NET 8 compiled binaries.
- The self-signed cert (`CN=MasterShadex`) approach is unchanged.
- `tray_native.dll` signing is unchanged (it remains a .NET class library).
- If/when a real CA cert (e.g., Certum) is acquired, the thumbprint in `_sign_msi.ps1` is the only change needed — no pipeline restructuring.
- `MastersFM_Tray.exe` and other new publish outputs should also be signed via the same script to avoid Defender warnings on first scan. The script already accepts `-MsiPath` for any PE file.

---

## Estimated Build Pipeline Effort

| Task | Effort |
|---|---|
| Create `.csproj` files for 5 C# targets (launcher, tray_launcher, customize, audio_spectrum, tray_native) | 2–3 hours |
| Replace `csc.exe` invocations in `_full_rebuild.ps1` with `dotnet publish` calls | 1–2 hours |
| Migrate `MastersFM_Tray.exe` from GAC SMA to `Microsoft.PowerShell.SDK` NuGet (highest risk — behavior change + large dep tree) | 4–8 hours |
| Audit `dotnet publish` output for each project and update `FILES` list in `build_msi.py` with new component GUIDs | 2–4 hours |
| Update `_build_spectrum.ps1` to use `dotnet publish` (remove `netstandard.dll` facade reference) | 1 hour |
| Add `.NET 8 Desktop Runtime` prerequisite check to `INSTALL.bat` | 1 hour |
| End-to-end test (clean machine, fresh install, tray loads, SMTC works, audio spectrum works) | 3–5 hours |
| **Total estimate** | **14–24 hours** |

The dominant risk and effort item is `MastersFM_Tray.exe`. The in-process PowerShell 5.1 host (via GAC SMA) must be migrated to PowerShell 7.x SDK, and `tray.ps1` itself may contain PowerShell 5.1-specific constructs that behave differently under PS 7 (e.g., `Add-Type` behavior, COM automation, `[void]` casting, certain WMI/CIM cmdlets). A functional regression sweep of `tray.ps1` under PS 7 should be treated as a parallel workstream to the build pipeline migration.
