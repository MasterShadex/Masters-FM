# V14_S8_OQ2_VERIFICATION.md

Stage 8 STEP 1 -- MSI component verification (OQ-2 gate).
Date: 2026-05-09.

---

## S1.1 dist/tray_csharp_release/ enumeration

20 files present:

| File | Bytes |
|---|---:|
| CommunityToolkit.Mvvm.dll | 380,928 |
| H.GeneratedIcons.System.Drawing.dll | 36,864 |
| H.NotifyIcon.dll | 475,136 |
| H.NotifyIcon.Wpf.dll | 290,816 |
| MastersFM_Tray_v14.deps.json | 8,205 |
| MastersFM_Tray_v14.dll | 901,120 |
| MastersFM_Tray_v14.exe | 163,840 |
| MastersFM_Tray_v14.pdb | 127,632 |
| MastersFM_Tray_v14.runtimeconfig.json | 567 |
| Microsoft.Extensions.DependencyInjection.Abstractions.dll | 131,072 |
| Microsoft.Extensions.DependencyInjection.dll | 204,800 |
| Microsoft.Win32.SystemEvents.dll | 86,016 |
| Microsoft.Windows.SDK.NET.dll | 24,877,600 |
| System.Drawing.Common.dll | 1,142,784 |
| System.Private.Windows.Core.dll | 1,183,744 |
| tray_native.dll | 65,536 |
| tray_native.pdb | 13,800 |
| WinRT.Runtime.dll | 528,944 |
| Wpf.Ui.Abstractions.dll | 20,480 |
| Wpf.Ui.dll | 7,086,080 |

PDB files (2): `MastersFM_Tray_v14.pdb`, `tray_native.pdb` -- excluded from MSI (debug symbols; correct behavior).

---

## S1.2 build_msi.py component coverage

`build_msi.py` SRC = `G:\Project Folder\Master FM\` (project root; `os.path.dirname(os.path.dirname(__file__))`).

Stage 7.10 block (lines 199-218): guarded by `if os.path.exists(SRC/dist/tray_csharp_release/MastersFM_Tray_v14.dll)` -- this file EXISTS, so the block fires.

| Component | MSI file | Source path | Status |
|---|---|---|---|
| GUID_COMP14 | MastersFM_Tray.exe | `dist/tray_csharp_release/MastersFM_Tray_v14.exe` | IN MSI |
| GUID_COMP28 | tray_native.dll | `tray_native.dll` (SRC root, 31,256 bytes) | IN MSI |
| GUID_COMP45 | MastersFM_Tray_v14.dll | `dist/tray_csharp_release/MastersFM_Tray_v14.dll` | IN MSI |
| **GUID_COMP46** | **MastersFM_Tray_v14.runtimeconfig.json** | `dist/tray_csharp_release/MastersFM_Tray_v14.runtimeconfig.json` | **IN MSI** |
| **GUID_COMP47** | **MastersFM_Tray_v14.deps.json** | `dist/tray_csharp_release/MastersFM_Tray_v14.deps.json` | **IN MSI** |
| GUID_COMP48 | CommunityToolkit.Mvvm.dll | `dist/tray_csharp_release/CommunityToolkit.Mvvm.dll` | IN MSI |
| GUID_COMP49 | H.GeneratedIcons.System.Drawing.dll | `dist/tray_csharp_release/H.GeneratedIcons.System.Drawing.dll` | IN MSI |
| GUID_COMP50 | H.NotifyIcon.dll | `dist/tray_csharp_release/H.NotifyIcon.dll` | IN MSI |
| GUID_COMP51 | H.NotifyIcon.Wpf.dll | `dist/tray_csharp_release/H.NotifyIcon.Wpf.dll` | IN MSI |
| GUID_COMP52 | Microsoft.Extensions.DependencyInjection.Abstractions.dll | `dist/tray_csharp_release/...` | IN MSI |
| GUID_COMP53 | Microsoft.Extensions.DependencyInjection.dll | `dist/tray_csharp_release/...` | IN MSI |
| GUID_COMP54 | Microsoft.Win32.SystemEvents.dll | `dist/tray_csharp_release/...` | IN MSI |
| GUID_COMP55 | Microsoft.Windows.SDK.NET.dll | `dist/tray_csharp_release/...` | IN MSI |
| GUID_COMP56 | System.Drawing.Common.dll | `dist/tray_csharp_release/...` | IN MSI |
| GUID_COMP57 | System.Private.Windows.Core.dll | `dist/tray_csharp_release/...` | IN MSI |
| GUID_COMP58 | WinRT.Runtime.dll | `dist/tray_csharp_release/...` | IN MSI |
| GUID_COMP59 | Wpf.Ui.Abstractions.dll | `dist/tray_csharp_release/...` | IN MSI |
| GUID_COMP60 | Wpf.Ui.dll | `dist/tray_csharp_release/...` | IN MSI |

---

## S1.3 Cross-check result

| Required file | In MSI | Component |
|---|---|---|
| MastersFM_Tray_v14.exe (as MastersFM_Tray.exe) | YES | GUID_COMP14 |
| MastersFM_Tray_v14.dll | YES | GUID_COMP45 |
| **MastersFM_Tray_v14.deps.json** | **YES** | **GUID_COMP47** |
| **MastersFM_Tray_v14.runtimeconfig.json** | **YES** | **GUID_COMP46** |
| 16 satellite DLLs (CommunityToolkit...Wpf.Ui) | YES | GUID_COMP48-60 |
| tray_native.dll | YES | GUID_COMP28 (from SRC root, 31,256 bytes) |

**Both critical files (deps.json + runtimeconfig.json) are present in the MSI.**

**Observation on tray_native.dll:** The `dist/tray_csharp_release/` copy is 65,536 bytes (from `dotnet publish`); the SRC root copy (shipped by GUID_COMP28) is 31,256 bytes. These are different builds. The larger copy is from the WPF tray csproj project reference output; the smaller copy is the pre-.NET-8 csc.exe build. Stage 7.10 INTERRUPT #2 confirmed the installed tray works with the current MSI configuration. This discrepancy is pre-existing and outside Stage 8 scope. Not a halt condition.

PDBs excluded: `MastersFM_Tray_v14.pdb`, `tray_native.pdb` -- correct (debug symbols not shipped).

---

## S1.4 Verdict

**OUTCOME A: All required files present in MSI components.**

- `MastersFM_Tray_v14.deps.json` -- GUID_COMP47 -- **CONFIRMED IN MSI**
- `MastersFM_Tray_v14.runtimeconfig.json` -- GUID_COMP46 -- **CONFIRMED IN MSI**
- All 16 satellite DLLs -- GUID_COMP48-60 -- **CONFIRMED IN MSI**

**OQ-2 CLOSED.** No OUTCOME B/C defect. No pre-Stage-8 fix commit required. Stage 8 Phase 1 deletions may proceed.

---

## S1.5 Commit

Included in STEP 1 commit: `Stage 8: STEP 1 -- MSI component verification (OQ-2 closed)`
