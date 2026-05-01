# Customize-pipeline probe — exercises every HTTP endpoint the customize UI
# and the overlay use, so regressions surface without the user having to open OBS.
function Try-Endpoint($name, $url, $method, $body) {
    try {
        $p = @{ Uri = $url; Method = $method; TimeoutSec = 4; UseBasicParsing = $true }
        if ($body) { $p.Body = $body; $p.ContentType = "application/json" }
        $r = Invoke-WebRequest @p
        Write-Host ("[PASS] " + $name.PadRight(30) + " HTTP " + $r.StatusCode)
        return $r
    } catch {
        Write-Host ("[FAIL] " + $name.PadRight(30) + " " + $_.Exception.Message)
        return $null
    }
}

Write-Host "=== CUSTOMIZE-PIPELINE END-TO-END ==="

$r1 = Try-Endpoint "/overlay-config GET" "http://localhost:4242/overlay-config" "GET" $null
if ($r1) {
    $cfg = $r1.Content | ConvertFrom-Json
    Write-Host ("       font=" + $cfg.font + "  opacity=" + $cfg.overlay.opacity)
}

$r2 = Try-Endpoint "/preview-config GET" "http://localhost:4242/preview-config" "GET" $null

$testPreview = @{ overlay = @{ opacity = 0.95 }; title = @{ fontSize = 70 } } | ConvertTo-Json
Try-Endpoint "/preview-config POST" "http://localhost:4242/preview-config" "POST" $testPreview | Out-Null

$r4 = Try-Endpoint "/list-presets" "http://localhost:4242/list-presets" "GET" $null
if ($r4) {
    $presets = $r4.Content | ConvertFrom-Json
    Write-Host ("       presets: " + (@($presets).Count) + " found")
}

$savePayload = @{ name = "_smoketest_preset"; config = @{ font = "Inter" } } | ConvertTo-Json
Try-Endpoint "/save-preset POST" "http://localhost:4242/save-preset" "POST" $savePayload | Out-Null
Try-Endpoint "/load-preset GET" "http://localhost:4242/load-preset?name=_smoketest_preset" "GET" $null | Out-Null

try {
    $r = Invoke-WebRequest "http://localhost:4242/delete-preset?name=_smoketest_preset" -Method DELETE -TimeoutSec 4 -UseBasicParsing
    Write-Host ("[PASS] /delete-preset                HTTP " + $r.StatusCode)
} catch { Write-Host ("[FAIL] /delete-preset                " + $_.Exception.Message) }

Try-Endpoint "/reload-config POST" "http://localhost:4242/reload-config" "POST" "" | Out-Null
Try-Endpoint "/current GET" "http://localhost:4242/current" "GET" $null | Out-Null
Try-Endpoint "/version GET" "http://localhost:4242/version" "GET" $null | Out-Null
Try-Endpoint "/customize GET" "http://localhost:4242/customize" "GET" $null | Out-Null

Write-Host ""
Write-Host "[INFO] SSE /events opens OK?"
try {
    $req = [System.Net.WebRequest]::Create("http://localhost:4242/events")
    $req.Timeout = 2000
    $resp = $req.GetResponse()
    if ($resp.StatusCode -eq 200) { Write-Host "[PASS] /events SSE                   HTTP 200" }
    $resp.Close()
} catch {
    $m = $_.Exception.Message
    Write-Host ("[INFO] /events SSE                   " + $m.Substring(0, [Math]::Min(80, $m.Length)))
}
