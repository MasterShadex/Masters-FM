# Automated smoke test — runs after each rebuild to catch regressions.
# ASCII only; designed to be called by the autonomous-loop orchestration.
# Output format: parseable key=value lines so the loop can grep for FAIL.
param([string]$OutFile)

$lines = @()
function Result($key, $pass, $detail) {
    $status = if ($pass) { "PASS" } else { "FAIL" }
    $line = "$status | $key | $detail"
    $script:lines += $line
    Write-Host $line
}

# 1. All three processes alive.
#    v2.0.0 can run tray.ps1 EITHER as a child powershell.exe (legacy path)
#    OR inside MastersFM_Tray.exe (hosted PowerShell via SMA) — either one
#    satisfies the "tray is up" check.
$procs = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq "MastersFM.exe" -or
    $_.Name -eq "server.exe" -or
    $_.Name -eq "MastersFM_Tray.exe" -or
    $_.Name -eq "audio_spectrum.exe" -or
    ($_.Name -eq "powershell.exe" -and $_.CommandLine -like "*tray.ps1*")
}
$names = @($procs | ForEach-Object { $_.Name }) | Sort-Object -Unique
$hasTray = ($names -contains "MastersFM_Tray.exe") -or ($names -contains "powershell.exe")
$allUp = ($names -contains "MastersFM.exe") -and ($names -contains "server.exe") -and $hasTray
Result "processes" $allUp ("names=" + ($names -join ","))

# 2. Server HTTP 200
$httpOk = $false
try {
    $r = Invoke-WebRequest "http://localhost:4242/current" -TimeoutSec 3 -UseBasicParsing
    $httpOk = ($r.StatusCode -eq 200)
    Result "http" $httpOk "status=$($r.StatusCode)"
} catch {
    Result "http" $false "error=$($_.Exception.Message)"
}

# 3. Discord RPC connected + READY seen recently
$dOk = $false
try {
    $last = Get-Content "C:\Users\Master\AppData\Local\MastersFM\server.log" -Tail 200 -ErrorAction Stop |
            Where-Object { $_ -match "\[discord\] READY" } |
            Select-Object -Last 1
    $dOk = [bool]$last
    Result "discord" $dOk $(if ($dOk) { "latest=$($last.Substring(0, [Math]::Min(60, $last.Length)))" } else { "no READY line in server.log" })
} catch { Result "discord" $false "log read error" }

# 4. Startup-folder shortcut present — the tray creates this ~5-10 s after
#    first launch (Invoke-AutoStartMigration runs after the welcome flow).
#    Wait up to 30 s for it to appear so rebuild tests don't false-fail.
$lnk = Join-Path ([System.Environment]::GetFolderPath("Startup")) "Master's FM.lnk"
$lnkOk = $false
for ($i = 0; $i -lt 30; $i++) {
    if (Test-Path $lnk) { $lnkOk = $true; break }
    Start-Sleep -Seconds 1
}
Result "startup-lnk" $lnkOk "path=$lnk"

# 5. Legacy Run entry removed
$runGone = $true
$runVal = ""
try {
    $runVal = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction Stop).MastersFM
    if ($runVal) { $runGone = $false }
} catch {}
Result "legacy-run-gone" $runGone $(if ($runGone) { "clean" } else { "still=$runVal" })

# 6. /version endpoint + bootId
try {
    $v = (Invoke-WebRequest "http://localhost:4242/version" -TimeoutSec 3 -UseBasicParsing).Content | ConvertFrom-Json
    Result "version-endpoint" ([bool]$v.bootId) "bootId=$($v.bootId)"
} catch { Result "version-endpoint" $false "error" }

# 7. /preview-config endpoint
try {
    $r = Invoke-WebRequest "http://localhost:4242/preview-config" -TimeoutSec 3 -UseBasicParsing
    Result "preview-config" ($r.StatusCode -eq 200) "status=$($r.StatusCode)"
} catch { Result "preview-config" $false "error" }

# 8. /art proxy (may be 404 if no art yet — that's OK, we just want a response)
try {
    $ts = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    $r = Invoke-WebRequest "http://localhost:4242/art?t=$ts" -TimeoutSec 4 -UseBasicParsing
    Result "art-proxy" $true "status=$($r.StatusCode) bytes=$($r.RawContentLength)"
} catch {
    # 404 is fine (no current art); 0 connection is FAIL
    $msg = $_.Exception.Message
    if ($msg -match "404") { Result "art-proxy" $true "404=no-art-set" }
    else { Result "art-proxy" $false "error=$msg" }
}

# 9. Recent real errors — ignore the benign "hook installed" startup line
$errCount = 0
try {
    $errs = Get-Content "C:\Users\Master\AppData\Local\MastersFM\overlay.log" -Tail 150 -ErrorAction Stop |
            Where-Object {
                ($_ -match "WinForms ThreadException" -and $_ -notmatch "hook installed") -or
                ($_ -match "FATAL CRASH") -or
                ($_ -match "Unhandled exception")
            }
    $errCount = @($errs).Count
} catch {}
Result "recent-errors" ($errCount -eq 0) "count=$errCount"

# 10. Current track (informational only)
try {
    $t = (Invoke-WebRequest "http://localhost:4242/current" -TimeoutSec 3 -UseBasicParsing).Content | ConvertFrom-Json
    if ($t) {
        $elapsed = [Math]::Round(([DateTimeOffset]::Now.ToUnixTimeMilliseconds() - $t.startedAt) / 1000)
        Write-Host ""
        Write-Host "Current track: $($t.artist) - $($t.track)"
        Write-Host ("  " + $elapsed + "s / " + [Math]::Round($t.duration / 1000) + "s  source=" + $t.source + "  art=" + $(if ($t.trackArt) { "YES" } else { "NO" }))
    }
} catch {}

# Summary
$passCount = @($lines | Where-Object { $_ -match "^PASS" }).Count
$failCount = @($lines | Where-Object { $_ -match "^FAIL" }).Count
Write-Host ""
Write-Host "=== SUMMARY: $passCount PASS / $failCount FAIL ==="

if ($OutFile) {
    $header = "=== Smoke test $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') === $passCount PASS / $failCount FAIL"
    $body = ($header, "") + $lines + "" | Out-String
    try {
        Add-Content -Path $OutFile -Value $body -ErrorAction Stop
    } catch {}
}

if ($failCount -gt 0) { exit 1 } else { exit 0 }
