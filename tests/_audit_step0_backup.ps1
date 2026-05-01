$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm'
$bkRoot = "F:\Claude AI\_BACKUPS_$stamp"
$bkSrc  = "$bkRoot\Master FM_SOURCE_BACKUP"
$bkApp  = "$bkRoot\MastersFM_APPDATA_SNAPSHOT"

Write-Host ("Backup root: " + $bkRoot)
New-Item -ItemType Directory -Path $bkRoot -Force | Out-Null
New-Item -ItemType Directory -Path $bkSrc -Force  | Out-Null
New-Item -ItemType Directory -Path $bkApp -Force  | Out-Null

Write-Host ""
Write-Host "=== robocopy source ==="
$srcRoot = "F:\Claude AI\Master FM"
& robocopy $srcRoot $bkSrc /E /COPYALL /R:1 /W:1 /XD node_modules .git 2>&1 | Out-Null
$srcExit = $LASTEXITCODE
Write-Host ("  robocopy exit code: " + $srcExit + " (anything < 8 is success)")

Write-Host ""
Write-Host "=== robocopy runtime (read-only snapshot) ==="
$appRoot = "C:\Users\Master\AppData\Local\MastersFM"
& robocopy $appRoot $bkApp /E /COPYALL /R:1 /W:1 2>&1 | Out-Null
$appExit = $LASTEXITCODE
Write-Host ("  robocopy exit code: " + $appExit + " (anything < 8 is success)")

if ($srcExit -ge 8) { throw "source robocopy FAILED (exit $srcExit)" }
if ($appExit -ge 8) { throw "runtime robocopy FAILED (exit $appExit)" }

Write-Host ""
Write-Host "=== Compress-Archive zips ==="
$zipSrc = $bkSrc + ".zip"
$zipApp = $bkApp + ".zip"
Compress-Archive -Path ($bkSrc + "\*") -DestinationPath $zipSrc -CompressionLevel Fastest -Force
Compress-Archive -Path ($bkApp + "\*") -DestinationPath $zipApp -CompressionLevel Fastest -Force
Write-Host ("  source zip:  " + $zipSrc)
Write-Host ("  runtime zip: " + $zipApp)

Write-Host ""
Write-Host "=== Verification ==="
$srcFiles = (Get-ChildItem $bkSrc -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
$appFiles = (Get-ChildItem $bkApp -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
$srcSize  = [math]::Round((Get-Item $zipSrc).Length / 1MB, 2)
$appSize  = [math]::Round((Get-Item $zipApp).Length / 1MB, 2)
Write-Host ("  source backup files: "  + $srcFiles)
Write-Host ("  runtime backup files: " + $appFiles)
Write-Host ("  source zip size:  "     + $srcSize + " MB")
Write-Host ("  runtime zip size: "     + $appSize + " MB")

Write-Host ""
Write-Host "STEP 0 DONE - backups ready."
Write-Host ("  " + $bkSrc)
Write-Host ("  " + $zipSrc)
Write-Host ("  " + $bkApp)
Write-Host ("  " + $zipApp)
Write-Host ""
Write-Host ("BACKUP_ROOT=" + $bkRoot)
