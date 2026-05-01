$d = [Environment]::GetFolderPath('LocalApplicationData') + '\MastersFM'
$log = Join-Path $d 'overlay.log'
if (-not (Test-Path $log)) { Write-Host 'missing'; exit 1 }
Write-Host "=== Latest Browser SMTC (last 8) ==="
Get-Content $log | Where-Object { $_ -match 'Browser SMTC' } | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "=== DIAG lines (last 5) ==="
Get-Content $log | Where-Object { $_ -match 'DIAG' } | Select-Object -Last 5 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "=== Errors (last 5) ==="
Get-Content $log | Where-Object { $_ -match '!! ERROR|!! SLOW TICK|threw:|browser threw' } | Select-Object -Last 5 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "=== Tick stats (last 2) ==="
Get-Content $log | Where-Object { $_ -match 'Tick stats' } | Select-Object -Last 2 | ForEach-Object { Write-Host $_ }
