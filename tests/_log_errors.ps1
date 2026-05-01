# Scan every runtime log for [DIAG], Exception, FATAL, ERROR. Counts per log.
$logs = @('launcher.log','host.log','server.log','overlay.log','audio_spectrum.log','customize.log','menu.log','startup.log')
$dir = 'C:\Users\Master\AppData\Local\MastersFM'
Write-Host ("File".PadRight(22) + "  DIAG   ERROR   FATAL   Exception")
Write-Host ('-' * 60)
foreach ($f in $logs) {
    $p = Join-Path $dir $f
    if (-not (Test-Path $p)) { continue }
    $content = Get-Content $p -ErrorAction SilentlyContinue
    if (-not $content) { continue }
    $diag  = ($content | Select-String -Pattern '\[DIAG\]').Count
    $err   = ($content | Select-String -Pattern 'ERROR' -CaseSensitive).Count
    $fatal = ($content | Select-String -Pattern 'FATAL' -CaseSensitive).Count
    $ex    = ($content | Select-String -Pattern 'Exception').Count
    Write-Host ('{0,-22}  {1,4}   {2,5}   {3,5}   {4,9}' -f $f, $diag, $err, $fatal, $ex)
}
