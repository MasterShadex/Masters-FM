# Test-compile audio_spectrum.cs directly via csc to see compile errors/warnings
$csc = $null
foreach ($p in @(
    (Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)) { if (Test-Path $p) { $csc = $p; break } }

$netstd = $null
foreach ($v in @('v4.8','v4.7.2')) {
    $cand = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\$v\Facades\netstandard.dll"
    if (Test-Path $cand) { $netstd = $cand; break }
}

$Root = 'F:\Claude AI\Master FM'
$src = Join-Path $Root 'audio_spectrum.cs'
$out = Join-Path $Root 'audio_spectrum.exe'

& $csc `
    /nologo `
    /target:exe `
    "/win32icon:$Root\MastersFM.ico" `
    "/out:$out" `
    "/reference:$Root\NAudio.Core.dll" `
    "/reference:$Root\NAudio.Wasapi.dll" `
    "/reference:$Root\NAudio.WinMM.dll" `
    "/reference:$Root\NAudio.Asio.dll" `
    "/reference:$netstd" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Net.dll `
    $src 2>&1 | Tee-Object -Variable compileOut
Write-Host "---"
Write-Host "exit=$LASTEXITCODE"
