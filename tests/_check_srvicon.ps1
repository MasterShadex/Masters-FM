# Extract server.exe's current icon and save it so we can see whether the
# resedit rebrand step actually swapped it to MastersFM.ico, or whether
# pkg's default Node green icon is still baked in.
Add-Type -AssemblyName System.Drawing
$exe  = 'C:\Users\Master\AppData\Local\MastersFM\server.exe'
$ref  = 'C:\Users\Master\AppData\Local\MastersFM\MastersFM.ico'
$out  = 'C:\Users\Master\AppData\Local\MastersFM\_server_icon_test.png'

try {
    $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
    Write-Host ("server.exe icon size: {0}x{1}" -f $ico.Width, $ico.Height)
    $ico.ToBitmap().Save($out)
    $bytes = (Get-Item $out).Length
    Write-Host ("Saved PNG ({0} bytes): {1}" -f $bytes, $out)
} catch {
    Write-Host ("ERR: $_")
}

# Also dump the icon-resource count using resedit --definition or similar.
Write-Host ""
Write-Host "--- server.exe resources in the .rsrc section ---"
try {
    $peBytes = [System.IO.File]::ReadAllBytes($exe)
    # Rough count: search for "PNG" / "RIFF" / GRPICON markers.  Better:
    # use resedit's own tooling; we just use a crude substring search.
    $text = [System.Text.Encoding]::ASCII.GetString($peBytes[0..([Math]::Min($peBytes.Length-1, 2097151))])
    $pngCount  = ([regex]::Matches($text, "PNG\s*IHDR")).Count
    $iconCount = ([regex]::Matches($text, "(?s)\x28\x00\x00\x00\x00")).Count
    Write-Host ("rough PNG chunk count: $pngCount")
} catch {
    Write-Host ("dump err: $_")
}
