$bytes = [System.IO.File]::ReadAllBytes('C:\Users\Master\AppData\Local\MastersFM\server.log')
Write-Host "Total bytes: $($bytes.Length)"
$take = [Math]::Min(400, $bytes.Length)

# Write out a hex-and-ASCII side-by-side dump of the first block so we
# can see exactly which byte sequences are tripping up PowerShell's UTF-8
# reader.
for ($i = 0; $i -lt $take; $i += 16) {
    $line = ''
    $ascii = ''
    for ($j = 0; $j -lt 16; $j++) {
        $idx = $i + $j
        if ($idx -ge $take) { $line += '   '; continue }
        $line += ('{0:X2} ' -f $bytes[$idx])
        $b = $bytes[$idx]
        if ($b -ge 32 -and $b -lt 127) { $ascii += [char]$b } else { $ascii += '.' }
    }
    Write-Host ('{0:X4}  {1}  {2}' -f $i, $line, $ascii)
}

# Also find sequences of high bytes (non-ASCII) so we can locate the first
# non-ASCII char and see what it is.
Write-Host ""
Write-Host "First non-ASCII byte in log:"
$first = -1
for ($i = 0; $i -lt $bytes.Length; $i++) {
    if ($bytes[$i] -gt 127) { $first = $i; break }
}
if ($first -ge 0) {
    $startShow = [Math]::Max(0, $first - 10)
    $endShow   = [Math]::Min($bytes.Length - 1, $first + 20)
    $context = ''
    for ($i = $startShow; $i -le $endShow; $i++) {
        $context += ('{0:X2} ' -f $bytes[$i])
    }
    Write-Host "  at offset $first (hex=$('{0:X}' -f $first)): $context"
}
