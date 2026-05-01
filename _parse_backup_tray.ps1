$backupTray = 'F:\Claude AI\_BACKUPS_2026-04-22_01-14\Master FM_SOURCE_BACKUP\tray.ps1'
try {
    $sb = [scriptblock]::Create((Get-Content $backupTray -Raw))
    Write-Host "backup tray.ps1 parses cleanly"
    exit 0
} catch {
    Write-Host "BACKUP tray.ps1 PARSE FAIL: $($_.Exception.Message)"
    exit 1
}
