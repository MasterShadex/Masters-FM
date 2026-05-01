# List every first-party source file in the project root (excludes helper
# scripts prefixed with '_' and vendored trees node_modules/, build_tools/,
# logs/, dist/, tool-results/, etc.).
$root = 'F:\Claude AI\Master FM'
$firstPartyExt = @('.js','.cs','.ps1','.py','.html','.bat','.json','.md','.txt','.cer','.ico')
Get-ChildItem $root -File |
    Where-Object {
        $firstPartyExt -contains $_.Extension -and -not $_.Name.StartsWith('_')
    } |
    ForEach-Object { '{0,-48} {1,10:N0} bytes' -f $_.Name, $_.Length } |
    Sort-Object

Write-Host ""
Write-Host "--- Master's FM Install\ ---"
Get-ChildItem (Join-Path $root "Master's FM Install") -File -ErrorAction SilentlyContinue |
    ForEach-Object { '{0,-48} {1,10:N0} bytes' -f $_.Name, $_.Length }

Write-Host ""
Write-Host "--- build_tools (direct only, no node_modules) ---"
Get-ChildItem (Join-Path $root "build_tools") -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\node_modules\\' } |
    ForEach-Object { '{0,-70} {1,10:N0} bytes' -f ($_.FullName.Substring($root.Length + 1)), $_.Length }
