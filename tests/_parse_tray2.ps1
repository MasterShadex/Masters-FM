try {
    $sb = [scriptblock]::Create((Get-Content 'F:\Claude AI\Master FM\tray.ps1' -Raw))
    Write-Host "tray.ps1 scriptblock created OK - parses cleanly"
    exit 0
} catch {
    Write-Host "PARSE FAIL: $($_.Exception.Message)"
    Write-Host "Offset: $($_.Exception.ErrorRecord.InvocationInfo.ScriptLineNumber)"
    exit 1
}
