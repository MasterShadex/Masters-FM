# Cycle through every render endpoint, capture 4 seconds each, log peak.
# Goal: find WHICH endpoint in the user's setup actually has audible audio.
$devs = (Invoke-WebRequest 'http://127.0.0.1:4243/devices' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
$render = $devs.devices | Where-Object { $_.flow -eq 'render' }

$results = @()
foreach ($d in $render) {
    Write-Host ""
    Write-Host "Testing render: $($d.name) [$($d.id)]"
    $body = '{"id":"' + $d.id + '"}'
    Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null
    Start-Sleep 5
    $peak = (Invoke-WebRequest 'http://127.0.0.1:4243/peak' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
    Write-Host ("  peak: rolling=$($peak.rolling) lifetime=$($peak.lifetime)")
    $results += [PSCustomObject]@{ Name = $d.name; Peak = $peak.lifetime }
}

Write-Host ""
Write-Host "===== RESULTS ====="
$results | Sort-Object Peak -Descending | Format-Table -AutoSize

$winner = $results | Sort-Object Peak -Descending | Select-Object -First 1
if ($winner -and [float]$winner.Peak -gt 0.001) {
    Write-Host "WINNER: $($winner.Name) has peak $($winner.Peak) - real audio flowing here."
} else {
    Write-Host "NONE of the render endpoints delivered audio via WASAPI loopback."
}
