Start-Sleep -Seconds 8
Write-Host "Processes:"
Get-Process MastersFM,MastersFM_Tray,server,audio_spectrum -ErrorAction SilentlyContinue |
    Select-Object Name,Id | Format-Table -AutoSize
Write-Host ""
Write-Host "Recent overlay.log lines (detection):"
Get-Content "$env:LOCALAPPDATA\MastersFM\overlay.log" -Tail 40 |
    Select-String -Pattern 'non-WASAPI|soundcloud-rpc|Position refresh|DETECT' -SimpleMatch |
    Select-Object -Last 8 | ForEach-Object { $_.Line }
