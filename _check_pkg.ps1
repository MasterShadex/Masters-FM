# Quick sanity check: did rcedit corrupt pkg's overlay?
# pkg-bundled exes have a VFS blob at the end of the file with identifiable
# markers. If those are still present, the bundle should still work.
param([string]$Path = 'F:\Claude AI\Master FM\_rcedit_test.exe')

if (-not (Test-Path $Path)) { Write-Host "MISSING: $Path"; exit 1 }

$bytes = [System.IO.File]::ReadAllBytes($Path)
$tailStart = [Math]::Max(0, $bytes.Length - 1048576)
$tail = [System.Text.Encoding]::ASCII.GetString($bytes[$tailStart..($bytes.Length - 1)])

$markers = @('PAYLOAD_POSITION_PLACEHOLDER', 'virtual_file_system',
             'source\.js', 'node_modules', 'snapshot/', 'package\.json')
$found = @()
foreach ($m in $markers) {
    if ($tail -match $m) { $found += $m }
}
Write-Host "File: $Path"
Write-Host "Size: $($bytes.Length) bytes"
Write-Host "pkg markers found in last 1 MB: $($found -join ', ')"
if ($found.Count -ge 2) { Write-Host "VERDICT: overlay LIKELY INTACT" }
else                    { Write-Host "VERDICT: overlay likely CORRUPTED" }
