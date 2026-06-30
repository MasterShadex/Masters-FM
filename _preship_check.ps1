# _preship_check.ps1 - the "checker" gate for Master's FM ships.
#
# Adapted from loop-engineering (github.com/cobusgreyling/loop-engineering):
# the maker/checker split. The MAKER is whoever changed the code; this CHECKER
# runs independent verification before the HUMAN GATE (the operator) ships.
# Every check below maps to a real failure we shipped this cycle:
#
#   * v14.2.0/v14.2.1 broken Audio-source menu, bricked installs
#       -> HeadlessTester ContextMenu walk must report 0 anomalies
#   * tray version stuck at "14.0.0" for every release
#       -> csproj <Version> must equal version.json "version"
#   * autoInstall=true silently installed over a running tray, no relaunch
#       -> autoInstall must be false until the updater relaunch bug is fixed
#   * versions like 14.1.10 (two-digit patch) violate the "xx.x.x" rule
#       -> version shape check
#   * stale / malformed version.json (wrong tag in msi_url, bad sha)
#       -> manifest shape check
#
# Output: a PASS/WARN/FAIL line per check + an overall verdict. Exit code 0 =
# SHIP-OK, non-zero = BLOCK. Appends every run to logs\preship_runlog.md (the
# loop-engineering "STATE / run-log" spine).
#
# USAGE:
#   .\_preship_check.ps1              # full gate (builds all + HeadlessTester)
#   .\_preship_check.ps1 -SkipBuild   # fast checks only (version/manifest/git)
#   .\_preship_check.ps1 -ExpectVersion 14.2.4   # assert the version we intend to ship

param(
    [switch]$SkipBuild,
    [string]$ExpectVersion = ''
)

$root = 'G:\Project Folder\Master FM'
Set-Location $root
$checks = New-Object System.Collections.ArrayList

function Add-Check([string]$name, [string]$status, [string]$detail) {
    # status: PASS | WARN | FAIL
    [void]$checks.Add([pscustomobject]@{ Name=$name; Status=$status; Detail=$detail })
}

Write-Host ""
Write-Host "=== Master's FM pre-ship checker ===" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------------------
# 1. version.json: valid JSON + shape
# ---------------------------------------------------------------------------
$vjPath = Join-Path $root 'version.json'
$vj = $null
$vjVersion = $null
if (-not (Test-Path $vjPath)) {
    Add-Check 'version.json present' 'FAIL' 'version.json not found at repo root'
} else {
    try {
        $raw = (Get-Content $vjPath -Raw).TrimStart([char]0xFEFF)
        $vj  = $raw | ConvertFrom-Json
        $vjVersion = [string]$vj.version
        Add-Check 'version.json parse' 'PASS' "version=$vjVersion autoInstall=$($vj.autoInstall)"
    } catch {
        Add-Check 'version.json parse' 'FAIL' ("invalid JSON: " + $_.Exception.Message)
    }
}

if ($vj) {
    # version shape: x.y.z, last segment single digit (the "xx.x.x not xx.x.xx" rule)
    if ($vjVersion -notmatch '^\d+\.\d+\.\d+$') {
        Add-Check 'version shape' 'FAIL' "'$vjVersion' is not x.y.z"
    } elseif ($vjVersion -match '^\d+\.\d+\.\d{2,}$') {
        Add-Check 'version shape' 'WARN' "'$vjVersion' has a two-digit patch; rule is xx.x.x (single-digit patch)"
    } else {
        Add-Check 'version shape' 'PASS' "$vjVersion"
    }

    # msi_url tag must match the version
    if ($vj.msi_url -and ($vj.msi_url -notmatch "v$([regex]::Escape($vjVersion))/")) {
        Add-Check 'msi_url tag' 'FAIL' "msi_url does not reference v$vjVersion : $($vj.msi_url)"
    } else {
        Add-Check 'msi_url tag' 'PASS' "points at v$vjVersion"
    }

    # sha256 present + 64 hex
    if ($vj.msi_sha256 -match '^[0-9a-fA-F]{64}$') {
        Add-Check 'msi_sha256' 'PASS' ($vj.msi_sha256.Substring(0,12) + '...')
    } else {
        Add-Check 'msi_sha256' 'FAIL' 'missing or not 64 hex chars'
    }

    # autoInstall gate: must be false until the updater relaunch bug is fixed
    if ($vj.autoInstall -eq $true) {
        Add-Check 'autoInstall' 'FAIL' 'autoInstall=true; silent install kills the tray with no relaunch. Keep false until updater is fixed.'
    } else {
        Add-Check 'autoInstall' 'PASS' 'false (notify-only, safe)'
    }
}

# ---------------------------------------------------------------------------
# 2. csproj <Version> == version.json version  (the "stuck at 14.0.0" guard)
# ---------------------------------------------------------------------------
$csprojPath = Join-Path $root 'src\tray_csharp\MastersFM_Tray_v14.csproj'
$csprojVersion = $null
if (Test-Path $csprojPath) {
    $cs = Get-Content $csprojPath -Raw
    $m = [regex]::Match($cs, '<Version[^>]*>(\d+\.\d+\.\d+)</Version>')
    if ($m.Success) {
        $csprojVersion = $m.Groups[1].Value
        if ($vjVersion -and $csprojVersion -ne $vjVersion) {
            Add-Check 'csproj==manifest version' 'FAIL' "tray csproj=$csprojVersion but version.json=$vjVersion (binaries would carry the wrong version)"
        } else {
            Add-Check 'csproj==manifest version' 'PASS' "$csprojVersion"
        }
    } else {
        Add-Check 'csproj <Version>' 'WARN' 'could not parse <Version> from tray csproj'
    }
} else {
    Add-Check 'csproj present' 'FAIL' 'tray csproj not found'
}

# Optional: assert the version we intend to ship
if ($ExpectVersion -ne '') {
    if ($vjVersion -eq $ExpectVersion -and $csprojVersion -eq $ExpectVersion) {
        Add-Check 'expected version' 'PASS' "both at $ExpectVersion"
    } else {
        Add-Check 'expected version' 'FAIL' "expected $ExpectVersion but csproj=$csprojVersion manifest=$vjVersion"
    }
}

# ---------------------------------------------------------------------------
# 3. git working tree (informational - what would ship)
# ---------------------------------------------------------------------------
try {
    $dirty = (& git status --porcelain 2>$null | Where-Object { $_ -match '^[ MARC]' })
    if ($dirty) {
        Add-Check 'git tracked changes' 'WARN' ("$($dirty.Count) tracked file(s) modified/staged - confirm they are intended for this ship")
    } else {
        Add-Check 'git tracked changes' 'PASS' 'working tree clean (tracked)'
    }
} catch {
    Add-Check 'git status' 'WARN' 'git not available'
}

# ---------------------------------------------------------------------------
# 4. build all projects  (compile gate)        [skippable]
# ---------------------------------------------------------------------------
if ($SkipBuild) {
    Add-Check 'build all' 'WARN' 'skipped (-SkipBuild)'
} else {
    $projects = @(
        'src\audio_spectrum.csproj',
        'src\customize.csproj',
        'src\launcher.csproj',
        'src\tray_native\tray_native.csproj',
        'src\tray_csharp\MastersFM_Tray_v14.csproj',
        'src\server_dotnet\server_dotnet.csproj',
        'src\obs_cleanup\MastersFM_ObsCleanup.csproj',
        'src\updater\updater.csproj'
    )
    $buildFails = @()
    foreach ($p in $projects) {
        $full = Join-Path $root $p
        if (-not (Test-Path $full)) { continue }
        & dotnet build $full -c Release --nologo -v quiet 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { $buildFails += $p }
    }
    if ($buildFails.Count -eq 0) {
        Add-Check 'build all' 'PASS' "$($projects.Count) projects compiled"
    } else {
        Add-Check 'build all' 'FAIL' ("compile errors in: " + ($buildFails -join ', '))
    }
}

# ---------------------------------------------------------------------------
# 5. HeadlessTester: 0 menu/UI anomalies  (the v14.2.x menu-bug guard)  [skippable]
# ---------------------------------------------------------------------------
if ($SkipBuild) {
    Add-Check 'HeadlessTester' 'WARN' 'skipped (-SkipBuild)'
} else {
    $htProj = Join-Path $root 'tests\HeadlessTester\HeadlessTester.csproj'
    if (Test-Path $htProj) {
        & dotnet build $htProj -c Debug --nologo -v quiet 2>&1 | Out-Null
        $htExe = Join-Path $root 'tests\HeadlessTester\bin\Debug\net8.0-windows10.0.19041.0\HeadlessTester.exe'
        if (Test-Path $htExe) {
            $htOut = & $htExe 2>&1
            $line  = ($htOut | Select-String -Pattern 'Anomalies:\s*(\d+)\s+Failures:\s*(\d+)' | Select-Object -First 1)
            if ($line) {
                $anom = [int]$line.Matches[0].Groups[1].Value
                $fail = [int]$line.Matches[0].Groups[2].Value
                if ($anom -gt 0) {
                    Add-Check 'HeadlessTester anomalies' 'FAIL' "$anom menu/structure anomaly(ies) - the v14.2.x menu-bug class"
                } else {
                    Add-Check 'HeadlessTester anomalies' 'PASS' "0 anomalies ($fail dialog render fail(s), non-blocking)"
                }
            } else {
                Add-Check 'HeadlessTester' 'WARN' 'ran but could not parse the anomaly line'
            }
        } else {
            Add-Check 'HeadlessTester' 'WARN' 'built but exe not found'
        }
    } else {
        Add-Check 'HeadlessTester' 'WARN' 'HeadlessTester project not found'
    }
}

# ---------------------------------------------------------------------------
# Verdict + run-log
# ---------------------------------------------------------------------------
Write-Host ""
foreach ($c in $checks) {
    $color = switch ($c.Status) { 'PASS' {'Green'} 'WARN' {'Yellow'} 'FAIL' {'Red'} default {'Gray'} }
    Write-Host ("  [{0}] {1,-26} {2}" -f $c.Status, $c.Name, $c.Detail) -ForegroundColor $color
}

# @() forces an array so .Count is correct even when exactly one item matches
# (PS5.1 returns a scalar - and $null .Count - for a single Where-Object match).
$nFail = @($checks | Where-Object { $_.Status -eq 'FAIL' }).Count
$nWarn = @($checks | Where-Object { $_.Status -eq 'WARN' }).Count
$verdict = if ($nFail -gt 0) { 'BLOCK' } elseif ($nWarn -gt 0) { 'REVIEW' } else { 'SHIP-OK' }
$vColor  = if ($nFail -gt 0) { 'Red' } elseif ($nWarn -gt 0) { 'Yellow' } else { 'Green' }

Write-Host ""
Write-Host ("=== VERDICT: {0}  ({1} fail, {2} warn) ===" -f $verdict, $nFail, $nWarn) -ForegroundColor $vColor
if ($verdict -ne 'SHIP-OK') {
    Write-Host "    Human gate: operator decides. Do NOT ship on BLOCK without fixing the FAILs." -ForegroundColor $vColor
}
Write-Host ""

# Append to the run-log (durable STATE)
$logDir = Join-Path $root 'logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$logPath = Join-Path $logDir 'preship_runlog.md'
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## $stamp  -> $verdict  ($nFail fail, $nWarn warn)")
foreach ($c in $checks) { [void]$sb.AppendLine("- [$($c.Status)] $($c.Name): $($c.Detail)") }
Add-Content -Path $logPath -Value $sb.ToString() -Encoding utf8

# Exit code: 0 = SHIP-OK, 1 = REVIEW (warns only), 2 = BLOCK (fails)
if ($nFail -gt 0) { exit 2 } elseif ($nWarn -gt 0) { exit 1 } else { exit 0 }
