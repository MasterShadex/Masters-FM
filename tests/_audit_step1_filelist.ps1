$ErrorActionPreference = 'Stop'
$srcRoot = "F:\Claude AI\Master FM"
$codeExt = @('.cs','.js','.ps1','.psm1','.py','.html','.css','.json','.bat','.cmd','.xml','.yaml','.yml')

$all = Get-ChildItem -Path $srcRoot -Recurse -File -ErrorAction SilentlyContinue |
       Where-Object { $_.FullName -notmatch '\\node_modules\\|\\_BACKUPS_|\\\.git\\|\\logs\\|\\bin\\|\\obj\\|\\dist\\' } |
       Where-Object { $codeExt -contains $_.Extension.ToLower() } |
       Sort-Object FullName

$lines = @()
$lines += "# Audit File List"
$lines += ""
$lines += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += "Total code files to audit: $($all.Count)"
$lines += ""
$lines += "| # | File | Ext | Size (bytes) |"
$lines += "|---|------|-----|--------------|"

$i = 0
foreach ($f in $all) {
    $i++
    $rel = $f.FullName.Substring($srcRoot.Length + 1)
    $lines += ("| " + $i + " | " + $rel + " | " + $f.Extension + " | " + $f.Length + " |")
}

$out = "F:\Claude AI\Master FM\AUDIT_FILELIST.md"
$lines -join "`r`n" | Set-Content -Path $out -Encoding UTF8
Write-Host ("Wrote " + $all.Count + " files to " + $out)

# Show breakdown by extension
Write-Host ""
Write-Host "Breakdown by extension:"
$all | Group-Object Extension | Sort-Object Count -Descending |
    ForEach-Object { Write-Host ("  " + $_.Name + " = " + $_.Count) }
