# Show uptime for every Master's FM process plus resource usage — so we can
# spot memory leaks, stalled threads, or processes that respawned
# unexpectedly. Pulls from Win32_Process for start-time + command line.
$names = @('MastersFM.exe', 'server.exe', 'MastersFM_Tray.exe', 'audio_spectrum.exe', 'customize.exe', 'powershell.exe')
$procs = Get-CimInstance Win32_Process -Filter "Name='MastersFM.exe' OR Name='server.exe' OR Name='MastersFM_Tray.exe' OR Name='audio_spectrum.exe' OR Name='customize.exe' OR (Name='powershell.exe' AND CommandLine LIKE '%tray.ps1%')"

foreach ($p in $procs) {
    $start = $p.CreationDate
    $age   = (Get-Date) - $start
    $memMB = [Math]::Round($p.WorkingSetSize / 1MB, 1)
    $line  = $p.Name.PadRight(22) +
             " PID=" + $p.ProcessId.ToString().PadRight(6) +
             " mem=" + $memMB.ToString().PadLeft(6) + " MB" +
             " age=" + ([Math]::Floor($age.TotalSeconds)).ToString().PadLeft(5) + "s"
    Write-Host $line
}

# Also check if any orphan powershell/wscript processes are lingering — these
# would indicate the cleanup step in _full_rebuild.ps1 missed something.
$orphans = Get-CimInstance Win32_Process -Filter "Name='wscript.exe' OR Name='cscript.exe'"
if ($orphans) {
    Write-Host ""
    Write-Host "ORPHANS DETECTED:"
    foreach ($o in $orphans) {
        Write-Host "  $($o.Name)  PID=$($o.ProcessId)  CommandLine=$($o.CommandLine)"
    }
} else {
    Write-Host ""
    Write-Host "(no wscript/cscript orphans)"
}
