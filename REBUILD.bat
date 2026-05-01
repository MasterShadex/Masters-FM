@echo off
:: ─────────────────────────────────────────────────────────────────────────────
::  REBUILD.bat — Silent full rebuild + uninstall + reinstall + relaunch.
::  Designed to be callable by Claude non-interactively: no prompts, no pauses,
::  no UI. All output + MSI logs are captured in F:\Claude AI\Master FM\logs\.
::  Exit code 0 on success, non-zero on failure (with the failing stage logged).
:: ─────────────────────────────────────────────────────────────────────────────
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "ROOT=%~dp0"
set "LOGDIR=%ROOT%logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"

:: Timestamp (yyyyMMdd_HHmmss) via PowerShell for filename
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "TS=%%i"

set "BUILDLOG=%LOGDIR%\rebuild_%TS%.log"
set "LATEST=%LOGDIR%\rebuild_latest.log"
set "MSILOG=%LOGDIR%\msi_install_%TS%.log"
set "MSIUNLOG=%LOGDIR%\msi_uninstall_%TS%.log"
set "MSI=%ROOT%Master's FM Install\MastersFM_Setup.msi"
set "PRODUCT_NAME=Master's FM"

:: All stdout+stderr → log file. User sees nothing in terminal.
call :run > "%BUILDLOG%" 2>&1
set "RC=%ERRORLEVEL%"
copy /y "%BUILDLOG%" "%LATEST%" >nul 2>&1
exit /b %RC%

:: ═════════════════════════════════════════════════════════════════════════════
:run
echo [%DATE% %TIME%] REBUILD start
echo Root:   %ROOT%
echo Log:    %BUILDLOG%
echo.

:: ── Step 1: Build server.exe via pkg ────────────────────────────────────────
echo [1/5] pkg build -^> server.exe
call npx pkg server.js --targets node18-win-x64 --output dist\server.exe
if errorlevel 1 (
    echo FAIL: pkg build failed.
    exit /b 11
)
copy /y dist\server.exe server.exe >nul
echo   server.exe OK.
echo.

:: ── Step 1b: Compile MastersFM.exe (C# launcher, shows as "Master's FM") ───
:: Uses csc.exe from .NET Framework 4.x (built into every Windows 10/11 install).
:: This exe is the single visible entry-point in Task Manager + auto-start list.
echo [1b/5] Compiling MastersFM.exe via csc.exe...
set "CSC="
for /f "delims=" %%i in ('dir /b /s "%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" 2^>nul') do if not defined CSC set "CSC=%%i"
if not defined CSC (
    for /f "delims=" %%i in ('dir /b /s "%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe" 2^>nul') do if not defined CSC set "CSC=%%i"
)
if defined CSC (
    "%CSC%" /nologo /target:winexe /win32icon:MastersFM.ico /out:MastersFM.exe ^
        launcher.cs
    if errorlevel 1 (
        echo   WARN: csc compile failed — MastersFM.exe may be stale.
    ) else (
        echo   MastersFM.exe OK.
    )
) else (
    echo   WARN: csc.exe not found — MastersFM.exe not rebuilt. Ensure .NET Framework 4 is installed.
)
echo.

:: ── Step 2: Build MSI ───────────────────────────────────────────────────────
echo [2/5] build_msi.py
set "PYTHONIOENCODING=utf-8"
python build_msi.py
if errorlevel 1 (
    echo FAIL: MSI build failed.
    exit /b 12
)
echo   MSI OK: %MSI%
echo.

:: ── Step 3: Stop running tray + server so msiexec can overwrite files ───────
echo [3/5] Stopping running tray + server
powershell -NoProfile -Command ^
  "Get-CimInstance Win32_Process | ? { $_.CommandLine -like '*tray.ps1*' } | %% { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
powershell -NoProfile -Command "Stop-Process -Name server -Force -ErrorAction SilentlyContinue"
powershell -NoProfile -Command "Stop-Process -Name wscript -Force -ErrorAction SilentlyContinue"
powershell -NoProfile -Command "Stop-Process -Name MastersFM -Force -ErrorAction SilentlyContinue"
:: brief wait for handles to release
powershell -NoProfile -Command "Start-Sleep -Milliseconds 1500"
echo   processes stopped.
echo.

:: ── Step 4: Uninstall any existing Master's FM via ProductCode lookup ───────
echo [4/5] Uninstalling existing Master's FM (if any)
powershell -NoProfile -Command ^
  "$app = Get-CimInstance Win32_Product -ErrorAction SilentlyContinue | ? { $_.Name -like '*Master*FM*' } | Select-Object -First 1;" ^
  "if ($app) { Write-Host ('  Found ProductCode: ' + $app.IdentifyingNumber); $p = Start-Process msiexec.exe -ArgumentList ('/x ' + $app.IdentifyingNumber + ' /qn /norestart /l*v \"%MSIUNLOG%\"') -Wait -PassThru -WindowStyle Hidden; Write-Host ('  uninstall exit=' + $p.ExitCode) } else { Write-Host '  none found.' }"
powershell -NoProfile -Command "Start-Sleep -Milliseconds 1000"
echo.

:: ── Step 5: Install fresh from the new MSI ──────────────────────────────────
echo [5/5] Installing fresh MSI
:: Plain /i install — we already uninstalled in step 4 so REINSTALL flags would
:: tell MSI "only replace existing files", which skips the FileCopy phase and
:: leaves the INSTALLFOLDER empty. Clean install lays the files down properly.
msiexec /i "%MSI%" /qn /norestart /l*v "%MSILOG%"
set "IRC=%ERRORLEVEL%"
if not "%IRC%"=="0" (
    echo FAIL: msiexec exit=%IRC%   log=%MSILOG%
    exit /b 15
)
echo   Installed OK. log=%MSILOG%
echo.

:: ── Launch overlay via MastersFM.exe (shows as "Master's FM" in Task Manager)
set "DEST=%LOCALAPPDATA%\MastersFM"
if exist "%DEST%\MastersFM.exe" (
    echo Launching overlay...
    start "" "%DEST%\MastersFM.exe"
    echo   launched.
) else if exist "%DEST%\Start Overlay.vbs" (
    echo Launching overlay (VBS fallback - MastersFM.exe missing^)...
    start "" wscript "%DEST%\Start Overlay.vbs"
    echo   launched.
) else (
    echo WARN: Neither MastersFM.exe nor Start Overlay.vbs found at %DEST%
)

echo.
echo [%DATE% %TIME%] REBUILD done OK
exit /b 0
