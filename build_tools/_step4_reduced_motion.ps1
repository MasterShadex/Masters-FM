# Stage 7.7B FINAL STEP 4: reduced-motion test
# 1. Disable ClientAreaAnimation via SPI
# 2. Kill and restart MastersFM
# 3. Wait for log entry
# 4. Check ReducedMotion in log
# 5. Re-enable and restart

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class AnimSPI {
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SystemParametersInfo(uint a, uint b, ref bool v, uint f);
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    public const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    public const uint SPIF_SENDCHANGE = 2;
}
"@

$logFile = "$env:LOCALAPPDATA\MastersFM\logs\MastersFM_current.log"
if (-not (Test-Path $logFile)) {
    $logFile = (Get-ChildItem "$env:LOCALAPPDATA\MastersFM\logs\MastersFM_*.log" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
Write-Host "Log file: $logFile"

# Read current value
$orig = $true
[AnimSPI]::SystemParametersInfo([AnimSPI]::SPI_GETCLIENTAREAANIMATION, 0, [ref]$orig, 0) | Out-Null
Write-Host "Original ClientAreaAnimation: $orig"

# --- Phase 1: Disable animations ---
Write-Host "`n[PHASE 1] Disabling ClientAreaAnimation..."
$off = $false
[AnimSPI]::SystemParametersInfo([AnimSPI]::SPI_SETCLIENTAREAANIMATION, 0, [ref]$off, [AnimSPI]::SPIF_SENDCHANGE) | Out-Null
$check = $true
[AnimSPI]::SystemParametersInfo([AnimSPI]::SPI_GETCLIENTAREAANIMATION, 0, [ref]$check, 0) | Out-Null
Write-Host "After disable: ClientAreaAnimation=$check"

# Stop app
Stop-Process -Name "MastersFM" -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# Launch installed app
$exePath = "$env:LOCALAPPDATA\MastersFM\MastersFM.exe"
if (Test-Path $exePath) {
    Start-Process $exePath
    Write-Host "Launched $exePath"
    Start-Sleep -Seconds 3

    # Check log for ReducedMotion
    if (Test-Path $logFile) {
        $hit = Select-String -Path $logFile -Pattern "ReducedMotion" | Select-Object -Last 3
        Write-Host "Log ReducedMotion entries:"
        $hit | ForEach-Object { Write-Host "  $_" }
        if ($hit | Where-Object { $_ -match "ReducedMotion=true" }) {
            Write-Host "PASS: ReducedMotion=true detected in log"
        } else {
            Write-Host "FAIL: ReducedMotion=true NOT found in log (check log path or timing)"
        }
    } else {
        Write-Host "WARNING: Log file not found at $logFile"
    }
} else {
    Write-Host "WARNING: MastersFM.exe not found at $exePath"
}

# Stop app again
Stop-Process -Name "MastersFM" -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# --- Phase 2: Re-enable animations ---
Write-Host "`n[PHASE 2] Re-enabling ClientAreaAnimation..."
$on = $true
[AnimSPI]::SystemParametersInfo([AnimSPI]::SPI_SETCLIENTAREAANIMATION, 0, [ref]$on, [AnimSPI]::SPIF_SENDCHANGE) | Out-Null
$check2 = $false
[AnimSPI]::SystemParametersInfo([AnimSPI]::SPI_GETCLIENTAREAANIMATION, 0, [ref]$check2, 0) | Out-Null
Write-Host "After re-enable: ClientAreaAnimation=$check2"

# Restart app normally
if (Test-Path $exePath) {
    Start-Process $exePath
    Write-Host "Restarted app normally"
    Start-Sleep -Seconds 2
    $proc = Get-Process "MastersFM" -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Host "PASS: App restarted (PID $($proc.Id))"
    } else {
        Write-Host "FAIL: App not running after restart"
    }
}

Write-Host "`nSTEP 4 reduced-motion test complete"
