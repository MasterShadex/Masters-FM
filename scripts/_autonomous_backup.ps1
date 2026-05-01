# Autonomous-run backup script (STEP 0)
# Creates F:\Claude AI\_BACKUPS_<timestamp>\ with two subfolders:
#   Master FM_SOURCE_BACKUP   - robocopy of source tree
#   MastersFM_APPDATA_SNAPSHOT - robocopy of runtime AppData folder
# Then Compress-Archive both as zip belt-and-braces.

$ErrorActionPreference = 'Stop'

$ts = Get-Date -Format 'yyyy-MM-dd_HH-mm'
$backupRoot = "F:\Claude AI\_BACKUPS_$ts"
$srcFrom    = "F:\Claude AI\Master FM"
$dataFrom   = "C:\Users\Master\AppData\Local\MastersFM"
$srcTo      = Join-Path $backupRoot "Master FM_SOURCE_BACKUP"
$dataTo     = Join-Path $backupRoot "MastersFM_APPDATA_SNAPSHOT"

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

# Full backup - no exclusions. Disk is cheap, a reliable byte-for-byte snapshot
# matters more than dodging node_modules. Makes the source vs dest count check
# trivially correct (robocopy copies everything, we count everything).
$excludeDirs = @()

Write-Host "=== BACKUP START ===" -ForegroundColor Cyan
Write-Host "Root      : $backupRoot"
Write-Host "SrcFrom   : $srcFrom"
Write-Host "SrcTo     : $srcTo"
Write-Host "DataFrom  : $dataFrom"
Write-Host "DataTo    : $dataTo"
Write-Host ""

# --- Source backup ---
$exdArgs = @()
foreach ($d in $excludeDirs) { $exdArgs += '/XD'; $exdArgs += (Join-Path $srcFrom $d) }
$null = & robocopy $srcFrom $srcTo /E /COPY:DAT /R:1 /W:1 /NFL /NDL /NP @exdArgs
$rc1Exit = $LASTEXITCODE
Write-Host "robocopy(source) exit=$rc1Exit (0-7 = success)"

# --- AppData snapshot ---
$null = & robocopy $dataFrom $dataTo /E /COPY:DAT /R:1 /W:1 /NFL /NDL /NP
$rc2Exit = $LASTEXITCODE
Write-Host "robocopy(appdata) exit=$rc2Exit (0-7 = success)"

if ($rc1Exit -ge 8 -or $rc2Exit -ge 8) {
    Write-Host "FAIL: robocopy error code >=8" -ForegroundColor Red
    exit 1
}

# --- File count sanity check ---
function Count-Files($path) {
    if (-not (Test-Path $path)) { return 0 }
    (Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object).Count
}
function Count-FilesExcluded($path, $excludeNames) {
    if (-not (Test-Path $path)) { return 0 }
    (Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $full = $_.FullName
            $hit = $false
            foreach ($n in $excludeNames) { if ($full -like "*\$n\*") { $hit = $true; break } }
            -not $hit
        } | Measure-Object).Count
}

$srcCount  = Count-FilesExcluded $srcFrom $excludeDirs
$dstCount  = Count-Files $srcTo
$dataCount = Count-Files $dataFrom
$dataDst   = Count-Files $dataTo

Write-Host ""
Write-Host "File-count verify"
Write-Host "  source : $srcCount -> $dstCount"
Write-Host "  appdata: $dataCount -> $dataDst"

if ($srcCount -ne $dstCount) {
    Write-Host "FAIL: source file count mismatch" -ForegroundColor Red
    exit 2
}
if ($dataCount -ne $dataDst) {
    Write-Host "WARN: appdata file count mismatch (runtime may have locked files)" -ForegroundColor Yellow
    if ($dataCount -gt 0 -and ($dataDst / [double]$dataCount) -lt 0.9) {
        Write-Host "FAIL: less than 90 percent of appdata files backed up - giving up" -ForegroundColor Red
        exit 3
    }
}

# --- Zip belts-and-braces ---
$srcZip  = "$backupRoot\Master_FM_SOURCE_BACKUP.zip"
$dataZip = "$backupRoot\MastersFM_APPDATA_SNAPSHOT.zip"
Write-Host ""
Write-Host "Compressing source backup -> $srcZip"
Compress-Archive -Path "$srcTo\*" -DestinationPath $srcZip -Force -CompressionLevel Fastest
Write-Host "Compressing appdata snapshot -> $dataZip"
Compress-Archive -Path "$dataTo\*" -DestinationPath $dataZip -Force -CompressionLevel Fastest

Write-Host ""
Write-Host "=== BACKUP DONE ===" -ForegroundColor Green
Write-Host "BACKUP_ROOT=$backupRoot"
Write-Host "SRC_BACKUP=$srcTo"
Write-Host "DATA_BACKUP=$dataTo"
Write-Host "SRC_ZIP=$srcZip"
Write-Host "DATA_ZIP=$dataZip"

$manifest = "BACKUP_ROOT=$backupRoot`r`nSRC_BACKUP=$srcTo`r`nDATA_BACKUP=$dataTo`r`nSRC_ZIP=$srcZip`r`nDATA_ZIP=$dataZip`r`nSRC_FILE_COUNT=$srcCount`r`nDATA_FILE_COUNT=$dataCount`r`n"
$manifest | Out-File -FilePath "$backupRoot\_backup_manifest.txt" -Encoding utf8
