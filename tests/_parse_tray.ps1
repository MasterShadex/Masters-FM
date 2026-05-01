$trayPath = 'F:\Claude AI\Master FM\tray.ps1'
$raw = [System.IO.File]::ReadAllText($trayPath)
$errors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($raw, [ref]$tokens, [ref]$errors)
if ($errors -and $errors.Count -gt 0) {
    Write-Host "PARSE ERRORS in tray.ps1:" -ForegroundColor Red
    foreach ($err in $errors) {
        Write-Host "  Line $($err.Extent.StartLineNumber):$($err.Extent.StartColumnNumber)  $($err.Message)"
    }
    exit 1
}
Write-Host "tray.ps1 parses cleanly (no syntax errors)."
Write-Host "tokens=$($tokens.Count)  ast-node=$($ast.GetType().Name)"
