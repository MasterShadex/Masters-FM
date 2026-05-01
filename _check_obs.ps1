# Check whether Master's FM browser source is registered in any OBS scene
# collection. Runs the same logic Test-ObsSourceExists uses in tray.ps1.
$dir = [System.IO.Path]::Combine(
    [System.Environment]::GetFolderPath('ApplicationData'),
    'obs-studio', 'basic', 'scenes')
if (-not (Test-Path $dir)) {
    Write-Host "OBS scene dir not found: $dir"
    exit
}
$found = 0
Get-ChildItem $dir -Filter '*.json' | ForEach-Object {
    $raw = [System.IO.File]::ReadAllText($_.FullName)
    $hasName = $raw.Contains("Master's FM") -or $raw.Contains('Master\u0027s FM')
    $hasUrl  = $raw.Contains('localhost:4242') -or $raw.Contains('localhost%3A4242')
    if ($hasName -and $hasUrl) {
        Write-Host ("FOUND in " + $_.Name)
        $found++
    }
}
if ($found -eq 0) { Write-Host "Master's FM source NOT found in any scene collection" }
