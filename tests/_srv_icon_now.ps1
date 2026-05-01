Add-Type -AssemblyName System.Drawing
$exe = 'C:\Users\Master\AppData\Local\MastersFM\server.exe'
$png = 'C:\Users\Master\AppData\Local\MastersFM\_srv_icon_now.png'
$ico = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
$ico.ToBitmap().Save($png)
Write-Host ("server.exe icon: $($ico.Width)x$($ico.Height)")
Write-Host ("PNG bytes: $((Get-Item $png).Length)")
