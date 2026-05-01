$msi = "F:\Claude AI\Master FM\Master's FM Install\MastersFM_Setup.msi"
$log = "F:\Claude AI\Master FM\logs\rebuild_latest.log"
if (Test-Path $msi) {
    $i = Get-Item $msi
    Write-Host ("MSI OK: {0}  size={1:N0} bytes  mtime={2}" -f $i.FullName, $i.Length, $i.LastWriteTime)
} else {
    Write-Host "MSI MISSING: $msi"
}
Write-Host ""
Write-Host "=== Last 40 lines of rebuild_latest.log ==="
if (Test-Path $log) { Get-Content $log -Tail 40 } else { Write-Host "no log" }
Write-Host ""
Write-Host "=== tray.ps1 running? ==="
Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" |
    Where-Object { $_.CommandLine -match 'tray\.ps1' } |
    ForEach-Object { "[$($_.ProcessId)] $($_.CommandLine)" }
Write-Host ""
Write-Host "=== APP_VERSION check ==="
$src = 'F:\Claude AI\Master FM\tray.ps1'
$dst = "$env:LOCALAPPDATA\MastersFM\tray.ps1"
"SOURCE : " + ((Select-String -Path $src -Pattern 'APP_VERSION\s*=' | Select-Object -First 1).Line)
if (Test-Path $dst) { "DEPLOY : " + ((Select-String -Path $dst -Pattern 'APP_VERSION\s*=' | Select-Object -First 1).Line) } else { "DEPLOY : missing" }
