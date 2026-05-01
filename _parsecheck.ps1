$errs = $null
$null = [System.Management.Automation.PSParser]::Tokenize((Get-Content -Raw .\tray.ps1), [ref]$errs)
if ($errs) {
    $errs | ForEach-Object {
        Write-Host ("LINE {0} COL {1}: {2}" -f $_.Token.StartLine, $_.Token.StartColumn, $_.Message)
    }
    exit 1
} else {
    Write-Host 'tray.ps1 parses OK'
    exit 0
}
