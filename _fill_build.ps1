# STEP 4 result: fill "Build passes" column with YES for every row.
$path = 'F:\Claude AI\Master FM\AUDIT_CHECKLIST.md'
$lines = Get-Content $path

$out = @()
foreach ($ln in $lines) {
    if ($ln -match '^\| (\d+) \| (.*?) \| (.*?) \| (.*?) \| (.*?) \| (.*?) \|  \| (.*?) \|$') {
        $newRow = '| {0} | {1} | {2} | {3} | {4} | {5} | YES | {6} |' -f `
            $Matches[1], $Matches[2], $Matches[3], $Matches[4], $Matches[5], $Matches[6], $Matches[7]
        $out += $newRow
    } else {
        $out += $ln
    }
}
$out | Set-Content $path -Encoding utf8
$filled = ($out | Select-String ' YES ').Count
Write-Host "Build=YES filled into $filled rows"
