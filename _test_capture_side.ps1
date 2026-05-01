# Find the CAPTURE-side Media (VB-Audio) endpoint, switch audio_spectrum to
# it, wait 8 s for audio to start flowing, then report the peak.
$devs = (Invoke-WebRequest 'http://127.0.0.1:4243/devices' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
$mediaCap = $devs.devices | Where-Object { $_.flow -eq 'capture' -and $_.name -match 'Media' } | Select-Object -First 1
if (-not $mediaCap) { Write-Host "CAPTURE-side Media not found"; exit 1 }

Write-Host "Switching to CAPTURE-side: $($mediaCap.name) [$($mediaCap.id)]"
$body = '{"id":"' + $mediaCap.id + '"}'
Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null

Write-Host "Waiting 8 s for live peak..."
Start-Sleep 8
$peak = (Invoke-WebRequest 'http://127.0.0.1:4243/peak' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
Write-Host "Rolling peak (last ~3s): $($peak.rolling)"
Write-Host "Lifetime peak:            $($peak.lifetime)"
if ($peak.lifetime -gt 0.001) {
    Write-Host "SUCCESS: CAPTURE-side Media is delivering real audio!"
} else {
    Write-Host "Still silent on capture side too."
}
