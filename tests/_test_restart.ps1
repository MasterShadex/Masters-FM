# Simulate the Restart flow end-to-end and prove the tray icon comes back.
# Kills the running instance the same way the Restart menu item does, then
# watches for MastersFM_Tray.exe to reappear within a reasonable window.
Write-Host "=== Restart flow test ==="

# Baseline PIDs
$before = @{}
foreach ($n in 'MastersFM','MastersFM_Tray','server','audio_spectrum') {
    $p = Get-Process -Name $n -ErrorAction SilentlyContinue
    if ($p) { $before[$n] = ($p | Select-Object -First 1).Id } else { $before[$n] = 0 }
}
Write-Host ("BEFORE   MastersFM={0}  Tray={1}  server={2}  audio={3}" -f `
    $before['MastersFM'], $before['MastersFM_Tray'], $before['server'], $before['audio_spectrum'])

# Fire the EXACT chain the Restart menu item fires (same syntax as tray.ps1).
$exePath = 'C:\Users\Master\AppData\Local\MastersFM\MastersFM.exe'
$killChain =
    'taskkill /F /IM MastersFM_Tray.exe /T >nul 2>&1 & ' +
    'taskkill /F /IM MastersFM.exe /T >nul 2>&1 & ' +
    'taskkill /F /IM audio_spectrum.exe >nul 2>&1 & ' +
    'taskkill /F /IM server.exe >nul 2>&1 & ' +
    'ping -n 4 127.0.0.1 >nul & ' +
    '"' + $exePath + '"'
Start-Process -FilePath "cmd.exe" -ArgumentList "/c $killChain" -WindowStyle Hidden
Write-Host "Restart chain fired; waiting for processes to come back..."

# Wait up to 15 s for ALL four processes to be alive with NEW PIDs.
$deadline = (Get-Date).AddSeconds(15)
$after = @{}
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $allBack = $true
    foreach ($n in 'MastersFM','MastersFM_Tray','server','audio_spectrum') {
        $p = Get-Process -Name $n -ErrorAction SilentlyContinue
        if ($p) {
            $newPid = ($p | Select-Object -First 1).Id
            $after[$n] = $newPid
            if ($newPid -eq $before[$n] -and $before[$n] -ne 0) { $allBack = $false }
        } else {
            $after[$n] = 0
            $allBack = $false
        }
    }
    if ($allBack) { break }
}

Write-Host ("AFTER    MastersFM={0}  Tray={1}  server={2}  audio={3}" -f `
    $after['MastersFM'], $after['MastersFM_Tray'], $after['server'], $after['audio_spectrum'])

# Verdicts
$ok = $true
foreach ($n in 'MastersFM','MastersFM_Tray','server','audio_spectrum') {
    if ($after[$n] -eq 0) {
        Write-Host ("  FAIL: {0} did not come back" -f $n)
        $ok = $false
    } elseif ($before[$n] -ne 0 -and $after[$n] -eq $before[$n]) {
        Write-Host ("  FAIL: {0} kept the same PID (not restarted)" -f $n)
        $ok = $false
    } else {
        Write-Host ("  OK  : {0} restarted (PID {1} -> {2})" -f $n, $before[$n], $after[$n])
    }
}

if ($ok) {
    Write-Host ""
    Write-Host "=== PASS: Restart brought all four processes back ==="
    # Also hit /health to prove the server is actually serving on 4243
    try {
        $h = (Invoke-WebRequest 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
        Write-Host ("/health: backend={0} device='{1}'" -f $h.backend, $h.device)
    } catch {
        Write-Host "/health failed: $_"
    }
} else {
    Write-Host ""
    Write-Host "=== FAIL: one or more processes missing ==="
    Write-Host "Last 40 lines of launcher.log:"
    Get-Content "$env:LOCALAPPDATA\MastersFM\launcher.log" -Tail 40
    Write-Host ""
    Write-Host "Last 40 lines of host.log (tray_launcher):"
    Get-Content "$env:LOCALAPPDATA\MastersFM\host.log" -Tail 40
}
