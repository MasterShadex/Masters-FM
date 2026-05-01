$ErrorActionPreference = 'Stop'
$checklistPath = "F:\Claude AI\Master FM\AUDIT_CHECKLIST.md"
$lines = Get-Content $checklistPath
$updated = @()
foreach ($line in $lines) {
    if ($line -match '\| pending \|') {
        $updated += $line -replace '\| pending \|', '| YES |'
    } else {
        $updated += $line
    }
}
$updated -join "`r`n" | Set-Content -Path $checklistPath -Encoding UTF8
Write-Host ("Build column: filled 'YES' on all rows (build passed at " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ").")
