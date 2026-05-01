$d = [Environment]::GetFolderPath('LocalApplicationData') + '\MastersFM'
$log = Join-Path $d 'overlay.log'
Write-Host "=== Browser SMTC (first 5) ==="
Get-Content $log | Where-Object { $_ -match 'Browser SMTC' } | Select-Object -First 5 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "=== Browser SMTC (last 5) ==="
Get-Content $log | Where-Object { $_ -match 'Browser SMTC' } | Select-Object -Last 5 | ForEach-Object { Write-Host $_ }
