# V14_S7_S7_10_CUTOVER_INVENTORY.md

Stage 7.10 STEP 1 -- read-only cutover inventory.
Date: 2026-05-08. Pattern A/B decision locked in S1.4.

---

## S1.1 -- Build flag inventory

### `_full_rebuild.ps1` flag state

| Flag | Line | Current default | Cutover action |
|---|---:|---|---|
| `$UseDotnet8Launcher` | 19 | `$true` | Unchanged |
| `$UseDotnet8AudioSpectrum` | 22 | `$true` | Unchanged |
| `$UseDotnet8Customize` | 25 | `$true` | Unchanged |
| `$UseDotnet8Bootstrapper` | 30 | `$false` | Unchanged (AV issue) |
| `$UseDotnet8Server` | 33 | `$true` | Unchanged |
| `$UseDotnetTrayNative` | 37 | `$true` | Unchanged |
| **`$UseDotnet8TrayCs`** | **45** | **`$false`** | **FLIP to `$true`** |

All Stages 1-5 are already shipped with $true. `$UseDotnet8Bootstrapper` stays `$false` (no real CA cert, AV concern documented in RC1).

### C# tray branch structure

`_full_rebuild.ps1` line 262:
```powershell
if ($UseDotnet8TrayCs) {
    # builds MastersFM_Tray_v14.exe to dist\tray_csharp_release\
}
```

Output: `dist\tray_csharp_release\MastersFM_Tray_v14.exe` (160 KB exe, ~36.8 MB total publish output)

### csc.exe rollback path (MUST REMAIN FUNCTIONAL)

`_full_rebuild.ps1` lines 186-227 (inside `if ($csc)` block, NOT gated by `$UseDotnet8TrayCs`):
```
[1d/5] Compiling MastersFM_Tray.exe (PowerShell host) via csc.exe...
```
- Source: `src\tray_launcher.cs`
- Output: `MastersFM_Tray.exe` in project root
- Condition: fires whenever `$csc` is found (always, if csc.exe is on PATH)
- **After cutover: csc.exe path is unchanged; still runs; produces PS-tray host as emergency revert**
- The flag flip to `$true` does NOT disable the csc.exe PS-tray build; it adds the WPF build on top
- Emergency rollback: set `$UseDotnet8TrayCs = $false` + change FILES list back = MSI ships csc.exe PS-tray again

---

## S1.2 -- MSI FILES list inventory

### Installer format

- **No `install.iss`** (Inno Setup not present in project)
- **`build_tools/build_msi.py`** is the ONLY MSI builder (Python + Windows msi.dll via ctypes)
- Output: `Master's FM Install\MastersFM_Setup.msi`

### Current FILES list (tray-related entries)

Line 109 (CURRENT -- PS-tray):
```python
("MastersFM_Tray.exe", "MastersFM_Tray.exe", GUID_COMP14)
```
Source: project root `MastersFM_Tray.exe` (built by csc.exe from `tray_launcher.cs`)

### WPF publish output (`dist\tray_csharp_release\`) -- 20 files

| File | Size | Ship? | Note |
|---|---:|---|---|
| `MastersFM_Tray_v14.exe` | 160 KB | YES (Pattern B: install as `MastersFM_Tray.exe`) | apphost stub |
| `MastersFM_Tray_v14.dll` | 880 KB | YES | managed assembly (apphost looks for this by name) |
| `MastersFM_Tray_v14.runtimeconfig.json` | 0.6 KB | YES | baked in apphost, must be present |
| `MastersFM_Tray_v14.deps.json` | 8 KB | YES | assembly dependency graph |
| `MastersFM_Tray_v14.pdb` | 124.6 KB | NO | debug symbols |
| `CommunityToolkit.Mvvm.dll` | 372 KB | YES | |
| `H.GeneratedIcons.System.Drawing.dll` | 36 KB | YES | |
| `H.NotifyIcon.dll` | 464 KB | YES | |
| `H.NotifyIcon.Wpf.dll` | 284 KB | YES | |
| `Microsoft.Extensions.DependencyInjection.Abstractions.dll` | 128 KB | YES | |
| `Microsoft.Extensions.DependencyInjection.dll` | 200 KB | YES | |
| `Microsoft.Win32.SystemEvents.dll` | 84 KB | YES | |
| `Microsoft.Windows.SDK.NET.dll` | 24294 KB | YES | CSWinRT projection (24 MB) |
| `System.Drawing.Common.dll` | 1116 KB | YES | |
| `System.Private.Windows.Core.dll` | 1156 KB | YES | |
| `WinRT.Runtime.dll` | 516 KB | YES | |
| `Wpf.Ui.Abstractions.dll` | 20 KB | YES | |
| `Wpf.Ui.dll` | 6920 KB | YES | |
| `tray_native.dll` | 64 KB | SKIP | already in GUID_COMP28 from project root |
| `tray_native.pdb` | 13.5 KB | NO | debug symbols |

**Net new DLLs to add to MSI: 16 entries (GUID_COMP45 through GUID_COMP60)**

### Note on apphost + rename (.NET runtime resolution)

The apphost (`MastersFM_Tray_v14.exe`) stores the managed DLL path and runtimeconfig path as EMBEDDED strings. Renaming the exe to `MastersFM_Tray.exe` does NOT affect these embedded paths -- the renamed stub continues to look for `MastersFM_Tray_v14.dll` and `MastersFM_Tray_v14.runtimeconfig.json` (same names as in publish output). Pattern B is safe without any secondary file renames.

### Consumers of `MastersFM_Tray.exe` name

| Consumer | Location | Needs change if Pattern B? |
|---|---|---|
| `launcher.cs` line 451 | `Path.Combine(dir, "MastersFM_Tray.exe")` | NO -- Pattern B keeps same filename |
| `_full_rebuild.ps1` stop-process (line 467) | `Stop-Process -Name 'MastersFM_Tray'` | NO -- process name derived from exe name |
| `build_msi.py` uninstall VBScript (line 358) | `Stop-Process -Name MastersFM_Tray` | NO |
| `build_msi.py` FILES list (line 109) | source changed; install name stays `MastersFM_Tray.exe` | Source path changes |

### Argument forwarding compatibility

`launcher.cs` passes `-scriptDir "<dir>" -skipServerLaunch` to `MastersFM_Tray.exe`. The WPF App.xaml.cs only checks for `--smoke-dialogs` (line 260); all other args are ignored. The new WPF tray starts normally when invoked with these legacy args.

---

## S1.3 -- Auto-update implications

`version.json` stays at v12.0.1 (modified-unstaged per absolute rule 7). The auto-update check in the tray polls for `version > APP_VERSION`; since the manifest still shows v12.0.1, no auto-update is triggered by this cutover.

**Cutover is local-only.** The change affects:
1. `_full_rebuild.ps1` (toggle) -- local build script
2. `build_msi.py` (FILES list) -- local MSI builder
3. The resulting MSI (local artifact, not pushed to GitHub Releases)

No tester running v12.0.1 will receive v14 from this commit. The auto-update channel remains dormant until version.json is bumped in a future ship-prep brief AND the MSI is uploaded to GitHub Releases.

---

## S1.4 -- Tray exe naming decision: PATTERN B

**Decision: Pattern B. The new C# WPF tray is installed as `MastersFM_Tray.exe`.**

### Rationale

1. `launcher.cs` spawns by filename (`MastersFM_Tray.exe`); Pattern B requires zero launcher changes
2. Uninstall VBScript kills `MastersFM_Tray` by process name; Pattern B matches naturally
3. Start/stop/monitoring all key off `MastersFM_Tray` process name; Pattern B preserves these
4. The apphost rename from `MastersFM_Tray_v14.exe` to `MastersFM_Tray.exe` is safe -- embedded DLL/runtimeconfig paths are not affected
5. Stage 8 (build pipeline cleanup) can revisit naming at its leisure; 7.10 is a zero-churn cutover

### Implementation

**`_full_rebuild.ps1` change (toggle flip only -- 1 line):**
```
line 45: $UseDotnet8TrayCs = $false  =>  $UseDotnet8TrayCs = $true
```

**`build_msi.py` changes (FILES list):**

1. GUID_COMP14 entry (line 109): change source path to C# build output + keep install name
   ```python
   # Before:
   ("MastersFM_Tray.exe", "MastersFM_Tray.exe", GUID_COMP14)
   # After:
   ("dist/tray_csharp_release/MastersFM_Tray_v14.exe", "MastersFM_Tray.exe", GUID_COMP14)
   ```

2. New conditional block (GUID_COMP45-COMP60) for WPF satellite DLLs -- appended after GUID_COMP44 section, conditioned on presence of the WPF publish output (same pattern as Stages 1-4):
   ```python
   GUID_COMP45 = ...  # MastersFM_Tray_v14.dll
   GUID_COMP46 = ...  # MastersFM_Tray_v14.runtimeconfig.json
   GUID_COMP47 = ...  # MastersFM_Tray_v14.deps.json
   GUID_COMP48 = ...  # CommunityToolkit.Mvvm.dll
   GUID_COMP49 = ...  # H.GeneratedIcons.System.Drawing.dll
   GUID_COMP50 = ...  # H.NotifyIcon.dll
   GUID_COMP51 = ...  # H.NotifyIcon.Wpf.dll
   GUID_COMP52 = ...  # Microsoft.Extensions.DependencyInjection.Abstractions.dll
   GUID_COMP53 = ...  # Microsoft.Extensions.DependencyInjection.dll
   GUID_COMP54 = ...  # Microsoft.Win32.SystemEvents.dll
   GUID_COMP55 = ...  # Microsoft.Windows.SDK.NET.dll
   GUID_COMP56 = ...  # System.Drawing.Common.dll
   GUID_COMP57 = ...  # System.Private.Windows.Core.dll
   GUID_COMP58 = ...  # WinRT.Runtime.dll
   GUID_COMP59 = ...  # Wpf.Ui.Abstractions.dll
   GUID_COMP60 = ...  # Wpf.Ui.dll
   ```
   Note: `tray_native.dll` is already GUID_COMP28, NOT duplicated.
   Note: `*.pdb` files are NOT included (debug symbols).

---

## S1.5 -- RC1 backup notes review

`V14_RC1_FLAGS_FINALIZED.md` confirms all Stages 1-5 flags already `$true`. `V14_RC1_HALT_REPORT.md` records the RC1 ship pause. No MSI-specific notes conflict with Pattern B.

`RELEASE_NOTES_v14.0.0-rc.1.md` exists -- STEP 2.1 will add the first-run wizard memory note.

---

## S1.6 -- Summary of cutover touch points

| File | Change | Scope |
|---|---|---|
| `_full_rebuild.ps1` | line 45: `$false` -> `$true` | Toggle flip only |
| `build_tools/build_msi.py` | GUID_COMP14 source path + 16 new GUID entries (COMP45-60) | FILES list per Pattern B |
| `RELEASE_NOTES_v14.0.0-rc.1.md` | One-line first-run wizard memory note | Documentation only |

Protected files (tray.ps1, tray_native.cs, launcher.cs, server.js, memory.md): UNCHANGED.
`src/tray_launcher.cs`: stays on disk (Stage 8 concern, per absolute rule in brief).
`src/tray.ps1`: stays on disk (dead code after cutover, per absolute rule).
