# Writes AUDIT_CYCLE_N.md + AUDIT_RUNTIME_LOGS.md summaries of the
# current log-folder state.
param([int]$CycleNum = 1)

$root = 'F:\Claude AI\Master FM'
$logDir = 'C:\Users\Master\AppData\Local\MastersFM'

$md = @()
$md += "# AUDIT_CYCLE_$CycleNum.md"
$md += ""
$md += "Cycle run at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$md += ""

foreach ($f in (Get-ChildItem $logDir -Filter '*.log' | Sort-Object Name)) {
    $content = Get-Content $f.FullName -ErrorAction SilentlyContinue
    if (-not $content) { continue }
    $err   = @($content | Select-String -Pattern 'ERROR' -CaseSensitive).Count
    $fatal = @($content | Select-String -Pattern 'FATAL' -CaseSensitive).Count
    $warn  = @($content | Select-String -Pattern 'WARN').Count
    $diag  = @($content | Select-String -Pattern '\[DIAG\]').Count
    $ex    = @($content | Select-String -Pattern 'Exception').Count
    $threw = @($content | Select-String -Pattern 'threw').Count
    $md += ("## {0} ({1} lines, {2} bytes)" -f $f.Name, $content.Count, $f.Length)
    $md += ""
    $md += ("- ERROR: {0}  FATAL: {1}  WARN: {2}  [DIAG]: {3}  Exception: {4}  'threw': {5}" -f $err, $fatal, $warn, $diag, $ex, $threw)
    $md += ""
    if (($err + $fatal + $warn + $diag) -gt 0) {
        $md += "### Relevant entries:"
        $md += '```'
        $md += ($content | Select-String -Pattern 'ERROR|FATAL|WARN|\[DIAG\]|threw')
        $md += '```'
        $md += ""
    }
}

$md | Set-Content -Path (Join-Path $root "AUDIT_CYCLE_$CycleNum.md") -Encoding utf8

# Also write/update AUDIT_RUNTIME_LOGS.md with the latest summary
$md | Set-Content -Path (Join-Path $root 'AUDIT_RUNTIME_LOGS.md') -Encoding utf8

Write-Host "Wrote AUDIT_CYCLE_$CycleNum.md and AUDIT_RUNTIME_LOGS.md"
