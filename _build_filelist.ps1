# STEP 1 per strict audit procedure: enumerate every first-party code file,
# excluding node_modules / _BACKUPS / .git and the strict extension whitelist.
$root = 'F:\Claude AI\Master FM'
$exts = @('.cs','.js','.ps1','.psm1','.py','.html','.css','.json','.bat','.cmd','.xml','.yaml','.yml')

$files = Get-ChildItem -Path $root -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '\\node_modules\\|\\_BACKUPS_|\\\.git\\' -and
        $exts -contains $_.Extension
    } |
    Sort-Object FullName

$md = @()
$md += '# Audit File List (frozen per STEP 1)'
$md += ''
$md += ('Total files: ' + $files.Count)
$md += ''
$md += '| # | File path (relative) | Size |'
$md += '|---|----------------------|------|'
$i = 0
foreach ($f in $files) {
    $i++
    $rel = $f.FullName.Substring($root.Length + 1)
    $md += ('| {0} | {1} | {2} |' -f $i, $rel, $f.Length)
}

$md | Set-Content -Path 'F:\Claude AI\Master FM\AUDIT_FILELIST.md' -Encoding utf8

Write-Host "Total files: $($files.Count)"
Write-Host "Written: F:\Claude AI\Master FM\AUDIT_FILELIST.md"
