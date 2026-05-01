# Quiet diagnostic — uses the standard Windows notification sound (respects
# the user's system-notification volume, which is usually much lower than the
# master volume). Previous [console]::beep() jump-scared the user because
# that API bypasses volume mixer and plays at master-volume level.
Add-Type -AssemblyName System.Windows.Forms
$a = (Invoke-WebRequest 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
Write-Host ("before: frame=$($a.frame) device=$($a.device)")
[System.Media.SystemSounds]::Asterisk.Play()
Start-Sleep -Milliseconds 1500
$b = (Invoke-WebRequest 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
Write-Host ("after:  frame=$($b.frame)")
Write-Host ("delta: $($b.frame - $a.frame) frames over ~1.5 s")

Write-Host ""
Write-Host "--- server.log spectrum-related ---"
Get-Content 'C:\Users\Master\AppData\Local\MastersFM\server.log' -Tail 60 | Select-String -Pattern 'WASAPI|spectrum|SSE'
