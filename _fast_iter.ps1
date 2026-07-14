# _fast_iter.ps1 - FAST local hot-swap for Master's FM dev iteration.
#
# WHY THIS EXISTS: the operator will NEVER again wait ~10 min for a full cold
# MSI rebuild (_full_rebuild.ps1) just to test a code change. That cold path
# rebuilds all 6 projects + makecab (414 MB) + MSI db + uninstall + install.
# None of that is needed to test a change on this machine. This script
# publishes ONLY the project(s) you changed and hot-swaps the single exe into
# the live install dir, then restarts ONLY that process. Target: 20-40 s.
#
# USAGE:
#   .\_fast_iter.ps1 tray            # rebuild + swap MastersFM_Tray.exe, restart tray
#   .\_fast_iter.ps1 spectrum        # rebuild + swap audio_spectrum.exe, restart spectrum
#   .\_fast_iter.ps1 server          # rebuild + swap server.exe, restart server
#   .\_fast_iter.ps1 tray spectrum   # multiple in one go
#   .\_fast_iter.ps1 all             # tray + spectrum + server
#
# The full cold MSI rebuild (_full_rebuild.ps1) is ONLY for the final
# ship-to-GitHub step, run ONCE after everything is verified here.

param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Targets)

$ErrorActionPreference = 'Stop'
$root    = 'G:\Project Folder\Master FM'
$install = "$env:LOCALAPPDATA\MastersFM"
Set-Location $root

if (-not $Targets -or $Targets.Count -eq 0) {
    Write-Host "usage: ._fast_iter.ps1 (tray|spectrum|server|all) [more...]" -ForegroundColor Yellow
    exit 1
}
if ($Targets -contains 'all') { $Targets = @('tray','spectrum','server') }

# project -> (csproj, published-exe-name, install-exe-name, process-name)
$map = @{
    'tray'     = @{ csproj='src\tray_csharp\MastersFM_Tray_v14.csproj'; pubexe='MastersFM_Tray_v14.exe'; dstexe='MastersFM_Tray.exe'; proc='MastersFM_Tray' }
    'spectrum' = @{ csproj='src\audio_spectrum.csproj';                 pubexe='audio_spectrum.exe';     dstexe='audio_spectrum.exe'; proc='audio_spectrum' }
    'server'   = @{ csproj='src\server_dotnet\server_dotnet.csproj';    pubexe='server.exe';             dstexe='server.exe';         proc='server' }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($t in $Targets) {
    if (-not $map.ContainsKey($t)) { Write-Host ("  unknown target '{0}' - skipping" -f $t) -ForegroundColor Yellow; continue }
    $m = $map[$t]
    $tmp = Join-Path $env:TEMP ("_mfm_fastiter_" + $t)

    Write-Host ("[{0}] publish {1} ..." -f $t, $m.csproj) -ForegroundColor Cyan
    # NO compression + NO R2R = fastest possible publish. The local exe is bigger
    # than the shipped one but that's irrelevant for on-machine testing. This is
    # the difference between ~25 s and ~60 s for the WPF tray.
    $out = & dotnet publish "$root\$($m.csproj)" -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false -p:PublishReadyToRun=false `
        -c Release -o $tmp --nologo -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("  PUBLISH FAILED for {0}:" -f $t) -ForegroundColor Red
        $out | Select-Object -Last 15 | ForEach-Object { Write-Host ("    {0}" -f $_) }
        exit 2
    }
    $src = Join-Path $tmp $m.pubexe
    if (-not (Test-Path $src)) { Write-Host ("  built exe not found: {0}" -f $src) -ForegroundColor Red; exit 3 }

    Write-Host ("[{0}] stop process '{1}' ..." -f $t, $m.proc) -ForegroundColor Cyan
    Get-Process -Name $m.proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600   # let the file handle release

    $dst = Join-Path $install $m.dstexe
    Copy-Item $src $dst -Force
    $szMB = [math]::Round((Get-Item $dst).Length / 1MB, 1)
    Write-Host ("[{0}] swapped -> {1} ({2} MB)" -f $t, $dst, $szMB) -ForegroundColor Green
}

# Relaunch. If the launcher (MastersFM.exe) is still alive, killing the swapped
# children left it running, so just re-spawn the specific child exes directly.
# Otherwise launch the full app.
$launcher = Get-Process -Name 'MastersFM' -ErrorAction SilentlyContinue
if ($launcher) {
    Write-Host "launcher alive - re-spawning swapped children directly" -ForegroundColor Cyan
    foreach ($t in $Targets) {
        if (-not $map.ContainsKey($t)) { continue }
        $exe = Join-Path $install $map[$t].dstexe
        # -WindowStyle Hidden: audio_spectrum.exe / server.exe are CONSOLE apps;
        # spawning them directly without this pops a CMD window every hot-swap
        # (operator was gaming and got spammed with console windows, 2026-06-30).
        if (Test-Path $exe) { Start-Process $exe -WorkingDirectory $install -WindowStyle Hidden | Out-Null }
    }
} else {
    Write-Host "launcher not running - launching full app" -ForegroundColor Cyan
    Start-Process (Join-Path $install 'MastersFM.exe') -WindowStyle Hidden | Out-Null
}

$sw.Stop()
Start-Sleep -Seconds 2
Write-Host ""
Write-Host ("=== fast-iter done in {0}s ===" -f [math]::Round($sw.Elapsed.TotalSeconds,1)) -ForegroundColor Green
Get-Process -Name MastersFM,MastersFM_Tray,audio_spectrum,server -ErrorAction SilentlyContinue |
    Select-Object Name, Id, @{n='Mem_MB';e={[math]::Round($_.WorkingSet/1MB)}} | Format-Table -AutoSize
