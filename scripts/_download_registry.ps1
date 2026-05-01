# NAudio.Asio references Microsoft.Win32.Registry 4.1.3.0 (netstandard2.0
# contract assembly). .NET Framework 4.x HAS the Registry class in mscorlib,
# but the binding redirector doesn't map the contract name through the
# netstandard facade for this type. Ship the netstandard-compatible DLL
# from the nuget package so the assembly loader finds it by its expected
# name. Version 4.7.0 is the latest stable and supports netstandard2.0.
$ErrorActionPreference = 'Stop'

$outDir    = 'F:\Claude AI\Master FM\build_tools\naudio'
$rootProj  = 'F:\Claude AI\Master FM'

$url  = 'https://www.nuget.org/api/v2/package/Microsoft.Win32.Registry/4.7.0'
$zip  = Join-Path $outDir 'Microsoft.Win32.Registry.zip'
$ext  = Join-Path $outDir 'Microsoft.Win32.Registry'

Write-Host "Downloading Microsoft.Win32.Registry 4.7.0..."
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 60
Write-Host "  -> $zip ($((Get-Item $zip).Length) bytes)"

if (Test-Path $ext) { Remove-Item $ext -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $ext -Force
Write-Host "  Extracted"

# Package has runtimes/win/lib/netstandard2.0/Microsoft.Win32.Registry.dll
# The Windows runtime DLL is the correct one for our needs.
$candidate = Join-Path $ext 'runtimes\win\lib\netstandard2.0\Microsoft.Win32.Registry.dll'
if (-not (Test-Path $candidate)) {
    # Fallback: lib/netstandard2.0 path
    $candidate = Join-Path $ext 'lib\netstandard2.0\Microsoft.Win32.Registry.dll'
}
if (-not (Test-Path $candidate)) {
    Write-Host "WARN: couldn't find Microsoft.Win32.Registry.dll in package"
    Get-ChildItem -Recurse $ext -Filter 'Microsoft.Win32.Registry.dll' | Format-Table FullName
    exit 1
}

$dst = Join-Path $rootProj 'Microsoft.Win32.Registry.dll'
Copy-Item $candidate $dst -Force
Write-Host "Copied -> $dst ($((Get-Item $dst).Length) bytes)"
