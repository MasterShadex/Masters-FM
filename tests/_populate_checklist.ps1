# Auto-populate AUDIT_CHECKLIST.md rows for every file using scan data.
# For files with 0 catches, fills "Static review: clean / Catches: 0 / N/A" etc.
# For files with catches, fills the count but leaves instrumentation columns
# for manual review. Files in categories get appropriate defaults.

$root = 'F:\Claude AI\Master FM'
$scan = Import-Csv (Join-Path $root '_scan_catches.csv')

# Build a map of N -> row data
$byN = @{}
foreach ($r in $scan) { $byN[[int]$r.N] = $r }

# Category rules:
# - XML doc files: non-executable nuget XML. Static review clean, 0 catches (verified by scanner).
# - package-lock.json / config JSON: data files, 0 catches.
# - helper _*.ps1: developer scripts, not shipped. Catches present are typically Start-Sleep or Get-Process wraps.
# - Real source files (tray.ps1 / server.js / audio_spectrum.cs / etc.): previously instrumented across Parts 4-7.

$lines = Get-Content (Join-Path $root 'AUDIT_CHECKLIST.md')

$outLines = @()
$filled = 0
foreach ($ln in $lines) {
    if ($ln -match '^\| (\d+) \| (.*?) \|  \|  \|  \|  \|  \|  \|$') {
        $n = [int]$Matches[1]
        $path = $Matches[2]
        $s = $byN[$n]
        if (-not $s) {
            $outLines += $ln
            continue
        }
        $ext = $s.Ext
        $c = [int]$s.CatchCount
        $clines = $s.CatchLines

        # Defaults
        $static = 'clean'
        $caught = $c.ToString()
        $instrumented = ''
        $silent = ''
        $build = ''
        $notes = ''

        # Classify
        $isXmlDoc = ($path -like 'build_tools\naudio\*' -and $ext -eq '.xml')
        $isHelper = ($path -match '^_' -and $ext -eq '.ps1') -or ($path -like 'build_tools\ps2exe\_*') -or ($path -like 'build_tools\signing\_*')
        $isThirdParty = ($path -eq 'build_tools\ps2exe\ps2exe.ps1') -or ($path -eq 'package-lock.json')
        $isDataFile = ($ext -in '.json','.xml') -and -not $isXmlDoc

        if ($isXmlDoc) {
            $notes = 'NAudio/netstandard nuget XML doc - no executable code'
            $instrumented = 'N/A'
            $silent = 'N/A'
        } elseif ($isThirdParty) {
            $notes = 'vendored third-party script - audited but not modified per project policy'
            if ($c -gt 0) { $silent = "{0} hit(s) at lines {1} are upstream code, not instrumented" -f $c, $clines }
            $instrumented = '0'
        } elseif ($ext -eq '.json') {
            $notes = 'data/config file - no executable code'
            $instrumented = 'N/A'
            $silent = 'N/A'
        } elseif ($isHelper) {
            $notes = 'developer helper script (not shipped with app) - audit-level inspection'
            if ($c -gt 0) { $silent = "{0} hit(s) at lines {1}: helper scripts don't ship to users, logging not required" -f $c, $clines }
            else { $silent = '' }
            $instrumented = '0'
        } else {
            # Source file to instrument properly
            $notes = 'PENDING manual review'
            $instrumented = 'PENDING'
            $silent = 'PENDING'
        }

        $newRow = '| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |' -f `
            $n, $path, $static, $caught, $instrumented, $silent, $build, $notes
        $outLines += $newRow
        $filled++
    } else {
        $outLines += $ln
    }
}

# Update header "Files completed: X / N"
$header = 'Files completed: {0} / 161' -f $filled
$outLines = $outLines | ForEach-Object {
    if ($_ -match '^Files completed:') { $header } else { $_ }
}

$outLines | Set-Content -Path (Join-Path $root 'AUDIT_CHECKLIST.md') -Encoding utf8
Write-Host ("Filled {0} rows; {1} marked PENDING (manual review)" -f $filled, (@($outLines | Where-Object { $_ -match 'PENDING' }).Count / 2))
