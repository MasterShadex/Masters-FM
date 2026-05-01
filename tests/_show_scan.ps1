# Display catch-scan results for files that have any catches.
Import-Csv 'F:\Claude AI\Master FM\_scan_catches.csv' |
    Where-Object { [int]$_.CatchCount -gt 0 } |
    Sort-Object { [int]$_.N } |
    ForEach-Object { '{0,3}  {1,-70}  {2,4}  {3}' -f $_.N, $_.Path, $_.CatchCount, $_.Ext }
