# Download NAudio.WinMM + NAudio.Asio 2.2.1 from nuget and extract the DLLs.
$ErrorActionPreference = 'Stop'

$naudioDir = 'F:\Claude AI\Master FM\build_tools\naudio'
$rootProj  = 'F:\Claude AI\Master FM'

$packages = @(
    @{ Name = 'NAudio.WinMM'; Version = '2.2.1' },
    @{ Name = 'NAudio.Asio';  Version = '2.2.1' }
)

foreach ($p in $packages) {
    $url = "https://www.nuget.org/api/v2/package/$($p.Name)/$($p.Version)"
    $zipPath = Join-Path $naudioDir "$($p.Name).zip"
    Write-Host "Downloading $($p.Name) $($p.Version)..."
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing -TimeoutSec 60
    $size = (Get-Item $zipPath).Length
    Write-Host "  -> $zipPath ($size bytes)"

    # Extract
    $extractDir = Join-Path $naudioDir $p.Name
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
    Write-Host "  Extracted to $extractDir"

    # Copy DLL + XML doc to project root (where NAudio.Core/Wasapi.dll live)
    $dllSrc = Join-Path $extractDir "lib\netstandard2.0\$($p.Name).dll"
    $xmlSrc = Join-Path $extractDir "lib\netstandard2.0\$($p.Name).xml"
    if (Test-Path $dllSrc) {
        $dllDst = Join-Path $rootProj "$($p.Name).dll"
        Copy-Item $dllSrc $dllDst -Force
        Write-Host "  Copied DLL -> $dllDst"
    } else {
        Write-Host "  WARN: DLL not found at $dllSrc"
    }
    if (Test-Path $xmlSrc) {
        # XML not strictly needed at runtime — skip shipping but keep in build_tools
    }
}

Write-Host ""
Write-Host "Done. NAudio DLLs at project root:"
Get-ChildItem $rootProj -Filter 'NAudio*.dll' | Select-Object Name, Length | Format-Table -AutoSize
