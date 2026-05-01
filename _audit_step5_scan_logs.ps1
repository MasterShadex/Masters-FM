$ErrorActionPreference = 'Continue'
$logDir = "$env:LOCALAPPDATA\MastersFM"

Write-Host "=== Log files in $logDir ==="
$files = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending
foreach ($f in $files) {
    Write-Host ("  {0}  {1,12}  {2}" -f $f.LastWriteTime.ToString('HH:mm:ss'), $f.Length, $f.Name)
}

# Patterns to flag as issues
$errorPattern = '(?i)\b(error|err|fatal|failed|failure|throw|uncaught|unhandled|exception|FATAL|E:|ERR:|\[ERROR\]|\[E\])\b'
$warnPattern  = '(?i)\b(warn|warning|WARN:|\[WARN\]|\[W\])\b'
$diagPattern  = '\[DIAG\]'

$results = @{}
foreach ($f in $files) {
    $name = $f.Name
    $content = try { Get-Content $f.FullName -ErrorAction Stop } catch { @() }
    $errors = $content | Where-Object { $_ -match $errorPattern -and $_ -notmatch 'ErrorAction SilentlyContinue|error_tracking|Set-ItemProperty.*ErrorAction|Error \s*=\s*\$false|ERR:\s*\[' }
    $warns  = $content | Where-Object { $_ -match $warnPattern  -and $_ -notmatch 'warnings? skipped|false positive' }
    $diags  = $content | Where-Object { $_ -match $diagPattern }
    $results[$name] = @{
        Lines = $content.Count
        Errors = @($errors)
        Warns  = @($warns)
        Diags  = @($diags)
    }
}

Write-Host ""
Write-Host "=== Summary ==="
foreach ($name in $results.Keys | Sort-Object) {
    $r = $results[$name]
    Write-Host ("  {0,-24}  lines={1}  errors={2}  warns={3}  diags={4}" -f
        $name, $r.Lines, $r.Errors.Count, $r.Warns.Count, $r.Diags.Count)
}

# Write the AUDIT_RUNTIME_LOGS.md
$out = "F:\Claude AI\Master FM\AUDIT_RUNTIME_LOGS.md"
$lines = @()
$lines += "# Audit Runtime Logs"
$lines += ""
$lines += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += "Cycle: 1"
$lines += "Log dir: $logDir (read-only per audit rule 6)"
$lines += ""
$lines += "## File overview"
$lines += ""
$lines += "| File | Lines | Errors | Warnings | Diag |"
$lines += "|------|-------|--------|----------|------|"
foreach ($name in $results.Keys | Sort-Object) {
    $r = $results[$name]
    $lines += ("| " + $name + " | " + $r.Lines + " | " + $r.Errors.Count + " | " + $r.Warns.Count + " | " + $r.Diags.Count + " |")
}

# For each log with non-zero errors/warns, dump first 20 of each
$lines += ""
$lines += "## Error / warning samples per file"
$lines += ""
foreach ($name in $results.Keys | Sort-Object) {
    $r = $results[$name]
    if ($r.Errors.Count -eq 0 -and $r.Warns.Count -eq 0) { continue }
    $lines += ("### " + $name)
    $lines += ""
    if ($r.Errors.Count -gt 0) {
        $lines += "**Errors (first 20):**"
        $lines += '```'
        $r.Errors | Select-Object -First 20 | ForEach-Object { $lines += $_ }
        $lines += '```'
        $lines += ""
    }
    if ($r.Warns.Count -gt 0) {
        $lines += "**Warnings (first 20):**"
        $lines += '```'
        $r.Warns | Select-Object -First 20 | ForEach-Object { $lines += $_ }
        $lines += '```'
        $lines += ""
    }
}

$lines -join "`r`n" | Set-Content -Path $out -Encoding UTF8
Write-Host ""
Write-Host ("Wrote " + $out)
