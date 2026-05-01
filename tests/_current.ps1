# Print current-track diagnostics — artist, track, source platform, duration, art.
$t = (Invoke-WebRequest http://localhost:4242/current -TimeoutSec 3 -UseBasicParsing).Content | ConvertFrom-Json
if ($t) {
    Write-Host ("artist    = " + $t.artist)
    Write-Host ("track     = " + $t.track)
    Write-Host ("source    = " + $t.source)
    Write-Host ("platform  = " + $t.platform)
    Write-Host ("duration  = " + [Math]::Round($t.duration/1000) + "s")
    Write-Host ("hasArt    = " + ($null -ne $t.trackArt))
    Write-Host ("isPaused  = " + $t.isPaused)
} else {
    Write-Host "(no current track)"
}
