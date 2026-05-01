# Switch audio_spectrum capture to System Default + update Roaming config
# so the user's visualizer works immediately when they come back from AFK.
# VB-Audio Matrix endpoints don't expose audible audio to WASAPI loopback
# (verified via peak-scan: all 0.0000 except Analogue 1/2 = 0.2783), so
# the user's saved Media selection silently makes the visualizer flat.
# "default" follows whatever Windows is using (Analogue 1/2 in their case).

# 1. Runtime switch via /set-device.
$body = '{"id":"default"}'
Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null
Write-Host "Runtime switched to System Default."

# 2. Persist to Roaming config so the next boot of audio_spectrum picks
#    default too.  Read-modify-write to preserve all other fields.
$cfg = Join-Path $env:APPDATA 'MastersFM\config.json'
$j   = Get-Content $cfg -Raw | ConvertFrom-Json
$j   | Add-Member -NotePropertyName audioSpectrumDevice -NotePropertyValue 'default' -Force
$noBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($cfg, ($j | ConvertTo-Json -Depth 10), $noBom)
Write-Host "Persisted audioSpectrumDevice=default in Roaming config."

Start-Sleep 5
$peak = (Invoke-WebRequest 'http://127.0.0.1:4243/peak' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
Write-Host ("Peak now: rolling=$($peak.rolling)  lifetime=$($peak.lifetime)  device=$($peak.device)")
