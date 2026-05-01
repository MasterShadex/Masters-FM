$ErrorActionPreference = 'Stop'
$srcRoot = "F:\Claude AI\Master FM"

# Files to classify (files with catches)
$needReview = @(
    'tray.ps1','server.js','audio_spectrum.cs','overlay.html','customize.html',
    'launcher.cs','_smoke.ps1','customize.cs','install_bootstrapper.cs',
    'discord_rpc.js','build_tools\ps2exe\ps2exe.ps1','tray_launcher.cs',
    '_full_rebuild.ps1','_stress_tests.ps1','_probe_customize.ps1','REBUILD.bat',
    '_check_srvicon.ps1','_deep_health.ps1','_exercise_endpoints.ps1',
    '_exercise_natural.ps1','_scan_asio_channels.ps1','_test_asio_ch56.ps1',
    '_test_backends.ps1','_final_smoke.ps1','_test_restart.ps1',
    '_verify_sctimer.ps1','_diag.ps1','_parse_backup_tray.ps1',
    '_parse_tray2.ps1','_sse_test.ps1'
)

function Classify-Catch {
    param([string]$body)
    # body is the contents of the catch block
    if ($body -match '\b(Log|flog|log|overlayLog|EarlyLog|LogErr|File\.AppendAllText|Write-Host|console\.log|console\.warn|console\.error|Write-Output|Write-Warning|Write-Error)\b') {
        return 'logs'
    }
    if ($body -match 'throw\b|rethrow\b') {
        return 'rethrows'
    }
    if ($body -match '^\s*$|^\s*#[^\n]*\s*$|^\s*//[^\n]*\s*$') {
        return 'empty'
    }
    if ($body -match '^\s*return\b|^\s*continue\b|^\s*break\b') {
        return 'flow-control'
    }
    # Default: has code but doesn't log
    return 'silent-action'
}

# Extract catch blocks from PowerShell/JS/C#/HTML
function Extract-Catches {
    param([string]$content, [string]$ext)
    $catches = @()
    if ($ext -in @('.ps1','.psm1','.cs','.js','.html')) {
        # Find all `catch [whatever] {...}` blocks
        $pattern = '(?ms)(?<![a-zA-Z0-9_])catch\s*(\[.*?\]|\(.*?\))?\s*\{'
        foreach ($m in [regex]::Matches($content, $pattern)) {
            # Find matching closing brace
            $start = $m.Index + $m.Length
            $depth = 1
            $i = $start
            while ($i -lt $content.Length -and $depth -gt 0) {
                if     ($content[$i] -eq '{') { $depth++ }
                elseif ($content[$i] -eq '}') { $depth-- }
                $i++
            }
            $bodyEnd = $i - 1
            if ($bodyEnd -le $start) { continue }
            $body = $content.Substring($start, $bodyEnd - $start)
            $lineNum = ($content.Substring(0, $m.Index) -split "`n").Count
            $catches += [pscustomobject]@{ Line = $lineNum; Body = $body.Trim() }
        }
    }
    return $catches
}

$allResults = @()
foreach ($rel in $needReview) {
    $path = Join-Path $srcRoot $rel
    if (-not (Test-Path $path)) { continue }
    $content = try { Get-Content $path -Raw -ErrorAction Stop } catch { '' }
    if (-not $content) { continue }
    $ext = [System.IO.Path]::GetExtension($rel).ToLower()
    $catches = Extract-Catches -content $content -ext $ext
    $summary = @{ logs=0; rethrows=0; empty=0; 'flow-control'=0; 'silent-action'=0 }
    $silentLines = @()
    foreach ($c in $catches) {
        $cat = Classify-Catch -body $c.Body
        $summary[$cat]++
        if ($cat -in @('empty','silent-action')) {
            $silentLines += $c.Line
        }
    }
    $allResults += [pscustomobject]@{
        File = $rel
        Total = $catches.Count
        Logs = $summary['logs']
        Rethrows = $summary['rethrows']
        Empty = $summary['empty']
        FlowControl = $summary['flow-control']
        SilentAction = $summary['silent-action']
        SilentLines = ($silentLines | Sort-Object -Unique)
    }
}

# Write classification report
$out = "F:\Claude AI\Master FM\AUDIT_CATCH_CLASSIFIED.md"
$outLines = @()
$outLines += "# Catch Block Classification"
$outLines += ""
$outLines += "| File | Total | Logs | Rethrows | Empty | Flow-control | Silent-action | Silent line #s |"
$outLines += "|------|-------|------|----------|-------|--------------|---------------|----------------|"
foreach ($r in ($allResults | Sort-Object @{E='Total';D=$true})) {
    $silentStr = if ($r.SilentLines.Count -gt 0) { $r.SilentLines -join ',' } else { '' }
    $outLines += ("| ``" + $r.File + "`` | " + $r.Total + " | " + $r.Logs + " | " + $r.Rethrows + " | " + $r.Empty + " | " + $r.FlowControl + " | " + $r.SilentAction + " | " + $silentStr + " |")
}
$outLines += ""
$outLines += "## Aggregate totals"
$totalAll   = ($allResults | Measure-Object Total -Sum).Sum
$totalLogs  = ($allResults | Measure-Object Logs -Sum).Sum
$totalRe    = ($allResults | Measure-Object Rethrows -Sum).Sum
$totalEmpty = ($allResults | Measure-Object Empty -Sum).Sum
$totalFlow  = ($allResults | Measure-Object FlowControl -Sum).Sum
$totalSA    = ($allResults | Measure-Object SilentAction -Sum).Sum
$outLines += ""
$outLines += "- Total catch blocks reviewed: $totalAll"
$outLines += "- Already log meaningfully:    $totalLogs ($([math]::Round($totalLogs / [math]::Max(1,$totalAll) * 100, 1))%)"
$outLines += "- Rethrow / wrap:              $totalRe"
$outLines += "- Empty body:                  $totalEmpty"
$outLines += "- Flow control (return/continue/break): $totalFlow"
$outLines += "- Silent non-trivial action:   $totalSA"
$outLines -join "`r`n" | Set-Content -Path $out -Encoding UTF8

Write-Host "Classification complete."
Write-Host "  total catches across files: $totalAll"
Write-Host "  logs: $totalLogs  rethrows: $totalRe  empty: $totalEmpty  flow: $totalFlow  silent-action: $totalSA"
Write-Host "  report: $out"
