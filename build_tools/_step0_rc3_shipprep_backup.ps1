# RC.3 SHIP-PREP STEP 0: checkpoint backup
# Run from project root.

$root = "G:\Project Folder\Master FM"
$backup = "$root\_BACKUPS_2026-05-10_18-29_RC3_SHIPPREP_PRE"

Write-Host "Creating backup directory: $backup"
New-Item -ItemType Directory -Path $backup -Force | Out-Null

# S0.2 -- src snapshot
Write-Host "robocopy src..."
robocopy "$root\src" "$backup\src_snapshot" /E /COPYALL /R:1 /W:1 /NP /NFL /NDL | Out-Null
Write-Host "  src done: $((Get-ChildItem $backup\src_snapshot -Recurse -File).Count) files"

# S0.3 -- md snapshot
Write-Host "robocopy md..."
robocopy "$root\md" "$backup\md_snapshot" /E /COPYALL /R:1 /W:1 /NP /NFL /NDL | Out-Null
Write-Host "  md done: $((Get-ChildItem $backup\md_snapshot -Recurse -File).Count) files"

# S0.4 -- installed runtime
Write-Host "robocopy installed..."
robocopy "$env:LOCALAPPDATA\MastersFM" "$backup\installed_pre_rc3" /E /R:1 /W:1 /NP /NFL /NDL | Out-Null
$installedCount = (Get-ChildItem "$backup\installed_pre_rc3" -Recurse -File -ErrorAction SilentlyContinue).Count
Write-Host "  installed done: $installedCount files"

# S0.5 -- OBS scene-collection.json files
Write-Host "robocopy OBS scenes..."
$obsScenes = "$env:APPDATA\obs-studio\basic\scenes"
if (Test-Path $obsScenes) {
    robocopy $obsScenes "$backup\obs_scene_collections" *.json /R:1 /W:1 /NP /NFL /NDL | Out-Null
    $obsCount = (Get-ChildItem "$backup\obs_scene_collections" -File -ErrorAction SilentlyContinue).Count
    Write-Host "  OBS scenes done: $obsCount files"
} else {
    Write-Host "  WARNING: OBS scenes dir not found at $obsScenes"
    New-Item -ItemType Directory -Path "$backup\obs_scene_collections" -Force | Out-Null
    "OBS scenes dir not found at $obsScenes" | Out-File "$backup\obs_scene_collections\NOTE.txt"
}

# S0.6 -- compress src_snapshot
Write-Host "Compressing src_snapshot..."
Compress-Archive -Path "$backup\src_snapshot" -DestinationPath "$backup\src_snapshot.zip" -Force
$zipSize = [math]::Round((Get-Item "$backup\src_snapshot.zip").Length / 1MB, 1)
Write-Host "  zip done: $zipSize MB"

# S0.7 -- git log
Write-Host "git log..."
Set-Location $root
git log --oneline -20 | Out-File "$backup\git_log_pre_rc3.txt" -Encoding utf8
Write-Host "  git log saved"

# S0.8 -- git tags
Write-Host "git tags..."
git tag --list "v14*" | Out-File "$backup\existing_tags.txt" -Encoding utf8
$tagContent = Get-Content "$backup\existing_tags.txt" -ErrorAction SilentlyContinue
Write-Host "  existing v14 tags: $(if ($tagContent) { $tagContent -join ', ' } else { '(none)' })"

# S0.9 -- verify zip integrity
Write-Host "Verifying zip..."
$zipOk = $false
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead("$backup\src_snapshot.zip")
    $entryCount = $zip.Entries.Count
    $zip.Dispose()
    Write-Host "  zip integrity OK: $entryCount entries"
    $zipOk = $true
} catch {
    Write-Host "  WARNING: zip verification failed: $_"
}

# Summary
Write-Host ""
Write-Host "STEP 0 BACKUP COMPLETE"
Write-Host "  Backup dir: $backup"
Write-Host "  src_snapshot: $((Get-ChildItem $backup\src_snapshot -Recurse -File).Count) files"
Write-Host "  md_snapshot: $((Get-ChildItem $backup\md_snapshot -Recurse -File).Count) files"
Write-Host "  installed_pre_rc3: $installedCount files"
Write-Host "  src_snapshot.zip: $zipSize MB, $entryCount entries, integrity=$(if ($zipOk) {'OK'} else {'WARN'})"
Write-Host "  git_log_pre_rc3.txt: written"
Write-Host "  existing_tags.txt: $(if ($tagContent) { $tagContent -join ', ' } else { '(none)' })"
