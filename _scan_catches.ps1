# STEP 3 scanner helper: for every file in AUDIT_FILELIST.md, count catch-
# block occurrences by appropriate pattern for the file type, and emit
# one CSV row per file with (number, path, ext, lines, catch_count,
# catch_lines). The audit loop reads this + inspects each file's content
# to make instrumentation decisions.

$listPath = 'F:\Claude AI\Master FM\AUDIT_FILELIST.md'
$root     = 'F:\Claude AI\Master FM'
$out      = 'F:\Claude AI\Master FM\_scan_catches.csv'

$files = @()
foreach ($line in Get-Content $listPath) {
    if ($line -match '^\| (\d+) \| (.*?) \| (\d+) \|$') {
        $files += [pscustomobject]@{ N = [int]$Matches[1]; Path = $Matches[2] }
    }
}

$csv = @('N,Path,Ext,Lines,CatchCount,CatchLines')
foreach ($f in $files) {
    $abs = Join-Path $root $f.Path
    $ext = [System.IO.Path]::GetExtension($f.Path).ToLower()
    if (-not (Test-Path $abs)) { continue }

    # Pattern per file type
    $patterns = @()
    switch ($ext) {
        '.cs'    { $patterns = @('catch\s*[({]') }
        '.js'    { $patterns = @('catch\s*[({]','\.catch\(') }
        '.ps1'   { $patterns = @('\}\s*catch\s*[{(]','trap\s*[{(]','-ErrorAction\s+SilentlyContinue','\$ErrorActionPreference\s*=\s*[''"]SilentlyContinue[''"]') }
        '.psm1'  { $patterns = @('\}\s*catch\s*[{(]','trap\s*[{(]','-ErrorAction\s+SilentlyContinue') }
        '.py'    { $patterns = @('except\s*[:(]') }
        '.html'  { $patterns = @('catch\s*[({]','\.catch\(') }
        '.bat'   { $patterns = @('2>&1','2>nul','errorlevel') }
        '.cmd'   { $patterns = @('2>&1','2>nul','errorlevel') }
        default  { $patterns = @() }
    }

    $lineNums = @()
    $count = 0
    if ($patterns.Count -gt 0) {
        $content = Get-Content $abs -ErrorAction SilentlyContinue
        if ($content) {
            $i = 0
            foreach ($ln in $content) {
                $i++
                foreach ($p in $patterns) {
                    if ($ln -match $p) {
                        $count++
                        $lineNums += $i
                        break
                    }
                }
            }
        }
        $totalLines = if ($content) { $content.Count } else { 0 }
    } else {
        $totalLines = (Get-Content $abs -ErrorAction SilentlyContinue).Count
        $count = 0
    }

    $csv += ('{0},"{1}",{2},{3},{4},"{5}"' -f $f.N, $f.Path, $ext, $totalLines, $count, ($lineNums -join ';'))
}
$csv | Set-Content -Path $out -Encoding utf8
Write-Host ("Scan done: " + $out)
Write-Host ("Files with catches: " + ($csv | Select-Object -Skip 1 | Where-Object { $_ -match ',(\d+),' } | Measure-Object).Count)
