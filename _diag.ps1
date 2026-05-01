Write-Host "=== Running tray processes ==="
Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" |
    Where-Object { $_.CommandLine -match 'tray\.ps1' } |
    ForEach-Object { Write-Host "[$($_.ProcessId)] $($_.CommandLine)" }

Write-Host "`n=== APP_VERSION from both tray.ps1 copies ==="
$src = 'F:\Claude AI\Master FM\tray.ps1'
$dst = "$env:LOCALAPPDATA\MastersFM\tray.ps1"
Write-Host ("SOURCE : " + ((Select-String -Path $src -Pattern 'APP_VERSION\s*=' | Select-Object -First 1).Line))
Write-Host ("DEPLOYED: " + ((Select-String -Path $dst -Pattern 'APP_VERSION\s*=' | Select-Object -First 1).Line))
Write-Host ("SOURCE mtime  : " + (Get-Item $src).LastWriteTime)
Write-Host ("DEPLOY mtime  : " + (Get-Item $dst).LastWriteTime)

Write-Host "`n=== Last 60 lines of overlay.log ==="
$log = "$env:LOCALAPPDATA\MastersFM\overlay.log"
if (Test-Path $log) {
    Get-Content $log -Tail 60
} else {
    Write-Host "NO LOG at $log"
}

Write-Host "`n=== /current ==="
try { (Invoke-RestMethod 'http://localhost:4242/current' -TimeoutSec 3) | ConvertTo-Json -Depth 5 } catch { "server not reachable: $_" }
