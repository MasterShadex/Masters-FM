# RC.3 SHIP-PREP STEP 2: Clean uninstall + fresh install for verification gate
# Run from project root. Takes ~5-10 min due to WMI.

$root = "G:\Project Folder\Master FM"

Write-Host "=== S2.1: Kill all MastersFM processes ==="
$procs = @("MastersFM_Tray","MastersFM","server","audio_spectrum","MastersFM_ObsCleanup")
foreach ($p in $procs) {
    $found = Get-Process $p -ErrorAction SilentlyContinue
    if ($found) {
        Write-Host "  Stopping $p (PID $($found.Id))..."
        Stop-Process -Name $p -Force -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Seconds 2
$stillRunning = $procs | Where-Object { Get-Process $_ -ErrorAction SilentlyContinue }
if ($stillRunning) { Write-Host "  WARNING: still running: $($stillRunning -join ', ')" }
else { Write-Host "  All processes stopped." }

Write-Host ""
Write-Host "=== S2.1: WMI uninstall ==="
Write-Host "  Querying Win32_Product (may take 1-2 min)..."
$wmi = Get-CimInstance Win32_Product -Filter "Name = 'Master''s FM'" -ErrorAction SilentlyContinue
if ($wmi) {
    Write-Host "  Found: $($wmi.Name) $($wmi.Version) {$($wmi.IdentifyingNumber)}"
    Write-Host "  Running msiexec /x ..."
    $msiArgs = "/x `"$($wmi.IdentifyingNumber)`" /qn /norestart"
    $proc = Start-Process msiexec -ArgumentList $msiArgs -Wait -PassThru
    Write-Host "  msiexec exit code: $($proc.ExitCode)"
    Start-Sleep -Seconds 5
    $check = Get-CimInstance Win32_Product -Filter "Name = 'Master''s FM'" -ErrorAction SilentlyContinue
    if ($check) { Write-Host "  WARNING: product still registered after uninstall" }
    else { Write-Host "  Uninstall confirmed: product no longer in Win32_Product" }
} else {
    Write-Host "  Master's FM not found in Win32_Product -- already uninstalled or never installed."
}

Write-Host ""
Write-Host "=== S2.1: Clear AppData/ProgramData ==="
Remove-Item "$env:LOCALAPPDATA\MastersFM\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:PROGRAMDATA\MastersFM\*" -Recurse -Force -ErrorAction SilentlyContinue
$remaining = Get-ChildItem "$env:LOCALAPPDATA\MastersFM" -ErrorAction SilentlyContinue
if ($remaining) { Write-Host "  WARNING: AppData not fully cleared ($($remaining.Count) items remain)" }
else { Write-Host "  AppData\MastersFM: cleared" }

Write-Host ""
Write-Host "=== S2.2: _full_rebuild.ps1 ==="
Write-Host "  Running (may take 2-5 min)..."
Set-Location $root
$rebuildOutput = & "$root\_full_rebuild.ps1" 2>&1
$rebuildOutput | ForEach-Object { Write-Host "  $_" }

$rebuildOk = $rebuildOutput | Where-Object { $_ -match "REBUILD DONE OK|DONE OK|BUILD OK|0 Error" }
if ($rebuildOk) { Write-Host "  REBUILD: PASS" }
else {
    $errors = $rebuildOutput | Where-Object { $_ -match "Error|FAIL|HALT" }
    Write-Host "  REBUILD: may have issues -- check output above"
    if ($errors) { $errors | ForEach-Object { Write-Host "  ! $_" } }
}

Write-Host ""
Write-Host "=== S2.2: git checkout -- version.json ==="
git checkout -- version.json
$verCheck = (Get-Content "$root\version.json" | ConvertFrom-Json).version
Write-Host "  version.json restored: $verCheck"

Write-Host ""
Write-Host "=== S2.3: Verify installed version ==="
$installVer = $null
$installVersionJson = "$env:LOCALAPPDATA\MastersFM\version.json"
if (Test-Path $installVersionJson) {
    $installVer = (Get-Content $installVersionJson | ConvertFrom-Json).version
    Write-Host "  Installed version: $installVer"
    if ($installVer -eq "14.0.0-rc.2") { Write-Host "  PASS: installed = rc.2" }
    else { Write-Host "  FAIL: installed = $installVer (expected 14.0.0-rc.2)" }
} else {
    Write-Host "  WARNING: $installVersionJson not found -- app may not have installed yet"
}

Write-Host ""
Write-Host "=== S2.4: Wait for tray ==="
Start-Sleep -Seconds 15
$tray = Get-Process MastersFM_Tray -ErrorAction SilentlyContinue
if ($tray) { Write-Host "  PASS: MastersFM_Tray running (PID $($tray.Id))" }
else { Write-Host "  WARNING: MastersFM_Tray not running -- may need manual launch" }

$server = Get-Process server -ErrorAction SilentlyContinue
$spectrum = Get-Process audio_spectrum -ErrorAction SilentlyContinue
Write-Host "  server: $(if ($server) { 'RUNNING PID ' + $server.Id } else { 'not running' })"
Write-Host "  audio_spectrum: $(if ($spectrum) { 'RUNNING PID ' + $spectrum.Id } else { 'not running' })"

Write-Host ""
Write-Host "=== STEP 2 COMPLETE ==="
