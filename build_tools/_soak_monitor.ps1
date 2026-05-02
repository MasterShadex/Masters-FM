param([string]$LogFile = "G:\Project Folder\Master FM\V1121_SOAK_LOG.txt")
$start = Get-Date
"SOAK START: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Out-File $LogFile -Encoding UTF8

for ($i = 0; $i -le 6; $i++) {
    if ($i -gt 0) { Start-Sleep -Seconds 300 }  # 5 min intervals
    $elapsed = [Math]::Round((Get-Date - $start).TotalMinutes, 1)
    $t = Get-Process MastersFM_Tray -ErrorAction SilentlyContinue
    if ($t) {
        $ws = [Math]::Round($t.WorkingSet64/1MB, 1)
        $priv = [Math]::Round($t.PrivateMemorySize64/1MB, 1)
        $logPath = "C:\Users\Master\AppData\Local\MastersFM\transcript.log"
        $canary = (Get-Content $logPath -Tail 200 | Where-Object { $_ -match 'CANARY' } | Select-Object -Last 1)
        $line = "T+${elapsed}min | WS=${ws}MB Priv=${priv}MB | $canary"
        $line | Out-File $LogFile -Append -Encoding UTF8
        Write-Host $line
    } else {
        "T+${elapsed}min | TRAY NOT RUNNING" | Out-File $LogFile -Append -Encoding UTF8
        Write-Host "T+${elapsed}min | TRAY NOT RUNNING"
    }
}
"SOAK END: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Out-File $LogFile -Append -Encoding UTF8
Write-Host "SOAK COMPLETE"
