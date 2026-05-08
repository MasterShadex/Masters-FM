# _soak_7_10.ps1 -- Stage 7.10 6h soak harness
#
# Reads MastersFM C# tray overlay.log heartbeat lines (DiagnosticHeartbeat, 60s cadence).
# Collects 360 samples (6 h), checks halt conditions, writes soak_7_10_<stamp>.json.
#
# Usage (run AFTER starting MastersFM_Tray.exe + server.exe):
#   powershell -ExecutionPolicy Bypass -File "_soak_7_10.ps1" [-TrayPid <pid>] [-OutJson <path>]
#
# Halt conditions (per Stage 7.10 brief):
#   1. WS plateau > 280 MB
#   2. Both-half mean diff > 15 MB (genuine drift, not GC sawtooth)
#   3. Final-30-min end-to-end slope > 8 MB/h
#
# Listening-session tag: set $env:S710_LISTENING_START when active listening begins;
#   harness tags those samples in output with "listening"=true.
#
# Outputs:
#   $OutJson   -- full sample array (JSON)
#   $OutJson.replace('.json','_halt.txt') -- halt reason (if any)

param(
    [int]$TrayPid      = 0,
    [int]$MaxSamples   = 360,       # 360 x 60s = 6h
    [string]$OutJson   = ''
)

$ErrorActionPreference = 'Stop'

# ----- paths -------------------------------------------------------------------
$LogDir  = "$env:LOCALAPPDATA\MastersFM"
$OverLog = Join-Path $LogDir 'overlay.log'
$Stamp   = Get-Date -Format 'yyyyMMdd_HHmmss'
if (-not $OutJson) { $OutJson = Join-Path $LogDir "soak_7_10_$Stamp.json" }
$HaltFile = $OutJson -replace '\.json$', '_halt.txt'

# ----- constants ---------------------------------------------------------------
$HALT_WS_CEIL_MB      = 280.0   # plateau ceiling
$HALT_HALFDIFF_MB     = 15.0    # both-half mean diff
$HALT_SLOPE_MBH       = 8.0     # final-30-min slope MB/h
$CADENCE_S            = 60      # heartbeat cadence (matches DiagnosticHeartbeat)

# ----- helpers -----------------------------------------------------------------
function Parse-Heartbeat($line) {
    # Format: heartbeat: ws=XMB gc=XMB priv=XMB threads=N handles=N ring=N | ... | osu=Xms ...
    # Hotfix 7.10: patterns use [\d\.,]+ to handle locale-comma decimals (e.g. ws=273,7MB).
    # [double] cast uses invariant parse (comma replaced with period) for safety.
    if ($line -notmatch 'heartbeat:') { return $null }
    $r = @{}
    if ($line -match 'ws=([\d\.,]+)MB')           { $r.ws      = [double]($Matches[1] -replace ',','.') }
    if ($line -match 'gc=([\d\.,]+)MB')           { $r.gc      = [double]($Matches[1] -replace ',','.') }
    if ($line -match 'priv=([\d\.,]+)MB')         { $r.priv    = [double]($Matches[1] -replace ',','.') }
    if ($line -match 'threads=(\d+)')             { $r.threads = [int]$Matches[1] }
    if ($line -match 'handles=(\d+)')             { $r.handles = [int]$Matches[1] }
    if ($line -match 'ring=(\d+)')                { $r.ring    = [int]$Matches[1] }
    if ($line -match 'osu=([\d\.,]+)ms')          { $r.osu_p99    = [double]($Matches[1] -replace ',','.') }
    if ($line -match 'vlc=([\d\.,]+)ms')          { $r.vlc_p99    = [double]($Matches[1] -replace ',','.') }
    if ($line -match 'wmp=([\d\.,]+)ms')          { $r.wmp_p99    = [double]($Matches[1] -replace ',','.') }
    if ($line -match 'webhook=([\d\.,]+)ms')      { $r.wh_p99     = [double]($Matches[1] -replace ',','.') }
    if ($line -match 'smtc=([\d\.,]+)ms')         { $r.smtc_p99   = [double]($Matches[1] -replace ',','.') }
    # telemetry summary (extract a few counters)
    if ($line -match 'track_changes=(\d+)')       { $r.track_changes    = [long]$Matches[1] }
    if ($line -match 'webhook_sends=(\d+)')       { $r.webhook_sends    = [long]$Matches[1] }
    if ($line -match 'smtc_events=(\d+)')         { $r.smtc_events      = [long]$Matches[1] }
    if ($line -match 'webhook_send_errors=(\d+)') { $r.wh_errors        = [long]$Matches[1] }
    return $r
}

function Check-HaltConditions($samples) {
    $ws_vals = $samples | Where-Object { $_.ws } | ForEach-Object { $_.ws }
    if ($ws_vals.Count -lt 2) { return $null }

    # 1. Plateau ceiling
    $peak = ($ws_vals | Measure-Object -Maximum).Maximum
    if ($peak -gt $HALT_WS_CEIL_MB) {
        return "HALT: WS peak ${peak}MB > ${HALT_WS_CEIL_MB}MB ceiling"
    }

    # 2. Both-half mean diff (only after enough samples)
    if ($ws_vals.Count -ge 20) {
        $mid   = [int]($ws_vals.Count / 2)
        $first = ($ws_vals[0..($mid-1)] | Measure-Object -Average).Average
        $last  = ($ws_vals[$mid..($ws_vals.Count-1)] | Measure-Object -Average).Average
        $diff  = $last - $first
        if ($diff -gt $HALT_HALFDIFF_MB) {
            $diffR = [Math]::Round($diff, 1)
            return "HALT: both-half mean diff ${diffR}MB > ${HALT_HALFDIFF_MB}MB"
        }
    }

    # 3. Final-30-min slope (last 30 samples = 30 min)
    if ($ws_vals.Count -ge 30) {
        $tail = $ws_vals[($ws_vals.Count-30)..($ws_vals.Count-1)]
        $n    = $tail.Count
        $x    = 0..($n-1)
        # linear regression slope (simple, in MB/sample = MB/min)
        $xm   = ($x | Measure-Object -Average).Average
        $ym   = ($tail | Measure-Object -Average).Average
        $num  = 0; $den = 0
        for ($i = 0; $i -lt $n; $i++) {
            $num += ($x[$i] - $xm) * ($tail[$i] - $ym)
            $den += ($x[$i] - $xm) * ($x[$i] - $xm)
        }
        $slope_per_min = if ($den -ne 0) { $num / $den } else { 0 }
        $slope_mBh     = $slope_per_min * 60.0
        if ($slope_mBh -gt $HALT_SLOPE_MBH) {
            $sR = [Math]::Round($slope_mBh, 2)
            return "HALT: final-30-min slope ${sR}MB/h > ${HALT_SLOPE_MBH}MB/h"
        }
    }

    return $null
}

# ----- tray process check ------------------------------------------------------
function Get-TrayProcess {
    # C# WPF tray: PID supplied or auto-detect by process name
    if ($TrayPid -gt 0) {
        try { return Get-Process -Id $TrayPid -ErrorAction Stop }
        catch { Write-Warning "Supplied TrayPid=$TrayPid not found; auto-detecting." }
    }
    $candidates = Get-Process -Name 'MastersFM_Tray' -ErrorAction SilentlyContinue
    if ($candidates) { return $candidates | Select-Object -First 1 }
    return $null
}

# ----- main loop ---------------------------------------------------------------
Write-Host "[S7.10 harness] starting -- target=$MaxSamples samples x ${CADENCE_S}s = $([int]($MaxSamples*$CADENCE_S/3600))h"
Write-Host "[S7.10 harness] overlay.log = $OverLog"
Write-Host "[S7.10 harness] output      = $OutJson"
Write-Host ""

$samples      = [System.Collections.Generic.List[hashtable]]::new()
$lastLineIdx  = 0
$sampleN      = 0
$startUtc     = [DateTimeOffset]::UtcNow
$haltReason   = $null

# Advance to current end of log so we don't replay old heartbeats
if (Test-Path $OverLog) {
    $existing = Get-Content $OverLog
    $lastLineIdx = $existing.Count
    Write-Host "[S7.10 harness] skipping $lastLineIdx pre-existing lines in overlay.log"
}

while ($sampleN -lt $MaxSamples -and -not $haltReason) {
    # Wait one cadence period, reading new log lines as they appear
    $deadline = (Get-Date).AddSeconds($CADENCE_S)
    $gotBeat  = $false

    while ((Get-Date) -lt $deadline -and -not $gotBeat) {
        Start-Sleep -Seconds 5
        if (Test-Path $OverLog) {
            $lines = Get-Content $OverLog
            if ($lines.Count -gt $lastLineIdx) {
                $newLines    = $lines[$lastLineIdx..($lines.Count-1)]
                $lastLineIdx = $lines.Count
                foreach ($ln in $newLines) {
                    $parsed = Parse-Heartbeat $ln
                    if ($parsed -and $parsed.ContainsKey('ws')) {
                        $gotBeat = $true
                        $sampleN++
                        $elapsed = ([DateTimeOffset]::UtcNow - $startUtc).TotalMinutes
                        $parsed.sampleN    = $sampleN
                        $parsed.elapsedMin = [Math]::Round($elapsed, 1)
                        $parsed.utc        = [DateTimeOffset]::UtcNow.ToString('o')
                        # tag listening session samples
                        $listening = $env:S710_LISTENING_START -and ([DateTimeOffset]::UtcNow - [DateTimeOffset]$env:S710_LISTENING_START).TotalMinutes -le 49
                        $parsed.listening  = [bool]$listening

                        $samples.Add($parsed)

                        $wsStr = "$($parsed.ws)MB"
                        $gcStr = if ($parsed.ContainsKey('gc'))   { " gc=$($parsed.gc)MB" }   else { '' }
                        $prStr = if ($parsed.ContainsKey('priv')) { " priv=$($parsed.priv)MB" } else { '' }
                        Write-Host ("[{0:D3}/{1}] T+{2:F0}min ws={3}{4}{5} h={6} t={7}" -f `
                            $sampleN, $MaxSamples, $elapsed, $wsStr, $gcStr, $prStr,
                            $(if ($parsed.ContainsKey('handles')) { $parsed.handles } else { '?' }),
                            $(if ($parsed.ContainsKey('threads')) { $parsed.threads } else { '?' }))

                        # check halt
                        $haltReason = Check-HaltConditions $samples
                        if ($haltReason) { break }
                    }
                }
            }
        }
    }

    # If cadence elapsed without a heartbeat, synthesize a missed-sample marker
    if (-not $gotBeat -and -not $haltReason) {
        $tray = Get-TrayProcess
        if (-not $tray) {
            $haltReason = "HALT: MastersFM_Tray.exe process not found -- tray died"
            break
        }
        # Sample WS directly from process as fallback
        $wsMB = [Math]::Round($tray.WorkingSet64 / 1MB, 1)
        $sampleN++
        $elapsed = ([DateTimeOffset]::UtcNow - $startUtc).TotalMinutes
        $fallback = @{
            sampleN    = $sampleN
            elapsedMin = [Math]::Round($elapsed, 1)
            utc        = [DateTimeOffset]::UtcNow.ToString('o')
            ws         = $wsMB
            source     = 'process-fallback'
            listening  = $false
        }
        $samples.Add($fallback)
        Write-Host ("[{0:D3}/{1}] T+{2:F0}min ws={3}MB (FALLBACK -- no heartbeat)" -f `
            $sampleN, $MaxSamples, $elapsed, $wsMB)
        $haltReason = Check-HaltConditions $samples
    }
}

# ----- summary -----------------------------------------------------------------
$ws_all  = $samples | Where-Object { $_.ContainsKey('ws') } | ForEach-Object { $_.ws }
$ws_min  = if ($ws_all) { [Math]::Round(($ws_all | Measure-Object -Minimum).Minimum, 1) } else { 0 }
$ws_max  = if ($ws_all) { [Math]::Round(($ws_all | Measure-Object -Maximum).Maximum, 1) } else { 0 }
$ws_mean = if ($ws_all) { [Math]::Round(($ws_all | Measure-Object -Average).Average, 1) } else { 0 }

# Compute final-30-min slope for report
$slope_final = 0.0
if ($ws_all.Count -ge 30) {
    $tail = $ws_all[($ws_all.Count-30)..($ws_all.Count-1)]
    $n = $tail.Count; $x = 0..($n-1)
    $xm = ($x | Measure-Object -Average).Average
    $ym = ($tail | Measure-Object -Average).Average
    $num = 0; $den = 0
    for ($i = 0; $i -lt $n; $i++) {
        $num += ($x[$i] - $xm) * ($tail[$i] - $ym)
        $den += ($x[$i] - $xm) * ($x[$i] - $xm)
    }
    $slope_final = if ($den -ne 0) { [Math]::Round($num / $den * 60.0, 2) } else { 0 }
}

$duration_h = [Math]::Round($samples.Count * $CADENCE_S / 3600.0, 2)
$verdict    = if ($haltReason) { 'FAIL' } elseif ($ws_max -gt 260) { 'PARTIAL' } else { 'PASS' }

$summary = @{
    stage       = '7.10'
    stamp       = $Stamp
    samples     = $samples.Count
    duration_h  = $duration_h
    ws_min_MB   = $ws_min
    ws_max_MB   = $ws_max
    ws_mean_MB  = $ws_mean
    slope_final_MBh = $slope_final
    halt_reason = $haltReason
    verdict     = $verdict
    data        = $samples
}

$json = $summary | ConvertTo-Json -Depth 5 -Compress
[System.IO.File]::WriteAllText($OutJson, $json, [System.Text.Encoding]::UTF8)

if ($haltReason) {
    [System.IO.File]::WriteAllText($HaltFile, $haltReason, [System.Text.Encoding]::UTF8)
    Write-Host ""
    Write-Host $haltReason -ForegroundColor Red
    Write-Host "[S7.10 harness] halt file written: $HaltFile"
}

Write-Host ""
Write-Host "=== S7.10 SOAK SUMMARY ==="
Write-Host "  samples   : $($samples.Count) / $MaxSamples"
Write-Host "  duration  : $duration_h h"
Write-Host "  ws min/mean/max: $ws_min / $ws_mean / $ws_max MB"
Write-Host "  slope (final 30): $slope_final MB/h  (halt threshold: $HALT_SLOPE_MBH)"
Write-Host "  verdict   : $verdict"
Write-Host "  output    : $OutJson"
