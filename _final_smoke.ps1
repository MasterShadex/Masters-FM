# Post-v5.3.0 smoke test - verifies the whole app is up and each piece
# is doing its job after the multi-backend audio refactor.
$errors = @()

function Check($name, $ok, $detail='') {
    if ($ok) { Write-Host "  [PASS] $name  $detail" -ForegroundColor Green }
    else     { Write-Host "  [FAIL] $name  $detail" -ForegroundColor Red; $script:errors += $name }
}

Write-Host "=== Master's FM v5.3.0 Final Smoke Test ==="

# 1. Processes up
$procs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @('MastersFM','MastersFM_Tray','server','audio_spectrum') }
$haveTray  = $procs | Where-Object { $_.ProcessName -eq 'MastersFM_Tray' } | Measure-Object | Select-Object -ExpandProperty Count
$haveSrv   = $procs | Where-Object { $_.ProcessName -eq 'server' }         | Measure-Object | Select-Object -ExpandProperty Count
$haveSpec  = $procs | Where-Object { $_.ProcessName -eq 'audio_spectrum' } | Measure-Object | Select-Object -ExpandProperty Count
$haveRoot  = $procs | Where-Object { $_.ProcessName -eq 'MastersFM' }      | Measure-Object | Select-Object -ExpandProperty Count
Check 'MastersFM.exe (launcher)'       ($haveRoot -ge 1)
Check 'MastersFM_Tray.exe (tray host)' ($haveTray -ge 1)
Check 'server.exe (overlay HTTP)'      ($haveSrv  -ge 1)
Check 'audio_spectrum.exe (capture)'   ($haveSpec -ge 1)

# 2. HTTP endpoints
function Probe($url) {
    try { (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3).Content } catch { $null }
}
$overlayOK = Probe 'http://127.0.0.1:4242/'
Check 'overlay HTTP served at /'       ($overlayOK -and $overlayOK.Length -gt 100)
$health = Probe 'http://127.0.0.1:4243/health'
if ($health) {
    $hj = $health | ConvertFrom-Json
    Check 'spectrum /health'           ($hj.ok -eq $true) "backend=$($hj.backend)"
} else { Check 'spectrum /health' $false }

$devs = Probe 'http://127.0.0.1:4243/devices'
if ($devs) {
    $dj = $devs | ConvertFrom-Json
    $nBackends = ($dj.devices | Group-Object backend | Measure-Object | Select-Object -ExpandProperty Count)
    Check 'spectrum /devices (>=3 backends)' ($nBackends -ge 3) "n_backends=$nBackends n_devices=$($dj.devices.Count)"
} else { Check 'spectrum /devices' $false }

# 3. Installed bundle
$bundleDir = Join-Path $env:USERPROFILE 'Desktop\MastersFM_Installer'
$bundleItems = Get-ChildItem $bundleDir -File -ErrorAction SilentlyContinue
Check 'Desktop bundle exists' ($bundleItems.Count -eq 3) "n_files=$($bundleItems.Count)"
foreach ($item in $bundleItems) { Write-Host "      $($item.Name) ($([int]($item.Length/1KB)) KB)" }

# 4. Installed runtime folder has the new NAudio DLLs
$rt = 'C:\Users\Master\AppData\Local\MastersFM'
foreach ($dll in @('NAudio.WinMM.dll','NAudio.Asio.dll','Microsoft.Win32.Registry.dll','System.Buffers.dll','System.Memory.dll','System.Security.AccessControl.dll')) {
    Check "runtime DLL: $dll" (Test-Path (Join-Path $rt $dll))
}

# 5. Version strings match in key files
$trayRaw = Get-Content 'F:\Claude AI\Master FM\tray.ps1' -Raw
$trayVer = if ($trayRaw -match '\$script:APP_VERSION\s*=\s*"([^"]+)"') { $Matches[1] } else { '?' }
Check 'tray.ps1 APP_VERSION = v5.3.0' ($trayVer -eq 'v5.3.0') "actual='$trayVer'"

Write-Host ""
if ($errors.Count -eq 0) {
    Write-Host "ALL CHECKS PASSED." -ForegroundColor Green
    exit 0
} else {
    Write-Host "FAILURES: $($errors -join ', ')" -ForegroundColor Red
    exit 1
}
