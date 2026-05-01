$cfg = (Invoke-WebRequest 'http://127.0.0.1:4242/overlay-config' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
Write-Host "Top-level keys returned by /overlay-config:"
Write-Host ("  " + ($cfg.PSObject.Properties.Name -join ', '))
Write-Host ""
Write-Host "dynamicColors:"
$cfg.dynamicColors | ConvertTo-Json -Depth 3
Write-Host ""
Write-Host "card.positionInSource:"
Write-Host ("  " + $cfg.card.positionInSource)
Write-Host ""
Write-Host "border.enabled:"
Write-Host ("  " + $cfg.border.enabled)
