$a = 'F:\Claude AI\Master FM\server.exe'
$b = 'F:\Claude AI\Master FM\_test_noicon.exe'
$ha = Get-FileHash $a -Algorithm SHA256
$hb = Get-FileHash $b -Algorithm SHA256
Write-Host "orig: $($ha.Hash)"
Write-Host "new:  $($hb.Hash)"
Write-Host "identical: $($ha.Hash -eq $hb.Hash)"

Add-Type -AssemblyName System.Drawing
$ico = [System.Drawing.Icon]::ExtractAssociatedIcon($b)
$png = 'F:\Claude AI\Master FM\_test_noicon.png'
$ico.ToBitmap().Save($png)
Write-Host "icon png: $((Get-Item $png).Length) bytes"
