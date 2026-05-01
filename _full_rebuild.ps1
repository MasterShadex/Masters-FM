Set-Location "G:\Project Folder\Master FM"
$root  = "G:\Project Folder\Master FM"
$logDir = "$root\logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$ts  = Get-Date -Format 'yyyyMMdd_HHmmss'
$log = "$logDir\rebuild_ps_$ts.log"

function L($msg) { $t = Get-Date -Format 'HH:mm:ss'; "$t  $msg" | Tee-Object -FilePath $log -Append | Write-Host }

L "=== REBUILD START ==="

# Step 1: pkg build → server.exe
L "[1/5] pkg build"
Push-Location $root
& npx pkg src\server.js --targets node18-win-x64 --output dist\server.exe
$pkgExit = $LASTEXITCODE
Pop-Location
if ($pkgExit -ne 0) { L "FAIL: pkg exit=$pkgExit"; exit 11 }
Copy-Item "$root\dist\server.exe" "$root\server.exe" -Force
L "  server.exe OK"

# Step 1b: Compile MastersFM.exe + customize.exe via csc.exe
L "[1b/5] Compiling MastersFM.exe via csc.exe..."
$csc = $null
$paths = @(
    (Join-Path $env:windir "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:windir "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
foreach ($p in $paths) { if (Test-Path $p) { $csc = $p; break } }
if ($csc) {
    # v5 added System.Windows.Forms (hidden form → HWND → Task Manager app-root).
    # System.Drawing needed for Size/Point used to push the form offscreen.
    $args = "/nologo /target:winexe /win32icon:assets\MastersFM.ico /out:MastersFM.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\launcher.cs"
    $c = Start-Process -FilePath $csc -ArgumentList $args -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($c.ExitCode -eq 0) { L "  MastersFM.exe OK" } else { L "  WARN: csc exit=$($c.ExitCode) - MastersFM.exe may be stale" }

    # customize.exe — WebView2-hosted native window for customize.html.
    # References the two managed WebView2 DLLs; native WebView2Loader.dll must
    # sit alongside customize.exe at runtime (copied by the MSI installer).
    L "[1c/5] Compiling customize.exe via csc.exe..."
    $wv2core = Join-Path $root 'Microsoft.Web.WebView2.Core.dll'
    $wv2wf   = Join-Path $root 'Microsoft.Web.WebView2.WinForms.dll'
    if ((Test-Path $wv2core) -and (Test-Path $wv2wf)) {
        $argsC = '/nologo /target:winexe /win32icon:assets\MastersFM.ico /out:customize.exe ' +
                 '/reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll ' +
                 "/reference:`"$wv2core`" /reference:`"$wv2wf`" src\customize.cs"
        $c2 = Start-Process -FilePath $csc -ArgumentList $argsC -WorkingDirectory $root -Wait -PassThru -NoNewWindow
        if ($c2.ExitCode -eq 0) { L "  customize.exe OK" } else { L "  WARN: customize.exe csc exit=$($c2.ExitCode)" }
    } else {
        L "  WARN: WebView2 DLLs missing - customize.exe NOT built"
    }

    # MastersFM_Tray.exe — C# exe that hosts PowerShell IN-PROCESS (via
    # System.Management.Automation). Replaces the separate powershell.exe
    # child — now there's no "Windows PowerShell" entry cluttering Task
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
} else {
    L "  WARN: csc.exe not found"
}

# Step 1d2: Compile audio_spectrum.exe (WASAPI loopback provider) — uses a
# separate helper script because NAudio.Core 2.x is netstandard2.0 and needs
# the netstandard.dll facade reference from .NET Framework 4.7.2+. That plus
# the "Program Files (x86)" space in the facade path made inline csc here
# brittle — the helper script quotes every path explicitly.
L "[1d2/5] Compiling audio_spectrum.exe (WASAPI loopback) via helper..."
$spec = Join-Path $root 'build_tools\ps2exe\_build_spectrum.ps1'
if (Test-Path $spec) {
    $sp = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$spec`"",'-Root',"`"$root`"") `
        -Wait -PassThru -NoNewWindow
    if ($sp.ExitCode -eq 0) { L "  audio_spectrum.exe OK" } else { L "  WARN: audio_spectrum.exe build exit=$($sp.ExitCode)" }
} else {
    L "  WARN: _build_spectrum.ps1 not found - audio_spectrum.exe NOT built"
}

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
        # v7.0.0 — also replace the Node green-box icon with MastersFM.ico so
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
        #    output — "Delete resources. (count = 2)" once fixed.
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
            # Swap in the rebranded binary. Keep the size check as a sanity net —
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
# autoInstall is set to false — flip it to true in _push_update.ps1 after
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

# Launch — always via the C# launcher (MastersFM.exe). The old VBS wrapper
# is no longer shipped by the installer, so no fallback path is needed.
$dest = "$env:LOCALAPPDATA\MastersFM"
if (Test-Path "$dest\MastersFM.exe") {
    L "Launching $dest\MastersFM.exe"
    Start-Process "$dest\MastersFM.exe"
    L "  launched"
} else {
    L "WARN: MastersFM.exe not found at $dest"
}

# v5.1.8 — single folder on the Desktop holding EVERYTHING friends need,
# nothing else. No standalone MSI, no standalone cer, no bootstrapper exe
# cluttering the Desktop. The folder is self-contained + zip-ready.
try {
    $trayTxt = Get-Content "$root\src\tray.ps1" -Raw
    $appVer  = 'v2.0.0'
    if ($trayTxt -match '\$script:APP_VERSION\s*=\s*"([^"]+)"') { $appVer = $matches[1] }

    # Purge ALL older Master's FM clutter from the Desktop — standalone
    # MSIs, old bootstrappers, separate .cer files — so only the single
    # MastersFM_Installer folder remains.
    Get-ChildItem "$env:USERPROFILE\Desktop" -Filter "Master's FM V*.msi" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue; L "  Purged from Desktop: $($_.Name)" }
    Get-ChildItem "$env:USERPROFILE\Desktop" -Filter "Install Master's FM *.exe" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue; L "  Purged from Desktop: $($_.Name)" }
    $strayCer = Join-Path $env:USERPROFILE 'Desktop\MastersFM_publisher.cer'
    if (Test-Path $strayCer) { Remove-Item $strayCer -Force -ErrorAction SilentlyContinue; L "  Purged from Desktop: MastersFM_publisher.cer" }
    $strayTxt = Join-Path $env:USERPROFILE 'Desktop\INSTALL_INSTRUCTIONS.txt'
    if (Test-Path $strayTxt) { Remove-Item $strayTxt -Force -ErrorAction SilentlyContinue; L "  Purged from Desktop: INSTALL_INSTRUCTIONS.txt" }

    # v5.1.7 — bootstrapper.exe build DISABLED pending a real CA cert
    # (option D, Certum ~EUR 25/yr). With only a self-signed cert the
    # bootstrapper pattern (embedded MSI payload + self-elevate + cert
    # store modification) tripped Bitdefender behavioural heuristics. MSI
    # + .cer + INSTALL.bat path is AV-safe because it runs through
    # Windows' trusted cert-install + msiexec infrastructure.  To restore
    # the bootstrapper once you have a real cert: un-comment the block
    # below and update _sign_msi.ps1 to point at the new cert thumbprint.
    # ---
    #   L "[2c/5] Building signed one-click bootstrapper exe..."
    #   $bootScript = Join-Path $root 'build_tools\signing\_build_bootstrapper.ps1'
    #   if (Test-Path $bootScript) {
    #       try {
    #           & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bootScript -Root $root -MsiPath $msi 2>&1 | ForEach-Object { L "    $_" }
    #       } catch { L "  WARN: bootstrapper build failed: $_" }
    #   }

    # Single folder on Desktop with EVERYTHING — send this to friends (zip it first).
    # v5.2.4: exactly THREE files inside — MSI + INSTALL.bat + publisher .cer.
    # The standalone INSTALL_INSTRUCTIONS.txt was retired; INSTALL.bat is
    # self-explanatory (one double-click → one UAC prompt → done) and friends
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
