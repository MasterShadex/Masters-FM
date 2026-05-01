# Dump the last lines of every log in the runtime folder (read-only)
$logDir = 'C:\Users\Master\AppData\Local\MastersFM'
Get-ChildItem $logDir -Filter '*.log' | ForEach-Object {
    Write-Host ("=== {0} ({1} bytes) ===" -f $_.Name, $_.Length)
    Get-Content $_.FullName -Tail 15
    Write-Host ''
}
