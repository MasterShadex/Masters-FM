Start-Sleep -Seconds 8
Write-Host "Processes:"
Get-Process MastersFM,MastersFM_Tray,server,audio_spectrum -ErrorAction SilentlyContinue |
    Select-Object Name,Id | Format-Table -AutoSize
Write-Host ""
Write-Host "/current payload:"
try {
    (Invoke-WebRequest 'http://127.0.0.1:4242/current' -UseBasicParsing -TimeoutSec 3).Content
} catch { "fetch failed: $_" }
Write-Host ""
Write-Host "Recent overlay.log tail (position / override lines):"
Get-Content "$env:LOCALAPPDATA\MastersFM\overlay.log" -Tail 40 |
    Select-String -Pattern 'Position refresh|soundcloud-rpc|overriding SMTC' |
    Select-Object -Last 10 |
    ForEach-Object { $_.Line }
