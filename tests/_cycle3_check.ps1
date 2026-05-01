# Cycle-3 check: record log line count, run natural test, diff.
$specLog = 'C:\Users\Master\AppData\Local\MastersFM\audio_spectrum.log'
$before  = if (Test-Path $specLog) { (Get-Content $specLog).Count } else { 0 }
Write-Host "before: $before lines"

& 'F:\Claude AI\Master FM\_exercise_natural.ps1'

Start-Sleep -Seconds 2
$after = (Get-Content $specLog).Count
Write-Host ""
Write-Host "after: $after lines ($($after - $before) new)"
Write-Host ""
Write-Host "NEW LINES SINCE NATURAL RUN:"
Get-Content $specLog | Select-Object -Skip $before | ForEach-Object { Write-Host "  $_" }

Write-Host ""
Write-Host "ANY [DIAG] in the delta?"
$newDiag = Get-Content $specLog | Select-Object -Skip $before | Select-String '\[DIAG\]'
if ($newDiag) { Write-Host "YES - see above"; exit 1 }
else { Write-Host "NO - natural run is clean"; exit 0 }
