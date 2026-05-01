# Print Product/Version info for every Master's FM exe — so we can confirm
# Task Manager grouping collapses them into one row (requires matching
# ProductName across all three exes).
Get-ChildItem 'C:\Users\Master\AppData\Local\MastersFM\*.exe' | ForEach-Object {
    $v = $_.VersionInfo
    $line = $_.Name.PadRight(30) + " Product='" + $v.ProductName +
            "'  Company='" + $v.CompanyName + "'  FileVersion=" + $v.FileVersion
    Write-Host $line
}
