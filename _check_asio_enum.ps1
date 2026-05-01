# Dump the ASIO entries from /devices to verify per-channel enumeration.
$r = (Invoke-WebRequest 'http://127.0.0.1:4243/devices' -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json
$asio = $r.devices | Where-Object { $_.backend -eq 'asio' }
Write-Host ("Total ASIO entries: " + $asio.Count)
Write-Host ""
$asio | Group-Object { ($_.id -split '\|')[0] } | ForEach-Object {
    Write-Host ("{0}  ({1} pair(s))" -f $_.Name, $_.Count)
    foreach ($e in $_.Group) {
        Write-Host ("    id='{0}'  name='{1}'" -f $e.id, $e.name)
    }
}
