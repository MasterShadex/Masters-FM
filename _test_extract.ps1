Add-Type -AssemblyName System.Drawing
$ico = [System.Drawing.Icon]::ExtractAssociatedIcon('F:\Claude AI\Master FM\_test_v2.exe')
$ico.ToBitmap().Save('F:\Claude AI\Master FM\_test_v2.png')
Write-Host ("_test_v2.png: $((Get-Item 'F:\Claude AI\Master FM\_test_v2.png').Length) bytes")
Write-Host ("size: $($ico.Width)x$($ico.Height)")
