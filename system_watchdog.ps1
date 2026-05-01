# system_watchdog.ps1 — Master's FM system-wide freeze monitor
# v9.10.0 — 2026-05-01
#
# PURPOSE
#   The tray-side [CANARY] and SLOW TICK logs tell us what Master's FM was doing
#   when a freeze happened. This script captures the SYSTEM view: DWM, Explorer,
#   and the top 5 CPU consumers every 500 ms. Comparing the timestamps in these
#   two logs pinpoints the exact process or action that triggered the freeze.
#
# USAGE
#   Run from an elevated or normal PowerShell window BEFORE playing music.
#   Keep it running in the background. When a freeze occurs:
#     1. Note the time (or just let it run).
#     2. After the freeze, Ctrl+C the watchdog.
#     3. Send the CSV at $OutPath to the next investigation session.
#
#   Example:
#     powershell -ExecutionPolicy Bypass -File "G:\...\system_watchdog.ps1"
#
# OUTPUT
#   system_watchdog_<timestamp>.csv in the same folder as this script.
#   Columns: EpochMs,DateTime,DWM_CPU,Explorer_CPU,TrayPID,Tray_CPU,
#             Tray_Handles,Tray_WS_MB,TopProcs,SampleLateMs
#
#   SampleLateMs > 1000 means Windows was frozen between samples (sample arrived
#   >1 s late when it should arrive every 500 ms). This is your freeze marker.

$ErrorActionPreference = 'SilentlyContinue'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp     = Get-Date -Format 'yyyyMMdd_HHmmss'
$OutPath   = Join-Path $scriptDir "system_watchdog_$stamp.csv"

# Header
"EpochMs,DateTime,DWM_CPU,Explorer_CPU,TrayPID,Tray_CPU,Tray_Handles,Tray_WS_MB,TopProcs,SampleLateMs" | Out-File $OutPath -Encoding UTF8

Write-Host "[system_watchdog] Logging to $OutPath"
Write-Host "[system_watchdog] Sample interval: 500 ms. Press Ctrl+C to stop."
Write-Host "[system_watchdog] SampleLateMs > 1000 = Windows freeze detected."

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$lastMs = 0

while ($true) {
    $nowMs = $sw.ElapsedMilliseconds
    $lateMs = if ($lastMs -gt 0) { [long]($nowMs - $lastMs - 500) } else { 0 }
    $lastMs = $nowMs

    $dt   = Get-Date -Format 'HH:mm:ss.fff'
    $epoch = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()

    # DWM CPU
    $dwmCpu = 0
    try {
        $dwmP = Get-Process -Name 'dwm' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($dwmP) { $dwmCpu = [math]::Round($dwmP.CPU, 2) }
    } catch {}

    # Explorer CPU
    $exCpu = 0
    try {
        $exP = Get-Process -Name 'explorer' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($exP) { $exCpu = [math]::Round($exP.CPU, 2) }
    } catch {}

    # Master's FM Tray
    $trayPid = 0; $trayCpu = 0; $trayHandles = 0; $trayWsMB = 0
    try {
        $trayP = Get-Process -Name 'MastersFM_Tray' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($trayP) {
            $trayPid     = $trayP.Id
            $trayCpu     = [math]::Round($trayP.CPU, 2)
            $trayHandles = $trayP.HandleCount
            $trayWsMB    = [math]::Round($trayP.WorkingSet64 / 1MB, 1)
        }
    } catch {}

    # Top 5 processes by CPU (excluding Idle)
    $topProcs = ''
    try {
        $top = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'Idle' } |
            Sort-Object CPU -Descending |
            Select-Object -First 5 |
            ForEach-Object { "$($_.Name)=$([math]::Round($_.CPU,1))" }
        $topProcs = ($top -join ';') -replace ',', '|'
    } catch {}

    # Emit CSV row
    "$epoch,$dt,$dwmCpu,$exCpu,$trayPid,$trayCpu,$trayHandles,$trayWsMB,`"$topProcs`",$lateMs" |
        Out-File $OutPath -Append -Encoding UTF8

    # Console feedback — flag freezes and high tray handles
    if ($lateMs -gt 1000) {
        Write-Host "[$dt] *** FREEZE DETECTED — sample $([int]$lateMs) ms late! ***" -ForegroundColor Red
    } elseif ($trayHandles -gt 5000) {
        Write-Host "[$dt] WARN: Tray handles=$trayHandles (leak may be active)" -ForegroundColor Yellow
    }

    Start-Sleep -Milliseconds 500
}
