# Stage 7.25.5 (R14 prep): -FullRebuild switch forces the full cold rebuild
# path even when only HTML files have changed. Default behavior with no flags
# is fast-path-when-eligible, cold-rebuild-otherwise. See the entry-point
# branch below (after the helper function definitions) for the dispatch.
param(
    [switch]$FullRebuild,
    [switch]$BuildOnly
)

Set-Location "G:\Project Folder\Master FM"
$root  = "G:\Project Folder\Master FM"
$logDir = "$root\logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$ts  = Get-Date -Format 'yyyyMMdd_HHmmss'

# ============================================================================
# v14.0.0-rc.1 build flags (Stage 8 flattened)
# ----------------------------------------------------------------------------
# All six .NET-8 migration flags permanently $true; csc.exe rollback branches
# collapsed in Stage 8. Bootstrapper remains disabled ($UseDotnet8Bootstrapper
# = $false) -- Certum cert pending; see open_issues.md for re-enable steps.
# ============================================================================

# Stages 1-7: all .NET 8 migration flags permanently $true (csc.exe rollback paths retired in Stage 8).
# Stage 3b: bootstrapper DISABLED ($false) -- Certum cert pending; see open_issues.md.
# Bitdefender flags self-signed bootstrapper (embedded MSI + self-elevate + cert store mod).
# Re-enable: (a) real CA cert, (b) EmbeddedResource items for payload.msi+publisher.cer, (c) update _sign_msi.ps1.
$UseDotnet8Bootstrapper = $false
$log = "$logDir\rebuild_ps_$ts.log"

function L($msg) { $t = Get-Date -Format 'HH:mm:ss'; "$t  $msg" | Tee-Object -FilePath $log -Append | Write-Host }

# ============================================================================
# Stage 7.25.5: HTML-only fast-path detection (R14 prep).
# ----------------------------------------------------------------------------
# Returns $true when `git diff --name-only HEAD` shows ONLY changes to
# `src/customize.html` and/or `src/overlay.html`. In that case the entry-point
# branch (added below in STEP 3) skips the ~10-minute cold rebuild and just
# copies the changed HTML files into the install location.
#
# Empty diff -> returns $false so the safer default (full rebuild) runs. This
# preserves existing behavior when the script is invoked against a clean
# working tree (e.g. after a successful rebuild; the rebuild script always
# runs unconditionally even with no changes).
#
# ANY non-HTML change in the diff (a .cs file, .xaml, .ps1, build script, etc.)
# also returns $false -- full rebuild handles those correctly.
# ============================================================================
function Test-IsHtmlOnlyChange {
    $changed = git diff --name-only HEAD 2>$null
    if (-not $changed) { return $false }

    $changedLines = $changed -split "`n" | Where-Object { $_ -ne '' } | ForEach-Object { $_.Trim() }
    if ($changedLines.Count -eq 0) { return $false }

    $htmlOnlyFiles = @('src/customize.html', 'src/overlay.html')
    foreach ($file in $changedLines) {
        if ($file -notin $htmlOnlyFiles) { return $false }
    }
    return $true
}

# ============================================================================
# Stage 7.25.5: HTML-only fast-path execution (R14 prep).
# ----------------------------------------------------------------------------
# Copies the changed HTML files from the source tree into the installed
# application directory. NO MSI rebuild, NO dotnet publish, NO certificate
# signing, NO process restart. Takes ~5 seconds vs the ~10-minute cold
# rebuild path.
#
# Install layout is FLAT (no `src/` subdirectory), so the file's leaf name
# is used at the destination side. Source paths from `git diff` include the
# `src/` prefix.
#
# Process management : customize.exe (PyWebView/WebView2 host) re-reads
# customize.html each time the user opens the Customize Overlay window from
# the tray menu. overlay.html is read by OBS browser source; OBS does not
# auto-refresh, so the user must refresh the browser source in OBS to see
# changes. Neither requires us to stop/restart any process here.
# ============================================================================
function Invoke-HtmlOnlyFastPath {
    L "=== HTML-ONLY FAST PATH ==="

    $installRoot = "$env:LOCALAPPDATA\MastersFM"
    if (-not (Test-Path $installRoot)) {
        L "  FAIL: install root not found at $installRoot"
        L "  Falling back is NOT done automatically -- run with -FullRebuild to perform a cold install."
        return $false
    }

    $changed = git diff --name-only HEAD 2>$null
    $changedLines = $changed -split "`n" | Where-Object { $_ -ne '' } | ForEach-Object { $_.Trim() }
    foreach ($file in $changedLines) {
        $src = Join-Path $root $file
        # Install layout is FLAT; strip src/ prefix.
        $leafName = Split-Path $file -Leaf
        $dst = Join-Path $installRoot $leafName
        if (-not (Test-Path $src)) {
            L "  WARN: source missing: $src"
            continue
        }
        Copy-Item $src $dst -Force
        L "  copied: $file -> $dst"
    }

    L ""
    L "  Reopen Customize Overlay (tray menu) OR refresh OBS browser source to see changes."
    L ""
    L "=== HTML-ONLY FAST PATH DONE ==="
    return $true
}

# ============================================================================
# Stage 7.25.5: HTML-only fast-path entry-point branch (R14 prep).
# ----------------------------------------------------------------------------
# When the working tree shows ONLY src/customize.html and/or src/overlay.html
# changes vs HEAD, skip the ~10-minute cold rebuild and just copy the HTML
# files into the install location (~5 seconds total). The -FullRebuild
# switch (declared in the param() block at the top of the file) overrides
# this and forces the full cold rebuild even on HTML-only changes -- use
# it when you suspect the fast path is missing something.
# ============================================================================
if (-not $FullRebuild -and (Test-IsHtmlOnlyChange)) {
    Invoke-HtmlOnlyFastPath | Out-Null
    exit 0
}

L "=== REBUILD START ==="

# Stage 7.30.7: preflight -- kill any lingering VBCSCompiler (Roslyn build server).
# It can hold file locks on obj/bin between builds and cause flaky/failed dotnet publishes.
# Safe no-op when none are running. The HTML-only fast-path branch above never builds, so
# placing this after REBUILD START keeps the fast path unaffected.
Get-Process VBCSCompiler -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
L "  preflight: VBCSCompiler pre-killed (if any were running)"

# v14: clean stale framework-dependent build outputs (satellite DLLs / *.deps.json /
# *.runtimeconfig.json) left at ROOT + dist by a PREVIOUS framework-dependent build.
# Under self-contained single-file publish these are bundled INTO each exe and are NOT
# regenerated, so the leftovers would otherwise be picked up by the MSI (build_msi bundles
# whatever exists). Removing them keeps the self-contained MSI clean + small. Only build
# artifacts are deleted -- tray_native.dll is rebuilt by dotnet build, *.exe are re-published,
# and src/*.html|*.ps1|*.ico|*.json are never touched.
$staleRoot = @(
  'server.dll','server.deps.json','server.runtimeconfig.json','server.staticwebassets.endpoints.json','web.config',
  'DiscordRPC.dll','Newtonsoft.Json.dll',
  'MastersFM.dll','MastersFM.deps.json','MastersFM.runtimeconfig.json',
  'customize.dll','customize.deps.json','customize.runtimeconfig.json',
  'Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','Microsoft.Web.WebView2.Wpf.dll','WebView2Loader.dll',
  'audio_spectrum.dll','audio_spectrum.deps.json','audio_spectrum.runtimeconfig.json',
  'NAudio.Core.dll','NAudio.Wasapi.dll','NAudio.WinMM.dll','NAudio.Asio.dll',
  'Microsoft.Win32.Registry.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll',
  'System.Runtime.CompilerServices.Unsafe.dll','System.Security.AccessControl.dll','System.Security.Principal.Windows.dll',
  'MastersFM_Tray_v14.dll','MastersFM_Tray_v14.deps.json','MastersFM_Tray_v14.runtimeconfig.json',
  'CommunityToolkit.Mvvm.dll','H.GeneratedIcons.System.Drawing.dll','H.NotifyIcon.dll','H.NotifyIcon.Wpf.dll',
  'Microsoft.Extensions.DependencyInjection.Abstractions.dll','Microsoft.Extensions.DependencyInjection.dll',
  'Microsoft.Win32.SystemEvents.dll','Microsoft.Windows.SDK.NET.dll','System.Drawing.Common.dll',
  'System.Private.Windows.Core.dll','WinRT.Runtime.dll','Wpf.Ui.Abstractions.dll','Wpf.Ui.dll'
)
$staleCleaned = 0
foreach ($sf in $staleRoot) { $p = Join-Path $root $sf; if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue; $staleCleaned++ } }
foreach ($dd in @('dist\server_dotnet_release','dist\launcher_net8','dist\customize_net8','dist\audio_spectrum_net8','dist\tray_csharp_release','dist\obs_cleanup_release')) {
  $dp = Join-Path $root $dd; if (Test-Path $dp) { Remove-Item "$dp\*" -Recurse -Force -ErrorAction SilentlyContinue }
}
L "  preflight: cleaned $staleCleaned stale ROOT build-outputs + cleared dist\*_release (keeps self-contained MSI clean)"

# Step 1: server.exe -- .NET ASP.NET Core (Stage 4; legacy Node.js+pkg path retired in Stage 8).
L "[1/5] Building server.exe via dotnet publish (net8.0, ASP.NET Core, R2R)..."
$svOut = Join-Path $root 'dist\server_dotnet_release'
$svArgs = "publish `"$root\src\server_dotnet\server_dotnet.csproj`" -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true " +
          "-p:PublishReadyToRun=false -c Release -o `"$svOut`" --nologo -v quiet"
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
        foreach ($sf in @('server.exe')) {
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

# Step 1b: Build C# binaries
L "[1b/5] Building C# binaries..."
# csc.exe detection removed in Stage 8 (no consumers after [1d/5] block deletion).

# MastersFM.exe (launcher) -- Stage 1: .NET 8 path (legacy csc.exe path retired in Stage 8).
L “  [Stage 1] Building MastersFM.exe via dotnet publish (net8.0-windows, R2R)...”
$pubOut = Join-Path $root 'dist\launcher_net8'
# dotnet publish embeds PublishDir into an MSBuild response file; any path with spaces is
# either split at whitespace (unquoted) or mis-resolved relative to the CWD (quoted with a
# literal '”' prefix) by the MSBuild server that lingers from the server_dotnet build.
# Fix: use the PowerShell '&' call operator (passes each arg as a native CLR string, not a
# single shell-tokenised blob) with a no-spaces temp output dir; copy artifacts afterwards.
# (Stage 8 investigation: S4 final fix, 2026-05-09)
$pubTmp = 'G:\lnch_pub_tmp'
if (Test-Path $pubTmp) { Remove-Item $pubTmp -Recurse -Force -ErrorAction SilentlyContinue }
$dpOut = & dotnet publish “$root\src\launcher.csproj” -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false -c Release -o $pubTmp --nologo -v quiet 2>&1
$dpExit = $LASTEXITCODE
if ($dpExit -eq 0) {
    if (-not (Test-Path $pubOut)) { New-Item -ItemType Directory -Path $pubOut -Force | Out-Null }
    Copy-Item “$pubTmp\*” $pubOut -Force
    Remove-Item $pubTmp -Recurse -Force -ErrorAction SilentlyContinue
    # Copy all launcher artifacts to root -- MSI picks them up from there.
    # MastersFM.pdb is intentionally excluded (debug symbols, not shipped).
    foreach ($lf in @('MastersFM.exe','MastersFM.dll','MastersFM.deps.json','MastersFM.runtimeconfig.json')) {
        $lsrc = Join-Path $pubOut $lf
        if (Test-Path $lsrc) { Copy-Item $lsrc “$root\$lf” -Force }
    }
    L “  MastersFM.exe OK (net8.0-windows, R2R)”
} else {
    Remove-Item $pubTmp -Recurse -Force -ErrorAction SilentlyContinue
    $dpOut | ForEach-Object { L “    $_” }
    L “  FAIL: dotnet publish exit=$dpExit -- Stage 1 launcher build failed, aborting”
    exit 11
}

# customize.exe -- Stage 3a: .NET 8 path (legacy csc.exe path retired in Stage 8).
L "[1c/5] Building customize.exe (WebView2 host)..."
$czOut = Join-Path $root 'dist\customize_net8'
$czTmp = 'G:\cz_pub_tmp'
if (Test-Path $czTmp) { Remove-Item $czTmp -Recurse -Force -ErrorAction SilentlyContinue }
$czOut2 = & dotnet publish "$root\src\customize.csproj" -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false -c Release -o $czTmp --nologo -v quiet 2>&1
$czExit = $LASTEXITCODE
if ($czExit -eq 0) {
    if (-not (Test-Path $czOut)) { New-Item -ItemType Directory -Path $czOut -Force | Out-Null }
    Copy-Item "$czTmp\*" $czOut -Force
    Remove-Item $czTmp -Recurse -Force -ErrorAction SilentlyContinue
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
        foreach ($sf in @('customize.exe')) {
            $sfPath = Join-Path $root $sf
            try {
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $sfPath 2>&1 | ForEach-Object { L "    $_" }
            } catch { L "  WARN: signing $sf failed: $_" }
        }
    }
} else {
    Remove-Item $czTmp -Recurse -Force -ErrorAction SilentlyContinue
    $czOut2 | ForEach-Object { L "    $_" }
    L "  FAIL: dotnet publish customize exit=$czExit -- Stage 3a build failed, aborting"
    exit 11
}

# [1d/5] tray_launcher.cs csc.exe build + tray_native.dll csc.exe fallback removed in Stage 8 (both paths dead since $UseDotnet8TrayCs and $UseDotnetTrayNative permanently true).

# tray_native.dll -- Stage 5: dotnet build netstandard2.0 for PS5.1 compat (legacy csc.exe retired in Stage 8).
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

# Stage 7: WPF tray (H.NotifyIcon.Wpf+WPF-UI+CommunityToolkit.Mvvm) -- Stage 7.10 cutover, PS tray retired.
# Pinned NuGet versions documented in V14_S7_S7_1B_NUGET_PINS.md.
L "=== Stage 7: building WPF tray (dotnet publish, net8.0-windows, R2R) ==="
L "    Pinned NuGets: H.NotifyIcon.Wpf 2.3.2, WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection 9.0.15"
$trayCsOut  = Join-Path $root 'dist\tray_csharp_release'
$trayCsTmp  = 'G:\tray_pub_tmp'
if (Test-Path $trayCsTmp) { Remove-Item $trayCsTmp -Recurse -Force -ErrorAction SilentlyContinue }
# Multi-file + R2R: small stub exe + apphost + .dll + .runtimeconfig.json + pinned NuGet DLLs.
$trayCsOut2 = & dotnet publish "$root\src\tray_csharp\MastersFM_Tray_v14.csproj" -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false -c Release -o $trayCsTmp --nologo -v quiet 2>&1
$trayCsExit = $LASTEXITCODE
if ($trayCsExit -eq 0) {
    if (-not (Test-Path $trayCsOut)) { New-Item -ItemType Directory -Path $trayCsOut -Force | Out-Null }
    Copy-Item "$trayCsTmp\*" $trayCsOut -Force
    Remove-Item $trayCsTmp -Recurse -Force -ErrorAction SilentlyContinue
    $trayCsExe = Join-Path $trayCsOut 'MastersFM_Tray_v14.exe'
    if (Test-Path $trayCsExe) {
        $trayCsSize = [math]::Round((Get-Item $trayCsExe).Length / 1KB, 1)
        $trayCsTotalKB = [math]::Round(((Get-ChildItem $trayCsOut -File | Measure-Object Length -Sum).Sum) / 1KB, 1)
        L "=== WPF tray built: $trayCsExe ($trayCsSize KB exe; $trayCsTotalKB KB total in dist) ==="
    } else {
        L "  WARN: WPF tray dotnet publish exit=0 but MastersFM_Tray_v14.exe not found at expected path"
        L "  FAIL: WPF tray build produced no exe -- aborting (Stage 7.30.7 hard error)"
        exit 11
    }
} else {
    Remove-Item $trayCsTmp -Recurse -Force -ErrorAction SilentlyContinue
    L "  FAIL: WPF tray dotnet publish failed (exit $trayCsExit) -- aborting (Stage 7.30.7 hard error)"
    exit 11
}

# Stage 7.8C STEP 4: MastersFM_ObsCleanup.exe -- MSI-uninstall OBS cleanup helper.
# Shipped to %PROGRAMDATA%\MastersFM\Cleanup\ by MSI (STEP 5).
# Framework-dependent (no R2R needed; binary is tiny + rarely executed).
L "=== Stage 7.8C: building MastersFM_ObsCleanup.exe ==="
$cleanupOut = Join-Path $root 'dist\obs_cleanup_release'
$cleanupTmp = 'G:\cleanup_pub_tmp'
if (Test-Path $cleanupTmp) { Remove-Item $cleanupTmp -Recurse -Force -ErrorAction SilentlyContinue }
$cleanupResult = & dotnet publish "$root\src\obs_cleanup\MastersFM_ObsCleanup.csproj" -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -c Release -o $cleanupTmp --nologo -v quiet 2>&1
$cleanupExit = $LASTEXITCODE
if ($cleanupExit -eq 0) {
    if (-not (Test-Path $cleanupOut)) { New-Item -ItemType Directory -Path $cleanupOut -Force | Out-Null }
    Copy-Item "$cleanupTmp\*" $cleanupOut -Force
    Remove-Item $cleanupTmp -Recurse -Force -ErrorAction SilentlyContinue
    $cleanupExe = Join-Path $cleanupOut 'MastersFM_ObsCleanup.exe'
    if (Test-Path $cleanupExe) {
        $cleanupSz = [math]::Round((Get-Item $cleanupExe).Length / 1KB, 1)
        L "  MastersFM_ObsCleanup.exe OK ($cleanupSz KB)"
    }
} else {
    Remove-Item $cleanupTmp -Recurse -Force -ErrorAction SilentlyContinue
    $cleanupResult | ForEach-Object { L "    $_" }
    L "  WARN: MastersFM_ObsCleanup build failed (exit $cleanupExit) -- continuing"
}

# v14 auto-update: MastersFM_Updater.exe -- the signed self-contained update SURVIVOR.
# Staged by the tray's InstallAsync into %LOCALAPPDATA%\MastersFM_Update\ then spawned
# ELEVATED to run msiexec /x + /i. Must be self-contained single-file (the friend's
# machine may have NO .NET runtime) and signed with the MasterShadex cert so the UAC
# prompt + import-cert path stay consistent with the MSI. build_msi.py harvests the exe
# from dist\updater_release\ AND from the repo ROOT (it bundles the publisher .cer from
# build_tools\signing\); we copy the exe to root after publish like the other components.
L "=== v14 auto-update: building MastersFM_Updater.exe (dotnet publish, net8.0-windows, self-contained) ==="
$updOut = Join-Path $root 'dist\updater_release'
$updTmp = 'G:\upd_pub_tmp'
if (Test-Path $updTmp) { Remove-Item $updTmp -Recurse -Force -ErrorAction SilentlyContinue }
$updResult = & dotnet publish "$root\src\updater\updater.csproj" -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false -c Release -o $updTmp --nologo -v quiet 2>&1
$updExit = $LASTEXITCODE
if ($updExit -eq 0) {
    if (-not (Test-Path $updOut)) { New-Item -ItemType Directory -Path $updOut -Force | Out-Null }
    Copy-Item "$updTmp\*" $updOut -Force
    Remove-Item $updTmp -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Remove-Item $updTmp -Recurse -Force -ErrorAction SilentlyContinue
    $updResult | ForEach-Object { L "    $_" }
    L "  FAIL: dotnet publish updater exit=$updExit -- v14 auto-update build failed, aborting"
    exit 11
}
# HARD-ERROR if the survivor exe is missing after publish (mirror the WPF tray hard error).
$updExe = Join-Path $updOut 'MastersFM_Updater.exe'
if (-not (Test-Path $updExe)) {
    L "  FAIL: updater build produced no MastersFM_Updater.exe at $updExe -- aborting (v14 auto-update hard error)"
    exit 11
}
$updSz = [math]::Round((Get-Item $updExe).Length / 1KB, 1)
L "  MastersFM_Updater.exe OK ($updSz KB)"
# SIGN the updater with the MasterShadex cert. Best-effort: warn (don't hard-fail) if the
# cert is missing -- matches how the MSI signing is non-fatal. Cert lookup + signtool
# discovery mirror build_tools\signing\_sign_msi.ps1.
$updCert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq 'CN=MasterShadex, O=MasterShadex' -and $_.FriendlyName -eq "Master's FM Code Signing" } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($updCert) {
    $updSigntool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match 'x64' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($updSigntool) {
        L "  signing MastersFM_Updater.exe with signtool ($($updSigntool.FullName))"
        & $updSigntool.FullName sign /sha1 $updCert.Thumbprint /fd SHA256 /t 'http://timestamp.digicert.com' $updExe 2>&1 | ForEach-Object { L "    $_" }
        if ($LASTEXITCODE -ne 0) {
            L "  WARN: signtool exit=$LASTEXITCODE -- falling back to Set-AuthenticodeSignature"
            try { Set-AuthenticodeSignature -FilePath $updExe -Certificate $updCert -TimestampServer 'http://timestamp.digicert.com' -HashAlgorithm SHA256 | Out-Null }
            catch { L "  WARN: Set-AuthenticodeSignature failed: $_" }
        }
    } else {
        L "  signtool not found -- signing MastersFM_Updater.exe via Set-AuthenticodeSignature"
        try { Set-AuthenticodeSignature -FilePath $updExe -Certificate $updCert -TimestampServer 'http://timestamp.digicert.com' -HashAlgorithm SHA256 | Out-Null }
        catch { L "  WARN: Set-AuthenticodeSignature failed: $_" }
    }
} else {
    L "  WARN: MasterShadex cert not found -- MastersFM_Updater.exe stays unsigned (best-effort)"
}
# Copy the signed updater to the repo ROOT (the SRC dir build_msi.py harvests from),
# exactly like the obs_cleanup / audio_spectrum exes are copied to root after publish.
Copy-Item $updExe "$root\MastersFM_Updater.exe" -Force
L "  MastersFM_Updater.exe copied to root"

# Step 1d2: Build audio_spectrum.exe (WASAPI/MME/WDM-KS/ASIO spectrum provider)
# Stage 2: .NET 8 path (legacy csc.exe+_build_spectrum.ps1 path retired in Stage 8).
L "[1d2/5] Building audio_spectrum.exe (WASAPI loopback)..."
$asOut = Join-Path $root 'dist\audio_spectrum_net8'
$asTmp = 'G:\as_pub_tmp'
if (Test-Path $asTmp) { Remove-Item $asTmp -Recurse -Force -ErrorAction SilentlyContinue }
$asOut2 = & dotnet publish "$root\src\audio_spectrum.csproj" -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false -c Release -o $asTmp --nologo -v quiet 2>&1
$asExit = $LASTEXITCODE
if ($asExit -eq 0) {
    if (-not (Test-Path $asOut)) { New-Item -ItemType Directory -Path $asOut -Force | Out-Null }
    Copy-Item "$asTmp\*" $asOut -Force
    Remove-Item $asTmp -Recurse -Force -ErrorAction SilentlyContinue
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
        foreach ($sf in @('audio_spectrum.exe')) {
            $sfPath = Join-Path $root $sf
            try {
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $sfPath 2>&1 | ForEach-Object { L "    $_" }
            } catch { L "  WARN: signing $sf failed: $_" }
        }
    }
} else {
    Remove-Item $asTmp -Recurse -Force -ErrorAction SilentlyContinue
    L "  FAIL: dotnet publish audio_spectrum exit=$($as.ExitCode) -- Stage 2 build failed, aborting"
    exit 11
}

# Step 1e: resedit rebrand removed in Stage 8 (.NET server has its own PE info; legacy Node pkg server retired).

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
    # Read version from version.json (single source of truth -- never from tray.ps1)
    $versionJsonPath = Join-Path $root 'version.json'
    $appVer = '14.0.0'
    if (Test-Path $versionJsonPath) {
        try { $appVer = (Get-Content $versionJsonPath -Raw | ConvertFrom-Json).version } catch {}
    }

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

    [System.IO.File]::WriteAllText($versionJsonPath, $versionJson, [System.Text.Encoding]::UTF8)
    L "  version.json written: version=$appVer sha256=$hashHex"
    L "  NEXT: upload '$msiName' to GitHub Releases tag v$appVer, then run _push_update.ps1 to publish"
} catch {
    L "  WARN: version.json generation failed: $_"
}

# v14 BuildOnly: the MSI is built + signed above. Stop here so we do NOT touch the
# running install -- used to produce a test-fm update artifact without bumping this machine.
if ($BuildOnly) {
    L "[BuildOnly] MSI built + signed: $msi"
    L "[BuildOnly] Skipping uninstall/install/launch -- running app left untouched."
    exit 0
}

# Step 3: Stop running processes (v14 .NET 8 process inventory; the pre-v14
# tray.ps1 command-line scan was retired with the PowerShell tray)
L "[3/5] Stopping tray + server"
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
    # v14: app version comes from version.json (single source of truth) instead of
    # the retired tray.ps1's $script:APP_VERSION. Display form keeps the v-prefix.
    $appVer = 'v0.0.0'
    try {
        $vj = Get-Content "$root\version.json" -Raw | ConvertFrom-Json
        if ($vj.version) { $appVer = if ($vj.version.StartsWith('v')) { $vj.version } else { "v$($vj.version)" } }
    } catch {}

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
    #               "-p:PublishReadyToRun=false -c Release -o `"$bsOut`" --nologo -v quiet"
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
    if (Test-Path $batSrc) {
        # v14: force CRLF on the shipped .bat -- cmd.exe mis-parses LF-only batch
        # files (a prior bundle shipped LF-only and silently failed on friends:
        # "X is not recognized" burst, then the window closed with nothing installed).
        $batTxt = [IO.File]::ReadAllText($batSrc) -replace "`r`n","`n" -replace "`n","`r`n"
        [IO.File]::WriteAllText((Join-Path $bundleDir 'INSTALL.bat'), $batTxt, (New-Object System.Text.UTF8Encoding $false))
        L "  Bundle: INSTALL.bat (CRLF-normalized)"
    }
    $cerSrc = Join-Path $root 'build_tools\signing\MastersFM_publisher.cer'
    if (Test-Path $cerSrc) { Copy-Item $cerSrc (Join-Path $bundleDir 'MastersFM_publisher.cer') -Force; L "  Bundle: MastersFM_publisher.cer" }

    L "  SEND-TO-FRIENDS: zip '$bundleDir' and share it (3 files inside)"
} catch { L "  WARN: Desktop bundle copy failed - $_" }

L "=== REBUILD DONE OK ==="


