$r = Invoke-WebRequest -Uri 'http://127.0.0.1:4243/devices' -UseBasicParsing -TimeoutSec 3
$j = $r.Content | ConvertFrom-Json
Write-Host "Total devices: $($j.devices.Count)"
Write-Host "Current backend: $($j.backend)"
Write-Host "Current device:  '$($j.current)'"
Write-Host "Default render:  $($j.default)"
Write-Host "Default capture: $($j.defaultCapture)"
Write-Host ""
Write-Host "By backend:"
$j.devices | Group-Object backend | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name): $($_.Count) device(s)"
    foreach ($d in $_.Group) {
        Write-Host "     - [$($d.type)] $($d.name)"
    }
}
