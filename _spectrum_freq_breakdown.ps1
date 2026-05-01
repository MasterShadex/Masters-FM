$BAND_COUNT = 480
$FFT_SIZE   = 2048
$SAMPLE     = 48000.0
$fMin       = 30.0
$fMax       = 16000.0

$binHz = $SAMPLE / $FFT_SIZE
$octaves = [math]::Log($fMax / $fMin, 2)
$barsPerOctave = $BAND_COUNT / $octaves

Write-Host ""
Write-Host "=== Master's FM Spectrum Frequency Breakdown ==="
Write-Host ""
Write-Host ("  Max bar count        : {0} bars" -f $BAND_COUNT)
Write-Host ("  Frequency range      : {0} Hz to {1:N0} Hz ({2:N0} kHz)" -f $fMin, $fMax, ($fMax/1000))
Write-Host ("  Octaves covered      : {0:N2} octaves" -f $octaves)
Write-Host ("  Bars per octave      : {0:N1} bars/octave" -f $barsPerOctave)
Write-Host ("  FFT size             : {0} samples at {1:N0} Hz sample rate" -f $FFT_SIZE, $SAMPLE)
Write-Host ("  FFT bin width        : {0:N2} Hz per bin" -f $binHz)
Write-Host ("  FFT latency          : {0:N1} ms per window" -f ($FFT_SIZE / $SAMPLE * 1000))
Write-Host ""
Write-Host "=== Frequency at each 10% of the visualizer ==="
Write-Host ""
$pct = @(0, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 95, 100)
Write-Host ("  {0,-6}  {1,-8}  {2,-14}  {3}" -f "% pos", "Band #", "Frequency", "Audio description")
Write-Host ("  {0,-6}  {1,-8}  {2,-14}  {3}" -f "-----", "------", "---------", "-----------------")
$labels = @{
    0   = 'sub-bass floor (room / HVAC rumble lives here)'
    5   = 'kick drum fundamental (~30 Hz)'
    10  = 'deep bass (synth bass, 808s)'
    20  = 'bass range (bass guitar root notes ~80 Hz)'
    30  = 'low mids (muddy frequencies)'
    40  = 'low mids (kick body, male vocals bottom)'
    50  = 'midrange (vocals, most instruments center here)'
    60  = 'upper mids (presence, intelligibility of vocals)'
    70  = 'high mids (brightness, snare crack)'
    80  = 'highs (cymbal body, hi-hats)'
    90  = 'air / brilliance (top of cymbal shimmer)'
    95  = 'very high (above most music content)'
    100 = 'ultrasonic ceiling (edge of human hearing)'
}
foreach ($p in $pct) {
    $n = [int]($BAND_COUNT * $p / 100)
    $f = $fMin * [math]::Pow($fMax/$fMin, $n / $BAND_COUNT)
    $fStr = if ($f -lt 1000) { "{0:N0} Hz" -f $f } else { "{0:N2} kHz" -f ($f/1000) }
    Write-Host ("  {0,-6}  #{1,-7}  {2,-14}  {3}" -f ($p.ToString() + '%'), $n, $fStr, $labels[$p])
}
Write-Host ""
Write-Host "=== Per-octave bar budget ==="
Write-Host ""
Write-Host ("  Each octave (doubling of frequency) gets {0:N1} bars." -f $barsPerOctave)
Write-Host ("  That's finer resolution than most music production EQs, which typically")
Write-Host ("  show 10-12 bands per octave. The visualizer shows roughly 2x the detail")
Write-Host ("  of a pro-audio parametric EQ across the full audible spectrum.")
