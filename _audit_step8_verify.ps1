$ErrorActionPreference = 'Stop'
$path = "F:\Claude AI\Master FM\AUDIT_CHECKLIST.md"
$lines = Get-Content $path

# First find the header claim
$headerMatch = $lines | Select-String 'Total files to audit:'
Write-Host ("Header: " + $headerMatch.Line)

# Count data rows (lines starting with | digit )
$rowLines = $lines | Where-Object { $_ -match '^\| \d+ \|' }
$rowCount = $rowLines.Count
Write-Host ("Data rows: " + $rowCount)

# Count rows where any cell is blank or contains obvious placeholder
$incomplete = 0
$badRows = @()
foreach ($r in $rowLines) {
    # Split on pipe, drop leading/trailing empty
    $cells = $r -split '\|' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
    # Expect exactly 8 cells per row (#, File, Static, Catches, Instrumented, Silent, Build, Notes)
    if ($cells.Count -lt 7) { $incomplete++; $badRows += $r; continue }
    $hasBlank = $false
    # A row is incomplete if Static review, Catches found, Instrumented, Silent, or Build is empty
    # The "Notes" column is allowed to be empty
    for ($i = 0; $i -lt 6; $i++) {
        if ($cells[$i] -eq '' -or $cells[$i] -match '^\s*$' -or $cells[$i] -eq 'PENDING' -or $cells[$i] -eq 'pending') {
            $hasBlank = $true; break
        }
    }
    if ($hasBlank) { $incomplete++; $badRows += $r }
}

Write-Host ""
if ($incomplete -eq 0) {
    Write-Host "VERIFICATION PASSED: every row is complete."
} else {
    Write-Host ("VERIFICATION FAILED: " + $incomplete + " incomplete rows:")
    $badRows | Select-Object -First 10 | ForEach-Object { Write-Host ("  " + $_) }
}

# Also verify header claim matches row count
if ($headerMatch.Line -match 'Total files to audit: (\d+)') {
    $claimed = [int]$Matches[1]
    if ($claimed -eq $rowCount) {
        Write-Host ("Header / row count match: " + $claimed)
    } else {
        Write-Host ("MISMATCH: header says " + $claimed + " but found " + $rowCount + " rows")
    }
}

# Verify Files completed matches total
$filesCompletedMatch = $lines | Select-String 'Files completed:'
Write-Host ""
Write-Host ("Header 'Files completed': " + $filesCompletedMatch.Line)
