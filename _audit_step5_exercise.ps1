$ErrorActionPreference = 'Continue'
Write-Host "=== STEP 5 exercise endpoints ==="

function Hit {
    param([string]$name, [string]$url)
    try {
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5
        Write-Host ("  " + $name + ": " + $r.StatusCode + " (" + $r.Content.Length + " bytes)")
    } catch {
        Write-Host ("  " + $name + ": FAILED - " + $_.Exception.Message)
    }
}

Hit '/health    (audio_spectrum)' 'http://127.0.0.1:4243/health'
Hit '/peak      (audio_spectrum)' 'http://127.0.0.1:4243/peak'
Hit '/devices   (audio_spectrum)' 'http://127.0.0.1:4243/devices'
Hit '/current   (server)'          'http://127.0.0.1:4242/current'
Hit '/version   (server)'          'http://127.0.0.1:4242/version'
Hit '/overlay-config (server)'     'http://127.0.0.1:4242/overlay-config'
Hit '/customize (server)'          'http://127.0.0.1:4242/customize'
Hit '/list-presets (server)'       'http://127.0.0.1:4242/list-presets'
Hit '/art?t=0  (server)'           'http://127.0.0.1:4242/art?t=0'

Write-Host ""
Write-Host "Exercise complete."
