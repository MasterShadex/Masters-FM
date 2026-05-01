# Verify every row of AUDIT_CHECKLIST.md has every column filled (no blanks, no PENDING).
$lines = Get-Content 'F:\Claude AI\Master FM\AUDIT_CHECKLIST.md'
$rows = $lines | Where-Object { $_ -match '^\| \d+ \|' }
$total = $rows.Count
$bad = 0
$problems = @()
foreach ($r in $rows) {
    # Expect 9 pipes (8 columns + surrounding pipes)
    $cols = ($r -split '\|')
    # Columns: 0='' (before first |), 1-8 = data, 9 = '' (after last |)
    if ($cols.Count -ne 10) { $bad++; $problems += "Column count off: $r"; continue }
    for ($i = 1; $i -le 8; $i++) {
        $c = $cols[$i].Trim()
        if (-not $c) { $bad++; $problems += "Row $($cols[1].Trim()) col $i empty"; break }
        if ($c -eq 'PENDING') { $bad++; $problems += "Row $($cols[1].Trim()) col $i = PENDING"; break }
    }
}
Write-Host "Rows: $total"
Write-Host "Problems: $bad"
if ($problems) { $problems | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" } }
if ($bad -eq 0) { Write-Host "ALL ROWS COMPLETE" -ForegroundColor Green }
