# Test switching the capture backend live via /set-device and
# verify /health reports the new backend.
function Test-Backend($backend, $id, $label) {
    Write-Host ""
    Write-Host "--- Testing: $label (backend=$backend, id=$id) ---"
    $body = '{"backend":"' + $backend + '","id":"' + $id + '"}'
    try {
        $resp = Invoke-WebRequest -Uri 'http://127.0.0.1:4243/set-device' -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5
        Write-Host "  set-device response: $($resp.Content)"
    } catch {
        Write-Host "  set-device FAILED: $($_.Exception.Message)"
        return
    }
    Start-Sleep -Seconds 3
    try {
        $h = (Invoke-WebRequest -Uri 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
        Write-Host "  /health: backend=$($h.backend) device='$($h.device)' frame=$($h.frame)"
        $p = (Invoke-WebRequest -Uri 'http://127.0.0.1:4243/peak' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
        Write-Host "  /peak: rolling=$($p.rolling) lifetime=$($p.lifetime)"
    } catch {
        Write-Host "  read FAILED: $($_.Exception.Message)"
    }
}

# 1. Back to WASAPI loopback default (baseline)
Test-Backend 'wasapi_loopback' 'default' 'WASAPI Loopback (System Default)'
# 2. MME Wave Mapper
Test-Backend 'mme' '-1' 'MME Wave Mapper'
# 3. ASIO - pick VB-Matrix since user has it
Test-Backend 'asio' 'VB-Matrix VASIO-128' 'ASIO VB-Matrix'
# 4. Put it back to WASAPI default for normal operation
Test-Backend 'wasapi_loopback' 'default' 'WASAPI Loopback (restore)'
