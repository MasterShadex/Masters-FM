# "Natural" endpoint exercise: no intentionally-broken requests. Hit every
# normal endpoint and cycle through the supported WASAPI Loopback default.
# If any [DIAG] fires during this, it's a real issue.
function Hit($url) {
    try { Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5 | Out-Null; Write-Host "  OK   $url" }
    catch { Write-Host "  ERR  $url  ($($_.Exception.Message))" }
}

Write-Host "=== Overlay server (4242) ==="
Hit 'http://127.0.0.1:4242/'
Hit 'http://127.0.0.1:4242/version'
Hit 'http://127.0.0.1:4242/current'
Hit 'http://127.0.0.1:4242/overlay-config'
Hit 'http://127.0.0.1:4242/list-presets'

Write-Host "=== Spectrum server (4243) ==="
Hit 'http://127.0.0.1:4243/health'
Hit 'http://127.0.0.1:4243/devices'
Hit 'http://127.0.0.1:4243/peak'

# Natural-only backend activity: stay on wasapi_loopback which is the default.
Write-Host "=== Staying on wasapi_loopback default ==="
try {
    Invoke-WebRequest -Uri 'http://127.0.0.1:4243/set-device' -Method POST `
        -Body '{"backend":"wasapi_loopback","id":"default"}' -ContentType 'application/json' `
        -UseBasicParsing -TimeoutSec 5 | Out-Null
    Write-Host "  OK"
} catch { Write-Host "  ERR  $($_.Exception.Message)" }
Write-Host "Done."
