param(
    [string]$Root    = 'F:\Claude AI\Master FM',
    [string]$MsiPath = '',
    [string]$OutExe  = ''
)

$ErrorActionPreference = 'Stop'

if (-not $MsiPath) { $MsiPath = Join-Path $Root "Master's FM Install\MastersFM_Setup.msi" }
if (-not (Test-Path $MsiPath)) { Write-Host "MSI not found: $MsiPath"; exit 1 }

$CerPath = Join-Path $Root 'build_tools\signing\MastersFM_publisher.cer'
if (-not (Test-Path $CerPath)) { Write-Host "CER not found (sign MSI first)"; exit 2 }

$Src = Join-Path $Root 'install_bootstrapper.cs'
if (-not $OutExe) {
    # Read APP_VERSION from tray.ps1 so the output exe name tracks the release.
    $trayTxt = Get-Content (Join-Path $Root 'tray.ps1') -Raw
    $appVer = 'v5.0.0'
    if ($trayTxt -match '\$script:APP_VERSION\s*=\s*"([^"]+)"') { $appVer = $matches[1] }
    $OutExe = Join-Path $Root ("Install Master's FM " + $appVer.ToUpper() + ".exe")
}

# Find csc.exe
$csc = $null
foreach ($p in @(
    (Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)) { if (Test-Path $p) { $csc = $p; break } }
if (-not $csc) { Write-Host "csc.exe not found"; exit 3 }

$icon = Join-Path $Root 'MastersFM.ico'

# Build. /target:exe (console window so user sees progress).
# /resource:<file>,<logical-name> embeds files as manifest resources.
$argString = @(
    '/nologo',
    '/target:exe',
    ('/win32icon:"{0}"' -f $icon),
    ('/out:"{0}"' -f $OutExe),
    ('/resource:"{0}",payload.msi'     -f $MsiPath),
    ('/resource:"{0}",publisher.cer'   -f $CerPath),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Security.dll',
    ('"{0}"' -f $Src)
) -join ' '

$p = Start-Process -FilePath $csc -ArgumentList $argString -WorkingDirectory $Root -Wait -PassThru -NoNewWindow
if ($p.ExitCode -ne 0) { Write-Host "csc exit=$($p.ExitCode)"; exit 4 }

Write-Host "Built bootstrapper: $OutExe"
Write-Host "  Size: $((Get-Item $OutExe).Length) bytes"

# Sign the bootstrapper with the same MasterShadex cert so UAC shows the
# correct publisher name.
$signScript = Join-Path $Root 'build_tools\signing\_sign_msi.ps1'
if (Test-Path $signScript) {
    Write-Host "Signing bootstrapper..."
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -MsiPath $OutExe
}

exit 0
