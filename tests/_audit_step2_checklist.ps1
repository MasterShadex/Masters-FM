$ErrorActionPreference = 'Stop'
$srcRoot = "F:\Claude AI\Master FM"
$codeExt = @('.cs','.js','.ps1','.psm1','.py','.html','.css','.json','.bat','.cmd','.xml','.yaml','.yml')

$all = Get-ChildItem -Path $srcRoot -Recurse -File -ErrorAction SilentlyContinue |
       Where-Object { $_.FullName -notmatch '\\node_modules\\|\\_BACKUPS_|\\\.git\\|\\logs\\|\\bin\\|\\obj\\|\\dist\\' } |
       Where-Object { $codeExt -contains $_.Extension.ToLower() } |
       Sort-Object FullName

$lines = @()
$lines += "# Audit Checklist"
$lines += ""
$lines += "Total files to audit: $($all.Count)"
$lines += "Files completed: 0 / $($all.Count)"
$lines += ""
$lines += "| # | File path | Static review | Catch blocks found | Catch blocks instrumented | Catch blocks intentionally left silent (with reason) | Build passes | Notes |"
$lines += "|---|-----------|---------------|-------------------|--------------------------|-------------------------------------------------|--------------|-------|"

$i = 0
foreach ($f in $all) {
    $i++
    $rel = $f.FullName.Substring($srcRoot.Length + 1)
    $lines += ("| " + $i + " | ``" + $rel + "`` |  |  |  |  |  |  |")
}

$out = "F:\Claude AI\Master FM\AUDIT_CHECKLIST.md"
$lines -join "`r`n" | Set-Content -Path $out -Encoding UTF8
Write-Host ("Wrote " + $all.Count + " rows to " + $out)
