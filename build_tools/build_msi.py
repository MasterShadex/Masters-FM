"""
Master's FM MSI builder — uses Windows Installer API (msi.dll) via ctypes.
Runs on any Windows machine with Python installed. No WiX, no dependencies.
"""

import ctypes, ctypes.wintypes, os, sys, struct, tempfile, shutil, subprocess, uuid, re

SRC   = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT   = os.path.join(SRC, "Master's FM Install", "MastersFM_Setup.msi")

def _parse_app_version():
    # Pull $script:APP_VERSION = "v6.9.2" out of tray.ps1. The MSI
    # ProductVersion needs to monotonically increase per release so
    # the Major-Upgrade machinery (Upgrade table + RemoveExistingProducts)
    # detects older installs as "older". Falls back to a low version on
    # any parse failure so the build never crashes silently.
    #
    # v14.0.0-rc.1: also strip SemVer pre-release suffix (-rc.N, -beta.N,
    # -alpha.N, -pre.N) from MSI ProductVersion. Windows Installer's
    # ProductVersion field requires numeric Major.Minor.Build only and
    # rejects non-numeric segments. The display form (with suffix) is
    # used elsewhere (filename, GitHub URL, version.json); only the
    # MSI internal ProductVersion gets the suffix stripped.
    tray_path = os.path.join(SRC, 'src', 'tray.ps1')
    try:
        with open(tray_path, 'r', encoding='utf-8') as f:
            for line in f:
                m = re.match(r'\s*\$script:APP_VERSION\s*=\s*"([^"]+)"', line)
                if m:
                    disp = m.group(1)
                    msi  = disp[1:] if disp.startswith('v') else disp
                    msi  = re.sub(r'-(?:rc|beta|alpha|pre)\.?\d*$', '', msi)
                    return disp, msi
    except Exception:
        pass
    return 'v0.0.0', '0.0.0'

APP_VERSION_DISPLAY, APP_VERSION_MSI = _parse_app_version()
# Fresh ProductCode every build → MSI treats each release as a NEW product,
# Upgrade table removes the prior install cleanly. Static ProductCode caused
# 1603 SECUREREPAIR errors when the cached-source filename mismatched.
PRODUCT_CODE = '{' + str(uuid.uuid4()).upper() + '}'

GUID_UPGRADE = "{11111111-1111-1111-1111-111111111111}"
GUID_COMP1   = "{22222222-2222-2222-2222-222222222222}"
GUID_COMP2   = "{33333333-3333-3333-3333-333333333333}"
GUID_COMP3   = "{44444444-4444-4444-4444-444444444444}"
GUID_COMP4   = "{55555555-5555-5555-5555-555555555555}"
GUID_COMP5   = "{66666666-6666-6666-6666-666666666666}"
GUID_COMP6   = "{77777777-7777-7777-7777-777777777777}"
GUID_COMP7   = "{88888888-8888-8888-8888-888888888888}"
GUID_COMP8   = "{99999999-9999-9999-9999-999999999999}"
GUID_COMP9   = "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"
GUID_COMP10  = "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBC}"  # customize.exe
GUID_COMP11  = "{CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC}"  # WebView2.Core.dll
GUID_COMP12  = "{DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD}"  # WebView2.WinForms.dll
GUID_COMP13  = "{EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE}"  # WebView2Loader.dll
GUID_COMP14  = "{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}"  # MastersFM_Tray.exe
GUID_COMP15  = "{11111111-2222-3333-4444-555555555555}"  # audio_spectrum.exe (v5.3.0 multi-backend)
GUID_COMP16  = "{22222222-3333-4444-5555-666666666666}"  # NAudio.Core.dll
GUID_COMP17  = "{33333333-4444-5555-6666-777777777777}"  # NAudio.Wasapi.dll
GUID_COMP18  = "{44444444-5555-6666-7777-888888888888}"  # NAudio.WinMM.dll (MME WaveIn)
GUID_COMP19  = "{55555555-6666-7777-8888-999999999999}"  # NAudio.Asio.dll (ASIO)
GUID_COMP20  = "{66666666-7777-8888-9999-AAAAAAAAAAAA}"  # Microsoft.Win32.Registry.dll (needed by NAudio.Asio)
GUID_COMP21  = "{77777777-8888-9999-AAAA-BBBBBBBBBBBB}"  # System.Buffers.dll
GUID_COMP22  = "{88888888-9999-AAAA-BBBB-CCCCCCCCCCCC}"  # System.Memory.dll
GUID_COMP23  = "{99999999-AAAA-BBBB-CCCC-DDDDDDDDDDDD}"  # System.Numerics.Vectors.dll
GUID_COMP24  = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"  # System.Runtime.CompilerServices.Unsafe.dll
GUID_COMP25  = "{BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF}"  # System.Security.AccessControl.dll
GUID_COMP26  = "{CCCCCCCC-DDDD-EEEE-FFFF-111111111111}"  # System.Security.Principal.Windows.dll
GUID_COMP27  = "{DDDDDDDD-EEEE-FFFF-1111-222222222222}"  # update.html
GUID_COMP28  = "{EEEEEEEE-1111-2222-3333-444444444444}"  # tray_native.dll (pre-compiled P/Invoke types)
# Stage 1 (.NET 8 launcher) - extra artifacts produced by dotnet publish
GUID_COMP29  = "{FFFFFFFF-2222-3333-4444-555555555556}"  # MastersFM.dll (.NET 8 managed assembly)
GUID_COMP30  = "{11111111-FFFF-3333-4444-555555555556}"  # MastersFM.runtimeconfig.json
GUID_COMP31  = "{22222222-FFFF-3333-4444-555555555556}"  # MastersFM.deps.json
# Stage 2 (.NET 8 audio_spectrum) - extra artifacts produced by dotnet publish
GUID_COMP32  = "{33333333-FFFF-4444-5555-666666666667}"  # audio_spectrum.dll (.NET 8 managed assembly)
GUID_COMP33  = "{44444444-FFFF-5555-6666-777777777778}"  # audio_spectrum.runtimeconfig.json
GUID_COMP34  = "{55555555-FFFF-6666-7777-888888888889}"  # audio_spectrum.deps.json
# Stage 3a (.NET 8 customize) - extra artifacts produced by dotnet publish
GUID_COMP35  = "{66666666-FFFF-7777-8888-99999999999A}"  # customize.dll (.NET 8 managed assembly)
GUID_COMP36  = "{77777777-FFFF-8888-9999-AAAAAAAAAAAB}"  # customize.runtimeconfig.json
GUID_COMP37  = "{88888888-FFFF-9999-AAAA-BBBBBBBBBBBC}"  # customize.deps.json
# Stage 4 (.NET 10 server) - extra artifacts produced by dotnet publish (server.exe already in GUID_COMP1)
GUID_COMP38  = "{99999999-FFFF-AAAA-BBBB-CCCCCCCCCCD0}"  # server.dll (.NET ASP.NET Core managed assembly)
GUID_COMP39  = "{AAAAAAAA-FFFF-BBBB-CCCC-DDDDDDDDDDE1}"  # server.deps.json
GUID_COMP40  = "{BBBBBBBB-FFFF-CCCC-DDDD-EEEEEEEEEEF2}"  # server.runtimeconfig.json
GUID_COMP41  = "{CCCCCCCC-FFFF-DDDD-EEEE-FFFFFFFFFFF3}"  # server.staticwebassets.endpoints.json
GUID_COMP42  = "{DDDDDDDD-FFFF-EEEE-FFFF-000000000004}"  # web.config (IIS hosting shim)
# Stage 4.10 (Discord RPC) - Lachee.DiscordRPC NuGet runtime DLL + transitive Newtonsoft.Json
GUID_COMP43  = "{EEEEEEEE-FFFF-FFFF-0000-111111111115}"  # DiscordRPC.dll (Lachee.DiscordRPC 1.2.1.24)
GUID_COMP44  = "{FFFFFFFF-FFFF-0000-1111-222222222226}"  # Newtonsoft.Json.dll (DiscordRPC transitive dep)
# Stage 7.10 (.NET 8 WPF C# tray) - satellite DLLs from dotnet publish (Pattern B cutover)
# MastersFM_Tray_v14.exe is handled by the modified GUID_COMP14 entry (installed as MastersFM_Tray.exe)
# tray_native.dll is already GUID_COMP28 -- NOT duplicated here
# MastersFM_Tray_v14.pdb and tray_native.pdb excluded (debug symbols only)
GUID_COMP45  = "{11111111-BBBB-7777-8888-999999999997}"  # MastersFM_Tray_v14.dll
GUID_COMP46  = "{22222222-BBBB-7777-8888-999999999997}"  # MastersFM_Tray_v14.runtimeconfig.json
GUID_COMP47  = "{33333333-BBBB-7777-8888-999999999997}"  # MastersFM_Tray_v14.deps.json
GUID_COMP48  = "{44444444-BBBB-7777-8888-999999999997}"  # CommunityToolkit.Mvvm.dll
GUID_COMP49  = "{55555555-BBBB-7777-8888-999999999997}"  # H.GeneratedIcons.System.Drawing.dll
GUID_COMP50  = "{66666666-BBBB-7777-8888-999999999997}"  # H.NotifyIcon.dll
GUID_COMP51  = "{77777777-BBBB-7777-8888-999999999997}"  # H.NotifyIcon.Wpf.dll
GUID_COMP52  = "{88888888-BBBB-7777-8888-999999999997}"  # Microsoft.Extensions.DependencyInjection.Abstractions.dll
GUID_COMP53  = "{99999999-BBBB-7777-8888-999999999997}"  # Microsoft.Extensions.DependencyInjection.dll
GUID_COMP54  = "{AAAAAAAA-BBBB-7777-8888-999999999997}"  # Microsoft.Win32.SystemEvents.dll
GUID_COMP55  = "{BBBBBBBB-BBBB-7777-8888-999999999997}"  # Microsoft.Windows.SDK.NET.dll
GUID_COMP56  = "{CCCCCCCC-BBBB-7777-8888-999999999997}"  # System.Drawing.Common.dll
GUID_COMP57  = "{DDDDDDDD-BBBB-7777-8888-999999999997}"  # System.Private.Windows.Core.dll
GUID_COMP58  = "{EEEEEEEE-BBBB-7777-8888-999999999997}"  # WinRT.Runtime.dll
GUID_COMP59  = "{FFFFFFFF-BBBB-7777-8888-999999999997}"  # Wpf.Ui.Abstractions.dll
GUID_COMP60  = "{11111111-CCCC-7777-8888-999999999997}"  # Wpf.Ui.dll
# Stage 7.8C STEP 5: OBS cleanup binary (installed to %PROGRAMDATA%\MastersFM\Cleanup\)
GUID_COMP61  = "{11111111-DDDD-8888-9999-AAAAAAAAAAAA}"  # MastersFM_ObsCleanup.exe
GUID_COMP62  = "{22222222-DDDD-8888-9999-AAAAAAAAAAAA}"  # MastersFM_ObsCleanup.dll
GUID_COMP63  = "{33333333-DDDD-8888-9999-AAAAAAAAAAAA}"  # MastersFM_ObsCleanup.runtimeconfig.json
GUID_COMP64  = "{44444444-DDDD-8888-9999-AAAAAAAAAAAA}"  # MastersFM_ObsCleanup.deps.json
# Stage 7.30.7: customize_legacy.html bundled into the MSI (instant-revert safety net; LEGACY_SHA 7E98377D).
GUID_COMP65  = "{55555555-DDDD-8888-9999-AAAAAAAAAAAA}"  # customize_legacy.html

# (source filename in SRC folder, install filename, component GUID)
FILES = [
    ("server.exe",                         "server.exe",                         GUID_COMP1),
    ("src/tray.ps1",                       "tray.ps1",                           GUID_COMP2),
    ("assets/MastersFM.ico",               "MastersFM.ico",                      GUID_COMP4),
    ("MastersFM.exe",                      "MastersFM.exe",                      GUID_COMP5),   # C# launcher
    ("src/config_default.json",            "config_default.json",                GUID_COMP6),
    ("src/overlay.html",                   "overlay.html",                       GUID_COMP7),
    ("src/customize.html",                 "customize.html",                     GUID_COMP8),
    ("src/customize_legacy.html",          "customize_legacy.html",              GUID_COMP65),  # Stage 7.30.7: instant-revert safety net (LEGACY_SHA 7E98377D)
    ("src/discord_rpc.js",                 "discord_rpc.js",                     GUID_COMP9),
    ("customize.exe",                      "customize.exe",                      GUID_COMP10),  # WebView2-hosted customize window
    ("Microsoft.Web.WebView2.Core.dll",    "Microsoft.Web.WebView2.Core.dll",    GUID_COMP11),
    ("Microsoft.Web.WebView2.WinForms.dll","Microsoft.Web.WebView2.WinForms.dll",GUID_COMP12),
    ("WebView2Loader.dll",                 "WebView2Loader.dll",                 GUID_COMP13),
    ("dist/tray_csharp_release/MastersFM_Tray_v14.exe", "MastersFM_Tray.exe",    GUID_COMP14),  # WPF C# tray (Pattern B: installed as MastersFM_Tray.exe; apphost rename safe)
    ("audio_spectrum.exe",                 "audio_spectrum.exe",                 GUID_COMP15),  # multi-backend (WASAPI/MME/KS/ASIO) spectrum provider (v5.3.0)
    ("NAudio.Core.dll",                    "NAudio.Core.dll",                    GUID_COMP16),  # NAudio core (needed by audio_spectrum.exe)
    ("NAudio.Wasapi.dll",                  "NAudio.Wasapi.dll",                  GUID_COMP17),  # NAudio WASAPI loopback + capture
    ("NAudio.WinMM.dll",                   "NAudio.WinMM.dll",                   GUID_COMP18),  # NAudio MME (legacy waveIn)
    ("NAudio.Asio.dll",                    "NAudio.Asio.dll",                    GUID_COMP19),  # NAudio ASIO (pro audio drivers)
    ("Microsoft.Win32.Registry.dll",          "Microsoft.Win32.Registry.dll",          GUID_COMP20),  # netstandard2.0 facade - NAudio.Asio reads HKLM\SOFTWARE\ASIO
    ("System.Buffers.dll",                    "System.Buffers.dll",                    GUID_COMP21),  # transitive dep of Microsoft.Win32.Registry chain
    ("System.Memory.dll",                     "System.Memory.dll",                     GUID_COMP22),
    ("System.Numerics.Vectors.dll",           "System.Numerics.Vectors.dll",           GUID_COMP23),
    ("System.Runtime.CompilerServices.Unsafe.dll", "System.Runtime.CompilerServices.Unsafe.dll", GUID_COMP24),
    ("System.Security.AccessControl.dll",     "System.Security.AccessControl.dll",     GUID_COMP25),
    ("System.Security.Principal.Windows.dll", "System.Security.Principal.Windows.dll", GUID_COMP26),
    ("src/update.html",                       "update.html",                           GUID_COMP27),
    ("tray_native.dll",                       "tray_native.dll",                       GUID_COMP28),  # pre-compiled P/Invoke types (eliminates csc.exe at tray startup)
]

# Stage 1: .NET 8 launcher produces extra artifacts alongside MastersFM.exe.
# Conditionally appended so the legacy csc.exe build (UseDotnet8Launcher=false) still works.
_net8_launcher_dll = os.path.join(SRC, "MastersFM.dll")
if os.path.exists(_net8_launcher_dll):
    FILES.extend([
        ("MastersFM.dll",                "MastersFM.dll",                GUID_COMP29),
        ("MastersFM.runtimeconfig.json", "MastersFM.runtimeconfig.json", GUID_COMP30),
        ("MastersFM.deps.json",          "MastersFM.deps.json",          GUID_COMP31),
    ])

# Stage 2: .NET 8 audio_spectrum produces extra artifacts alongside audio_spectrum.exe.
# Conditionally appended so the legacy csc.exe build (UseDotnet8AudioSpectrum=false) still works.
_net8_spectrum_dll = os.path.join(SRC, "audio_spectrum.dll")
if os.path.exists(_net8_spectrum_dll):
    FILES.extend([
        ("audio_spectrum.dll",                "audio_spectrum.dll",                GUID_COMP32),
        ("audio_spectrum.runtimeconfig.json", "audio_spectrum.runtimeconfig.json", GUID_COMP33),
        ("audio_spectrum.deps.json",          "audio_spectrum.deps.json",          GUID_COMP34),
    ])

# Stage 3a: .NET 8 customize produces extra artifacts alongside customize.exe.
# Conditionally appended so the legacy csc.exe build (UseDotnet8Customize=false) still works.
# WebView2 DLLs (Core, WinForms, Loader) are already in FILES above (GUID_COMP11-13) and
# are updated in-place from the dotnet publish output by _full_rebuild.ps1.
_net8_customize_dll = os.path.join(SRC, "customize.dll")
if os.path.exists(_net8_customize_dll):
    FILES.extend([
        ("customize.dll",                "customize.dll",                GUID_COMP35),
        ("customize.runtimeconfig.json", "customize.runtimeconfig.json", GUID_COMP36),
        ("customize.deps.json",          "customize.deps.json",          GUID_COMP37),
    ])

# Stage 4: .NET ASP.NET Core server produces extra artifacts alongside server.exe.
# Conditionally appended so the legacy Node pkg build (UseDotnet8Server=false) still works.
# server.exe itself is already in FILES above (GUID_COMP1) and is updated in-place by _full_rebuild.ps1.
_net10_server_dll = os.path.join(SRC, "server.dll")
if os.path.exists(_net10_server_dll):
    _optional = [
        ("server.dll",                               "server.dll",                               GUID_COMP38),
        ("server.deps.json",                         "server.deps.json",                         GUID_COMP39),
        ("server.runtimeconfig.json",                "server.runtimeconfig.json",                GUID_COMP40),
        ("server.staticwebassets.endpoints.json",    "server.staticwebassets.endpoints.json",    GUID_COMP41),
        ("web.config",                               "web.config",                               GUID_COMP42),
        # Stage 4.10 Discord RPC runtime deps (RC1 fix: previously missing from MSI -> server crash on launch)
        ("DiscordRPC.dll",                           "DiscordRPC.dll",                           GUID_COMP43),
        ("Newtonsoft.Json.dll",                      "Newtonsoft.Json.dll",                      GUID_COMP44),
    ]
    FILES.extend([(s, d, g) for s, d, g in _optional if os.path.exists(os.path.join(SRC, s))])

# Stage 7.10: .NET 8 WPF tray satellite DLLs (Pattern B cutover).
# MastersFM_Tray_v14.exe itself is already in GUID_COMP14 above (installed as MastersFM_Tray.exe).
# tray_native.dll is already GUID_COMP28 -- skipped here to avoid duplicate component error.
# .pdb files excluded (debug symbols, not shipped).
_cs_tray_dll = os.path.join(SRC, "dist", "tray_csharp_release", "MastersFM_Tray_v14.dll")
if os.path.exists(_cs_tray_dll):
    FILES.extend([
        ("dist/tray_csharp_release/MastersFM_Tray_v14.dll",                                "MastersFM_Tray_v14.dll",                                GUID_COMP45),
        ("dist/tray_csharp_release/MastersFM_Tray_v14.runtimeconfig.json",                 "MastersFM_Tray_v14.runtimeconfig.json",                 GUID_COMP46),
        ("dist/tray_csharp_release/MastersFM_Tray_v14.deps.json",                          "MastersFM_Tray_v14.deps.json",                          GUID_COMP47),
        ("dist/tray_csharp_release/CommunityToolkit.Mvvm.dll",                             "CommunityToolkit.Mvvm.dll",                             GUID_COMP48),
        ("dist/tray_csharp_release/H.GeneratedIcons.System.Drawing.dll",                   "H.GeneratedIcons.System.Drawing.dll",                   GUID_COMP49),
        ("dist/tray_csharp_release/H.NotifyIcon.dll",                                      "H.NotifyIcon.dll",                                      GUID_COMP50),
        ("dist/tray_csharp_release/H.NotifyIcon.Wpf.dll",                                  "H.NotifyIcon.Wpf.dll",                                  GUID_COMP51),
        ("dist/tray_csharp_release/Microsoft.Extensions.DependencyInjection.Abstractions.dll", "Microsoft.Extensions.DependencyInjection.Abstractions.dll", GUID_COMP52),
        ("dist/tray_csharp_release/Microsoft.Extensions.DependencyInjection.dll",          "Microsoft.Extensions.DependencyInjection.dll",          GUID_COMP53),
        ("dist/tray_csharp_release/Microsoft.Win32.SystemEvents.dll",                      "Microsoft.Win32.SystemEvents.dll",                      GUID_COMP54),
        ("dist/tray_csharp_release/Microsoft.Windows.SDK.NET.dll",                         "Microsoft.Windows.SDK.NET.dll",                         GUID_COMP55),
        ("dist/tray_csharp_release/System.Drawing.Common.dll",                             "System.Drawing.Common.dll",                             GUID_COMP56),
        ("dist/tray_csharp_release/System.Private.Windows.Core.dll",                       "System.Private.Windows.Core.dll",                       GUID_COMP57),
        ("dist/tray_csharp_release/WinRT.Runtime.dll",                                     "WinRT.Runtime.dll",                                     GUID_COMP58),
        ("dist/tray_csharp_release/Wpf.Ui.Abstractions.dll",                               "Wpf.Ui.Abstractions.dll",                               GUID_COMP59),
        ("dist/tray_csharp_release/Wpf.Ui.dll",                                            "Wpf.Ui.dll",                                            GUID_COMP60),
    ])

# Stage 7.8C STEP 5: OBS cleanup binary — shipped to %PROGRAMDATA%\MastersFM\Cleanup\.
# Conditional: only included if dist/obs_cleanup_release was built (mirrors other artifact blocks).
CLEANUP_FILES = []
_cleanup_exe_path = os.path.join(SRC, "dist", "obs_cleanup_release", "MastersFM_ObsCleanup.exe")
if os.path.exists(_cleanup_exe_path):
    _all_cleanup = [
        ("dist/obs_cleanup_release/MastersFM_ObsCleanup.exe",                "MastersFM_ObsCleanup.exe",                GUID_COMP61),
        ("dist/obs_cleanup_release/MastersFM_ObsCleanup.dll",                "MastersFM_ObsCleanup.dll",                GUID_COMP62),
        ("dist/obs_cleanup_release/MastersFM_ObsCleanup.runtimeconfig.json", "MastersFM_ObsCleanup.runtimeconfig.json", GUID_COMP63),
        ("dist/obs_cleanup_release/MastersFM_ObsCleanup.deps.json",          "MastersFM_ObsCleanup.deps.json",          GUID_COMP64),
    ]
    CLEANUP_FILES = [(s, d, g) for s, d, g in _all_cleanup if os.path.exists(os.path.join(SRC, s))]

MSIDBOPEN_CREATE = ctypes.c_wchar_p(3)

msi_dll = ctypes.WinDLL("msi.dll")

def check(rc, msg):
    if rc != 0:
        raise RuntimeError(f"{msg} failed with code {rc}")

def open_db(path, mode):
    h = ctypes.c_uint(0)
    rc = msi_dll.MsiOpenDatabaseW(path, mode, ctypes.byref(h))
    check(rc, f"MsiOpenDatabase({path})")
    return h.value

def exec_sql(db, sql):
    hv = ctypes.c_uint(0)
    rc = msi_dll.MsiDatabaseOpenViewW(db, sql, ctypes.byref(hv))
    check(rc, f"MsiDatabaseOpenView: {sql[:80]}")
    rc = msi_dll.MsiViewExecute(hv.value, 0)
    check(rc, f"MsiViewExecute: {sql[:80]}")
    msi_dll.MsiCloseHandle(hv.value)

def insert_row(db, sql, *values):
    hv = ctypes.c_uint(0)
    rc = msi_dll.MsiDatabaseOpenViewW(db, sql, ctypes.byref(hv))
    check(rc, f"MsiDatabaseOpenView(insert): {sql[:80]}")
    n = len(values)
    hr = msi_dll.MsiCreateRecord(n)
    if not hr:
        raise RuntimeError("MsiCreateRecord failed")
    for i, v in enumerate(values, 1):
        if v is None:
            msi_dll.MsiRecordSetInteger(hr, i, ctypes.c_int(0x80000000))
        elif isinstance(v, int):
            msi_dll.MsiRecordSetInteger(hr, i, ctypes.c_int(v))
        else:
            msi_dll.MsiRecordSetStringW(hr, i, str(v))
    rc = msi_dll.MsiViewExecute(hv.value, hr)
    check(rc, f"MsiViewExecute(insert)")
    msi_dll.MsiCloseHandle(hr)
    msi_dll.MsiCloseHandle(hv.value)

def build_cab(files, cab_path):
    """Use makecab.exe (built into Windows) to create a CAB."""
    tmp = tempfile.mkdtemp()
    ddf = os.path.join(tmp, "files.ddf")
    cab_dir = os.path.dirname(cab_path)
    with open(ddf, "w") as f:
        f.write('.OPTION EXPLICIT\n')
        f.write('.Set CabinetNameTemplate=Data1.cab\n')
        f.write(f'.Set DiskDirectoryTemplate={cab_dir}\n')
        f.write('.Set Cabinet=ON\n')
        f.write('.Set Compress=ON\n')
        f.write('.Set MaxCabinetSize=0\n')
        f.write('.Set MaxDiskSize=0\n')
        f.write('.Set CompressionType=LZX\n')
        f.write('.Set CompressionLevel=7\n')
        for src_name, install_name, _ in files:
            src_path = os.path.join(SRC, src_name)
            # MSI locates files in the CAB by the File table key (spaces → _)
            file_key = install_name.replace(" ", "_")
            f.write(f'"{src_path}" "{file_key}"\n')
    result = subprocess.run(["makecab", "/F", ddf], capture_output=True, text=True)
    print(result.stdout[-500:] if result.stdout else "")
    if result.returncode != 0:
        print("makecab stderr:", result.stderr)
    shutil.rmtree(tmp, ignore_errors=True)
    expected = os.path.join(cab_dir, "Data1.cab")
    if os.path.exists(expected):
        if expected != cab_path:
            shutil.move(expected, cab_path)
        return True
    return False

def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    if os.path.exists(OUT):
        os.remove(OUT)

    cab_path = OUT.replace(".msi", ".cab")
    print("[1/3] Building CAB with makecab.exe...")
    if not build_cab(FILES + CLEANUP_FILES, cab_path):
        print("ERROR: makecab failed — cab not found")
        sys.exit(1)
    print(f"      CAB: {cab_path}")

    print("[2/3] Creating MSI database...")
    db = open_db(OUT, MSIDBOPEN_CREATE)

    # ── SummaryInformation stream (REQUIRED — msiexec rejects without it) ────
    VT_LPSTR = 30
    VT_I4    = 3
    hsi = ctypes.c_uint(0)
    rc = msi_dll.MsiGetSummaryInformationW(db, None, 20, ctypes.byref(hsi))
    check(rc, "MsiGetSummaryInformationW")
    def si_str(pid, val):
        rc = msi_dll.MsiSummaryInfoSetPropertyW(hsi.value, pid, VT_LPSTR, 0, None, val)
        check(rc, f"SummaryInfo str pid={pid}")
    def si_int(pid, val):
        rc = msi_dll.MsiSummaryInfoSetPropertyW(hsi.value, pid, VT_I4, val, None, None)
        check(rc, f"SummaryInfo int pid={pid}")
    si_str(2,  "Installation Database")           # PID_TITLE
    si_str(3,  "Master's FM")                     # PID_SUBJECT
    si_str(4,  "MasterShadex")                    # PID_AUTHOR
    si_str(7,  "Intel;1033")                      # PID_TEMPLATE  (platform;language)
    si_str(9,  "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}")  # PID_REVNUMBER (package code)
    si_int(14, 200)                               # PID_PAGECOUNT (MSI schema version)
    si_int(15, 2)                                 # PID_WORDCOUNT (2 = compressed)
    rc = msi_dll.MsiSummaryInfoPersist(hsi.value)
    check(rc, "MsiSummaryInfoPersist")
    msi_dll.MsiCloseHandle(hsi.value)

    # ── Tables ───────────────────────────────────────────────────────────────
    exec_sql(db, "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) NOT NULL PRIMARY KEY `Property`)")
    exec_sql(db, "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL PRIMARY KEY `Directory`)")
    exec_sql(db, "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)")
    exec_sql(db, "CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64), `Description` CHAR(255), `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)")
    exec_sql(db, "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`,`Component_`)")
    exec_sql(db, "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)")
    exec_sql(db, "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `DiskPrompt` CHAR(64), `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)")
    exec_sql(db, "CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL, `Component_` CHAR(72) NOT NULL, `Target` CHAR(72) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255), `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)")
    exec_sql(db, "CREATE TABLE `Icon` (`Name` CHAR(72) NOT NULL, `Data` OBJECT NOT NULL PRIMARY KEY `Name`)")
    exec_sql(db, "CREATE TABLE `RemoveFile` (`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255), `DirProperty` CHAR(72) NOT NULL, `InstallMode` SHORT NOT NULL PRIMARY KEY `FileKey`)")
    exec_sql(db, "CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)")
    exec_sql(db, "CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)")
    exec_sql(db, "CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(255) PRIMARY KEY `Action`)")
    exec_sql(db, "CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`)")

    # ── Properties ───────────────────────────────────────────────────────────
    # ProductCode is regenerated every build (uuid4) so each MSI is a NEW
    # product to Windows Installer. ProductVersion tracks the real app
    # version (parsed from tray.ps1). The legacy joke "67.0.0" caused
    # MSI to enter maintenance/repair mode on every reinstall — fine when
    # the cached source filename matched, but fatal (1603 SECUREREPAIR)
    # when the bundle's MSI was version-stamped. Major-Upgrade machinery
    # below sidesteps the whole class of failure.
    for k, v in [
        ("ProductName",    "Master's FM"),
        ("ProductCode",    PRODUCT_CODE),
        ("ProductVersion", APP_VERSION_MSI),
        ("Manufacturer",   "MasterShadex"),
        ("UpgradeCode",    GUID_UPGRADE),
        ("ARPNOREPAIR",    "1"),
        ("ARPNOMODIFY",    "1"),
    ]:
        insert_row(db, "INSERT INTO `Property` (`Property`,`Value`) VALUES (?,?)", k, v)

    # ── Upgrade table — Major Upgrade rule ───────────────────────────────────
    # "Find any product registered with our UpgradeCode (any version) and
    # store its ProductCode in MFM_UPGRADE_DETECTED so RemoveExistingProducts
    # can uninstall it before this MSI lays down its files."
    # Attributes = msidbUpgradeAttributesMigrateFeatures (0x01)
    #            | msidbUpgradeAttributesVersionMinInclusive (0x100)
    #            = 0x101 = 257
    # VersionMax = NULL means "no upper bound", so legacy installs that
    # registered with the meme ProductVersion 67.0.0 are still caught
    # despite this MSI's ProductVersion being the real (lower) app version.
    insert_row(db, "INSERT INTO `Upgrade` (`UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty`) VALUES (?,?,?,?,?,?,?)",
               GUID_UPGRADE, "0.0.0", None, None, 257, None, "MFM_UPGRADE_DETECTED")

    # ── Uninstall custom action ───────────────────────────────────────────────
    # Type 54 = VBScript (0x06) sourced from a Property (0x30).
    # 0x06 + 0x30 = 0x36 = 54. Source = property name holding the script text.
    # Type 22 (0x16 = VBScript from installed File) was wrong — MSI looked for
    # "MFM_UninstOBS" in the File table, found nothing, and threw error 2715.
    # The CA fires only during uninstall (condition REMOVE="ALL"), before file removal.
    # Write a temp PS1 to %TEMP%, run it, delete it.
    # This avoids all VBScript WMI For-Each loop and inline quote-escaping issues.
    # The PS1 kills server.exe and the tray.ps1 PowerShell process cleanly.
    _uninstall_vbs = (
        'On Error Resume Next : '
        'Set oShell = CreateObject("WScript.Shell") : '
        'Set fso   = CreateObject("Scripting.FileSystemObject") : '
        'instDir   = Session.Property("INSTALLFOLDER") : '
        'If Right(instDir,1)="\\" Then instDir=Left(instDir,Len(instDir)-1) : '

        # ── Step 1: write a temp PS1 that kills every Master's FM process ──
        # v2.0.0 added MastersFM_Tray.exe (in-process PowerShell host) so the
        # old kill list missed it, leaving the tray running while the MSI
        # tried to remove its files. Kill order: tray host → MastersFM launcher
        # → server → legacy powershell children → customize window.
        'tmpPs1 = oShell.ExpandEnvironmentStrings("%TEMP%\\mfm_kill.ps1") : '
        'Set f = fso.OpenTextFile(tmpPs1, 2, True) : '
        'f.WriteLine "Stop-Process -Name MastersFM_Tray -Force -ErrorAction SilentlyContinue" : '
        'f.WriteLine "Stop-Process -Name customize -Force -ErrorAction SilentlyContinue" : '
        'f.WriteLine "Stop-Process -Name audio_spectrum -Force -ErrorAction SilentlyContinue" : '
        'f.WriteLine "Stop-Process -Name MastersFM -Force -ErrorAction SilentlyContinue" : '
        'f.WriteLine "Stop-Process -Name server -Force -ErrorAction SilentlyContinue" : '
        'f.WriteLine "Get-CimInstance Win32_Process |'
        ' Where-Object { $_.CommandLine -like \'*tray.ps1*\' } |'
        ' ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" : '
        'f.Close : '

        # ── Step 2: run the PS1 ──────────────────────────────────────────────
        'oShell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden'
        ' -File """ & tmpPs1 & """", 0, True : '
        'fso.DeleteFile tmpPs1, True : '

        # ── Step 3: wait ~2s for processes to fully die ──────────────────────
        'oShell.Run "ping -n 3 127.0.0.1", 0, True : '

        # ── Step 3b: spawn MastersFM_ObsCleanup.exe detached (Stage 7.8C) ───
        # Removes Master's FM browser source from OBS scene-collection files.
        # Polls for OBS exit if OBS is running; runs immediately if not.
        # bWaitOnReturn=False: MSI uninstall continues without blocking on OBS.
        # Self-deletes after completing its cleanup pass.
        'cleanupExe = oShell.ExpandEnvironmentStrings'
        '("%ProgramData%\\MastersFM\\Cleanup\\MastersFM_ObsCleanup.exe") : '
        'If fso.FileExists(cleanupExe) Then '
            'oShell.Run """" & cleanupExe & """", 0, False : '
        'End If : '

        # ── Step 4: run tray.ps1 -Uninstall (removes OBS source from scenes) ─
        'oShell.Run "powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden'
        ' -File """ & instDir & "\\tray.ps1"""'
        ' & " -scriptDir """ & instDir & """ -Uninstall", 0, True : '

        # ── Step 5: delete desktop + startup shortcuts + legacy Run entry ────
        'desktop = oShell.SpecialFolders("Desktop") : '
        'sc = desktop & "\\Master\'s FM.lnk" : '
        'If fso.FileExists(sc) Then fso.DeleteFile sc, True : '
        'startupDir = oShell.SpecialFolders("Startup") : '
        'scStartup = startupDir & "\\Master\'s FM.lnk" : '
        'If fso.FileExists(scStartup) Then fso.DeleteFile scStartup, True : '
        'oShell.RegDelete "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\MastersFM" : Err.Clear : '
        'oShell.RegDelete "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run\\MastersFM" : Err.Clear : '

        # ── Step 6: back up config.json, then delete the install folder ────────
        # config.json holds the user's Last.fm username + all overlay settings.
        # Back it up to %APPDATA% (Roaming) before wiping LocalAppData so it
        # survives uninstall and is restored automatically on next install.
        'appData    = oShell.ExpandEnvironmentStrings("%LOCALAPPDATA%") : '
        'mfmDir     = appData & "\\MastersFM" : '
        'cfgFile    = mfmDir & "\\config.json" : '
        'roaming    = oShell.ExpandEnvironmentStrings("%APPDATA%") : '
        'cfgBackup  = roaming & "\\MastersFM_config_save.json" : '
        'If fso.FileExists(cfgFile) Then fso.CopyFile cfgFile, cfgBackup, True : '
        'If fso.FolderExists(mfmDir) Then fso.DeleteFolder mfmDir, True'
    )
    insert_row(db, "INSERT INTO `Property` (`Property`,`Value`) VALUES (?,?)",
               "MFM_UninstOBS", _uninstall_vbs)
    insert_row(db, "INSERT INTO `CustomAction` (`Action`,`Type`,`Source`,`Target`) VALUES (?,?,?,?)",
               "UninstallOBS", 54, "MFM_UninstOBS", None)

    # ── Auto-start migration custom action (runs at install time) ────────────
    # Every install — fresh or upgrade — runs this. It:
    #   (1) If HKCU\...\Run\MastersFM exists (legacy wscript+VBS from <1.7, or
    #       our brief Run\MastersFM=exe experiment), DELETES it entirely.
    #       Also deletes the StartupApproved cache entry. Windows startup
    #       execution + Task Manager then stop using that polluted path.
    #   (2) If the user previously had auto-start on (either via a legacy
    #       Run entry OR an existing Startup-folder .lnk), ensures the
    #       Startup-folder shortcut "Master's FM.lnk" exists and points at
    #       MastersFM.exe with MastersFM.ico as the icon.
    #
    # Shortcut instead of Run key because Task Manager's Startup Apps tab
    # reads .lnk files directly — no StartupApproved / WMI caches in the
    # path, so no stuck "wscript.exe" display. The .lnk also naturally
    # surfaces the app's name + icon from the shortcut's properties.
    _migrate_vbs = (
        'On Error Resume Next : '
        'Set oShell = CreateObject("WScript.Shell") : '
        'Set fso   = CreateObject("Scripting.FileSystemObject") : '
        'instDir  = Session.Property("INSTALLFOLDER") : '
        'If Right(instDir,1)="\\" Then instDir=Left(instDir,Len(instDir)-1) : '
        'exePath  = instDir & "\\MastersFM.exe" : '
        'icoPath  = instDir & "\\MastersFM.ico" : '
        'runKey   = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\MastersFM" : '
        'appKey   = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run\\MastersFM" : '
        'startupDir = oShell.SpecialFolders("Startup") : '
        'lnkPath  = startupDir & "\\Master\'s FM.lnk" : '
        ''

        # Detect whether the user had auto-start enabled (legacy Run entry,
        # or existing shortcut from a previous 1.7+ install).
        'hadAutoStart = False : '
        'cur = oShell.RegRead(runKey) : '
        'If Err.Number = 0 Then hadAutoStart = True : '
        'Err.Clear : '
        'If fso.FileExists(lnkPath) Then hadAutoStart = True : '
        ''

        # Wipe the Run entry + StartupApproved cache always — they are the
        # source of the wscript.exe display bug, and the .lnk replaces them.
        'oShell.RegDelete runKey : Err.Clear : '
        'oShell.RegDelete appKey : Err.Clear : '
        ''

        # Create / refresh the shortcut if the user wanted auto-start.
        'If hadAutoStart And fso.FileExists(exePath) Then '
            'Set sc = oShell.CreateShortcut(lnkPath) : '
            'sc.TargetPath = exePath : '
            'sc.WorkingDirectory = instDir : '
            'sc.WindowStyle = 7 : '
            'sc.Description = "Master\'s FM - Now Playing Overlay" : '
            'If fso.FileExists(icoPath) Then '
                'sc.IconLocation = icoPath & ",0" '
            'Else '
                'sc.IconLocation = exePath & ",0" '
            'End If : '
            'sc.Save : '
        'End If : '
        'Err.Clear'
    )
    insert_row(db, "INSERT INTO `Property` (`Property`,`Value`) VALUES (?,?)",
               "MFM_MigrateAutostart", _migrate_vbs)
    insert_row(db, "INSERT INTO `CustomAction` (`Action`,`Type`,`Source`,`Target`) VALUES (?,?,?,?)",
               "MigrateAutostart", 54, "MFM_MigrateAutostart", None)

    # ── URL ACL registration custom action ───────────────────────────────────
    # Master's FM Audio Spectrum binds an HttpListener to
    # http://127.0.0.1:4243/ (and http://localhost:4243/) so the tray's
    # Audio Source dialog can fetch the device list. On some Windows
    # configurations (corporate-managed images, locked-down accounts,
    # certain AV/EDR products) HttpListener.Start() throws "access
    # denied" because the URL prefix isn't registered in HTTP.sys's
    # URL ACL table. Registration requires admin rights — which the
    # MSI install path has anyway (UAC has already elevated us).
    # We register both prefixes for "Everyone" so any user account on
    # the machine can listen, with the SDDL lifted from MS docs.
    # This runs ONLY during install (NOT REMOVE) and silently swallows
    # all errors — netsh exits non-zero if the URL is already
    # registered, which is fine and shouldn't fail the install.
    _urlacl_vbs = (
        'On Error Resume Next : '
        'Set oShell = CreateObject("WScript.Shell") : '
        'oShell.Run "netsh http add urlacl url=http://127.0.0.1:4243/ user=Everyone", 0, True : '
        'oShell.Run "netsh http add urlacl url=http://localhost:4243/ user=Everyone", 0, True : '
        'Err.Clear'
    )
    insert_row(db, "INSERT INTO `Property` (`Property`,`Value`) VALUES (?,?)",
               "MFM_RegisterUrlAcl", _urlacl_vbs)
    insert_row(db, "INSERT INTO `CustomAction` (`Action`,`Type`,`Source`,`Target`) VALUES (?,?,?,?)",
               "RegisterUrlAcl", 54, "MFM_RegisterUrlAcl", None)

    # ── Launch app after install ─────────────────────────────────────────────
    # Fires after InstallFiles (4000) and CreateShortcuts (4500) so all
    # executables are on disk. Sequenced at 5001 so the app starts in the
    # background while MSI is still finishing RegisterProduct / InstallFinalize
    # — the user sees the tray icon appear before the installer window closes.
    #
    # Uses "explorer.exe <path>" as the intermediary (same trick INSTALL.bat
    # uses): explorer.exe runs as the current user, so MastersFM.exe inherits
    # the user token — NOT the MSI's elevated token.
    #
    # bWaitOnReturn = False — fire-and-forget; MSI does not wait for the app.
    # MastersFM.exe's own mutex (Global\MastersFM_Launcher) prevents double-
    # launch if the app was somehow already running.
    _launch_vbs = (
        'On Error Resume Next : '
        'Set oShell = CreateObject("WScript.Shell") : '
        'Set fso   = CreateObject("Scripting.FileSystemObject") : '
        'instDir   = Session.Property("INSTALLFOLDER") : '
        'If Right(instDir,1)="\\" Then instDir=Left(instDir,Len(instDir)-1) : '
        'exePath   = instDir & "\\MastersFM.exe" : '
        'If fso.FileExists(exePath) Then '
            'oShell.Run "explorer.exe """ & exePath & """", 0, False : '
        'End If : '
        'Err.Clear'
    )
    insert_row(db, "INSERT INTO `Property` (`Property`,`Value`) VALUES (?,?)",
               "MFM_LaunchApp", _launch_vbs)
    insert_row(db, "INSERT INTO `CustomAction` (`Action`,`Type`,`Source`,`Target`) VALUES (?,?,?,?)",
               "LaunchApp", 54, "MFM_LaunchApp", None)

    # ── Directories ──────────────────────────────────────────────────────────
    insert_row(db, "INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)",
               "TARGETDIR", None, "SourceDir")
    insert_row(db, "INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)",
               "LocalAppDataFolder", "TARGETDIR", ".")
    insert_row(db, "INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)",
               "INSTALLFOLDER", "LocalAppDataFolder", "MastersFM")
    insert_row(db, "INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)",
               "DesktopFolder", "TARGETDIR", ".")
    insert_row(db, "INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)",
               "ProgramMenuFolder", "TARGETDIR", ".")
    insert_row(db, "INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)",
               "ProgramMenuAppDir", "ProgramMenuFolder", "Master's FM")
    # Stage 7.8C STEP 5: %PROGRAMDATA%\MastersFM\Cleanup\ for OBS cleanup binary
    insert_row(db, "INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)",
               "CommonAppDataFolder", "TARGETDIR", ".")
    insert_row(db, "INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)",
               "MFMCommonDataDir", "CommonAppDataFolder", "MastersFM")
    insert_row(db, "INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)",
               "MFMCleanupDir", "MFMCommonDataDir", "Cleanup")

    # ── Feature ──────────────────────────────────────────────────────────────
    insert_row(db, "INSERT INTO `Feature` (`Feature`,`Feature_Parent`,`Title`,`Description`,`Display`,`Level`,`Directory_`,`Attributes`) VALUES (?,?,?,?,?,?,?,?)",
               "MainFeature", None, "Master's FM", "Now Playing OBS Overlay", 1, 1, "INSTALLFOLDER", 0)

    # ── Components + Files ───────────────────────────────────────────────────
    for i, (src_name, install_name, guid) in enumerate(FILES):
        src_path = os.path.join(SRC, src_name)
        size     = os.path.getsize(src_path)
        comp     = f"Comp_{i+1}"
        file_key = install_name.replace(" ", "_")  # spaces→_ only; dots are valid in File keys
        insert_row(db, "INSERT INTO `Component` (`Component`,`ComponentId`,`Directory_`,`Attributes`,`Condition`,`KeyPath`) VALUES (?,?,?,?,?,?)",
                   comp, guid, "INSTALLFOLDER", 0, None, file_key)
        insert_row(db, "INSERT INTO `File` (`File`,`Component_`,`FileName`,`FileSize`,`Version`,`Language`,`Attributes`,`Sequence`) VALUES (?,?,?,?,?,?,?,?)",
                   file_key, comp, install_name, size, None, None, 0, i+1)
        insert_row(db, "INSERT INTO `FeatureComponents` (`Feature_`,`Component_`) VALUES (?,?)",
                   "MainFeature", comp)

    # Stage 7.8C STEP 5: cleanup binary components (installed to MFMCleanupDir)
    _cleanup_base_seq = len(FILES) + 1
    for i, (src_name, install_name, guid) in enumerate(CLEANUP_FILES):
        src_path = os.path.join(SRC, src_name)
        size     = os.path.getsize(src_path)
        comp     = f"CleanupComp_{i+1}"
        file_key = install_name.replace(" ", "_")
        insert_row(db, "INSERT INTO `Component` (`Component`,`ComponentId`,`Directory_`,`Attributes`,`Condition`,`KeyPath`) VALUES (?,?,?,?,?,?)",
                   comp, guid, "MFMCleanupDir", 0, None, file_key)
        insert_row(db, "INSERT INTO `File` (`File`,`Component_`,`FileName`,`FileSize`,`Version`,`Language`,`Attributes`,`Sequence`) VALUES (?,?,?,?,?,?,?,?)",
                   file_key, comp, install_name, size, None, None, 0, _cleanup_base_seq + i)
        insert_row(db, "INSERT INTO `FeatureComponents` (`Feature_`,`Component_`) VALUES (?,?)",
                   "MainFeature", comp)

    # ── Registry — override DisplayVersion shown in Apps & Features ──────────
    # ProductVersion is the real app version (e.g., 6.9.2) — needed for the
    # MSI Major-Upgrade machinery. DisplayVersion writes a "v"-prefixed copy
    # so Apps & Features shows e.g. "v6.9.2" matching the in-app version
    # string. Path uses the per-build ProductCode (random uuid4) since each
    # MSI registers under its own Uninstall\{ProductCode} key.
    exec_sql(db, (
        "CREATE TABLE `Registry` ("
        "`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, "
        "`Key` CHAR(255) NOT NULL, `Name` CHAR(255), `Value` CHAR(255), "
        "`Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)"
    ))
    insert_row(db,
        "INSERT INTO `Registry` (`Registry`,`Root`,`Key`,`Name`,`Value`,`Component_`) VALUES (?,?,?,?,?,?)",
        "RegDisplayVersion",
        2,    # HKLM
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + PRODUCT_CODE,
        "DisplayVersion",
        APP_VERSION_DISPLAY,
        "Comp_1"   # server.exe component — always installed
    )

    # ── Icon table — embed MastersFM.ico so the shortcut uses it ─────────────
    ico_path = os.path.join(SRC, "assets", "MastersFM.ico")
    hv_ico = ctypes.c_uint(0)
    rc = msi_dll.MsiDatabaseOpenViewW(db, "INSERT INTO `Icon` (`Name`,`Data`) VALUES (?,?)", ctypes.byref(hv_ico))
    check(rc, "open Icon view")
    hr_ico = msi_dll.MsiCreateRecord(2)
    msi_dll.MsiRecordSetStringW(hr_ico, 1, "MastersFM.ico")
    msi_dll.MsiRecordSetStreamW(hr_ico, 2, ico_path)
    rc = msi_dll.MsiViewExecute(hv_ico.value, hr_ico)
    check(rc, "insert icon stream")
    msi_dll.MsiCloseHandle(hr_ico)
    msi_dll.MsiCloseHandle(hv_ico.value)

    # ── Desktop shortcut — attached to Comp_5 (MastersFM.exe, the C# launcher)
    insert_row(db,
        "INSERT INTO `Shortcut` (`Shortcut`,`Directory_`,`Name`,`Component_`,`Target`,`Arguments`,`Description`,`Hotkey`,`Icon_`,`IconIndex`,`ShowCmd`,`WkDir`) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
        "DesktopSC", "DesktopFolder", "Master's FM",
        "Comp_5",
        "[INSTALLFOLDER]MastersFM.exe",
        None,
        "Master's FM - Now Playing Overlay",
        None, "MastersFM.ico", 0, None, "INSTALLFOLDER"
    )

    # ── Start Menu shortcut — makes app findable via Windows Search ──────────
    insert_row(db,
        "INSERT INTO `Shortcut` (`Shortcut`,`Directory_`,`Name`,`Component_`,`Target`,`Arguments`,`Description`,`Hotkey`,`Icon_`,`IconIndex`,`ShowCmd`,`WkDir`) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
        "StartMenuSC", "ProgramMenuAppDir", "Master's FM",
        "Comp_5",
        "[INSTALLFOLDER]MastersFM.exe",
        None,
        "Master's FM - Now Playing Overlay",
        None, "MastersFM.ico", 0, None, "INSTALLFOLDER"
    )

    # ── RemoveFile — explicitly delete shortcuts and Start Menu folder on uninstall
    # InstallMode 2 = remove on uninstall only
    insert_row(db,
        "INSERT INTO `RemoveFile` (`FileKey`,`Component_`,`FileName`,`DirProperty`,`InstallMode`) VALUES (?,?,?,?,?)",
        "DesktopSC_rm", "Comp_5", "Master's FM.lnk", "DesktopFolder", 2)
    insert_row(db,
        "INSERT INTO `RemoveFile` (`FileKey`,`Component_`,`FileName`,`DirProperty`,`InstallMode`) VALUES (?,?,?,?,?)",
        "StartMenuSC_rm", "Comp_5", "Master's FM.lnk", "ProgramMenuAppDir", 2)
    insert_row(db,
        "INSERT INTO `RemoveFile` (`FileKey`,`Component_`,`FileName`,`DirProperty`,`InstallMode`) VALUES (?,?,?,?,?)",
        "StartMenuDir_rm", "Comp_5", None, "ProgramMenuAppDir", 2)

    # ── Media (CAB reference) ─────────────────────────────────────────────────
    insert_row(db, "INSERT INTO `Media` (`DiskId`,`LastSequence`,`DiskPrompt`,`Cabinet`,`VolumeLabel`,`Source`) VALUES (?,?,?,?,?,?)",
               1, len(FILES) + len(CLEANUP_FILES), None, "#Data1.cab", None, None)

    # ── Install sequences ─────────────────────────────────────────────────────
    # Major-Upgrade trio:
    #   FindRelatedProducts (200)   — scans installed products against the
    #                                 Upgrade table, populates MFM_UPGRADE_DETECTED.
    #   MigrateFeatureStates (1200) — carries feature-selection state forward
    #                                 from the prior install (we have one feature
    #                                 so this is largely formality, but is the
    #                                 documented partner of the Upgrade attribute
    #                                 0x01 = MigrateFeatures).
    #   RemoveExistingProducts (1401) — runs `msiexec /x` against every
    #                                   ProductCode in MFM_UPGRADE_DETECTED.
    #                                   Sequence 1401 = AFTER InstallValidate
    #                                   (1400), BEFORE InstallInitialize (1500),
    #                                   which is the "early" placement: the
    #                                   prior install is fully removed first,
    #                                   then this MSI installs cleanly. Avoids
    #                                   the file-locking and config-clash
    #                                   issues of the "late" placement (6601+).
    for action, seq in [
        ("FindRelatedProducts",   200),
        ("CostInitialize",        800),
        ("FileCost",              900),
        ("CostFinalize",         1000),
        ("MigrateFeatureStates", 1200),
        ("InstallValidate",      1400),
        ("RemoveExistingProducts", 1401),
        ("InstallInitialize",    1500),
        ("ProcessComponents",    1600),
        ("UnpublishFeatures",    1800),
        ("RemoveShortcuts",      3100),   # removes desktop shortcut on uninstall
        ("RemoveFiles",          3500),
        ("InstallFiles",         4000),
        ("CreateShortcuts",      4500),
        ("RegisterProduct",      6100),
        ("PublishFeatures",  6300),
        ("PublishProduct",   6400),
        ("InstallFinalize",  6600),
    ]:
        insert_row(db, "INSERT INTO `InstallExecuteSequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
                   action, None, seq)
    # UninstallOBS runs only during uninstall (before files are removed)
    insert_row(db, "INSERT INTO `InstallExecuteSequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
               "UninstallOBS", 'REMOVE="ALL"', 1450)
    # MigrateAutostart runs on INSTALL only (skipped on uninstall), after
    # CreateShortcuts (4500) so the install files exist. Sequence 5000 is
    # between shortcut creation and RegisterProduct.
    insert_row(db, "INSERT INTO `InstallExecuteSequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
               "MigrateAutostart", 'NOT REMOVE', 5000)
    # LaunchApp fires on INSTALL only (fresh install or upgrade), right after
    # MigrateAutostart (5000). All files are on disk at this point
    # (InstallFiles ran at 4000). The app starts in the background while MSI
    # is still running RegisterProduct / InstallFinalize, so the tray icon
    # appears before the installer window closes.
    insert_row(db, "INSERT INTO `InstallExecuteSequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
               "LaunchApp", 'NOT REMOVE', 5001)
    # RegisterUrlAcl runs on INSTALL only, after files are placed (sequence
    # > InstallFiles which is around 4000). Uninstall doesn't bother
    # removing the ACL — it's harmless leftover and might still be useful
    # if the user reinstalls.
    insert_row(db, "INSERT INTO `InstallExecuteSequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
               "RegisterUrlAcl", 'NOT REMOVE', 5005)
    for action, seq in [
        ("FindRelatedProducts", 200),
        ("CostInitialize",      800),
        ("FileCost",            900),
        ("CostFinalize",       1000),
        ("ExecuteAction",      1300),
    ]:
        insert_row(db, "INSERT INTO `InstallUISequence` (`Action`,`Condition`,`Sequence`) VALUES (?,?,?)",
                   action, None, seq)

    # ── Embed the CAB ─────────────────────────────────────────────────────────
    print("[3/3] Embedding CAB into MSI...")
    hv = ctypes.c_uint(0)
    rc = msi_dll.MsiDatabaseOpenViewW(db, "INSERT INTO `_Streams` (`Name`,`Data`) VALUES (?,?)", ctypes.byref(hv))
    check(rc, "open _Streams view")
    hr = msi_dll.MsiCreateRecord(2)
    msi_dll.MsiRecordSetStringW(hr, 1, "Data1.cab")
    msi_dll.MsiRecordSetStreamW(hr, 2, cab_path)
    rc = msi_dll.MsiViewExecute(hv.value, hr)
    check(rc, "insert CAB stream")
    msi_dll.MsiCloseHandle(hr)
    msi_dll.MsiCloseHandle(hv.value)

    rc = msi_dll.MsiDatabaseCommit(db)
    check(rc, "MsiDatabaseCommit")
    msi_dll.MsiCloseHandle(db)

    if os.path.exists(cab_path):
        os.remove(cab_path)

    print(f"\n✓ MSI created: {OUT}")
    print(f"  ProductVersion : {APP_VERSION_MSI}  (display: {APP_VERSION_DISPLAY})")
    print(f"  ProductCode    : {PRODUCT_CODE}")
    print(f"  UpgradeCode    : {GUID_UPGRADE}")
    print("  Major-Upgrade enabled — installs over any prior Master's FM cleanly.")

if __name__ == "__main__":
    main()
