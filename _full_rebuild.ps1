Set-Location "G:\Project Folder\Master FM"
$root  = "G:\Project Folder\Master FM"
$logDir = "$root\logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$ts  = Get-Date -Format 'yyyyMMdd_HHmmss'

# ============================================================================
# v14.0.0-rc.1 build flags (RC1 ship state)
# ----------------------------------------------------------------------------
# All five .NET-8-ready flags are $true. csc.exe rollback paths are preserved
# behind the $false branch of each flag: emergency revert path only, do not
# delete the csc.exe-side build steps. install_bootstrapper remains on csc.exe
# ($UseDotnet8Bootstrapper = $false) until a real CA cert + EmbeddedResource
# items land; see open_issues.md for re-enable steps.
# ============================================================================

# Stage 1 migration flag: $true = build MastersFM.exe via dotnet publish (net8.0-windows, R2R)
# RC1 rollback path: set $false to revert to the legacy csc.exe .NET Framework 4.x build.
$UseDotnet8Launcher = $true
# Stage 2 migration flag: $true = build audio_spectrum.exe via dotnet publish (net8.0-windows, R2R)
# RC1 rollback path: set $false to revert to legacy csc.exe via build_tools\ps2exe\_build_spectrum.ps1.
$UseDotnet8AudioSpectrum = $true
# Stage 3a migration flag: $true = build customize.exe via dotnet publish (net8.0-windows, R2R)
# RC1 rollback path: set $false to revert to the legacy csc.exe build.
$UseDotnet8Customize = $true
# Stage 3b migration flag: bootstrapper build remains DISABLED ($false) for RC1 ship.
# Bitdefender flags the self-signed bootstrapper pattern (embedded MSI + self-elevate + cert store mod).
# Re-enable requires (a) real CA cert (e.g. Certum), (b) EmbeddedResource items for payload.msi +
# publisher.cer, (c) updated _sign_msi.ps1. RC1 ships hybrid: .NET 8 runtime stack + csc.exe bootstrapper.
$UseDotnet8Bootstrapper = $false
# Stage 4 migration flag: $true = build server.exe via dotnet publish (net8.0, ASP.NET Core, R2R)
# RC1 default $true. Set $false to fall back to legacy Node.js + @yao-pkg/pkg path (RC1 rollback path).
$UseDotnet8Server = $true
# Stage 5 migration flag: $true = build tray_native.dll via dotnet build (netstandard2.0).
# netstandard2.0 keeps Windows PowerShell 5.1 (.NET Framework 4.x) compatibility AND stays loadable
# by future PS7 / .NET 8 hosts (Q1=C decision). RC1 rollback path: set $false for csc.exe build.
$UseDotnetTrayNative = $true
$log = "$logDir\rebuild_ps_$ts.log"

function L($msg) { $t = Get-Date -Format 'HH:mm:ss'; "$t  $msg" | Tee-Object -FilePath $log -Append | Write-Host }

L "=== REBUILD START ==="

# Step 1: server.exe -- either .NET ASP.NET Core (Stage 4) or Node.js pkg (legacy)
if ($UseDotnet8Server) {
    L "[1/5] Building server.exe via dotnet publish (net8.0, ASP.NET Core, R2R)..."
    $svOut = Join-Path $root 'dist\server_dotnet_release'
    $svArgs = "publish `"$root\src\server_dotnet\server_dotnet.csproj`" -r win-x64 --self-contained false " +
              "-p:PublishReadyToRun=true -c Release -o `"$svOut`" --nologo -v quiet"
    $sv = Start-Process -FilePath 'dotnet' -ArgumentList $svArgs -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($sv.ExitCode -eq 0) {
        # Copy server artifacts to root -- MSI picks them up from there.
        # server.pdb excluded (debug symbols, not shipped).
        # Stage 4.10 (RC1 fix): DiscordRPC.dll + Newtonsoft.Json.dll added to copy list.
        # Without them, DiscordRpcService.TryInitClient throws FileNotFoundException at runtime
        # and the BackgroundService failure brings down the whole host (StopHost default).
        foreach ($ff in @('server.exe','server.dll','server.deps.json','server.runtimeconfig.json',
                          'server.staticwebassets.endpoints.json','web.config',
                          'DiscordRPC.dll','Newtonsoft.Json.dll')) {
            $fsrc = Join-Path $svOut $ff
            if (Test-Path $fsrc) { Copy-Item $fsrc "$root\$ff" -Force }
        }
        L "  server.exe OK (net8.0, ASP.NET Core, R2R)"
        # Sign server.exe and server.dll -- unsigned .NET PE files can trigger Defender on first scan
        $signScript = Join-Path $root 'build_tools\signing\_sign_msi.ps1'
        if (Test-Path $signScript) {
            foreach ($sf in @('server.exe','server.dll')) {
                $sfPath = Join-Path $root $sf
                try {
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $sfPath 2>&1 | ForEach-Object { L "    $_" }
                } catch { L "  WARN: signing $sf failed: $_" }
            }
        }
    } else {
        L "  FAIL: dotnet publish server exit=$($sv.ExitCode) -- Stage 4 server build failed, aborting"
        exit 11
    }
} else {
    L "[1/5] pkg build (legacy Node.js server)"
    Push-Location $root
    & npx @yao-pkg/pkg src\server.js --targets node18-win-x64 --output dist\server.exe
    $pkgExit = $LASTEXITCODE
    Pop-Location
    if ($pkgExit -ne 0) { L "FAIL: pkg exit=$pkgExit"; exit 11 }
    Copy-Item "$root\dist\server.exe" "$root\server.exe" -Force
    L "  server.exe OK (legacy Node.js pkg)"
}

# Step 1b: Build C# binaries
L "[1b/5] Building C# binaries..."
$csc = $null
$paths = @(
    (Join-Path $env:windir "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:windir "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
foreach ($p in $paths) { if (Test-Path $p) { $csc = $p; break } }

# MastersFM.exe (launcher) â€” Stage 1: dotnet publish (net8.0-windows, R2R) or legacy csc.exe
if ($UseDotnet8Launcher) {
    L "  [Stage 1] Building MastersFM.exe via dotnet publish (net8.0-windows, R2R)..."
    $pubOut = Join-Path $root 'dist\launcher_net8'
    $dpArgs = "publish `"$root\src\launcher.csproj`" -r win-x64 --self-contained false " +
              "-p:PublishReadyToRun=true -c Release -o `"$pubOut`" --nologo -v quiet"
    $dp = Start-Process -FilePath 'dotnet' -ArgumentList $dpArgs -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($dp.ExitCode -eq 0) {
        # Copy all launcher artifacts to root â€” MSI picks them up from there.
        # MastersFM.pdb is intentionally excluded (debug symbols, not shipped).
        foreach ($lf in @('MastersFM.exe','MastersFM.dll','MastersFM.deps.json','MastersFM.runtimeconfig.json')) {
            $lsrc = Join-Path $pubOut $lf
            if (Test-Path $lsrc) { Copy-Item $lsrc "$root\$lf" -Force }
        }
        L "  MastersFM.exe OK (net8.0-windows, R2R)"
    } else {
        L "  FAIL: dotnet publish exit=$($dp.ExitCode) -- Stage 1 launcher build failed, aborting"
        exit 11
    }
} elseif ($csc) {
    # Legacy csc.exe rollback â€” set $UseDotnet8Launcher = $false at top of script to activate
    $argsL = "/nologo /target:winexe /win32icon:assets\MastersFM.ico /out:MastersFM.exe " +
             "/reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\launcher.cs"
    $c = Start-Process -FilePath $csc -ArgumentList $argsL -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($c.ExitCode -eq 0) { L "  MastersFM.exe OK (legacy csc.exe)" } else { L "  WARN: csc exit=$($c.ExitCode) - MastersFM.exe may be stale" }
} else {
    L "  WARN: dotnet not available and csc.exe not found -- MastersFM.exe NOT built"
}

# customize.exe -- Stage 3a: dotnet publish (net8.0-windows, R2R) or legacy csc.exe rollback.
L "[1c/5] Building customize.exe (WebView2 host)..."
if ($UseDotnet8Customize) {
    $czOut = Join-Path $root 'dist\customize_net8'
    $czArgs = "publish `"$root\src\customize.csproj`" -r win-x64 --self-contained false " +
              "-p:PublishReadyToRun=true -c Release -o `"$czOut`" --nologo -v quiet"
    $cz = Start-Process -FilePath 'dotnet' -ArgumentList $czArgs -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($cz.ExitCode -eq 0) {
        # Copy customize artifacts to root -- MSI picks them up from there.
        # customize.pdb excluded (debug symbols, not shipped).
        # Microsoft.Web.WebView2.Wpf.dll excluded (WPF bindings, not needed for WinForms host).
        foreach ($cf in @('customize.exe','customize.dll','customize.deps.json','customize.runtimeconfig.json',
                          'Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll')) {
            $csrc = Join-Path $czOut $cf
            if (Test-Path $csrc) { Copy-Item $csrc "$root\$cf" -Force }
        }
        L "  customize.exe OK (net8.0-windows, R2R)"
        # Sign both the stub exe and the managed dll -- unsigned new PE files can
        # be flagged by Defender on first scan (same as tray_native.dll + audio_spectrum.dll pattern).
        $signScript = Join-Path $root 'build_tools\signing\_sign_msi.ps1'
        if (Test-Path $signScript) {
            foreach ($sf in @('customize.exe','customize.dll')) {
                $sfPath = Join-Path $root $sf
                try {
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $sfPath 2>&1 | ForEach-Object { L "    $_" }
                } catch { L "  WARN: signing $sf failed: $_" }
            }
        }
    } else {
        L "  FAIL: dotnet publish customize exit=$($cz.ExitCode) -- Stage 3a build failed, aborting"
        exit 11
    }
} else {
    # Legacy csc.exe rollback -- set $UseDotnet8Customize = $false at top of script to activate.
    if ($csc) {
        $wv2core = Join-Path $root 'Microsoft.Web.WebView2.Core.dll'
        $wv2wf   = Join-Path $root 'Microsoft.Web.WebView2.WinForms.dll'
        if ((Test-Path $wv2core) -and (Test-Path $wv2wf)) {
            $argsC = '/nologo /target:winexe /win32icon:assets\MastersFM.ico /out:customize.exe ' +
                     '/reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll ' +
                     "/reference:`"$wv2core`" /reference:`"$wv2wf`" src\customize.cs"
            $c2 = Start-Process -FilePath $csc -ArgumentList $argsC -WorkingDirectory $root -Wait -PassThru -NoNewWindow
            if ($c2.ExitCode -eq 0) { L "  customize.exe OK (legacy csc.exe)" } else { L "  WARN: customize.exe csc exit=$($c2.ExitCode)" }
        } else {
            L "  WARN: WebView2 DLLs missing - customize.exe NOT built (legacy path)"
        }
    } else {
        L "  WARN: csc.exe not found - customize.exe NOT built (legacy path requires csc.exe)"
    }
}

if ($csc) {
    # MastersFM_Tray.exe â€” C# exe that hosts PowerShell IN-PROCESS (via
    # System.Management.Automation). Replaces the separate powershell.exe
    # child â€” now there's no "Windows PowerShell" entry cluttering Task
    # Manager; tray.ps1 runs inside an exe whose VersionInfo reports
    # ProductName "Master's FM", so Task Manager groups it alongside
    # MastersFM.exe + server.exe under the same app row.
    L "[1d/5] Compiling MastersFM_Tray.exe (PowerShell host) via csc.exe..."
    $sma = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll'
    if (Test-Path $sma) {
        $argsT = '/nologo /target:winexe /win32icon:assets\MastersFM.ico /out:MastersFM_Tray.exe ' +
                 '/reference:System.dll /reference:System.Core.dll ' +
                 "/reference:$sma src\tray_launcher.cs"
        $c3 = Start-Process -FilePath $csc -ArgumentList $argsT -WorkingDirectory $root -Wait -PassThru -NoNewWindow
        if ($c3.ExitCode -eq 0) { L "  MastersFM_Tray.exe OK" } else { L "  WARN: MastersFM_Tray.exe csc exit=$($c3.ExitCode)" }
    } else {
        L "  WARN: System.Management.Automation.dll not found - MastersFM_Tray.exe NOT built"
    }

    # tray_native.dll csc.exe rollback path (active when $UseDotnetTrayNative=$false).
    # Sub-stage 5.1: the dotnet build path below replaces this when flag is $true.
    if (-not $UseDotnetTrayNative) {
        # Pre-compiled P/Invoke + COM types for tray.ps1.
        # Source moved to src\tray_native\tray_native.cs in sub-stage 5.1.
        L "[1d3/5] Compiling tray_native.dll (csc.exe, .NET Framework 4.x rollback)..."
        $argsN = "/nologo /target:library /out:tray_native.dll /reference:System.dll /reference:System.Core.dll src\tray_native\tray_native.cs"
        $c4 = Start-Process -FilePath $csc -ArgumentList $argsN -WorkingDirectory $root -Wait -PassThru -NoNewWindow
        if ($c4.ExitCode -eq 0) {
            L "  tray_native.dll OK (csc.exe rollback)"
            $signScript = Join-Path $root 'build_tools\signing\_sign_msi.ps1'
            if (Test-Path $signScript) {
                $dllPath = Join-Path $root 'tray_native.dll'
                try {
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $dllPath 2>&1 | ForEach-Object { L "    $_" }
                } catch { L "  WARN: DLL signing failed: $_" }
            }
        } else { L "  WARN: tray_native.dll csc exit=$($c4.ExitCode)" }
    }
} else {
    L "  WARN: csc.exe not found -- MastersFM_Tray.exe NOT built"
    if (-not $UseDotnetTrayNative) { L "  WARN: tray_native.dll also NOT built (set UseDotnetTrayNative=true to use dotnet path)" }
}

# tray_native.dll dotnet build path (sub-stage 5.1, active when $UseDotnetTrayNative=$true).
# Builds src\tray_native\tray_native.csproj targeting netstandard2.0 for PS5.1 compatibility.
# Does not require csc.exe; runs independently of the if ($csc) block above.
if ($UseDotnetTrayNative) {
    L "[1d3/5] Building tray_native.dll (dotnet build, netstandard2.0)..."
    Push-Location "$root\src\tray_native"
    try {
        $buildResult = & dotnet build "tray_native.csproj" -c Release --nologo -o "$root" 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            $buildResult | ForEach-Object { L "    $_" }
            L "  ERROR: tray_native dotnet build failed (exit $exitCode)"
        } else {
            L "  tray_native.dll OK (dotnet build, netstandard2.0)"
            $signScript = Join-Path $root 'build_tools\signing\_sign_msi.ps1'
            if (Test-Path $signScript) {
                $dllPath = Join-Path $root 'tray_native.dll'
                try {
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $dllPath 2>&1 | ForEach-Object { L "    $_" }
                } catch { L "  WARN: DLL signing failed: $_" }
            }
        }
    } finally {
        Pop-Location
    }
}

# Step 1d2: Build audio_spectrum.exe (WASAPI/MME/WDM-KS/ASIO spectrum provider)
# Stage 2: dotnet publish path replaces the old csc.exe + netstandard.dll facade approach.
# Legacy fallback retained behind $UseDotnet8AudioSpectrum = $false for rollback.
L "[1d2/5] Building audio_spectrum.exe (WASAPI loopback)..."
if ($UseDotnet8AudioSpectrum) {
    $asOut = Join-Path $root 'dist\audio_spectrum_net8'
    $asArgs = "publish `"$root\src\audio_spectrum.csproj`" -r win-x64 --self-contained false " +
              "-p:PublishReadyToRun=true -c Release -o `"$asOut`" --nologo -v quiet"
    $as = Start-Process -FilePath 'dotnet' -ArgumentList $asArgs -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($as.ExitCode -eq 0) {
        # Copy all audio_spectrum artifacts to root -- MSI picks them up from there.
        # audio_spectrum.pdb intentionally excluded (debug symbols, not shipped).
        foreach ($af in @('audio_spectrum.exe','audio_spectrum.dll','audio_spectrum.deps.json','audio_spectrum.runtimeconfig.json','NAudio.Core.dll','NAudio.Wasapi.dll','NAudio.WinMM.dll','NAudio.Asio.dll')) {
            $asrc = Join-Path $asOut $af
            if (Test-Path $asrc) { Copy-Item $asrc "$root\$af" -Force }
        }
        L "  audio_spectrum.exe OK (net8.0-windows, R2R)"
        # Sign both the stub exe and the managed dll -- unsigned new PE files can
        # be flagged by Defender on first scan (same as tray_native.dll pattern).
        $signScript = Join-Path $root 'build_tools\signing\_sign_msi.ps1'
        if (Test-Path $signScript) {
            foreach ($sf in @('audio_spectrum.exe','audio_spectrum.dll')) {
                $sfPath = Join-Path $root $sf
                try {
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $sfPath 2>&1 | ForEach-Object { L "    $_" }
                } catch { L "  WARN: signing $sf failed: $_" }
            }
        }
    } else {
        L "  FAIL: dotnet publish audio_spectrum exit=$($as.ExitCode) -- Stage 2 build failed, aborting"
        exit 11
    }
} else {
    # Legacy csc.exe rollback -- set $UseDotnet8AudioSpectrum = $false at top of script to activate.
    $spec = Join-Path $root 'build_tools\ps2exe\_build_spectrum.ps1'
    if (Test-Path $spec) {
        $sp = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$spec`"",'-Root',"`"$root`"") `
            -Wait -PassThru -NoNewWindow
        if ($sp.ExitCode -eq 0) { L "  audio_spectrum.exe OK (legacy csc.exe)" } else { L "  WARN: audio_spectrum.exe build exit=$($sp.ExitCode)" }
    } else {
        L "  WARN: _build_spectrum.ps1 not found - audio_spectrum.exe NOT built"
    }
}

# Step 1e: rebrand server.exe via resedit (legacy Node server only -- .NET server has its own PE info)
if (-not $UseDotnet8Server) {
# Step 1e: rebrand server.exe via resedit (pure-JS PE editor that is pkg-
# compatible). rcedit CORRUPTS pkg output because its BeginUpdateResource
# shifts the overlay offset (pkg stores its Node VFS at the overlay). The
# jet2jet/resedit-js-cli package is pure JavaScript, preserves overlays,
# and has been tested against pkg binaries.
# Vendored at build_tools/resedit so builds don't need npm network access.
L "[1e/5] Rebranding server.exe VersionInfo via resedit..."
$resedit = Join-Path $root 'build_tools\resedit\node_modules\.bin\resedit.cmd'
if (Test-Path $resedit) {
    $srv = Join-Path $root 'server.exe'
    $srvTmp = Join-Path $env:TEMP 'mastersfm_server_rebrand.exe'
    if (Test-Path $srv) {
        # v7.0.0 â€” also replace the Node green-box icon with MastersFM.ico so
        # server.exe displays the purple Master's FM note in Task Manager /
        # Details tab / File Properties. The previous rebrand only touched
        # VersionInfo, leaving pkg's default Node-shaped icon intact.  The
        # --delete-allicon pair strips every existing icon/groupicon resource
        # so our --icon lands cleanly without being shadowed.
        $ico = Join-Path $root 'assets\MastersFM.ico'
        # resedit flag gotchas discovered during v5 debugging:
        #  - `--delete-allicon` (one token, dash-separated) is NOT a thing
        #    resedit actually ships; it was picked up as `--delete-allicon=<next-arg>`
        #    by yargs and silently consumed --delete-allgroupicon. Result:
        #    only 1 resource deleted, then the "new" icon was added alongside
        #    the old one and Windows kept showing pkg's green Node icon.
        #  - The correct form is two separate `--delete` flags, each taking
        #    `allicon` / `allgroupicon` as its VALUE. Verified via --verbose
        #    output â€” "Delete resources. (count = 2)" once fixed.
        #  - `--icon` REQUIRES `<ID>,<path>` format when you want to place
        #    the icon at a specific resource ID. ID 1 is what Windows looks
        #    up for file associations / Task Manager display.
        $reArgs = @(
            '--in', "`"$srv`"",
            '--out', "`"$srvTmp`"",
            '--product-name', '"Master''s FM"',
            '--file-description', '"Master''s FM Server"',
            '--company-name', 'MasterShadex',
            '--product-version', '5.0.0.0',
            '--file-version', '5.0.0.0',
            '--delete', 'allicon',
            '--delete', 'allgroupicon',
            '--icon', "`"1,$ico`""
        ) -join ' '
        $r = Start-Process -FilePath $resedit -ArgumentList $reArgs -Wait -PassThru -NoNewWindow
        if ($r.ExitCode -eq 0 -and (Test-Path $srvTmp)) {
            # Swap in the rebranded binary. Keep the size check as a sanity net â€”
            # pkg overlay losses show up as dramatic file-size drops.
            $origSize = (Get-Item $srv).Length
            $newSize  = (Get-Item $srvTmp).Length
            $delta    = [Math]::Abs($origSize - $newSize)
            if ($delta -lt 200000) {
                Copy-Item $srvTmp $srv -Force
                Remove-Item $srvTmp -Force -ErrorAction SilentlyContinue
                L "  server.exe rebranded OK (size delta=$delta bytes)"
            } else {
                L "  WARN: resedit output size delta=$delta bytes - likely overlay corruption, keeping original"
                Remove-Item $srvTmp -Force -ErrorAction SilentlyContinue
            }
        } else {
            L "  WARN: resedit exit=$($r.ExitCode)"
        }
    }
} else {
    L "  WARN: resedit not found - skipping rebrand"
}
} # end if (-not $UseDotnet8Server) -- resedit block skipped for .NET server

# Step 2: build_msi.py
L "[2/5] build_msi.py"
$env:PYTHONIOENCODING = 'utf-8'
Push-Location $root
& python build_tools\build_msi.py
$pyExit = $LASTEXITCODE
Pop-Location
if ($pyExit -ne 0) { L "FAIL: MSI build exit=$pyExit"; exit 12 }
$msi = "$root\Master's FM Install\MastersFM_Setup.msi"
L "  MSI OK: $msi"

# Sign the MSI with a persistent self-signed cert (MasterShadex). Not a
# full fix for Smart App Control (needs a real CA cert for that), but
# promotes the UAC prompt from "Unknown Publisher" to "MasterShadex" and
# gives SmartScreen reputation something to anchor to.
L "[2b/5] Signing MSI with MasterShadex self-signed cert..."
$signScript = Join-Path $root 'build_tools\signing\_sign_msi.ps1'
if (Test-Path $signScript) {
    try {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $msi 2>&1 | ForEach-Object { L "    $_" }
    } catch {
        L "  WARN: MSI signing failed: $_"
    }
} else {
    L "  WARN: $signScript not found - MSI stays unsigned"
}

# Step 2c: Generate version.json for auto-updater (v10.0.0)
# SHA-256 of the signed MSI is embedded so the tray can verify downloads.
# autoInstall is set to false â€” flip it to true in _push_update.ps1 after
# uploading the MSI to GitHub Releases to enable silent auto-install for friends.
L "[2c/5] Generating version.json..."
try {
    $trayTxt  = Get-Content "$root\src\tray.ps1" -Raw
    $appVer   = '10.0.0'
    if ($trayTxt -match '\$script:APP_VERSION\s*=\s*"v([^"]+)"') { $appVer = $matches[1] }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $msiBytes = [System.IO.File]::ReadAllBytes($msi)
    $hashHex  = ([BitConverter]::ToString($sha256.ComputeHash($msiBytes)) -replace '-','').ToLower()
    $sha256.Dispose()

    $msiName = "Masters-FM-V$appVer.msi"
    $msiUrl  = "https://github.com/MasterShadex/Masters-FM/releases/download/v$appVer/$msiName"

    $versionJson = @{
        version     = $appVer
        msi_url     = $msiUrl
        msi_sha256  = $hashHex
        autoInstall = $false
    } | ConvertTo-Json -Compress

    $versionJsonPath = Join-Path $root 'version.json'
    [System.IO.File]::WriteAllText($versionJsonPath, $versionJson, [System.Text.Encoding]::UTF8)
    L "  version.json written: version=$appVer sha256=$hashHex"
    L "  NEXT: upload '$msiName' to GitHub Releases tag v$appVer, then run _push_update.ps1 to publish"
} catch {
    L "  WARN: version.json generation failed: $_"
}

# Step 3: Stop running processes
L "[3/5] Stopping tray + server"
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*tray.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Stop-Process -Name 'server'          -Force -ErrorAction SilentlyContinue
Stop-Process -Name 'wscript'         -Force -ErrorAction SilentlyContinue
Stop-Process -Name 'MastersFM_Tray'  -Force -ErrorAction SilentlyContinue
Stop-Process -Name 'audio_spectrum'  -Force -ErrorAction SilentlyContinue
Stop-Process -Name 'customize'       -Force -ErrorAction SilentlyContinue
Stop-Process -Name 'MastersFM'       -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1500
L "  processes stopped"

# Step 4: Uninstall existing
L "[4/5] Uninstalling existing Master's FM (if any)"
$app = Get-CimInstance Win32_Product -ErrorAction SilentlyContinue |
       Where-Object { $_.Name -like '*Master*FM*' } | Select-Object -First 1
if ($app) {
    L "  Found ProductCode: $($app.IdentifyingNumber)"
    $msiunlog = "$logDir\msi_uninstall_$ts.log"
    $u = Start-Process msiexec.exe -ArgumentList "/x $($app.IdentifyingNumber) /qn /norestart /l*v `"$msiunlog`"" `
        -Wait -PassThru -WindowStyle Hidden
    L "  uninstall exit=$($u.ExitCode)"
} else {
    L "  none found"
}
Start-Sleep -Milliseconds 1000

# Step 5: Install fresh MSI
L "[5/5] Installing MSI"
$msilog = "$logDir\msi_install_$ts.log"
$inst = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /norestart /l*v `"$msilog`"" `
    -Wait -PassThru -WindowStyle Hidden
if ($inst.ExitCode -ne 0) { L "FAIL: msiexec exit=$($inst.ExitCode)"; exit 15 }
L "  Installed OK. log=$msilog"

# Launch â€” always via the C# launcher (MastersFM.exe). The old VBS wrapper
# is no longer shipped by the installer, so no fallback path is needed.
$dest = "$env:LOCALAPPDATA\MastersFM"
if (Test-Path "$dest\MastersFM.exe") {
    L "Launching $dest\MastersFM.exe"
    Start-Process "$dest\MastersFM.exe"
    L "  launched"
} else {
    L "WARN: MastersFM.exe not found at $dest"
}

# v5.1.8 â€” single folder on the Desktop holding EVERYTHING friends need,
# nothing else. No standalone MSI, no standalone cer, no bootstrapper exe
# cluttering the Desktop. The folder is self-contained + zip-ready.
try {
    $trayTxt = Get-Content "$root\src\tray.ps1" -Raw
    $appVer  = 'v2.0.0'
    if ($trayTxt -match '\$script:APP_VERSION\s*=\s*"([^"]+)"') { $appVer = $matches[1] }

    # Purge ALL older Master's FM clutter from the Desktop â€” standalone
    # MSIs, old bootstrappers, separate .cer files â€” so only the single
    # MastersFM_Installer folder remains.
    Get-ChildItem "$env:USERPROFILE\Desktop" -Filter "Master's FM V*.msi" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue; L "  Purged from Desktop: $($_.Name)" }
    Get-ChildItem "$env:USERPROFILE\Desktop" -Filter "Install Master's FM *.exe" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue; L "  Purged from Desktop: $($_.Name)" }
    $strayCer = Join-Path $env:USERPROFILE 'Desktop\MastersFM_publisher.cer'
    if (Test-Path $strayCer) { Remove-Item $strayCer -Force -ErrorAction SilentlyContinue; L "  Purged from Desktop: MastersFM_publisher.cer" }
    $strayTxt = Join-Path $env:USERPROFILE 'Desktop\INSTALL_INSTRUCTIONS.txt'
    if (Test-Path $strayTxt) { Remove-Item $strayTxt -Force -ErrorAction SilentlyContinue; L "  Purged from Desktop: INSTALL_INSTRUCTIONS.txt" }

    # Stage 3b: install_bootstrapper.exe build DISABLED ($UseDotnet8Bootstrapper = $false at top).
    # Reason: self-signed cert trips Bitdefender behavioural heuristics (embedded MSI + self-elevate
    # + cert store mod). MSI + .cer + INSTALL.bat is the AV-safe distribution path.
    # To activate when a real CA cert is obtained (Certum, ~EUR 25/yr):
    #   1. Set $UseDotnet8Bootstrapper = $true at top of this script
    #   2. Add EmbeddedResource items to install_bootstrapper.csproj (payload.msi + publisher.cer)
    #      OR use a wrapper build that passes them after the MSI is built
    #   3. Update _sign_msi.ps1 to point at the new cert thumbprint
    # The .NET 8 csproj (src/install_bootstrapper.csproj) is ready to activate.
    # ---
    # if ($UseDotnet8Bootstrapper) {
    #     L "[3b/5] Building install_bootstrapper.exe (net8.0-windows, R2R) ..."
    #     $bsOut = Join-Path $root 'dist\bootstrapper_net8'
    #     $bsArgs = "publish `"$root\src\install_bootstrapper.csproj`" -r win-x64 --self-contained false " +
    #               "-p:PublishReadyToRun=true -c Release -o `"$bsOut`" --nologo -v quiet"
    #     $bs = Start-Process -FilePath 'dotnet' -ArgumentList $bsArgs -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    #     if ($bs.ExitCode -eq 0) {
    #         foreach ($bf in @('install_bootstrapper.exe','install_bootstrapper.dll')) {
    #             $bsrc = Join-Path $bsOut $bf
    #             if (Test-Path $bsrc) { Copy-Item $bsrc "$root\$bf" -Force }
    #         }
    #         L "  install_bootstrapper.exe OK (net8.0-windows, R2R)"
    #         # Sign both the stub exe and the managed dll.
    #         $signScript = Join-Path $root 'build_tools\signing\_sign_msi.ps1'
    #         if (Test-Path $signScript) {
    #             foreach ($sf in @('install_bootstrapper.exe','install_bootstrapper.dll')) {
    #                 $sfPath = Join-Path $root $sf
    #                 try { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $sfPath 2>&1 | ForEach-Object { L "    $_" } }
    #                 catch { L "  WARN: signing $sf failed: $_" }
    #             }
    #         }
    #     } else {
    #         L "  WARN: dotnet publish install_bootstrapper exit=$($bs.ExitCode) -- bootstrapper not built"
    #     }
    # }

    # Single folder on Desktop with EVERYTHING â€” send this to friends (zip it first).
    # v5.2.4: exactly THREE files inside â€” MSI + INSTALL.bat + publisher .cer.
    # The standalone INSTALL_INSTRUCTIONS.txt was retired; INSTALL.bat is
    # self-explanatory (one double-click â†’ one UAC prompt â†’ done) and friends
    # kept ignoring the readme anyway. Any stale TXT from previous builds is
    # purged below so the bundle doesn't grow extra files across rebuilds.
    $bundleDir = Join-Path $env:USERPROFILE 'Desktop\MastersFM_Installer'
    if (-not (Test-Path $bundleDir)) { New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null }
    # Clear old contents so version-renamed MSIs don't accumulate inside, and
    # purge the old INSTALL_INSTRUCTIONS.txt if it's still hanging around from
    # a pre-v5.2.4 build.
    Get-ChildItem $bundleDir -Filter "Master's FM V*.msi" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    $staleReadme = Join-Path $bundleDir 'INSTALL_INSTRUCTIONS.txt'
    if (Test-Path $staleReadme) { Remove-Item $staleReadme -Force -ErrorAction SilentlyContinue; L "  Bundle: purged stale INSTALL_INSTRUCTIONS.txt" }

    $msiVersioned = "Master's FM $($appVer.ToUpper()).msi"
    Copy-Item $msi (Join-Path $bundleDir $msiVersioned) -Force
    L "  Bundle: $msiVersioned"
    $batSrc = Join-Path $root "Master's FM Install\INSTALL.bat"
    if (Test-Path $batSrc) { Copy-Item $batSrc (Join-Path $bundleDir 'INSTALL.bat') -Force; L "  Bundle: INSTALL.bat" }
    $cerSrc = Join-Path $root 'build_tools\signing\MastersFM_publisher.cer'
    if (Test-Path $cerSrc) { Copy-Item $cerSrc (Join-Path $bundleDir 'MastersFM_publisher.cer') -Force; L "  Bundle: MastersFM_publisher.cer" }

    L "  SEND-TO-FRIENDS: zip '$bundleDir' and share it (3 files inside)"
} catch { L "  WARN: Desktop bundle copy failed - $_" }

L "=== REBUILD DONE OK ==="
