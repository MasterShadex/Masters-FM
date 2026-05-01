# Exercise every non-destructive HTTP endpoint + switch backends so instrumentation
# gets a chance to log something across the whole spectrum of code paths.
function Hit($url) {
    try {
        $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
        Write-Host ("  {0,3} {1}" -f $r.StatusCode, $url)
        return $r.Content
    } catch {
        Write-Host ("  ERR {0}  ({1})" -f $url, $_.Exception.Message)
        return $null
    }
}

function HitPost($url, $body) {
    try {
        $r = Invoke-WebRequest -Uri $url -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5
        Write-Host ("  {0,3} POST {1}" -f $r.StatusCode, $url)
        return $r.Content
    } catch {
        Write-Host ("  ERR POST {0}  ({1})" -f $url, $_.Exception.Message)
        return $null
    }
}

Write-Host "=== Overlay server (port 4242) ==="
Hit 'http://127.0.0.1:4242/'             | Out-Null   # overlay.html
Hit 'http://127.0.0.1:4242/version'      | Out-Null
Hit 'http://127.0.0.1:4242/current'      | Out-Null
Hit 'http://127.0.0.1:4242/overlay-config' | Out-Null
Hit 'http://127.0.0.1:4242/list-presets' | Out-Null
Hit 'http://127.0.0.1:4242/MastersFM.ico' | Out-Null
Hit 'http://127.0.0.1:4242/manifest.json' | Out-Null
Hit 'http://127.0.0.1:4242/does-not-exist' | Out-Null  # 404 path

Write-Host ""
Write-Host "=== Spectrum server (port 4243) ==="
Hit 'http://127.0.0.1:4243/health'   | Out-Null
Hit 'http://127.0.0.1:4243/devices'  | Out-Null
Hit 'http://127.0.0.1:4243/peak'     | Out-Null
Hit 'http://127.0.0.1:4243/nonexistent' | Out-Null

Write-Host ""
Write-Host "=== Backend switching (cycle all five, observe fallbacks) ==="
$backends = @(
    @{ backend='wasapi_loopback';   id='default' },
    @{ backend='wasapi_input';      id='default' },
    @{ backend='wasapi_exclusive';  id='default' },
    @{ backend='mme';               id='-1' },
    @{ backend='asio';              id='bogus-driver-name' },  # intentionally bad: exercises fallback
    @{ backend='wasapi_loopback';   id='default' }   # restore
)
foreach ($b in $backends) {
    $body = '{"backend":"' + $b.backend + '","id":"' + $b.id + '"}'
    HitPost 'http://127.0.0.1:4243/set-device' $body | Out-Null
    Start-Sleep -Milliseconds 1500
}

Write-Host ""
Write-Host "=== Client-log relay (posts a fake overlay message) ==="
HitPost 'http://127.0.0.1:4242/client-log' '{"level":"info","msg":"[DIAG-TEST] endpoint exercise script ran"}' | Out-Null

Write-Host ""
Write-Host "Done."
