# Scan every channel pair on VB-Matrix VASIO-32 to localize where audio
# actually flows. This tells us whether NAudio's InputChannelOffset is
# being respected: if pair 1-2 has audio while 5-6 is silent, the offset
# IS working and the user just hasn't routed Media to 5-6. If ALL pairs
# are silent, either routing is wrong or offset is being ignored.
$driver = 'VB-Matrix VASIO-32'
$results = @()
for ($p = 0; $p -lt 16; $p++) {
    $ofs   = $p * 2
    $label = "Ch $($ofs+1)-$($ofs+2)"
    $id    = "$driver|$ofs"
    $body  = '{"backend":"asio","id":"' + $id + '"}'
    try {
        Invoke-WebRequest -Uri 'http://127.0.0.1:4243/set-device' -Method POST `
            -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5 | Out-Null
    } catch {
        Write-Host ("  FAIL set-device for " + $label + ": " + $_.Exception.Message)
        continue
    }
    # Wait for ASIO driver to init + a couple of buffer callbacks to
    # establish a real peak reading. VB-Matrix takes ~1-2 s to settle.
    Start-Sleep -Seconds 3
    try {
        $peak = (Invoke-WebRequest 'http://127.0.0.1:4243/peak' -UseBasicParsing -TimeoutSec 2).Content | ConvertFrom-Json
        $h    = (Invoke-WebRequest 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 2).Content | ConvertFrom-Json
        $marker = if ([double]$peak.lifetime -gt 0.01) { '[LIVE]' } `
                  elseif ([double]$peak.lifetime -gt 0.0005) { '[quiet]' } `
                  else { '[silence]' }
        Write-Host ("{0,-8}  lifetime={1,-8}  rolling={2,-8}  backend={3,-5}  running='{4}'  {5}" -f `
            $label, $peak.lifetime, $peak.rolling, $h.backend, $h.device, $marker)
        $results += [pscustomobject]@{
            Pair = $label; Offset = $ofs; Lifetime = [double]$peak.lifetime; Backend = $h.backend
        }
    } catch {
        Write-Host ("  FAIL read for " + $label + ": " + $_.Exception.Message)
    }
}

Write-Host ""
Write-Host "=== Summary ==="
$live = $results | Where-Object { $_.Lifetime -gt 0.01 }
if ($live) {
    Write-Host "Channels with LIVE audio:"
    foreach ($r in $live) { Write-Host ("  {0}  peak={1}" -f $r.Pair, $r.Lifetime) }
} else {
    Write-Host "NO channel pair showed audio above noise floor."
    Write-Host "This means either VB-Matrix isn't routing Media to any VASIO-32 channels,"
    Write-Host "or NAudio's InputChannelOffset is being ignored by this driver."
    Write-Host ""
    Write-Host "Please confirm in VB-Matrix Routing Grid:"
    Write-Host "  - Source row: Matrix VAIO 3 Media"
    Write-Host "  - Target column: one of the VASIO32 I/O columns"
    Write-Host "  - 0dB cell drawn at the intersection"
}
