$d = [Environment]::GetFolderPath('LocalApplicationData') + '\MastersFM'
$log = Join-Path $d 'overlay.log'
Write-Host "=== All SLOW TICK lines since relaunch ==="
$raw = Get-Content $log
$lastRelaunch = ($raw | Select-String -Pattern 'Overlay started|APP_VERSION|startup ticks' | Select-Object -Last 1)
$raw | Where-Object { $_ -match '!! SLOW TICK' } | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "=== Tick stats lines ==="
$raw | Where-Object { $_ -match 'Tick stats|tickStats' } | Select-Object -Last 5 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "=== Last 3 Browser SMTC ==="
$raw | Where-Object { $_ -match 'Browser SMTC' } | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "=== File size / age ==="
$fi = Get-Item $log
Write-Host "Size: $($fi.Length) bytes  LastWrite: $($fi.LastWriteTime)"
