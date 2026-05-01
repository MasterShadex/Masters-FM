# End-to-end verify run: smoke + customize + deep health + version check + startup.
# ASCII only — PowerShell 5.1 reads scripts as Latin-1 unless BOM is present,
# so sprinkling em-dashes/smart-quotes into a non-BOM file breaks parsing.
$root = 'F:\Claude AI\Master FM'

Write-Host "=========================================="
Write-Host "Masters FM - full verify $(Get-Date -Format 'HH:mm:ss')"
Write-Host "=========================================="

Write-Host ""
Write-Host ">>> SMOKE"
$histDir = Join-Path $env:APPDATA 'MastersFM'
if (-not (Test-Path $histDir)) { New-Item -ItemType Directory -Path $histDir -Force | Out-Null }
& (Join-Path $root '_smoke.ps1') -OutFile (Join-Path $histDir 'smoke_history.log')

Write-Host ""
Write-Host ">>> CUSTOMIZE PROBE"
& (Join-Path $root '_probe_customize.ps1')

Write-Host ""
Write-Host ">>> VERSIONS"
& (Join-Path $root '_check_versions.ps1')

Write-Host ""
Write-Host ">>> STARTUP SHORTCUT"
& (Join-Path $root '_check_startup.ps1')

Write-Host ""
Write-Host ">>> DEEP HEALTH"
& (Join-Path $root '_deep_health.ps1')

Write-Host ""
Write-Host ">>> LOG ERRORS"
& (Join-Path $root '_log_errors.ps1')

Write-Host ""
Write-Host "=========================================="
Write-Host "VERIFY DONE"
Write-Host "=========================================="
