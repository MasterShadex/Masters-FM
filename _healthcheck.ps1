$d = [Environment]::GetFolderPath('LocalApplicationData') + '\MastersFM'
$log = Join-Path $d 'overlay.log'
if (-not (Test-Path $log)) { Write-Host 'missing'; exit 1 }
$raw = Get-Content $log
Write-Host "Total lines:      $($raw.Count)"
$errs = @($raw | Where-Object { $_ -match '!! ERROR|!! SLOW TICK|threw:|browser threw' })
Write-Host "Error-ish lines:  $($errs.Count)"
if ($errs.Count -gt 0 -and $errs.Count -le 20) {
    Write-Host ""
    Write-Host "--- Errors ---"
    $errs | ForEach-Object { Write-Host $_ }
} elseif ($errs.Count -gt 20) {
    Write-Host ""
    Write-Host "--- First 10 errors ---"
    $errs | Select-Object -First 10 | ForEach-Object { Write-Host $_ }
}
Write-Host ""
Write-Host "--- Last 5 DETECT lines ---"
$raw | Where-Object { $_ -match 'DETECT:' } | Select-Object -Last 5 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "--- Last 3 Browser SMTC lines ---"
$raw | Where-Object { $_ -match 'Browser SMTC' } | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "--- Tick stats lines (if any) ---"
$raw | Where-Object { $_ -match 'Tick stats' } | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
