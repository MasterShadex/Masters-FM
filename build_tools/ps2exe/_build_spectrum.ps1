# Compile audio_spectrum.cs → audio_spectrum.exe.
# Separated into its own script so _full_rebuild.ps1 can call it without
# wrestling with nested bash/PowerShell quoting around netstandard.dll's
# path (it has spaces + parens that PowerShell via bash mangles).
param(
    [string]$Root = 'F:\Claude AI\Master FM'
)

$csc = $null
foreach ($p in @(
    (Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)) { if (Test-Path $p) { $csc = $p; break } }
if (-not $csc) { Write-Host "[spectrum] csc.exe not found"; exit 1 }

# NAudio.Core 2.x is netstandard2.0 — needs netstandard.dll facade shim
# that ships with .NET Framework 4.7.2+. Prefer v4.8 when both are installed.
$netstd = $null
foreach ($v in @('v4.8','v4.7.2')) {
    $cand = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\$v\Facades\netstandard.dll"
    if (Test-Path $cand) { $netstd = $cand; break }
}
if (-not $netstd) { Write-Host "[spectrum] netstandard.dll not found"; exit 2 }

$src          = Join-Path $Root 'audio_spectrum.cs'
$out          = Join-Path $Root 'audio_spectrum.exe'
$naudio       = Join-Path $Root 'NAudio.Core.dll'
$naudioWasapi = Join-Path $Root 'NAudio.Wasapi.dll'
# v5.3.0: add NAudio.WinMM (MME WaveIn) + NAudio.Asio (ASIO) for the
# multi-backend capture support. Referenced unconditionally - their DLLs
# are vendored alongside NAudio.Core / NAudio.Wasapi so this never
# breaks a build as long as the download step ran once.
$naudioWinmm  = Join-Path $Root 'NAudio.WinMM.dll'
$naudioAsio   = Join-Path $Root 'NAudio.Asio.dll'
$icon         = Join-Path $Root 'MastersFM.ico'

if (-not (Test-Path $src))          { Write-Host "[spectrum] $src missing"; exit 3 }
if (-not (Test-Path $naudio))       { Write-Host "[spectrum] $naudio missing"; exit 4 }
if (-not (Test-Path $naudioWasapi)) { Write-Host "[spectrum] $naudioWasapi missing"; exit 5 }
if (-not (Test-Path $naudioWinmm))  { Write-Host "[spectrum] $naudioWinmm missing"; exit 6 }
if (-not (Test-Path $naudioAsio))   { Write-Host "[spectrum] $naudioAsio missing"; exit 7 }

# Quote every path that might contain spaces - Start-Process splits
# arguments on whitespace by default, which mangles "Program Files (x86)"
# and any other path with a space in it.
$argString = @(
    '/nologo',
    '/target:exe',
    ('/win32icon:"{0}"' -f $icon),
    ('/out:"{0}"'       -f $out),
    ('/reference:"{0}"' -f $naudio),
    ('/reference:"{0}"' -f $naudioWasapi),
    ('/reference:"{0}"' -f $naudioWinmm),
    ('/reference:"{0}"' -f $naudioAsio),
    ('/reference:"{0}"' -f $netstd),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Net.dll',
    ('"{0}"' -f $src)
) -join ' '

$p = Start-Process -FilePath $csc -ArgumentList $argString -WorkingDirectory $Root -Wait -PassThru -NoNewWindow
exit $p.ExitCode
