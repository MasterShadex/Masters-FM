# STEP 2: Create AUDIT_CHECKLIST.md with one empty row per file from AUDIT_FILELIST.md.
$rows = @()
$files = @()

$content = Get-Content 'F:\Claude AI\Master FM\AUDIT_FILELIST.md'
foreach ($line in $content) {
    if ($line -match '^\| (\d+) \| (.*?) \| (\d+) \|$') {
        $num = [int]$Matches[1]
        $path = $Matches[2]
        $files += [pscustomobject]@{ N = $num; Path = $path }
    }
}

$md = @()
$md += '# Audit Checklist'
$md += ''
$md += ('Total files to audit: {0}' -f $files.Count)
$md += ('Files completed: 0 / {0}' -f $files.Count)
$md += ''
$md += '| # | File path | Static review | Catch blocks found | Catch blocks instrumented | Catch blocks intentionally left silent (with reason) | Build passes | Notes |'
$md += '|---|-----------|---------------|-------------------|--------------------------|-------------------------------------------------|--------------|-------|'
foreach ($f in $files) {
    $md += ('| {0} | {1} |  |  |  |  |  |  |' -f $f.N, $f.Path)
}
$md | Set-Content -Path 'F:\Claude AI\Master FM\AUDIT_CHECKLIST.md' -Encoding utf8
Write-Host ('Written checklist with {0} rows' -f $files.Count)
