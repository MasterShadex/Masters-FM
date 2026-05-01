# Part 7 idle-run memory monitor.
# Takes two snapshots ~60s apart of the 4 Master's FM processes and diffs
# WorkingSet64 + HandleCount so we can spot runaway leaks.

function Snapshot() {
    $snap = @{}
    foreach ($name in @('MastersFM','MastersFM_Tray','server','audio_spectrum')) {
        $p = Get-Process -Name $name -ErrorAction SilentlyContinue
        if ($p) {
            $snap[$name] = @{
                WS      = [long]$p.WorkingSet64
                WsMB    = [Math]::Round($p.WorkingSet64 / 1MB, 1)
                Handles = [int]$p.HandleCount
                PID_    = $p.Id
            }
        }
    }
    return $snap
}

Write-Host "Snapshot 1 (t=0s):"
$s1 = Snapshot
foreach ($k in $s1.Keys | Sort-Object) {
    Write-Host ("  {0,-18} PID={1,-6} WS={2,6} MB   Handles={3}" -f $k, $s1[$k].PID_, $s1[$k].WsMB, $s1[$k].Handles)
}

Write-Host ""
Write-Host "Waiting 60s..."
Start-Sleep -Seconds 60

Write-Host ""
Write-Host "Snapshot 2 (t=60s):"
$s2 = Snapshot
foreach ($k in $s2.Keys | Sort-Object) {
    Write-Host ("  {0,-18} PID={1,-6} WS={2,6} MB   Handles={3}" -f $k, $s2[$k].PID_, $s2[$k].WsMB, $s2[$k].Handles)
}

Write-Host ""
Write-Host "Deltas:"
foreach ($k in $s1.Keys | Sort-Object) {
    if ($s2.ContainsKey($k)) {
        $dMB = [Math]::Round(($s2[$k].WS - $s1[$k].WS) / 1MB, 2)
        $dH  = $s2[$k].Handles - $s1[$k].Handles
        Write-Host ("  {0,-18} dWS={1,+8:N2} MB   dHandles={2,+5}" -f $k, $dMB, $dH)
    } else {
        Write-Host "  $k DIED between snapshots"
    }
}
