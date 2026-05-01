# Verify the Startup-folder shortcut is correct — target, icon, name —
# so Task Manager's Startup Apps UI shows "Master's FM" with the purple icon,
# not "wscript.exe" or anything else confusing.
$lnk = [System.IO.Path]::Combine([Environment]::GetFolderPath('Startup'), "Master's FM.lnk")
if (-not (Test-Path $lnk)) {
    Write-Host "NOT FOUND: $lnk"
    exit 1
}

$sh = New-Object -ComObject WScript.Shell
$s  = $sh.CreateShortcut($lnk)
Write-Host "Startup shortcut: $lnk"
Write-Host ""
Write-Host "  Target:      $($s.TargetPath)"
Write-Host "  Arguments:   $($s.Arguments)"
Write-Host "  IconPath:    $($s.IconLocation)"
Write-Host "  WorkingDir:  $($s.WorkingDirectory)"
Write-Host "  Description: $($s.Description)"
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($sh)

# Also dump the Task Manager Startup Apps entry metadata
Write-Host ""
Write-Host "Task Manager Startup Apps entry:"
$run = Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue |
       Where-Object { $_.Name -like '*Master*' -or $_.Command -like '*MastersFM*' -or $_.Command -like '*tray.ps1*' }
if ($run) {
    foreach ($r in $run) {
        Write-Host "  Name:    $($r.Name)"
        Write-Host "  Command: $($r.Command)"
        Write-Host "  Location:$($r.Location)"
        Write-Host "  User:    $($r.User)"
        Write-Host ""
    }
} else {
    Write-Host "  (no CIM entry found via Win32_StartupCommand)"
}
