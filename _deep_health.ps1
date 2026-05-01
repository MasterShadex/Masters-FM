# Deep health check — looks for subtle regressions the 9-point smoke misses.
# Runs on the last 500 lines of each log and buckets interesting things.
$dir = "C:\Users\Master\AppData\Local\MastersFM"

function Count-Pattern($file, $pattern, $tail) {
    $p = Join-Path $dir $file
    if (-not (Test-Path $p)) { return 0 }
    try {
        return @(Get-Content $p -Tail $tail -ErrorAction Stop |
                 Select-String -Pattern $pattern -CaseSensitive:$false).Count
    } catch { return -1 }
}

Write-Host "=== DEEP HEALTH CHECK ==="
Write-Host ""

# Overlay.log — the business-logic log
$slowTicks  = Count-Pattern 'overlay.log' '!! SLOW TICK'     500
$terminErrs = Count-Pattern 'overlay.log' 'TerminatingError' 500
$unhandled  = Count-Pattern 'overlay.log' 'Unhandled'        500
$warns      = Count-Pattern 'overlay.log' 'WARN'             500
Write-Host "overlay.log:"
Write-Host "  SLOW TICK ........ $slowTicks"
Write-Host "  TerminatingError . $terminErrs"
Write-Host "  Unhandled ........ $unhandled"
Write-Host "  WARN ............. $warns"

# Server.log — Node server
$discReady = Count-Pattern 'server.log' 'discord.*READY'   500
$discFail  = Count-Pattern 'server.log' 'discord.*(fail|error|disconnect)' 500
$artHit    = Count-Pattern 'server.log' '.*Track ready.*art: .OK'   500
$artMiss   = Count-Pattern 'server.log' '.*Track ready.*art: .FAIL' 500
$nodeExc   = Count-Pattern 'server.log' 'UnhandledPromiseRejection|TypeError|ReferenceError' 500
Write-Host ""
Write-Host "server.log:"
Write-Host "  discord READY .... $discReady"
Write-Host "  discord failures . $discFail"
Write-Host "  art resolved ..... $artHit"
Write-Host "  art whiffed ...... $artMiss"
Write-Host "  Node exceptions .. $nodeExc"

# Drift — how in-sync the overlay is. 0s drift is perfect.
$driftLines = @()
try {
    $driftLines = Get-Content (Join-Path $dir 'server.log') -Tail 500 -ErrorAction Stop |
                  Select-String -Pattern 'drift=([-\d]+)s'
} catch {}
if ($driftLines) {
    $drifts = $driftLines | ForEach-Object {
        if ($_.Line -match 'drift=([-\d]+)s') { [int]$matches[1] }
    }
    $max = ($drifts | Measure-Object -Maximum).Maximum
    $min = ($drifts | Measure-Object -Minimum).Minimum
    $avg = [Math]::Round(($drifts | Measure-Object -Average).Average, 2)
    Write-Host ""
    Write-Host "drift monitor (last $($drifts.Count) ticks):  min=$min  max=$max  avg=$avg"
}

# Transcript errors (silent failures inside PS)
$psErrs = Count-Pattern 'transcript.log' 'TerminatingError'  500
$psExc  = Count-Pattern 'transcript.log' 'At line:\d+'       500
Write-Host ""
Write-Host "transcript.log:"
Write-Host "  TerminatingError . $psErrs"
Write-Host "  At line ERR ...... $psExc"

Write-Host ""
Write-Host "=== DONE ==="
