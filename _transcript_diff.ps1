# Does the transcript.log continue to accumulate TerminatingError entries
# POST the registry-read fix?
$p = 'C:\Users\Master\AppData\Local\MastersFM\transcript.log'
$before = (Get-Content $p).Count
$beforeErr = (Get-Content $p | Select-String 'MastersFM does not exist').Count
Write-Host "t=0: lines=$before, TerminatingErrors=$beforeErr"
Start-Sleep -Seconds 30
$after = (Get-Content $p).Count
$afterErr = (Get-Content $p | Select-String 'MastersFM does not exist').Count
Write-Host "t=30s: lines=$after (+$($after - $before)), TerminatingErrors=$afterErr (+$($afterErr - $beforeErr))"
