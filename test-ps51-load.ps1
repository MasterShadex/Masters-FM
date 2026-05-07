# PS5.1 load test for tray_native.dll (sub-stage 5.1 validation)
# Run with: C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File test-ps51-load.ps1
$ErrorActionPreference = "Stop"

$dllPath = "G:\Project Folder\Master FM\tray_native.dll"
if (-not (Test-Path $dllPath)) {
    Write-Error "tray_native.dll not found at $dllPath"
    exit 1
}

$dllItem = Get-Item $dllPath
Write-Host "PSVersion:  $($PSVersionTable.PSVersion)"
Write-Host "PSEdition:  $($PSVersionTable.PSEdition)"
Write-Host "CLRVersion: $($PSVersionTable.CLRVersion)"
Write-Host "DLL path:   $($dllItem.FullName)"
Write-Host "DLL size:   $($dllItem.Length) bytes"
Write-Host "DLL time:   $($dllItem.LastWriteTime)"
Write-Host ""

Write-Host "Loading tray_native.dll via Add-Type -Path..."
try {
    Add-Type -Path $dllPath
    Write-Host "Add-Type: OK"
} catch {
    Write-Error "Add-Type FAILED: $($_.Exception.Message)"
    exit 1
}
Write-Host ""

$expectedTypes = @(
    'MFM_Shell',
    'MFM_MenuNative',
    'NativeMethods.GuiRes',
    'MasterFM.Win32Windows',
    'MasterFM.AudioPeak',
    'MasterFM.SMTC.SMTCWatcher',
    'MasterFM.SMTC.SMTCSessionSnapshot',
    'MasterFM.SMTC.SMTCChangeRecord',
    'MasterFM.SMTC.SMTCEventKind'
)

$failures = @()
foreach ($t in $expectedTypes) {
    $resolved = $t -as [type]
    if ($null -eq $resolved) {
        $failures += $t
        Write-Host "FAIL: $t"
    } else {
        Write-Host "OK:   $t -> $($resolved.FullName)"
    }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Error "TYPE RESOLUTION FAILURES ($($failures.Count)): $($failures -join ', ')"
    exit 1
}

Write-Host ""
Write-Host "ALL $($expectedTypes.Count) TYPES RESOLVED."
Write-Host "PS5.1 LOAD TEST: PASS"
exit 0
