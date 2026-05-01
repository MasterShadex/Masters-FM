# Part 7 forced-failure stress tests.
# Drives scenarios that should fire the newly-added instrumentation.
# Read logs after to verify the instrumentation caught them and the
# app recovered.

function Hr() { Write-Host ('-' * 60) }

Hr
Write-Host "TEST 1: Send malformed JSON to /webhook (should 400 + log parse error)"
Hr
try {
    $r = Invoke-WebRequest -Uri 'http://127.0.0.1:4242/webhook' `
        -Method POST -Body '{NOT JSON' -ContentType 'application/json' `
        -UseBasicParsing -TimeoutSec 5 -ErrorAction SilentlyContinue
    Write-Host "  Status: $($r.StatusCode)"
} catch {
    Write-Host "  Status: $($_.Exception.Response.StatusCode.value__) (expected 400)"
}

Hr
Write-Host "TEST 2: Kill audio_spectrum.exe mid-run (overlay should fall back to simulator)"
Hr
$p = Get-Process -Name 'audio_spectrum' -ErrorAction SilentlyContinue
if ($p) {
    Write-Host "  Killing audio_spectrum.exe PID=$($p.Id)"
    $p | Stop-Process -Force
    Write-Host "  Waiting 4s for overlay to notice via SSE disconnect..."
    Start-Sleep -Seconds 4
    Write-Host "  Checking spectrum port is now dead..."
    try {
        Invoke-WebRequest 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 2 | Out-Null
        Write-Host "  WARN: spectrum still reachable, kill didn't stick"
    } catch {
        Write-Host "  OK: spectrum port closed as expected"
    }
} else {
    Write-Host "  audio_spectrum.exe not running"
}

Hr
Write-Host "TEST 3: Request /set-device with NO running spectrum server (tray-side probe)"
Hr
# This simulates what happens if the tray posts to /set-device while
# audio_spectrum is restarting. Expected: connection refused, caller
# catches + logs.
try {
    Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST `
        -Body '{"backend":"wasapi_loopback","id":"default"}' -ContentType 'application/json' `
        -UseBasicParsing -TimeoutSec 2 | Out-Null
    Write-Host "  WARN: request succeeded (spectrum server still up)"
} catch {
    Write-Host "  OK: request failed as expected: $($_.Exception.Message.Substring(0, [Math]::Min(80, $_.Exception.Message.Length)))"
}

Hr
Write-Host "Done. Check logs for diagnostic entries."
