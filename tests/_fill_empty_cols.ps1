# Fill any empty cell in any table row with 'N/A'. Run unconditionally over
# every table row.
$path = 'F:\Claude AI\Master FM\AUDIT_CHECKLIST.md'
$lines = Get-Content $path

$out = @()
foreach ($ln in $lines) {
    if ($ln -match '^\| \d+ \|') {
        $parts = $ln -split '\|'
        for ($i = 1; $i -le 8; $i++) {
            if ($i -lt $parts.Count) {
                if ($parts[$i].Trim() -eq '') { $parts[$i] = ' N/A ' }
            }
        }
        $out += ($parts -join '|')
    } else {
        $out += $ln
    }
}
$out | Set-Content $path -Encoding utf8
Write-Host "Empty cells filled with N/A"
