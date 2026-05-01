# Quick verify that /devices enumerates expected ASIO channel pairs after
# the v5.3.0 rebuild and prints the current backend/device for sanity.
$d = (Invoke-WebRequest 'http://127.0.0.1:4243/devices' -UseBasicParsing -TimeoutSec 5).Content | ConvertFrom-Json
Write-Host ("total devices = " + $d.devices.Count)
Write-Host ""
Write-Host "by backend:"
$d.devices | Group-Object backend | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-18}  {1}" -f $_.Name, $_.Count)
}

Write-Host ""
Write-Host "ASIO VB-Matrix VASIO-32 entries:"
$d.devices | Where-Object { $_.backend -eq 'asio' -and $_.id -like 'VB-Matrix VASIO-32*' } | ForEach-Object {
    Write-Host ("  id={0,-32}  name={1}" -f $_.id, $_.name)
}

Write-Host ""
$h = (Invoke-WebRequest 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
$p = (Invoke-WebRequest 'http://127.0.0.1:4243/peak'   -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
Write-Host ("health : backend={0}  device='{1}'  frame={2}" -f $h.backend, $h.device, $h.frame)
Write-Host ("peak   : rolling={0}  lifetime={1}  device='{2}'" -f $p.rolling, $p.lifetime, $p.device)
