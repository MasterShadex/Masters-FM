# Compile tray.ps1 → MastersFM_Tray.exe via ps2exe.
# Invoked from _full_rebuild.ps1 as a separate step so the nested quoting
# doesn't bite (backtick-continuation on a Bash → pwsh chain eats the args).
param(
    [string]$InputPs1    = 'F:\Claude AI\Master FM\tray.ps1',
    [string]$OutputExe   = 'F:\Claude AI\Master FM\MastersFM_Tray.exe',
    [string]$Ps2ExeScript= 'F:\Claude AI\Master FM\build_tools\ps2exe\ps2exe.ps1',
    [string]$Icon        = 'F:\Claude AI\Master FM\MastersFM.ico'
)

& $Ps2ExeScript `
    -inputFile   $InputPs1 `
    -outputFile  $OutputExe `
    -iconFile    $Icon `
    -title       "Master's FM" `
    -description "Master's FM Tray" `
    -product     "Master's FM" `
    -company     'MasterShadex' `
    -version     '1.9.9.0' `
    -noConsole `
    -STA `
    -noOutput `
    -verbose:$false

# Exit with the underlying ps2exe's exit code so the caller can react.
exit $LASTEXITCODE
