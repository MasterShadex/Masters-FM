# Directly test ASIO VB-Matrix VASIO-32 channel 5-6 capture via HTTP.
# The test:
#   1. Switch the running audio_spectrum to ASIO VASIO-32 channels 5-6
#   2. Wait 5 s for the ASIO driver to initialize and deliver buffers
#   3. Read /peak to see if anything actually arrives on those channels
#   4. Also read /health to confirm backend didn't fall back to wasapi

Write-Host "1. POST /set-device backend=asio id='VB-Matrix VASIO-32|4' (channels 5-6)"
try {
    $resp = Invoke-WebRequest -Uri 'http://127.0.0.1:4243/set-device' -Method POST `
        -Body '{"backend":"asio","id":"VB-Matrix VASIO-32|4"}' `
        -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5
    Write-Host ("   response: " + $resp.Content)
} catch {
    Write-Host ("   FAILED: " + $_.Exception.Message)
    exit 1
}

Write-Host ""
Write-Host "2. Waiting 5 s for ASIO init + buffers..."
Start-Sleep -Seconds 5

Write-Host ""
Write-Host "3. Peak readings (sampling every 1 s for 10 s):"
for ($i = 1; $i -le 10; $i++) {
    try {
        $p = (Invoke-WebRequest -Uri 'http://127.0.0.1:4243/peak' -UseBasicParsing -TimeoutSec 2).Content | ConvertFrom-Json
        $h = (Invoke-WebRequest -Uri 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 2).Content | ConvertFrom-Json
        Write-Host ("   [{0,2}s] backend={1}  device='{2}'  rolling={3}  lifetime={4}" -f `
            $i, $h.backend, $h.device, $p.rolling, $p.lifetime)
    } catch {
        Write-Host ("   [{0,2}s] read failed: {1}" -f $i, $_.Exception.Message)
    }
    Start-Sleep -Seconds 1
}

Write-Host ""
Write-Host "4. Tail of audio_spectrum.log (last 20 lines):"
Get-Content 'C:\Users\Master\AppData\Local\MastersFM\audio_spectrum.log' -Tail 20
