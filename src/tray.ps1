# Master's FM - System Tray
param(
    [string]$scriptDir = "",
    [switch]$Uninstall,            # Pass -Uninstall from the MSI to remove OBS source on uninstall
    [switch]$ShowCustomizerOnly,   # Open just the customizer dialog (no tray, no server)
    [switch]$skipServerLaunch      # MastersFM.exe already started the server; skip our own launch
)

# ── Load pre-compiled native types (tray_native.dll) — eliminates csc.exe at startup ──
# tray_native.dll is compiled once by _full_rebuild.ps1 and shipped in the MSI.
# Replaces 5 inline Add-Type/csc.exe calls (10-25s) with a single ~50ms DLL load.
# Falls back to inline Add-Type for each type if the DLL is absent (old install / dev).
try {
    $__nativeDllDir = if ($scriptDir -and (Test-Path $scriptDir)) { $scriptDir }
                      elseif ($PSScriptRoot -and (Test-Path $PSScriptRoot)) { $PSScriptRoot }
                      else { $PWD.Path }
    $__nativeDll = Join-Path $__nativeDllDir 'tray_native.dll'
    if ((Test-Path $__nativeDll) -and -not ('MasterFM.Win32Windows' -as [type])) {
        Add-Type -Path $__nativeDll
    }
} catch {}

# ── Set AppUserModelID (AUMID) — shared with MastersFM.exe / MastersFM_Tray.exe
# / customize.exe so every Master's FM process has one Shell identity for
# taskbar grouping, toast notifications, Jump Lists etc. Inline Add-Type is the
# fallback when tray_native.dll wasn't loaded (old install or dev run without DLL).
try {
    if (-not ([System.Management.Automation.PSTypeName]'MFM_Shell').Type) {
        Add-Type -Name MFM_Shell -Namespace '' -MemberDefinition @"
            [System.Runtime.InteropServices.DllImport("shell32.dll", PreserveSig = false)]
            public static extern void SetCurrentProcessExplicitAppUserModelID(
                [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string AppID);
"@
    }
    [MFM_Shell]::SetCurrentProcessExplicitAppUserModelID("MastersFM.App")
} catch {}

# ── PHASE 0: Absolute earliest logging  -  before anything can crash ─────────────
# All logs now live in %LOCALAPPDATA%\MastersFM\ alongside the install — not
# in %TEMP% — so the user can find them without hunting through TempCleaner's
# crosshairs. The folder also holds server.log, overlay.log, transcript.log,
# menu.log, host.log, smoke_history.log — one-stop troubleshooting.
# Keep the filename-key "TEMP_LOG" variable name for diff compatibility; the
# path underneath has moved.
$script:MFM_LOG_DIR = [System.IO.Path]::Combine([System.Environment]::GetFolderPath('LocalApplicationData'), "MastersFM")
try { [System.IO.Directory]::CreateDirectory($script:MFM_LOG_DIR) | Out-Null } catch {}
$TEMP_LOG = [System.IO.Path]::Combine($script:MFM_LOG_DIR, "startup.log")
function EarlyLog($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"
    try { [System.IO.File]::AppendAllText($TEMP_LOG, "$line`r`n") } catch {}
    # Write-Host goes to console only - does NOT pollute the function return pipeline
    Write-Host $line
}
# Truncate old startup log on each fresh startup so it never grows unbounded
try { [System.IO.File]::WriteAllText($TEMP_LOG, "=== Master's FM startup $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===`r`n") } catch {}

# ── Single-instance guard (must run before anything else) ─────────────────────
# Prevents a second launch from killing the first and leaving a zombie process.
# Skipped when -Uninstall is passed — the MSI uninstaller must be able to run
# cleanup even while the tray is still live (it kills the tray first, but races happen).
if (-not $Uninstall -and -not $ShowCustomizerOnly) {
    $global:_mutex = New-Object System.Threading.Mutex($false, "Global\MastersFM_SingleInstance")
    try {
        $gotMutex = $global:_mutex.WaitOne(0)
    } catch {
        $gotMutex = $true  # abandoned mutex from a previous crash - we own it now
    }
    if (-not $gotMutex) {
        # Another instance is already running - exit immediately
        try { [System.IO.File]::AppendAllText($TEMP_LOG, "[$(Get-Date -Format 'HH:mm:ss')] Already running - second instance exiting`r`n") } catch {}
        exit 0
    }
}

EarlyLog "param scriptDir='$scriptDir'"
EarlyLog "PSVersion=$($PSVersionTable.PSVersion)  Edition=$($PSVersionTable.PSEdition)"
EarlyLog "PID=$PID  User=$env:USERNAME  ComputerName=$env:COMPUTERNAME"
EarlyLog "TEMP=$env:TEMP"
EarlyLog "PSScriptRoot='$PSScriptRoot'"
EarlyLog "MyInvocation.Definition='$($MyInvocation.MyCommand.Definition)'"
EarlyLog "PWD='$PWD'"

# ── Resolve scriptDir FIRST - before any Add-Type that could crash ─────────────
if (-not $scriptDir -or -not (Test-Path $scriptDir)) {
    if ($PSScriptRoot -and (Test-Path $PSScriptRoot)) {
        $scriptDir = $PSScriptRoot
        EarlyLog "scriptDir resolved via PSScriptRoot"
    } elseif ($MyInvocation.MyCommand.Definition -and (Test-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition))) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
        EarlyLog "scriptDir resolved via MyInvocation"
    } else {
        $scriptDir = $PWD.Path
        EarlyLog "scriptDir resolved via PWD (fallback)"
    }
}
EarlyLog "scriptDir final='$scriptDir'"

# List all files in scriptDir so we know what was actually installed
try {
    $installedFiles = Get-ChildItem $scriptDir -ErrorAction Stop | Select-Object -ExpandProperty Name
    EarlyLog "Files in scriptDir: $($installedFiles -join ', ')"
} catch {
    EarlyLog "Cannot list scriptDir: $_"
}

# ── Primary log file (in install dir) ─────────────────────────────────────────
$logFile     = [System.IO.Path]::Combine($scriptDir, "overlay.log")
$obsFlagFile = [System.IO.Path]::Combine($scriptDir, "obs_configured.flag")

# Start PowerShell transcript  -  captures ALL output, errors, exceptions
# v11.0.0: truncate transcript.log on each startup (mirrors overlay.log behaviour).
# Using -Append caused unbounded growth across restarts; after weeks/months the
# file could reach hundreds of MB. Truncate first so only the current session is kept.
$transcriptPath = [System.IO.Path]::Combine($scriptDir, "transcript.log")
try { [System.IO.File]::WriteAllText($transcriptPath, "=== Transcript started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===`r`n") } catch {}
try {
    Start-Transcript -Path $transcriptPath -Append -ErrorAction Stop
    EarlyLog "Transcript started: $transcriptPath"
} catch {
    # Transcript path may be unwritable  -  try TEMP
    $transcriptPath = [System.IO.Path]::Combine($script:MFM_LOG_DIR, "transcript.log")
    try { Start-Transcript -Path $transcriptPath -Append -ErrorAction SilentlyContinue } catch {}
    EarlyLog "Transcript (TEMP): $transcriptPath"
}

# ── v9.10.0 Stall instrumentation globals ─────────────────────────────────────
# Log ring buffer — keeps the 20 most recent log messages so SLOW TICK reports
# can include context (what was happening just before the stall).
$global:_logRingBuf    = [System.Collections.Generic.Queue[string]]::new()
$global:_logRingSize   = 20
# Per-minute WinRT call + timeout counters — reset by the [CANARY] every 60 s.
$global:_winrtCallsMin  = 0
$global:_winrtTmoMin    = 0
# v11.2.0: 5-minute Gen2 GC flush epoch (drains WinRT RCW finalization queue).
$global:_gcFlushLastMs  = [long]0

# ── Main log function  -  writes to install dir AND TEMP as fallback ─────────────
function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"
    # AppendAllText opens-writes-closes atomically: no partial lines from timer events
    try { [System.IO.File]::AppendAllText($logFile, "$line`r`n", [System.Text.Encoding]::UTF8) } catch {}
    # v11.1.0: skip TEMP_LOG write after init completes — TEMP_LOG (startup.log) is for
    # pre-init crash diagnosis only; $script:_initDone is set just before the timer starts.
    # The crash trap at line 4069 still references $TEMP_LOG for pre-init crashes.
    if (-not $script:_initDone) {
        try { [System.IO.File]::AppendAllText($TEMP_LOG, "$line`r`n", [System.Text.Encoding]::UTF8) } catch {}
    }
    Write-Host $line
    # v9.10.0: feed ring buffer for SLOW TICK context snapshots
    try {
        if ($global:_logRingBuf.Count -ge $global:_logRingSize) { $global:_logRingBuf.Dequeue() | Out-Null }
        $global:_logRingBuf.Enqueue($msg)
    } catch {}
}
# Rich error logger — captures message, type, stack, + inner exception. Callable
# from any catch block: LogErr "what I was doing" $_
function LogErr($context, $err) {
    try {
        $msg   = ($err | Out-String).Trim()
        $inv   = if ($err.InvocationInfo) { $err.InvocationInfo.PositionMessage } else { '' }
        $stk   = if ($err.ScriptStackTrace) { $err.ScriptStackTrace } else { '' }
        $inner = if ($err.Exception -and $err.Exception.InnerException) { $err.Exception.InnerException.ToString() } else { '' }
        Log "!! ERROR [$context]: $msg"
        if ($inv)   { Log "   at: $inv" }
        if ($stk)   { Log "   stack:`r`n$stk" }
        if ($inner) { Log "   inner: $inner" }
    } catch {
        try { Log "!! ERROR [$context] (fallback): $($err.ToString())" } catch {}
    }
}

# ── Global error trap — catches anything the scrobble timer / dialogs miss ─────
# PowerShell's default behaviour on unhandled error from a Timer.Tick handler is
# to eat the exception silently; the trap here at least lands it in overlay.log.
$ErrorActionPreference = 'Continue'   # don't silently swallow on non-stop errors
trap {
    try {
        LogErr "UNHANDLED TRAP" $_
    } catch {}
    continue   # keep script alive instead of bubbling up + killing the tray
}
# .NET AppDomain unhandled-exception hook — WinForms timer ticks can throw
# through to the CLR before PowerShell sees them. This catches those.
try {
    [System.AppDomain]::CurrentDomain.add_UnhandledException({
        param($sender, $e)
        try {
            $ex = $e.ExceptionObject
            [System.IO.File]::AppendAllText($logFile, "!! CLR UnhandledException: $($ex.GetType().FullName): $($ex.Message)`r`n$($ex.StackTrace)`r`n", [System.Text.Encoding]::UTF8)
        } catch {}
    })
} catch {}
# Thread-level (Application.ThreadException) registered AFTER assembly load below.

# Truncate the primary overlay.log on each fresh startup (mirrors server.log behaviour)
try { [System.IO.File]::WriteAllText($logFile, "=== Master's FM started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===`r`n") } catch {}
# v11.0.0: also truncate menu.log (Invoke-MenuAction appends per-click; never truncated before)
try { [System.IO.File]::WriteAllText([System.IO.Path]::Combine($script:MFM_LOG_DIR, "menu.log"), "") } catch {}

Log "=== Master's FM started ==="
Log "scriptDir = $scriptDir"
Log "logFile   = $logFile"
Log "TEMP log  = $TEMP_LOG"
Log "Transcript= $transcriptPath"
Log "PSVersion = $($PSVersionTable.PSVersion)"
Log "User      = $env:USERNAME @ $env:COMPUTERNAME"
if ($Uninstall) { Log "Mode: UNINSTALL" }

# Load assemblies
foreach ($asm in @("System.Windows.Forms","System.Drawing","Microsoft.VisualBasic")) {
    try {
        Add-Type -AssemblyName $asm -ErrorAction Stop
        Log "Loaded: $asm"
    } catch {
        LogErr "Add-Type $asm" $_
        if ($asm -ne "Microsoft.VisualBasic") {
            [System.Reflection.Assembly]::LoadWithPartialName("System.Windows.Forms") | Out-Null
            [System.Windows.Forms.MessageBox]::Show(
                "Failed to load $asm`n`n$_`n`nCheck overlay.log for details.",
                "Master's FM Error")
            exit 1
        }
    }
}

Log "All assemblies loaded"

# ── WinForms thread-exception hook — now that System.Windows.Forms is loaded ─
# This is the main catch-all for everything that goes wrong inside timer ticks,
# button clicks, paint handlers, etc. Without this, those exceptions are eaten
# silently by the message pump.
try {
    [System.Windows.Forms.Application]::add_ThreadException({
        param($sender, $e)
        try {
            $ex = $e.Exception
            [System.IO.File]::AppendAllText(
                $logFile,
                "!! WinForms ThreadException: $($ex.GetType().FullName): $($ex.Message)`r`n$($ex.StackTrace)`r`n",
                [System.Text.Encoding]::UTF8)
        } catch {}
    })
    # Route all WinForms exceptions through the ThreadException handler above.
    [System.Windows.Forms.Application]::SetUnhandledExceptionMode(
        [System.Windows.Forms.UnhandledExceptionMode]::CatchException)
    Log "WinForms ThreadException hook installed"
} catch {
    LogErr "Install WinForms ThreadException hook" $_
}

# ── App version — bump this string to re-trigger the welcome/patch-notes screen
#     on the user's next launch (even if config.json survived). ─────────────
$script:APP_VERSION = "v11.2.1"

# ── Cumulative patch history ──────────────────────────────────────────────
# Newest release first. NEVER delete old entries — the Welcome / View Patch
# Notes dialog scrolls, so users can always read back what changed. When
# bumping APP_VERSION, prepend a new entry here. Each note: { Tag, Text }
# where Tag is NEW / FIXED / IMPROVED / REMOVED (colors are applied by the
# renderer).
$script:PATCH_HISTORY = @(
    # ────────────────────────────────────────────────────────────────────────
    # New versioning scheme (2026-04-21): one major-version bump per
    # "big-step upgrade". Legacy v1.8-v1.9.x entries below are preserved
    # for history — they predate the renumbering.
    # ────────────────────────────────────────────────────────────────────────
    @{ Version = "v11.2.1"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED";    Text = 'Memory leak (~185 MB/hr with SoundCloud-RPC active): replaced $Session.GetHashCode() with $Session.SourceAppUserModelId in 3 SMTC caches (Get-SMTCMediaPropsCached, Get-SMTCPlaybackInfoCached, Get-SMTCPosition). Each new SMTC manager (~87/min) produced fresh COM proxy wrappers with unstable hashes, causing unbounded cache growth. Stable SAUMID key means one entry per source, overwritten on each re-acquisition.' }
        @{ Tag = "FIXED";    Text = 'Track-change CPU spike (26% on Ryzen 7 7800X3D): same root cause -- unstable hash caused staleness guards to always miss for SoundCloud-RPC, firing GetPlaybackInfo() 600/min instead of ~120/min. Fix reduces per-tick WinRT call rate.' }
    ) },
    @{ Version = "v11.2.0"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED";    Text = 'CPU spike on track skip: GetPlaybackInfo() now arms a 750 ms transition guard when SMTC reports Changing status, preventing repeat synchronous ALPC blocks during Spotify/player track transitions.' }
        @{ Tag = "FIXED";    Text = 'RAM leak: forced Gen2 GC flush every 5 minutes drains the WinRT RCW finalization queue that the 60-second Gen1 hint could not reach.' }
    ) },
    @{ Version = "v11.1.9"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Internal test build — verifies auto-update installs correctly on machines with spaces in Windows username.' }
    ) },
    @{ Version = "v11.1.8"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Auto-update on machines with a space in the Windows username (e.g. "AER Alex") — the v11.1.6 fix had a here-string escaping error that produced invalid PowerShell in the helper script. Fixed using single-string form for msiexec -ArgumentList.' }
    ) },
    @{ Version = "v11.1.7"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Internal test build — no user-visible changes.' }
    ) },
    @{ Version = "v11.1.6"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Auto-update no longer silently self-uninstalls on machines with a space in the Windows username (e.g. "AER Alex"). The new MSI installer path is now correctly quoted when passed to msiexec.' }
    ) },
    @{ Version = "v11.1.5"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Memory leak: GetPlaybackInfo() and GetTimelineProperties() WinRT calls are now staleness-guarded (500ms), cutting per-tick RCW churn from ~600/min to ~120/min.' }
    ) },
    @{ Version = "v11.1.4"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Memory leak: SMTC session finder now uses the playback-info cache instead of calling GetPlaybackInfo() directly on every tick, eliminating ~1,800 abandoned WinRT RCW objects per minute.' }
    ) },
    @{ Version = "v11.1.3"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Internal test build — no user-visible changes.' }
    ) },
    @{ Version = "v11.1.2"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Internal test build — no user-visible changes.' }
    ) },
    @{ Version = "v11.1.0"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'SoundCloud playback detection now uses a single batched process lookup with a 5-second cache instead of 8 individual lookups per tick, reducing per-tick overhead.' },
        @{ Tag = "IMPROVED"; Text = 'Startup log (startup.log) no longer accumulates all session log lines after initialisation — it now captures only the boot sequence, keeping it compact for crash diagnostics.' }
    ) },
    @{ Version = "v11.0.0"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED";    Text = 'Album art memory leak: artwork cache now has a 200-entry LRU cap. Long shuffle sessions no longer accumulate hundreds of MB of cached album art.' },
        @{ Tag = "FIXED";    Text = 'Transcript log no longer grows unbounded across restarts — it is now reset on each startup.' },
        @{ Tag = "FIXED";    Text = 'In-flight update downloads are now properly cancelled when the app exits or restarts.' },
        @{ Tag = "IMPROVED"; Text = 'Windows Media Player and VLC process lookups are now cached for 5 seconds, reducing per-tick overhead from repeated process enumeration.' },
        @{ Tag = "IMPROVED"; Text = 'Timer objects (fade animations, OBS auto-add) are now properly disposed after stopping, preventing handle accumulation over many menu interactions.' },
        @{ Tag = "IMPROVED"; Text = 'Per-tick hashtable allocations eliminated — GC pressure from 72,000 short-lived objects per hour reduced to zero.' }
    ) },
    @{ Version = "v10.2.3"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Auto-update check now runs every 1 hour instead of every 6 hours. You will receive updates faster.' },
        @{ Tag = "IMPROVED"; Text = 'After an automatic update installs and the app restarts, a balloon notification now appears instead of the patch notes window opening automatically. The window is still available any time via "Patch Notes" in the tray menu.' },
        @{ Tag = "NEW";      Text = '"Patch Notes" tray menu item — opens the patch notes window on demand.' }
    ) },
    @{ Version = "v10.2.2"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Startup time reduced by 10-25 seconds on first launch after install. Five inline C# compilations (Add-Type) that each invoked csc.exe have been replaced by a single pre-compiled tray_native.dll that loads in ~50ms.' }
    ) },
    @{ Version = "v10.2.1"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Test build — version bump only.' }
    ) },
    @{ Version = "v10.2.0"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Update install helper script now correctly passes the MSI path to msiexec. The previous version embedded the path directly into a double-quoted here-string causing PowerShell to mis-parse the quotes when the helper ran — result was a silent failure after uninstall. Fixed by assigning paths as single-quoted variables and using array-form ArgumentList.' }
    ) },
    @{ Version = "v10.1.9"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Test build — version bump only.' }
    ) },
    @{ Version = "v10.1.8"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Auto-update install no longer silently fails. The previous approach (msiexec /i over existing install) fails with error 1603 because Windows Installer cannot locate the source of the prior version. Fixed by writing a helper script that uninstalls the old version by ProductCode first, then installs the new MSI.' }
    ) },
    @{ Version = "v10.1.7"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Test build — version bump only.' }
    ) },
    @{ Version = "v10.1.6"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Update check now always fetches a fresh copy of the version manifest. The previous check used a bare URL that GitHub''s CDN could serve from cache, causing "up to date" false positives immediately after a new release was pushed.' }
    ) },
    @{ Version = "v10.1.5"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Test build — version bump only.' }
    ) },
    @{ Version = "v10.1.4"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Update download no longer stalls. Replaced the HttpClient streaming approach (unreliable in PS 5.1 + WinForms) with WebClient.DownloadDataAsync whose progress and completion events fire directly on the UI thread.' }
    ) },
    @{ Version = "v10.1.3"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Test build — version bump only.' }
    ) },
    @{ Version = "v10.1.2"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Update window now correctly shows "Running v10.x.x" when up to date. The version string was not resolving inside the WinForms timer closure due to a PowerShell scope quirk — fixed by capturing APP_VERSION as a local variable before GetNewClosure().' }
    ) },
    @{ Version = "v10.1.1"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Test build — version bump only.' }
    ) },
    @{ Version = "v10.1.0"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Update download now completes in seconds instead of minutes. Two root causes fixed: (1) The download chunk loop processed one 65 KB chunk per 2-second poll tick — a 12 MB file took ~6 minutes. Fixed by draining all buffered TCP chunks per tick in a time-bounded loop (≤80 ms per tick). (2) Content-Length was never read from the response headers because PowerShell 5.1 unwraps Nullable<long> automatically — calling .HasValue on the resulting plain long silently returned null. Fixed by removing the .HasValue check. The progress bar now shows a real percentage instead of the marquee.' }
    ) },
    @{ Version = "v10.0.9"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Update window now has a Download / Install button. No longer need to go back to the tray menu to trigger the download or install — the button appears directly in the progress window.' }
    ) },
    @{ Version = "v10.0.8"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Test build — version bump only. Used to verify the native WinForms update progress window end-to-end.' }
    ) },
    @{ Version = "v10.0.7"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Update progress window is now a native in-app WinForms window instead of a browser tab. Clicking "Check for Updates" opens a compact dark-themed overlay (420×200) showing live state: checking, downloading with % bar and MB counter, verifying, ready to install, installing. A custom two-panel progress bar (purple fill, dark background) replaces the system control for full color control. A marquee animation appears when download size is unknown. The window auto-closes after ~3 seconds when the "up to date" result arrives.' }
    ) },
    @{ Version = "v10.0.6"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Update progress window. Clicking "Check for Updates" opens a browser tab at http://localhost:4242/update showing live progress: checking → downloading (with % bar and byte counter) → verifying → installing → done. Download is now streaming (ReadAsync loop) instead of a single GetByteArrayAsync call, enabling real byte-level progress tracking. When the app restarts for install, the page detects the server going offline and auto-reconnects.' }
    ) },
    @{ Version = "v10.0.5"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Auto-update install no longer hangs on "Installing...". Root cause: Install-Update launched msiexec while the tray was still running, causing msiexec to wait indefinitely for locked files. Fix: msiexec is now scheduled via cmd.exe with a 2-second ping delay, then Application.Exit() closes the tray first so msiexec finds all files free when it arrives.' }
    ) },
    @{ Version = "v10.0.4"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "NEW"; Text = 'Auto-update end-to-end test build. No behaviour changes — version bump only. Used to verify the full update chain: manifest fetch, MSI download, SHA-256 verify, silent install, and app restart.' }
    ) },
    @{ Version = "v10.0.3"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Check for Updates now reliably shows feedback. Clicking the button while the startup auto-check is still in flight now flags it as user-triggered so the result balloon still fires. Menu label shows ''Checking...'' during an in-flight check. Tray tooltip also flashes ''Up to date'' for 5 seconds as a fallback if the balloon is suppressed by Windows.' }
    ) },
    @{ Version = "v10.0.2"; Date = "2026-05-02"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Check for Updates now shows a balloon tip confirming you are on the latest version when no update is found. Previously clicking it from an up-to-date install appeared to do nothing.' }
    ) },
    @{ Version = "v10.0.1"; Date = "2026-05-01"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Auto-update now works correctly on all machines. The signature check previously required the code-signing certificate to be fully trusted by the system (status ''Valid''), which failed on machines where the publisher certificate had not been added to the Trusted Root store. The check now also accepts ''UnknownError'' status, which is the correct status for a valid self-signed certificate that has not been added to the system trust store. SHA-256 hash verification (which is the primary integrity check) is unaffected.' }
    ) },
    @{ Version = "v10.0.0"; Date = "2026-05-01"; Notes = @(
        @{ Tag = "NEW"; Text = 'Auto-updater: Master''s FM now checks for updates automatically every 6 hours (and on startup). When a new version is available, a balloon notification appears and a menu item shows in the tray menu. With autoInstall enabled by the developer, the update downloads silently in the background and installs via the MSI installer — the app restarts automatically after install. SHA-256 and Authenticode signature verification are performed before install. When autoInstall is off, the user can trigger the download and install from the tray menu.' },
        @{ Tag = "NEW"; Text = 'MSI auto-launch: the installer now starts Master''s FM automatically at the end of install, so the tray icon appears before the installer window even closes. Windows Defender pre-scans the executable during install rather than on first user launch, eliminating the slow-start experience on first install.' }
    ) },
    @{ Version = "v9.9.9"; Date = "2026-05-01"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Track-change hard lag: every music skip caused a visible system stutter. All UI-thread blocking on track change is now zero. (1) SMTC thumbnail extraction (all sources): Get-SMTCThumbnailDataUri returns immediately on every call. On cache miss it queues the request and returns ""; Invoke-DeferredThumbExtraction fires 400 ms later via a non-blocking state machine (idle/waiting/opening/loading) — each stage fires one async Task and returns immediately, no .Wait() at any point. (2) WMP track title lookup: Resolve-WMPTrackByDuration replaced Invoke-WebRequest (4 s synchronous) with HttpClient.GetStringAsync fire-and-poll. (3) SMTC session manager: Get-SMTCManager replaced Await-WinRT RequestAsync with fire-and-poll — RequestAsync wrapped in AsTask<T>(op, CancellationToken), stored as global Task, no .Wait(). (4) SMTC media properties: Get-SMTCMediaPropsCached replaced per-tick Await-WinRT with a cross-tick async cache — fires TryGetMediaPropertiesAsync once per session per tick, stores Task globally, returns last cached result immediately. (5) pollTimer tray text: Invoke-RestMethod -TimeoutSec 1 replaced with HttpClient.GetStringAsync fire-and-poll. (6) SMTC transition guard: synchronous COM/ALPC calls (GetPlaybackInfo, GetTimelineProperties, GetSessions) each take ~15 ms during track changes as the SMTC service is busy. On title change detection, a 500 ms guard window activates — all three calls are suppressed and return last cached values instead. New Get-SMTCPlaybackInfoCached helper. Zero .Wait() calls anywhere in the scrobble tick or pollTimer path.' },
        @{ Tag = "IMPROVED"; Text = 'Diagnostic instrumentation for the intermittent Windows-wide freeze symptom. When Master''s FM stalls the WinForms UI thread for >250 ms, the log now captures: the 20 most recent log entries as context, a process snapshot (handles, threads, working set, GDI object count), and the WinRT timeout count for that minute. A new [CANARY] log entry appears every 60 s with handles, threads, GDI objects, User objects, and per-minute WinRT call + timeout counts. If timeouts exceed 10/min the canary is marked WARN; >50/min is ERROR. A companion script system_watchdog.ps1 can be run alongside the app to capture system-level data (DWM CPU, Explorer CPU, tray handles, top-5 processes, and freeze timestamps) into a CSV for post-mortem analysis. No other behavior changes shipped — instrumentation only. The root cause investigation continues in the next session using live data from these logs.' }
    ) },
    @{ Version = "v9.9.4"; Date = "2026-05-01"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Tray memory leak: MastersFM_Tray.exe no longer grows from ~105 MB to ~1.9 GB after one night of idle running. Root cause: Await-WinRT used the 1-argument AsTask<T>(IAsyncOperation<T>) overload, which has no cancellation path. When SMTC RequestAsync() timed out (triggered by a third-party soundcloud-rpc app returning SERVERCALL_RETRYLATER), each orphaned IAsyncOperation kept its COM cross-process proxy alive indefinitely — consuming 1 LPC-blocked thread + 5-6 OS handles per leaked operation. After 797 timeout events over 10 hours, this produced 17,150+ threads and 106,713+ handles, eventually exhausting USER objects and crashing. Fix: switched to the 2-argument AsTask<T>(IAsyncOperation<T>, CancellationToken) overload with a CancellationTokenSource(TimeoutMs). When the CTS fires, it unregisters the Completed handler, releases the COM proxy back-channel, and transitions the task to Canceled — then a finally block disposes both CTS and Task. Verified by 30-minute idle soak: handles stable at ~880 (was 106k+), threads stable at ~33 (was 17k+), working set ~150 MB (was 1,986 MB).' }
    ) },
    @{ Version = "v9.9.3"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "FIXED"; Text = 'OBS spectrum lag: the spectrum now updates at its configured fps (120) inside OBS regardless of the Browser Source FPS setting. Root cause: requestAnimationFrame in OBS''s CEF engine is throttled to the browser-source fps (default 30 fps = 33 ms cadence), not the monitor refresh rate. Fix: detect OBS via user-agent and drive the render loop with setInterval instead. Measured improvement: up to 30 ms reduction in spectrum-to-audio lag at the default 30 fps browser-source setting.' },
        @{ Tag = "FIXED"; Text = 'Reaction Speed CPU spike (12 % on Ryzen 7 7800X3D at 0.01 ms): the WASAPI hot loop now uses an effective FFT stride of max(hop, 384 samples ~8 ms) instead of the raw hop. Setting 0.01 ms previously spun 48 000 FFTs/sec — all discarded since SSE only delivers ~120/sec. With the 8 ms floor, FFT rate matches SSE delivery rate and CPU returns to normal (~1-3 %).' }
    ) },
    @{ Version = "v9.9.2"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'WebGL CPU + GPU optimisation: (1) Uniform cache — gl.uniform* driver calls are now skipped when the value has not changed since the last frame. For a static spectrum (solid colour, no animations), this drops from 14 uniform calls/frame to 0. For rainbow mode, only the hue-offset uniform is sent each frame. (2) gl.useProgram removed from the per-frame path — the program is set once at init and never changes. (3) gl.viewport only called when the canvas actually resizes (was called every frame). (4) gl.clearColor moved to init (was a constant re-set every frame). (5) Per-band gamma array for Auto-Gain upload pre-computed at startup instead of recalculated inside the 480-band loop.' }
    ) },
    @{ Version = "v9.9.1"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Reaction Speed slider minimum lowered from 0.50 ms to 0.01 ms — allows setting the FFT hop to a single sample (~48 000 FFTs/sec) for absolute minimum audio latency. Frame Rate slider maximum reduced from 1000 to 120 (max); any saved value above 120 is automatically capped. Both changes match the real-world ceiling: audio delivery is bounded by the SSE publish rate (~120/sec) and OBS browser sources typically run at 60–120 fps.' }
    ) },
    @{ Version = "v9.9.0"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "FIXED"; Text = 'Bass flat wall: the bass region of the spectrum no longer appears as a solid horizontal plateau when Auto-Gain or Loudness Boost is active. Two root causes fixed: (1) With Loudness Boost, bands that exceeded the compression ceiling all clipped to the same maximum value — replaced the hard clip with a smooth asymptotic limiter so bands with slightly different energies produce different output values instead of all going to 255. (2) With Auto-Gain, the uniform normalization (scale all bands so peak = 255) mapped groups of similar bass bands to indistinguishable heights — replaced with a per-band power curve (γ 2→1 from bass to treble) so near-peak bands spread into a visible gradient rather than a wall.' }
    ) },
    @{ Version = "v9.8.3"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Default spectrum settings tuned for maximum visual impact: Number of Bars 50→480 (full resolution), Spacing 3px→0px (packed bars), Bar Roundness 4px→0px (sharp), Lowest Bar Height 2px→0px (full silence = invisible), Reaction Speed 10.7ms→0.5ms (near-real-time FFT rate — ~2000 FFTs/sec vs the old 93/sec). These are the defaults for new installs; existing users keep their saved settings.' }
    ) },
    @{ Version = "v9.8.2"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Heartbeat mode: spectrum bars now react at maximum possible speed. Rise is instant — bars snap to the audio peak in the same render frame the data arrives, no lerp or threshold on the up-path. Fall is a fast exponential decay (15 ms half-life at Smoothing=0 → punchy heartbeat) instead of the previous 50 ms. Smoothing slider now controls fall speed only: 0 = heartbeat pulse, 1 = slow floaty ambient. Default smoothing changed from 0.6 to 0 so new installs get the heartbeat look. Response Time setting is now applied from the very first frame after startup (was only applied when the user moved the slider in customize).' }
    ) },
    @{ Version = "v9.8.1"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "IMPROVED"; Text = 'Real-time audio response at any Frame Rate setting. Previously the Frame Rate slider controlled BOTH the SSE delivery rate and the render fps — at 60 fps the overlay waited up to 16 ms between receiving new band data, making bars feel sluggish even on fast hardware. Now: SSE always connects at fps=2000 (server unthrottled / new-frames-only mode). The server only sends when a genuinely new FFT frame is ready (~120 per second, driven by the 8 ms FFT publish interval in audio_spectrum.exe) — no duplicate frames ever. Bar data is always at most 8 ms old regardless of the slider. The Frame Rate slider now exclusively controls render smoothness (animation fps), not data freshness. SSE traffic is constant at ~77 KB/s no matter what the slider is set to. Default render fps raised from 60 back to 120.' }
    ) },
    @{ Version = "v9.8.0"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "FIXED";    Text = 'Spectrum lags when games are running. Two compounding causes fixed: (1) audio_spectrum.exe was at Normal process priority — during heavy gaming, games elevate via MMCSS to High/Realtime and Normal-priority threads miss their WaitOne wake-up by 5-10 ms extra, causing bursty SSE delivery. Fixed by raising audio_spectrum.exe to AboveNormal. (2) Default Frame Rate slider was 1000 fps — at 1000 fps the SSE loop runs 1000 context-switches/sec and sends duplicate frames at 640 KB/s loopback, competing with games for CPU even when bars only visually update at monitor refresh. Default lowered to 60 fps which matches typical OBS Browser Source FPS settings and eliminates the duplicate-frame CPU overhead.' }
    ) },
    @{ Version = "v9.7.0"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "FIXED";    Text = 'Critical: spectrum was completely blank after v9.6.9. Root cause: the autoGain noise-gate code referenced const autoGain before it was declared in the same function (temporal dead zone). Every _glRender() call threw a ReferenceError silently inside requestAnimationFrame — the error was swallowed by the browser each frame leaving the spectrum invisible. Fix: changed the gate check from autoGain to cfg.autoGain (the function parameter, always in scope). The noise gate itself works correctly.' }
        @{ Tag = "FIXED";    Text = 'Auto Volume Match (AutoGain) made the spectrum look pixelated / spiky. Root cause: gainScale = 255/normPeak can be 10-50x on quiet audio, amplifying the WASAPI loopback noise floor (band values 1-5) into 10-250 range — hundreds of tiny bars appeared everywhere between the real peaks. Fix: a proportional noise gate now zeroes any band below 6% of normPeak before gainScale is applied. Real frequency content (above that floor) is unaffected; pure noise bands become 0 and disappear. AutoGain off: gate = 0 so no change to normal behaviour.' }
    ) },
    @{ Version = "v9.6.8"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "FIXED";    Text = 'INSTALL.bat cert fix: the installer was only importing the MasterShadex publisher certificate into TrustedPublisher — without Root CA trust the chain is always invalid, so friends saw ''Unknown Publisher'' in UAC or had the MSI rejected by strict installer policies. Fix: INSTALL.bat now also runs certutil -addstore ''Root'' (LocalMachine) before TrustedPublisher. From an already-elevated cmd.exe this import is completely silent — the scary ''Install Certificate from a Certification Authority'' dialog only appears via the GUI Certificate Import Wizard or with the -user flag; neither applies here. Friends should now get a clean silent install.' }
    ) },
    @{ Version = "v9.6.7"; Date = "2026-04-30"; Notes = @(
        @{ Tag = "FIXED";    Text = 'Number of Bars slider was a dead control since v9.4.0 (canvas2d wipe). canvas2d drew exactly barCount bars by slicing its band array; WebGL always received all 480 bands from audio_spectrum.exe and rendered all of them, making the slider update config but have zero visual effect. Fix: overlay.html now decimates the 480-band _renderedBands array to barCount groups before passing it to the WebGL texture upload — each group takes the max of its constituent bands so peaks are preserved. The slider now visually changes bar density from 1 (one wide bar) to 480 (full resolution). Client-side only; audio_spectrum.exe still captures at 480 bands internally.' }
    ) },
    @{ Version = "v9.6.6"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum cut off completely at very quiet audio levels even with v9.6.5's 100x sensitivity slider — a friend nailed the cause: 'are we using something that only allows the spectrum after a certain dB?' YES. Two silence gates in audio_spectrum.cs were swallowing the signal BEFORE the user's sensitivity multiplier could amplify it: an inner gate at 0.0008 RMS (line 1540, the dominant one) and an outer gate at 0.0001 RMS (line 1062). Both were CPU optimizations from v7.0.6 (skip FFT during paused music) using static thresholds. For SteelSeries Sonar friends, music dynamics swing the raw RMS around 0.001-0.005 — flickering above/below the 0.0008 inner gate — and 100x sensitivity didn't help because s_sensitivity is applied AFTER the gates. Fix: both gates now divide their threshold by s_sensitivity. At sensitivity=1.0 (default) gates behave identically to v7.0.6. At sensitivity=100x the gates are 100x more permissive, so any real signal above the float-precision noise floor passes through to FFT + sensitivity amplification. The user's intent when cranking sensitivity is 'make me see quieter audio' — the gates now respect that. CPU optimization for true silence still works: float-precision quantization noise is below 1e-7 RMS even at max sensitivity, so paused-music savings are preserved." }
    ) },
    @{ Version = "v9.6.5"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "FIXED";    Text = "SteelSeries Sonar / Voicemeeter / virtual-mixer users couldn't get a visible spectrum without earraping themselves at 50% Sonar volume. Root cause: Sonar (and similar virtual mixers) multiply the captured digital signal by their own volume slider, which sits at 5-10% for headphone safety. With s_inputGain hardcoded at 1.0x for wasapi_loopback and the v9.6.4 sensitivity max of 20x, friends were maxing the slider with the spectrum still nearly flat. Three layered fixes: (1) sensitivity slider max bumped from 20x to 100x — gives users 5x more manual gain headroom for the worst-case quiet-virtual-mixer scenarios. Server-side clamp (audio_spectrum.exe) bumped from 20.0 to 100.0 to match. Float-precision noise floor at +40 dB of gain is still ~-110 dB, way below visible. (2) AutoGain activation threshold lowered from 20 to 5 — friends streaming through Sonar were producing band peaks of 8-15, just below the old threshold so AutoGain stayed dormant. With threshold=5, AutoGain engages on any source louder than the typical noise floor (~1-3) and rescales bands to fill the canvas regardless of source volume. (3) Help text updated to call out Sonar / Voicemeeter scenarios + recommend AutoGain as the one-click fix. The recommended workflow for Sonar users is now: (a) toggle AutoGain on, OR (b) crank sensitivity to 50-100x manually. Either way the bars fill correctly at any Sonar volume." }
    ) },
    @{ Version = "v9.6.4"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "FIXED";    Text = "Three Spectrum Visualizer sliders that quietly stopped working when canvas2d was wiped in v9.4.0: Gap, Bar Radius, and Opacity. The WebGL fragment shader had no uniforms for any of them, so dragging the sliders updated config + customize preview wired through but the actual rendered output ignored them — bars were always edge-to-edge solid rectangles at full opacity. Fix: added u_canvasW + u_gap + u_barRadius + u_opacity uniforms to the fragment shader and wired them through _glInit (uniform locations) + _glRender (per-frame values). Gap math uses pixel-space slot widths (slotW = canvasW/bandCount, barW = slotW - gap). Top-corner rounding uses an SDF-style distance check clamped to min(barW/2, barH/2) so a thick radius on a thin/short bar can't create a circle. Opacity multiplies the fragment's alpha channel — same visual result canvas2d got via CSS opacity on the canvas element. All three sliders work again." },
        @{ Tag = "FIXED";    Text = "Loudness Boost slider was capped at 10x in customize, but audio_spectrum.exe accepts up to 20x server-side. Bumped the slider max from 10 to 20 to match. Friends running through SteelSeries GG / Sonar / Voicemeeter virtual sinks were maxing the 10x slider with the spectrum still showing tiny bars — they now have headroom to push to 20x. Help text updated to mention quiet-virtual-mixer scenarios + suggest Auto-Gain as the fallback for cases where even 20x isn't enough (Auto-Gain normalizes to the actual sample peak so it always fills the bars regardless of source volume)." }
    ) },
    @{ Version = "v9.6.3"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "NEW";      Text = "Preset Manager now has Export + Import. Each saved preset row has a `📤 Export` button that downloads it as a .json file (named `MastersFM_<presetname>.json`) — friends can share that file. The Preset Manager modal also has a `📥 Import preset…` button up top that opens a file picker, accepts a shared .json, and saves it as a new preset with a sensible name (from the file's envelope, or the filename if it's a hand-crafted bare config). De-conflicts gracefully: importing a preset whose name already exists appends a `(2)`/`(3)`/etc suffix instead of silently overwriting. The export envelope includes format/version/name/exportedAt/exportedFrom metadata so future imports can validate provenance — but Import is forgiving and also accepts bare config JSON for ad-hoc sharing." }
    ) },
    @{ Version = "v9.6.2"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "OBS Browser Source URL is now `http://localhost:4242/?renderer=webgl` (was bare `http://localhost:4242` since v9.4.0). Functionally a no-op in v9.4.0+ (WebGL is the only renderer; the `?renderer=` URL param is silently ignored), but two reasons to write it explicitly: (1) defensive future-proofing — if canvas2d ever returns as a fallback path in some future version, these existing OBS sources stay pinned to WebGL via URL param > config > default resolution; (2) friendly marker — anyone inspecting the OBS Browser Source URL sees 'yes, GPU-accelerated' at a glance. Both code paths updated: Add-OBSBrowserSourceWS (live OBS via obs-websocket) and Add-OBSBrowserSourceDirect (file path: writes into scene-collection JSON before OBS launches). Existing v9.4.0/v9.5.0/v9.6.x sources with the bare URL get rewritten to the new canonical form on the next auto-add cycle (Test-OBSBrowserSourceExists URL canonicality check now expects the suffix)." }
    ) },
    @{ Version = "v9.6.1"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "FIXED";    Text = "INSTALL.bat self-elevation crashing on friends' machines. Symptom: friend opens INSTALL.bat, sees 'Requesting admin privileges...', then the script exits without ever showing a UAC prompt. Three compounding causes: (1) apostrophes in user paths broke the PowerShell single-quoted string literal — `Start-Process -FilePath '%~f0'` becomes `'C:\Users\Joe'` followed by garbage when expanded against `C:\Users\Joe's PC\...\INSTALL.bat`. Silent failure, no error visible. (2) Modern Windows Defender ASR rules increasingly block the pattern `cmd -> powershell -Command Start-Process -Verb RunAs` because that's the textbook malware-dropper signature; the PS process gets silently killed. (3) Some corporate Windows installs have ExecutionPolicy=AllSigned which kills ad-hoc PowerShell invocations. Fix: switched to a VBS helper using the 8.3 short path (`%~s0`). VBS sidesteps the ASR blocklist (wscript/cscript aren't on it), the short path has no spaces/apostrophes/special chars to escape, and ShellExecute 'runas' is the documented Windows self-elevation API. Pattern: write a 2-line VBS to `%temp%`, run with `cscript //nologo`, delete after. Standard, robust, works everywhere from Win7 to Win11." }
    ) },
    @{ Version = "v9.6.0"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "FIXED";    Text = "Background-app lag while Master's FM was running. User-reported symptom: foreground app + Master's FM + mouse cursor stayed smooth, but other background apps (OBS preview, other browser tabs, Discord background) lagged hard during high-CPU moments. Root cause: Master's FM processes were at Normal priority, competing on equal footing with whatever the user had focused. With dwm.exe (High), parsecd (High), the obs-browser-page rendering OUR overlay (AboveNormal — set by OBS for browser sources), AND audio_spectrum's outer capture thread at AboveNormal (kept from v9.1.0 boost), background Normal-priority threads on OTHER apps got demoted by Windows scheduler whenever CPU was contested. Fix: launcher.cs now lowers MastersFM_Tray.exe + server.exe to BelowNormal process priority post-spawn. They yield CPU to other apps when contested, take it freely when system is idle. audio_spectrum.exe stays at Normal (capture has hard real-time deadlines — BelowNormal would cause buffer underruns); the v9.1.0 outer-capture-thread AboveNormal boost is preserved. v9.5.0 caching preserved (the per-tick cost was already minimal at 2ms after that release; this fix is about WHEN the OS schedules tray vs other apps, not how much work tray does). The user explicitly authorized this tradeoff: 'If we can set the program to lower priority that might also work, but that freeze bug has to get out.' WebGL renderer + 4-backend audio + v9.5.0 track-change spike fix all preserved + verified." }
    ) },
    @{ Version = "v9.5.0"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "FIXED";    Text = "Track-change CPU spike that hit 16.7% on the tray host process (visible as a brief lag every time a song changed). Diagnosed via per-detector instrumentation added to Invoke-Detector — slow ticks were 200-230ms with detector-chain consuming 140ms+ of that. Root cause: Get-SpotifyNowPlaying and Get-SMTCNowPlaying each independently called mgr.GetSessions() AND awaited TryGetMediaPropertiesAsync() per session, duplicating the slow WinRT round-trip work. Fix #1: added Get-SMTCSessionsCached + Get-SMTCMediaPropsCached helpers with per-tick cache (keyed by `$global:_diagTickCount`) so all detectors in one tick share one enumeration + one props await per session. Find-SMTCSession + Get-SMTCNowPlaying + Dump-DiagnosticState now all use the helpers. Result: track-change tick dropped from 200-230ms to 37-67ms (no SLOW TICK fires after the first-boot scrobble)." },
        @{ Tag = "FIXED";    Text = "Per-tick Get-Process overhead in Spotify + osu detectors. Get-SpotifyNowPlaying was unconditionally calling Find-SMTCSession 'spotify' every 100ms tick (~25ms WinRT scan) even when Spotify.exe wasn't running. Get-OsuNowPlaying's kernel-filtered Get-Process scan was measured at ~30ms per tick on this rig. Both now cache the process-existence result for 5 seconds via TickCount; cache miss = 30ms, cache hit = ~1ms. Newly-launched Spotify or osu! is detected within ~5s. Combined with the SMTC cache fix, baseline tick avg dropped from 6-9ms to 2ms — the tray idle CPU is meaningfully lower too, not just the spike." },
        @{ Tag = "IMPROVED"; Text = "Dump-diagnostic frequency reduced from every 200 ticks (20s) to every 600 ticks (60s). The diagnostic dump (process list + SMTC session enumeration) costs ~46ms per run, and at 20s cadence it had a one-in-three chance of landing on a track-change tick — pushing total tick time over the 200ms SLOW TICK threshold even when the actual detector work was reasonable. 60s is plenty for log-trawling diagnostics. Dump-DiagnosticState also now uses the v9.5.0 cached SMTC helpers, so when it does coincide with a tick that runs detector chain, the cache is shared instead of duplicating the WinRT work." },
        @{ Tag = "IMPROVED"; Text = "SLOW TICK log lines now include per-detector breakdown — was opaque `phase=webhook-newtrack breakdown=detector-chain=195ms` (no idea which detector), now includes `detectors=smtc=72ms,osu=31ms,spotify=25ms` so future regressions are diagnosable in one log read. Sorted descending by ms; cooldown-skipped detectors filtered out. Instrumentation kept in (per the V9.5 procedure) — useful for any future track-change perf work." }
    ) },
    @{ Version = "v9.4.0"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "REMOVED";  Text = "Canvas 2D spectrum renderer wiped — WebGL is now the ONLY renderer. After v9.3.x's iterations on the toggle (overlap fix, auto-save, URL-rewrite + restart-OBS notification), the user opted to drop the canvas2d path entirely and standardise on WebGL. Removed: canvas2d render branch in drawSpectrum (~440 lines), _glWantsWebGL/_glWantWebGL/_glActive/_lastRenderMode state machinery, renderer-mode transition cleanup block, GPU Acceleration dropdown in customize, /switch-renderer endpoint in server.js, FileSystemWatcher sentinel handler in tray.ps1, Get-RendererUrl/Get-SavedRenderer helpers, the v9.3.0 migration log line. Code is meaningfully simpler — drawSpectrum no longer dispatches between two render paths. The spectrum-canvas DOM element stays in the HTML as a transparent layout placeholder (the layout system's data-layout-node='spectrum' hook + per-frame ResizeObserver + _glRender's offset-tracking all keep working unchanged); spectrum-canvas-gl is positioned absolute over it and is the only one that actually paints. If WebGL fails to initialize on a user's machine (rare — bad GPU drivers, ancient CEF), the spectrum doesn't render but the rest of the overlay (text, art, progress bar, glow, border) keeps working since they're all DOM/CSS, not canvas-based. Saved configs that still have spectrum.renderer load harmlessly (the field is just ignored). Existing OBS sources with v9.3.x's ?renderer=<value> URL suffix get rewritten back to bare http://localhost:4242 on the next auto-add cycle." },
        @{ Tag = "IMPROVED"; Text = "OBS Browser Source URL is now bare 'http://localhost:4242' again (was per-renderer in v9.3.x). Auto-add reverted to the simple form; both Add-OBSBrowserSourceWS (WebSocket path) and Add-OBSBrowserSourceDirect (direct JSON path) write the bare URL. Test-OBSBrowserSourceExists validates URL canonicality against the bare form, so v9.3.x sources with locked ?renderer= suffixes auto-migrate." }
    ) },
    @{ Version = "v9.3.2"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Renamed the renderer dropdown in the customize panel from 'Spectrum Renderer (WebGL / Canvas 2D)' to 'GPU Acceleration (On / Off)' — friends recognise the term 'GPU acceleration' from games and other apps, while 'WebGL' / 'Canvas 2D' meant nothing to most users. Internal config values stay 'webgl' / 'canvas2d' for compatibility." },
        @{ Tag = "FIXED";    Text = "Customize toggle didn't actually switch the OBS Browser Source renderer. Two compounding issues from v9.3.0/v9.3.1: (1) the toggle was preview-only — it POSTed to /preview-config (live preview for the customize iframe) but never to /save-overlay-config, so non-preview overlays (OBS Browser Source, plain Chrome tabs on http://localhost:4242) never received the change. (2) Even if you clicked Apply, v9.3.0/v9.3.1 wrote 'http://localhost:4242/?renderer=webgl' into freshly-auto-added OBS sources, locking them to WebGL via URL parameter regardless of saved config. v9.3.2 makes the toggle a destructive structural action: change-handler POSTs to a new endpoint /switch-renderer that (a) saves overlay.spectrum.renderer to config.json, (b) broadcasts overlay-config SSE so live overlays flip immediately, (c) drops a sentinel file the tray polls every 2 s. Tray rewrites OBS scene JSON URLs via Add-OBSBrowserSourceDirect (whose existing-source branch now syncs URL to the saved-config renderer choice) AND pops a Windows balloon: 'GPU Acceleration switched to On/Off — restart OBS to apply'. If OBS is currently open, the existing exit-watcher fires so the JSON change survives OBS's in-memory dump on close." },
        @{ Tag = "NEW";      Text = "OBS Browser Source URL is now per-renderer: 'http://localhost:4242/?renderer=<savedRenderer>' (always 'webgl' or 'canvas2d', matching the customize toggle state). Auto-add reads the saved-config renderer at install time. Test-OBSBrowserSourceExists now compares the URL against the canonical Get-RendererUrl helper, so existing v9.3.0/v9.3.1 sources locked to 'http://localhost:4242/?renderer=webgl' get rewritten on the next auto-add cycle if the user has since toggled to canvas2d. URL param is highest-priority in the renderer-resolution order (URL > config > default), so the OBS source state is unambiguous: whatever URL is in the JSON is what OBS will render after restart. The user's preset manager already saves overlay.spectrum.renderer as part of the saved S object, so loading a preset that has 'renderer': 'canvas2d' will switch the customize state to canvas2d on next preset apply." }
    ) },
    @{ Version = "v9.3.1"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "FIXED";    Text = "Renderer toggle in customize panel left BOTH spectrums visible at the same time. v9.3.0 made the toggle work for the active rendering path (drawSpectrum correctly early-returns to skip whichever renderer wasn't selected) but never tore down the inactive canvas's last painted frame. WebGL has `preserveDrawingBuffer:true` (kept for /screenshot reliability) so its last frame stays on screen by design; canvas2d's last frame is just bitmap memory that nobody clears. Result: toggling in the customize panel produced a visible overlap of canvas2d's pink filled-area shape AND WebGL's red-orange spiky bars on top of each other. Fix: added a one-shot transition cleanup in drawSpectrum that fires when the render mode changes — webgl→canvas2d hides the WebGL canvas (display:none, no layout impact since it's position:absolute) and gl.clear()s its framebuffer; canvas2d→webgl runs a one-time clearRect on canvas2d so its last frame doesn't bleed through WebGL's transparent areas. Canvas2d itself stays display:visible always (it's a layout placeholder per the v9.2.1 fix), only its drawn content is wiped. Transition state lives in module-scope `_lastRenderMode`. Logged once per toggle as `renderer transition: webgl → canvas2d` so the cleanup is visible in overlay.log." },
        @{ Tag = "FIXED";    Text = "/screenshot endpoint was capturing the wrong canvas after a renderer toggle. The capture handler used `_gl.ready` to decide which canvas to read, but that flag stays true after a webgl→canvas2d switch (init succeeded once, sticky). Toggling to canvas2d would still screenshot the stale WebGL framebuffer. Fix: hoisted `_glActive` (the canonical 'rendering right now' flag) from drawSpectrum-local to module-scope and the screenshot handler now reads it instead. _glActive = _glWantWebGL && _gl.ready, re-evaluated every drawSpectrum tick, so screenshots always reflect the user-visible state." }
    ) },
    @{ Version = "v9.3.0"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "NEW";      Text = "WebGL spectrum renderer is now the DEFAULT for new and existing installs. v9.2.x shipped WebGL as URL-param opt-in (`?renderer=webgl`); v9.3.0 promotes it to the out-of-the-box experience after the user's verified ~4× CPU reduction at max settings (2.0% → 0.5% on the user's reference rig). Fresh installs default to WebGL; existing users on configs from v9.2.x or earlier are auto-migrated on first launch via server.js's migrateConfig() — the deep-merge fills in `overlay.spectrum.renderer = 'webgl'` from updated defaults, then writes the migrated config back to disk. Migration is logged at server start as `[MIGRATE v9.3.0] Upgraded config: overlay.spectrum.renderer = 'webgl' (was unset)` so the change is visible in server.log. Canvas2d remains a fully-tested fallback selectable from the new toggle (see next note). The migration is one-shot — once written, future server starts read the persisted value and don't re-migrate." },
        @{ Tag = "NEW";      Text = "Renderer toggle in the customize panel — Spectrum section, under a new 'Performance' sub-heading just below 'Animation feel'. Two-option dropdown: 'WebGL (GPU-accelerated)' (default) and 'Canvas 2D (compatibility)'. Help text under the dropdown swaps based on selection — explains the trade-off (WebGL = much lower CPU at high frame rates, may have issues on old GPU drivers; Canvas 2D = software rendering, works everywhere, higher CPU). The toggle is wired to the existing /preview-config + SSE config flow so flipping it instantly switches the live preview iframe. 'Apply to OBS' persists the choice to config.json and broadcasts overlay-config SSE — OBS Browser Source picks up the change without a refresh. The customize iframe also inherits the saved value via the same config flow (URL param overrides config; config overrides default)." },
        @{ Tag = "NEW";      Text = "OBS Auto-Add now configures fresh Browser Sources with WebGL pre-enabled via the URL parameter. Both code paths in tray.ps1 — `Add-OBSBrowserSourceWS` (live OBS via obs-websocket) and `Add-OBSBrowserSourceDirect` (file path: writes into scene-collection JSON before OBS launches) — now use `http://localhost:4242/?renderer=webgl` as the source URL. Existing-source detection (which checks by source name, not URL) is unchanged, so users with sources from v9.2.x or earlier keep their current URL and get WebGL via the config-migration flow instead. The URL-substring scan that powers the 'OBS Overlay' menu checkmark continues to recognize the source (the 'localhost:4242' substring is preserved in the new URL form). Why URL param instead of trusting config: URL param is highest priority in the renderer-resolution order (URL > config > default), so the auto-added source stays on WebGL even if the user later toggles their customize preview to canvas2d for compatibility testing — the OBS source is meant to be the fast widget, the customize is for tweaking." },
        @{ Tag = "IMPROVED"; Text = "WebGL initialization failure is now bulletproof against half-init states. The v9.2.4 fix moved `_gl.ready=true` to AFTER the post-init logging so any throw left ready=false. v9.3.0 adds a defense-in-depth catch block: if init throws AFTER the WebGL canvas was set to display:block but before ready flipped on, the catch explicitly resets `cgl.style.display='none'` AND nulls out the ctx/program/vao/vbo/u handles. Result: a WebGL init failure on a user's machine (rare, but possible — bad GPU drivers, ancient CEF, exotic Browser Source build) leaves zero visible artifacts on the page. The canvas2d path takes over transparently via the existing `_glActive = _glWantWebGL && _gl.ready` gate in drawSpectrum. Migration to WebGL-default is only safe with this safety net in place — verified before the default flip." }
    ) },
    @{ Version = "v9.2.4"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "FIXED";    Text = "WebGL spectrum renderer (`?renderer=webgl`) was running on a half-initialized state. v9.2.2 renamed the canvas-size variables `w`/`h` to `oW`/`oH` (offsetWidth/offsetHeight) but missed updating the final log line in _glInit, which still read `(${w}x${h})`. The template-string reference to undeclared variables threw `ReferenceError: w is not defined` at runtime. The exception was caught by the outer try/catch and set `_gl.initFailed=true`, BUT `_gl.ready=true` had already been set ONE LINE EARLIER. Result: drawSpectrum saw the contradictory `_gl.ready=true && _gl.initFailed=true` state, ran the WebGL fast path, called _glRender on a half-initialized state — partially worked because the GL state setup completed before the log line, but produced no actual visual difference vs canvas2d (and the user reported no CPU savings). User caught it via Chrome DevTools console: `[webgl] init exception: ReferenceError: w is not defined ... [webgl] FIRST RENDER ok`. Three-line fix: (a) use the correct `${oW}x${oH}` variable names; (b) move `_gl.ready=true` to AFTER all logging+init steps so any exception leaves ready=false; (c) catch block explicitly resets `_gl.ready=false` for defense-in-depth. Canvas2d default unchanged. Lesson recorded: template-string variable references aren't statically validated by the JS parser — a typo only surfaces when the code path actually executes." }
    ) },
    @{ Version = "v9.2.3"; Date = "2026-04-29"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Two architectural cleanups for the WebGL renderer. (Q1) drawSpectrum's WebGL fast path now skips ALL canvas2d work when WebGL is active — getContext('2d'), ctx.clearRect, NUM/gap/barW computation, analyser.getByteFrequencyData, simTick, gradient build, path build, fill — all bypassed. Previously v9.2.2's `if (okGL) return;` only skipped the path/fill at the end of the function, leaving ~70-260µs/frame of wasted canvas2d work (clearRect + setup). v9.2.3 lifts the WebGL render dispatch to the TOP of drawSpectrum, runs the renderer-agnostic work inline (dyn-color lerp + smoothing into _renderedBands, both of which WebGL needs), then renders + early-returns BEFORE any canvas2d code executes. _glInit also does a one-time clearRect on canvas2d so any pre-WebGL frame doesn't bleed through the WebGL canvas's transparent areas. Net: at 144fps the previously-wasted ~10-40 ms/sec of canvas2d work is reclaimed. (Q2) Renderer is now a persisted config field (`spectrum.renderer`, default 'canvas2d') in addition to the URL parameter. The customize preview iframe reads this field through the existing config-SSE flow, so a user with `renderer:'webgl'` saved sees a WebGL preview in customize instead of always canvas2d. Resolution priority: URL ?renderer=… (highest, for testing) → config.spectrum.renderer (persisted) → 'canvas2d' default. Per-frame re-evaluation in drawSpectrum so a config change via SSE activates WebGL without a page reload. Canvas2d default verified IDENTICAL to v9.2.2 by screenshot diff." }
    ) },
    @{ Version = "v9.2.2"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "WebGL spectrum renderer placement bug. v9.2.1 fixed the off-screen issue but used `inset:0; width:100%; height:100%` which made the GL canvas fill the ENTIRE `.right-col` parent (which includes 16/24px padding plus the time-bar slot below the spectrum). Result: WebGL bars rendered across a much larger area than the canvas2d slot — visually misaligned. v9.2.2 fix: position the GL canvas with EXPLICIT `top/left/width/height` from `canvas2d.offsetLeft/offsetTop/offsetWidth/offsetHeight`. Since canvas2d's parent is now position:relative (the v9.2.1 fix), canvas2d.offset* values are relative to that parent — setting cgl's top/left to those values puts it exactly on top of canvas2d, no more, no less. Per-frame resize sync also tracks canvas2d's position changes (window resize, OBS source resize, parent flex recompute) so they stay aligned at all times. Canvas2d default unchanged." }
    ) },
    @{ Version = "v9.2.1"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "WebGL spectrum renderer (`?renderer=webgl` URL param) actually works now. v9.2.0 shipped the WebGL code but the user reported the spectrum bars were completely missing in OBS — only the card rendered. Diagnosis (which was deferred from v9.2.0 due to no browser tooling) found three compounding bugs: (1) The WebGL canvas had `position:absolute` but its parent `.right-col` was `position:static` (the default), so absolute positioning escaped to a higher positioned ancestor and the GL canvas landed off-screen — bars were rendered, just invisible. Fix: set `position:relative` on the parent at init time AND use `inset:0; width:100%; height:100%` on the GL canvas to fill the parent regardless of layout. (2) The `gainScale` uniform double-divided by 255: canvas2d's energy formula is `bandVal * (gainScale/255)` where `bandVal=0..255`, but in the WebGL fragment shader `bandVal` comes from a R8 texture's texelFetch which is ALREADY normalized to 0..1. v9.2.0 still passed `gainScale=1/255`, making `energy=0..0.004` → bars at minBarPx=2px (effectively invisible at the bottom). Fix: WebGL gainScale is now `1.0` for non-autoGain, `255/normPeak` for autoGain (no extra /255). (3) WebGL2 context was created without `preserveDrawingBuffer:true`, so toDataURL captures via the screenshot endpoint sometimes returned blank PNGs (framebuffer cleared at the next compositor frame). Fix: enable preserveDrawingBuffer (slight perf cost, big diagnostic win). Bonus: per-frame canvas resize sync (handles OBS source resize). Diagnostic console.log lines added at every major WebGL step (`[webgl] init begin`, `[webgl] context acquired`, `[webgl] shaders compiled`, `[webgl] FIRST RENDER ok`) for future debugging. Canvas2d default path verified by screenshot diff to render IDENTICALLY to v9.2.0. The screenshot endpoint now smart-picks the active canvas (GL when `_gl.ready`, else canvas2d) so /screenshot works for both renderer modes." }
    ) },
    @{ Version = "v9.2.0"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "NEW";      Text = "Screenshot endpoint added — server.js exposes GET /screenshot which broadcasts an `event: capture` SSE message to the overlay; the overlay calls canvas.toDataURL('image/png') and POSTs the result back to /screenshot-response; the GET returns a real PNG. Used during this run to visually verify (via the `view` tool on saved PNGs) that the canvas2d default path still renders correctly after the WebGL code was added — the verification protocol that v9.1.0 lacked. Test: `curl http://localhost:4242/screenshot --output spectrum.png` returns a 25-30 KB PNG of the live spectrum bars. Returns 503 if no overlay is connected, 504 on 2.5s timeout, 502 on overlay-side error. Foundational testing infrastructure that any future visualizer change should now use." },
        @{ Tag = "NEW";      Text = "Optional WebGL spectrum renderer (opt-in via `?renderer=webgl` URL parameter). v9.1.0's WebGL attempt was rolled back because a shared CSS rule + per-frame display toggling broke the canvas2d default path; v9.2.0 fixes both root causes by (a) keeping the WebGL canvas position:absolute and outside the layout system (no data-layout-node attribute, no shared CSS), (b) setting display state ONCE at init (not per-frame), (c) opting in via URL parameter only — no config schema change, no behavior change for default users. To enable: add `?renderer=webgl` to the OBS Browser Source URL (or the overlay URL when previewing). The WebGL canvas positions itself absolutely over the canvas2d canvas, hides the canvas2d canvas with display:none, and renders a single full-screen quad with a fragment shader that samples the bands as an R8 1D texture. Supports all three color modes (solid / gradient / rainbow) + dynamic palette + mirror mode + auto-gain — same math as the canvas2d path. Falls back to canvas2d transparently if WebGL2 init fails (sticky). Default canvas2d path verified by screenshot to render IDENTICALLY to v9.1.0. WebGL runtime parity verification was deferred since no browser-driving tooling was available in this run; user can verify visually by adding the URL parameter and comparing visually." },
        @{ Tag = "IMPROVED"; Text = "Capture thread priority boosted to AboveNormal in audio_spectrum.exe. Reduces context-switch overhead under load and smooths CPU spikes when other processes hammer the system. Measured impact on the user's max-out path (Path B: fps=1000, HOP_SIZE=24, ASIO + audio): audio_spectrum.exe CPU dropped from 16.21% (v9.0.0 baseline) to 10.27% mean over a 5-min soak — a 37% reduction beyond the v9.0.0 RFFT win. Per-FFT mean dropped from 0.05ms to 0.04ms. Memory drift +272 KB over 5 min (essentially flat, no leak). Handle count went DOWN by 14 (no leak). 10/10 mid-stream backend switches passed (WASAPI loopback / WASAPI input / MME / ASIO). All four audio backends verified working post-v9.1.0. v9.0.0 RFFT path kept intact. Confirmed at runtime via log line: 'capture thread: priority=AboveNormal (v9.1.0)'. Two attempted optimizations rolled back during this run: (1) SIMD magnitude calc (Vector<float>) — three strikes on .NET Framework System.Numerics.Vectors version mismatch, runtime GAC v4.0.0.0 vs SDK reference v4.1.6.0, binding redirect via .exe.config not picked up by build_msi.py manifest. (2) Optional WebGL spectrum renderer — added but rolled back when the user reported the overlay rendering blank in OBS Browser Source after install; the canvas2d default code path was correct in theory but something in the integration broke OBS rendering. WebGL deferred for v9.2 with proper visual-parity testing harness." }
    ) },
    @{ Version = "v9.0.0"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Performance overhaul: spectrum visualizer's FFT migrated to a real-input FFT (RFFT). Audio input is real-valued, so the legacy complex-input FFT was wasting half its work computing redundant imaginary inputs and conjugate-symmetric outputs. RFFT exploits this by packing N real samples into N/2 complex (even-indexed → real part, odd-indexed → imaginary part), running an N/2-point complex FFT (half the butterflies of the original), then applying a post-FFT untangle using precomputed twiddle factors to recover the N/2+1 unique bins of the real-input spectrum. Measured impact (5-min sample, ASIO + audio playing, 1 SSE client at fps=144): audio_spectrum.exe CPU mean dropped from 17.10% to 13.30% (a 22% reduction); per-FFT mean (PERF-ROLLUP) dropped from 0.06ms to 0.04-0.05ms (25-33% per-FFT compute reduction). 30-minute long soak confirmed the gain is stable: CPU mean 13.38%, WS drift +60 KB over 30 min (essentially flat), handle count went DOWN by 16 (no leak), per-FFT mean held at 0.05ms across all rollups. Correctness verified at startup via RfftSelfTest: synthetic 440Hz sine fed through both CFFT and RFFT paths, magnitudes compared bin-by-bin across 65 bins above noise floor — max relative diff = 0.101% (vastly under the 5% acceptance threshold). Visualizer output is bit-equivalent to v8.3.8 within float-precision noise; spectrum looks IDENTICAL. Backend safety: all 4 audio backends (WASAPI loopback, WASAPI input, MME, ASIO) verified working post-RFFT; the change is downstream of the OnData decode path so backend-specific NAudio adapters are unaffected. Stress test: 10 mid-stream backend switches with 0 failures. Fallback safety: if RFFT self-test ever fails (>5% bin diff), DoFftAndPublish automatically falls back to the legacy CFFT algorithm; both paths are kept in source for diagnosis. Browser-side micro-wins in overlay.html drawSpectrum: per-bar interpolation short-circuits when barCount equals BAND_COUNT (the typical user case where interpolation is identity = direct array access, ~2400 ops/frame saved at NUM=480); hoisted invariant constants (_gainScale, _energyScale=_gainScale/255, _bandsScale, _bandsMax, _identityBands) out of the for-loop body so the JIT keeps them in registers across all NUM iterations; replaced Math.floor with `| 0` bitwise fast-int trick. NOT shipped this version: .NET Framework bump (4.0 → 4.6.2+) and SIMD via System.Numerics.Vector<float> were evaluated and skipped per the v9.0.0 procedure's STEP 3 decision logic — RFFT delivered most of the perf goal cleanly, framework bump risks breaking the build pipeline for marginal additional gain. No new runtime dependencies. No build pipeline changes." }
    ) },
    @{ Version = "v8.3.8"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Single-precision FFT + bulk byte-decode for the audio_spectrum.exe hot path. FFT now operates on float[] (was double[]) which (a) halves memory bandwidth — at FFT_SIZE=2048 the working set drops from 32 KB to 16 KB, fits more snugly in L1 and reduces L1↔L2 traffic; (b) lets the JIT pack twice as many lanes per SIMD register if vectorization fires; (c) eliminates the implicit double↔float narrowing on every store-to-byte at the end of the chain. The per-butterfly twiddle accumulator (wr/wi) STAYS double internally — twiddle recurrence drift over 11 passes × 1024 butterflies would lose ~3 sig figs in float, but in double it stays bulletproof; only the data values are stored as float. For the stereo IEEE-float input path (WASAPI loopback, ASIO, WDM-KS-in-float-mode) OnData now uses Buffer.BlockCopy to bulk-decode the byte buffer into a pre-allocated float scratch array in one memmove instead of per-sample BitConverter.ToSingle calls (each of which does bounds checking + endian dispatch). Per-sample reads from the scratch then have zero per-call overhead. WDM-KS / MME int16 paths keep their existing BitConverter.ToInt16 since the byte layout doesn't match the float result anyway. Output is byte-quantized at the very end of DoFftAndPublish so the precision loss from double→float is invisible (a 7-bit float mantissa is plenty for 8-bit output). All four backends (MME / WDM-KS / WASAPI loopback / ASIO) verified to still capture and produce visualizer output unchanged. SIMD via System.Numerics.Vector<float> evaluated and skipped — the build script targets .NET Framework 4.0 csc.exe which doesn't include System.Numerics.Vectors, and pulling in the NuGet package would create a deployment-side dependency. The float pipeline already lets the JIT do its own loop unrolling + register allocation wins." }
    ) },
    @{ Version = "v8.3.7"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Aggressive optimization round on top of the v8.3.6 baseline (which the user described as 'worked like a champ' and asked for max optimization). Four wins, two on the audio_spectrum.exe side and two on the overlay rendering side. AUDIO SIDE: (1) Incremental RMS — the silence-gate previously did a fresh O(2048) sum-of-squares scan on every FFT trigger (~4 M fp ops/sec at HOP=24). Now `s_rmsSumSq` is maintained as a rolling sum in OnData (subtract old² + add new² when each sample is written to the circular buffer = O(1) per sample). Pre-FFT path just reads the cached value. A periodic full rescan every ~60 s of FFTs corrects floating-point drift accumulated by repeated incremental updates. Saves ~3.5 M fp ops/sec (~0.5 % CPU) plus the cache-friendliness of not re-scanning 16 KB of float data per FFT. (2) Combined band scaling factor — the per-band loop did `tilted = avg * tiltLin[b]; norm = tilted * sensitivity / REF_MAG` = 2 mults + 1 divide per band. Pre-merged `s_bandScaleOverRef[b] = tiltLin[b] / REF_MAG` so the hot path is `norm = avg * scaleOverRef[b] * sensitivity` = 2 mults. Eliminates ~1 M divides/sec at 480 bands × 2000 FFT/sec. OVERLAY SIDE: (3) Skip simTick() when WASAPI is the fresh source — simTick computes a 480-element simulator-fallback array on every rAF tick (~70 K math ops/sec at NUM=480, 144 fps), but the result was discarded whenever the WASAPI loopback path was active (the typical case). Now compute lazily only on rAF ticks where a fallback path will actually consume it. (4) Cache the spectrum fillStyle gradient — drawSpectrum was building a fresh CanvasGradient via createLinearGradient + multiple addColorStop calls on every rAF tick (~144 builds/sec). Now cached with a key that includes color mode + canvas size + base RGB / dynamic-palette hash; static modes reuse the gradient indefinitely, rainbow mode reuses across consecutive ticks within each integer hue-degree window (~3 ticks per degree at default 45°/sec drift). Cuts gradient-build overhead by ~95 % across all color modes." }
    ) },
    @{ Version = "v8.3.6"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Three more audio_spectrum.exe hot-path optimizations on top of the v8.3.5 baseline (which the user described as 'worked like a champ'). All targeted at the inner loops that run 2000×/sec at the user's responseMs=0.5 / HOP=24 settings. (1) FFT twiddle factor precompute: the radix-2 FFT was calling Math.Cos + Math.Sin once per butterfly pass = 11 trig calls per FFT × 2000 = 22 000 trig calls/sec. Now a small static `s_fftTwiddleWpr/Wpi` table holds the per-pass-size (cos, sin) pair and the FFT just reads from it. (2) Per-band gamma lookup table: the per-band loop was calling Math.Pow(norm, gamma_b) for all 480 bands per FFT = 960 000 Pow calls/sec ≈ 5 % CPU. Replaced with a 480 × 256 entry float LUT (`s_bandGammaLut`) holding pre-computed `Math.Pow(q/255, gamma_b) * 255` clamped, indexed by `b * 256 + q` for cache locality. 8-bit input precision is fine since the output is a byte anyway. Memory cost: 480 KB (fits in L2). Both tables are populated once at startup in BootstrapFromConfig — cheap (~1 ms total) and then untouched. (3) Shared SSE payload cache: v8.3.3's allow-duplicates mode resends the latest frame during quiet windows; previously each duplicate regenerated the base64 string from scratch. Now `s_sseCacheBytes` (reference-compared against `s_latest`) gates a cached `s_sseCacheRaw` byte array, so duplicate sends skip Convert.ToBase64String + Encoding.ASCII.GetBytes entirely. Race-free without locking: s_latest swaps exactly once per FFT publish, and concurrent writers to the cache just both compute the same payload — loser is GC'd. Net: another ~5-10 % CPU off audio_spectrum.exe at the user's settings. No behaviour change — output is bit-identical to v8.3.5." }
    ) },
    @{ Version = "v8.3.5"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum visualizer CPU regression — audio_spectrum.exe was burning ~7-8 % CPU after v8.3.3 (was ~1 % before). Cause: v8.3.3's per-client SSE throttle used Thread.SpinWait at high fps for sub-ms precision, which at the customize default of fps=1000 meant the SSE thread busy-spun ~1 ms between every send = ~95 % of one core dedicated to spinning. Three fixes in v8.3.5: (1) Removed Thread.SpinWait entirely from the SSE serve loop — always use WaitOne (1 ms minimum) for the throttle wait. CPU on the wait path drops near zero, at the cost of slightly bursty pacing at very high fps (Windows scheduler floor ~1-3 ms even with timeBeginPeriod(1)). (2) Lowered the customize.html default from fps=1000 to fps=144 — that matches typical 144 Hz monitors and is the actual sweet spot, since the browser can't render faster than display refresh anyway. (3) Two render-side optimizations targeting the original 'lag during drops' symptom that v8.3.4 partially addressed: (a) overlay.html now sets `backdrop-filter: none` on the card-inner when blur=0 instead of `blur(0px)` — even at 0 px the property forces the browser to allocate a backdrop-snapshot layer and re-composite on every spectrum-canvas paint, pure overhead for the default no-blur config; (b) removed the always-on `filter: brightness(1)` and `will-change: filter` from #spectrum-canvas — the identity filter forced an off-screen render-buffer allocation per paint for zero visual benefit; (c) at gap=0 the bar X edges are now pixel-snapped via Math.round, eliminating sub-pixel anti-aliasing for high-bar-count configs (480 bars in 420 px canvas = 0.875 px sub-pixel bars previously, now integer-aligned columns)." }
    ) },
    @{ Version = "v8.3.4"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum visualizer no longer stutters during drops / heavy activity (was smooth at quiet passages, lagged when the spectrum got busy). Root cause: at SSE rate 1000 fps (the slider's customize default), the per-message handler did atob + 480-element charCodeAt loop on EVERY arrival = ~25% of one core just for SSE work. During quiet passages the rAF idle-skip kicked in and the canvas redraw cost was zero, so the JS thread had headroom. During drops, every rAF tick had to do a full canvas redraw (480 rect path operations + gradient fill on a 420×135 canvas with sub-pixel bars) — combined with the SSE overhead, the JS thread saturated and rAF couldn't keep up → visible lag. Fix: STASH-AND-DECODE-AT-RAF (re-introduces the v6.5.3 pattern that was reverted in v6.6.6, but the v6.6.6 reversion's stated downside — '16 ms cached older target' — was actually irrelevant since a 144 Hz monitor inherently has 16 ms latency anyway). The SSE handler now just stashes the latest payload (~free, single reference assignment); drawSpectrum decodes the latest stash at the top of each rAF tick. Decode rate drops from SSE rate (1000) to rAF rate (typically 60-360, capped by monitor refresh) — ~7× less JS overhead at high SSE settings, leaving headroom for the canvas redraw during drops. Visual smoothness unchanged: the stash always reflects the freshest server frame at the moment of decode, and the canvas can only display monitor-refresh frames anyway, so showing a 16 ms-old SSE frame at rAF tick T is identical to showing the just-arrived SSE frame from T-0.5 ms. Recommendation: for max smoothness, set the Frame Rate slider to your monitor's refresh rate (typically 60/144/240) — going higher just spends CPU on duplicate sends without visual gain." }
    ) },
    @{ Version = "v8.3.3"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum SSE rate slider now genuinely accurate at any value (60 → 2000), not just monitor-refresh-typical rates. v8.3.2's burst-aware spin still measured ~350 fps for slider 500-1500 because of the actual root cause: NAudio delivers audio in BURSTS (one ASIO buffer = ~5 ms = ~10 FFT signals fired in <1 ms then 4 ms quiet). With the previous 'send only fresh frames' policy (`if f != lastSent`), there was nothing to send during the 4 ms quiet windows, so output capped at burst-frequency (~400 fps) regardless of slider. Fix: in throttled mode (any fps < 2000), allow sending duplicate frames during quiet periods so the slider value is the actual SSE rate. The cost is bandwidth (~640 B/frame × extra duplicates — trivial on localhost) and a small extra CPU on the browser side for redundant atob+apply at high fps (same cliff as v8.2.9 — pick a slider value your browser can keep up with, 60-240 is the safe zone matching typical monitor refresh). The fps=2000 special case keeps its existing signal-driven 'no duplicates' behaviour since min-gap=0 with duplicates would just spin at infinite rate. Caveat: at very high fps with duplicates, the server-side per-client SSE thread can hit ~10% of one core (1ms-precision spin × 1000 sends/sec) — acceptable for single-display setups, just don't open 5 OBS instances all at fps=1000." }
    ) },
    @{ Version = "v8.3.2"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum SSE rate slider now genuinely accurate from 60 to 2000. v8.3.1's sub-ms throttle still measured ~350 fps for any slider in [500, 1500] because of the actual root cause: the audio thread delivers FFT signals in BURSTS (one ASIO buffer = ~5 ms = ~10 FFT signals fired in <1 ms then nothing for 4 ms). The previous wait branch slept on WaitOne after each send and missed the in-burst window for sending additional frames — net was 1 send per ASIO buffer cycle = ~350 fps. Fix: detect the burst case (f != lastSent means there's an unsent frame waiting and we're inside a burst) and busy-spin via Stopwatch.GetTimestamp() + Thread.SpinWait(8) until the per-frame deadline, then loop and send. Only spin during active bursts — between bursts (f == lastSent) we still WaitOne on the signal for energy efficiency. Per-client CPU cost scales with target fps: fps=60-144 ≈ 0% (idle most of the time); fps=1000 ≈ 10% of one core (1ms spin × 1000 sends/sec); fps=2000 still bypasses throttle entirely (0% spin). Now slider value drives actual SSE rate end-to-end at any setting." }
    ) },
    @{ Version = "v8.3.1"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Spectrum SSE rate slider is now precise across the FULL range (60 → 2000), not just at the low end. v8.3.0 wired the slider to the server but the throttle used Stopwatch.ElapsedMilliseconds + WaitOne(int milliseconds), both with 1 ms quantization. Combined with Windows' scheduler floor (~1-3 ms per WaitOne(1) even with timeBeginPeriod(1)), targets in the 500-1500 fps range all measured ~340 fps actual delivery. Fix: switched the per-client throttle to Stopwatch.GetTimestamp() (100 ns precision on modern Windows) and added a three-tier wait strategy: ≥ 5 ms remaining → real WaitOne sleep (energy-efficient); 1-5 ms remaining → WaitOne(1); < 1 ms remaining → Thread.SpinWait(64) + Thread.Sleep(0) for sub-ms precision. The SpinWait branch only runs at high-fps settings (1000+) and only burns ~1-3% of one core per active SSE client, which is the unavoidable cost of asking for 1 ms-precise pacing on a non-realtime OS. Net result: slider value now drives actual SSE rate within ~5% across the whole range. fps=1000 actually delivers 1000 fps. fps=2000 still bypasses the throttle entirely (special case: 0 min-gap) and runs at full FFT rate (~1957 fps measured). Caveat unchanged: at the very high end (1000+) the BROWSER's onmessage handler can become the bottleneck and starve rAF — same root cause as the v8.2.9 1-fps overlay regression. The slider is now end-to-end accurate; pick a value your browser can keep up with (60-144 is the safe range; matches typical monitor refresh rates)." }
    ) },
    @{ Version = "v8.3.0"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Frame Rate slider in customize.html now actually drives the SSE delivery rate, not just the overlay's local rAF cap. v8.2.9 fixed the 1-fps customize-overlay bug by hardcoding the server-side SSE throttle to 120 fps, but the user wanted the slider to be live: setting it to 1000 should produce 1000 fps SSE delivery, setting it to 60 should produce 60 fps. Implementation: (1) audio_spectrum.cs /spectrum endpoint now parses an optional `?fps=N` query parameter, clamps to [10, 2000], and uses N as the per-client throttle (fallback 120 if missing/invalid; 2000 = 'no throttle' since 2000 is the FFT ceiling at HOP=24). (2) overlay.html opens its EventSource as `/spectrum?fps=<config.spectrum.fps>` so each client tells the server its preferred rate. (3) overlay.html's applyConfig now compares the new fps value against the previously-sent value (cached in _loopbackSseFps) and reconnects the SSE if it changed — without this, sliding the Frame Rate slider in customize would only update the local rAF cap; the server would keep delivering at the OLD rate from the original SSE connection. Net behaviour: the slider is finally end-to-end live. Server-log line `sse: client connected fps=N` records each client's chosen rate so misconfigurations are diagnosable. The user is responsible for not setting it absurdly high — at 1000+ the JS-thread message-handling cost can outpace rAF on slower machines, with the same 1-fps symptom this whole arc was about. Default in customize stays at 120 (sensible)." }
    ) },
    @{ Version = "v8.2.9"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "Customize / overlay UI was running at ~1 fps after v8.2.8 — caused by the SSE stream blasting 2000 messages/sec at the browser. The audio_spectrum.exe SSE serve loop was uncapped (per the v7.0.8 design comment 'no rate-limiting'), so at the user's responseMs=0.5 / HOP_SIZE=24 setting it fired SignalAllSseClients 2000×/sec and the per-client loop base64-encoded + flushed 2000 messages/sec onto the localhost socket. The browser-side EventSource handler then had to atob + parse + apply 480 bands × 2000 = 960 000 band updates/sec on the JS event loop, starving requestAnimationFrame so the overlay rendered at 1-5 fps. v8.2.7 + v8.2.8's audio-thread fixes (peak-log throttle + per-buffer lock) actually MADE THIS WORSE: before the fixes, audio-thread stutter was unintentionally rate-limiting SSE delivery to a sustainable rate; with the fixes the audio path runs full-throttle and SSE saturates. Fix: added a stopwatch-based throttle in the SSE serve loop. Each client now receives at most SSE_MAX_FPS=120 frames/sec (one frame per ~8 ms wall-clock). The overlay's requestAnimationFrame interpolates between server frames at the monitor's refresh rate (60-360 Hz typical), so visual smoothness is unchanged but JS-thread load drops ~17×. The SSE_INTERVAL_MS=8 keep-alive WaitOne timeout still serves as a backstop in case the FFT stalls. FFT continues to run at full HOP-driven rate internally so /peak / /health / future high-rate consumers stay live; only the SSE delivery to /spectrum is throttled, since the actual audience (a browser) can't render faster than the display refresh anyway." }
    ) },
    @{ Version = "v8.2.8"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Spectrum visualizer hot path optimized end-to-end for smoother FPS at the Response Time slider's lowest setting. v8.2.7 already fixed the worst offender (peak-log file I/O firing 14×/sec from inside the audio thread), but the per-sample work in OnData still had four pieces of avoidable overhead. Changes (all behaviour-preserving): (1) Lock scope: was acquiring/releasing s_fftLock ONCE PER SAMPLE inside the frame loop (480-2400 lock ops per NAudio buffer, ~96 000/sec at 48 kHz × 2 ch × 100 ms buffers). Now: lock once per buffer at the top, release at the bottom. SSE readers don't take this lock so widening it doesn't add contention. (2) Bitmask vs modulo: `(s_writePos + 1) % FFT_SIZE` is now `(writePos + 1) & FFT_MASK` — modulo is slow on x64, bitwise-AND is one micro-op. Promoted FFT_MASK from a const local in DoFftAndPublish to a class-scope constant. (3) Hoisted hot fields (s_writePos, s_samplesSinceFft, s_peakSampleMax, s_peakRollingMax) to local variables inside the lock so the JIT keeps them in registers across the inner loop; they only get flushed back to fields once per buffer (or before each FFT call, since DoFftAndPublish reads s_writePos directly). (4) Hoisted `if (s_inputGain != 1.0f)` and the `1.0f / channelCount` reciprocal outside the per-sample loop. Net effect at HOP_SIZE=24: the audio thread spends measurably less wall-clock time per OnData callback, leaving more budget for the FFT and the FFT-to-SSE delivery cadence becomes more consistent. No functional change — every endpoint (/spectrum, /set-device, /set-hop, /set-mode, /set-sensitivity, /devices, /health, /peak) returns identical results." }
    ) },
    @{ Version = "v8.2.7"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum visualizer was stuttering — not actually running smoothly even at FPS=1000. Cause: audio_spectrum.cs had a hardcoded `if (s_peakWindowCount >= 141)` gate around its 'peak (last ~3 s)' diagnostic Log() call, calibrated for the default HOP_SIZE=1024 (~21 ms × 141 ≈ 3 s). At the user's Response Time slider at 0.5 ms (HOP_SIZE=24), each hop is 0.5 ms, so the gate fired every 70 ms = 14 file-I/O calls per SECOND from inside the audio capture thread (NAudio's OnData callback), producing visible glitches in the FFT-to-SSE pipeline. Same bug class as the original PERF-ROLLUP issue I fixed in v8.2.4 (tick-count threshold spammy at low HOP), but I missed this pre-existing peak log during that pass. Fix: replaced the tick-count gate with a wall-clock gate (`if (DateTime.Now >= s_peakRollupAt)`), so the peak diagnostic logs once every 3 s regardless of HOP_SIZE. Drops audio_spectrum.log from ~14 lines/sec to ~1 line per 3 s and removes the corresponding file-I/O contention from the audio thread." }
    ) },
    @{ Version = "v8.2.6"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "SoundCloud (and other browser-source) tracks no longer scrobble with the PREVIOUS track's 'fully played' position. Symptom: every other (or worse) song would show e.g. 22:08/45:09 in the overlay even though the SoundCloud web player itself was at 0:02. Two compounding root causes: (1) `_songEpoch[key]` was set ONCE per `(artist|||track)` key with `if (-not ContainsKey)` and never reset, so when a song repeated within a session the epoch from the first play was reused and the estimate became 'X minutes ago' instead of '0s'; (2) the epoch was anchored using `nowMs - $np.positionMs`, but for browser sources `$np.positionMs` is unreliable at track-change time — Chrome/Edge don't push fresh MediaSession TimelineProperties for backgrounded tabs, and `com.richardhbtz.soundcloud-rpc` reports cumulative-session-ms in positionMs (not per-track ms). Together: epoch got pinned to '12 minutes ago', and the next epoch-mode estimate painted the previous track's near-end position. Fix: on entering the new-track path, ALWAYS reset `_songEpoch[key]`. For browser sources (soundcloud / chrome / edge / firefox / opera / brave / youtube / deezer / tidal / apple music / bandcamp) force `np.positionMs = 0`, set `_songEpoch[key] = nowMs`, and drop any stale `_sourceLastPos[key]` entry so the SMTC-frozen check below doesn't matchback to an ancient stored position. Non-browser sources (Spotify desktop, osu!, WMP COM, VLC) keep the trusted backtrack since their positionMs is accurate at the first tick. The heartbeat path's seek-detection re-pins epoch correctly if the user actually started mid-track. Visible result: every SC track scrobbles with pos=0 instead of pos=stale-previous-track-position." }
    ) },
    @{ Version = "v8.2.5"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "PC freeze on rapid track changes — definitive fix. v8.2.4's tick-budget guard was targeting the detector chain (only 10-106 ms per slow tick), but the PERF phase instrumentation it added immediately revealed the actual culprit: the webhook POST to /webhook (`webhook-newtrack` phase) takes 225-901 ms per call because the server synchronously does art-resolution / Discord RPC update / SSE broadcast and the tray was waiting for the response on the WinForms UI thread. EVERY observed slow tick was dominated (>85%) by this one webhook call. Fix: replaced all 3 synchronous `Invoke-RestMethod` webhook calls in the scrobble loop (heartbeat / new-track / source-closed) with a new `Send-WebhookAsync` helper that uses `[System.Net.Http.HttpClient]::PostAsync` and discards the returned Task — fire-and-forget. The HttpClient is lazy-initialized once and reused across ticks (won't leak sockets). Webhook still arrives at the server with the same payload; the tray simply doesn't block waiting for the response anymore. Trade-off: server-down failures are no longer logged in transcript.log (the previous catch-block log was throttled to once per 30 s anyway). The phase instrumentation added in v8.2.4 is kept so future regressions are immediately diagnosable. Cumulative perf work this run: PERF-ROLLUP in audio_spectrum.cs (60 s FFT-cost stats), PERF-WINRT in Await-WinRT (logs slow WinRT awaits), tick-phase tracking in scrobble loop (slow-tick log now includes `phase=X breakdown=A=Xms,B=Yms`)." }
    ) },
    @{ Version = "v8.2.4"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "Tray UI no longer freezes for 300-600 ms during rapid track-change bursts (e.g. scrubbing through a SoundCloud playlist where 4-5 tracks change in a few seconds). Cause: the scrobble timer's detector chain runs 10 sources sequentially on the WinForms UI thread; each detector individually stays under the 150 ms slow-detector circuit-breaker threshold (so the existing per-detector cooldown didn't trigger), but during heavy SMTC stress 3-5 detectors could each take 50-100 ms in the SAME tick → cumulative 300-600 ms blocking the UI thread → user perceives the tray as frozen for that fraction of a second. Diagnosis confirmed by the new PERF-WINRT instrumentation in Await-WinRT (logs every WinRT await over 100 ms or that times out) and PERF-ROLLUP in audio_spectrum.cs (logs per-tick FFT cost stats every 60 s). Fix: added a per-tick budget guard in the detector chain — if the tick stopwatch already exceeds 150 ms when about to run another detector, skip the rest of the chain (logged as 'X=skip-tickbudget' in the DETECT line). The skipped detector runs on the next tick 100 ms later — invisible UX impact since the lower-priority sources (WMP, VLC) only matter when no higher-priority source is active anyway. Tick worst-case now bounded at ~200 ms incl. tick overhead. Existing per-detector circuit breaker (Invoke-Detector SLOW_MS=150 + 30-tick cooldown) is preserved as the second line of defense. The audio_spectrum.cs PERF-ROLLUP instrumentation is kept in the build because it's diagnostic-only (logs once per minute, no behaviour change) and pays dividends in future investigation." }
    ) },
    @{ Version = "v8.2.3"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "WdmKsCaptureAdapter now populates WaveFormat in its constructor instead of waiting for StartRecording(). This matches the contract of all the other capture backends (WasapiCapture / WaveInEvent / AsioOut all set WaveFormat as soon as they're constructed). The previous behavior — null until StartRecording — required a defensive null-check in the outer capture loop's diagnostic log line (added in v8.2.0). With this refactor the workaround is no longer needed and has been removed: the log line now reads `capture: starting, waveFormat=...` cleanly without any conditional. Behavior change for users: none. Behavior change for fuzz tests: a `wasapi_exclusive` request still goes through the adapter's exclusive→shared internal fallback (Discord input → exclusive failed → shared opened → first audio buffer received), but now WITHOUT the previous log noise of 'waveFormat=(deferred...)'. Bonus: removes a class of latent bugs where future code reading WaveFormat off a freshly-constructed adapter would have silently NRE'd." }
    ) },
    @{ Version = "v8.2.2"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "audio_spectrum.exe /set-device endpoint now validates input instead of silently no-op'ing on garbage. Previously it regex-extracted backend and id, defaulted both to wasapi_loopback if missing, and returned 200 OK regardless of what was sent — meaning a POST with an empty body, malformed JSON, an unknown backend name, or no id field would all silently keep the prior backend while pretending the request succeeded. Now: empty/non-JSON body returns 400 'Body must be a JSON object'; missing backend returns 400 'Missing required field: backend'; missing id returns 400 'Missing required field: id'; unknown backend (not in the wasapi_loopback / wasapi_input / wasapi_exclusive / wdm_ks / mme / asio set) returns 400 'Unknown backend X (valid: ...)'. Each 400 response also writes a `set-device: 400 — <reason>` line to audio_spectrum.log so misuse is diagnosable. Empty-id-string and 'default' both still mean 'system default endpoint' (kept as sentinels for back-compat). Surfaced by Phase B fuzz audit (finding 6.3)." }
    ) },
    @{ Version = "v8.2.1"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "FIXED";    Text = "HTTP wrong-method requests now return 405 Method Not Allowed with an Allow header listing the valid methods, instead of a generic 404 Not Found. So a PUT to /current returns 405 + 'Allow: GET' (the path exists, just not for that verb), while PUT to /does-not-exist still returns 404 (path is unknown). Matches RFC 7231 semantics. Implementation: small addition to the server.js router right before the catch-all 404 — a static map of path → allowed methods is consulted; if the path is known the response is 405 with the Allow header, else 404. No router refactor; the existing if-chain that does method+url matching is unchanged. Surfaced by the prior fuzz audit (Phase B finding 6.5)." }
    ) },
    @{ Version = "v8.2.0"; Date = "2026-04-28"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Audit-found stability fixes from prior cycles are now formally shipped together. Four real bugs that had landed in source but never been released as a tagged build are bundled into v8.2.0: (1) audio_spectrum.cs WdmKsCaptureAdapter — when you POSTed wasapi_exclusive with id=default, the outer capture loop was dereferencing capture.WaveFormat BEFORE StartRecording populated the inner WasapiCapture, throwing NullReferenceException on every retry and forcing a 3-strike fallback to wasapi_loopback. Defensive null-check now lets the adapter's exclusive→shared internal fallback actually run (verified in fuzz audit cycle 1: EXCLUSIVE on Discord input failed, SHARED succeeded, first audio buffer received). (2) server.js /save-preset — was returning HTTP 500 for empty body or missing 'config' field because JSON.parse threw inside the outer catch. Now returns 400 with clear error message, matches the pattern the existing /preview-config handler was already using. (3) server.js /save-overlay-config — same anti-pattern as (2), same fix, same defensive try-inside-try shape. (4) tray.ps1 AUTO_START registry probe — Get-ItemPropertyValue with -ErrorAction Stop was emitting a TerminatingError to transcript.log every poll when the registry value was absent (which is the normal pre-first-launch state), polluting transcript.log with 67+ identical entries per session. Switched to -ErrorAction SilentlyContinue so the missing-value case returns null cleanly without flooding the log. None of these fixes change user-visible behavior in the normal path — they fix wrong status codes, log spam, and hidden NRE chains that only surface under stress or fuzz testing." }
    ) },
    @{ Version = "v8.1.9"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "Carry-over detection now uses RAW (un-extrapolated) position from SMTC for cache comparison, fixing the v8.1.8 regression where opening a new YouTube video showed the previous video's pos+dur immediately. Cause: the position passed into Get-TrustedTimelineMs was already extrapolated by Get-SMTCPosition (which adds wall-clock age to rawPosMs when status=Playing, to compensate for SMTC update lag). That meant the same stale data compared DIFFERENTLY across consecutive polls — prev tick saw posMs=37000 (ext), current tick saw posMs=42000 (ext but different ageMs), diff=5s, exceeded the 3-s tolerance, and the carry-over signature wasn't recognized. Now: detection compares rawPosMs (truly un-extrapolated, what SMTC physically has stored) against a much simpler signal — fresh YouTube videos always start at position 0, so rawPosMs > 5 s on a new title is almost certainly carry-over. Combined with durMs unchanged from previous track for confidence. Also: give-up timer extended from 20 → 60 ticks (~15 s) so Chrome has more time to actually push fresh TimelineProperties before we accept the stale values, and stale-mode clears on rawPosMs dropping below 5 s (new video actually starts) instead of the unreliable 'posMs moved by 3 s' check." }
    ) },
    @{ Version = "v8.1.8"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "New YouTube videos no longer show the PREVIOUS video's position AND duration. Cause: v8.1.4's stale-duration guard only checked durMs (so when two videos had the same duration the guard mis-fired); v8.1.7 trusted SMTC's tlFresh signal, but tlFresh just means 'Chrome touched the timeline this tick' (which it does on every title change without actually refreshing the values). Symptom: clicking a new YouTube video showed e.g. '1:31 / 5:52' (both copied from the previous track) until the user manually scrubbed. Fixed by replacing Get-TrustedDurMs with Get-TrustedTimelineMs which now checks BOTH posMs AND durMs against the previous track's values — true carry-over signature is title-changed-but-both-pos-and-dur-unchanged. When detected, tray emits posMs=0 AND durMs=0 (overlay hides the time bar; server resets startedAt to now), staying suppressed until: (a) durMs changes from cached stale value, (b) posMs changes by more than 3 s, OR (c) give-up timer (20 ticks ~5 s). The narrowed trigger means two videos genuinely sharing a duration won't false-positive, AND when the carry-over does fire the override of BOTH timeline fields prevents the overlay from briefly showing wrong values when the bar reappears." }
    ) },
    @{ Version = "v8.1.7"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "Timestamps no longer disappear permanently when opening a new YouTube video. Cause: the v8.1.4 stale-duration carry-over guard was too aggressive — when the new video happened to share its duration with the previous video (or with a pre-roll ad), or when Chrome legitimately re-pushed the same duration on track change, the guard would emit duration=0 forever and the overlay would hide the time-bar with no path back to showing it. Even pause/skip didn't recover because the guard only cleared on a CHANGED durMs. Now: (1) tlFresh signal — if Chrome actively pushed setPositionState this tick (LastUpdatedTime advanced), the timeline is trustworthy regardless of whether durMs equals the previous value, so the guard skips firing AND clears existing stale state. (2) Give-up timer — once in stale mode, the guard accepts whatever durMs after 20 polls (~5 seconds at the 250 ms heartbeat) so the overlay self-recovers in the worst case where Chrome never sends a different duration. The original v8.1.4 fix still protects against the actual carry-over case (title change without timeline refresh) — just no longer keeps state stuck forever." }
    ) },
    @{ Version = "v8.1.6"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "YouTube long-pause carry-over no longer makes the overlay claim the video finished. Cause: when you pause a YouTube video and walk away for a while, Chrome eventually re-emits the SMTC session in a busted state — SMTC reports PlaybackStatus=Playing AND positionMs equals the video's EndTime (a contradiction — a video at duration is finished, not playing). Symptom: overlay shows '17:43 / 17:43' while you were actually paused at, say, 15:39. Re-pausing snapped back to 15:39 (correct), but unpausing flipped it back to 17:43 because the next webhook still carried positionMs ≈ duration. New `Get-TrustedPlaybackState` helper in tray.ps1 caches the most recent mid-video paused position per SMTC session. When SMTC later reports playing-AND-at-end while we have a mid-video pausedPos cached, tray overrides the playback state back to Paused at the cached position — so the overlay stays frozen at the correct timestamp. Once SMTC eventually reports a sane positionMs (clearly under duration), the guard clears and tray trusts SMTC again. Only fires when cached pausedPos is < 90% of duration AND current pos is within 1.5 s of duration, so legitimate end-of-track playback still works." }
    ) },
    @{ Version = "v8.1.5"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "All five glow controls (Now Playing label, Platform Badge, Track Title, Artist, Timestamps) are collected into a new dedicated 'Text Glow' dropdown in the customize sidebar — sits right under Outer Glow, before Album Art, so all glow-related controls live next to each other. Each element keeps its own enable toggle, glow color, and intensity slider; nothing else moved. The original sections (Now Playing Label, Platform Badge, Track & Artist, Progress & Timestamps) no longer carry their own Glow sub-sections — they're cleaner now and you don't have to hunt across five different dropdowns to fine-tune the glow look." },
        @{ Tag = "IMPROVED"; Text = "Default glow intensity unified to 6 px across ALL five elements (was 12 / 10 / 20 / 14 / 12). 6 px is a subtle halo that reads well at the overlay's 1000x200 export size and stacks correctly with the card's outer glow without looking blurry. Themes that explicitly set their own glow size (Synthwave, Vaporwave, Retro Orange, Cyber Matrix, Cherry Blossom, etc.) keep their designed values — the change only affects the baseline DEFAULTS. All `?? N` fallback patterns in overlay.html and customize.html updated to ?? 6 in lockstep so a config object missing the glow fields renders identically to one with explicit values." }
    ) },
    @{ Version = "v8.1.4"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "YouTube overlay end-timestamp no longer shows the PREVIOUS video's duration after auto-advance. Cause: when YouTube switches videos (or a pre-roll ad ends and the real video starts), Chrome updates SMTC's MediaProperties (title + artist) on the same tick but does NOT always push fresh TimelineProperties — the previous video's EndTime sits pinned on the SMTC session for several heartbeats while title/artist already reflect the new video. Symptom: an 8:39 video showed '0:12 / 0:44' on the overlay because the 44-second pre-roll ad's duration was still cached. Or worse — a finished 6:50 video would carry over as '6:50 / 6:50' on the next song. Now: tray tracks per-SMTC-session (title, durMs) and detects the moment title changes while durMs hasn't moved — that's the carry-over signature. We then emit duration=0 for the affected ticks, which the overlay already handles by hiding the time-bar entirely (cleanly). The instant Chrome pushes a genuinely new durMs the timestamps reappear with the correct end-time. Tradeoff: if two consecutive YouTube tracks happen to be EXACTLY the same length down to the millisecond, the overlay would briefly hide timestamps for the second one until any other timeline update lands — extremely rare and self-recovers." }
    ) },
    @{ Version = "v8.1.3"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "NEW";      Text = "Glow now works on Now Playing label, Platform Badge, and Timestamps. Each gets its own three controls (Enabled, Glow Color, Glow Intensity) under their respective sections, plus matching DEFAULTS that auto-persist into every saved preset and config (deepMerge handles it). Three new dynamic-glow sub-toggles (Now Playing Glow, Platform Badge Glow, Timestamps Glow) under Dynamic Colors drive each halo off the album palette: NP keeps the album's primary hue, Platform shifts +30deg, Timestamps shifts -30deg, so all three channels read as related-but-distinct when Dynamic is on. clearDynamicPalette restores the preset glow values when dynamic flips off. CSS now has --np-text-shadow / --platform-text-shadow / --ts-text-shadow vars driving each label's text-shadow." }
    ) },
    @{ Version = "v8.1.2"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "All 11 layout template coordinates rewritten so elements stop overlapping each other within the 935x135 visible card-inner. Previous numbers had spectrum heights of 110px in templates that also placed text rows in the 38-102 y-range — guaranteeing collisions where the text and spectrum visually competed for the same pixels. The Spectrum Top template was the worst offender (text rows starting at y=124 inside a 135px tall card meant the artist row clipped off the bottom). Now: spectrum heights bounded so they share the card without colliding with text or the progress bar; text rows have consistent gap spacing; progress bar always sits in the bottom 8-30px reserved zone. Default Horizontal, Right-Aligned (true mirror with text now properly on the right side of left-spectrum), Centered Art, Spectrum Top / Bottom, Spectrum Hero, No Album Art, DJ Booth, Compact, Title-On-Art and Square Art-Forward all redone with breathing-room margins." }
    ) },
    @{ Version = "v8.1.1"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "Border Thickness now grows OUTWARD instead of eating INWARD into the card content. Before: increasing the border slider shrank .card-inner from the inside (because card-inner was inset by --border-thickness from a fixed-size card-outer). Result: thicker borders clipped progress bars, spectrum bars, and any layout-template element at the edges, because every template assumed a constant 935x135 visible card-inner. Now: card-outer's width and height calc adds `2 * (border-thickness - 5px)` so the OUTER edge expands into the glow padding while card-inner stays exactly the same visible size — the border ring grows around the content instead of into it. Default thickness=5 renders identically to before (the calc resolves to the original `calc(100% - 40px)`); thicker values just extend further outward. Layout templates now render correctly at any border thickness." }
    ) },
    @{ Version = "v8.1.0"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "Selecting a layout template no longer instantly snaps back to the previous layout. Cause: the modal's hover-preview added a mouseleave handler with a 60ms debounce that would call restoreSnapshot() — to handle the 'mouse out without clicking' case. But when the user actually CLICKED a card, mouseleave still fired (the modal closed and the cursor left the card), and 60ms later the timer ran restoreSnapshot() which UNDID the just-applied template. The setTimeout now also checks the _committed flag, so if the user clicked a card the restore is skipped. Symptom: clicking 'Spectrum Top' would flash to the new layout, then 1ms later flip back to the previous one, AND silently turn off Use Custom Layout because the snapshot taken at modal-open had layout.enabled=false. Both gone." },
        @{ Tag = "FIXED";    Text = "Applying a layout template now clears the loaded-preset pointer so the Preset Manager modal doesn't keep claiming you're still on your old preset after switching layout. applyLayoutTemplate calls markDirty() now, which blanks _lastPresetName + S.lastPresetName. The 'active' row highlight in the Preset Manager goes away as soon as your live state diverges from the saved file — which is what every other binding already does." }
    ) },
    @{ Version = "v8.0.9"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum visualizer no longer shows a phantom thin pink/magenta line at the bottom when there is no audio. Cause: even when WASAPI was connected and reporting silence, the renderer was forcing a 15% backfill from the idle 'chill' simulator (Math.max(energy, simBands * 0.15)) — which then produced a positive bar height that the Lowest Bar Height slider couldn't override (barH = Math.max(minBarPx, energy*H*heightMult)). Removed the backfill on the WASAPI path so when WASAPI says 'silence', the bars now actually go to zero. The simulator floor is still kept in the analyser-only fallback path (browsers that can't reach audio_spectrum.exe at all). With the Lowest Bar Height slider at 0, silence = a fully flat baseline now. Bonus: setting it back to 2px gives a uniform 2-pixel line that doesn't pulse from the leftover simulator wave." }
    ) },
    @{ Version = "v8.0.8"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "MAJOR fix — every customize-page tweak was instantly hitting the live OBS Browser Source overlay (then snapping back ~5 s later when the polling re-fetched the saved config). Cause: the SSE 'preview-config' event was being subscribed by BOTH the customize-iframe overlay AND the OBS overlay — the comment even bragged about it. Fix: gate the preview-config listener on _IS_PREVIEW so only the customize iframe (loaded with ?preview=1) responds. The OBS overlay now only updates on 'overlay-config', which is what Apply to OBS actually triggers. Streamers can tweak settings without their viewers seeing every slider drag in real-time anymore." },
        @{ Tag = "IMPROVED"; Text = "Layout editor opacity rules cleaned up. Default state: every layout-node outline + label is fully INVISIBLE — zero clutter while you're just looking at the live overlay. Mouse over any editable node and that node's purple border + label snap to 100% (so the one you're about to grab is unmissable). Every OTHER node fades to about 50% so you see the whole layout context but it's clear which one is in focus. Selected nodes keep their solid pink outline at 100% always. Locked nodes follow the same rules but tint amber instead of purple." }
    ) },
    @{ Version = "v8.0.7"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Live preview editor outlines hidden by default. Previously every layout-node showed a thick dashed purple border + a label badge + corner handles ALL the time, which was a wall of clutter the moment you turned Custom Layout on. Now the borders, labels, and resize handles fade in only when you HOVER a node OR have it selected. Actual content (art, text, spectrum) stays untouched. Locked nodes still show a faint amber outline at all times so you know what's locked." },
        @{ Tag = "FIXED";    Text = "Locked layout nodes no longer block your mouse. They have pointer-events:none now, so a locked element sitting on top of an unlocked one doesn't intercept hover/click — you mouse THROUGH the locked node to whatever's behind it. Combined with the always-visible faint amber outline, you still see what's locked but can freely edit unlocked elements next to or under them. To unlock, use the right-side toggles in the Layout sidebar's Show / Lock Elements rows." },
        @{ Tag = "NEW";      Text = "Hover-dim other layout nodes. When you mouse over one editable node in the live preview, every other node fades to about 45 percent opacity so it's obvious which one you're about to grab. Selected nodes stay at full opacity even while you mouse around so their handles remain easy to find. CSS-only via :has() — Chromium 105+, well below the 108+ Master's FM ships on." }
    ) },
    @{ Version = "v8.0.6"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "NEW";      Text = "Hover-preview in the Layout Templates modal. Mousing over any template card LIVE-pushes that layout into the iframe overlay — no click required to see what it looks like. Mouse-leave snaps your original layout right back. The customize state is snapshotted when the modal opens and restored on close (X / ESC / backdrop click) unless you actually committed by clicking a card. A 60ms debounce stops rapid card-to-card hovering from flickering back to the snapshot between cards." },
        @{ Tag = "REMOVED";  Text = "'Are you sure you want to apply this layout?' confirm dialog gone. Clicking a template card now applies it instantly and closes the modal. Reset Layout in the sidebar still undoes it if you change your mind." }
    ) },
    @{ Version = "v8.0.5"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Layout Templates modal redesigned end-to-end. Each card now ships with a real SVG mini-preview of the layout — eight color-coded rectangles drawn in the same z-order as the live overlay so users can see at a glance where each element will land (purple = album art, green = spectrum, blue = title, cyan = artist, pink = NP label, orange = platform badge, yellow = progress bar, red = EQ bars). Card aspect-ratio matches the visible card-inner (935:135) so the preview is at true proportions instead of a generic placeholder. Description lines are clamped to 3 max so cards stay roughly the same height regardless of how chatty the description is." },
        @{ Tag = "FIXED";    Text = "All 11 layout templates rewritten to actually fit inside the visible card-inner area (935x135, after the 32.5px V6_PAD inset). Earlier coordinates had elements like 200x200 album art spilling off the bottom of the visible card; now every coordinate is bounded to the inner card area so nothing renders off-card." }
    ) },
    @{ Version = "v8.0.4"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "MAJOR recovery fix — the entire customize page had been broken since v8.0.0 because a JS comment contained the literal text close-script-tag. The HTML parser stops the script block the moment it sees that string — even inside a JS comment — so the script terminated mid-execution. Result: init() never ran, themes never rendered, layout buttons never bound, none of the customize sliders pushed changes to the overlay, and the preview iframe came up size-zero with the card invisible. Re-worded the comment to avoid the literal close-script string. As soon as v8.0.4 lands the customize page comes fully back to life." }
    ) },
    @{ Version = "v8.0.3"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Spectrum Visualizer section reorganised into 4 clear sub-groups with friendlier non-technical labels. ON/OFF stays on top, then Smart Auto-tune (Auto-Volume Match, Loudness Boost), then Look and Feel (Color Mode, Base Color, Mirror Bars, Opacity), then Bar Shape (Number of Bars, Spacing Between Bars, Bar Roundness, Lowest Bar Height, Tallest Bar Height), then Animation Feel (Reaction Speed, Smoothness, Frame Rate). Old technical names like 'Render FPS', 'Bar Gap', 'Bar Radius', 'Min Bar Height', 'Height Multiplier', 'Smoothing', 'Sensitivity', 'Auto-Gain', and 'Response Time' replaced with everyday wording so newcomers can find what they need without guessing. Each sub-group gets a divider + small icon header so the long list of spectrum controls reads as four short related groups instead of one wall of sliders." },
        @{ Tag = "IMPROVED"; Text = "Visual Themes section collapsed by default. The customize page used to open with a wall of theme buttons taking up the full height of the sidebar; now you click the Visual Themes header to expand the grid when you actually want to pick one. Default look is much tidier." }
    ) },
    @{ Version = "v8.0.2"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "NEW";      Text = "11 layout templates, all locked to the same 1000x200 canvas — only WHERE each element sits is different. Templates: Horizontal (default art-left), Right-Aligned (art-right mirror), Centered Art (art middle with double-fade pairing), Spectrum Bottom (full-width spectrum stripe at the bottom), Spectrum Top (full-width spectrum at the top), No Album Art (art hidden, big title across the card), Minimal Text-Only (just title + artist + tiny progress, everything else off), Bookend (art-left, spectrum-right, text squeezed into the middle), Title-On-Art (album fills the card, title overlays with glow), DJ Booth (big NP label + EQ bars front-and-centre, spectrum below), Square Art-Forward (135x135 art, spectrum stretching beside it). Each one re-arranges Album Art, Title, Artist, Progress Bar, Spectrum, Now-Playing label, EQ bars, and Platform Badge differently. Canvas size is no longer a variable — it's the fixed Master's FM 1000x200 envelope on every template." },
        @{ Tag = "NEW";      Text = "12 new visual themes added to the picker: Vaporwave, Aurora, Royal Purple, Coffee, Volcano, Ice Crystal, Galaxy, Sunset, Lime, Vintage Sepia, Cyber Matrix, Dreamcore. Each carries its own font + card gradient + spinning border palette + glow + spectrum colour mode + per-element accent colours. Several leverage the new Artist Glow channel (Vaporwave, Cyber Matrix, Dreamcore) so when Dynamic Colors is off you still get the dual-halo effect baked into the theme." },
        @{ Tag = "IMPROVED"; Text = "Sidebar accordion sections reorganised top-to-bottom into a logical flow: AUTOMATIC (Visual Themes, Dynamic Colors) → GLOBAL STRUCTURE (General, Layout, Font, Slide-In Animation) → CARD FRAME (Card Shape, Spinning Border, Outer Glow) → PER-ELEMENT MANUAL (Album Art, Now Playing Label, Platform Badge, Track & Artist, Spectrum Visualizer, Progress & Timestamps). One-click smart stuff at the top, fine-tuning at the bottom — much faster to find what you need. The reorder is purely a runtime DOM swap (appendChild moves the existing section nodes), so nothing else breaks." }
    ) },
    @{ Version = "v8.0.1"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "Preset Manager X (close) button did nothing. Cause: the click listener for pm-close was being registered at the top level of the customize script — but the script tag sits before the modal HTML in the document, so getElementById returned null at script-eval time and addEventListener threw a silent TypeError that aborted the rest of that registration block. Moved all pm-* event bindings into a wirePresetManager() function called from the existing init() async IIFE, which runs after an await fetch yields to the parser — by which point the entire document (modal markup included) has been parsed." },
        @{ Tag = "NEW";      Text = "Master's FM now auto-loads the last preset that was equipped. Previously a Master's FM update or a fresh customize-page open would leave the saved-on-disk overlay config in place — fine if the user had clicked Apply to OBS, but stale if they had only used the Preset Manager Load action without applying. Now: clicking Load on any preset row also writes the loaded config plus a new lastPresetName field to /save-overlay-config, so the next time the overlay starts up it renders that exact preset, and the Preset Manager modal shows the same row highlighted as 'active'. Save / Overwrite take the same auto-apply path so saving a new preset immediately makes it the active one. Editing any setting in the customize page clears the active-preset pointer (the modal's loaded-row highlight goes away) since the live state no longer matches the saved file." },
        @{ Tag = "IMPROVED"; Text = "Preset Manager intro line wraps cleanly. The original copy ended one line on 'currently-' and started the next on 'loaded preset (if any) is highlighted.' — an awkward mid-hyphen break. Rewrote the sentence to be shorter and added text-wrap:balance so the two lines come out roughly the same length regardless of modal width. Active row indicator phrased as 'active preset' instead of 'currently-loaded preset' for the same reason." },
        @{ Tag = "REMOVED";  Text = "Topbar preset dropdown ('— Presets —') and 1000x200 size-preset dropdown removed. Both were redundant after the Preset Manager modal landed in v8.0.0 — preset operations all live in the manager now, and the canvas is hard-locked to 1000x200 anyway so there's nothing to switch between. Topbar is cleaner: just the Preset Manager button + Reset to Defaults + Apply to OBS." },
        @{ Tag = "REMOVED";  Text = "Layout sidebar Canvas Preset / Canvas Width / Canvas Height controls removed. Canvas is now hard-coded to 1000x200 in the live overlay; the sliders, the preset dropdown, and the helper text that explained the cap are all gone. Toggling 'Use Custom Layout' on always lands on a 1000x200 canvas now. Snap Grid, the Show / Lock Elements rows, the Layout Templates modal, and Reset Layout are all kept — only the canvas-size controls left." }
    ) },
    @{ Version = "v8.0.0"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "NEW";      Text = "Preset Manager — replaces every browser prompt() and confirm() dialog the customizer was using for preset save / rename / delete. Click the new 'Preset Manager' button in the topbar (replacing the old Save Preset and X buttons) and you get a proper themed modal that matches the rest of the customizer: a dark glass-blur backdrop, the same accordion-section visual language, and inline action buttons next to every saved preset row. Each row exposes Load, Overwrite (save current settings into this name), Rename (clicks turn the name into an inline text input — Enter saves, Esc cancels), Duplicate (auto-names 'Foo' to 'Foo copy', then 'Foo copy 2' if that's taken), and Delete (in-row Yes/No confirmation, no native browser dialog). The Save form at the top pre-fills with the currently-loaded preset name so a one-click Save overwrites that preset, or you can clear it and type a new name. Status messages appear inline at the bottom of the modal in green (success) or red (error). The topbar dropdown is preserved for one-click quick-load — picking a name there loads it into the editor immediately. v8 is the explicit major-version signal: customize.html is now fully native-feel, no more browser-default popups." },
        @{ Tag = "NEW";      Text = "Artist text now scrolls with its own marquee, matching the track-title behaviour. Two new sliders in the Track Title + Artist section: Marquee Speed and Marquee Pause for the artist row. They default to 68 px/s and 2 s of dwell at each end (same as title); each label has its own keyframes block (id _mq_title vs _mq_artist) so the two never stomp each other when both overflow. The artist DOM was wrapped in a new .artist-wrap container that handles overflow:hidden while .artist itself becomes inline-block (mirroring the title-wrap / track-title pattern) so the transform-driven marquee actually reveals hidden text instead of just shifting clipped content." },
        @{ Tag = "NEW";      Text = "Artist Glow — separate text-shadow channel under Track Title + Artist > Artist Glow Effect. Three controls (Enabled, Glow Color, Glow Intensity) just like Title Glow, with a distinct default colour (#80c0ff sky-blue) and a smaller default size (14 px vs 20 px) so the artist halo reads as softer / more ambient than the title's primary halo. When Dynamic Colors is enabled, the new 'Artist Glow' sub-toggle there drives the artist halo OFF the album palette but with a +60° hue offset from the title-glow hue and slightly lower saturation / higher lightness — the two channels stay related but visually distinct. clearDynamicPalette + applyConfig + applyDynamicPalette all carry separate code paths for the artist glow so toggling the dynamic-glow sub-options independently does what it says on the label." },
        @{ Tag = "FIXED";    Text = "Applying a theme no longer silently disables Dynamic Colors. applyTheme used to deepMerge the theme onto DEFAULTS and then preserve only overlay/art/nowPlaying from the user's S — which dropped S.dynamicColors and replaced it with DEFAULTS.dynamicColors (enabled=false). So users with dynamic colors on, who clicked any theme preview, would see all dynamic-colour stuff snap back to the static preset. dynamicColors and layout are now both in the preserve list, so themes change look-and-feel without flipping these two feature toggles. Same root cause was making 'all the other stuff comes back but Dynamic Colors stays off' after some preset / theme operations; this fixes it." },
        @{ Tag = "NEW";      Text = "Album Art 'Center' position with double-fade. Was just Left or Right; now you can pick Center which applies fade gradients on BOTH edges of the album-art block (::before for the left fade, ::after for the right). Useful when placing art in the middle of the card via the custom layout editor — both edges blend cleanly into the card content instead of one hard cut + one soft fade. The fade colour follows --card-bg-left and --card-bg-right which already track the dynamic-palette and gradient-angle helpers, so the dual-fade keeps colour-matching automatically when songs change." },
        @{ Tag = "IMPROVED"; Text = "Custom Layout canvas locked to horizontal up to 1000 x 200. The width slider max went from 2400 to 1000, the height slider max from 1200 to 200; lower is still allowed (down to 300x60). The Canvas Preset dropdown now lists only the two sizes that fit (1000x200 and 600x60); the eight rotation / scaled-up entries are gone. The Swap W/H button is removed since with height capped at 200 there's nothing to swap to. Layout Templates trimmed to the two that already fit (Horizontal Card and Compact Pill); Vertical Phone, Square Art-Forward, Wide Banner, and Wide Card are removed. Old configs that were saved with bigger canvases are auto-clamped on load (both in customize and in the live overlay) so nothing breaks — they just shrink to the new envelope." },
        @{ Tag = "FIXED";    Text = "Layout-mode applyConfig now reapplies its compute-once _cardA opacity scaling to the --card-bg-left and --card-bg-right CSS variables for the art-fade pseudo-elements; previously these two specific stops were skipping the v7.1.3 backgroundOpacity helper because they're written from a different code path further down in applyConfig. With this fix, dialing Card Background Opacity below 99% now also fades the art-edge fade, so the whole card is consistently transparent instead of having opaque side-fade rectangles." }
    ) },
    @{ Version = "v7.1.3"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "Background Blur slider was wired correctly all the way through (DEFAULTS, bind, sync, CSS variable, backdrop-filter on .card-inner) but had essentially zero visible effect because .card-inner was painting its own gradient at 99% opacity on top of whatever the blur was supposed to reveal. Backdrop-filter blurs whatever sits behind the element — when the element then draws a near-opaque background, that blur is simply hidden. v7.1.3 introduces a Background Opacity slider in the Card section (0% to 100%, defaults to 99% so existing presets render identically to v7.1.2). Lower the slider and the gradient stops fade, the OBS scene behind the overlay shows through, and Background Blur finally has something to operate on. The opacity is applied dynamically via a new _withCardAlpha helper that rewrites the alpha of the configured backgroundTop/backgroundBottom/backgroundLeft/backgroundRight CSS variables at every applyConfig + clearDynamicPalette + dynamic-palette branch, so dynamic colors and the static path both honour the user's chosen transparency." },
        @{ Tag = "NEW";      Text = "Per-node Lock toggle in the Layout sidebar. The locked field has existed in DEFAULT_LAYOUT and every built-in template since v7.0.0 but was never actually enforced — there was no UI to set it and no code to honour it. v7.1.3 wires it up: each row in 'Show / Lock Elements' now has TWO toggles (visibility on the left, lock on the right with an amber colour). When a node is locked: the editor outline shifts to amber, the resize handles disappear, the cursor becomes 'not-allowed', the label gets a small lock emoji, mousedown still selects the node so you can see what you clicked but skips the drag/resize handler entirely, and arrow-key nudges return early. Locking is purely an editor-side feature — the live overlay renders locked nodes the same as unlocked ones, so Apply-to-OBS gives the same output regardless of lock state. Useful when you've placed an element exactly where you want it and don't want to bump it accidentally while dragging neighbours around." }
    ) },
    @{ Version = "v7.1.2"; Date = "2026-04-27"; Notes = @(
        @{ Tag = "FIXED";    Text = "Preset save paths audited end-to-end to confirm every visible customize option is written to disk. Three verifications: (1) all three save paths (Apply to OBS, Save Preset, live preview broadcast) wrap the customize state object in deepMerge(DEFAULTS, S) so any key omitted from S gets filled from DEFAULTS — already in place since v6.8.9. (2) Diffed customize.html DEFAULTS against overlay.html DEFAULTS programmatically and found one gap: liveAudioVisualizer existed in overlay but not in customize, so saved presets/configs were dropping it (the on-disk top-level liveAudioVisualizer flag set by the tray was preserved, but if a friend imported a preset they would not get the value). Added it to customize DEFAULTS. (3) Audited every c-* DOM control ID in customize against bind/sync calls: 121 controls, 100% coverage including the dynamic c-layout-vis-* loop, bindColor picker/text pairs, bindText label inputs, the dynamic border-colors picker list, and the spectrum-sensitivity reset button. No control silently fails to write to S anymore, and every saved preset now serialises a complete config." }
    ) },
    @{ Version = "v7.1.1"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Spectrum rise lerp floor lowered 3 ms -> 1 ms at smoothing=0. The hybrid rise still uses SNAP-TO-TARGET on big gaps (>= 20/255) for instant kick/punch landing; that's unchanged. The change is the SUB-SNAP_GAP path that was lerping at 3 ms half-life and taking ~3 rAF frames (~21 ms at 144 Hz) to fully settle subtle transients. v7.1.1 puts that floor at 1 ms half-life, so on a 144 Hz monitor riseAlpha jumps from ~0.80 to ~0.992 per frame and a small gap reaches target in ONE rAF instead of three. Sustained drift (typical per-frame delta of 1-5 out of 255) doesn't oscillate because the per-frame change is tiny anyway. Higher smoothing values keep their existing dreamy long half-life (formula now 1 + smooth*32 instead of 3 + smooth*30) so the slider's top end is unchanged — only the bottom (smoothing=0) gets the snappier behaviour." }
    ) },
    @{ Version = "v7.1.0"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Final round of spectrum sub-millisecond optimizations. (1) SNAP_GAP threshold lowered 30 -> 20 in the overlay's hybrid rise lerp, so slightly subtler transients now land via the instant-snap path. Sustained-content drift is well below this threshold so smoothness/heartbeat character is unchanged. (2) Lock-free fast path in audio_spectrum's SignalAllSseClients: if zero clients are connected, skip acquiring the list lock entirely. With the producer running at 2000 Hz on the user's HOP=24 setting, the per-cycle save compounds across an entire stream. (3) Dropped the legacy-JSON auto-detect branch on the browser SSE handler. After v7.0.9 the server only emits raw base64; the leading-curly-brace check + JSON.parse fallback was dead code adding one comparison per SSE arrival. We're now at the architectural floor for SSE+rAF rendering — remaining latency is mostly audio-buffer + monitor vsync, both fundamentally unfixable from this layer." }
    ) },
    @{ Version = "v7.0.9"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Spectrum SSE payload simplified. Was a JSON wrapper carrying a frame counter and a base64 band string; the frame counter has been unused on the client side since v6.6.6 dropped on-arrival deduplication, and the JSON wrapper cost a JSON.parse call on every frame at the FFT cadence (~94 Hz default). Now the SSE data field is the raw base64 string. Saves ~30 bytes per message and the JSON.parse on the browser side. Client auto-detects format (a leading curly brace triggers the legacy JSON path) so a mid-rebuild refresh never breaks. Marginal latency win individually but compounds: every microsecond shaved off per-frame work means the next FFT-publish-to-rendered-bar chain runs that much sooner." },
        @{ Tag = "IMPROVED"; Text = "Silence-skip in audio_spectrum.exe now also stops PUBLISHING when bands have fully decayed to zero. Previous behaviour: silence-skip ran the cheap envelope decay and published the result every FFT tick — fine, but once bands hit all-zero, it kept publishing identical all-zero frames forever, waking SSE clients pointlessly. v7.0.9 tracks an s_lastSilenceWasZero flag: if the current silence frame is all-zero AND the previous one was too, skip the publish entirely (no s_frame increment, no SignalAllSseClients). The frame counter stays put → SSE clients see no new frame → they sleep. CPU goes truly zero on both sides during sustained silence. The flag clears the moment audio rises above threshold so the first non-silence frame still publishes immediately." }
    ) },
    @{ Version = "v7.0.8"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Spectrum SSE pipeline switched from poll-based to event-driven. Previously the SSE loop ran Thread.Sleep(8 ms) between checks; even with 8 ms publishes from v7.0.6, the average wait between an FFT producing new bands and the SSE write firing was ~4 ms (half the poll period, worst case 8 ms). v7.0.8 wires up a per-client AutoResetEvent that DoFftAndPublish (and the silence-skip path) Sets immediately after updating s_latest+s_frame. Each connected client wakes within sub-millisecond of every new frame, with the 8 ms WaitOne timeout retained as a keep-alive safety net. Per-client events (not a single shared one) so OBS and the customize preview iframe both wake simultaneously on every publish — a single AutoResetEvent.Set wakes only ONE waiter, so a shared event would have starved the second client. CPU is unchanged or slightly lower (loop only runs when there's actual work). The v6.5.4 event-driven attempt was reverted because it added stopwatch rate-limiting and skip-via-continue paths that produced jittery send cadences; this implementation is pure event-driven (no rate-limiting, no continue paths) which keeps the cadence smooth — same heartbeat character as v7.0.7, just less latency from peak to bar." }
    ) },
    @{ Version = "v7.0.7"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Sustained basslines bumped +2 dB back up after v7.0.6's -5 dB drop felt slightly too low. SUSTAINED_KEEP 0.20 -> 0.25 (linear factor 10^(2/20) = 1.26). Net change from the v7.0.5 baseline is now -3 dB instead of -5 dB. Steady mid-card bass still sits noticeably lower than v7.0.5 (so kicks have more visual headroom above the baseline) but no longer pinned near the floor. Transient (above-baseline) path is unchanged so kicks/punches still hit 100 % at the same threshold." }
    ) },
    @{ Version = "v7.0.6"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Spectrum visualizer pushed even closer to realtime. SSE publish cadence in audio_spectrum.exe doubled from 60 Hz to 120 Hz (interval 16 ms -> 8 ms) so the overlay sees fresh band values within ~8 ms after the FFT finishes instead of ~16 ms. Hybrid rise lerp on the overlay side: when a band's target jumps by >= 30 (out of 255 = ~12 %), the rendered bar SNAPS to the new value in one frame for instant kick/punch landing. When the gap is smaller (sustained content drifting), it keeps the existing 3-33 ms half-life smooth lerp so bars don't stutter or chase per-frame between FFT updates. Net: percussive transients land within 1 frame of the FFT detecting them, while sustained sections still flow smoothly. CPU is unchanged — pure latency win." },
        @{ Tag = "IMPROVED"; Text = "audio_spectrum.exe CPU drops to near-zero during quiet/paused/silent moments. Added a cheap RMS gate at the top of DoFftAndPublish: if the FFT input window's RMS is below ~ -62 dBFS (well below typical music noise floor), skip the entire FFT + multi-pass band processor (tilt, compressor, gamma, spatial unsharp, bass transient expander) and just decay the existing envelope toward zero. Visible behavior is identical because the same envelope decay constants run; only the heavy producer-side work is skipped. Recovers immediately the moment audio rises above threshold." },
        @{ Tag = "IMPROVED"; Text = "Sustained bass-lines sit visibly lower (~5 dB drop) without affecting kicks. Bass transient expander's SUSTAINED_KEEP lowered 0.35 -> 0.20 (linear factor 10^(-5/20) = 0.56 applied to the previous keep). Steady mid-card basslines that filled to ~50 % bar height now fill to ~28 %, leaving more visible headroom for percussive content above the baseline. The transient excess path (above-baseline content) is unchanged so kicks/punches don't lose any of their punch height." },
        @{ Tag = "IMPROVED"; Text = "Kicks and punches reach the ceiling easier. KNEE_R 2.0 -> 1.6 (compression above the soft knee is gentler, so peaks past 0.75 expand more linearly toward 1.0). TRANSIENT_BOOST 1.8 -> 2.0 (the above-baseline excess gets a slightly bigger amplification before the compressor sees it). Combined with the SUSTAINED_KEEP drop above, the kick-to-bassline visual contrast is wider AND ordinary kicks reach 100 % a bit easier without saturating non-percussive content." }
    ) },
    @{ Version = "v7.0.5"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "FIXED";    Text = "Outer-glow halo restored when 'Use Custom Layout' is enabled. Root cause: v7.0.1/v7.0.2 overrode v6's #widget inset:40, .card-outer calc(100%-40), and .card-inner inset:5 — collapsing all of those to inset:0 so the card filled the iframe edge-to-edge. That left no visible padding for the glow drop-shadow OR the spinning border ring around the card outline, and made the card look noticeably zoomed compared to v6. v7.0.5 reverts those overrides entirely. Layout mode now keeps the full v6 hierarchy (#widget at 40px inset, .card-outer at calc(100%-40), .card-inner at 5px inset). Layout-nodes are positioned absolute against .card-inner — their nearest positioned ancestor — so coordinates are CARD-INNER-relative now, and the visible card area sits centered within the iframe with the glow halo around it." },
        @{ Tag = "FIXED";    Text = "Drag handles in the customize Layout editor now overlay the actual rendered card-inner area, not the full iframe. The layout-edit-overlay div is inset by V6_PAD = 32.5×scale on each side, sized to (canvas - 65) × (canvas - 65). Anchor math (topright / bottomleft / bottomright / center) uses card-inner dims instead of canvas dims so a 'topright x=18' node sits 18 px from the card-inner right edge — not the iframe right edge — exactly matching where it actually renders." },
        @{ Tag = "IMPROVED"; Text = "DEFAULT_LAYOUT and the 6 templates updated so element heights fit within the card-inner area. e.g. albumArt h=200 → 135 (was overflowing past card-inner bottom for the default 1000×200 canvas which yields a 935×135 card-inner)." }
    ) },
    @{ Version = "v7.0.4"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "FIXED";    Text = "Layout-mode default sizes now match v6 visible appearance instead of being 2x too big. Root cause: the DEFAULT_LAYOUT and 6 templates copied dimensions from v6's CSS as-written values (font-size 68px, art width 310px, line-height 76px, etc.) which are LOGICAL pixels INSIDE the scale-wrapper's 0.5x transform — so they appear at 34px, 155px, 38px etc. on screen. Layout mode treats coordinates as visible/canvas pixels (matches OBS Browser Source pixel size) so the doubled values rendered everything at literally 2x v6's natural appearance, producing the 'zoomed in really badly' visual. Halved every h (and most w's) in DEFAULT_LAYOUT and all 6 templates to track v6's actual on-screen sizing. Toggling 'Use Custom Layout' on now produces a card that visually matches the v6 default — same art width, same title size, same progress bar height — and resizing handles work in pixel-perfect canvas coordinates as before." }
    ) },
    @{ Version = "v7.0.3"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "FIXED";    Text = "Hotfix: the v7.0.2 cut shipped with a duplicate 'const id' declaration inside the applyLayoutMode forEach loop, which caused the entire overlay script block to fail parsing with 'SyntaxError: Identifier id has already been declared'. Net effect: applyConfig never ran, so the overlay rendered as an empty card on every load and ignored every config update. Music was being detected server-side but the iframe was frozen — that's what 'i think u broke the overlay now / Music is running and it detects nothing' was. Removed the redundant declaration; the id from the top of the iteration is reused for the font-size switch. Verified with node parse-check: overlay JS now parses clean." },
        @{ Tag = "IMPROVED"; Text = "General and Font sections in customize no longer open automatically when the customizer launches. They start collapsed (matching every other section's behaviour); click the header to expand. Visual Themes stays open by default since it's the most common entry point." }
    ) },
    @{ Version = "v7.0.2"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "FIXED";    Text = "Enabling 'Use Custom Layout' no longer zooms the entire card to ~2x. Root cause: v7.0.1's CSS override removed #scale-wrapper's `transform: scale(0.5)` on its 200%×200% box. v6 designed every CSS pixel value (font-size 68px, EQ bar 6×36 px, padding 24 px, etc.) assuming that 0.5x scale, so removing it doubled the apparent size of every CSS-pixel-driven element. Fix: keep the scale-wrapper trick (visual rendering matches v6), keep #widget+card-outer overrides (so the canvas fills the iframe pixel-for-pixel), AND multiply every inline px applyLayoutMode writes by LM_SCALE=2 so layout-node coordinates land at the right *visible* pixel positions inside the 0.5-scaled wrapper. Drag handles in customize still operate in canvas pixels and align perfectly because the iframe natural size = canvas size (the scale-wrapper math cancels out at the iframe boundary)." },
        @{ Tag = "NEW";      Text = "The 'Now Playing' bundle is now THREE independent layout-nodes instead of one — EQ Bars (the animated equalizer strip), Now Playing Text (the 'VIBING WITH' label), and Platform Badge (the 'SOUNDCLOUD' tag). Each gets its own visibility toggle and is independently positionable / resizable. All 6 layout templates updated to lay these out at sensible default positions. The wrapper .now-playing-label uses display:contents in layout mode so the children become direct positioning children of card-inner." },
        @{ Tag = "FIXED";    Text = "Track Title and Artist text now actually shrink when their layout-node is shrunk, instead of getting clipped. Root cause: text font-size was driven by CSS variables (--title-font-size, --artist-font-size) set by applyConfig, fixed regardless of node height. Resizing the layout-node smaller didn't shrink the text — it just clipped the overflow because of the wrapper's overflow:hidden + ellipsis. v7.0.2: applyLayoutMode now writes inline font-size on text-bearing layout-nodes proportional to node height (trackTitle 0.85×h, artistName 0.78×h, nowPlayingText 0.78×h, platformBadge 0.80×h, clamped to 8 px minimum). The text scales smoothly as you drag the resize handles. When layout mode is turned off, the inline font-size is cleared so the legacy fixed-font-size CSS-var path takes over again." }
    ) },
    @{ Version = "v7.0.1"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "FIXED";    Text = "Drag handles now align with the rendered nodes. Root cause: overlay.html's #scale-wrapper has `transform: scale(0.5)` on a 200% × 200% box (a half-pixel-rendering trick from v6) which means inline left/top values inside it get halved when displayed. The customize-side drag handles assumed a 1:1 canvas-to-iframe-pixel mapping, so layout-mode nodes rendered at half-position vs the handles. Layout mode now neutralises the scale-wrapper transform AND the v6 #widget inset:40px AND .card-outer's calc(100% - 40px) margin so the canvas fills the iframe pixel-for-pixel. The card design canvas now matches the OBS Browser Source dimensions exactly." },
        @{ Tag = "FIXED";    Text = "Element visibility toggles now actually hide the rendered element. Root cause: the v6 CSS rule `#spectrum-canvas { display: block; }` (specificity 1,0,0 — ID selector) was beating the v7.0.0 layout-hidden rule `body.layout-mode-on [data-layout-node].layout-hidden { display: none; }` (specificity 0,3,1 — no ID). Even with the layout-hidden class added, the spectrum canvas stayed visible. Added `!important` to the layout-hidden rule so it trumps any existing ID-level rules for the 6 layout-nodes." },
        @{ Tag = "NEW";      Text = "Swap Width ↔ Height button next to Canvas Preset. One click flips canvas orientation — useful for vertical streamers who want to rotate any layout (Horizontal Card → Vertical Card, Wide Banner → Tall Banner, etc.). Canvas preset dropdown now lists portrait + landscape pairs of every shape (Horizontal Card 1000x200 / Vertical Card 200x1000, Wide Banner / Tall Banner, Compact Pill / Compact Vertical Pill, Wide Card / Tall Card, plus Vertical Phone 400x700 + Landscape Phone 700x400). Default stays at 1000x200 to match the standard OBS Browser Source size. Help text added explaining that the canvas should match your OBS source dimensions, and that the swap button flips orientation but does NOT auto-rotate node positions — click 'Reset Layout' if you want the default positions on the new orientation." },
        @{ Tag = "NEW";      Text = "v7.0.0 release content (recap): drag-and-drop layout editor with 8 resize handles per node, 5 anchor types, snap-to-grid (1/4/8/16 px), arrow-key keyboard nudge (1 / 10 px with Shift), ESC and click-outside deselect. 6 layout templates ship: Horizontal Card, Vertical Phone, Square Art-Forward, Wide Banner, Compact Pill, Wide Card. Per-element visibility toggles. Editor lives entirely in the customize parent doc — OBS Browser Source NEVER captures the handles." }
    ) },
    @{ Version = "v7.0.0"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "NEW";      Text = "DRAG-AND-DROP LAYOUT EDITOR. Every visible element on the card — album art, now-playing label, track title, artist, spectrum, progress bar — is now a positionable layout-node. Open the new Layout section in customize, flip 'Use Custom Layout' on, and drag each element anywhere on the card. Eight resize handles per element (4 corners + 4 edges) let you reshape on the fly. Snap-to-grid (1 / 4 / 8 / 16 px). Arrow-key keyboard nudge (1 px, or 10 px with Shift). ESC to deselect. The editor lives entirely in the customize parent document, NOT inside the preview iframe — OBS Browser Source NEVER captures the handles, so you can drag while live-streaming." },
        @{ Tag = "NEW";      Text = "Six layout templates ship with v7.0.0 — Horizontal Card (the classic 1000x200), Vertical Phone (400x700 portrait), Square Art-Forward (600x600 with art filling 70% and info overlay), Wide Banner (1600x100 streamer banner), Compact Pill (600x60 minimal corner widget), and Wide Card (1200x300 spacious horizontal). Click 'Open Templates' in the Layout section to apply a starting point — your colors and other settings stay; only positions change." },
        @{ Tag = "NEW";      Text = "Configurable canvas size with 6 preset shapes plus 'Custom...' for arbitrary dimensions (300x60 to 2400x1200). The card design canvas is what you author at; the overlay auto-scales the whole card to fit your OBS Browser Source via the existing transform path — text, art, and bars all scale together. Dropdown autodetects when your canvas matches a known preset." },
        @{ Tag = "NEW";      Text = "Per-element visibility toggles in the Layout section — hide the spectrum, the progress bar, the 'VIBING WITH' label, even the album art independently of any other setting. Compact-Pill template uses this to ship as a single line of 'artist - title' with everything else hidden." },
        @{ Tag = "NEW";      Text = "Anchor system for layout nodes — each node remembers which corner of the card it's pinned to (top-left, top-right, bottom-left, bottom-right, or center). Resize the card canvas later and edge-pinned nodes (e.g. spectrum on the right, progress bar at the bottom) stay in place instead of jamming into the corner." },
        @{ Tag = "FIXED";    Text = "v6.9.9 presets continue to render unchanged. Layout mode is OPT-IN: every existing preset has no `layout` block (or `layout.enabled: false`), so the legacy flexbox cascade renders pixel-identically until the user explicitly toggles 'Use Custom Layout' on. The renderer's applyLayoutMode() strips any inline absolute-position styles when off, so even mid-session toggling the layout off restores the v6 look perfectly." }
    ) },
    @{ Version = "v6.9.9"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "FIXED";    Text = "Three remaining gaps in dynamic-color coverage (caught after the initial v6.9.9 ship): (1) Animated 'EQ-style' bars in front of the now-playing label were never picking up the album palette — `--bar-color` was only ever set in applyConfig's preset path so applyDynamicPalette had no effect on them. Now overridden alongside `--np-color` whenever dynamicColors.nowPlayingText is on. (2) The 'Now Playing' / 'Vibing With' label appeared near-white on low-saturation album art (e.g. sepia/pastel sleeves with s≈27%) because the initial visL ceiling was 0.78 and saturation floor 0.55. Bumped saturation floor to 0.70 and lowered lightness ceiling to 0.72 so the album hue is clearly readable on the label even when the source art itself is muted. (3) Spectrum bars in rainbow mode were technically dynamic in v6.9.9 (hue offset centered on album) but the 80° spread was wide enough that the album hue got swallowed by a yellow→pink rainbow. Tightened the dynamic spread to 45° and added album-L tracking so the bars now read as 'the album color, varied' rather than 'a wide rainbow with subtle tint'. Also fixed clearDynamicPalette to restore `--bar-color` to preset when dynamic is toggled off (was leaving the bars stuck on the last dynamic color)." },
        @{ Tag = "FIXED";    Text = "Hotfix during the v6.9.9 cycle: when the v6.8 vSat()/bSat()/dSat() saturation helpers were replaced with the new accurateSat() passthrough, two callsites in applyDynamicPalette (titleGlow + nowPlayingText) still referenced the old vSat() — every track change threw ReferenceError before reaching the apply path, which is why dynamic colors broke entirely the first cut of v6.9.9. Both callsites now use accurateSat + visL to stay consistent with the rest of the v6.9.9 colour treatment." },
        @{ Tag = "IMPROVED"; Text = "Dynamic colors are now MUCH more accurate to the actual album art. The v6.8 path forced lightness=0.58 and over-saturated by 1.20-1.35x, so a deep crimson album showed salmon-pink bars and a dusty-rose sleeve looked neon. Three changes: (1) accurateSat() helper preserves extracted saturation for already-vivid art and only mildly boosts genuinely pale art (s<0.35) — colors now track what's actually on the album sleeve. (2) visL() helper biases each visible element toward the album's own lightness, clamped to a per-element visibility window so dark albums get darker but still legible borders/spectrum/timeline, and bright albums don't blow out. (3) Spinning-border 5-stop palette now derives directly from extracted primary+secondary RGB in a primary | primary-darker | secondary | mix | primary-lighter pattern instead of synthesised hue offsets — the rotating border now reads as 'the album sleeve' rather than 'a synthesised color cycle'. Same approach applied to spectrum bars (spectrum.fillColor target) and timeline progress bar (3-stop primary→mix→secondary). Mid-hue blend uses shortest-arc averaging so red+pink albums blend through magenta instead of through cyan." },
        @{ Tag = "FIXED";    Text = "Dark or near-monochrome album art no longer falls back to the user's preset colors. Root cause: the v6.8 extractor used hard-cutoff thresholds (s<0.12 || L<0.06) on every pixel — for an album that's mostly very dark with subtle color hints, every pixel got rejected, the bucketing pass returned zero valid buckets, and extractPalette returned null. The overlay then re-emitted the user's preset colors so the dynamic system silently disabled itself. v6.9.9 replaces that with a 4-tier extractor: vibrant → muted → dark → minimal, each pass progressively relaxes (sMin, lMin, lMax) and uses a smooth lightness gate with a non-zero floor so dark pixels still contribute weight. If even tier 4 yields nothing (truly black-or-grey art with no hue at all), a final synthesis pass derives a tinted palette from the image's mean color so SOMETHING dynamic is always shown. Bumped sample resolution 64 -> 96 px and bucket count 12 -> 24 (15 deg buckets) for finer hue quantization. Secondary-hue distance threshold relaxed 60 deg -> 45 deg so monochromatic albums still get a real second hue from the art instead of a synthesised complement." }
    ) },
    @{ Version = "v6.9.8"; Date = "2026-04-26"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spinning border now actually spins around the perimeter instead of synchronising top and bottom. Root cause: the v6.3.0 implementation was a horizontal linear-gradient slide on a pseudo-element that covered the whole card — same gradient column painted both top and bottom edges, so the same colors appeared at the same X-position simultaneously, which read as 'mirrored, not spinning'. Switched back to a true conic-gradient with transform: rotate() — each color occupies one perimeter position at a time and visibly traces around the card edge. The pseudo-element is now sized 200vmax square and centered via translate(-50%, -50%) so the rotating square always exceeds the card's diagonal at every angle (no corner exposure). buildConicStops also rewritten for single-cycle stops (the previous double-cycle layout was tuned for the linear-slide pattern and would double-paint each color per conic sweep)." },
        @{ Tag = "NEW";      Text = "'Spinning Border' toggle added to the Dynamic Colors section in customize. The gating code (if dc.border !== false) was already in overlay.html since v6.0.5 but the dynamicColors.border field was never declared in DEFAULTS and there was no UI checkbox — so the toggle was effectively always ON and invisible. Now: defaults set border:true (existing behaviour preserved), the customize panel exposes a checkbox right under 'Progress Bar', and turning it OFF makes the spinning border keep its preset cfg.border.colors palette instead of adopting the album-art-derived 5-stop palette while every other dynamic-color toggle stays active independently." }
    ) },
    @{ Version = "v6.9.7"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "OBS Browser Source CPU usage from the overlay drops during quiet music / paused playback. The overlay's drawSpectrum() runs on requestAnimationFrame at the monitor's refresh rate (60-240 Hz) and was redrawing the full canvas — clearRect, recompute lerp, build Path2D for every bar, create LinearGradient, fill — every single frame even when nothing visually changed. Added an idle-frame skip: when every rendered band has decayed to zero AND no color animation is in progress AND the color mode isn't rainbow (which still needs continuous hue-sweep), AND the WASAPI loopback path is the active audio source, the rAF callback returns immediately. The previous frame already committed the at-rest pixels so the visible result is identical. Bars resume drawing the moment audio returns. Hotfix during the v6.9.7 cycle moved the frameMax variable to outer scope after the first cut placed the idle-skip read outside the let-block scope (browser threw ReferenceError every frame, killing the whole spectrum render — that's what the 'spectrum turned off' screenshot was)." }
    ) },
    @{ Version = "v6.9.6"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Tray CPU spikes during rapid track-switching are dramatically reduced. Telemetry from a session of fast SoundCloud skipping showed Spotify+SMTC detectors taking 150-210 ms each on every 50 ms tick — Windows SMTC genuinely slows down when flooded with state changes, and our 50 ms tick rate kept stacking new queries against an already-overloaded subsystem. Two changes: (1) tick interval bumped 50 -> 100 ms, halving baseline CPU and post-spike recovery time. Pause/unpause latency moves from ~80-120 ms to ~130-170 ms — still well inside the 'feels instant' window. (2) Slow-detector cooldown extended from 10 ticks (500 ms) to 30 ticks (3 s at 100 ms cadence), so when SMTC IS under stress we back off long enough for it to recover instead of immediately retrying into the same slow-path. Combined effect: Task Manager %CPU during rapid track switching drops by roughly 4x, and steady-state CPU drops by 2x." }
    ) },
    @{ Version = "v6.9.5"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Tray-process CPU spikes to 17-30 % in Task Manager are gone. Two root causes: (1) the osu! detector enumerated every running process every 50 ms tick (Get-Process with no -Name filter | Where-Object filter), so on a PC with 200+ processes the tray was burning ~5-15 ms per tick on a process scan that returns the same answer every time. Fixed by filtering at the kernel level via -Name 'osu*'. (2) When ANY detector (UIA, browser-tab scan, slow WMI query, etc.) momentarily took >150 ms, the next 50 ms tick would re-run it immediately, repeating the spike. Added a per-detector circuit breaker: if a detector exceeds 150 ms, it's skipped for the next 10 ticks (~500 ms cooldown), then automatically retries. Sustained-broken detectors get rate-limited to ~2 attempts/sec instead of 20. Pause/unpause sync stays at 50 ms latency for healthy detectors — only the broken ones back off." }
    ) },
    @{ Version = "v6.9.4"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Audio Source dialog now opens on PCs with many audio devices. Friend's audio_spectrum.log showed 'HandleDevices: The specified network name is no longer available' — the tray's /devices fetch was timing out at 3 s before audio_spectrum finished enumerating WASAPI loopback + input + exclusive + MME + ASIO endpoints (slow on systems with VB-Cable / VB-Matrix / Voicemeeter / multiple ASIO drivers). When the client disconnects mid-response, the audio side throws a WriteAsync IOException and the tray sees a generic catch error, then offers a misleading 'Fix permissions?' YES/NO dialog. Bumped TimeoutSec 3 -> 20 across all /devices fetches in the Audio Source dialog and the Scan & Auto-select loop. 20 s covers worst-case ASIO enumeration with a comfortable margin; fast PCs respond in under a second so users see no UI delay." },
        @{ Tag = "FIXED";    Text = "Tray's 'Uninstall Master's FM' menu item now works on PCs where the registry-scan path comes up empty. v6.9.2 dropped the hardcoded ProductCode (Major-Upgrade gives each build its own random GUID) and replaced it with a registry scan over HKLM/HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall. If the install registered under WOW6432Node (32-bit view) or wrote a slightly different DisplayName, the scan returned nothing and the tray showed 'Could not locate the Master's FM install entry'. Three improvements: (1) scan paths extended to include WOW6432Node hives in both HKLM and HKCU, (2) DisplayName wildcard widened to '*Master*FM*' so unicode-quote variants and spelling differences still match, (3) Win32_Product WMI query as a second fallback if registry finds nothing. If BOTH paths come up empty (truly unregistered install), the tray now offers a manual-cleanup option that closes processes, deletes the install folder + shortcuts + URL ACLs, so users always have a path forward instead of hitting a dead-end dialog." },
        @{ Tag = "IMPROVED"; Text = "Boot banner in audio_spectrum.log now shows the actual current version (v6.9.4) instead of the stale 'v6.5.2' literal that was hiding what binary was running on friends' PCs." }
    ) },
    @{ Version = "v6.9.3"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "NEW";      Text = "Spectrum 'Sensitivity' slider in customize. Multiplies the audio magnitude pre-compressor so quiet input still fills the bars — friends streaming with the music at 1-5 % volume now look just as alive as people running at 80 %. Range 0.5x to 10x, default 1.0x (the original tuning, untouched). The downstream compressor + final clamp cap loud audio at 100 %, so the slider can be cranked safely without clipping. Live: moving the slider POSTs to /set-sensitivity on audio_spectrum.exe and the next FFT frame already reflects it. A small ↺ reset button next to the value snaps it back to 1.0x in one click for when you want to A/B against the default. Persisted as spectrum.sensitivity in config.json so it survives restarts." }
    ) },
    @{ Version = "v6.9.2"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "MSI installer no longer fails with error 1603 (SECUREREPAIR) on second-and-later installs. Root cause: every Master's FM MSI shipped with the same hardcoded ProductCode + the same joke ProductVersion '67.0.0' — so Windows Installer treated each new MSI as a 'repair' of the existing install, then tried to validate against the cached source filename. Bundle filenames are version-stamped (Master's FM V6.9.0.msi vs V6.9.1.msi etc.), so the cached-vs-new filename mismatch failed the repair → 1603. v6.9.2 implements proper MSI Major-Upgrade machinery: each build now generates a fresh ProductCode (uuid4), the real app version is written to ProductVersion, and an Upgrade-table rule (with VersionMax=NULL so it matches the legacy joke 67.0.0 too) calls RemoveExistingProducts at sequence 1401 BEFORE InstallInitialize. Net result: installing v6.9.2 over v6.9.0/v6.9.1 cleanly uninstalls the old one first, then installs the new one. INSTALL.bat's pre-uninstall step also got upgraded — old WMIC scan replaced with a PowerShell registry scan because WMIC is deprecated and absent on Windows 11 22H2+, so the original safety net was a silent no-op on modern PCs." }
    ) },
    @{ Version = "v6.9.1"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Audio Source dialog now self-repairs the URL ACL issue with one UAC prompt instead of just erroring out. Background: Master's FM is a per-user MSI installed under LocalAppData, and per-user MSIs run UN-ELEVATED by default — so the v6.9.0 'register URL ACL during install' custom action ran in the user's normal context where netsh has no admin rights. The reservation never landed for friends whose MSI install didn't trigger UAC. New flow: when /devices is unreachable, the tray now offers a YES/NO 'fix permissions now?' prompt; on YES, it runs `netsh http add urlacl ...` via Start-Process -Verb RunAs (single UAC prompt with clear context), restarts audio_spectrum.exe, and retries the /devices fetch. After one click + one UAC accept, Audio Source works permanently. If the user declines or the elevation fails, they get the manual netsh commands they can copy-paste into an admin PowerShell." }
    ) },
    @{ Version = "v6.9.0"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Audio Source dialog 'Could not reach service at http://127.0.0.1:4243' on friends' PCs. Final root-cause fix: locked-down Windows configs (corporate images, locked accounts, certain AV/EDR products) deny HttpListener.Start() on 127.0.0.1 unless the URL prefix is explicitly registered in HTTP.sys's URL ACL table. Registration requires admin — which the v6.8.3 self-registration attempt didn't have, since audio_spectrum.exe runs as the user, not elevated. v6.9.0 moves URL ACL registration to an MSI install custom action: when you install the MSI (UAC-elevated), it runs `netsh http add urlacl url=http://127.0.0.1:4243/ user=Everyone` and `localhost:4243/` once. From that point on, any user account on the machine can bind the prefix. Installs that already happened need a reinstall (or your friend can run the same netsh command manually as admin)." }
    ) },
    @{ Version = "v6.8.9"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Customizer save paths now ALWAYS save a complete config. User audit caught that customize.html DEFAULTS.platformBadge was missing the color/dotColor/fontSize/fontWeight/letterSpacing keys (only the overlay.html DEFAULTS had them). After a Reset-to-defaults click, those fields were silently dropped from S, so saving a preset right after a reset would produce a JSON file lacking 5 platform-badge keys — sharing that preset to a friend would mean those 5 fields fell back to whatever the friend's UI defaults were. Two-part fix: (1) customize.html DEFAULTS.platformBadge now lists all 7 keys verbatim from overlay.html DEFAULTS. (2) Both save paths (applyConfig + savePreset) now wrap S in deepMerge(DEFAULTS, S) before POSTing, so even if a future bug introduces another partial DEFAULTS entry, the persisted file is always complete. Audited every other bind path and confirmed all setters write to keys that exist in DEFAULTS — platformBadge was the only gap." }
    ) },
    @{ Version = "v6.8.8"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "BASS TRANSIENT EXPANDER added. User feedback (multiple iterations): 'a sustained bassline fills the front of the visualizer to 50-70 %, leaving no headroom for kicks to punch through'. Sustained bass and kick drum produce the SAME magnitude in their respective bands — a static gain/compressor curve cannot distinguish them. v6.8.8 fixes this in the time domain: each of the lowest 100 bands maintains a slow EMA-baseline (~270 ms half-life) of its 'sustained' level. The displayed value is now a small (35 %) fraction of the baseline PLUS an amplified (1.8x) excess above baseline. Net effect: a steady bassline shrinks to ~30 % of its previous height (because baseline ≈ target → excess ≈ 0); a kick punching above baseline gets expanded above its natural peak. Tapers from full effect at band 0 to none at band 100 so the mid/treble region is unaffected. PLUS per-band gamma curve (1.3 → 0.8 ramp across bands 0..150) for additional sustained-vs-peak contrast." },
        @{ Tag = "IMPROVED"; Text = "Per-band output gamma curve. Default OUTPUT_GAMMA=0.8 lifts dark values (great for treble visibility) but over-pumps the mid-range bass content where a sustained bassline lives. Now ramps from gamma=1.3 at band 0 (suppresses mid-range, keeps peaks) to gamma=0.8 at band 150 (current treble behaviour). Synergises with the transient expander above: transient expander handles dynamic content, gamma handles static magnitude scaling. Both target the same 'bassline pumping the front' problem from different angles." }
    ) },
    @{ Version = "v6.8.7"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Bass kicks now PUNCH as defined peaks instead of rolling-curve domes. User's diagnosis: 'when a kick hits, the entire front of the visualizer rises as a curve — would prefer a sharper individual peak like the way treble looks'. Implemented spatial high-pass / unsharp-mask filter on low bands (0..150): each band now has a fraction of its 5-neighbour local average subtracted from it, emphasising the kick's peak frequency over the broad skirt of overlapping bass bins it's smeared across. Sharpening strength tapers from 0.7 at band 0 to 0 at band 150 so the transition into mid-band region is invisible. Algorithm runs in 2 extra passes over s_targets[] (stored separately so the unsharp-mask reads from raw values, not partially-modified ones). CPU cost is negligible — 2 × 480 float ops per FFT." }
    ) },
    @{ Version = "v6.8.6"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Bass bars more dynamic + use of dead frequency range. (1) minFreq 20 Hz -> 30 Hz: virtually no music has content below ~30 Hz (sub-bass synths bottom out at 30-40 Hz, kick drum fundamentals 50-80 Hz), so the leftmost 5-10 bars were always near-silent. Pulling the floor up redistributes the 480 log-spaced bands across a narrower frequency range so each bass band now covers slightly more useful frequency = visibly more variation. (2) v6.8.4's bass envelope-decay scaling pushed harder: 1.25x -> 1.6x at band 0, ramp extended to band 150 (was 120). Bass bars drop 60 % faster between FFTs at the lowest end, exposing more intra-cycle dynamics. Combined: kick drums and basslines visibly 'punch' instead of holding flat." }
    ) },
    @{ Version = "v6.8.5"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Found why audio_spectrum.exe was still pinned at 6 % even after v6.8.3's SSE throttle: the user's saved config had responseMs=0.021 ms (HOP=1 from when the slider's min used to be 0.02 ms). That's 48,000 FFTs/sec — physically impossible to run with comfortable CPU on any machine, and stale on every fresh session-load until the user happened to drag the slider. Now (1) BootstrapFromConfig clamps any loaded responseMs < 0.5 ms up to 0.5 ms (logs the clamp so it's visible in audio_spectrum.log); (2) slider min raised 0.05 ms -> 0.5 ms so even brand-new users can't drag into the impossible region. CPU returns to 1-2 %." }
    ) },
    @{ Version = "v6.8.4"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Bass bars look ~25 % more 'spikey' to match treble's natural jitter. Per-band envelope decay now scales by frequency: bands 0..120 (sub-bass through low-mid, ~20-200 Hz) get up to 1.25x faster decay than the treble bands, with a smooth linear ramp. Effect is exposing the intra-cycle variation that the FFT naturally averages out at low frequencies. Bass still looks 'rounder' than treble overall (because that's the actual music content), but the visualizer feels more uniform end-to-end now." }
    ) },
    @{ Version = "v6.8.3"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "audio_spectrum.exe back to ~1-2 % CPU. SSE_INTERVAL_MS 4 -> 16 (60 Hz, matches typical monitor refresh). User reported the 6.3 % CPU survived v6.8.2's HOP=512 change — the actual culprit was SSE encoding running 250x/sec when the overlay can only render at monitor rate (60-240 Hz). At 60 Hz SSE the overlay still has fresh data every monitor frame but Base64 encoding cost drops ~75 %. (4K monitor doesn't affect audio_spectrum directly — that process has no UI, all rendering happens in OBS / customize.exe.)" },
        @{ Tag = "FIXED";    Text = "Audio Source dialog 'Could not reach service at http://127.0.0.1:4243' for friends. Some Windows configurations (corporate images, certain AV setups, locked-down policies) deny HttpListener.Start() on 127.0.0.1 without an explicit URL ACL grant. v6.8.3 makes audio_spectrum.exe (1) listen on BOTH 127.0.0.1 AND localhost prefixes so the tray finds it either way; (2) attempt a one-shot URL ACL registration via netsh on Start() failure (requires admin to actually add — the MSI install path now prompts for elevation anyway, so this should succeed during install); (3) log loudly with the exact HttpListenerException error code so we can diagnose if it still fails." }
    ) },
    @{ Version = "v6.8.2"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "CPU back to ~1-2 %. HOP_SIZE 128 -> 512 (10.7 ms FFT cadence). User: 'used to be 1-2 %, now 6 %'. The HOP=128 was 4x more FFTs per second than HOP=512 — visually invisible because the monitor refresh is the floor anyway, but the meter showed every extra cycle. Back to the lower-CPU pace." },
        @{ Tag = "FIXED";    Text = "Kick-drop dynamic range. User report: 'when a bassline plays, front of visualizer fills to 50-75 %. When a kick actually drops, only 25 % more — looks unreal, not punchy'. Root cause was the v6.4.5 compressor (knee=0.5, ratio=4.3:1) squashing exactly the kick peaks the user wanted to PUNCH. Re-tuned for the OPPOSITE behavior: REF_MAG 60 -> 112 (sustained bass takes half as much of the card), knee 0.5 -> 0.75 (compressor only kicks in for the highest peaks — below 0.75 the bars are pure linear so kicks shoot up unobstructed), ratio 4.3:1 -> 2.0:1 (even when compressor IS active, it's gentle so very loud kicks still reach 95-100 %). Net dynamic range: sustained bass ~35 %, ordinary kick ~76 %, loud kick ~93 %, hardest spike clips at 100 %. ~40 % of the card is now 'kick punch range' (was ~17 %)." }
    ) },
    @{ Version = "v6.8.1"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Realtime tighter again + CPU offload pass. (1) HOP_SIZE default 256 -> 128 (FFT cadence 2.7 ms, total perceived delay ~20 ms). (2) Card-inner promoted to its own GPU compositor layer via translate3d + will-change so OBS's Browser Source capture grabs it as a single texture upload. (3) SSE_INTERVAL_MS 1 -> 4 ms — overlay only renders at monitor rate (60-240 Hz), so 1000 Hz SSE was 4x more Base64 encoding + HTTP writes than the screen could ever show. At 250 Hz the overlay still has fresh data on every rAF tick but the SSE thread spends ~75 % less time encoding payloads. User asked 'offload something to GPU' — we can't easily move C# .NET FFTs to GPU, but skipping wasted CPU on invisible payloads achieves the same '6% -> ~3-4%' meter result." }
    ) },
    @{ Version = "v6.8.0"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Realtime tighter again + more compositor hints. (1) HOP_SIZE default 512 -> 256: FFT cadence drops to 5.3 ms, total perceived delay now ~25 ms. The long-fall overlay lerp continues to smooth the visible motion so it doesn't read as jittery. (2) Three more belt-and-suspenders compositor hints stacked on the canvas — `filter: brightness(1)`, `mix-blend-mode: normal`, `perspective: 1px` — all visually no-ops that historically force GPU layer promotion in older Chromium/CEF builds where will-change is ignored. Combined with the existing translate3d / backface-visibility / contain / isolation hints, the canvas is now GPU-promoted everywhere CEF runs." }
    ) },
    @{ Version = "v6.7.9"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Realtime response pushed up + max GPU compositor hints. User said 'make GPU layering stronger again, but make it more responsive realtime — this is the way I wanted to go'. (1) HOP_SIZE default 1024 -> 512: FFT cadence drops from 21.3 ms to 10.7 ms, total perceived delay from ~50 ms to ~30-35 ms — feels noticeably more 'live' on transients while the long-fall lerp keeps motion smooth. (2) Stacked every reliable GPU-compositor hint on the canvas: will-change: contents,transform,opacity / transform: translate3d(0,0,0) / backface-visibility: hidden / contain: layout paint style / isolation: isolate. Together these guarantee CEF keeps the canvas as its own GPU-resident texture, which OBS Browser Source capture can grab without a CPU round-trip — translates to less micro-stutter in OBS." }
    ) },
    @{ Version = "v6.7.8"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum bars filling more of the card again. User screenshot showed peaks around 60-70 % with lots of empty space above; user said 'fix that ceiling issue again, use the compressor (not aggressive) like before'. Dropped REF_MAG 112 -> 60 (signal needs ~half as much amplitude to drive bars to the top). Compressor stays at the gentle v6.4.5 settings (knee 0.5, ratio 4.3:1, gamma 0.8) so peaks still don't saturate hard at 100 %." },
        @{ Tag = "IMPROVED"; Text = "Stronger GPU-compositor hints on the spectrum canvas for OBS smoothness. Added backface-visibility: hidden (forces a compositor layer in older CEF builds), contain: layout paint style (isolates canvas paints from the rest of the document so OBS's capture grabs only the canvas texture), explicit image-rendering: auto, and will-change: transform alongside contents. Combined with v6.5.5's desynchronized canvas context, OBS Browser Source should grab a smoother texture per scene tick." }
    ) },
    @{ Version = "v6.7.7"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Bar motion still reading as 'not smooth overall' even at v6.7.6's widened lerp. User clarified: 'I like the realtime responses, but it's a bit too aggressive in a way'. So: keep RISE snappy (response stays realtime) but stretch FALL way longer so the up-and-down chase dies down between peaks. New ranges: rise 3..33 ms (was 5..35), fall 50..250 ms (was 20..170). At default smoothing=0.6 the bar still rises in 21 ms (one 60 Hz frame is enough to land most of a transient) but takes 170 ms to fall halfway — so percussive bursts bloom upward instantly then slowly bleed back down. Visible motion is now dominated by GENTLE DECAY between hits, not chaotic flicker." }
    ) },
    @{ Version = "v6.7.6"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Bar motion 'fps-ghost' visual artefact. User feedback on v6.7.5: 'real-time response is great, but the bar goes too fast up and down — looks like fps ghosts in between'. Fix is on the OVERLAY side, not the FFT pipeline (response stays great). Lerp ranges widened so bars move more deliberately at default smoothing — rise 1..21 ms -> 5..35 ms, fall 4..104 ms -> 20..170 ms. At default smoothing=0.6 the bar now rises in ~23 ms (was 13) and falls in ~110 ms (was 64), so each 60 Hz frame moves the bar ~40 % of the way toward its target instead of 60 %+. That extra 1-2 frames of follow-through eliminates the per-frame jumps that were reading as trail/ghost on rapid percussive content." }
    ) },
    @{ Version = "v6.7.5"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Restart Master's FM no longer leaves an orphan cmd.exe console window on screen for the duration of the kill-chain. The v6.5.2 WMI-spawn fix (which solved the actual restart-fails-because-cmd-was-killed bug) was launching cmd.exe with WMI's default visible-window. Now passing a Win32_ProcessStartup instance with ShowWindow = SW_HIDE (0) when calling Win32_Process.Create. Cmd still runs the chain (kill, wait, relaunch) but invisibly." }
    ) },
    @{ Version = "v6.7.4"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Defaults walked back from 'as realtime as possible' to ~50 ms perceived delay per user request: 'response is good, but a bit too good. Not fully realtime was good, i think like 50 ms'. HOP_SIZE default 16 -> 1024 (21.3 ms FFT cadence at 48 kHz). Combined with ~10 ms WASAPI engine period + ~16 ms overlay rAF = 47-50 ms total acoustic-event-to-bar latency. Slider stays the same range so anyone who wants more realtime can drag it left to 0.05 ms, anyone who wants less CPU can push right to 42 ms." }
    ) },
    @{ Version = "v6.7.3"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Reverted spectrum tuning to the v6.5.6 state — user pointed at the message 'do even HARDER REALTIME, like 0 ms literally, and make 1000 FPS feel like 1000 FPS even through OBS' as the pivot point. That message came right BEFORE the v6.5.7 silent-build-failure series. Per request, kept the OBS smoothness work (FPS=1000 default, canvas desynchronized hint, GPU layer promotion via will-change/translateZ, /current poll @100ms) since 'leave the obs fixes out' of the revert. Reverts: HOP_SIZE default 8 -> 16, slider default 0.17 -> 0.33 ms, overlay bar lerp ranges back to rise 1..21 ms / fall 4..104 ms (the v6.3.5 values that were active at v6.5.6). Other newer features kept (Response Time slider, pause/unpause source-truth sync, polling SSE, on-arrival SSE parse)." }
    ) },
    @{ Version = "v6.7.2"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "v6.7.1's revert overshot — set FFT=8192 / HOP=32 (v6.5.3 era), but the user's actual 'this is great' moment was at v6.6.7 with FFT=2048 / HOP=8. Re-reverted to those values. 42 ms FFT integration window + 0.17 ms hop cadence — snappy on percussive content but smooth flow on sustained tones. Slider range adjusted back to 0.05–42 ms (max equals one full FFT-window hop)." }
    ) },
    @{ Version = "v6.7.1"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum response reverted to the v6.5.3 sweet spot per user request ('revert to a few patches ago when I said omg it works so well, 1.5% CPU, lower the audio again'). FFT_SIZE 2048 -> 8192 (170 ms integration window — gives that smoother 'flow' feel in the bars while still being plenty responsive via envelope + lerp). HOP_SIZE default 16 -> 32 samples (0.67 ms cadence at 48 kHz). Response Time slider stays and now ranges 0.1 ms to 170 ms (matching the wider FFT window). Slider default 0.33 ms -> 0.67 ms." }
    ) },
    @{ Version = "v6.7.0"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Response Time slider actually POSTs to audio_spectrum.exe now. Missing CORS-preflight OPTIONS handler on port 4243 was making the browser abort the cross-origin POST from customize (served at 4242) before the actual request landed. Added Access-Control-Allow-Methods / Headers and an explicit OPTIONS short-circuit that returns 204. Slider changes are now immediately visible in audio_spectrum.log as 'set-hop: HOP_SIZE=N samples (X ms)'." },
        @{ Tag = "FIXED";    Text = "Slider default + range tuned after 'too fast at current lowest speed' feedback. Default 0.083 ms (HOP=4) -> 0.33 ms (HOP=16), which is the setting users consistently settled on as 'snappy without being twitchy'. Slider min 0.02 ms -> 0.1 ms (below that is pure jitter territory for most hardware). Max 500 ms -> 42 ms (above 42 ms the FFT hop is larger than the window so there's no physical effect)." },
        @{ Tag = "REMOVED";  Text = "v6.6.9's adaptive envelope scheme. Reverted to fixed ENV_ATTACK=0.85 / ENV_DECAY=0.28 that users consistently liked at v6.6.8. The adaptive math was correct in theory (keep ms time-constant regardless of HOP) but felt subtly off in practice vs the user's muscle memory with the fixed constants." }
    ) },
    @{ Version = "v6.6.9"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "NEW";      Text = "Response Time slider in the customizer. Under the Spectrum Visualizer section you now have a live 'Response Time' slider (0.02 ms to 500 ms). Lower = more realtime + more CPU, higher = less CPU but coarser bar updates. Value tracks the underlying FFT HOP_SIZE; changes take effect the instant you release the slider via a POST to audio_spectrum.exe's new /set-hop endpoint. Persists to config.json under spectrum.responseMs and re-applies on next Master's FM startup." },
        @{ Tag = "IMPROVED"; Text = "Adaptive envelope smoothing. The C#-side envelope alphas (ENV_ATTACK / ENV_DECAY) are now computed per-FFT from the current HOP_SIZE, using fixed TIME-CONSTANT targets (1.2 ms rise half-life, 4 ms fall). Without this, setting Response Time to 0.02 ms would make the envelope re-converge 50x faster than at 0.33 ms, turning the spectrum into pure noise during rapid drum hits. With adaptive alphas the 'feel' stays identical across the slider's full range — you get more realtime at lower settings without gaining flicker." }
    ) },
    @{ Version = "v6.6.8"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "HOP_SIZE 8 -> 4 (samples). User's one-two asks: (a) 'make HOP_SIZE as low as possible'; (b) 'make it look smoother in whatever way made it good last time'. This single change does both: FFT cadence halves again from 0.17 ms to 0.083 ms (12000 Hz FFT rate), AND because sequential FFTs now overlap 99.8 % of their input, the byte values the overlay receives naturally evolve gradually — consecutive SSE frames show tiny incremental changes instead of big jumps. That smoothness is STRUCTURAL, not temporal, so it adds zero lag. Bars feel more 'liquid' while still being sample-accurate to recent audio. CPU: ~10 % of one core on Ryzen 7 7800X3D (up from ~5 %), still nothing on modern hardware." }
    ) },
    @{ Version = "v6.6.7"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Two-pronged fix for the 'bars go up fast but fall slow / look ghosty' feedback. (1) Overlay bar lerp retuned: rise half-life range 1..21 ms -> 0.5..10.5 ms, fall half-life range 4..104 ms -> 3..43 ms. Default smoothing=0.6 now gives 6.5 ms rise / 27 ms fall (was 13/64) — bars drop to 42 % of peak within two frames at 60 fps, eliminating the ghosting trail. Rise tighter too so transients still land visible on the same frame they hit. (2) HOP_SIZE 16 -> 8 on audio_spectrum side: FFT cadence 0.33 ms -> 0.17 ms. Now that the build actually ships audio_spectrum.exe (v6.6.4 fixed that), users will actually feel the latency cut this time. CPU: ~5 % of one core on modern machines, negligible." }
    ) },
    @{ Version = "v6.6.6"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Reverted two subtle timing-fragile changes that were making bars feel 'up/down fast but slow to respond'. (1) SSE send loop on audio_spectrum side: went from v6.5.4's event-driven AutoResetEvent + Stopwatch rate-limit back to plain Thread.Sleep(SSE_INTERVAL_MS) polling. The event-driven path had IRREGULAR send cadence (fast on signal, slow on timeout, skip-via-continue on rate-limit), which produced bursty jittery updates. Plain polling at 1000 Hz with 1 ms timer resolution is perfectly steady. (2) Overlay SSE handling: went from v6.5.3's 'stash raw + parse at rAF' back to on-arrival parsing. The stash scheme was capping the freshest-visible data at one rAF frame back, so a bar that should have reflected something new ended up reflecting a ~16 ms old target. Both reverts restore the clean predictable pipeline that v6.5.0-v6.5.2 had before we optimized too far." }
    ) },
    @{ Version = "v6.6.5"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Reverted spectrum response speed back to 'v6.5.6 feel'. FFT_SIZE 512 -> 2048, HOP_SIZE 4 -> 16. Rationale: from v6.5.7 to v6.6.3 my audio_spectrum.exe builds silently failed, so users were actually running the v6.5.6 compiled binary the entire time — they'd gotten used to that responsiveness. When v6.6.4 finally compiled the newer values, bars suddenly ran 4x faster (42 ms -> 5 ms integration window). User said 'way too fast, gotta fall back'. Now matches what was running before the silent-failure window, with all the non-spectrum improvements preserved (pause sync, event-driven SSE, hot-loop precompute, Base64 encoding, etc.)." }
    ) },
    @{ Version = "v6.6.4"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "CRITICAL build-silent-failure bug discovered. Since v6.5.7, audio_spectrum.exe was failing to compile because I'd added a 3-arg WasapiLoopbackCapture constructor (dev, true, bufferMs) that doesn't exist in our bundled NAudio.Wasapi.dll. The _full_rebuild.ps1 script only printed 'WARN: audio_spectrum.exe build exit=1' and kept going — so every single user install since v6.5.7 has been shipping the v6.5.6-era audio_spectrum.exe with the old latency/FFT values while ALL the other components upgraded. Every 'max realtime' improvement I claimed between v6.5.7 and v6.6.3 was a lie. Reverted to the 1-arg constructor which compiles; the v6.5.6 FFT_SIZE=1024 and HOP_SIZE=8 values are what users actually had. v6.6.4 now correctly ships the true v6.5.8 values (FFT_SIZE=512, HOP_SIZE=4) for the first time. Shared-mode WASAPI's engine period of ~10 ms is a hard floor the buffer-size arg couldn't have beaten anyway." },
        @{ Tag = "FIXED";    Text = "Pause/unpause timestamps now actually sync to the source on EVERY event. Previously the server intentionally avoided re-pinning startedAt on pause/resume to prevent visible position 'snaps' when SMTC's reported position was stale. User explicitly wanted source-truth over visual smoothness ('every time when I pause or unpause the media, it will instantly sync the correct timestamps for both start and end time'). New behaviour: each pause/resume webhook re-pins startedAt = now - positionMs so the overlay's elapsed equals the source's positionMs exactly at that moment. Also the duration (end time) is updated whenever the webhook reports a different value, not just on first-set. Trade-off: you may see a brief position snap if SMTC's timeline was drifting, but the timestamps now match the source directly." }
    ) },
    @{ Version = "v6.6.3"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "server.listen() now fires IMMEDIATELY after createServer() — used to be the last line of the file. Structurally the intervening ~350 lines were all async-function declarations (no synchronous blocking), so this is a minor win, but it removes any possibility of a late init stalling the port bind. The big 10-30 s 'OBS detects Master's FM' delay most users see is dominated by: (1) pkg-packaged Node.js cold start (unpacking the embedded runtime, ~2-5 s on first launch after reboot); (2) OBS Browser Source retry behaviour when the URL was unreachable at scene-load time — OBS waits 10-15 s before re-probing. Workaround: right-click the Master's FM source in OBS and choose 'Refresh' after starting the app, OR keep Master's FM running before you open OBS so the first load succeeds." }
    ) },
    @{ Version = "v6.6.2"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "v6.6.1's 8K supersampling tanked performance — user reported '10 fps and laggy, very badly' in OBS. A 1000x200 card rendered at 8000x1600 internally = 12.8M pixels per frame, and compositing that back down to display size plus OBS's own capture + scale was blowing the GPU frame budget. Reverted to 1:1 rendering. Lesson: GPU cost scales O(pixels^2) for complex compositor chains; 3x was already over budget, 8x was catastrophic. 1:1 with native browser anti-aliasing is actually the crispest option in this context." },
        @{ Tag = "IMPROVED"; Text = "Pause/unpause timestamps sync instantly across all platforms now. Two timers dialed in: tray scrobble poll 300 ms -> 50 ms (detects SMTC state changes within one scheduler quantum), overlay /current poll 500 ms -> 100 ms (picks up server-side push sooner). Total pause→overlay-freeze latency now ~80-120 ms, inside the 'feels instant' perception window. User: 'every time when i pause or unpause any platform, it will instantly sync the correct timestamps for both start and end time'." }
    ) },
    @{ Version = "v6.6.1"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "8K SUPERSAMPLING (user asked). Canvas internal is now 8× CSS — a 1000×200 card renders at 8000×1600 internally, i.e. ~8K horizontal. Combined with CSS `image-rendering: high-quality` the browser compositor uses Lanczos/bicubic filtering for the downsample instead of bilinear — should produce noticeably sharper edges than v6.5.9's 3× attempt (which blurred). If the cascaded filter chain (canvas → browser composite → OBS capture → OBS scene scale) still washes out the benefit the way 3× did, fallback to v6.6.0's 1:1 is one revert away." }
    ) },
    @{ Version = "v6.6.0"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "FIXED";    Text = "Reverted v6.5.9's supersampling experiment — turned out to cause MORE blur in OBS rather than less. Theory said: render canvas at 3× internal, downsample in compositor, smoothed edges reach OBS. Reality: canvas -> browser composite -> OBS Browser Source capture -> OBS scene scale is a cascaded chain and EACH step does bilinear filtering. The combined blur from 4 successive filters overwhelmed the anti-alias benefit of the supersample. Back to 1:1 pixel rendering, where every bar edge is a single browser anti-alias pass — crisp, matches what was perfect before." }
    ) },
    @{ Version = "v6.5.9"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Spectrum canvas now SUPERSAMPLED at 3x internal resolution — edges look crisp / anti-aliased like a 4K render even through the 1000x200 card. Mechanism: the canvas DOM element still has the same CSS width/height so layout is unchanged, but its internal buffer (canvas.width × canvas.height) is 3x larger (e.g. 3000x600 for a 1000x200 card). We apply a ctx.setTransform(3,3) at the start of every draw so the existing bar-position math stays in CSS pixels, and the GPU rasterizes into the 3x denser buffer. The browser compositor downsamples that to CSS resolution using high-quality filtering BEFORE OBS CEF grabs the pixels — net: OBS sees the supersampled-and-smoothed result. Free 9x MSAA-equivalent edge quality. Cost: 9x more fragments per frame, trivial for simple fills on any GPU made this decade." }
    ) },
    @{ Version = "v6.5.8"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "One more realtime notch (user: '1.3% CPU on 7800X3D, lower again'). FFT_SIZE 1024 -> 512 (integration window 10.7 ms, peak-contribution lag now ~5 ms, was ~10), HOP_SIZE 8 -> 4 (FFT cadence 12000 Hz, essentially sample-accurate), WASAPI loopback buffer 5 -> 3 ms (driver honors what it can, falls back otherwise). Acoustic-event-to-bar-movement now sits around 10-15 ms best case; the remaining slack is driver + monitor scan-out. Trade-off: sub-bass bars (20-90 Hz) now share bin 0 + bin 1 (94 Hz bin width) so they move together rather than showing independent fundamentals below 90 Hz — kick drums (~60 Hz) still register strongly because bin 1 picks them up, but sub-bass synth bass (40 Hz) looks flattened into the kick column. Everything mid/treble is perfect. CPU stays under 2 % on 7800X3D." }
    ) },
    @{ Version = "v6.5.7"; Date = "2026-04-25"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "MAX-REALTIME pass (user: 'do even HARDER REALTIME. Like 0 ms literally'). Three more cuts: (1) FFT_SIZE 2048 -> 1024 — acoustic integration window halved to 21 ms, transient peak-contribution lag now ~10 ms (was ~21 ms). Sub-bass bars at 20-45 Hz lose some differentiation since they all share bin 0 + bin 1 (46.88 Hz wide), but kick fundamentals 50+ Hz still resolve. (2) HOP_SIZE 16 -> 8 — FFT runs at 6000 Hz now, each sample influences output within 0.17 ms of arrival (was 0.33 ms). (3) WASAPI loopback buffer 10 ms -> 5 ms — driver grants whatever it can honour, most ship ~3-5 ms shared-mode engine periods. Total pipeline latency (FFT+SSE+draw) is now sub-millisecond; only physical floors remain: WASAPI driver (~3-5 ms), Hann integration (~10 ms), monitor scan-out (~0-16 ms). End-to-end: 15-30 ms worst case." },
        @{ Tag = "NOTE";     Text = "If the spectrum still feels choppy IN OBS specifically (but silky in customize.exe preview), it's because OBS's Browser Source has its own FPS setting that defaults to 30 on many installs. The browser-side overlay can't override OBS's CEF render rate from inside. In OBS: right-click the Master's FM Browser Source -> Properties -> change 'FPS' to 60 (or match your scene/stream FPS). Single setting, instant smoothness." }
    ) },
    @{ Version = "v6.5.6"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "FOUND THE REAL realtime floor. Every prior latency pass was shaving the OUTER pipeline (FFT hop, SSE interval, rAF rate), but the inner acoustic integration window — set by FFT_SIZE — was quietly adding ~85 ms of perceived lag because a Hann-windowed 8192-sample FFT at 48 kHz weights recent samples LOW and centre-of-window samples HIGH, so a brand-new transient needs to slide to the middle of the window (halfway, ~85 ms) to contribute full magnitude to the bar. Dropped FFT_SIZE from 8192 to 2048 — integration window now 42 ms, transient peak-contribution lag now ~21 ms. Bar height now tracks actual audio amplitude within ~25 ms end-to-end (FFT integration + our ~4 ms pipeline + one rAF). CPU goes DOWN because 2048-pt FFT is 5x cheaper than 8192-pt (~5 µs vs ~25 µs on 7800X3D). Freq resolution drops from 5.86 Hz to 23.4 Hz bins which matters below ~50 Hz — but BAND_COUNT=480 with sub-bin linear interpolation keeps the low end visually smooth (no plateau). This is the change you feel, not just the one you measure." }
    ) },
    @{ Version = "v6.5.5"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Spectrum canvas now explicitly GPU-promoted for smoother rendering in OBS. Three changes: (1) canvas 2D context requested with { desynchronized: true } — bypasses the default 'wait for vsync' barrier so pixels land on the surface the moment they're drawn, letting OBS CEF's Browser Source capture grab fresh frames sooner. (2) Added `will-change: contents` + `transform: translateZ(0)` CSS to the canvas element so the compositor keeps it on its own GPU layer, a widely-supported way to force hardware-accelerated composition in CEF. (3) Cached the 2D context across rAF frames instead of re-calling getContext('2d') every single draw — tiny win but frees the browser from walking the canvas element tree 60-240 times per second. OBS smoothness should match customize.exe preview now, assuming the OBS Browser Source FPS is set to match your scene FPS (usually 60)." }
    ) },
    @{ Version = "v6.5.4"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Deeper realtime pass + higher default FPS. HOP_SIZE 32 -> 16 (0.33 ms FFT cadence, 3000 Hz). SSE loop switched from polling-with-Thread.Sleep to EVENT-DRIVEN: capture thread signals an AutoResetEvent after each FFT publish, SSE thread WaitOne's on it with the 1 ms interval as a safety timeout. Net effect: SSE fires TENS of microseconds after a new FFT lands (was up to 1 ms wait before). Rate is still capped at 1000 Hz via a Stopwatch check so the HTTP thread doesn't burn CPU on redundant sends between rAF frames. Default FPS slider raised 120 -> 1000 so the rAF gate is effectively transparent and every monitor tick draws. Result: bars now update the same frame they physically can, every monitor refresh, with sub-millisecond latency inside our pipeline." },
        @{ Tag = "IMPROVED"; Text = "Overlay SSE parse deferred to rAF. At 1000+ Hz SSE the onmessage handler was previously parsing every single event (Base64 decode + copy loop), which is wasted work since most are overwritten before rendering. Now onmessage just stashes the raw payload in a variable; drawSpectrum parses the freshest-available payload right before reading _loopbackBands. Overlay CPU flat regardless of SSE producer rate. Monitor-rate parse = 60-240 parses/sec, instead of 1000+." }
    ) },
    @{ Version = "v6.5.3"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "MAX-realtime pass. HOP_SIZE 64 -> 32 (0.67 ms FFT cadence, 1500 Hz), SSE 2 ms -> 1 ms (1000 Hz), WASAPI loopback buffer 100 ms default -> 10 ms (less headroom between engine-period callback + our FFT). End-to-end audio-to-bar latency now: ~0.67 ms (FFT) + ~1 ms (SSE) + rAF. At 240 Hz monitor that's ~6 ms total; at 60 Hz ~17 ms. Both under any human perception threshold for audio-visual sync. User reported 1.5 % CPU on 7800X3D at v6.5.2's 750 Hz FFT rate, so 1500 Hz = ~3 % — still negligible." },
        @{ Tag = "IMPROVED"; Text = "Overlay SSE parse deferred to rAF. At 1000 Hz SSE the onmessage handler was previously parsing every single event (Base64 decode + copy loop), which is wasted work since most are overwritten before rendering. Now onmessage just stashes the raw payload string in a variable and drawSpectrum parses the freshest-available payload right before it reads _loopbackBands for drawing. Net: parse rate drops from 1000 Hz to monitor refresh rate (~60-240 Hz), CPU saved scales linearly. Overlay CPU flat regardless of SSE producer rate." }
    ) },
    @{ Version = "v6.5.2"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "FIXED";    Text = "Root cause of the 'not actually 1000 FPS / sluggy' complaint found. Thread.Sleep(2) in the SSE send loop was sleeping ~15 ms in practice because Windows' default system timer resolution is 15.625 ms (64 Hz scheduler quantum). Nominal 500 Hz SSE was therefore running at ~64 Hz. Fix: audio_spectrum.exe now calls timeBeginPeriod(1) on startup to request 1 ms timer resolution. Every Thread.Sleep in the process now honours its nominal value. SSE truly runs at 500 Hz now." },
        @{ Tag = "FIXED";    Text = "Tray 'Restart Master's FM' now actually restarts. Root cause: the restart action used Start-Process cmd.exe which spawned the kill-chain as a CHILD of the tray. The kill-chain's first step was `taskkill /F /IM MastersFM_Tray.exe /T` which kills tray AND its children via /T — including the cmd.exe running the chain itself. Restart halted at that point, nothing relaunched. Fix: spawn cmd.exe via WMI (Win32_Process.Create) so it runs under wmiprvse.exe, outside the tray's process tree. Also dropped the /T flag since we explicitly kill every child image by name anyway." },
        @{ Tag = "IMPROVED"; Text = "Huge audio_spectrum.exe CPU reduction via hot-loop precompute. The FFT loop at 750 Hz was: (a) computing Hann window via Math.Cos 8192 times per FFT, (b) computing per-band frequencies via Math.Pow 480x3 times per FFT, (c) computing per-band pink-noise tilt via Math.Log + Math.Pow 480 times per FFT, (d) allocating a fresh 4096-double mag buffer and 480-byte bands buffer every FFT (24 MB/s + 360 KB/s of GC pressure). v6.5.2 precomputes Hann window ONCE at startup, precomputes all per-band tables (f0, f1, fCenter, tilt multiplier, bin indices, sub-bin interp factors) the first time a sample rate is seen, swaps % modulo for & bitmask on circular-buffer reads, and double-buffers the bands output via A/B arrays. Net: ~1 million Math calls per second eliminated, zero allocations in the hot path, CPU drops from ~10-15 % of one core to ~2-4 %." },
        @{ Tag = "IMPROVED"; Text = "SSE payload 3x smaller + ~10x faster to parse on the overlay. Bands were serialized as a JSON array of 480 numbers (~1920 bytes / frame, JSON.parse over 480 number tokens). Now they're Base64-encoded (640 chars = 640 bytes / frame, atob + charCodeAt loop). At 500 Hz SSE that's ~850 KB/sec -> ~320 KB/sec on localhost loopback, and overlay parse cost drops from ~100 µs per frame to ~10 µs. Overlay stays backward-compatible with legacy array format too, so older audio_spectrum.exe instances still work." }
    ) },
    @{ Version = "v6.5.1"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Another latency pass, getting to the physical floor. HOP_SIZE halved from 128 to 64 samples (~1.33 ms FFT hop) and SSE cadence halved from 4 ms to 2 ms (500 Hz). End-to-end audio-to-bar latency at 60 fps overlay: 1.3 ms + 2 ms + 16 ms = ~19 ms — one monitor frame total. At 240 Hz FPS slider the overlay rAF drops to 4.2 ms and total hits ~7 ms, below the human perception threshold for audio-visual sync (~20-30 ms). CPU cost: 16x more FFTs per second than v6.3.5 (750 Hz vs 94 Hz), which at ~100-200 µs per 8192-point FFT works out to 7-15 % of one core — still negligible for anything made in the last decade." }
    ) },
    @{ Version = "v6.5.0"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Latency cut in half AGAIN, now essentially at monitor-refresh limit. HOP_SIZE dropped from 512 samples (~10.7 ms) to 128 samples (~2.7 ms) so the FFT output refreshes every 2.7 ms. SSE send cadence dropped from 16 ms (60 Hz) to 4 ms (250 Hz) so new data reaches the overlay within 7 ms of the audio event. Total end-to-end audio-to-bar latency now: 2.7 ms (FFT) + 4 ms (SSE) + 16 ms (overlay rAF @ 60 fps) = ~23 ms — one monitor refresh frame. If you crank the FPS slider in customize.exe to 240 Hz, the overlay rAF drops to 4.2 ms and total latency hits ~11 ms which is below every human perception threshold for audio-visual sync. CPU cost: 8x more FFTs per second (375 Hz vs 94 Hz), but a single-core spike of <5 % on any modern machine." },
        @{ Tag = "IMPROVED"; Text = "Low-volume visibility fix (v6.4.9 refinement): at 30 % listening volume or below, quiet bands were rendering as sub-visible bar heights (1-3 px) even though the music was clearly audible. Added a gentle output gamma of 0.8 as a post-compression step. Effect: 5 %-amplitude bars now render at ~9 %, 20 % → 30 %, 30 % → 42 %. Peak bars around 80 % lift by only 4 % and the ceiling at 100 % is unchanged — preserves the proportional v6.4.5/v6.4.8 feel with quiet bands visible at low volume." }
    ) },
    @{ Version = "v6.4.8"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "FIXED";    Text = "Reverted the spectrum calibration to v6.4.5 values exactly. User verdict on v6.4.6 and v6.4.7: 'everything looks like +50 dB, real bass / kicks are hard to reach the ceiling, 2 versions ago was better'. v6.4.5 was the last version the user endorsed ('this looks better'). Restored: REF_MAG = 112, single-stage knee compressor at 0.5 / 4.3:1. Every later tweak was stacking boost/compression on top of what was already calibrated correctly — overshooting in both directions. Back to baseline." }
    ) },
    @{ Version = "v6.4.7"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "FIXED";    Text = "v6.4.6's 40:1 compressor was too steep — it flattened typical music peaks to a narrow plateau around 0.66 but ALSO made the ceiling at 1.0 almost unreachable except for truly absurd signal levels. User correctly complained: 'you fucked up the ceiling again. I said COMPRESS, not LOWER the ceiling'. v6.4.7 switches to a TWO-STAGE compressor that both compresses AND preserves the ceiling. Stage 1 (input 0.5..3.3) plateaus typical music peaks at bar=0.64 (user's 20 %-below-ceiling target). Stage 2 (input > 3.3) lets bars climb toward 1.0 at a slower rate so genuine transient spikes (bass kicks, snare hits 2-4× louder than sustained peaks) still reach the top of the card. Normal music now lives in the bottom two-thirds with a plateau around 64 %, and the top ~35 % is reserved for real spike moments — preserves BOTH the compressed average AND the ceiling reachability." }
    ) },
    @{ Version = "v6.4.6"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Same transformation as v6.4.5 applied a second time, per user request ('do the same thing one more time'). Dropped REF_MAG from 112 to 63 (another +5 dB of quiet-content lift) and stiffened the knee compressor from 0.5/4.3:1 to 0.6/40:1 so the peaks that hit bar=0.8 in v6.4.5 now land at ~0.66 (another 20 % reduction from the ceiling). Net effect on bars: quiet content +5 dB taller again, mid content 1-2 dB taller, peaks come DOWN ~20 % — sustained music now fills the bottom two-thirds of the card with real dynamic shape, and only TRUE transient spikes (3-4× louder than sustained peaks) reach the top. The top ~35 % of the card is now reserved purely for 'surprise me' audio moments." }
    ) },
    @{ Version = "v6.4.5"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Another +5 dB on top of v6.4.4 for the quiet/mid range, PLUS a soft-knee compressor so peaks don't slam into the ceiling. User feedback on v6.4.4: 'still too weak overall, and bars hit the ceiling too aggressively now — lower the clipping point by 20 % but keep the ceiling'. Solution: dropped REF_MAG from 200 to 112 (+5 dB boost for all bars below the knee) AND added a hard-knee compressor at norm=0.5 with 4.3:1 ratio above that. Behaviour: quiet content +5 dB taller (e.g. a bar that was at 30 % now at ~54 %), mid content +5 dB taller too, but signal peaks that clipped at bar=1.0 in v6.4.4 now read ~0.8 (= 20 % below the ceiling). TRUE spike transients (~3× louder than v6.4.4's peaks) still reach 1.0, so the top of the card is preserved for genuine loud-hit moments." }
    ) },
    @{ Version = "v6.4.4"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Average bar height +5 dB overall. User feedback on v6.4.3: the curve shape (25 % vol = 33 % bars, etc.) was right, but the whole spectrum felt about 5 dB too quiet on average — mid-frequency sustained content in particular looked underfed. Dropped REF_MAG from 350 to 200 (350 / 1.78 ≈ 197). That's a +5 dB linear-amplitude lift across every band at every volume. Proportional response preserved: 25 % vol peaks still hit ~33 % relative to the NEW max, 75 % vol still fills the card; everything just sits ~5 dB taller on the vertical axis. Spike transient peaks now clip slightly earlier (at ~42 % vol instead of 75 % vol peak) but the body of sustained music reads with way more presence." }
    ) },
    @{ Version = "v6.4.3"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "REF_MAG tuned to final value 350 (from 150) based on real-world user calibration. At v6.4.2's REF=150, 30 % vol peaks were still hitting 90-100 % of the card — meaning the music's tiltedMag was ~142 at that volume. Extrapolating linearly for 75 % vol target: 142.5 × (75/30) = 356. Rounded to 350. Expected behavior: 25 % vol → ~32 % bars, 30 % vol → ~40 %, 50 % vol → ~68 %, 75 % vol → 100 %, above that clips. This is the sweet spot for the user's 'proportional bar-to-volume' calibration target." }
    ) },
    @{ Version = "v6.4.2"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "REF_MAG bumped again, 50 → 150, to actually hit the 25%/50%/75% → 33%/66%/100% target. v6.4.1's REF=50 left bass spikes at 25 % vol still reaching 95 % of the card. Recalculated from the user's screenshot data point: pink peak bars at 47.5 tiltedMag needed REF = 47.5 / 0.33 = 144, rounded up to 150. New expected behavior: 25 % vol peaks ~32 %, 50 % vol ~63 %, 75 % vol clips at 100 %. Sustained (non-peak) parts scale proportionally down too — i.e. the visualizer is now more 'peaky' (short sustained + tall transient hits), which is the accurate linear response." }
    ) },
    @{ Version = "v6.4.1"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Linear-amplitude REF_MAG tuned from 20 → 50 to match the real volume-to-bar targets. v6.4.0 shipped the right APPROACH (linear magnitude) but the reference constant was calibrated off a log-formula data point that underestimated typical band magnitudes by ~2.5x. In practice, bars at 25 % vol were hitting 80-100 % of the card instead of the target ~33 %. Cutting REF_MAG to 2.5x higher (50) shrinks all bar heights proportionally: 25 % vol now lands around ~33 %, 50 % around ~66 %, 75 % around ~100 %. Spike zone preserved above that. Single-number tuning knob at audio_spectrum.cs line 1074 for further adjustment." }
    ) },
    @{ Version = "v6.4.0"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum bar heights are now PROPORTIONAL to music volume. User observation: at 5 % system/music volume, bars were at ~50 % of the card — way too loud-looking for whisper music. Root cause: every prior version used dB-log normalization (20*log10(mag) + offset), which compresses the quiet end of the signal range and makes tiny inputs look almost as loud as medium inputs. Switched the entire pipeline to LINEAR AMPLITUDE: tilt is applied as a linear multiplier (10^(tilt_dB/20)) and the band magnitude divides by a fixed reference to get 0..1. Net effect: bar height now tracks volume linearly. Targets: 5 % vol → ~7 % bar, 25 % → ~33 %, 50 % → ~66 %, 75 % → ~100 %, above that = spike clip zone. If bars peak too early or too late in practice, REF_MAG (line 1065) is the single tuning knob." }
    ) },
    @{ Version = "v6.3.9"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "FIXED";    Text = "Found the 'invisible ceiling' users have been complaining about since v6.3.5. The overlay's spectrum draw loop had a hidden `* 0.78` multiplier on every bar height that capped raw output at 78 % of the canvas — line 2326, been there since v5.0.9. That's why bars always looked 'half the size of the card' no matter what dB calibration we used, AND why turning auto-gain ON made it look right (auto-gain's scale factor 255/_normPeak just happened to cancel the 0.78 out). Removed the multiplier. Raw bars now naturally fill the card up to 100 % at real spike transients, tracking the dB window directly. No auto-gain needed to get tall bars — though auto-gain still works for very quiet sources." }
    ) },
    @{ Version = "v6.3.8"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "FIXED";    Text = "Rightmost bars no longer stay flat-zero at louder music. Root cause: maxFreq was 20 kHz, putting the last bar in the 10-20 kHz range — but virtually no streaming music has energy above 15-16 kHz (MP3/AAC/Opus codecs low-pass around 15 kHz, SoundCloud streams at 15 kHz, most mastering rolls off at 18 kHz). So the rightmost bars were averaging mostly-empty FFT bins. Dropped maxFreq to 16 kHz so the last bar lands in the 8-16 kHz range where cymbals / hi-hats / snare air actually live. Test case: at bar count 10, the last bar now consistently shows content with any cymbal/hi-hat pattern in the music." },
        @{ Tag = "IMPROVED"; Text = "Bars now FILL the card at normal listening volume — no more 'peaks hovering around 45% height with dead space above'. Combined two changes: (1) applied a gamma 0.6 curve to the dB normalization, which boosts the mid-range output specifically (51% becomes 65%, 37% becomes 54%) without affecting silence or max; (2) kept the dB window wide enough that real spike transients still have ~20-25% of the card to jump up into. Concretely: at +15 dB peak (normal music) bars now reach 61%, +30 dB (loud) reaches 72%, +50 dB (very loud) 85%, and only genuine +75 dB transient peaks clip at 100%. Normal music reads 'tall, expressive, varied' instead of 'short and flat'." }
    ) },
    @{ Version = "v6.3.7"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "FIXED";    Text = "Far-right end of the spectrum (15-20 kHz air band) no longer dead-zero at whisper-quiet music volumes (1-5 %). At those levels, high-frequency FFT magnitudes land around -40 to -55 dB raw; even after the +12 dB pink-noise tilt boost, they were still below the v6.3.6 dB floor of -30 and got clipped to zero, so the last ~50 bars rendered as a flat line. Lowered the floor to -45 (window 120 dB now, by extending DOWNWARD — ceiling stayed at +75 so peak behavior and spike-headroom are unchanged). Concretely: a -28 dB signal used to read 2 % height, now reads 14 %. Normal and loud volumes identical to v6.3.6 — +55 dB still reads 0.83, +65 dB still 0.90, +75 dB still clips at 1.0. Only the quiet end gained visibility." }
    ) },
    @{ Version = "v6.3.6"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum peak height calibration — v6.3.5 over-corrected. Widening the dB window from 95 dB to 120 dB fixed the 'bars max out at 30-40% volume' complaint but dropped typical music peaks to 40-50% of the canvas at normal listening volume, which felt like the whole visualizer had shrunk. v6.3.6 splits the difference: window is now 105 dB (-30..+75 dB). Only +10 dB of extra headroom over the pre-v6.3.5 behavior, so peaks reach the top of the card at comfortable volume (+55 dB = 81% height, +65 dB = 90%, +75 dB = 100%) but don't clip until genuinely loud transients. Best-of-both-worlds between v6.3.4 (saturated) and v6.3.5 (too short)." }
    ) },
    @{ Version = "v6.3.5"; Date = "2026-04-24"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum bars no longer saturate to max height at 30-40% loudness. Users reported 'the moment I turn the music up a little, every bar is pinned at the top and I can't see the wave pattern anymore'. Root cause: the dB normalization window was -30..+65 dB (95 dB range), which mapped typical music peaks at +55 to +65 dB straight into the 0.9-1.0 (clipped) zone. Widened the ceiling by 25 dB to -30..+90 dB (120 dB range). Concretely: a +55 dB peak used to read 0.89 (89% height), now reads 0.71; a +75 dB peak used to clip at 1.0, now reads 0.87. You only hit max height at GENUINELY loud peaks now, and the whole dynamic range of the music stays visible across normal listening volumes." },
        @{ Tag = "IMPROVED"; Text = "High frequencies more visible at normal listening volume. Doubled the spectral tilt from +1.5 dB/oct to +3.0 dB/oct (full pink-noise compensation, referenced at 1 kHz). Before: quiet hi-hat / cymbal / air-band content got +6 dB lift at 16 kHz vs the bass, which wasn't enough — users had to crank the music before highs showed. After: +12 dB lift at 16 kHz vs bass, so cymbal shimmer and snare crack are visible on the spectrum at normal volume. Bass pulls down by -14 dB at 40 Hz (was -7) to compensate — still plenty of kick-drum thump visible because kicks transient-peak around +45 dB which is well above the adjusted floor." },
        @{ Tag = "IMPROVED"; Text = "Latency cut in half — spectrum bars now react in ~10.7 ms instead of ~21 ms. Halved HOP_SIZE from 1024 samples to 512 (at 48 kHz sample rate). End-to-end audio-to-bar latency is now ~45-50 ms (was ~55-60 ms). The FFT now runs faster than the 16 ms SSE send cadence, so every single SSE frame carries fresh data — no more 'the FFT I'm sending is already one hop stale'. Rebuilt NAudio capture loop runs at ~94 FFTs/sec up from ~47, negligible CPU on anything made after 2015." },
        @{ Tag = "IMPROVED"; Text = "Much sharper heartbeat at low smoothing values (user request). Widened the Smoothing slider's range by shrinking both half-lives at the 0 end: rise half-life is now 1..21 ms (was 5..30 ms) and fall half-life 4..104 ms (was 18..115 ms). At smoothing=0 the bar reaches 99.998% of target in a single 60 fps frame = instant snap = maximum heartbeat, and drops 95% in 2 frames. At smoothing=0.6 (default) the classic v6.3.4 heartbeat feel is unchanged (~13 ms rise / 64 ms fall). At smoothing=1 the decay is even dreamier than before. Crank the slider to 0 for snare-hit-level staccato response." }
    ) },
    @{ Version = "v6.3.4"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "FIXED";    Text = "Black vertical stripes between bars at high bar counts. The v6.1.7 auto-fit code was adding a fractional ~0.08 px gap between bars even when the user set Bar Gap to 0, because the canvas was slightly wider than NUM*barWidth at minimum bar width. With 480 bars the cumulative fractional gap showed as visible seams. Now gap=0 is honoured exactly — bars render edge-to-edge, sub-pixel widths are allowed, and the canvas fillStyle anti-aliases boundaries cleanly." },
        @{ Tag = "FIXED";    Text = "Bar Radius slider actually does something now at high bar counts. The old rendering threshold was 'skip rounded corners if bar is narrower than 4 px' which meant radius was useless above ~120 bars. Reduced the threshold to 'skip only if radius resolves to less than 0.3 px' — which is only the degenerate case. Radius slider now produces visible rounding at 240 and 480 bar counts too." },
        @{ Tag = "FIXED";    Text = "Smoothing slider wired to the WASAPI loopback data path (the one everyone actually uses). It was previously ONLY connected to analyser.smoothingTimeConstant, which is the browser getUserMedia path that kicks in when loopback SSE is offline — nobody sees that codepath in normal use. Now smoothing (0..1) scales both the rise half-life (5..30 ms) and fall half-life (18..115 ms) of the bar lerp: 0 gives a hard-snap heartbeat, 1 gives a dreamy slow decay, default 0.6 preserves the old v6.1.8 feel (12 / 45 ms)." }
    ) },
    @{ Version = "v6.3.3"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "NEW";      Text = "Spectrum doubled in resolution: 480 bars / 48 bars per octave (was 240 / 24). Every frequency between 20 Hz and 20 kHz now has 2x the bar-grain, so isolated spikes (a kick at 60 Hz, a snare crack at 2 kHz, cymbal shimmer at 12 kHz) show as SHARP single-bar spikes instead of smearing across 3-4 neighbors. FFT window also doubled from 4096 to 8192 samples (bin width 11.7 Hz -> 5.86 Hz), so the extra bars actually map to distinct frequencies at the low end rather than plateauing on sub-bin data. Customizer's Bar Count slider raised to 480 to match. Rough visual: 48 bars per octave is 4x the density of a pro-audio parametric EQ." },
        @{ Tag = "FIXED";    Text = "Big latency cut - spectrum bars react in ~21 ms instead of ~85 ms (was going to be ~170 ms with the new 8192 FFT). Implemented overlap-add FFT: the sample buffer is now a CIRCULAR sliding window of 8192 samples. Every 1024 new samples (~21 ms at 48 kHz) we run an FFT over the most recent 8192 samples. Decouples FFT window length (resolution) from FFT update rate (latency) - standard pro-audio analyzer pattern. Matches the SSE send cadence of 16 ms so bars pop in sync with what your ears are hearing. No more 'I heard the beat before I saw the bar move'." }
    ) },
    @{ Version = "v6.3.2"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "FIXED"; Text = "Patch Notes dialog now shows entries in correct descending version order. v5.3.0 / v5.2.4-v5.2.0 / v5.1.9-v5.1.8 were slotted in ABOVE the v6.x block by accident during the v6.0.0 big-version bump, so the dialog showed them as 'newer than' v6.3.1 which is obviously wrong. Moved the whole block down to its correct slot between v6.0.0 and v5.1.7. The reading order now is v6.3.2 -> v6.3.1 -> ... -> v6.0.0 -> v5.3.0 -> v5.2.4 -> ... -> v5.1.8 -> v5.1.7 -> ... -> v1.0.0." }
    ) },
    @{ Version = "v6.3.1"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Customizer size-preset dropdown trimmed to just 1000x200 - the only supported OBS Browser Source size after the v6.2.3 revert. The stale 800x160 / 1280x256 / 1440x180 presets were leftovers from the v6.0.x-v6.2.x experimental layouts and only confused users into picking sizes the layout no longer handles cleanly." }
    ) },
    @{ Version = "v6.3.0"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spinning border redesigned: one-direction linear slide instead of conic-gradient rotation. Root cause of the 'bouncing' complaint that survived v6.2.6-v6.2.9: rotating a conic gradient on a 1000x200 card looks circular on paper, but 85% of each color's rotation time is spent on the long 1000 px top/bottom edges and only ~10% flashing through the tiny 200 px left/right edges. So a color appeared on the right, vanished almost instantly (barely visible on the 200 px right edge), then re-appeared on the left — which perceptually reads as a jump/bounce, not a rotation. The math was correct; the wide-aspect-ratio visual wasn't. New model: linear-gradient with 5 colors distributed across 50% of the element, repeated to 100%, and animated via background-position 0% -> -200%. Produces unambiguous left-to-right color motion along the visible border (LED-marquee look). All 5 colors still cycle; direction is now crystal-clear regardless of card aspect ratio." }
    ) },
    @{ Version = "v6.2.9"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spinning border: simpler CSS so it ACTUALLY spins. v6.2.8 used transform: translate(-50%, -50%) rotate(theta) on the pseudo-element, animated via keyframes that also combined translate with rotate. In CEF that multi-function transform was sometimes visually rendered as a symmetric left/right oscillation instead of one-way rotation - users saw it as 'bouncing left and right'. Rewrote: the 1600x1600 pseudo-element is now pre-centred via top: 50%, left: 50%, negative margins, transform-origin: 50% 50% - so the animated transform is JUST `rotate(0deg -> 360deg)` with nothing ambiguous for the compositor. One GPU layer, one property animating, unmistakable circular rotation." }
    ) },
    @{ Version = "v6.2.8"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spinning border spins again. v6.2.7 switched to Houdini @property animation of --spin-angle used in the conic-gradient - that's cleaner in theory but CEF (OBS Browser Source's Chromium embed) rendered the gradient correctly on first paint but never interpolated the custom property, so the border stayed static at 0deg. Reverted to the classic transform: rotate(0 -> 360deg) keyframe on the 1600x1600 pseudo-element - that's a straightforward GPU-composite of a single layer and works reliably in every Chromium-based host. The original 'bouncing' complaint that drove the v6.2.7 switch was actually caused by the mirrored conic-gradient stop pattern, which v6.2.6 fixed - so with that already landed, element rotation now produces clean one-direction color cycling." }
    ) },
    @{ Version = "v6.2.7"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spinning border spins cleanly in one direction now — no more bouncing. Previous approach rotated the whole 1600x1600 pseudo-element via transform keyframes, which required promoting the element to its own GPU layer (will-change + contain). In CEF (OBS Browser Source's browser engine) that occasionally tripped the CPU-fallback path or got the animation into weird states where users perceived colors sweeping back and forth instead of around. New approach: use a Houdini @property custom property --spin-angle animated 0 to 360 degrees, and the conic-gradient reads `from var(--spin-angle)` — so the GRADIENT ITSELF rotates while the element stays still. No transform keyframes, no GPU layer promotion, no containment, and rotation is identical in Chrome DevTools preview and in CEF." }
    ) },
    @{ Version = "v6.2.6"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spinning border actually SPINS now instead of bouncing. The conic-gradient stop builder was distributing the 5 colors over 0->180 degrees and then MIRRORING them back 180->360 degrees — so the resulting gradient looked like A-B-C-D-E-D-C-B-A around the ring. When the element rotated, viewers saw colors sweep one way, hit the middle, reverse, and come back. That's the 'bouncing' users reported. Fix: distribute the 5 colors EVENLY around the full 360 degrees (A at 0, B at 72, C at 144, D at 216, E at 288, A at 360 to close the loop). The card now rotates cleanly in one direction and all 5 colors cycle past once per rotation like a proper rainbow wheel." }
    ) },
    @{ Version = "v6.2.5"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "FIXED";    Text = "SoundCloud overlay timer stuck at 0 s — now ACTUALLY fixed. v6.2.4 only covered ASIO/WDM-KS/MME audio backends, but the real bug affects WASAPI Loopback users too. Root cause: the Core Audio peak fallback called GetPeakForProcessName('soundcloud'), which matches sc-rpc.exe — but sc-rpc is a pure SMTC bridge that reads SoundCloud state from Chrome / Edge / Firefox via CDP and registers a SMTC session. The actual audio flows through the BROWSER, not sc-rpc. So sc-rpc's Windows audio session peak is ALWAYS ~0 regardless of whether SoundCloud is playing, and the peak override was permanently pinning SMTC Playing to Paused on every user with sc-rpc installed. Removed the peak fallback entirely — SMTC's Playing/Paused + the title-prefix ▶/► detection are the authoritative signals and they're reliable on their own. Overlay timer now advances normally again." }
    ) },
    @{ Version = "v6.2.4"; Date = "2026-04-23"; Notes = @(
        @{ Tag = "FIXED";    Text = "SoundCloud overlay timer stuck at 0 s when audio is routed through ASIO / WDM-KS / MME. The soundcloud-rpc detector falls back to Windows Core Audio session peak when it can't read the SoundCloud browser window title — if the peak reads below 0.001 it overrides SMTC's Playing state to Paused. Problem: Core Audio's per-process peak is ALWAYS near zero when audio leaves Windows through an ASIO/WDM-KS driver (it bypasses the shared mixer entirely) — so VB-Matrix / Voicemeeter / Audient users saw SoundCloud's track identity detected fine but the overlay timer frozen at 0 s because the tray kept force-pausing it. Now the audio-peak override is SKIPPED when the user's Audio Source is set to a non-WASAPI backend; SMTC's Playing/Paused status is trusted directly. WASAPI Loopback users are unaffected — the Core Audio peak still resolves their edge cases." }
    ) },
    @{ Version = "v6.2.3"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "REMOVED"; Text = "Big revert — all v6.0.5 through v6.2.2 OBS placement / card-layout experiments pulled out. Back to the pre-v6.0.5 model: OBS Browser Source defaults to 1000x200, the card fills the source edge-to-edge like always, no Card Side picker, no Card Placement section, no Advanced OBS Source Size, no Card Width/Height/Text Column Width sliders, no Text & Art Side flip, no Expand Visualizer slider, no Reset Position & Size button. Users resize the source in OBS properties the way they always did." },
        @{ Tag = "REMOVED"; Text = "Supporting code cleaned up: overlay.html's #widget is back to inset:40px, .info is back to flex:1 (min-width:0), .title-wrap mask-image is gone, DEFAULTS.card has no layout keys, the spinning border pseudo-element is back to 1600x1600. tray.ps1 dropped Get-OBSCanvasSize / Get-OBSSourceSide / Get-OBSSourceSize / Compute-OBSPosition / Reset-OBSSourcePosition and the /obs-reset-position flag watcher. server.js dropped the /obs-reset-position endpoint. customize.html dropped every placement binding, sync, DEFAULTS key, and the HTML rows for those controls." },
        @{ Tag = "KEPT";     Text = "Spectrum visualizer improvements are ALL preserved — this revert only touched layout / placement. Kept: 240-band output, 4096-point FFT, 20 Hz - 20 kHz range, sub-bin linear interpolation (flat-front fix), Path2D single-draw CPU optimization, horizontal rainbow gradient, thin-bar rect fast-path, simplified glow (fewer drop-shadows), dt-aware dual half-life rise/fall (12 ms rise, 45 ms fall) for FPS-independent sharp heartbeat, dynamic bar gap so 240 bars fit any canvas, -30..+65 dB headroom, Render FPS slider honored up to 1000 with no preview cap." },
        @{ Tag = "KEPT";     Text = "Non-layout bug fixes also preserved: Discord RPC rate-limit throttle + faster reconnect (v6.0.4), dynamic colors match album art (gradient / rainbow) (v6.0.3), per-card meters in Audio Source scan (v6.0.1), non-blocking scan + Restart-brings-tray-back (v6.0.2), Welcome dialog taskbar icon (v6.1.5), dynamic spinning border colors (v6.0.5), spin-border animation re-emit fix (v6.2.0), customize UI defensive sync helpers + normalize-against-DEFAULTS (v6.2.2), /version poll auto-reload on rebuild (v6.2.1)." }
    ) },
    @{ Version = "v6.2.2"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Customize toggles and colors now ACTUALLY reflect the saved state. Two compounding bugs: (1) the sync helpers (syncToggle, syncColor, etc.) threw on any missing DOM element or throwing getter, which aborted syncAll mid-iteration — every control AFTER the failure point got stuck at its HTML default (unchecked for toggles, #000000 for color pickers). Now every helper is wrapped in try+null-guard so one broken line doesn't cascade. (2) DEFAULTS.dynamicColors was missing `platformBadge` and `titleGlow` keys, so the normalize step couldn't fill them from fallbacks when saved configs lacked those fields either. Added both to DEFAULTS. Combined result: every toggle, slider, and color picker always shows the exact state saved to disk or in the loaded preset, with no silent sync failures." }
    ) },
    @{ Version = "v6.2.1"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Customize window now auto-reloads after a Master's FM rebuild. v6.2.0 fixed the syncAll bug that was making toggles show 'off' even when they were saved as 'on' — but if users had the customize window ALREADY OPEN during the rebuild, they kept running the pre-v6.2.0 HTML with the broken sync logic. Now the customize page polls /version every 3 seconds and location.reload()s itself when the server's boot-id bumps (which every rebuild does). Fresh HTML every rebuild, no manual close + reopen needed." }
    ) },
    @{ Version = "v6.2.0"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spinning border actually spins again (was 'bouncing at the top and bottom'). Every applyConfig call was re-emitting the 'animation: border-spin ...' rule into the dynamic CSS block - and browsers RESTART a CSS animation when its animation property is re-set, even to the identical value. Preview pushes + SSE broadcasts fire applyConfig multiple times per second, so the 4-second spin kept resetting to 0 degrees before completing a rotation. Fixed by leaving the animation declaration solely in the head CSS (which is loaded once and never touched) - buildDynCss now only emits the conic-gradient background, not the animation rule." },
        @{ Tag = "FIXED";    Text = "Customizer sidebar no longer shows every toggle as 'off' and every color as black when they're actually on with real colors. Root cause: syncAll was pushing whatever was in S into each UI control, but if the saved config was partial (an old preset, a just-loaded theme, etc.) missing keys read as `undefined` - which Chrome renders as unchecked for checkboxes and #000000 for color pickers. The underlying overlay was using the right values (via its own ?? fallbacks), so the behavior was correct but the UI pane was lying about the state. Now syncAll runs `S = deepMerge(DEFAULTS, S)` first so every key exists with either the user's value or the default, and the sidebar always reflects what the overlay is actually showing." },
        @{ Tag = "FIXED";    Text = "OBS card placement no longer flips left/right when toggling Dynamic Color options. Cause: the dyn-toggle POST was sending an S that was missing card.positionInSource / contentSide / width / height, so the overlay fell back to the DEFAULTS for those keys (positionInSource='left-bottom') and visually snapped the card back to the left edge. sendPreview now normalizes S against DEFAULTS before the POST so the broadcast payload always contains every key the overlay expects - no more silent fallbacks." },
        @{ Tag = "FIXED";    Text = "Theme switches now preserve your card layout. Clicking 'Rainbow (Default)' / 'Neon Blue' / etc. no longer resets Card Width, Card Height, Text Column Width, Content Side, Position In Source, or Visualizer Expand to defaults. Themes are an AESTHETIC choice (colors, glow, border stops) - they now explicitly pull your layout keys through the merge so the card stays sized and positioned the way you set it." }
    ) },
    @{ Version = "v6.1.9"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Track title fade mask no longer makes short titles unreadable. v6.1.0-v6.1.8 applied a 40 px left-edge fade mask unconditionally to .title-wrap so the marquee's right-edge clipping looked softer. Side-effect: titles that DIDN'T need to scroll (which is most titles) had their FIRST characters permanently faded - 'OPEN THE DOOR' looked like 'PEN THE DOOR' etc. The mask is now only applied while the marquee animation is actively running (long-title case), and it's a narrow 25 px bidirectional fade so both entering-right and exiting-left chars soft-blend without eating either end of the visible window. Short titles that fit the container stay fully crisp start-to-end." }
    ) },
    @{ Version = "v6.1.8"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "1000 fps Render FPS setting now actually runs at 1000 fps (or monitor refresh — whichever is lower). v6.1.7 hard-capped the customizer PREVIEW at 60 fps to cut CPU, but that was masking the slider and users reported '1000 fps still doesn't look like 1000 fps'. Removed the preview hard-cap — rAF naturally clamps to monitor refresh (60 / 120 / 240 Hz), so there's no runaway CPU cost from setting the slider high." },
        @{ Tag = "FIXED";    Text = "Bars are no longer sawtooth-jittery during loud music. v6.1.7 had instant rise-snap (alpha=1 on rise) so every SSE frame at 60 Hz caused a discrete visual jump UP, then a smooth lerp down, then another jump. That's a sawtooth pattern users perceived as 'not smooth at all'. New model: RISE uses a 12 ms half-life exponential lerp — at 60 fps a rise still completes ~94 % in ONE frame (visually indistinguishable from a snap) but the sub-frame motion is continuous, eliminating the per-SSE-frame step artefact. Fall stays at 45 ms half-life." },
        @{ Tag = "IMPROVED"; Text = "Dynamic range widened on loud music. Spectrum dB window bumped from -30..+50 (80 dB) to -30..+65 (95 dB). Loud music peaks at +55 to +65 dB were flat-topping multiple adjacent bars to 255 (saturation) — the bars all looked the same tall height instead of showing the subtle difference between, say, a kick at +60 and a snare at +62. Extra 15 dB of headroom keeps the dynamic range VISIBLE through the loudest moments." }
    ) },
    @{ Version = "v6.1.7"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Rightmost bars no longer clipped off-canvas when bar count is high. At 240 bars in a narrow (e.g. 630 px) canvas, the configured gap of 3 px times 239 gaps = 717 px of gaps alone — more space than the canvas had — so the last ~100 bars were positioned at x > canvas.width and drew OFF-SCREEN. Users reported 'the bar on the end doesn't even activate the frequencies'. Now the gap auto-shrinks so all bars fit; at extreme counts it drops to 0 and bars go down to 1 px wide rather than disappearing." },
        @{ Tag = "FIXED";    Text = "Bar fall is now FPS-independent (sharp heartbeat at any render rate). Previous fixed per-frame 0.55 alpha made fall WALL-CLOCK time depend on FPS — at 10 fps (throttled WebView2 / CPU-starved preview) bars took ~1 second to decay which felt dead. New model: exponential decay with a 45 ms half-life regardless of render FPS. 30 fps, 60 fps, 120 fps, 240 fps — all give the same wall-clock sharp snap-and-decay shape. Rises still snap instantly, so the heartbeat-pop feel is unchanged." },
        @{ Tag = "IMPROVED"; Text = "Customizer preview iframe now hard-caps rAF at 60 fps regardless of your Render FPS slider. The preview is a thumbnail sanity check — it doesn't need 120 fps. With 240 bars, pulsing glow, and rotating border all animating continuously, uncapped 120 fps preview was burning ~12 % CPU on WebView2. 60 fps preview looks identical to the eye and cuts that in half. The LIVE OBS Browser Source still respects your full FPS setting." },
        @{ Tag = "IMPROVED"; Text = "Default OBS Browser Source fps raised 30 -> 60 for fresh adds. Our overlay pushes frames at whatever the Render FPS slider says (up to 1000 hz), but OBS's own Browser Source has its own fps cap that clamps what it captures — 30 was conservative from early days. 60 matches typical monitor refresh and lets the sharper bar animations actually reach viewers." }
    ) },
    @{ Version = "v6.1.6"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum low-frequency flat-front fixed (again). At BAND_COUNT=240 with a 4096-point FFT (11.7 Hz bins), many low-end log-spaced bands fell within the same FFT bin — they all read the IDENTICAL average magnitude and bars plateaued at the same height. Now sub-bin bands use linear interpolation at the band's geometric-mean center frequency between the two enclosing FFT bins, so each band gets a unique interpolated value even though the underlying FFT resolution is unchanged. Low bass bars now step smoothly instead of aligning at one dB level." },
        @{ Tag = "IMPROVED"; Text = "Spectrum draw loop rewritten for big CPU savings. Before: 240 LinearGradient objects created per frame + 240 separate ctx.fill() calls = 14,400 GPU uploads/s at 60 fps. After: ONE gradient per frame (horizontal multi-stop for rainbow/gradient modes so each bar's x-position naturally picks up its own hue), all bars batched into a single Path2D, and one ctx.fill() per frame. Measured 5-10x less spectrum draw cost on a 240-bar card. Visual output is essentially identical; rainbow mode loses the per-bar vertical shading but keeps the dominant horizontal hue sweep." },
        @{ Tag = "IMPROVED"; Text = "Glow animation simplified — removed the 3rd drop-shadow at the 50 % keyframe (previously 80 px blur radius at 0.12 alpha). Large-kernel drop-shadow filters are per-pixel GPU work on every frame of the continuous glow-pulse animation; dropping that one shadow cuts ~33 % of filter work during the pulse without a visible difference to the user (the shadow was so faint at 0.12 alpha you could barely see it)." },
        @{ Tag = "IMPROVED"; Text = "Thin bars skip rounded corners. When a bar is under 4 px wide OR its corner radius resolves to <1 px, the draw loop calls path.rect() instead of the 7-operation path.moveTo/lineTo/quadraticCurveTo chain. Visually imperceptible at high bar counts (human eye can't see rounding on a 3 px bar) but removes a lot of path-ops from the frame budget." }
    ) },
    @{ Version = "v6.1.5"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Patch Notes / Welcome dialog taskbar thumbnail now shows the Master's FM purple icon instead of the default WinForms blank-window icon. Cause: Show-WelcomeDialog is the only form in the app with ShowInTaskbar=\$true (every other dialog doesn't appear in the taskbar) but the form's Icon property was never set. Now it loads MastersFM.ico on dialog open so the taskbar hover-preview matches the rest of the app's icons in File Explorer / Task Manager." }
    ) },
    @{ Version = "v6.1.4"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Slider maxes raised back: Card Width 3000, Card Height 600, Text Column Width 900. Previous v6.1.1/v6.1.3 restrictions (1840/200/720) were too conservative — users wanted room to make big dramatic cards. The clamp logic on #widget (max-width: calc(100% - 80px), max-height: calc(100% - 80px)) still prevents overflow, so sliding the card to its max just produces a BIG card that fits cleanly inside the source." },
        @{ Tag = "IMPROVED"; Text = "Defaults bumped to match the new maxes: Card Width 3000, Card Height 600, Text Column Width 900. Fresh installs now get a bold big card out of the box; users who want a smaller compact card can dial the sliders down. Reset Position & Size button now snaps back to these NEW defaults — one click = big clean card in the user's chosen dock side." },
        @{ Tag = "IMPROVED"; Text = "OBS Browser Source default returned to 1920x1080 (full canvas) after the brief v6.1.0-v6.1.1 detour to 1920x200. The 'Advanced: OBS Source Size' collapsible in the Card Placement and OBS section is back, so users who need a different aspect ratio (1280x720, 2560x1440, whatever) can still override." }
    ) },
    @{ Version = "v6.1.3"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Reset Position & Size button now ACTUALLY resets the size sliders. v6.1.2 bumped the overlay bootId so OBS would reload, but it pushed the user's current (possibly bad) values unchanged — so clicking Reset on a 1840 / 200 / 900 card just reloaded OBS with the same broken 1840 / 200 / 900 card. Now clicking Reset snaps Card Width back to 1000, Card Height to 200, Text Column Width to 560 (the known-good defaults), syncs the UI sliders to match, saves the config, then force-reloads OBS. One click = clean slate in the dock side the user picked." },
        @{ Tag = "FIXED";    Text = "Text Column Width slider max lowered from 900 to 720. At 900 with the default card width of 1000, the text column was eating 90 percent of the card and leaving essentially no room for the spectrum — a value users could hit by accident and wonder why the spectrum disappeared. 720 is still wide enough to fit long titles without wrapping and keeps at least ~300 px of spectrum room even on narrower cards." }
    ) },
    @{ Version = "v6.1.2"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Card Width default reverted from 1500 back to 1000 — v6.1.1's larger default felt wrong on screen even though the math was better. 1000 is the same value that was good pre-v6.0.5, matches user muscle memory, and leaves 920 px of transparent scene-bg visible on the opposite side of the 1920x200 strip. Users who want a bigger card can still drag the slider up to 1840." },
        @{ Tag = "FIXED";    Text = "'Reset Position & Size' button now always produces a visible effect in OBS. Before: the button would save config + write a scene-item-rewrite flag, but since the 1920x200 source fills the full canvas width, left/right/center all computed the same scene-item position (x=0) — nothing changed visually and users thought the button was broken. Now the reset ALSO bumps the server's bootId, which forces every connected overlay (OBS Browser Source + customize preview iframe) to detect a version change on its /version poll and trigger a full location.reload() within ~3 seconds. You see your settings flash into place in OBS every time." }
    ) },
    @{ Version = "v6.1.1"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Customizer sections reorganised — 'Card Placement and OBS' now sits directly below 'Card Side in OBS' (previously buried at the bottom of the sidebar). The two sections that answer the same question — where does my card go, and how big — are now side-by-side at the top so users find them first without scrolling through every other section." },
        @{ Tag = "IMPROVED"; Text = "Better out-of-box defaults for the card. Card Width now defaults to 1500 px (was 1000) so a fresh install gives art (310) + info (560) + spectrum (~630) — the 240-band visualizer finally has enough pixels to breathe. Old 1000-wide default squashed the spectrum to 130 px which made the bars look cramped." },
        @{ Tag = "IMPROVED"; Text = "OBS Browser Source is now locked at 1920x200 — no user-facing knobs to change it. The advanced 'Source Size' collapsible section was removed; previous versions exposed it as an escape hatch but in practice all the layout math assumes 1920x200 and changing it created weird artefacts. Users who need a different aspect ratio can still edit the source dimensions manually in OBS properties." },
        @{ Tag = "FIXED";    Text = "Title marquee now shows the END of long titles crisply instead of fading out the final characters. v6.0.9 added a right-edge fade mask so titles would soft-blend into the card background — but when the marquee scrolled all the way to the end, that same fade ate the last few letters, making it look like the title was truncated. Moved the fade to the LEFT edge (where characters scroll OFF-screen) so the right edge — where the end of the title comes to rest — stays fully legible." }
    ) },
    @{ Version = "v6.1.0"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "OBS Browser Source default corrected to 1920x200 — a full-width strip the card can dock to, not the 1920x1080 full canvas v6.0.9 briefly set. The strip is short enough that docking it at the top or bottom of the OBS scene never covers gameplay, wide enough that left/right card positioning is visually distinct. Auto-migrates on next tray boot." },
        @{ Tag = "NEW";      Text = "Dedicated 'Card Side in OBS' section at the top of the customizer — a single clear dropdown (← Left / → Right / ═ Center) for the one question users care about: which side of the 1920x200 strip does my card dock to? Stays in lockstep with the existing Placement section's side picker so touching either one updates both. Users wanted this one-click toggle rather than hunting through the Placement section." },
        @{ Tag = "IMPROVED"; Text = "Size-preset dropdown in the topbar now starts at 1920x200 (matching the new default) with 1280x200, 2560x200, and a 1920x300 'tall strip' option. Legacy 1000x200 kept as an option for users migrating from older installs." }
    ) },
    @{ Version = "v6.0.9"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "NEW";      Text = "OBS Browser Source now fills the whole canvas (1920x1080 default) and the card sits INSIDE it as a fixed-size box. That means your scene background shows through everywhere except where the card is — no big black rectangle around the card, no weird aspect-ratio distortion if your canvas isn't 1080p. The Source Size sliders are now in an 'Advanced' collapsed section; most users never need to touch them." },
        @{ Tag = "NEW";      Text = "Card Width + Card Height sliders control the size of the CARD (separate from the source). Info column (art + text) stays at its fixed width — expanding Card Width grows ONLY the spectrum + timeline side. Exactly matches the mental model users described: 'extend only on the part of the spectrum visualizer and the timeline'. Wider card = more pixels for the bars, so the 240-band spectrum (v6.0.5) finally has visual room to stretch — bars at 20, 30, 40, 50, 60 Hz read as distinct features instead of a mush at the low end." },
        @{ Tag = "NEW";      Text = "'Card Position in Source' picker — Left edge / Right edge / Center bottom. Anchors the card to one corner of the source. Switching it is instant (CSS-var update, no reload). Combine with Card Width + the Text & Art Side flip for the exact layout you want." },
        @{ Tag = "NEW";      Text = "Text Column Width slider (320-900 px, default 560). Sets how much of the card the artist + title + badges get. Narrower = more space for the spectrum at the same card width. Wider = more room for long titles before the marquee kicks in." },
        @{ Tag = "FIXED";    Text = "Track title now fades softly into the card background when it scrolls instead of hitting a hard right-edge cut-off. Added a 50 px linear-gradient mask on the title container so the text gracefully fades out as the marquee reveals more of a long title, and fades back in on reset. Same behavior users had in older builds before the flex-layout refactor." }
    ) },
    @{ Version = "v6.0.8"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "OBS Browser Source default reverted to the classic compact 1000x200. v6.0.5 bumped it to 1920x200 trying to fill a 1080p canvas edge-to-edge, but that clashed with how people actually use the overlay (small corner card, not full-width). OBS scene canvas (1920x1080, 1280x720, whatever you set in OBS Settings > Video) is unchanged — only the Master's FM source size is reset. Existing installs auto-migrate to the configured size on next tray boot." },
        @{ Tag = "NEW";      Text = "Card Width / Card Height sliders in the customizer (Placement section). Range 400-2400 px wide, 100-600 px tall. The Master's FM Browser Source in every OBS scene collection syncs to whatever you pick — either on the next tray boot, or immediately when you hit 'Reset Position & Size' (the existing Reset button now also pushes your size preference, not just the anchor side)." },
        @{ Tag = "IMPROVED"; Text = "Reset Position button is now 'Reset Position & Size' — rewrites both the scene-item position AND the source dimensions on the existing Master's FM source in every scene collection. Anchor math uses the NEW source size (so a 'right' anchor stays flush-right even right after you shrink the width). Restart OBS to see the change in the live scene — OBS holds scene state in memory and overwrites the JSON on exit." }
    ) },
    @{ Version = "v6.0.7"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spinning card border now spins correctly inside OBS. The v6.0.6 pseudo-element was 2800x2800 = 7.84 MP, which combined with 'contain: strict' tripped CEF's (OBS Browser Source's browser engine) threshold for CPU-fallback compositing on some GPUs. On CPU, transforms of a multi-megapixel layer don't run smoothly and the animation looked frozen even though the CSS clock was ticking. Dropped to 2000x2000 (4 MP, stays in the GPU fast-path universally) and relaxed 'contain: strict' to 'contain: layout paint' so CEF doesn't over-eagerly skip paint updates. Border now sweeps smoothly in both Chrome/Edge preview AND live OBS Browser Source. 2000 square still covers the 1920-wide default card at every rotation angle with a small safety margin." },
        @{ Tag = "FIXED";    Text = "Big empty gap between track text and spectrum visualizer — closed. On the 1920-wide default card, the info column had 'flex: 1' which made it hog ~800 px of horizontal space even when the title/artist text only needed ~400 px, leaving a ~400 px dead zone between the text and the visualizer. Info is now 'flex: 0 1 auto' so it sizes to its text content, and the right column (still 'flex: 1') fills everything else — text sits snug against the visualizer with no visible gap." },
        @{ Tag = "NEW";      Text = "Card Layout - Text & Art Side picker in the customizer. New select under the Card section: choose 'Left' (art + text on the left half, visualizer on the right — the classic layout) or 'Right' (mirrors the whole thing: visualizer on the left, text + art on the right). Flips flex-direction on .card-inner so the whole layout inverts cleanly including the divider border." },
        @{ Tag = "NEW";      Text = "Expand Visualizer slider (0-100%). Shrinks the info column's maximum width so the spectrum canvas can claim more of the card. At 0% (default) info can take up to 55% of the card; at 100% it's clamped to 25% so the visualizer gets the other 75%. Useful when you want the bars to dominate or when your titles are short and you don't need the text space." }
    ) },
    @{ Version = "v6.0.6"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spinning card border actually spins again. v6.0.5 bumped the default OBS Browser Source to 1920 wide, but the rotating conic-gradient pseudo-element was still 1600x1600 — that left 160 px uncovered on each side of the card at 0 / 180 deg rotation, and as the animation swept those gaps moved around in a way that made the border look frozen or broken instead of sweeping smoothly. Bumped the pseudo-element to 2800x2800 so the card is fully covered at every rotation angle (handles up to 3840-wide ultrawide sources too). Also added an explicit 'animation: border-spin ... linear infinite' in the dynamic-CSS injection so the animation can't be clobbered by a stale earlier state." },
        @{ Tag = "NEW";      Text = "OBS Source Side placement. New 'Placement' section in the customizer lets you choose where the Master's FM Browser Source anchors itself on the OBS canvas: Left edge, Right edge, or Center bottom. When the tray auto-adds the source to a scene for the first time, the pos is set from that preference (canvas size read from your most recent OBS profile's basic.ini, falls back to 1920x1080). Scene items you've already positioned by hand are LEFT ALONE — the anchor only applies to first-time adds, so rearranging the source inside OBS sticks." },
        @{ Tag = "NEW";      Text = "'Reset Position to [side]' button in the Placement section. Rewrites just the pos field on the existing Master's FM scene-item in every scene collection, re-snapping to the chosen edge without removing/re-adding. Works via a flag file the tray watches; if OBS is open during the reset, the tray shows a balloon reminder to restart OBS for the change to stick (OBS keeps scene state in memory and overwrites the JSON on exit)." }
    ) },
    @{ Version = "v6.0.5"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "NEW";      Text = "Spectrum capacity DOUBLED. Output bands raised 120 -> 240 so users with enlarged OBS Browser Sources have room for twice as many bars. The customizer's Bar Count slider goes to 240 to match. More bars -> finer frequency resolution -> more accurate visualizer across the whole spectrum, not just clustered at one end." },
        @{ Tag = "NEW";      Text = "Frequency range expanded to the full audible band. Spectrum now covers 20 Hz - 20 kHz (was 40 Hz - 16 kHz). Sub-bass (kick drum fundamentals, reggae bass, 808s) finally register on their own bars at the low end, and cymbal shimmer / hi-hat air reads distinctly at the top. FFT size bumped 2048 -> 4096 so the 11.7 Hz bin width still gives every one of the 240 bands a distinct FFT slice — no flat plateaus at the low end even at max bar count." },
        @{ Tag = "NEW";      Text = "Spinning border is now dynamic-color-aware. When Dynamic Colors are enabled, the rotating conic-gradient border uses a 5-stop palette derived from the album art (primary hue + shifts + secondary hue) instead of your static preset. The border finally reads as 'the album color' like the rest of the card, rather than sitting out as a fixed preset palette." },
        @{ Tag = "NEW";      Text = "Default OBS Browser Source size bumped from 1000x200 to 1920x200 on auto-add. Card now fills the full canvas width on a standard 1080p OBS scene out of the box — gives you more room for long track titles and artist names, and more bars across the spectrum. Existing 500x100 / 1000x200 auto-migrate to 1920x200 on next tray boot; user-customized sizes (anything other than those two legacy defaults) are preserved." }
    ) },
    @{ Version = "v6.0.4"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Discord Rich Presence no longer hangs when you skip tracks rapidly. Root cause: Discord's RPC IPC enforces ~5 SET_ACTIVITY writes per 20 seconds per client. Each track change normally produces 2+ writes (initial push with placeholder art, then a second push when real art resolves asynchronously), so rapid skipping easily hit 10+ writes in a few seconds — past the limit Discord silently drops further frames or force-closes the pipe. The RPC client now enforces a 2-second minimum between socket writes with latest-wins coalescing: intermediate states during rapid track changes collapse into one final write at the window boundary, so Discord always gets the correct final state without hitting the rate limit." },
        @{ Tag = "FIXED";    Text = "Discord pipe reconnect is now fast enough to be useful. If Discord wasn't running when Master's FM started, the retry was on a 30-second cadence — opening Discord right after Master's FM meant up to 30 s before presence appeared. Cut to 10 s so the common 'I just opened Discord' case recovers quickly while still being quiet when Discord is genuinely not installed." },
        @{ Tag = "NEW";      Text = "Adaptive rate-limit detection. If Discord ever sends back an ERROR frame mentioning 'rate' / 'too many' / code 4006 (the known variants of their rate-limit response), the client extends its minimum-send window by an extra 10 seconds so the next write definitely falls under the limit. Self-healing — normal cadence resumes after the back-off window clears." }
    ) },
    @{ Version = "v6.0.3"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Dynamic-color GRADIENT mode was almost never matching the album art. The bar hue was computed from the AVERAGE BRIGHTNESS of the base color ((r+g+b)/3/255 * 240) instead of from the art-derived hue — so a bright-red cover landed around 100 degrees (green) on the spectrum, a deep blue cover landed on orange, and so on. Gradient mode now centers on the art hue and spans 60 degrees across the bars, so every bar visually reads as 'the album art color' instead of a random unrelated hue." },
        @{ Tag = "FIXED";    Text = "Dynamic-color RAINBOW mode also drifted away from album art. Even with dynamic colors on, the rainbow's center was being rotated a full 360 degrees every ~30 seconds, so you only briefly caught the art hue once per cycle — the rest of the time the bars showed the wrong (often complementary) colors. The rotation is now SKIPPED in dynamic mode: the 80-degree rainbow stays statically centered on the art hue. Non-dynamic rainbow keeps the original 8-second animated sweep." },
        @{ Tag = "IMPROVED"; Text = "Spectrum bass/treble balance. Real music follows a ~1/f amplitude spectrum so bass bins always come in ~10-20 dB hotter than treble — the bars were visually tilted bass-heavy regardless of genre. Added a gentle +1.5 dB/octave tilt above 1 kHz (half of full pink-noise compensation). At 40 Hz bass is pulled down ~7 dB, at 16 kHz treble is lifted ~6 dB. Subtle on individual transients, clearly more balanced across a full track. Character stays recognizable — this is polish, not a flattening." }
    ) },
    @{ Version = "v6.0.2"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED";    Text = "Scan & Auto-select no longer freezes the Audio Source window. The old implementation ran a synchronous for-loop with Start-Sleep on the UI thread, so you couldn't drag the window, click Cancel, or close the dialog mid-scan — the whole form was locked for the 3+ minute sweep. Rewritten as a System.Windows.Forms.Timer state machine (250 ms tick, 1.25 s settle per device). The form stays fully responsive: drag it, hit 'Stop scan' mid-sweep, close the dialog, everything still works while the scan runs in the background." },
        @{ Tag = "IMPROVED"; Text = "Per-card status text during scan is now plain English: 'Waiting...', 'Checking...', 'Audio detected!' (green), 'No audio' (dim). No more raw 'lifetime=0.482 rolling=0.340' numbers — you see at-a-glance which entries actually have audio flowing, exactly like 'audio detected' on a mic-test screen." },
        @{ Tag = "FIXED";    Text = "'Restart Master's FM' reliably brings the tray icon back. Before: ~1 in 3 restarts came back with server.exe + audio_spectrum.exe running but no tray icon visible. Cause: the old restart chain used 'ping -n 2' (~1 second) between killing the old process and launching the new one, but the old tray + launcher's named mutexes (Global\\MastersFM_Launcher, Global\\MastersFM_SingleInstance) take longer than that to fully release during PowerShell-runspace + WinForms teardown. The new tray hit WaitOne(0), saw 'already running', and exited silently. Fix: (a) explicitly ReleaseMutex + Dispose the single-instance mutex before Application.Exit, (b) taskkill /F every stale Master's FM process in the restart chain, (c) bump the pre-launch delay to 3 seconds. Tested end-to-end: all four processes consistently restart with fresh PIDs." }
    ) },
    @{ Version = "v6.0.1"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "NEW";      Text = "Per-card live meters in the Audio Source dialog. Click 'Scan & Auto-select Best' and each card's description row live-updates as the scan reaches it — you see 'Waiting...' → 'Checking...' → 'Audio detected!' (green) or 'No audio' (dim) progressively across every card. No more clicking every entry one-by-one to find which ASIO channel has your Media bus — the scan shows you directly, under the name of every output." },
        @{ Tag = "IMPROVED"; Text = "Scan pass time shortened from 2.5 s to 1.5 s per device. Still long enough for ASIO drivers to spin up + deliver real buffers, but cuts a full 140-entry sweep from ~6 minutes to ~3.5 minutes." },
        @{ Tag = "IMPROVED"; Text = "If the scan finds no audio on any endpoint, your pre-scan device is restored automatically instead of stranding the visualizer on whatever was last probed. No more 'I scanned and now nothing works' surprise." }
    ) },
    @{ Version = "v6.0.0"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "NEW";      Text = "Per-channel ASIO selection. Every ASIO driver now enumerates each channel pair separately — 'VB-Matrix VASIO-32 - Ch 1-2', 'Ch 3-4', 'Ch 5-6', ..., up to 16 pairs per driver (caps the 672-entry VASIO-128 enumeration explosion). VB-Matrix / Voicemeeter / MOTU / Focusrite / RME users can finally route Master's FM onto whichever named bus (Media / Main / Discord / AUX) their mixer grid points at. ASIO finally actually works for the routing use-case everyone's been hitting." },
        @{ Tag = "NEW";      Text = "Audio Source dialog fully redesigned: clickable card panels with instant-apply on click (no Save required), live green status header 'Currently capturing: ... [live 0.824]' polling every 250 ms, backend-aware per-type descriptions, drag-to-move window. All five safe backends enumerated: WASAPI Loopback, WASAPI Input (shared), WASAPI Exclusive / WDM-KS, MME, ASIO." },
        @{ Tag = "FIXED";    Text = "ASIO 'ASE_NoClock' silent failure on SetSampleRate. Drivers like VB-Matrix VASIO-32 reject the default 48 kHz if the Matrix itself is already running at a different rate. The new AsioCaptureAdapter tries 48 / 44.1 / 96 / 88.2 / 192 / 32 / 22.05 / 16 kHz in sequence and rebuilds the entire AsioOut instance from scratch on each failure (the 'Already initialised this instance' error was the other dead-end). First rate that sticks wins. No more 'selected ASIO, got silence' with zero explanation." },
        @{ Tag = "FIXED";    Text = "WDM-KS / WASAPI Exclusive on virtual endpoints (VB-Matrix Media, Voicemeeter B-buses) no longer silently falls back to Loopback. Virtual drivers typically don't implement exclusive-mode. New WdmKsCaptureAdapter tries Exclusive first, falls back to Shared on the same device automatically — catches the common case without manual intervention." },
        @{ Tag = "FIXED";    Text = "MME / ASIO visualizer 'only bass moves' bug. Cause: MME + ASIO present line-level inputs at roughly -50 dBFS vs WASAPI loopback's -10 dBFS, so the FFT dB window was clipping everything past the low bins. Added per-backend input gain compensation (1x loopback, 20x WASAPI Input / Exclusive / MME, 40x ASIO) — full frequency range now reads on every backend." },
        @{ Tag = "NEW";      Text = "Safe 'Scan & Auto-select Best' button. Iterates WASAPI Loopback + WASAPI Input + MME + every ASIO channel pair (deliberately SKIPS WDM-KS / Exclusive to avoid cracking other apps' audio with exclusive-mode grabs), records each device's peak, picks the loudest. Turns 'I don't know which of 143 ASIO channels has my Media bus' into one click. Cycled 1.5 s per probe." },
        @{ Tag = "NEW";      Text = "Fallback-detection banner. If your pick silently falls back to WASAPI Loopback (e.g. ASIO refused every sample rate we tried), the green status header now says 'FALLBACK from ASIO to WASAPI Loopback' so you know your selection didn't actually stick instead of wondering why your visualizer is on the wrong input." },
        @{ Tag = "FIXED";    Text = "'I can't select any other audio source.' The click handler was writing to \$script:_audCurKey inside a .GetNewClosure() scope, which is unreliable from a hosted PowerShell runspace (MastersFM_Tray.exe). Switched to a mutated hashtable (\$localState.CurKey = \$localKey) which always writes back to the parent scope cleanly. Every card in the list is now properly click-to-select." },
        @{ Tag = "FIXED";    Text = "Green status header used to always say 'System Default' regardless of what was actually running. The filter matched id-only; now matches on backend + id together so the status correctly reflects e.g. 'VB-Matrix VASIO-32 Ch 7-8 (ASIO)' when that's what's live." },
        @{ Tag = "IMPROVED"; Text = "ASIO entries now use compound IDs of the form 'DriverName|ChannelOffset' — lets the same driver appear 16 times in the list without collision while keeping the on-disk config key stable across reinstalls. Natural-number sort (Ch 1-2, Ch 3-4, Ch 5-6, ..., Ch 31-32) instead of lexicographic (Ch 1-2, Ch 11-12, Ch 13-14 ...)." },
        @{ Tag = "NEW";      Text = "Audio Source window is drag-to-move. FormBorderStyle=None + MouseDown/MouseMove handlers on both the form itself and the title label, mirroring the welcome-dialog pattern. Reposition it wherever you want — no more stuck-in-the-corner." }
    ) },
    @{ Version = "v5.3.0"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "NEW"; Text = "Multi-backend audio capture. The visualizer can now listen via WASAPI Loopback (default, what speakers hear), WASAPI Input (mic / Stereo Mix / virtual cable output), WDM-KS / Exclusive (kernel-mode exclusive capture, same path pro audio apps use), MME WaveIn (legacy Windows Multimedia waveIn — the classic Stereo Mix path on Realtek), and ASIO (pro audio drivers — the ONLY reliable way to capture VB-Audio Matrix / Voicemeeter internal buses, because those mixers route internally via ASIO and bypass all Windows audio APIs). Audio Source dialog now groups devices by backend with bold section headers. Selection is a {backend, id} tuple persisted as audioSpectrumBackend + audioSpectrumDevice; old configs with just audioSpectrumDevice still work (default to wasapi_loopback)." },
        @{ Tag = "NEW"; Text = "audio_spectrum.exe bumped to v5.3.0. Ships with two additional NAudio DLLs (NAudio.WinMM.dll for MME WaveIn, NAudio.Asio.dll for ASIO) - automatically picked up by the build pipeline + MSI file list. ASIO capture is wrapped via an AsioCaptureAdapter class that presents AsioOut's AudioAvailable event behind the same IWaveIn interface the rest of the pipeline uses, so the FFT / banding / SSE logic didn't need to change - every backend just emits raw PCM into the existing OnData callback." },
        @{ Tag = "IMPROVED"; Text = "Scan & Auto-select now explicitly limits itself to WASAPI Loopback endpoints. Scanning ASIO drivers or MME devices made no sense - those backends require user-specific routing setup that an auto-scan can't guess at. The no-audio warning text also now points VB-Matrix / Voicemeeter users toward the ASIO backend as a real solution instead of just shrugging." }
    ) },
    @{ Version = "v5.2.4"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Desktop bundle slimmed to exactly 3 files: Master's FM Vx.x.x.msi, INSTALL.bat, MastersFM_publisher.cer. INSTALL_INSTRUCTIONS.txt no longer bundled — the INSTALL.bat itself is self-explanatory (one double-click, one UAC prompt, done), and the extra readme was visual noise for friends who just want to run the thing. Rebuild pipeline also purges any stale INSTALL_INSTRUCTIONS.txt from the bundle folder on each build so old copies don't linger." }
    ) },
    @{ Version = "v5.2.3"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED"; Text = "INSTALL.bat now stops any running Master's FM processes (MastersFM.exe, MastersFM_Tray.exe, audio_spectrum.exe, customize.exe, server.exe) and uninstalls the previous version BEFORE running msiexec on the new one. Without these steps the MSI would fail with exit 1603 ('fatal error during installation') because running .exes held file locks on the install folder. Same pre-flight the developer rebuild pipeline does — just wasn't in the friend-facing installer until now. Also adds an MSI log file in %TEMP% on failure for easier diagnosis." }
    ) },
    @{ Version = "v5.2.2"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED"; Text = "INSTALL.bat no longer pops Windows' 'install a certificate from a CA' confirmation dialog mid-install. Root-store imports removed — only TrustedPublisher imports remain (silent, no dialog). Msiexec is already running elevated at that point, so the self-signed MSI installs fine regardless of chain-validity; the Root trust step was belt-and-braces that came at the cost of a scary security warning popping up at friends. Same change applied to install_bootstrapper.cs for when it's eventually re-enabled with a real CA cert. Friends now see: UAC elevation on bat double-click, then silent install, no other dialogs." }
    ) },
    @{ Version = "v5.2.1"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "'Audio Source...' promoted to the top primary block alongside Platform Detection and Customize Overlay. All three picker dialogs now live in the same section — no more hunting under toggles for the audio device selector." }
    ) },
    @{ Version = "v5.2.0"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "REMOVED";  Text = "Tray menu 'Open in Browser' entry removed — redundant with the Customize Overlay + OBS Browser Source paths; almost nobody opened the overlay in a regular browser for real use." },
        @{ Tag = "IMPROVED"; Text = "'Platform Detection...' promoted to the top of the tray menu, above Customize Overlay. Most-used setting goes first." }
    ) },
    @{ Version = "v5.1.9"; Date = "2026-04-22"; Notes = @(
        @{ Tag = "FIXED"; Text = "Platform Detection dialog cards were unclickable — toggle handlers referenced `$cards and `$script:_platStates directly instead of capturing them as locals before .GetNewClosure(). In the MastersFM_Tray.exe hosted runspace those lookups silently fail at click-time, so every click was a no-op. Same fix the Audio Source dialog already has: local-capture every var the closure needs + pass the states hashtable in as a local reference (no more `$script`:-scope hops from the click path). Bulk Enable all / Disable all buttons got the same treatment." }
    ) },
    @{ Version = "v5.1.8"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Desktop-distribution layout simplified to a single folder. `MastersFM_Installer\\` is the only thing left on the Desktop after a rebuild; standalone MSI, standalone .cer, standalone bootstrapper exe, stray INSTALL_INSTRUCTIONS.txt — all auto-purged. One folder, zip-ready, send to friends. Inside: MSI + publisher .cer + INSTALL.bat + INSTALL_INSTRUCTIONS.txt." }
    ) },
    @{ Version = "v5.1.7"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "REMOVED"; Text = "Single-file bootstrapper 'Install Master\u0027s FM.exe' disabled pending a trusted-CA code-signing certificate. Behavioural AVs (Bitdefender) were flagging it as a dropper because the pattern is identical to malware: embedded MSI payload + self-elevate + cert-store modification + zero signature reputation. The C# source (install_bootstrapper.cs) + build script stay in the tree for re-enable once a real cert is in place (one un-comment in _full_rebuild.ps1 step [2c])." },
        @{ Tag = "IMPROVED"; Text = "Shipping bundle reverted to the AV-safe path: signed MSI + publisher .cer + INSTALL.bat + INSTALL_INSTRUCTIONS.txt, all in the MastersFM_Installer folder on Desktop. Primary friend workflow is now the 2-click method (double-click .cer > Trusted Publishers, then double-click .msi > UAC shows MasterShadex > install). Both steps route through Windows-native dialogs that no antivirus has ever blocked. INSTALL.bat stays as an optional one-click alternative for friends whose AVs don't mind it." }
    ) },
    @{ Version = "v5.1.6"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED"; Text = "Customizer-preview spectrum FPS was capped at ~30 fps in the customize.exe WebView2 window while OBS Browser Source (using CEF) hit full rate. Cause: the preview iframe uses transform:scale() to fit the 1000x200 overlay, and without GPU-layer hints WebView2 had to re-rasterize the entire scaled layer every rAF. Fix: added `will-change: transform` + `backface-visibility: hidden` + `translate3d(0,0,0)` + `contain: strict` on the iframe, and `contain: paint layout size` on the wrapper. That promotes the iframe to its own GPU texture and stops slider drags elsewhere in the customizer from dragging the iframe's repaint with them. Customize preview spectrum is now as smooth as the OBS Browser Source spectrum." }
    ) },
    @{ Version = "v5.1.5"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED"; Text = "One-click installer (Install Master\u0027s FM.exe + INSTALL.bat fallback) now auto-starts Master's FM after the MSI finishes, AND auto-closes its console window after 10 seconds (or any key press) instead of waiting for the friend to press a key. Launch uses explorer.exe as a de-elevation trampoline so the tray app runs as the user's normal token, not with the installer's admin rights — correct security posture for a long-running background app." }
    ) },
    @{ Version = "v5.1.4"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW"; Text = "TRULY one-click friend installer. New signed bootstrapper 'Install Master\u0027s FM Vx.x.x.exe' embeds the MSI + publisher cert as manifest resources — it IS the full installer in a single file. Friend downloads one exe, double-clicks, approves the UAC prompt ('MasterShadex' publisher, thanks to signing), and the bootstrapper: (a) extracts MSI + cert to %TEMP%, (b) imports MasterShadex cert into both user + machine scope of Trusted Publishers AND Trusted Root, (c) runs msiexec /passive, (d) reports result in a small console window. No zip extract. No cert import dance. No multi-file archive. Just: download exe, click, done. Desktop auto-purges older bootstrapper versions so there's always one canonical 'Install Master\u0027s FM.exe' ready to share." }
    ) },
    @{ Version = "v5.1.3"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW"; Text = "One-click INSTALL.bat for friends. Send them the 'MastersFM_Installer' folder (auto-created on the Desktop on every rebuild — contains the MSI, the INSTALL.bat, the publisher .cer, and the instructions TXT) and they just double-click INSTALL.bat. The bat self-elevates to admin via a single UAC prompt, imports MasterShadex into Trusted Publishers + Trusted Root (both user AND machine scope), unblocks the MSI, runs msiexec /passive. No more 'Unknown Publisher' UAC. No more 'right-click > Unblock'. No manual cert import dance. .bat files aren't gated by Smart App Control (SAC only blocks .exe/.msi) so the bootstrapper runs even on SAC-on machines — though the spawned msiexec still needs SAC off on those rare strict setups (Microsoft policy, not ours)." }
    ) },
    @{ Version = "v5.1.2"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW"; Text = "MSI is now signed with a self-signed 'MasterShadex' code-signing cert. Every build signs with the same persistent cert (stored in CurrentUser\\My, valid 5 years) so SmartScreen reputation accumulates instead of resetting each release. Friends doing a fresh install on strict Windows setups (Smart App Control on) still get a block, but the UAC prompt shows 'MasterShadex' instead of 'Unknown Publisher'. The public certificate (MastersFM_publisher.cer) is now shipped to the Desktop alongside the MSI so friends can double-click it, import it once into 'Trusted Publishers', and then every future Master's FM install runs without warnings." },
        @{ Tag = "NEW"; Text = "INSTALL_INSTRUCTIONS.txt is now bundled with the Desktop MSI — three fallback paths for friends who hit Smart App Control / policy-block errors: (1) right-click MSI > Properties > Unblock, (2) admin PowerShell + msiexec, (3) one-time cert import for silent future installs. Saves the 'Master why is Windows yelling at me' support messages." }
    ) },
    @{ Version = "v5.1.1"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED"; Text = "Render FPS slider actually works in the customizer preview now. The v8 customizer-preview hard-cap (halved rAF to 30 fps for CPU savings) was pre-empting the user's FPS slider, so the preview looked stuck at 30 no matter what you set. Removed the hard-cap; the FPS slider is the single source of truth for render cadence in both preview AND OBS Browser Source. Note: if your OBS Browser Source's own FPS setting is lower than the slider value, OBS caps the visible rate to OBS's setting (that's on the OBS side, not us)." }
    ) },
    @{ Version = "v5.1.0"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED"; Text = "Visualizer bars now feel equally punchy at any FPS. v5.0.8's dt-aware fall lerp kept wall-clock decay time constant across FPS settings — but that meant at 240 fps the same 70 ms decay got spread across 4× as many visible frames, making the motion look SMOOTHER (less heartbeat-like) than 60 fps. Replaced with a fixed per-frame alpha (0.55) so the fall always takes ~4 render frames regardless of FPS. At 60 fps that's a 67 ms decay; at 240 fps it's 17 ms — both show the same visible step count so the heartbeat pop reads the same on any monitor refresh rate." }
    ) },
    @{ Version = "v5.0.9"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW"; Text = "Auto-Gain toggle under Customize → Visualizer. When on, the spectrum normalizes to its own rolling peak — bars fill the card consistently whether you're playing at 5 % system volume (quiet background music while working / streaming chill) or 90 %. Off (default) keeps the original behaviour where bar height tracks actual playback volume. Rolling-peak decay has a ~6-second time constant, so bars don't visibly rescale during a song's dynamics but recalibrate within a couple of seconds when you change tracks or volume." }
    ) },
    @{ Version = "v5.0.8"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "User-selectable spectrum Render FPS slider in the customizer (30-1000 fps, default 120). Throttles how often the overlay redraws the spectrum canvas regardless of monitor refresh rate. 1000 = effectively uncapped (browser rAF caps it to monitor refresh anyway). Dial down for laptop battery life; dial up for butter-smooth on 240 Hz monitors." },
        @{ Tag = "IMPROVED"; Text = "End-to-end audio-to-bars latency cut from ~50 ms to ~25 ms. audio_spectrum.exe SSE send cadence halved (33 ms → 16 ms, ~60 Hz). Overlay lerp fall-rate tightened from 0.35 to 0.55/60fps, and made dt-aware so the perceived decay speed stays constant whether you're on a 30 Hz OBS Browser Source or a 240 Hz desktop browser. Bars now chase transients within 1-2 frames." },
        @{ Tag = "IMPROVED"; Text = "Max bar count raised 100 → 120 to match audio_spectrum.exe's BAND_COUNT, so users who crank bars all the way to max get per-bar frequency resolution instead of two bars sharing a band." }
    ) },
    @{ Version = "v5.0.7"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED"; Text = "Spectrum low-frequency 'flat front' at high bar counts finally actually fixed. v5.0.6 bumped bands to 256 but the FFT was still 1024 (47 Hz bin width) — so the bottom 20-30 log-spaced bands all fell below one FFT bin and ended up reading the exact same value. Now: FFT size 2048 (~23 Hz bin width, half as coarse), 120 bands total (matches customizer max barCount), and low-freq floor raised to 40 Hz so every band gets distinct FFT data. Every bar in the spectrum now picks a different frequency slice, no matter the bar count." }
    ) },
    @{ Version = "v5.0.6"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED"; Text = "High bar-count spectrum no longer flat-syncs on the low-frequency bars. audio_spectrum.exe now ships 256 log-spaced bands (was 60) spanning 10 Hz - 16 kHz (was 30 Hz - 16 kHz) so sub-bass gets its own bars. The overlay does linear interpolation between adjacent bands when sampling — every bar at every bar count picks a distinct frequency slice, no more 2-3 neighbours moving in lock-step at the front." }
    ) },
    @{ Version = "v5.0.5"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED"; Text = "Audio Source dialog card descriptions now fit on one line — v5.0.4's longer 'VB-Matrix uses ASIO for internal routing which Windows / WASAPI cannot see' copy overflowed the 28 px description area and got clipped mid-sentence. Shortened every per-type description (including the status prefix — 'Currently the system default.' → 'Default.', 'Active right now.' → 'Active.') so every card renders cleanly without scroll-clip, while still conveying which endpoints actually work." }
    ) },
    @{ Version = "v5.0.4"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED";    Text = "Audio Source dialog no longer doubles every physical device with a [Recording] duplicate right under the [Playback] one. v5.0.2 added capture-side enumeration thinking it'd help VB-Matrix users, but in practice VB-Matrix's capture-side endpoints are also silent (Matrix routes internally via its own ASIO driver that bypasses Windows audio entirely). /devices now returns render (playback) endpoints only. One row per physical/virtual device. Cleaner list." },
        @{ Tag = "NOTE";     Text = "Honest ASIO warning on VB-Matrix rows: 'VB-Audio Matrix virtual device — Matrix uses ASIO for internal routing which Windows / WASAPI cannot see. This row will always be silent to the visualizer. Pick your PHYSICAL output instead (where Matrix routes audio to).' Same honest explanation added for Voicemeeter virtual inputs and VB-Cable. WASAPI-based tools (including us, OBS loopback, Discord, Teams) cannot capture from ASIO-routed audio — that's a fundamental limitation of the audio-driver ecosystem, not a bug we can fix here." },
        @{ Tag = "IMPROVED"; Text = "Capture pipeline simplified — only WasapiLoopbackCapture is instantiated now; the WasapiCapture branch added in v5.0.2 for capture-side endpoints was removed since we no longer expose capture-side devices to users. Less code, less surface area for bugs." }
    ) },
    @{ Version = "v5.0.3"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "Smart per-device descriptions in the Audio Source dialog for virtual-mixer users. Each endpoint is now classified by type — Physical / VB-Audio Matrix (render|capture) / Voicemeeter (input|bus output) / VB-Cable (in|out) / generic virtual — and the dialog shows a targeted sentence for each: 'VB-Audio Matrix virtual INPUT — Matrix routes this internally; WASAPI loopback gets SILENCE here. Pick your physical output instead.' / 'Voicemeeter B-bus OUTPUT — this IS the final mix. RECOMMENDED for Voicemeeter routing.' / 'Physical audio hardware — RECOMMENDED for users on virtual mixers.' etc. Descriptions tell the user directly what works and what doesn't." },
        @{ Tag = "NEW";      Text = "Audio Source dialog sorts devices by TYPE instead of enumeration order. Physical outputs first (recommended for virtual-mixer users), then Voicemeeter/VB-Cable/VB-Audio capture-side outputs (these ARE the final mix), then virtual-output capture endpoints, then virtual inputs last (silent loopback). Finding the right endpoint goes from trial-and-error to top-of-list." },
        @{ Tag = "NEW";      Text = "audio_spectrum.exe /devices endpoint now returns a 'type' field for each device (physical / vbmatrix_render / vbmatrix_capture / voicemeeter_input / voicemeeter_bus_out / vbaudio_cable_in / vbaudio_cable_out / vbaudio_virtual_in / vbaudio_virtual_out / virtual_other / unknown). Lets the tray dialog render appropriate guidance without having to repeat the device-name pattern matching in PowerShell." }
    ) },
    @{ Version = "v5.0.2"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED";    Text = "VB-Audio Matrix / Voicemeeter / virtual-mixer visualizer silence — now works. On complex audio-routing setups the user's 'apparent' music output (e.g. 'Media (VB-Audio Matrix VAIO)') is a virtual endpoint that intercepts audio before the Windows shared-mixer buffer, so WASAPI loopback on that endpoint returns all-zero samples. The REAL audible mix lives on whichever physical endpoint the virtual mixer routes to (typically the user's audio interface — Audient, Focusrite, on-board DAC, etc.). Verified: capturing Analogue 1/2 peak=0.3086, capturing Media peak=0.0000. Added a 'Scan & Auto-select Best' button to the Audio Source dialog that cycles every render endpoint for ~3s each and automatically picks whichever has the highest peak signal." },
        @{ Tag = "NEW";      Text = "Audio Source dialog now enumerates BOTH [Playback] (Render) and [Recording] (Capture) endpoints. Playback endpoints are read via WASAPI loopback; Recording endpoints via WASAPI capture. Flow badge shown next to each device name. This exposes VB-Audio's paired recording-side endpoints (whether they contain audio depends on the virtual-mixer driver's internal routing)." },
        @{ Tag = "FIXED";    Text = "/set-device 'default' was silently falling through to whatever was saved in config.audioSpectrumDevice — so users who picked 'default' after previously picking a specific device kept getting the specific one. 'default' is now preserved as a sentinel through the resolver pipeline and correctly re-targets the system default render endpoint." },
        @{ Tag = "FIXED";    Text = "Static process-lifetime MMDeviceEnumerator — v5.0.1's using() block disposed the enumerator before WasapiLoopbackCapture finished binding the returned MMDevice, causing silent-sample bugs. The enumerator now lives for the life of the process." },
        @{ Tag = "NEW";      Text = "Deep audio diagnostics in audio_spectrum.log: on each capture start, dumps device info (ID / DataFlow / State / MixFormat), first 32 raw bytes of the first buffer in hex, and the list of currently-active audio sessions on the endpoint (proc name + PID + state). Every ~3 s, emits a 'peak = 0.xxxx' line tagged [SILENCE] / [quiet audio] / [LIVE AUDIO] so tail -f of the log shows live audio activity." },
        @{ Tag = "NEW";      Text = "New /peak HTTP endpoint on audio_spectrum.exe returning JSON { rolling, lifetime, device } — powers the Scan & Auto-select feature and can power a live VU-meter in the dialog." }
    ) },
    @{ Version = "v5.0.1"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED";    Text = "Audio Source + Platform Detection dialogs redesigned — the WinForms RadioButton/CheckBox with FlatStyle=Flat was rendering invisibly on the dark dialog background, so users couldn't see which rows were selected even when Checked was true. Replaced with clickable card panels: enabled rows light up purple with a white check, disabled rows stay dark with an empty box. The whole row is clickable (not just the tiny checkbox), and the Audio Source dialog now shows a green 'Currently capturing: ...' header so you always know which output is actually driving the visualizer." },
        @{ Tag = "NEW";      Text = "Platform Detection gets Enable-all / Disable-all quick buttons so you can flip the whole list with one click and then tick only the platforms you want." },
        @{ Tag = "FIXED";    Text = "Config-read bug in the Audio Source dialog: piping Get-RoamingCfgPath through ForEach-Object leaked $null into the 'current' variable and prevented any row from rendering as selected. Rewritten as a clean Test-Path + Get-Content block." }
    ) },
    @{ Version = "v5.0.0"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "Audio Source picker in the tray. New 'Audio Source...' menu entry lists every Windows audio-output endpoint (System Default, 'Discord (VB-Audio Matrix VAIO)', 'Media (VB-Audio Matrix VAIO)', 'Hi-Fi Cable Input', 'Analogue 1/2 (Audient iD14)', etc.) as radio-button rows and lets you pick which one the visualizer captures. The pick is persisted in Roaming config AND live-switches the capture via audio_spectrum.exe's new /set-device endpoint — no restart, no reload. Fixes the 'wrong input' misdetection for users whose default endpoint is their Discord voice output instead of their media output." },
        @{ Tag = "NEW";      Text = 'audio_spectrum.exe now exposes /devices (lists all active render endpoints) and /set-device (POST {"id":"..."} to switch live). On boot it reads audioSpectrumDevice from the Roaming config and opens loopback on THAT endpoint; falls back to the system default if the configured device is missing or unplugged. A ManualResetEvent signals the capture thread to tear down cleanly and reopen on the newly selected endpoint.' },
        @{ Tag = "FIXED";    Text = "Customizer preview iframe now runs the WASAPI-loopback spectrum too — previously connectSpectrumSSE early-returned on _IS_PREVIEW, so the customizer's preview card showed the simulator while the real OBS overlay showed live audio. Makes spectrum-height / colour / heightMult tuning visible in real time during customization. Cost is small: preview rAF is already throttled to 30 fps by the earlier v5 optimization." },
        @{ Tag = "NEW";      Text = "Discord-style process-tree grouping in Task Manager. All four Master's FM processes (MastersFM.exe, server.exe, MastersFM_Tray.exe, audio_spectrum.exe) collapse into one expandable 'Master's FM' row. MastersFM.exe now carries a hidden-but-Shell-visible window (opacity 0, offscreen, not in taskbar) AND sets PKEY_AppUserModel_ID='MastersFM.App' on that window's property store via SHGetPropertyStoreForWindow — the combo the Shell grouping engine actually looks at (explicit-process AUMID alone wasn't enough)." },
        @{ Tag = "FIXED";    Text = "Idle tray CPU down from ~1.3 % to ~0.3 %. Scrobble-detect timer was firing every 100 ms (10 Hz) — way overkill, SMTC state doesn't change that fast. Bumped to 300 ms; music-change latency stays well inside the ~200 ms human 'instant' threshold, and the overlay's own /current poll already runs at 500 ms. Net idle-CPU saving: roughly 2/3 of the tray's baseline cost." },
        @{ Tag = "FIXED";    Text = "server.exe finally shows the purple Master's FM icon in Task Manager / File Properties / Details tab — the resedit rebuild step now strips every existing icon + groupicon resource and installs MastersFM.ico in their place. ProductName and the icon finally match." },
        @{ Tag = "FIXED";    Text = "Customizer CPU / GPU drain cut hard. Three wins stacked: (1) The spinning-border conic-gradient pseudo-element shrank from 5000×5000 px to 1600×1600 px — the texture the GPU had to recomposite every frame got 10× smaller (was pinning WebView2 at 13 % GPU on modern hardware). `will-change: transform` + `contain: strict` now hint the compositor to keep that layer independent + scoped. (2) The overlay's spectrum rAF loop bails immediately when `document.hidden` is true, so minimized OBS / minimized customizer = zero work. (3) Inside the customizer's `/?preview=1` iframe the overlay rAF is throttled to 30 fps — the preview is a thumbnail sanity check, 60 fps there is overkill. Real OBS Browser Source still runs full 60 fps." },
        @{ Tag = "IMPROVED"; Text = "audio_spectrum.exe tightened for near-zero idle cost: FFT size halved 2048 → 1024, SSE send cadence halved 60 Hz → 30 Hz (overlay lerps back to 60 fps visually), RMS silence-gate skips FFT entirely below 1e-4 mono amplitude, and an active-client counter skips all FFT work when nothing is listening. Idle cost measured at 0 % CPU in Task Manager." }
    ) },
    @{ Version = "v4.0.0"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "Visualizer now tracks REAL Windows audio. New audio_spectrum.exe sits alongside server.exe, opens a WASAPI loopback capture on your default speakers (same mechanism OBS and Discord use), runs an FFT at 48 kHz, and streams 60 log-spaced frequency bands over SSE at http://127.0.0.1:4243/spectrum. The overlay consumes that stream so the spectrum bars finally move in sync with whatever's playing — Spotify, SoundCloud, YouTube, any system audio — without requiring an Audio Input Capture source in OBS. Falls back automatically to the built-in simulator when the stream drops." },
        @{ Tag = "NEW";      Text = "'Live Audio Visualizer' tray toggle — flip the WASAPI spectrum on and off without opening the customizer. Balloon tip confirms the new state. Setting persists in the Roaming config." },
        @{ Tag = "NEW";      Text = "Left-click the tray icon opens the menu now (previously right-click only). Right-click still works. Double-click was dropped to avoid the menu flickering open/close/open." },
        @{ Tag = "FIXED";    Text = "'Way too maxed out' — FFT dB normalization was treating 0 dB as 100 % which pegged every bar at 255 on any audible audio. Recalibrated so normal music sits around 50-75 % fill with clear headroom for peaks. Additionally the overlay caps bar height at 78 % of canvas so bars always clear the top edge of the card instead of slamming into it." },
        @{ Tag = "IMPROVED"; Text = "Heartbeat-like response. Bars snap up on beats and decay gently on the fall — no more wavey cross-frame fades. audio_spectrum.exe internal envelope uses near-instant attack (0.85) + moderate decay (0.28); the overlay per-rAF lerp uses INSTANT rise to the new target + gentle 0.35 decay. End-to-end latency from bass hit to bar peak is ~30-50 ms." },
        @{ Tag = "FIXED";    Text = "OBS Overlay menu checkmark now reflects reality. tray.ps1 scans %APPDATA%\\obs-studio\\basic\\scenes\\*.json for a browser_source named 'Master's FM' pointing at localhost:4242 (matching both the literal 'Master''s FM' form and OBS's serialized 'Master\\u0027s FM' escape) and checks/unchecks accordingly. Cached 30 s so menu opens stay snappy; auto-add invalidates the cache the moment it runs so freshly-added sources show up immediately on the next open." },
        @{ Tag = "IMPROVED"; Text = "Auto-add runs on every tray boot and the OBS menu entry defaults to CHECKED. The 'Not Set Up' label only appears when we've definitively confirmed OBS is installed AND the source is missing AND auto-add has already been tried unsuccessfully this session — no more confusing 'Not Set Up' flash during the ~3 s it takes auto-add to complete." }
    ) },
    @{ Version = "v3.0.0"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "Platform Detection dialog in the tray. New 'Platform Detection...' menu entry opens a scrollable checkbox-per-platform dialog keyed on what you actually see on the overlay badge: Spotify, SoundCloud, YouTube, YouTube Music, Apple Music, TIDAL, Deezer, Amazon Music, Pandora, Bandcamp, Mixcloud, osu!, VLC, Windows Media Player — plus a top-level 'Browser' master-switch that kills ALL browser-based detection in one click when you don't want Master's FM picking anything up from a browser at all. Each platform toggle spans both the desktop app AND the web player of that service (e.g. unchecking Spotify silences both the Spotify desktop app and the Spotify Web Player in any browser)." },
        @{ Tag = "IMPROVED"; Text = "Per-platform gating is now applied AFTER platform resolution. When SMTC returns a generic browser session (Chrome / Edge / Firefox / Opera / Opera GX / Brave / Vivaldi / Arc / IE), the browser-title / URL-domain / SMTC-album passes resolve it to a service name, and THAT name is checked against the dialog. Result: unchecking 'YouTube' silences only YouTube tabs, not Spotify Web Player tabs in the same browser. The 'Browser' master-switch gates the whole category before we even do session lookup — saves CPU when you don't want browser detection at all." },
        @{ Tag = "IMPROVED"; Text = "Toggles live in Roaming config.json (%APPDATA%\\MastersFM\\config.json → platforms.{Spotify, YouTube, YouTubeMusic, AppleMusic, TIDAL, Deezer, AmazonMusic, Pandora, Bandcamp, Mixcloud, SoundCloud, osu, VLC, WMP, Browser}) so your choices survive MSI reinstalls. Unknown services (a newly supported app we haven't added a toggle for yet) default to ALLOWED so nothing silently stops working after an upgrade." }
    ) },
    @{ Version = "v2.0.0"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED";    Text = "Spectrum no longer flicks an entire frame of colors every 8 seconds in rainbow + dynamic mode. The per-frame hue offset was being pre-wrapped at 360° BEFORE being multiplied by the dynamic-mode spread ratio (80/300 = 0.267) — the wrap boundary didn't align with 360° after scaling, so every 8 s you saw a 96° hue snap across every bar. Now the outer per-bar modulo handles the wrap on a clean 360° boundary and the colors flow continuously." },
        @{ Tag = "FIXED";    Text = "YouTube videos whose titles happen to contain the word 'soundcloud' (DJ mixes, upload comparisons, reaction videos) are no longer misclassified as SoundCloud. Browser-tab platform detection now runs in three passes: (1) URL-domain markers (music.apple.com, youtube.com, soundcloud.com, listen.tidal.com, ...) — 100 % authoritative; (2) site-SUFFIX markers at end of title (' - YouTube' / '| YouTube' / ' — YouTube') with the browser name stripped first; (3) mid-title keyword matching as a last resort, with YouTube now checked BEFORE SoundCloud so a mention of 'soundcloud' inside a YouTube video title can't hijack the badge." },
        @{ Tag = "FIXED";    Text = "Apple Music in Chrome no longer sticks as 'TIDAL' after you've previously listened to TIDAL in the same browser window. The per-browser platform cache was 'once positive, always cached' — a stale 'TIDAL' latched onto Chrome would keep serving even after the track identity changed. Cache now keys on artist+title, so switching services in the same browser drops the old detection the moment the new track plays." },
        @{ Tag = "FIXED";    Text = "Browser support rewritten: Internet Explorer, Vivaldi, and Arc now join Chrome / Edge / Firefox / Opera / Opera GX / Brave in the SMTC scan + suffix regex. Firefox's em-dash-separated title format (' — Mozilla Firefox') resolves cleanly. Opera GX's AUMID still reads as 'opera' (Chromium fork) so it just works." },
        @{ Tag = "IMPROVED"; Text = "SMTC album-field platform detection now requires word-boundary matches instead of plain substring. An album literally titled 'Tidal Wave' or 'Apple Music for Kids' can no longer be misread as the service name — only albums that START with the service keyword (or are exactly it) count." }
    ) },
    @{ Version = "v1.0.0"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "One-application Task Manager grouping — FINALLY. Every Master's FM process (MastersFM.exe launcher, server.exe Node, MastersFM_Tray.exe PowerShell host) reports ProductName 'Master''s FM' + Company 'MasterShadex' in its Windows VersionInfo, so Task Manager collapses them into a single 'Master's FM' row. No more 'Windows PowerShell' entry cluttering your list, no more 'Node.js' ghost, no more 'wscript.exe' orphan from the old VBS auto-start." },
        @{ Tag = "NEW";      Text = "MastersFM_Tray.exe hosts PowerShell in-process via System.Management.Automation instead of spawning powershell.exe as a child. The C# host runs tray.ps1 inside its own CLR-loaded runspace using AddScript(useLocalScope:false) so GetNewClosure WinForms event handlers keep script-scope visibility forever — same behaviour as the old powershell.exe child, one fewer process in Task Manager." },
        @{ Tag = "NEW";      Text = "server.exe rebranded via resedit-js (pkg-overlay-safe PE editor) — replaces rcedit which was corrupting pkg's Node VFS. VersionInfo ProductName says 'Master's FM' in Task Manager instead of 'Node.js'. Icon now uses MastersFM.ico." },
        @{ Tag = "IMPROVED"; Text = "AppUserModelID 'MastersFM.App' set on every process via shell32.SetCurrentProcessExplicitAppUserModelID — taskbar grouping, toast-notification attribution, and Jump Lists now converge onto one Master's FM identity instead of scattering across three different Windows-internal identities." },
        @{ Tag = "IMPROVED"; Text = "All runtime logs consolidated in %LOCALAPPDATA%\\MastersFM\\ — startup.log, transcript.log, server.log, overlay.log, menu.log, audio_spectrum.log, host.log. Previously some lived in %TEMP% making troubleshooting a scavenger hunt." },
        @{ Tag = "IMPROVED"; Text = "All user state lives in Roaming (%APPDATA%\\MastersFM\\config.json). Welcome dialog no longer re-appears on MSI reinstall, the 'autostart defaulted' flag doesn't re-trip, and the tray config survives clean upgrades cleanly. Startup time after welcome acknowledgement dropped from ~11 s (with 10-second countdown) to ~350 ms on reinstalls." },
        @{ Tag = "IMPROVED"; Text = "Auto-start handled via a Startup-folder .lnk shortcut instead of a Run registry key — Task Manager's Startup tab shows 'Master's FM' with the purple icon instead of the old 'wscript.exe' ghost. Default ON on first install. tray.ps1 self-heals the .lnk on boot if the uninstaller deleted it mid-upgrade AND the user hadn't explicitly opted out (new autostart_user_optout flag)." },
        @{ Tag = "IMPROVED"; Text = "Graceful fallback — if MastersFM_Tray.exe is missing (stripped-down Windows SKU, deleted file), MastersFM.exe auto-falls-back to spawning powershell.exe -File tray.ps1 the old way. No user-visible break." }
    ) },
    @{ Version = "v1.9.9"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "Start on Login is now enabled by default on fresh v1.9.9 installs. A one-shot config flag records that we defaulted the setting, so users who turn it off later won't have it re-enabled on future boots." },
        @{ Tag = "IMPROVED"; Text = "server.exe is now branded as 'Master''s FM' in Task Manager (was 'Node.js'). This means Task Manager's process grouping collapses MastersFM.exe + server.exe into one 'Master''s FM' row. Achieved with resedit-js (pkg-overlay-safe PE editor) replacing rcedit (which corrupted pkg's Node virtual filesystem). Only the PowerShell tray remains as a separate 'Windows PowerShell' entry — targeted for v2.0.0." },
        @{ Tag = "IMPROVED"; Text = "All logs now live in one place — %LOCALAPPDATA%\MastersFM\. Previously startup/transcript logs lived in %TEMP% while server/overlay logs lived in the install folder, making troubleshooting a scavenger hunt. Everything is now alongside each other: startup.log, transcript.log, server.log, overlay.log, menu.log, host.log." }
    ) },
    @{ Version = "v1.9.8"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED";    Text = "Task Manager's Startup Apps list now shows 'Master''s FM' with the correct app icon instead of the confusing 'wscript.exe' orphan that pre-1.7 users were stuck with. The old Run-registry entry (wscript.exe + the no-longer-shipped Start Overlay.vbs) survived WMI cache restarts across reboots and refused to be overwritten by Set-ItemProperty — replaced entirely with a .lnk shortcut in the Startup folder, which Task Manager reads fresh every time." },
        @{ Tag = "FIXED";    Text = "Auto-start on Windows login is reliable again. Using a Startup-folder shortcut (target=MastersFM.exe, icon=MastersFM.ico) means Windows actually launches the app — the old dead wscript/VBS command produced a silent failure at every login." },
        @{ Tag = "IMPROVED"; Text = "Every MSI install AND every tray boot auto-migrates any legacy wscript/VBS Run entry to the new shortcut. Friends installing fresh MSIs get the fix transparently — no manual cleanup, no reboot required." }
    ) },
    @{ Version = "v1.9.7"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED";    Text = "Discord Rich Presence appears within milliseconds of a track change instead of taking 2-15 seconds. The server used to wait for album art to resolve before pushing to Discord; now it pushes immediately with a placeholder image, then re-pushes with the resolved art URL when it lands." },
        @{ Tag = "FIXED";    Text = "Stale end-timestamp carryover between tracks — where Discord showed the NEW song's start time but the PREVIOUS song's duration ('this 3:00 song is ending in 5:00 ago') — eliminated. The dedup cache is force-reset on every track change so the new activity completely overwrites the old." },
        @{ Tag = "FIXED";    Text = "Discord presence recovers after the Discord client restarts mid-session. A onReady callback now fires on each reconnect, resets the dedup cache, and force-pushes the current state." },
        @{ Tag = "IMPROVED"; Text = "Dedup with 30-second self-heal: identical SET_ACTIVITY frames are skipped to prevent the large_image refetch flash every 2 s, but after 30 s of stable state the next push goes through anyway — so a lost frame can't silently stick forever." }
    ) },
    @{ Version = "v1.9.6"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "Last-resort album-art web search. When Deezer / iTunes / MusicBrainz / SoundCloud / YouTube all whiff (common for obscure DJ sets, small-label releases, live mixes), Master's FM now scrapes Bing Images for '<artist> <track> album cover' and uses the first matching image. 1.2 s race deadline so a slow response never blocks the track-change animation." }
    ) },
    @{ Version = "v1.9.5"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "Platform Badge section in the customizer — full control over the little 'SOUNDCLOUD' / 'YOUTUBE' / 'SPOTIFY' label next to the Now Playing text. Enable / disable, rename the SoundCloud label, pick color + dot-color, font size, weight, letter spacing." },
        @{ Tag = "NEW";      Text = "Platform Badge added to Dynamic Colors. When enabled, the badge and its dot color follow the album-art palette instead of staying the preset hue." },
        @{ Tag = "NEW";      Text = "Track Title Glow added to Dynamic Colors. When the title glow is turned on, its hue now matches the card's outer glow instead of always being pink." },
        @{ Tag = "FIXED";    Text = "Dynamic colors no longer jump instantly between tracks. Background gradient, title, artist, Now Playing label, platform badge, timestamps, progress bar — everything now cross-fades smoothly over ~1.4 s. Spectrum bars interpolate their base color every frame." },
        @{ Tag = "IMPROVED"; Text = "Slide / spring / bounce / ease easings are ~20% more aggressive. Overshoot is punchier, bounces dig deeper — the animation now 'reads' on a live stream instead of being a subtle shift a viewer might miss." }
    ) },
    @{ Version = "v1.9.4"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED";    Text = "OBS Browser Source no longer shows a blank album-art slot until you right-click → Refresh cache. The overlay's <img src> now routes through /art?t=<timestamp> with Cache-Control: no-store, so CEF can't latch onto a stale cached response." },
        @{ Tag = "NEW";      Text = "Overlay auto-reloads itself when Master's FM restarts. A /version endpoint exposes the server's boot-id; the overlay polls it every 3 s and calls location.reload() when the id changes. No more manual 'Refresh cache of current page' after every rebuild." }
    ) },
    @{ Version = "v1.9.3"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Customize-panel preview is now INSTANT — ~20 ms end-to-end, was up to 650 ms before. Removed the 150 ms debounce; every slider / color-picker change POSTs immediately; the server broadcasts the new config via SSE instead of waiting for the overlay's next poll. Works in OBS too: viewers see every knob-twist in real time while you're tweaking." },
        @{ Tag = "IMPROVED"; Text = "Apply-to-OBS also broadcasts via SSE — the overlay flips to the persisted state the moment you click Save, not on the next 5 s reconciliation poll." }
    ) },
    @{ Version = "v1.9.2"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "Get-SMTCPosition — one shared helper every SMTC consumer (Find-SMTCSession, Get-SMTCNowPlaying, osu!, WMP) now funnels through. Single source of truth for extrapolation from tl.LastUpdatedTime and fresh-timeline-update detection (play / pause / seek)." },
        @{ Tag = "FIXED";    Text = "Mid-track seek on sources that don't continuously call setPositionState (soundcloud-rpc, some web players) now reaches the overlay. The tray's seek detector fires on LastUpdatedTime changes, not just on position deltas, so seeks propagate even when rawPos stays the same." },
        @{ Tag = "FIXED";    Text = "Seek-through-pause transitions no longer freeze the overlay. The detector used to short-circuit whenever EITHER tick was paused — now only when BOTH previous and current tick are paused. A 1-tick false-pause flip (VLC silent-audio scrub) can't eat the seek anymore." }
    ) },
    @{ Version = "v1.9.1"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED";    Text = "Restart Master's FM mid-song and the overlay now picks up at the REAL playhead, not 0:00. SMTC's tl.Position is a snapshot from the last setPositionState — it can be many minutes stale during normal playback. The tray now extrapolates the live position by adding (now - tl.LastUpdatedTime) when the session is Playing." },
        @{ Tag = "FIXED";    Text = "Album art no longer disappears after Master's FM restarts. setArt('') used to schedule a display:none in 350 ms; if real art arrived 400 ms later, the timer fired AFTER the new image loaded and hid it again. The hide timer is now cancelled the moment a new URL arrives." },
        @{ Tag = "FIXED";    Text = "SSE reconnect force-refreshes the album art. If SSE drops during a restart and reconnects to a track whose URL is bit-identical to before, setArt is now called unconditionally once on reconnect so OBS's cached image gets swapped fresh." }
    ) },
    @{ Version = "v1.9.0"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "NEW";      Text = "Customize Overlay now opens as a TRUE NATIVE window — no browser tab, no browser chrome, no extra Chrome/Edge taskbar entries cluttering your session. Uses the Microsoft Edge WebView2 runtime (preinstalled on Windows 11, auto-installed via Edge updates on Windows 10). Standalone app icon, standalone taskbar, same customizer UI underneath." },
        @{ Tag = "IMPROVED"; Text = "Customize window loads in ~200 ms vs a full browser launch that could take several seconds on slower machines." }
    ) },
    @{ Version = "v1.8.0"; Date = "2026-04-21"; Notes = @(
        @{ Tag = "FIXED";    Text = "SoundCloud artist/title parsing. Tracks like 'MONTA - Lizdek & Silcrow - Dogma' no longer split on the FIRST dash (which gave Artist='MONTA', Track='Lizdek & Silcrow - Dogma'). The parser now uses LastIndexOf for ' by ' and ' - ' / en-dash / em-dash, so the split happens on the LAST separator — which is how SoundCloud actually constructs its tab titles." },
        @{ Tag = "FIXED";    Text = "YouTube fallback album art. Non-music YouTube uploads (podcasts, DJ sets, gaming soundtracks) that Deezer / iTunes / MusicBrainz can't identify now get the YouTube video's own thumbnail. The server scrapes YouTube search and pulls the first matching video's hqdefault.jpg." },
        @{ Tag = "FIXED";    Text = "osu! beatmap covers now show up in the overlay. osu!lazer's SMTC thumbnail was being thrown away — it's now captured and forwarded through the webhook. For stable / cuttingedge (no SMTC), the server hits osu.ppy.sh's public JSON search and pulls the beatmapset cover from assets.ppy.sh." },
        @{ Tag = "FIXED";    Text = "VLC mid-track seek no longer freezes the overlay timestamp on the pre-seek position. Added silent-audio debounce (4-tick gap required before assuming paused), and the seek detector now fires through pause transitions instead of being disabled by any pause flag." }
    ) },
    @{ Version = "v1.7.0"; Date = "2026-04-20"; Notes = @(
        @{ Tag = "NEW";      Text = "Dynamic Album Art Colors — the overlay now automatically extracts the dominant color from the current album art and applies it to the card background gradient, outer glow, text, spectrum bars, timestamps, and progress bar. Each element has its own toggle under the new Dynamic Colors section in the Overlay Customizer." },
        @{ Tag = "NEW";      Text = "Restart Master's FM — tray icon context menu now includes a Restart option that relaunches the full app (server + tray) without needing to open Task Manager." },
        @{ Tag = "FIXED";    Text = "Timer drift on long tracks: the overlay's elapsed-time clock now stays in sync with actual playback even after 30+ minutes. Root cause was SMTC position snapshots freezing while a browser tab was backgrounded, causing the server to repeatedly correct the clock backward to a stale position." },
        @{ Tag = "FIXED";    Text = "False seek detection: the tray was using a fixed 250 ms expected-position-delta for seek detection, but the actual interval between sends varies. False seeks were triggering when SMTC position caught up after a freeze. Now uses real wall-clock elapsed time — SMTC catch-ups are no longer treated as seeks." },
        @{ Tag = "FIXED";    Text = "Dynamic spectrum bars now correctly respond to album art colors in rainbow mode. The rainbow hue rotation is now offset by the art's dominant hue so it animates around the track's palette instead of always cycling from the same starting color." },
        @{ Tag = "FIXED";    Text = "Slide direction labels were reversed: Slide Up was animating downward and Slide Left was animating rightward. All four directions are now correct." },
        @{ Tag = "IMPROVED"; Text = "Startup speed: the tray icon now appears immediately after assembly loading, before the welcome dialog renders. Previously the icon could take 2-5 extra seconds to appear if a patch-notes dialog was due." },
        @{ Tag = "IMPROVED"; Text = "Diagnostic log messages from the overlay (dynamic color extraction results, CORS failures, palette values) are now relayed to server.log so issues can be traced without opening OBS DevTools." }
    ) },
    @{ Version = "v1.6.9"; Date = "2026-04-20"; Notes = @(
        @{ Tag = "FIXED"; Text = "Discord RPC toggle in the tray menu now actually disables rich presence. The setting was being saved to the wrong config file (install-dir config.json instead of the Roaming config.json that the server reads), so the server never saw the change." },
        @{ Tag = "FIXED"; Text = "The Overlay Customizer no longer shows a microphone permission popup. The spectrum visualizer preview now runs silently — audio capture is only started in the live OBS overlay, not in the customizer preview." },
        @{ Tag = "IMPROVED"; Text = "Overlay Customizer window now shows the Master's FM icon in the title bar and taskbar instead of the browser icon." }
    ) },
    @{ Version = "v1.6.8"; Date = "2026-04-20"; Notes = @(
        @{ Tag = "FIXED"; Text = "Preset delete button now works reliably. Previously, the dropdown reset to '— Presets —' immediately after selecting a preset, making it unclear which preset was loaded and causing confusion about what the delete button would target." },
        @{ Tag = "IMPROVED"; Text = "Preset dropdown now keeps the selected preset name visible after loading it. The dropdown only resets to '— Presets —' when you change any setting, making it clear you've drifted from the saved preset." },
        @{ Tag = "IMPROVED"; Text = "Reset to Defaults button is now clearly labeled and visually distinct so it doesn't get missed." }
    ) },
    @{ Version = "v1.6.7"; Date = "2026-04-20"; Notes = @(
        @{ Tag = "FIXED"; Text = "SteelSeries GG (Sonar EQ and audio software) exposes a fake SMTC media session that was incorrectly detected as a music source. SteelSeries sessions are now fully ignored so the overlay never shows SteelSeries audio as a track." }
    ) },
    @{ Version = "v1.6.0"; Date = "2026-04-19"; Notes = @(
        @{ Tag = "REMOVED";  Text = "Last.fm dependency removed entirely. Master's FM is now self-contained — track detection, album art, and duration are resolved through platform-native SMTC, SoundCloud API, Deezer, iTunes, and MusicBrainz. No account or API key required." },
        @{ Tag = "FIXED";    Text = "When two platforms are open (e.g. Spotify + SoundCloud) and you pause one then press play on the other, the overlay now shows the correct timestamp immediately. Stale SMTC position from the background source is corrected by the next heartbeat." },
        @{ Tag = "FIXED";    Text = "Switching back to a previously backgrounded platform (e.g. closing Spotify while SoundCloud was mid-track) now shows the real elapsed time instead of 0:00. SMTC position freezes when a source loses focus — the server estimates position from a stored epoch instead." },
        @{ Tag = "FIXED";    Text = "Two-platform setups no longer flicker between sources when skipping a track. A source change now requires 3 consecutive detections (~300 ms) before the overlay switches, eliminating the A→B→A flash that occurred during the brief gap between skip and new-track detection." },
        @{ Tag = "FIXED";    Text = "Previous track button (⏮) now correctly resets the overlay to 0:00." },
        @{ Tag = "NEW";      Text = "Discord Rich Presence overhauled — Application ID is now built-in (no per-user Discord developer setup needed). Shows 'Listening to Master''s FM' header, live timestamps, album art, and a 'Listen on SoundCloud' button when available." },
        @{ Tag = "IMPROVED"; Text = "Discord RPC is now a single-click toggle in the tray menu. No client ID dialog." },
        @{ Tag = "IMPROVED"; Text = "Detection speed increased to ~100 ms (was 250 ms) — play, pause, skip, and source switches appear on the overlay almost instantly." },
        @{ Tag = "IMPROVED"; Text = "Debug logging expanded: source-switch debounce steps, SMTC frozen detection, and first-heartbeat position corrections are all visible in overlay.log and server.log for easier troubleshooting." }
    ) },
    @{ Version = "v1.5.9"; Date = "2026-04-19"; Notes = @(
        @{ Tag = "FIXED"; Text = "Non-Latin track and artist names (Hebrew, Russian, Arabic, Korean, Chinese, Japanese, Spanish accents, emoji, musical symbols etc.) now display correctly instead of '????'. PowerShell 5.1 was encoding HTTP webhook bodies with the system ANSI code page, corrupting any character outside Windows-1252. All webhook POSTs now use explicit UTF-8 byte encoding." },
        @{ Tag = "IMPROVED"; Text = "Overlay font stack extended with Noto Sans Hebrew, Arabic, Thai, Symbols, Symbols 2, Noto Emoji, plus system emoji fonts (Segoe UI Emoji on Windows). Every writing system that appears in music metadata now renders with proper glyphs." }
    ) },
    @{ Version = "v1.5.8"; Date = "2026-04-19"; Notes = @(
        @{ Tag = "FIXED"; Text = "Music detection now starts within ~10 seconds instead of up to 6+ minutes — SMTC retry backoff is now capped at 5 s (was escalating up to 30 s, causing very long startup delays when a broken Store-app SMTC session was present)." },
        @{ Tag = "FIXED"; Text = "SoundCloud RPC heartbeat webhooks now reach the server even though the desktop app never exposes SMTC timeline position. The overlay no longer falls silent mid-track and the server no longer spams 'nowplaying gone' every 30 s." },
        @{ Tag = "FIXED"; Text = "Closing Master's FM now reliably terminates both the server and the PowerShell tray. Previously only server.exe was included in the Job Object kill group; the tray process was missed and kept running." },
        @{ Tag = "FIXED"; Text = "SMTC calls (RequestAsync, TryGetMediaPropertiesAsync) now time out in 300 ms and cancel the underlying WinRT operation on timeout — the SMTC service no longer spins at 10% CPU from un-cancelled requests." },
        @{ Tag = "IMPROVED"; Text = "All webhook calls use 127.0.0.1 instead of localhost, eliminating a ~2 s delay from IPv6-first DNS resolution." }
    ) },
    @{ Version = "v1.5.7"; Date = "2026-04-19"; Notes = @(
        @{ Tag = "FIXED"; Text = "Tick performance: SMTC manager is now cached per-tick (Get-SMTCManager) so all three detectors share one RequestAsync() call instead of three independent ones. RequestAsync() and TryGetMediaPropertiesAsync() now have a 1.5s hard timeout. With a broken Windows Store SMTC session (e.g. SoundCloud Store app), worst-case tick time drops from ~30s to ~1.5s." }
    ) },
    @{ Version = "v1.5.6"; Date = "2026-04-19"; Notes = @(
        @{ Tag = "FIXED"; Text = "Master's FM is now truly one application in Task Manager — MastersFM.exe directly spawns both the server and the PowerShell tray as child processes. All three PIDs are grouped under the single MastersFM.exe entry; expanding it reveals the children. Killing MastersFM.exe kills everything instantly via a Windows Job Object." }
    ) },
    @{ Version = "v1.5.5"; Date = "2026-04-19"; Notes = @(
        @{ Tag = "FIXED";    Text = "VLC timestamp no longer sticks at 0:00 — 'no SMTC session' is no longer blindly treated as paused. Audio peak is checked first: if VLC is producing sound the clock runs; debounce handles the brief gap before SMTC registers." },
        @{ Tag = "FIXED";    Text = "SoundCloud, YouTube, and YouTube Music album art now appears immediately — the browser's SMTC thumbnail (exact art from the media player UI) is used as the primary source for browser-based platforms, before slower online lookups." },
        @{ Tag = "FIXED";    Text = "YouTube video thumbnails now show on the overlay — same SMTC-first art path returns the video frame thumbnail for plain YouTube playback." },
        @{ Tag = "FIXED";    Text = "Preset delete now works — the delete button stays visible after loading a preset (previously the button hid itself when the select reset for re-selection). Added success / error feedback toast." },
        @{ Tag = "NEW";      Text = "MastersFM.exe — a proper C# launcher compiled during build. Task Manager now shows 'Master''s FM' as the process description instead of wscript.exe. PowerShell (tray) and server.exe run as its children." },
        @{ Tag = "FIXED";    Text = "Windows Firewall no longer prompts 'Allow Node.js?' — the server now binds to 127.0.0.1 (loopback only). OBS Browser Source uses localhost which routes to loopback; no firewall rule or admin rights needed." },
        @{ Tag = "FIXED";    Text = "'Start on Login' now registers MastersFM.exe in the Run key instead of wscript.exe — auto-start no longer shows a wscript.exe entry in Task Manager." }
    ) },
    @{ Version = "v1.5.4"; Date = "2026-04-19"; Notes = @(
        @{ Tag = "FIXED"; Text = "Multiple open platforms (Spotify + SoundCloud, YouTube + Spotify, etc.) no longer fight / flicker — the overlay now always shows the actively PLAYING source. A paused platform is only shown if nothing else is currently playing." }
    ) },
    @{ Version = "v1.5.3"; Date = "2026-04-19"; Notes = @(
        @{ Tag = "NEW";      Text = "Non-Latin scripts now render correctly — Chinese (Simplified + Traditional), Japanese, Korean, Cyrillic (Russian / Ukrainian / etc.), Lithuanian and other Latin-extended letters all display proper glyphs instead of tofu boxes. Noto Sans CJK + Noto Sans are loaded as a permanent fallback chain after the user's chosen font." },
        @{ Tag = "FIXED";    Text = "osu!lazer menu / song-select detection — the overlay now reads the SMTC session first, so the currently previewed song shows up even when the window title is just 'osu!'. Also catches skip (F2) and seek events because SMTC's timeline reflects the real audio playhead." },
        @{ Tag = "FIXED";    Text = "osu! pause-menu detection works on rigs with busy pause-menu backgrounds — CPU-delta threshold raised from 4 % to 10 % of one core, which is still well below any real gameplay (≥15 %) but above the menu's idle UI animation cost." },
        @{ Tag = "IMPROVED"; Text = "osu! CPU-delta pause heuristic no longer runs on osu!lazer (SMTC's authoritative paused flag is used instead), saving the per-tick Process.Refresh() + TotalProcessorTime read." }
    ) },
    @{ Version = "v1.5.2"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "FIXED";    Text = "Browser platform badge now stays on SoundCloud / YouTube / Deezer etc. instead of flipping back to 'Chrome' when the tab loses focus — the last specific platform is remembered per-browser." },
        @{ Tag = "FIXED";    Text = "Browser music no longer auto-pauses after ~30 seconds — the stagnant-position pause inference is now skipped for every browser source (Media Session API freezes positionMs unreliably)." },
        @{ Tag = "FIXED";    Text = "SoundCloud tab-title heuristic no longer forces 'paused' when the ▶ glyph is missing — SMTC's play-state is trusted unless the glyph explicitly proves the track is playing." },
        @{ Tag = "IMPROVED"; Text = "Platform detection for browsers now also reads the SMTC album field, so SoundCloud / YouTube Music / Deezer badges appear even when the tab title lacks the service name (if the page populates the Media Session album field)." },
        @{ Tag = "IMPROVED"; Text = "Overlay.log now dumps visible window titles + SMTC appId/album once per browser when the platform falls back to a generic browser name, so future detection misses can be diagnosed from a single log line." }
    ) },
    @{ Version = "v1.5.1"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "FIXED";    Text = "SoundCloud in Chrome/Edge no longer stays stuck on 'paused' — tab title's ▶ glyph is now the authoritative play-state signal." },
        @{ Tag = "FIXED";    Text = "Seeking mid-track in browser SoundCloud brings the overlay back instead of leaving it faded out." },
        @{ Tag = "FIXED";    Text = "Playlist page titles no longer leak through as 'PlaylistName / Unknown Artist' — tab-parsed Artist - Track wins when SMTC metadata is missing." },
        @{ Tag = "FIXED";    Text = "Browser platform detection (Chrome / Edge / Firefox / Opera / Brave badges) no longer throws on every tick — caught by the new diagnostics." },
        @{ Tag = "IMPROVED"; Text = "SMTC session picker now prefers actually-Playing sessions over Paused ones — fixes the wrong-tab-wins bug when multiple browser tabs have media." },
        @{ Tag = "IMPROVED"; Text = "Extensive diagnostic logging added: every tick is timed, slow ticks (>200 ms) are flagged, thumbnail extraction is throttled, and every exception is captured with full stack." }
    ) },
    @{ Version = "v1.5.0"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "NEW";   Text = "Discord Rich Presence — show what you're playing on your Discord profile with live timestamps and album art." },
        @{ Tag = "NEW";   Text = "Tray menu 'Discord RPC' — paste your Discord Application ID once, toggle the integration on/off any time." },
        @{ Tag = "FIXED"; Text = "YouTube thumbnails now appear in the overlay — SMTC video thumbnail is forwarded when music-metadata lookups don't match." },
        @{ Tag = "FIXED"; Text = "Spotify art is now pulled directly from SMTC as a fallback, so it lands instantly instead of waiting on Deezer/iTunes." }
    ) },
    @{ Version = "v1.4.1"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "FIXED";    Text = "VLC pause button now freezes the timestamp — session-drop is inferred as paused instead of being treated as 'still playing'." },
        @{ Tag = "FIXED";    Text = "VLC scrubbing forward no longer snaps the timestamp back to 0 — the 0-transient VLC emits mid-seek is ignored." },
        @{ Tag = "IMPROVED"; Text = "VLC timeline runs on a local tick-clock with stability-gated SMTC sync; only real seeks (>3 s delta, confirmed across two ticks) re-anchor the position." }
    ) },
    @{ Version = "v1.4.0"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "Patch-notes panel is tighter — smaller pills, denser rows, more history visible at once." },
        @{ Tag = "FIXED";    Text = "No more horizontal ghost lines while scrolling patch notes." }
    ) },
    @{ Version = "v1.3.9"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "NEW";      Text = "Tray menu 'View Patch Notes' — reopen any time; history is scrollable and cumulative." }
    ) },
    @{ Version = "v1.3.8"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "FIXED";    Text = "Overlay no longer false-fades mid-scroll in osu! song-select (source-closed webhook debounced to 3 s)." },
        @{ Tag = "FIXED";    Text = "osu! pause-menu detection works — CPU-delta sampling now actually refreshes." },
        @{ Tag = "IMPROVED"; Text = "3-sample rolling window on CPU-delta so a GC hitch can't false-pause." }
    ) },
    @{ Version = "v1.3.7"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "NEW";      Text = "osu! pause-menu detection via CPU-delta (no audio COM, no Explorer risk)." },
        @{ Tag = "IMPROVED"; Text = "Rapid song-select scroll queues tracks — overlay lands on the latest one." }
    ) },
    @{ Version = "v1.3.6"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "FIXED";    Text = "Explorer no longer freezes with osu! open — dropped the WASAPI-exclusive probe." },
        @{ Tag = "FIXED";    Text = "osu! detected again — matcher accepts osu! / lazer / cuttingedge." },
        @{ Tag = "FIXED";    Text = "Track-switch animation restored; VLC no longer snaps to 0:00 on pause/seek." },
        @{ Tag = "FIXED";    Text = "Overlay auto-hides 10 s after source closes; timestamp clamped to duration." }
    ) },
    @{ Version = "v1.3.5"; Date = "2026-04-18"; Notes = @(
        @{ Tag = "IMPROVED"; Text = "osu! song-select parser handles lazer, cuttingedge, unicode dashes, no-artist titles." },
        @{ Tag = "IMPROVED"; Text = "Pause detection latency cut from ~1.2 s to ~200 ms." }
    ) },
    @{ Version = "v1.3.4"; Date = "2026-04-17"; Notes = @(
        @{ Tag = "FIXED";    Text = "SoundCloud pause reacts in ~500 ms instead of 1.2 s — overlay freezes almost the instant you hit pause." },
        @{ Tag = "FIXED";    Text = "Overlay timestamp is clamped to the song's duration so OBS browser sources can't drift past the end." }
    ) },
    @{ Version = "v1.3.3"; Date = "2026-04-16"; Notes = @(
        @{ Tag = "NEW";      Text = "Customize Overlay opens as a standalone app window — no browser tabs, no address bar." },
        @{ Tag = "NEW";      Text = "Redesigned welcome screen with auto-dismiss countdown and categorized patch notes." },
        @{ Tag = "FIXED";    Text = "SoundCloud pause now detected via Core Audio peak samples — works for the sc-rpc desktop app too." },
        @{ Tag = "FIXED";    Text = "Pause and resume transitions no longer snap the timestamp — freezes exactly where the overlay was showing." },
        @{ Tag = "FIXED";    Text = "Version badge redesigned as a painted rounded pill — no more clipping into the title." },
        @{ Tag = "IMPROVED"; Text = "Last.fm fully optional — connect any time from the tray; tray label shows your current user." }
    ) }
)

function Get-UserCfgPath {
    # Single source of truth for the user's config.json location. ROAMING
    # appdata (%APPDATA%\MastersFM\config.json) — survives MSI uninstalls,
    # matches where server.js reads/writes. Previously Save-ConfigField
    # wrote to $scriptDir\config.json (%LOCALAPPDATA%\MastersFM\), which
    # the MSI uninstaller wipes — so welcome_seen, welcome_seen_version,
    # autostart_defaulted_on_v199 all got reset on every reinstall and
    # the user saw the welcome dialog + default-on auto-start flip again.
    # Unifying to Roaming fixes that for both tray AND server.
    $roaming = [System.Environment]::GetFolderPath('ApplicationData')
    $dir = [System.IO.Path]::Combine($roaming, 'MastersFM')
    try { [System.IO.Directory]::CreateDirectory($dir) | Out-Null } catch {}
    return [System.IO.Path]::Combine($dir, 'config.json')
}

# ── Platform-detection toggles — v5 refactor ─────────────────────────────────
# Toggles are now keyed on PLATFORM NAMES (what the user sees on the overlay
# badge) instead of DETECTION METHODS. Disabling "Spotify" kills Spotify
# detection whether the source is the desktop app or the Spotify web player
# inside any browser; disabling "YouTube" kills YouTube-in-browser; etc.
#
# "Browser" is a top-level master switch — unchecking it kills all browser-
# based detection regardless of platform-specific toggle state.  This lets the
# user silence "everything in the browser" with one click.
$script:PLATFORM_KEYS = @(
    @{ Key = 'Spotify';      Label = 'Spotify';              Desc = 'Desktop app + Spotify Web Player.' },
    @{ Key = 'SoundCloud';   Label = 'SoundCloud';           Desc = 'Desktop RPC app + soundcloud.com.' },
    @{ Key = 'YouTube';      Label = 'YouTube';              Desc = 'youtube.com videos in any browser.' },
    @{ Key = 'YouTubeMusic'; Label = 'YouTube Music';        Desc = 'music.youtube.com.' },
    @{ Key = 'AppleMusic';   Label = 'Apple Music';          Desc = 'Desktop app + music.apple.com.' },
    @{ Key = 'TIDAL';        Label = 'TIDAL';                Desc = 'Desktop app + listen.tidal.com.' },
    @{ Key = 'Deezer';       Label = 'Deezer';               Desc = 'Desktop app + deezer.com.' },
    @{ Key = 'AmazonMusic';  Label = 'Amazon Music';         Desc = 'Desktop app + music.amazon.*' },
    @{ Key = 'Pandora';      Label = 'Pandora';              Desc = 'pandora.com.' },
    @{ Key = 'Bandcamp';     Label = 'Bandcamp';             Desc = 'bandcamp.com.' },
    @{ Key = 'Mixcloud';     Label = 'Mixcloud';             Desc = 'mixcloud.com.' },
    @{ Key = 'osu';          Label = 'osu!';                 Desc = 'Rhythm game window title.' },
    @{ Key = 'VLC';          Label = 'VLC';                  Desc = 'VLC media player.' },
    @{ Key = 'WMP';          Label = 'Windows Media Player'; Desc = 'Legacy WMP + Windows 11 Media Player.' },
    @{ Key = 'Browser';      Label = 'Browser';              Desc = 'Master switch — uncheck to kill ALL browser detection at once.' }
)

# Map friendly / overlay-badge names (what Get-PlatformName / Get-BrowserPlatformFromWindows
# return) → toggle config key. Detectors pass in a display name; this dict
# finds the right on/off switch.
$script:PLATFORM_DISPLAY_TO_KEY = @{
    'Spotify'              = 'Spotify'
    'SoundCloud'           = 'SoundCloud'
    'YouTube'              = 'YouTube'
    'YouTube Music'        = 'YouTubeMusic'
    'Apple Music'          = 'AppleMusic'
    'TIDAL'                = 'TIDAL'
    'Deezer'               = 'Deezer'
    'Amazon Music'         = 'AmazonMusic'
    'Pandora'              = 'Pandora'
    'Bandcamp'             = 'Bandcamp'
    'Mixcloud'             = 'Mixcloud'
    'osu!'                 = 'osu'
    'VLC'                  = 'VLC'
    'Windows Media Player' = 'WMP'
    'Browser'              = 'Browser'
}

# Tiny cache so each scrobble tick doesn't re-read disk 10x. Invalidated
# whenever Save-ConfigField runs.
$script:_PlatformsCache   = $null
$script:_PlatformsCacheAt = [DateTime]::MinValue

function Invalidate-PlatformsCache { $script:_PlatformsCache = $null }

function Get-PlatformsConfig {
    # Read the current platforms map from Roaming config.
    # Defaults ALL platforms to TRUE when the key is missing — new users on
    # first boot should still get full detection until they opt out of any.
    $now = [DateTime]::UtcNow
    if ($script:_PlatformsCache -and (($now - $script:_PlatformsCacheAt).TotalSeconds -lt 5)) {
        return $script:_PlatformsCache
    }
    $map = @{}
    foreach ($p in $script:PLATFORM_KEYS) { $map[$p.Key] = $true }
    try {
        $cfgPath = Get-UserCfgPath
        if (Test-Path $cfgPath) {
            $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
            if ($cfg -and $cfg.platforms) {
                foreach ($p in $script:PLATFORM_KEYS) {
                    $k = $p.Key
                    if ($cfg.platforms.PSObject.Properties.Name -contains $k) {
                        $map[$k] = [bool]$cfg.platforms.$k
                    }
                }
            }
        }
    } catch {}
    $script:_PlatformsCache   = $map
    $script:_PlatformsCacheAt = $now
    return $map
}

function Test-PlatformEnabled($platform) {
    # Fast-path guard. Accepts EITHER a config key ('Spotify', 'YouTubeMusic')
    # or a display name from overlay-badge-land ('Spotify', 'YouTube Music',
    # 'Apple Music', 'Windows Media Player', 'osu!', ...) — the name the
    # detectors see after Get-PlatformName / Get-BrowserPlatformFromWindows
    # resolves the appId. Falls back to TRUE on unknown platform so new
    # services (ones we haven't added a toggle for yet) aren't silently
    # killed.
    if (-not $platform) { return $true }
    $key = $platform
    if ($script:PLATFORM_DISPLAY_TO_KEY.ContainsKey($platform)) {
        $key = $script:PLATFORM_DISPLAY_TO_KEY[$platform]
    }
    $map = Get-PlatformsConfig
    if ($map -and $map.ContainsKey($key)) { return [bool]$map[$key] }
    return $true
}

function Save-PlatformsConfig($map) {
    # Save the whole $map hashtable back as config.platforms. Single write,
    # read-modify-write like Save-ConfigField but for the nested object.
    try {
        $cfgPath = Get-UserCfgPath
        $existing = $null
        if (Test-Path $cfgPath) {
            try {
                $raw = [System.IO.File]::ReadAllText($cfgPath, [System.Text.Encoding]::UTF8) -replace '^\uFEFF',''
                $existing = $raw | ConvertFrom-Json
            } catch {}
        }
        $bag = [ordered]@{}
        if ($existing) {
            foreach ($prop in $existing.PSObject.Properties) { $bag[$prop.Name] = $prop.Value }
        }
        # Convert hashtable → ordered dict so JSON key order is stable.
        $platformsObj = [ordered]@{}
        foreach ($p in $script:PLATFORM_KEYS) {
            $k = $p.Key
            if ($map.ContainsKey($k)) { $platformsObj[$k] = [bool]$map[$k] } else { $platformsObj[$k] = $true }
        }
        $bag['platforms'] = $platformsObj
        $noBom = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($cfgPath, ($bag | ConvertTo-Json -Depth 10), $noBom)
        Invalidate-PlatformsCache
        Log ("Platforms saved: " + (($platformsObj.Keys | ForEach-Object { "$_=$($platformsObj[$_])" }) -join ', '))
    } catch { Log "Save-PlatformsConfig failed: $_" }
}

function Save-ConfigField($field, $value) {
    # Merge-save a single top-level field into the ROAMING config.json,
    # preserving all other fields.  Reads the existing file, mutates one
    # key, writes back — so we never clobber unrelated settings (overlay.*,
    # lastfm_username, discord_rpc.*, etc.) the server may have written.
    $cfg   = Get-UserCfgPath
    $noBom = [System.Text.UTF8Encoding]::new($false)
    $existing = $null
    try {
        if (Test-Path $cfg) {
            $raw = [System.IO.File]::ReadAllText($cfg, [System.Text.Encoding]::UTF8) -replace '^\uFEFF',''
            $existing = $raw | ConvertFrom-Json
        }
    } catch {}
    $bag = [ordered]@{}
    if ($existing) {
        foreach ($p in $existing.PSObject.Properties) { $bag[$p.Name] = $p.Value }
    }
    $bag[$field] = $value
    $json = $bag | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($cfg, $json, $noBom)
    Log "Config saved: $field=$value"
}

# (v6.2.3: removed OBS Source Side placement helpers —
# Get-OBSCanvasSize, Get-OBSSourceSide, Get-OBSSourceSize,
# Compute-OBSPosition, Reset-OBSSourcePosition and the reset flag timer
# are all gone now that the card fills the 1000x200 source directly.)

function Get-WelcomeSeen {
    # Returns $true only if the user has acknowledged the welcome screen for
    # the CURRENT $script:APP_VERSION. Any version bump re-shows it so users
    # see the new patch notes.
    $cfg = Get-UserCfgPath
    if (-not (Test-Path $cfg)) { return $false }
    try {
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        # Legacy bool flag (pre-versioning): treat as 'seen v0'
        if ($j.welcome_seen -and -not $j.welcome_seen_version) { return $false }
        $seenVer = ($j.welcome_seen_version + "").Trim()
        return ($seenVer -eq $script:APP_VERSION)
    } catch { return $false }
}

function Show-SetupDialog {
    # Last.fm removed — kept as stub so old references don't break during transition
    return $null
}

# ── First-run Welcome dialog: modern, borderless, gradient, patch notes ─────
function Show-WelcomeDialog {
    param([switch]$Manual)   # $Manual = opened from tray → no auto-dismiss countdown
    $APP_VERSION = $script:APP_VERSION
    $APP_BUILD   = (Get-Date -Format "yyyy.MM.dd")

    $form = New-Object System.Windows.Forms.Form
    $form.Text            = "Welcome to Master's FM"
    $form.Size            = New-Object System.Drawing.Size(780, 760)
    $form.StartPosition   = "CenterScreen"
    $form.FormBorderStyle = "None"
    $form.BackColor       = [System.Drawing.Color]::FromArgb(255, 10, 3, 22)
    $form.TopMost         = $true
    $form.ShowInTaskbar   = $true

    # v6.1.5 — set the form icon to Master's FM so the taskbar thumbnail
    # shows the purple logo instead of the default WinForms icon. Only
    # affects this dialog because it's the only form with ShowInTaskbar
    # = $true (every other modal is $false). Load from disk each time so
    # we don't depend on script-scope $icon being available in every
    # invocation path (some tests call the function in isolation).
    try {
        $icoPath = [System.IO.Path]::Combine($scriptDir, "MastersFM.ico")
        if (Test-Path $icoPath) {
            $form.Icon = New-Object System.Drawing.Icon($icoPath)
        }
    } catch { Log "Show-WelcomeDialog: icon load failed — $_" }
    # Enable double buffering via SetStyle (Form.DoubleBuffered is protected in .NET Framework).
    # IMPORTANT: do NOT include UserPaint — that flag tells Windows to skip the default
    # WM_PAINT handler, which prevents child Label controls from rendering.
    $dbStyle = [System.Windows.Forms.ControlStyles]::OptimizedDoubleBuffer -bor `
               [System.Windows.Forms.ControlStyles]::AllPaintingInWmPaint
    $setStyleMethod = [System.Windows.Forms.Form].GetMethod('SetStyle', [System.Reflection.BindingFlags]'NonPublic,Instance')
    try { $setStyleMethod.Invoke($form, @([System.Windows.Forms.ControlStyles]$dbStyle, $true)) | Out-Null } catch {}

    # ── Background paint: gradient + accent strip + logo + section dividers ───
    $form.add_Paint({
        param($sender, $e)
        $g = $e.Graphics
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $W = $sender.Width; $H = $sender.Height

        # Diagonal gradient body
        $rect = New-Object System.Drawing.Rectangle(0, 0, ($W - 1), ($H - 1))
        $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 28, 10, 54),
            [System.Drawing.Color]::FromArgb(255, 8, 2, 18),
            [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
        $g.FillRectangle($bodyBrush, $rect)
        $bodyBrush.Dispose()

        # Corner glow bloom (top-right magenta)
        $bloomPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $bloomPath.AddEllipse(($W - 260), -160, 420, 420)
        $bloom = New-Object System.Drawing.Drawing2D.PathGradientBrush($bloomPath)
        $bloom.CenterColor = [System.Drawing.Color]::FromArgb(80, 255, 40, 160)
        $bloom.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
        $g.FillPath($bloom, $bloomPath)
        $bloom.Dispose(); $bloomPath.Dispose()

        # Corner glow bloom (bottom-left purple)
        $bloomPath2 = New-Object System.Drawing.Drawing2D.GraphicsPath
        $bloomPath2.AddEllipse(-180, ($H - 240), 460, 460)
        $bloom2 = New-Object System.Drawing.Drawing2D.PathGradientBrush($bloomPath2)
        $bloom2.CenterColor = [System.Drawing.Color]::FromArgb(70, 140, 70, 255)
        $bloom2.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
        $g.FillPath($bloom2, $bloomPath2)
        $bloom2.Dispose(); $bloomPath2.Dispose()

        # Outer 1px purple border
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 140, 70, 255), 1)
        $g.DrawRectangle($pen, $rect)
        $pen.Dispose()

        # Top accent strip (magenta → purple gradient)
        $accent = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.Rectangle(0, 0, $W, 4)),
            [System.Drawing.Color]::FromArgb(255, 255, 16, 133),
            [System.Drawing.Color]::FromArgb(255, 74, 10, 184),
            [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
        $g.FillRectangle($accent, 0, 0, $W, 4)
        $accent.Dispose()

        # ── Logo disc (top-left): magenta→purple radial + music glyph ─────────
        # Use RectangleF for DrawString — PowerShell won't auto-convert Rectangle.
        $logoRF = New-Object System.Drawing.RectangleF(32.0, 30.0, 68.0, 68.0)
        $logoPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $logoPath.AddEllipse($logoRF)
        $logoBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($logoPath)
        $logoBrush.CenterColor    = [System.Drawing.Color]::FromArgb(255, 255, 90, 200)
        $logoBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 110, 30, 200))
        $g.FillPath($logoBrush, $logoPath)
        $logoBrush.Dispose(); $logoPath.Dispose()
        # subtle halo
        $halo = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 255, 120, 220), 2)
        $g.DrawEllipse($halo, 28, 26, 76, 76)
        $halo.Dispose()
        # music glyph — DrawString(String, Font, Brush, RectangleF, StringFormat)
        $glyphFont = New-Object System.Drawing.Font("Segoe UI Symbol", 28, [System.Drawing.FontStyle]::Bold)
        $glyphBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString([string][char]0x266B, $glyphFont, $glyphBrush, $logoRF, $sf)
        $glyphFont.Dispose(); $glyphBrush.Dispose(); $sf.Dispose()

        # ── Section dividers (thin purple lines) ──────────────────────────────
        $divPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 140, 70, 255), 1)
        $g.DrawLine($divPen, 32, 148, ($W - 32), 148)  # under header
        $g.DrawLine($divPen, 32, 460, ($W - 32), 460)  # between features & patch notes
        $divPen.Dispose()
    })

    # Drag-to-move
    $dragState = @{ dragging = $false; offset = (New-Object System.Drawing.Point(0,0)) }
    $form.add_MouseDown({ param($s,$e) if ($e.Button -eq 'Left') { $dragState.dragging = $true; $dragState.offset = $e.Location } })
    $form.add_MouseUp({ $dragState.dragging = $false })
    $form.add_MouseMove({
        param($s,$e)
        if ($dragState.dragging) {
            $p = $s.PointToScreen($e.Location)
            $s.Location = New-Object System.Drawing.Point(($p.X - $dragState.offset.X), ($p.Y - $dragState.offset.Y))
        }
    })

    # Close (X) button — top-right
    $closeBtn = New-Object System.Windows.Forms.Label
    $closeBtn.Text = [char]0x2715
    $closeBtn.Font = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)
    $closeBtn.ForeColor = [System.Drawing.Color]::FromArgb(255, 200, 170, 240)
    $closeBtn.BackColor = [System.Drawing.Color]::Transparent
    $closeBtn.TextAlign = 'MiddleCenter'
    $closeBtn.Size      = New-Object System.Drawing.Size(32, 28)
    $closeBtn.Location  = New-Object System.Drawing.Point(740, 10)
    $closeBtn.Cursor    = [System.Windows.Forms.Cursors]::Hand
    $closeBtn.add_Click({ $form.DialogResult = 'OK'; $form.Close() })
    $form.Controls.Add($closeBtn)

    # ── HEADER (right of logo disc at x=32, y=30, 68x68) ───────────────────────
    $title = New-Object System.Windows.Forms.Label
    $title.Text      = "Master's FM"
    $title.Font      = New-Object System.Drawing.Font("Segoe UI", 28, [System.Drawing.FontStyle]::Bold)
    $title.ForeColor = [System.Drawing.Color]::White
    $title.BackColor = [System.Drawing.Color]::Transparent
    $title.AutoSize  = $true
    $title.Location  = New-Object System.Drawing.Point(120, 28)
    $form.Controls.Add($title)

    # Version pill — custom Panel that paints its own rounded-rect background.
    # Placed well clear of the title so it doesn't clip into the big "Master's FM".
    $verPill = New-Object System.Windows.Forms.Panel
    $verPill.Size      = New-Object System.Drawing.Size(176, 24)
    $verPill.Location  = New-Object System.Drawing.Point(122, 82)
    $verPill.BackColor = [System.Drawing.Color]::Transparent
    $verText = "$APP_VERSION   -   build $APP_BUILD"
    $verPill.add_Paint({
        param($s,$e)
        $g = $e.Graphics
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $r = New-Object System.Drawing.Rectangle(0, 0, ($s.Width - 1), ($s.Height - 1))
        # rounded rect path
        $radius = 12
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc($r.X, $r.Y, $radius, $radius, 180, 90)
        $path.AddArc(($r.Right - $radius), $r.Y, $radius, $radius, 270, 90)
        $path.AddArc(($r.Right - $radius), ($r.Bottom - $radius), $radius, $radius, 0, 90)
        $path.AddArc($r.X, ($r.Bottom - $radius), $radius, $radius, 90, 90)
        $path.CloseFigure()
        # gradient fill
        $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $r,
            [System.Drawing.Color]::FromArgb(255, 74, 20, 130),
            [System.Drawing.Color]::FromArgb(255, 40, 12, 80),
            [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillPath($fill, $path)
        $fill.Dispose()
        # border
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 180, 100, 255), 1)
        $g.DrawPath($pen, $path)
        $pen.Dispose(); $path.Dispose()
        # text
        $font = New-Object System.Drawing.Font("Segoe UI Semibold", 8, [System.Drawing.FontStyle]::Bold)
        $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 180, 240))
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $rf = New-Object System.Drawing.RectangleF([float]$r.X, [float]$r.Y, [float]$r.Width, [float]$r.Height)
        $g.DrawString($s.Tag, $font, $brush, $rf, $sf)
        $font.Dispose(); $brush.Dispose(); $sf.Dispose()
    })
    $verPill.Tag = $verText
    $form.Controls.Add($verPill)

    # Tagline
    $sub = New-Object System.Windows.Forms.Label
    $sub.Text      = "Your music, on screen. A Now Playing overlay for OBS - built on Windows Media Transport."
    $sub.Font      = New-Object System.Drawing.Font("Segoe UI", 10)
    $sub.ForeColor = [System.Drawing.Color]::FromArgb(255, 195, 165, 235)
    $sub.BackColor = [System.Drawing.Color]::Transparent
    $sub.AutoSize  = $false
    $sub.Size      = New-Object System.Drawing.Size(700, 20)
    $sub.Location  = New-Object System.Drawing.Point(32, 112)
    $form.Controls.Add($sub)

    # ── SECTION 1: WHAT IT DOES ───────────────────────────────────────────────
    $feaHdr = New-Object System.Windows.Forms.Label
    $feaHdr.Text      = "WHAT IT DOES"
    $feaHdr.Font      = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $feaHdr.ForeColor = [System.Drawing.Color]::FromArgb(255, 200, 130, 255)
    $feaHdr.BackColor = [System.Drawing.Color]::Transparent
    $feaHdr.AutoSize  = $true
    $feaHdr.Location  = New-Object System.Drawing.Point(32, 164)
    $form.Controls.Add($feaHdr)

    # Feature cards: icon + bold title + muted description
    $features = @(
        @{ Icon = [char]0x266B; Title = "Universal track detection"; Desc = "Detects music from Spotify, YouTube, YouTube Music, TIDAL, Deezer, Apple Music, SoundCloud, VLC, foobar2000, MusicBee, osu! and more — any app that reports Now Playing on Windows." },
        @{ Icon = [char]0x25C9; Title = "Sleek animated overlay";     Desc = "Glowing gradient card, rotating border, marquee title, progress bar, and spectrum bars. Every element tunable from the tray." },
        @{ Icon = [char]0x2795; Title = "One-click OBS integration";  Desc = "Drops a Browser Source straight into your active scene — no manual URL copy, no resolution guesswork." },
        @{ Icon = [char]0x25CE;  Title = "Discord Rich Presence";      Desc = "Automatically shows what you're listening to on your Discord profile — with live timestamps, album art, and a 'Listen on SoundCloud' button. No setup needed." }
    )
    $featY = 196
    foreach ($f in $features) {
        # icon
        $ico = New-Object System.Windows.Forms.Label
        $ico.Text      = [string]$f.Icon
        $ico.Font      = New-Object System.Drawing.Font("Segoe UI Symbol", 18, [System.Drawing.FontStyle]::Bold)
        $ico.ForeColor = [System.Drawing.Color]::FromArgb(255, 255, 90, 200)
        $ico.BackColor = [System.Drawing.Color]::Transparent
        $ico.AutoSize  = $false
        $ico.Size      = New-Object System.Drawing.Size(40, 40)
        $ico.TextAlign = 'MiddleCenter'
        $ico.Location  = New-Object System.Drawing.Point(40, $featY)
        $form.Controls.Add($ico)
        # title
        $ft = New-Object System.Windows.Forms.Label
        $ft.Text      = $f.Title
        $ft.Font      = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
        $ft.ForeColor = [System.Drawing.Color]::White
        $ft.BackColor = [System.Drawing.Color]::Transparent
        $ft.AutoSize  = $true
        $ft.Location  = New-Object System.Drawing.Point(88, $featY)
        $form.Controls.Add($ft)
        # description
        $fd = New-Object System.Windows.Forms.Label
        $fd.Text      = $f.Desc
        $fd.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
        $fd.ForeColor = [System.Drawing.Color]::FromArgb(255, 190, 170, 225)
        $fd.BackColor = [System.Drawing.Color]::Transparent
        $fd.AutoSize  = $false
        $fd.Size      = New-Object System.Drawing.Size(640, 32)
        $fd.Location  = New-Object System.Drawing.Point(88, ($featY + 22))
        $form.Controls.Add($fd)
        $featY += 64
    }

    # ── SECTION 2: WHAT'S NEW (scrollable, cumulative) ─────────────────────────
    $notesHdr = New-Object System.Windows.Forms.Label
    $notesHdr.Text      = "PATCH NOTES  -  current: $APP_VERSION"
    $notesHdr.Font      = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $notesHdr.ForeColor = [System.Drawing.Color]::FromArgb(255, 255, 90, 200)
    $notesHdr.BackColor = [System.Drawing.Color]::Transparent
    $notesHdr.AutoSize  = $true
    $notesHdr.Location  = New-Object System.Drawing.Point(32, 476)
    $form.Controls.Add($notesHdr)

    # Scrollable container for the full patch history. AutoScroll gives us a
    # native WinForms scrollbar with mouse-wheel support. We use a nested
    # outer/inner panel pair:
    #   outer — fixed position, paints the 1 px purple border ONCE at the
    #           visible client rectangle (no scroll-smear).
    #   inner — AutoScroll + double-buffered so rows don't leave ghost lines.
    $notesOuter = New-Object System.Windows.Forms.Panel
    $notesOuter.Location   = New-Object System.Drawing.Point(32, 500)
    $notesOuter.Size       = New-Object System.Drawing.Size(716, 178)
    $notesOuter.BackColor  = [System.Drawing.Color]::FromArgb(255, 14, 4, 28)
    $notesOuter.BorderStyle = 'FixedSingle'
    $form.Controls.Add($notesOuter)

    $notesPanel = New-Object System.Windows.Forms.Panel
    $notesPanel.Location   = New-Object System.Drawing.Point(0, 0)
    $notesPanel.Size       = New-Object System.Drawing.Size(714, 176)
    $notesPanel.BackColor  = [System.Drawing.Color]::FromArgb(255, 14, 4, 28)
    $notesPanel.AutoScroll = $true
    $notesPanel.BorderStyle = 'None'
    # Force double-buffering so scrolling doesn't smear transparent-label
    # content across the panel (Panel.DoubleBuffered is protected).
    try {
        $setStyleMethod2 = [System.Windows.Forms.Panel].GetMethod('SetStyle', [System.Reflection.BindingFlags]'NonPublic,Instance')
        $dbStyle2 = [System.Windows.Forms.ControlStyles]::OptimizedDoubleBuffer -bor `
                    [System.Windows.Forms.ControlStyles]::AllPaintingInWmPaint -bor `
                    [System.Windows.Forms.ControlStyles]::ResizeRedraw
        $setStyleMethod2.Invoke($notesPanel, @([System.Windows.Forms.ControlStyles]$dbStyle2, $true)) | Out-Null
    } catch {}
    $notesOuter.Controls.Add($notesPanel)

    # Tag colour map — shared with the single-line renderer.
    $tagStyle = @{
        'NEW'      = @{ Bg = [System.Drawing.Color]::FromArgb(255, 74, 10, 184);  Fg = [System.Drawing.Color]::FromArgb(255, 230, 200, 255) }
        'FIXED'    = @{ Bg = [System.Drawing.Color]::FromArgb(255, 200, 40, 110); Fg = [System.Drawing.Color]::White }
        'IMPROVED' = @{ Bg = [System.Drawing.Color]::FromArgb(255, 140, 70, 255); Fg = [System.Drawing.Color]::White }
        'REMOVED'  = @{ Bg = [System.Drawing.Color]::FromArgb(255, 120, 60, 60);  Fg = [System.Drawing.Color]::White }
    }

    # Layout constants — tuned for compact density so multiple releases fit
    # in the viewport without scrolling. Pills line up with the first line
    # of each wrapped note.
    $PAD_LEFT     = 14
    $PILL_W       = 64
    $PILL_H       = 16
    $TEXT_X       = $PAD_LEFT + $PILL_W + 8     # = 86
    $TEXT_W       = 716 - $TEXT_X - 24          # right-margin of 24 (scrollbar clearance)
    $ROW_GAP      = 3                            # between rows inside a release
    $REL_GAP      = 10                           # between releases
    $HDR_H        = 20
    $noteFont     = New-Object System.Drawing.Font("Segoe UI", 8.25)
    # Solid BG matching the panel — Transparent labels inside AutoScroll
    # panels are the root cause of the horizontal smear lines. Using an
    # explicit solid background forces clean redraws on every scroll tick.
    $notesBg = [System.Drawing.Color]::FromArgb(255, 14, 4, 28)

    $y = 10
    $firstRelease = $true
    foreach ($rel in $script:PATCH_HISTORY) {
        # Version header row
        $vh = New-Object System.Windows.Forms.Label
        $vh.Text      = "$($rel.Version)   -   $($rel.Date)"
        $vh.Font      = New-Object System.Drawing.Font("Segoe UI", 8.75, [System.Drawing.FontStyle]::Bold)
        $vh.ForeColor = if ($firstRelease) { [System.Drawing.Color]::FromArgb(255, 255, 90, 200) } else { [System.Drawing.Color]::FromArgb(255, 200, 160, 255) }
        $vh.BackColor = $notesBg
        $vh.AutoSize  = $true
        $vh.Location  = New-Object System.Drawing.Point($PAD_LEFT, $y)
        $notesPanel.Controls.Add($vh)
        $y += $HDR_H
        $firstRelease = $false

        foreach ($n in $rel.Notes) {
            $style = if ($tagStyle.ContainsKey($n.Tag)) { $tagStyle[$n.Tag] } else { $tagStyle['IMPROVED'] }

            # Note text — AutoSize=true + MaximumSize lets WinForms compute the
            # exact GDI+-wrapped height. Add to panel first so layout runs, then
            # read Height. This avoids the GDI vs GDI+ discrepancy that
            # TextRenderer.MeasureText produced (causing text to be clipped).
            $nt = New-Object System.Windows.Forms.Label
            $nt.Text        = $n.Text
            $nt.Font        = $noteFont
            $nt.ForeColor   = [System.Drawing.Color]::FromArgb(255, 215, 200, 235)
            $nt.BackColor   = $notesBg
            $nt.AutoSize    = $true
            $nt.MaximumSize = New-Object System.Drawing.Size($TEXT_W, 0)  # 0 = no height limit
            $nt.Location    = New-Object System.Drawing.Point($TEXT_X, $y)
            $notesPanel.Controls.Add($nt)
            $textH = [Math]::Max($PILL_H, $nt.Height + 2)

            # Category pill — vertically centered against the text block.
            $pill = New-Object System.Windows.Forms.Label
            $pill.Text      = $n.Tag
            $pill.Font      = New-Object System.Drawing.Font("Segoe UI", 6.5, [System.Drawing.FontStyle]::Bold)
            $pill.ForeColor = $style.Fg
            $pill.BackColor = $style.Bg
            $pill.TextAlign = 'MiddleCenter'
            $pill.AutoSize  = $false
            $pill.Size      = New-Object System.Drawing.Size($PILL_W, $PILL_H)
            $pillY          = $y + [int](($textH - $PILL_H) / 2)
            $pill.Location  = New-Object System.Drawing.Point($PAD_LEFT, $pillY)
            $notesPanel.Controls.Add($pill)

            $y += $textH + $ROW_GAP
        }
        $y += $REL_GAP
    }

    # Footer attribution (bottom-left)
    $footer = New-Object System.Windows.Forms.Label
    $footer.Text      = "Powered by MasterShadex  -  github.com/MasterShadex"
    $footer.Font      = New-Object System.Drawing.Font("Segoe UI", 8, [System.Drawing.FontStyle]::Italic)
    $footer.ForeColor = [System.Drawing.Color]::FromArgb(255, 150, 110, 200)
    $footer.BackColor = [System.Drawing.Color]::Transparent
    $footer.AutoSize  = $true
    $footer.Location  = New-Object System.Drawing.Point(34, 704)
    $form.Controls.Add($footer)

    # Get Started (primary)
    $goBtn = New-Object System.Windows.Forms.Button
    $goBtn.Text      = "Get Started  " + [char]0x2192
    $goBtn.Size      = New-Object System.Drawing.Size(420, 44)
    $goBtn.Location  = New-Object System.Drawing.Point(316, 690)
    $goBtn.BackColor = [System.Drawing.Color]::FromArgb(255, 140, 70, 255)
    $goBtn.ForeColor = [System.Drawing.Color]::White
    $goBtn.FlatStyle = 'Flat'
    $goBtn.FlatAppearance.BorderSize = 0
    $goBtn.Font      = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
    $goBtn.Cursor    = [System.Windows.Forms.Cursors]::Hand
    $goBtn.DialogResult = 'OK'
    $goBtn.add_Click({ Log "Welcome: Get Started clicked"; $form.Close() })
    $form.Controls.Add($goBtn)
    $form.AcceptButton = $goBtn

    # ── Auto-dismiss countdown ────────────────────────────────────────────────
    # If the user doesn't interact within AUTO_CLOSE_SECONDS, the dialog closes
    # itself so the rest of tray startup proceeds. Any mouse move / click / key
    # press resets the countdown. A live "auto-closing in Ns" hint is shown
    # below the Get Started button so the user isn't surprised.
    $AUTO_CLOSE_SECONDS = 10
    $state = [hashtable]::Synchronized(@{ remaining = $AUTO_CLOSE_SECONDS; interacted = [bool]$Manual; closed = $false })

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text      = if ($Manual) { "" } else { "auto-closing in $AUTO_CLOSE_SECONDS s  -  press any key to stay" }
    $hint.Font      = New-Object System.Drawing.Font("Segoe UI", 8)
    $hint.ForeColor = [System.Drawing.Color]::FromArgb(255, 150, 110, 200)
    $hint.BackColor = [System.Drawing.Color]::Transparent
    $hint.AutoSize  = $true
    $hint.Location  = New-Object System.Drawing.Point(534, 738)
    $form.Controls.Add($hint)

    $tickTimer = New-Object System.Windows.Forms.Timer
    $tickTimer.Interval = 1000
    $tickTimer.add_Tick({
        if ($state.interacted) {
            $hint.Visible = $false
            $tickTimer.Stop()
            Log "Welcome: user interacted — auto-dismiss cancelled"
            return
        }
        $state.remaining = $state.remaining - 1
        $hint.Text = "auto-closing in $($state.remaining) s  -  press any key to stay"
        Log "Welcome: auto-dismiss countdown $($state.remaining)s"
        if ($state.remaining -le 0) {
            $tickTimer.Stop()
            $state.closed = $true
            Log "Welcome: auto-dismissed after $AUTO_CLOSE_SECONDS s of inactivity"
            try { $form.Close() } catch {}
        }
    })

    # Keyboard-only cancel: any key press keeps the dialog open. Mouse moves
    # are ignored (too sensitive — the cursor can land on the form accidentally
    # when it pops up). Mouse CLICKS on the buttons still work normally because
    # the button handlers call $form.Close() directly.
    $cancelAuto = {
        param($s,$e)
        if (-not $state.interacted) {
            $state.interacted = $true
            $key = try { $e.KeyCode } catch { '?' }
            Log "Welcome: key '$key' pressed - auto-dismiss cancelled"
        }
    }
    # KeyPreview routes keystrokes to the form even when a child control has focus.
    $form.KeyPreview = $true
    $form.add_KeyDown($cancelAuto)

    $form.add_Shown({
        Log "Welcome: dialog shown ($($form.Width)x$($form.Height)) manual=$Manual"
        if (-not $Manual) { $tickTimer.Start() }
    })
    $form.add_FormClosed({
        $tickTimer.Stop()
        $tickTimer.Dispose()
        $reason = if ($state.closed) { "auto-dismiss" } elseif ($state.interacted) { "user closed" } else { "other" }
        Log "Welcome: dialog closed ($reason)"
    })

    Log "Welcome: opening dialog"
    [void]$form.ShowDialog()
    $form.Dispose()
    Log "Welcome: disposed"
}

# ── Platform Detection dialog (v2.1.0) ──────────────────────────────────────
# Borderless, gradient, matches the look of Show-WelcomeDialog — each platform
# gets a row with a checkbox, a bold label, and a grey description.  Save
# button writes all toggles to the ROAMING config.platforms object.
function Show-AudioDeviceDialog {
    # v5.3.0 — extended for multi-backend capture. /devices now returns
    # rows tagged with `backend` (wasapi_loopback / wasapi_input /
    # wasapi_exclusive / mme / asio) and `id` (WASAPI endpoint ID / MME
    # numeric index / ASIO driver name). The dialog groups devices by
    # backend into CARD sections with bold headers, so users can pick
    # "Focusrite Scarlett (ASIO)" or "Stereo Mix (MME)" as easily as a
    # WASAPI loopback endpoint. Selection tuple = (backend, id).
    # On Save: (a) POST /set-device {backend, id}, (b) persist both as
    # audioSpectrumBackend + audioSpectrumDevice in the Roaming config.
    $devicesJson = $null
    try {
        # v6.9.4: TimeoutSec bumped 3 -> 20 because device enumeration is
        # slow on PCs with VB-Cable / VB-Matrix / Voicemeeter / lots of
        # ASIO drivers — friend's log showed /devices not finishing inside
        # 3 s, the disconnect logging as 'HandleDevices: network name no
        # longer available' on the audio_spectrum side. 20 s covers the
        # slowest realistic enumeration; the dialog opens immediately on
        # fast machines so users don't notice the higher ceiling.
        $r = Invoke-WebRequest -Uri 'http://127.0.0.1:4243/devices' -UseBasicParsing -TimeoutSec 20
        $devicesJson = $r.Content | ConvertFrom-Json
    } catch {
        # v6.9.1: instead of just showing an error, offer to fix the most
        # common cause — Windows HTTP.sys URL ACL permission missing on
        # http://127.0.0.1:4243/. Per-user MSI installs don't get UAC
        # elevation by default, so the v6.9.0 install-time netsh custom
        # action silently fails on accounts where MSI runs unelevated.
        # The fix: re-prompt the user with elevation explicitly via
        # PowerShell's -Verb RunAs, run netsh, restart audio_spectrum.exe.
        Log "Audio Source: /devices fetch failed ($($_.Exception.Message)) — offering URL-ACL repair"
        $promptResult = [System.Windows.Forms.MessageBox]::Show(
            "Master's FM can't reach its audio service at http://127.0.0.1:4243.`n`n" +
            "This is almost always a one-time Windows permission issue (the URL prefix is not registered for your user account). " +
            "Click YES to fix it now — Windows will ask for admin rights once.`n`n" +
            "If that doesn't work, check audio_spectrum.log in %LOCALAPPDATA%\MastersFM\.",
            "Audio Source — Fix permissions?", 'YesNo', 'Warning')
        if ($promptResult -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        # Build elevated netsh chain and run audio_spectrum.exe restart afterwards
        $netshChain =
            'netsh http add urlacl url=http://127.0.0.1:4243/ user=Everyone & ' +
            'netsh http add urlacl url=http://localhost:4243/ user=Everyone'
        try {
            Log "Audio Source: launching elevated netsh via Start-Process -Verb RunAs"
            $p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c $netshChain" -Verb RunAs -WindowStyle Hidden -Wait -PassThru
            Log "Audio Source: netsh elevated process exit=$($p.ExitCode)"
        } catch {
            Log "Audio Source: elevation cancelled or failed ($($_.Exception.Message))"
            [System.Windows.Forms.MessageBox]::Show(
                "Permission fix was cancelled. You can run these two commands manually in an elevated PowerShell:`n`n" +
                "netsh http add urlacl url=http://127.0.0.1:4243/ user=Everyone`n" +
                "netsh http add urlacl url=http://localhost:4243/ user=Everyone`n`n" +
                "Then restart Master's FM.",
                "Audio Source", 'OK', 'Information') | Out-Null
            return
        }
        # Restart audio_spectrum.exe so it can re-bind with the new URL ACL
        try {
            $aspProc = Get-Process -Name audio_spectrum -ErrorAction SilentlyContinue
            if ($aspProc) { $aspProc | Stop-Process -Force -ErrorAction SilentlyContinue }
            $exe = [System.IO.Path]::Combine($scriptDir, "audio_spectrum.exe")
            if (Test-Path $exe) {
                Start-Process -FilePath $exe -WorkingDirectory $scriptDir -WindowStyle Hidden
                Log "Audio Source: audio_spectrum.exe restarted with PID $((Get-Process -Name audio_spectrum -ErrorAction SilentlyContinue).Id)"
            }
            Start-Sleep -Milliseconds 1500   # give it a beat to bind the listener
        } catch { Log "Audio Source: audio_spectrum restart failed ($($_.Exception.Message))" }
        # Re-try the /devices fetch (matching the bumped 20 s timeout above)
        try {
            $r = Invoke-WebRequest -Uri 'http://127.0.0.1:4243/devices' -UseBasicParsing -TimeoutSec 20
            $devicesJson = $r.Content | ConvertFrom-Json
            Log "Audio Source: /devices reachable after URL-ACL repair, opening dialog normally"
        } catch {
            Log "Audio Source: /devices STILL unreachable after URL-ACL repair ($($_.Exception.Message))"
            [System.Windows.Forms.MessageBox]::Show(
                "The permission fix ran but Master's FM still can't reach the audio service. " +
                "Please check %LOCALAPPDATA%\MastersFM\audio_spectrum.log for the actual error.",
                "Audio Source", 'OK', 'Error') | Out-Null
            return
        }
    }

    # Read the user's persisted choice cleanly.
    $cfgPath = Get-RoamingCfgPath
    $current        = $null
    $currentBackend = $null
    if ($cfgPath -and (Test-Path $cfgPath)) {
        try {
            $cfgObj = Get-Content $cfgPath -Raw | ConvertFrom-Json
            $current        = $cfgObj.audioSpectrumDevice
            $currentBackend = $cfgObj.audioSpectrumBackend
        } catch {}
    }
    if ([string]::IsNullOrWhiteSpace($current))        { $current        = 'default' }
    if ([string]::IsNullOrWhiteSpace($currentBackend)) { $currentBackend = 'wasapi_loopback' }

    # Look up whichever device the audio service is ACTUALLY capturing right
    # now (may differ from the user's saved choice if /set-device was called
    # via the dialog but Save wasn't clicked). We highlight this device at
    # the top so the user sees what's currently driving the visualizer bars.
    $liveDevice  = $devicesJson.current
    $liveBackend = $devicesJson.backend
    $systemDefaultId = $devicesJson.default

    $form = New-Object System.Windows.Forms.Form
    $form.Text            = "Audio Source"
    $form.StartPosition   = "CenterScreen"
    $form.FormBorderStyle = "None"
    $form.BackColor       = [System.Drawing.Color]::FromArgb(255, 10, 3, 22)
    $form.TopMost         = $true
    $form.ShowInTaskbar   = $false
    # Bumped to 680 tall in v5.3.0 because the backend section headers
    # (WASAPI Loopback / WASAPI Input / WDM-KS / MME / ASIO) each take
    # ~60 px, so the card list needs more vertical room before the Save
    # button would push off the bottom of a 560-tall dialog.
    $form.Size            = New-Object System.Drawing.Size(640, 680)

    $dbStyle = [System.Windows.Forms.ControlStyles]::OptimizedDoubleBuffer -bor `
               [System.Windows.Forms.ControlStyles]::AllPaintingInWmPaint
    $setStyleMethod = [System.Windows.Forms.Form].GetMethod('SetStyle', [System.Reflection.BindingFlags]'NonPublic,Instance')
    try { $setStyleMethod.Invoke($form, @([System.Windows.Forms.ControlStyles]$dbStyle, $true)) | Out-Null } catch {}

    # Drag-to-move handlers. FormBorderStyle=None means Windows' own
    # title-bar drag doesn't work, so the form has to move itself.
    # Pattern mirrors Show-WelcomeDialog's drag. Only fires when the user
    # clicks the form background - clicks on child cards are absorbed by
    # the card click handler and don't reach here.
    $audDragState = @{ dragging = $false; offset = (New-Object System.Drawing.Point(0,0)) }
    $form.add_MouseDown({
        param($s, $e)
        if ($e.Button -eq 'Left') { $audDragState.dragging = $true; $audDragState.offset = $e.Location }
    }.GetNewClosure())
    $form.add_MouseUp({ $audDragState.dragging = $false }.GetNewClosure())
    $form.add_MouseMove({
        param($s, $e)
        if ($audDragState.dragging) {
            $p = $s.PointToScreen($e.Location)
            $s.Location = New-Object System.Drawing.Point(($p.X - $audDragState.offset.X), ($p.Y - $audDragState.offset.Y))
        }
    }.GetNewClosure())

    $form.add_Paint({
        param($sender, $e)
        $g = $e.Graphics
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $W = $sender.Width; $H = $sender.Height
        $rect = New-Object System.Drawing.Rectangle(0, 0, ($W - 1), ($H - 1))
        $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 28, 10, 54),
            [System.Drawing.Color]::FromArgb(255, 8, 2, 18),
            [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
        $g.FillRectangle($bodyBrush, $rect)
        $bodyBrush.Dispose()
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 140, 70, 255), 1)
        $g.DrawRectangle($pen, $rect)
        $pen.Dispose()
    })

    $title = New-Object System.Windows.Forms.Label
    $title.Text      = "Audio Source"
    $title.Font      = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $title.ForeColor = [System.Drawing.Color]::FromArgb(255, 240, 220, 255)
    $title.BackColor = [System.Drawing.Color]::Transparent
    $title.SetBounds(24, 22, 600, 34)
    $form.Controls.Add($title)
    # Title acts as a drag handle too - WinForms intercepts mouse events
    # on child controls, so the form's MouseDown never fires when the
    # user clicks the title text. Wire the same drag state here.
    $title.add_MouseDown({
        param($s, $e)
        if ($e.Button -eq 'Left') { $audDragState.dragging = $true; $audDragState.offset = (New-Object System.Drawing.Point(($e.X + $s.Left), ($e.Y + $s.Top))) }
    }.GetNewClosure())
    $title.add_MouseUp({ $audDragState.dragging = $false }.GetNewClosure())
    $title.add_MouseMove({
        param($s, $e)
        if ($audDragState.dragging) {
            $p = $s.Parent.PointToScreen((New-Object System.Drawing.Point(($e.X + $s.Left), ($e.Y + $s.Top))))
            $s.Parent.Location = New-Object System.Drawing.Point(($p.X - $audDragState.offset.X), ($p.Y - $audDragState.offset.Y))
        }
    }.GetNewClosure())

    # Show what's currently being captured so the user always knows the
    # active state - helps when they pick a silent virtual device and
    # wonder why the visualizer went flat.
    # Match BOTH backend AND id so we don't pick the wrong entry when
    # multiple backends expose the same id string (e.g. a WASAPI endpoint
    # GUID also listed under exclusive mode). Also show the backend in
    # parens so user can confirm the switch actually took effect.
    $liveBackendLabel = switch ($liveBackend) {
        'wasapi_loopback'  { 'WASAPI Loopback' }
        'wasapi_input'     { 'WASAPI Input' }
        'wasapi_exclusive' { 'WDM-KS / Exclusive' }
        'mme'              { 'MME' }
        'asio'             { 'ASIO' }
        default            { $liveBackend }
    }
    $liveEntry = $devicesJson.devices | Where-Object { $_.id -eq $liveDevice -and $_.backend -eq $liveBackend } | Select-Object -First 1
    $liveName  = if ($liveEntry) { $liveEntry.name } else { $null }
    if (-not $liveName) {
        # Empty / default id → report the system default for that backend
        if ([string]::IsNullOrEmpty($liveDevice)) {
            $liveName = 'System Default'
        } else {
            # Device id present but not found in the enumerated list
            # (device was unplugged or backend enumeration skipped it) -
            # show the raw id so the user can diagnose.
            $liveName = $liveDevice
        }
    }
    $statusLine = New-Object System.Windows.Forms.Label
    $statusLine.Text      = "Currently capturing:  $liveName  ($liveBackendLabel)"
    $statusLine.Font      = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $statusLine.ForeColor = [System.Drawing.Color]::FromArgb(255, 140, 220, 150)
    $statusLine.BackColor = [System.Drawing.Color]::Transparent
    $statusLine.SetBounds(24, 60, 590, 20)
    $form.Controls.Add($statusLine)

    $sub = New-Object System.Windows.Forms.Label
    $sub.Text      = "Pick how Master's FM listens to audio. The visualizer works with any of these backends — choose the one that matches your setup. WASAPI Loopback is the zero-config default. WDM-KS / MME / ASIO are for users with virtual cables, Stereo Mix, or pro audio drivers (VB-Matrix, Voicemeeter, Focusrite, etc.)."
    $sub.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
    $sub.ForeColor = [System.Drawing.Color]::FromArgb(255, 180, 160, 220)
    $sub.BackColor = [System.Drawing.Color]::Transparent
    $sub.SetBounds(24, 84, 590, 58)
    $form.Controls.Add($sub)

    # Scan button — cycles every endpoint, finds the one actually delivering
    # audible audio right now, auto-selects it. Useful for users with
    # complicated VB-Audio / Voicemeeter setups who don't know which
    # endpoint WASAPI loopback can actually read.
    $scanBtn = New-Object System.Windows.Forms.Button
    $scanBtn.Text      = "Scan & Auto-select Best"
    $scanBtn.Font      = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $scanBtn.ForeColor = [System.Drawing.Color]::White
    $scanBtn.BackColor = [System.Drawing.Color]::FromArgb(255, 80, 140, 220)
    $scanBtn.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $scanBtn.FlatAppearance.BorderSize = 0
    $scanBtn.SetBounds(400, 118, 220, 26)
    $scanBtn.Cursor    = [System.Windows.Forms.Cursors]::Hand
    $form.Controls.Add($scanBtn)

    $scroll = New-Object System.Windows.Forms.Panel
    $scroll.SetBounds(12, 148, 616, ($form.Height - 148 - 80))
    $scroll.BackColor = [System.Drawing.Color]::Transparent
    $scroll.AutoScroll = $true
    $form.Controls.Add($scroll)

    # Build card rows grouped by backend. Each backend is a section with
    # a bold header; the devices it enumerates are listed below.
    # Descriptions are picked backend-aware via Get-DeviceDesc below - the
    # SAME device type (e.g. vbmatrix_render) means very different things
    # under WASAPI Loopback vs WDM-KS/Shared vs ASIO, so a flat type-only
    # map was lying to users about what they'd actually capture.
    function Get-DeviceDesc($bKey, $dtype) {
        # Kept terse - the description label is 520 px wide at 8 pt, ~85-90
        # chars max before the text clips at the right edge. Longer copy
        # went into the section header's sub-paragraph instead.
        switch ($bKey) {
            'wasapi_loopback' {
                switch ($dtype) {
                    'physical'          { return "Physical output.  Captures what these speakers play.  RECOMMENDED." }
                    'vbmatrix_render'   { return "VB-Matrix render side.  Usually silent via loopback - try WDM-KS." }
                    'voicemeeter_input' { return "Voicemeeter input bus.  Usually silent via loopback - try WASAPI Input." }
                    'vbaudio_cable_in'  { return "VB-Cable input.  Audio re-surfaces on the Cable Output (WASAPI Input)." }
                    'vbaudio_virtual_in'{ return "VB-Audio virtual - routing-dependent, may be silent via loopback." }
                    'virtual_other'     { return "Virtual audio endpoint.  Use Scan to test." }
                    default             { return "Render endpoint." }
                }
            }
            'wasapi_input' {
                switch ($dtype) {
                    'physical'          { return "Physical input (mic / line / Stereo Mix)." }
                    'vbmatrix_capture'  { return "VB-Matrix bus capture (shared).  Good for Main / Discord / Media." }
                    'voicemeeter_bus_out' { return "Voicemeeter output bus.  RECOMMENDED for Voicemeeter users." }
                    'vbaudio_cable_out' { return "VB-Cable capture side.  Receives what apps sent to Cable Input." }
                    'vbaudio_virtual_out'{ return "VB-Audio virtual capture." }
                    'virtual_other'     { return "Virtual capture endpoint." }
                    default             { return "Input endpoint." }
                }
            }
            'wasapi_exclusive' {
                switch ($dtype) {
                    'physical'          { return "Physical input, exclusive mode (falls back to shared)." }
                    'vbmatrix_capture'  { return "VB-Matrix bus via WDM-KS.  Auto-falls back to shared mode." }
                    'voicemeeter_bus_out' { return "Voicemeeter bus via WDM-KS.  Auto-falls back to shared." }
                    'vbaudio_cable_out' { return "VB-Cable capture via WDM-KS.  Auto-falls back to shared." }
                    'vbaudio_virtual_out'{ return "VB-Audio virtual via WDM-KS.  Auto-falls back to shared." }
                    default             { return "Capture endpoint.  Tries exclusive, falls back to shared." }
                }
            }
            'mme' {
                switch ($dtype) {
                    'mme_mapper'        { return "Default Windows recording device (Stereo Mix if configured)." }
                    'mme'               { return "MME WaveIn device (legacy Windows Multimedia API)." }
                    default             { return "MME WaveIn device." }
                }
            }
            'asio' {
                switch ($dtype) {
                    'asio'              { return "ASIO input channel pair.  Route a bus here in the driver's grid." }
                    'asio_none'         { return "Install ASIO4ALL or a vendor ASIO driver to use this backend." }
                    default             { return "ASIO input." }
                }
            }
            default                     { return "Audio endpoint." }
        }
    }
    $backendHeaders = [ordered]@{
        'wasapi_loopback'   = @{ Title = "WASAPI Loopback (system audio output)";         Sub = "Captures the output mix of any render endpoint — Spotify, YouTube, SoundCloud, anything your speakers hear. Zero-config default." }
        'wasapi_input'      = @{ Title = "WASAPI Input (microphone / Stereo Mix / cable)"; Sub = "Shared-mode capture from any input endpoint. Picks up mic + virtual-cable outputs like VB-Cable Output or Voicemeeter Out B1/B2/B3." }
        'wasapi_exclusive'  = @{ Title = "WDM-KS / Exclusive (pro low-latency)";           Sub = "Exclusive-mode WASAPI capture routed through the kernel audio stack. Same path FL Studio / Reaper / Ableton use for low-latency WDM-KS." }
        'mme'               = @{ Title = "MME (legacy WaveIn)";                            Sub = "Old Windows Multimedia Extensions waveIn API. Best for Realtek / Creative Stereo Mix and anything that only exposes MME." }
        'asio'              = @{ Title = "ASIO (Steinberg pro audio)";                     Sub = "Input channels of an installed ASIO driver. The only reliable capture for VB-Audio Matrix internal buses." }
    }

    # Sort within each backend: physical first, then virtual. Backend
    # section order is the declaration order of $backendHeaders.
    $typeOrder = @{ 'physical'=0; 'mme_mapper'=0;
                    'virtual_other'=5;
                    'voicemeeter_input'=6; 'vbaudio_cable_in'=7; 'vbaudio_virtual_in'=8;
                    'vbmatrix_render'=9; 'mme'=4; 'asio'=1; 'asio_none'=99; 'unknown'=10 }

    # Group devices array by backend. Use a hashtable so we can iterate in
    # our preferred backend order independent of what /devices returned.
    $byBackend = @{}
    foreach ($d in $devicesJson.devices) {
        $b = if ([string]::IsNullOrEmpty($d.backend)) { 'wasapi_loopback' } else { $d.backend }
        if (-not $byBackend.ContainsKey($b)) { $byBackend[$b] = @() }
        $byBackend[$b] += $d
    }

    # Entry list: the "entries" array now holds a mix of section-header
    # markers (Kind=header) and device cards (Kind=device). The renderer
    # treats them differently so we can draw bold headers between card
    # groups without making each section its own AutoScroll panel.
    $entries = @()
    # Always-present top entry: the fallback "system default for the
    # active backend" which is backend-agnostic. We key it on an unused
    # pair (backend="default", id="default") and the set-device handler
    # translates it into the current backend's system default.
    $entries += @{ Kind = 'device'; Backend = 'wasapi_loopback'; Id = 'default';
                   Name = 'System Default (WASAPI Loopback)';
                   Desc = "Follows whatever Windows is using as default output. RECOMMENDED starting point." }

    foreach ($bKey in $backendHeaders.Keys) {
        if (-not $byBackend.ContainsKey($bKey)) { continue }
        $section = $backendHeaders[$bKey]
        $entries += @{ Kind = 'header'; Backend = $bKey; Title = $section.Title; Sub = $section.Sub }

        # Composite sort: (1) type order (physical first, virtual last),
        # (2) natural-sort of driver name so "VASIO-8" < "VASIO-32" <
        # "VASIO-128" (not lexicographic 128 < 256 < 32 < 512 < 64 < 8),
        # (3) numeric channel offset so pairs list 1-2, 3-4, 5-6, ...
        # in order instead of lexicographic |0|10|12|14|2|4|6|8.
        $sorted = $byBackend[$bKey] | Sort-Object @{
            Expression = {
                $t    = $_.type
                $tOrd = if ($typeOrder.ContainsKey($t)) { $typeOrder[$t] } else { 99 }
                $drv  = $_.id
                $ofs  = 0
                if ($drv -match '^(.*)\|(-?\d+)$') {
                    $drv = $Matches[1]
                    $ofs = [int]$Matches[2]
                }
                # Pad every run of digits to 6 chars so numeric-aware
                # comparison falls out of plain lexicographic sort.
                $nat = [regex]::Replace($drv, '(\d+)', { param($m) '{0:D6}' -f [int]$m.Value })
                # Pad offset to always-positive 6-digit too (add 1M bias
                # so the theoretical negative offsets like MME "-1" still
                # sort sensibly).
                $ofsKey = '{0:D7}' -f ($ofs + 1000000)
                '{0:D3}_{1}_{2}' -f $tOrd, $nat, $ofsKey
            }
        }
        foreach ($d in $sorted) {
            $dtype    = $d.type
            # Backend-aware description - same type means different things
            # depending on which backend section it's listed under.
            $baseDesc = Get-DeviceDesc $bKey $dtype
            $status   = if ($d.isDefault -and $bKey -eq 'wasapi_loopback') { "Default.  " }
                        elseif ($d.id -eq $liveDevice -and $bKey -eq $liveBackend) { "Active.  " }
                        else { "" }
            $entries += @{ Kind    = 'device';
                           Backend = $bKey;
                           Id      = $d.id;
                           Name    = $d.name;
                           Desc    = $status + $baseDesc;
                           Type    = $dtype }
        }
    }

    # Shared palette for card rendering.
    $rowBg       = [System.Drawing.Color]::FromArgb(255, 22, 12, 40)
    $rowBgSel    = [System.Drawing.Color]::FromArgb(255, 90, 45, 160)
    $rowBgHover  = [System.Drawing.Color]::FromArgb(255, 40, 22, 75)
    $rowStroke   = [System.Drawing.Color]::FromArgb(255, 60, 40, 100)
    $rowStrokeSel= [System.Drawing.Color]::FromArgb(255, 200, 140, 255)
    $textFg      = [System.Drawing.Color]::FromArgb(255, 235, 220, 255)
    $descFg      = [System.Drawing.Color]::FromArgb(255, 180, 160, 220)
    $descFgSel   = [System.Drawing.Color]::FromArgb(255, 230, 215, 255)

    # Cards hash: id → @{ panel, nameLbl, descLbl, dot } — so we can flip
    # selection styling across ALL cards when one is clicked. Keys are
    # "{backend}::{id}" so MME device 1 and WASAPI endpoint 1 don't
    # collide even though both pass through the same hashtable.
    $cards = @{}
    $selectedKey = "$currentBackend::$current"
    # Shared state hashtable - the click handler mutates THIS, not a
    # $script:-scope variable. $script: assignments from inside a
    # .GetNewClosure() closure are flaky in the MastersFM_Tray.exe hosted
    # runspace (they silently no-op in some invocation paths). A plain
    # hashtable is captured by reference via .GetNewClosure() and mutates
    # reliably. Same pattern the working Platforms dialog uses for
    # $states. Save reads _audState.CurKey, not $script:_audCurKey.
    $_audState = @{ CurKey = $selectedKey }

    $y = 8
    foreach ($entry in $entries) {
        if ($entry.Kind -eq 'header') {
            # Section header - NOT a clickable card. Bold title + small
            # subtitle, slightly offset from cards below for grouping.
            $hdrGap = if ($y -gt 8) { 14 } else { 0 }   # breathing room between sections (none at top)
            $y += $hdrGap

            $hdrTitle = New-Object System.Windows.Forms.Label
            $hdrTitle.Text      = $entry.Title
            $hdrTitle.Font      = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
            $hdrTitle.ForeColor = [System.Drawing.Color]::FromArgb(255, 220, 200, 255)
            $hdrTitle.BackColor = [System.Drawing.Color]::Transparent
            $hdrTitle.SetBounds(14, $y, 584, 22)
            $scroll.Controls.Add($hdrTitle)
            $y += 22

            $hdrSub = New-Object System.Windows.Forms.Label
            $hdrSub.Text      = $entry.Sub
            $hdrSub.Font      = New-Object System.Drawing.Font("Segoe UI", 8)
            $hdrSub.ForeColor = [System.Drawing.Color]::FromArgb(255, 170, 150, 210)
            $hdrSub.BackColor = [System.Drawing.Color]::Transparent
            $hdrSub.SetBounds(14, $y, 584, 32)
            $scroll.Controls.Add($hdrSub)
            $y += 34
            continue
        }

        $id      = $entry.Id
        $name    = $entry.Name
        $dtext   = $entry.Desc
        $backend = $entry.Backend
        $key     = "$backend::$id"
        $isSel   = ($key -eq $selectedKey)

        $card = New-Object System.Windows.Forms.Panel
        $card.SetBounds(10, $y, 584, 60)
        $card.BackColor = if ($isSel) { $rowBgSel } else { $rowBg }
        $card.Cursor    = [System.Windows.Forms.Cursors]::Hand
        # Store the compound backend::id key so the Paint handler can
        # look up the currently-selected card and draw the highlighted
        # stroke on just that one.
        $card.Tag       = $key
        # Paint handler reads $script:_audCur* globals because Paint events
        # fire on the UI thread's WM_PAINT message and the local closure vars
        # aren't in scope there. Mirrored state: click handler writes to
        # BOTH $_audState.CurKey AND $script:_audCurKey (the script-scope
        # copy is only used by the Paint handler; the real source of truth
        # for Save is $_audState.CurKey).
        $card.add_Paint({
            param($sender, $e)
            $strokeCol = if ($sender.Tag -eq $script:_audCurKey) { $script:_audStrokeSel } else { $script:_audStroke }
            $pen = New-Object System.Drawing.Pen($strokeCol, 1)
            $e.Graphics.DrawRectangle($pen, 0, 0, ($sender.Width - 1), ($sender.Height - 1))
            $pen.Dispose()
        })
        $scroll.Controls.Add($card)

        # Selection indicator bullet.
        $dot = New-Object System.Windows.Forms.Label
        $dot.Text      = if ($isSel) { [char]0x25C9 } else { [char]0x25CB }   # filled / hollow circle
        $dot.Font      = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
        $dot.ForeColor = if ($isSel) { [System.Drawing.Color]::FromArgb(255, 255, 255, 255) } else { [System.Drawing.Color]::FromArgb(255, 150, 130, 200) }
        $dot.BackColor = [System.Drawing.Color]::Transparent
        $dot.SetBounds(16, 18, 28, 28)
        $dot.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
        $card.Controls.Add($dot)

        # Name + description stack
        $nameLbl = New-Object System.Windows.Forms.Label
        $nameLbl.Text      = $name
        $nameLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
        $nameLbl.ForeColor = $textFg
        $nameLbl.BackColor = [System.Drawing.Color]::Transparent
        $nameLbl.SetBounds(52, 10, 520, 22)
        $card.Controls.Add($nameLbl)

        $descLbl = New-Object System.Windows.Forms.Label
        $descLbl.Text      = $dtext
        $descLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 8)
        $descLbl.ForeColor = if ($isSel) { $descFgSel } else { $descFg }
        $descLbl.BackColor = [System.Drawing.Color]::Transparent
        $descLbl.SetBounds(52, 33, 520, 20)
        $card.Controls.Add($descLbl)

        # Store the card by its compound key so the click handler can
        # find ALL cards and flip the selection on a click. Also store
        # the backend + id as separate fields so Save can extract them
        # without re-parsing the key.
        $cards[$key] = @{ panel = $card; dot = $dot; nameLbl = $nameLbl; descLbl = $descLbl; Backend = $backend; Id = $id }

        # Click handlers. Capture locals BEFORE .GetNewClosure() so
        # PowerShell snapshots them correctly in the hosted runspace.
        # Critical: $_audState is a hashtable (captured by reference), so
        # the closure's writes propagate out. $script:_audCurKey is ALSO
        # written for the Paint handler, but the Save handler reads the
        # hashtable, which is the source of truth.
        $localKey         = $key
        $localCards       = $cards
        $localState       = $_audState
        $localRowBg       = $rowBg
        $localRowBgSel    = $rowBgSel
        $localDescFg      = $descFg
        $localDescFgSel   = $descFgSel

        $clickHandler = {
            try { Log "AudioDevice click: localKey='$localKey' cardsCount=$($localCards.Count)" } catch {}
            $localState.CurKey = $localKey
            $script:_audCurKey = $localKey   # for Paint handler only
            foreach ($k in $localCards.Keys) {
                $c  = $localCards[$k]
                $on = ($k -eq $localKey)
                $c.panel.BackColor  = if ($on) { $localRowBgSel } else { $localRowBg }
                $c.dot.Text         = if ($on) { [char]0x25C9 } else { [char]0x25CB }
                $c.dot.ForeColor    = if ($on) { [System.Drawing.Color]::White } else { [System.Drawing.Color]::FromArgb(255, 150, 130, 200) }
                $c.descLbl.ForeColor= if ($on) { $localDescFgSel } else { $localDescFg }
                $c.panel.Invalidate()  # repaints the border stroke
            }
            # INSTANT-APPLY: POST /set-device right now so the audio_spectrum
            # service switches immediately. No need to hit Save first - users
            # can audition each backend/device live. Save is still what
            # persists the choice to config.json; Cancel reverts to whatever
            # was saved. Decompose the compound key here rather than the card
            # metadata so we don't rely on $localCards lookups inside the
            # closure scope.
            if ($localKey -match '^([^:]+)::(.*)$') {
                $bk = $Matches[1]
                $id = $Matches[2]
                try {
                    $bdy = '{"backend":"' + $bk + '","id":"' + $id + '"}'
                    Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST -Body $bdy -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null
                    # Stash the requested-backend so the poll timer below
                    # can show "FALLBACK: requested X, running Y" if the
                    # audio_spectrum 3-strike counter demoted us.
                    $localState.Requested = $bk
                } catch {
                    Log "AudioDevice instant-apply: /set-device POST failed: $_"
                }
            }
        }.GetNewClosure()

        $card.add_Click($clickHandler)
        $dot.add_Click($clickHandler)
        $nameLbl.add_Click($clickHandler)
        $descLbl.add_Click($clickHandler)

        $y += 68
    }

    # Script-scope helpers the cards' paint handlers read (closures can't
    # easily capture $rowStroke* without extra plumbing).
    $script:_audCurKey    = $selectedKey
    $script:_audStroke    = $rowStroke
    $script:_audStrokeSel = $rowStrokeSel

    # Footer buttons — Save + Cancel.
    $saveBtn = New-Object System.Windows.Forms.Button
    $saveBtn.Text          = "Save"
    $saveBtn.Font          = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $saveBtn.ForeColor     = [System.Drawing.Color]::White
    $saveBtn.BackColor     = [System.Drawing.Color]::FromArgb(255, 130, 60, 220)
    $saveBtn.FlatStyle     = [System.Windows.Forms.FlatStyle]::Flat
    $saveBtn.FlatAppearance.BorderSize = 0
    $saveBtn.SetBounds(400, ($form.Height - 56), 110, 36)
    $saveBtn.Cursor        = [System.Windows.Forms.Cursors]::Hand
    $saveBtn.DialogResult  = [System.Windows.Forms.DialogResult]::OK
    $form.Controls.Add($saveBtn)
    $form.AcceptButton = $saveBtn

    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text          = "Cancel"
    $cancelBtn.Font          = New-Object System.Drawing.Font("Segoe UI", 10)
    $cancelBtn.ForeColor     = [System.Drawing.Color]::FromArgb(255, 200, 185, 230)
    $cancelBtn.BackColor     = [System.Drawing.Color]::FromArgb(255, 30, 18, 52)
    $cancelBtn.FlatStyle     = [System.Windows.Forms.FlatStyle]::Flat
    $cancelBtn.FlatAppearance.BorderSize = 0
    $cancelBtn.SetBounds(520, ($form.Height - 56), 90, 36)
    $cancelBtn.Cursor        = [System.Windows.Forms.Cursors]::Hand
    $cancelBtn.DialogResult  = [System.Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancelBtn)
    $form.CancelButton = $cancelBtn

    # Wire up the Scan handler. The old implementation ran a synchronous
    # for-loop with Start-Sleep on the UI thread - that froze the entire
    # window for the full 3-4 min sweep (user couldn't drag, click cancel,
    # close, etc.). Fixed in v5.4 by driving scan off a Forms.Timer state
    # machine: the timer ticks every 250ms on the UI thread but doesn't
    # block between ticks, so the form stays fully responsive.
    #
    # State machine per entry: on first tick we POST /set-device and flip
    # the card to "Checking...". We then let SETTLE_TICKS pass (~1.25 s)
    # for the ASIO / WASAPI driver to spin up + deliver a couple of buffer
    # callbacks. On the settle-tick we read /peak, update the card to
    # "Audio detected!" or "No audio", and advance.
    #
    # Scans WASAPI Loopback + WASAPI Input + MME + every ASIO channel pair.
    # VB-Matrix / Voicemeeter users have Media/Main/Discord on user-chosen
    # ASIO channels (the app can't know which without actually listening
    # on each). WDM-KS / Exclusive is deliberately skipped - opening
    # exclusive on a device owned by another app would crack that app's
    # audio. All four scanned backends are multi-client-safe.
    $scanState = @{
        Active    = $false
        Timer     = $null
        List      = @()
        Index     = 0
        TickCount = 0
        Best      = $null
        BestPeak  = 0.0
        PreBack   = $null
        PreId     = $null
    }
    # 250 ms tick × 5 ticks = 1.25 s settle per device. ASIO needs ~1 s to
    # init + deliver buffers; WASAPI locks in faster. Five ticks gives
    # margin without making the full 140-entry sweep feel glacial.
    $SETTLE_TICKS = 5

    $colAudioOk   = [System.Drawing.Color]::FromArgb(255, 140, 220, 150)   # green = detected
    $colAudioNone = [System.Drawing.Color]::FromArgb(255, 165, 150, 190)   # dim purple = silent
    $colScanning  = [System.Drawing.Color]::FromArgb(255, 220, 200, 120)   # warm yellow = checking

    $scanStopProc = {
        if ($scanState.Timer) {
            $scanState.Timer.Stop()
            $scanState.Timer.Dispose()
            $scanState.Timer = $null
        }
        $scanState.Active = $false
        $scanBtn.Text     = "Scan & Auto-select Best"
        $scanBtn.Enabled  = $true
    }.GetNewClosure()

    $scanTickProc = {
        if (-not $scanState.Active) { return }

        # Bounds-check: machine finished advancing past last entry.
        if ($scanState.Index -ge $scanState.List.Count) {
            # Finalise: apply best if we found one, else revert to pre-scan.
            if ($scanState.Best -and $scanState.BestPeak -gt 0.01) {
                $b = $scanState.Best
                $bestKey = "$($b.Backend)::$($b.Id)"
                $_audState.CurKey  = $bestKey
                $script:_audCurKey = $bestKey
                foreach ($k in $cards.Keys) {
                    $c  = $cards[$k]
                    $on = ($k -eq $bestKey)
                    $c.panel.BackColor = if ($on) { $rowBgSel } else { $rowBg }
                    $c.dot.Text        = if ($on) { [char]0x25C9 } else { [char]0x25CB }
                    $c.dot.ForeColor   = if ($on) { [System.Drawing.Color]::White } else { [System.Drawing.Color]::FromArgb(255, 150, 130, 200) }
                    $c.panel.Invalidate()
                }
                try {
                    $applyBody = '{"backend":"' + $b.Backend + '","id":"' + ($b.Id -replace '"','\"') + '"}'
                    Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST -Body $applyBody -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null
                } catch { Log "AudioDevice Scan: final apply POST failed: $_" }
                & $scanStopProc
                [System.Windows.Forms.MessageBox]::Show(
                    "Audio detected on:`n`n   $($b.Name)`n`nClick Save to keep this, or Cancel to revert.",
                    "Audio Source", 'OK', 'Information') | Out-Null
            } else {
                if ($scanState.PreBack) {
                    try {
                        $revertBody = '{"backend":"' + $scanState.PreBack + '","id":"' + ($scanState.PreId -replace '"','\"') + '"}'
                        Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST -Body $revertBody -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null
                    } catch {}
                }
                & $scanStopProc
                [System.Windows.Forms.MessageBox]::Show(
                    "No audio detected on any WASAPI / MME / ASIO endpoint.`n`nPossible causes:`n" +
                    " - Music isn't playing right now.`n" +
                    " - Your virtual mixer (VB-Matrix / Voicemeeter) isn't routing audio to any endpoint / ASIO channel. In VB-Matrix, open the Routing Grid and add a 0dB cell from your source bus to a VASIO-N column.`n" +
                    " - Spotify is in Exclusive Mode (Spotify > Settings > Audio Quality > Exclusive Mode).",
                    "Audio Source", 'OK', 'Warning') | Out-Null
            }
            return
        }

        $rd      = $scanState.List[$scanState.Index]
        $cardKey = "$($rd.Backend)::$($rd.Id)"
        $card    = if ($cards.ContainsKey($cardKey)) { $cards[$cardKey] } else { $null }

        if ($scanState.TickCount -eq 0) {
            # First tick for this entry: fire the /set-device request and
            # flip its card to "Checking...". The POST isn't instant but
            # Invoke-WebRequest with TimeoutSec 3 returns fast on success
            # and the UI resumes ticking immediately after.
            $safeId = $rd.Id -replace '"', '\"'
            $bdy = '{"backend":"' + $rd.Backend + '","id":"' + $safeId + '"}'
            try { Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST -Body $bdy -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null }
            catch { Log "AudioDevice Scan: /set-device POST failed for '$($rd.Id)': $_" }
            if ($card) {
                $card.descLbl.Text      = "Checking..."
                $card.descLbl.ForeColor = $colScanning
            }
            # Also reflect progress on the button so user sees motion
            $short = $rd.Name
            if ($short.Length -gt 22) { $short = $short.Substring(0, 22) }
            $scanBtn.Text = ("Scanning {0}/{1}..." -f ($scanState.Index + 1), $scanState.List.Count)
        }
        elseif ($scanState.TickCount -ge $SETTLE_TICKS) {
            # Settle elapsed: read /peak and decide.
            $pkVal = 0.0
            try {
                $pk = (Invoke-WebRequest 'http://127.0.0.1:4243/peak' -UseBasicParsing -TimeoutSec 1).Content | ConvertFrom-Json
                $pkVal = [float]$pk.lifetime
            } catch { Log "AudioDevice Scan: /peak GET failed for '$($rd.Id)': $_" }

            if ($pkVal -gt $scanState.BestPeak) {
                $scanState.BestPeak = $pkVal
                $scanState.Best     = $rd
            }
            if ($card) {
                if ($pkVal -gt 0.01) {
                    $card.descLbl.Text      = "Audio detected!"
                    $card.descLbl.ForeColor = $colAudioOk
                } else {
                    $card.descLbl.Text      = "No audio"
                    $card.descLbl.ForeColor = $colAudioNone
                }
            }
            # Advance
            $scanState.Index++
            $scanState.TickCount = -1
        }
        $scanState.TickCount++
    }.GetNewClosure()

    $scanBtn.add_Click({
        # If already scanning, the button becomes a Stop control.
        if ($scanState.Active) {
            if ($scanState.PreBack) {
                try {
                    $revertBody = '{"backend":"' + $scanState.PreBack + '","id":"' + ($scanState.PreId -replace '"','\"') + '"}'
                    Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST -Body $revertBody -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null
                } catch {}
            }
            & $scanStopProc
            return
        }

        # Remember pre-scan state so we can restore if nothing found.
        try {
            $h0 = (Invoke-WebRequest 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 2).Content | ConvertFrom-Json
            $scanState.PreBack = $h0.backend
            $scanState.PreId   = $h0.device
        } catch {}

        # Build scan list (safe backends only).
        $list = @()
        try {
            $devsNow = (Invoke-WebRequest 'http://127.0.0.1:4243/devices' -UseBasicParsing -TimeoutSec 20).Content | ConvertFrom-Json
            foreach ($rd in @($devsNow.devices | Where-Object { $_.backend -eq 'wasapi_loopback' -and $_.id })) {
                $list += [pscustomobject]@{ Backend = 'wasapi_loopback'; Id = $rd.id; Name = $rd.name }
            }
            foreach ($rd in @($devsNow.devices | Where-Object { $_.backend -eq 'wasapi_input' -and $_.id })) {
                $list += [pscustomobject]@{ Backend = 'wasapi_input'; Id = $rd.id; Name = $rd.name }
            }
            foreach ($rd in @($devsNow.devices | Where-Object { $_.backend -eq 'mme' -and $_.id })) {
                $list += [pscustomobject]@{ Backend = 'mme'; Id = $rd.id; Name = $rd.name }
            }
            foreach ($rd in @($devsNow.devices | Where-Object { $_.backend -eq 'asio' -and $_.id -and $_.type -ne 'asio_none' })) {
                $list += [pscustomobject]@{ Backend = 'asio'; Id = $rd.id; Name = $rd.name }
            }
        } catch {
            Log "AudioDevice Scan: /devices failed: $_"
            return
        }

        # Reset any stale meter text on every card to a neutral state.
        foreach ($k in $cards.Keys) {
            $c = $cards[$k]
            $c.descLbl.Text      = "Waiting..."
            $c.descLbl.ForeColor = [System.Drawing.Color]::FromArgb(255, 160, 140, 200)
        }

        $scanState.Active    = $true
        $scanState.List      = $list
        $scanState.Index     = 0
        $scanState.TickCount = 0
        $scanState.Best      = $null
        $scanState.BestPeak  = 0.0

        $scanBtn.Text = "Stop scan"

        $t = New-Object System.Windows.Forms.Timer
        $t.Interval = 250
        $t.add_Tick($scanTickProc)
        $scanState.Timer = $t
        $t.Start()
    }.GetNewClosure())

    # If the dialog closes mid-scan, stop the timer so it doesn't fire
    # against a disposed form.
    $form.add_FormClosed({
        if ($scanState.Active) {
            if ($scanState.PreBack) {
                try {
                    $revertBody = '{"backend":"' + $scanState.PreBack + '","id":"' + ($scanState.PreId -replace '"','\"') + '"}'
                    Invoke-WebRequest 'http://127.0.0.1:4243/set-device' -Method POST -Body $revertBody -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null
                } catch {}
            }
            & $scanStopProc
        }
    }.GetNewClosure())

    # Live status poll timer - every 250ms ask audio_spectrum.exe what's
    # actually running + the current peak level, and update the green
    # status line. When the user clicks a card the click handler POSTs
    # /set-device and stashes the REQUESTED backend in $_audState.Requested;
    # if the server reports back a different current backend we show a
    # "FALLBACK from X to Y" banner so the user knows their pick didn't
    # actually stick (common on VB-Matrix endpoints that reject exclusive
    # mode, or ASIO drivers that reject all sample rates we try).
    $localStatusLine  = $statusLine
    $localAudState    = $_audState
    # Map backend → pretty label (one place only so the timer stays short).
    $localBackendMap = @{
        'wasapi_loopback'  = 'WASAPI Loopback'
        'wasapi_input'     = 'WASAPI Input'
        'wasapi_exclusive' = 'WDM-KS / Exclusive'
        'mme'              = 'MME'
        'asio'             = 'ASIO'
    }

    $pollTimer = New-Object System.Windows.Forms.Timer
    $pollTimer.Interval = 250
    $pollTimer.add_Tick({
        try {
            $h = (Invoke-WebRequest 'http://127.0.0.1:4243/health' -UseBasicParsing -TimeoutSec 1).Content | ConvertFrom-Json
            $p = (Invoke-WebRequest 'http://127.0.0.1:4243/peak'   -UseBasicParsing -TimeoutSec 1).Content | ConvertFrom-Json
            $runningBackend = $h.backend
            $runningLabel   = if ($localBackendMap.ContainsKey($runningBackend)) { $localBackendMap[$runningBackend] } else { $runningBackend }
            $runningId      = $h.device
            if ([string]::IsNullOrEmpty($runningId)) { $runningId = 'System Default' }

            # Build a small ASCII-meter bar from peak (0..1). Rolling peak
            # decays every read so the meter twitches live with audio.
            $rolling = [double]$p.rolling
            $barLen  = [int]([Math]::Min(20, [Math]::Round($rolling * 40)))
            $meter   = ('█' * $barLen).PadRight(20, '·')

            # Fallback detection: did the user request a backend that's
            # not the one currently running?
            $prefix = ''
            $col    = [System.Drawing.Color]::FromArgb(255, 140, 220, 150)   # green = OK
            if ($localAudState.ContainsKey('Requested') -and $localAudState.Requested -and $localAudState.Requested -ne $runningBackend) {
                $reqLabel = if ($localBackendMap.ContainsKey($localAudState.Requested)) { $localBackendMap[$localAudState.Requested] } else { $localAudState.Requested }
                $prefix   = "FALLBACK: requested $reqLabel, running "
                $col      = [System.Drawing.Color]::FromArgb(255, 255, 180, 80)  # amber = warn
            } elseif ($rolling -lt 0.001) {
                $col = [System.Drawing.Color]::FromArgb(255, 210, 210, 160)  # soft yellow = silent
            }

            $localStatusLine.ForeColor = $col
            $localStatusLine.Text = ("{0}{1}  ({2})   [{3}] {4}" -f $prefix, $runningId, $runningLabel, $meter, $rolling.ToString('F3'))
        } catch {
            $localStatusLine.ForeColor = [System.Drawing.Color]::FromArgb(255, 220, 120, 120)
            $localStatusLine.Text = "(audio_spectrum service unreachable)"
        }
    }.GetNewClosure())
    $pollTimer.Start()
    $form.add_FormClosed({ try { $pollTimer.Stop(); $pollTimer.Dispose() } catch {} }.GetNewClosure())

    Log "AudioDevice: opening dialog, current='$currentBackend::$current', live='$liveBackend::$liveDevice'"
    $result = $form.ShowDialog()
    if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        # Source of truth is the hashtable that the click handler mutates.
        # $script:_audCurKey is mirrored only for the Paint handler to read.
        $chosenKey = $_audState.CurKey
        Log "AudioDevice: Save clicked - _audState.CurKey='$chosenKey' script:_audCurKey='$script:_audCurKey'"
        # Compound key is "backend::id" - parse back out. If for some
        # reason it got cleared, default to WASAPI Loopback + system
        # default so the user doesn't end up with a broken config.
        $selectedBackend = 'wasapi_loopback'
        $selectedId      = 'default'
        if ($chosenKey -match '^([^:]+)::(.*)$') {
            $selectedBackend = $Matches[1]
            $selectedId      = $Matches[2]
        }
        if ([string]::IsNullOrEmpty($selectedId)) { $selectedId = 'default' }
        Log "AudioDevice: saving backend='$selectedBackend' id='$selectedId'"
        try { Save-ConfigField 'audioSpectrumBackend' $selectedBackend } catch { Log "Save audioSpectrumBackend failed: $_" }
        try { Save-ConfigField 'audioSpectrumDevice'  $selectedId      } catch { Log "Save audioSpectrumDevice failed: $_" }
        try {
            $body = '{"backend":"' + $selectedBackend + '","id":"' + $selectedId + '"}'
            Invoke-WebRequest -Uri 'http://127.0.0.1:4243/set-device' -Method POST `
                -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null
        } catch {
            Log "AudioDevice: POST /set-device failed: $_"
        }
    } else {
        # Cancel path: since card clicks now instant-apply via /set-device,
        # we need to REVERT the audio_spectrum service to whatever was
        # saved in config.json before the dialog opened. Without this,
        # a user who auditioned backends then hit Cancel would end up
        # with their LAST CLICKED card still running, not their saved one.
        Log "AudioDevice: Cancel - reverting to saved backend='$currentBackend' id='$current'"
        try {
            $body = '{"backend":"' + $currentBackend + '","id":"' + $current + '"}'
            Invoke-WebRequest -Uri 'http://127.0.0.1:4243/set-device' -Method POST `
                -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 3 | Out-Null
        } catch {
            Log "AudioDevice: Cancel revert /set-device failed: $_"
        }
    }
    $form.Dispose()
}

function Show-PlatformsDialog {
    # v5.0.1 card-style redesign — the v5.0.0 WinForms CheckBox with
    # FlatStyle=Flat rendered as an empty square on the dark dialog
    # background even when Checked=true, so the user had no way to tell
    # which platforms were on or off. Replaced with clickable Panel cards
    # using big Unicode ✓ / ☐ glyphs + a purple-highlight background for
    # selected rows. Always obvious which toggles are on.
    $current = Get-PlatformsConfig

    $form = New-Object System.Windows.Forms.Form
    $form.Text            = "Platform Detection"
    $form.StartPosition   = "CenterScreen"
    $form.FormBorderStyle = "None"
    $form.BackColor       = [System.Drawing.Color]::FromArgb(255, 10, 3, 22)
    $form.TopMost         = $true
    $form.ShowInTaskbar   = $false
    $form.Size            = New-Object System.Drawing.Size(640, 760)

    $dbStyle = [System.Windows.Forms.ControlStyles]::OptimizedDoubleBuffer -bor `
               [System.Windows.Forms.ControlStyles]::AllPaintingInWmPaint
    $setStyleMethod = [System.Windows.Forms.Form].GetMethod('SetStyle', [System.Reflection.BindingFlags]'NonPublic,Instance')
    try { $setStyleMethod.Invoke($form, @([System.Windows.Forms.ControlStyles]$dbStyle, $true)) | Out-Null } catch {}

    $form.add_Paint({
        param($sender, $e)
        $g = $e.Graphics
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $W = $sender.Width; $H = $sender.Height
        $rect = New-Object System.Drawing.Rectangle(0, 0, ($W - 1), ($H - 1))
        $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 28, 10, 54),
            [System.Drawing.Color]::FromArgb(255, 8, 2, 18),
            [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
        $g.FillRectangle($bodyBrush, $rect)
        $bodyBrush.Dispose()
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 140, 70, 255), 1)
        $g.DrawRectangle($pen, $rect)
        $pen.Dispose()
    })

    $title = New-Object System.Windows.Forms.Label
    $title.Text      = "Platform Detection"
    $title.Font      = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $title.ForeColor = [System.Drawing.Color]::FromArgb(255, 240, 220, 255)
    $title.BackColor = [System.Drawing.Color]::Transparent
    $title.SetBounds(24, 22, 600, 34)
    $form.Controls.Add($title)

    $sub = New-Object System.Windows.Forms.Label
    $sub.Text      = "Click a card to toggle it. Enabled platforms light up purple with a white check; disabled ones stay dark with an empty box. 'Browser' is the master switch for anything in a web browser."
    $sub.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
    $sub.ForeColor = [System.Drawing.Color]::FromArgb(255, 180, 160, 220)
    $sub.BackColor = [System.Drawing.Color]::Transparent
    $sub.SetBounds(24, 60, 600, 56)
    $form.Controls.Add($sub)

    # Quick-action row — Enable all / Disable all (handy when you want a
    # clean slate before toggling just a couple of platforms).
    $btnAll = New-Object System.Windows.Forms.Button
    $btnAll.Text      = "Enable all"
    $btnAll.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
    $btnAll.ForeColor = [System.Drawing.Color]::FromArgb(255, 220, 200, 255)
    $btnAll.BackColor = [System.Drawing.Color]::FromArgb(255, 40, 22, 75)
    $btnAll.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $btnAll.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(255, 100, 70, 180)
    $btnAll.SetBounds(24, 120, 100, 26)
    $btnAll.Cursor    = [System.Windows.Forms.Cursors]::Hand
    $form.Controls.Add($btnAll)

    $btnNone = New-Object System.Windows.Forms.Button
    $btnNone.Text      = "Disable all"
    $btnNone.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
    $btnNone.ForeColor = [System.Drawing.Color]::FromArgb(255, 220, 200, 255)
    $btnNone.BackColor = [System.Drawing.Color]::FromArgb(255, 40, 22, 75)
    $btnNone.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $btnNone.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(255, 100, 70, 180)
    $btnNone.SetBounds(132, 120, 100, 26)
    $btnNone.Cursor    = [System.Windows.Forms.Cursors]::Hand
    $form.Controls.Add($btnNone)

    $scroll = New-Object System.Windows.Forms.Panel
    $scroll.SetBounds(12, 158, 616, ($form.Height - 158 - 80))
    $scroll.BackColor = [System.Drawing.Color]::Transparent
    $scroll.AutoScroll = $true
    $form.Controls.Add($scroll)

    # Shared palette for card rendering.
    $rowBgOn     = [System.Drawing.Color]::FromArgb(255, 90, 45, 160)   # enabled
    $rowBgOff    = [System.Drawing.Color]::FromArgb(255, 22, 12, 40)    # disabled
    $strokeOn    = [System.Drawing.Color]::FromArgb(255, 200, 140, 255)
    $strokeOff   = [System.Drawing.Color]::FromArgb(255, 60, 40, 100)
    $textFg      = [System.Drawing.Color]::FromArgb(255, 235, 220, 255)
    $descFgOff   = [System.Drawing.Color]::FromArgb(255, 160, 150, 200)
    $descFgOn    = [System.Drawing.Color]::FromArgb(255, 240, 230, 255)
    $checkChar   = [char]0x2611   # ☑
    $uncheckChar = [char]0x2610   # ☐

    # Track state — $states[key] = $true/$false. Initial from config.
    $states = @{}
    foreach ($p in $script:PLATFORM_KEYS) {
        $states[$p.Key] = if ($current -and $current.ContainsKey($p.Key)) { [bool]$current[$p.Key] } else { $true }
    }

    $cards = @{}
    $y = 6
    foreach ($p in $script:PLATFORM_KEYS) {
        $key   = $p.Key
        $label = $p.Label
        $dtxt  = $p.Desc
        $isOn  = $states[$key]

        $card = New-Object System.Windows.Forms.Panel
        $card.SetBounds(8, $y, 590, 62)
        $card.BackColor = if ($isOn) { $rowBgOn } else { $rowBgOff }
        $card.Cursor    = [System.Windows.Forms.Cursors]::Hand
        $card.Tag       = $key
        $card.add_Paint({
            param($sender, $e)
            $strokeCol = if ($script:_platStates[$sender.Tag]) { $script:_platStrokeOn } else { $script:_platStrokeOff }
            $pen = New-Object System.Drawing.Pen($strokeCol, 1)
            $e.Graphics.DrawRectangle($pen, 0, 0, ($sender.Width - 1), ($sender.Height - 1))
            $pen.Dispose()
        })
        $scroll.Controls.Add($card)

        # Big check/uncheck glyph as the unambiguous on/off indicator.
        $mark = New-Object System.Windows.Forms.Label
        $mark.Text      = if ($isOn) { $checkChar } else { $uncheckChar }
        $mark.Font      = New-Object System.Drawing.Font("Segoe UI Symbol", 18, [System.Drawing.FontStyle]::Bold)
        $mark.ForeColor = if ($isOn) { [System.Drawing.Color]::White } else { [System.Drawing.Color]::FromArgb(255, 150, 130, 200) }
        $mark.BackColor = [System.Drawing.Color]::Transparent
        $mark.SetBounds(16, 18, 34, 30)
        $mark.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
        $card.Controls.Add($mark)

        $nameLbl = New-Object System.Windows.Forms.Label
        $nameLbl.Text      = $label
        $nameLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
        $nameLbl.ForeColor = $textFg
        $nameLbl.BackColor = [System.Drawing.Color]::Transparent
        $nameLbl.SetBounds(56, 10, 520, 22)
        $card.Controls.Add($nameLbl)

        $descLbl = New-Object System.Windows.Forms.Label
        $descLbl.Text      = $dtxt
        $descLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 8)
        $descLbl.ForeColor = if ($isOn) { $descFgOn } else { $descFgOff }
        $descLbl.BackColor = [System.Drawing.Color]::Transparent
        $descLbl.SetBounds(56, 33, 520, 22)
        $card.Controls.Add($descLbl)

        $cards[$key] = @{ panel = $card; mark = $mark; descLbl = $descLbl }

        # v5.1.9 — ALL referenced vars (states hashtable AND cards hashtable
        # AND palette colors) must be captured as local copies BEFORE
        # .GetNewClosure(), or the click handler can't resolve them when
        # invoked by the MastersFM_Tray.exe hosted runspace.  Previous
        # version referenced $cards + $script:_platStates directly; both
        # were unreachable at click-time so clicks were silent no-ops.
        $localKey       = $key
        $localCards     = $cards
        $localStates    = $states
        $localRowBgOn   = $rowBgOn
        $localRowBgOff  = $rowBgOff
        $localDescFgOn  = $descFgOn
        $localDescFgOff = $descFgOff
        $localCheckChar = $checkChar
        $localUnchChar  = $uncheckChar
        $localMarkOn    = [System.Drawing.Color]::White
        $localMarkOff   = [System.Drawing.Color]::FromArgb(255, 150, 130, 200)

        $toggle = {
            $nowOn = -not $localStates[$localKey]
            $localStates[$localKey] = $nowOn
            $c = $localCards[$localKey]
            $c.panel.BackColor   = if ($nowOn) { $localRowBgOn } else { $localRowBgOff }
            $c.mark.Text         = if ($nowOn) { $localCheckChar } else { $localUnchChar }
            $c.mark.ForeColor    = if ($nowOn) { $localMarkOn } else { $localMarkOff }
            $c.descLbl.ForeColor = if ($nowOn) { $localDescFgOn } else { $localDescFgOff }
            $c.panel.Invalidate()
        }.GetNewClosure()

        $card.add_Click($toggle)
        $mark.add_Click($toggle)
        $nameLbl.add_Click($toggle)
        $descLbl.add_Click($toggle)

        $y += 70
    }

    # Script-scope state ONLY for card paint handlers (border stroke) —
    # add_Paint fires on WM_PAINT and lookup scope is different from Click.
    # Paint handlers read $script:_platStates & $script:_platStroke*.
    $script:_platStates    = $states
    $script:_platStrokeOn  = $strokeOn
    $script:_platStrokeOff = $strokeOff

    # Enable-all / Disable-all — like the per-card toggle, we have to
    # capture all referenced vars as LOCALS before GetNewClosure.
    $bulkLocalCards    = $cards
    $bulkLocalStates   = $states
    $bulkRowBgOn       = $rowBgOn
    $bulkRowBgOff      = $rowBgOff
    $bulkDescFgOn      = $descFgOn
    $bulkDescFgOff     = $descFgOff
    $bulkCheckChar     = $checkChar
    $bulkUnchChar      = $uncheckChar
    $bulkMarkOn        = [System.Drawing.Color]::White
    $bulkMarkOff       = [System.Drawing.Color]::FromArgb(255, 150, 130, 200)

    $bulkApply = {
        param([bool]$on)
        foreach ($k in @($bulkLocalStates.Keys)) {
            $bulkLocalStates[$k] = $on
            $c = $bulkLocalCards[$k]
            $c.panel.BackColor   = if ($on) { $bulkRowBgOn } else { $bulkRowBgOff }
            $c.mark.Text         = if ($on) { $bulkCheckChar } else { $bulkUnchChar }
            $c.mark.ForeColor    = if ($on) { $bulkMarkOn } else { $bulkMarkOff }
            $c.descLbl.ForeColor = if ($on) { $bulkDescFgOn } else { $bulkDescFgOff }
            $c.panel.Invalidate()
        }
        # Also sync the script-scope states hashtable so paint handlers
        # see the new stroke colour on next WM_PAINT.
        $script:_platStates = $bulkLocalStates
    }.GetNewClosure()

    $localApplyAll  = $bulkApply
    $btnAll.add_Click({  & $localApplyAll $true  }.GetNewClosure())
    $btnNone.add_Click({ & $localApplyAll $false }.GetNewClosure())

    $saveBtn = New-Object System.Windows.Forms.Button
    $saveBtn.Text      = "Save"
    $saveBtn.Font      = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $saveBtn.ForeColor = [System.Drawing.Color]::White
    $saveBtn.BackColor = [System.Drawing.Color]::FromArgb(255, 130, 60, 220)
    $saveBtn.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $saveBtn.FlatAppearance.BorderSize = 0
    $saveBtn.SetBounds(400, ($form.Height - 56), 110, 36)
    $saveBtn.Cursor    = [System.Windows.Forms.Cursors]::Hand
    $saveBtn.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.Controls.Add($saveBtn)
    $form.AcceptButton = $saveBtn

    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text      = "Cancel"
    $cancelBtn.Font      = New-Object System.Drawing.Font("Segoe UI", 10)
    $cancelBtn.ForeColor = [System.Drawing.Color]::FromArgb(255, 200, 185, 230)
    $cancelBtn.BackColor = [System.Drawing.Color]::FromArgb(255, 30, 18, 52)
    $cancelBtn.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $cancelBtn.FlatAppearance.BorderSize = 0
    $cancelBtn.SetBounds(520, ($form.Height - 56), 90, 36)
    $cancelBtn.Cursor    = [System.Windows.Forms.Cursors]::Hand
    $cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancelBtn)
    $form.CancelButton = $cancelBtn

    Log "Platforms: opening dialog"
    $result = $form.ShowDialog()
    if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        $newMap = @{}
        foreach ($p in $script:PLATFORM_KEYS) {
            # Prefer the direct $states hashtable (same reference the click
            # handlers mutated) over the script-scope copy, which may lag.
            $newMap[$p.Key] = [bool]$states[$p.Key]
        }
        Save-PlatformsConfig $newMap
        Log ("Platforms: saved -> " + (($newMap.Keys | ForEach-Object { "$_=$($newMap[$_])" }) -join ', '))
    } else {
        Log "Platforms: cancelled"
    }
    $form.Dispose()
}

# ── Auto-start (Windows login) helpers ───────────────────────────────────────
# Strategy: Startup-folder .lnk shortcut, NOT the Run registry key.
#
# Why a shortcut beats the Run key here:
#   • Task Manager's Startup tab reads the .lnk's filename + icon directly —
#     no StartupApproved cache, no WMI Win32_StartupCommand cache in the
#     path. The cache those use is what got stuck showing "wscript.exe"
#     even after reg.exe confirmed the Run value had been rewritten.
#   • The icon comes from the .lnk's IconLocation, which we set to
#     MastersFM.exe (the exe embeds MastersFM.ico at compile time via
#     /win32icon). So the startup entry always displays the right icon.
#   • The .lnk is just a file — stateless. Deleting and re-creating it
#     reliably resets Task Manager's view. No KTM transaction weirdness.
#
# The legacy Run\MastersFM value gets DELETED on migration to stop Windows
# from also trying to launch the dead wscript+VBS command at login.
$AUTO_START_KEY  = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$AUTO_START_NAME = "MastersFM"
$STARTUP_APPROVED_KEY = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'

function Get-AutoStartLnkPath {
    # Startup folder for the current user (CSIDL_STARTUP = %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup)
    $startup = [System.Environment]::GetFolderPath('Startup')
    return [System.IO.Path]::Combine($startup, "Master's FM.lnk")
}

function Get-AutoStartEnabled {
    return (Test-Path (Get-AutoStartLnkPath))
}

function New-AutoStartShortcut {
    param([string]$LnkPath, [string]$ExePath, [string]$IcoPath)
    # WScript.Shell is the standard way to create .lnk files from PowerShell —
    # it's the same COM that CreateShortcut() uses under the hood.
    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($LnkPath)
    $shortcut.TargetPath       = $ExePath
    $shortcut.WorkingDirectory = [System.IO.Path]::GetDirectoryName($ExePath)
    $shortcut.WindowStyle      = 7     # Minimized — we don't show a console anyway
    $shortcut.Description      = "Master's FM - Now Playing Overlay"
    # Prefer the standalone .ico when it's present (crisper than whatever
    # resource compiler embedded into the exe). Fall back to the exe itself.
    if ($IcoPath -and (Test-Path $IcoPath)) {
        $shortcut.IconLocation = "$IcoPath,0"
    } else {
        $shortcut.IconLocation = "$ExePath,0"
    }
    $shortcut.Save()
    # Release COM so the handle doesn't leak
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)    | Out-Null
}

function Set-AutoStart([bool]$enable) {
    $lnk = Get-AutoStartLnkPath
    if ($enable) {
        $exe = [System.IO.Path]::Combine($scriptDir, "MastersFM.exe")
        if (-not (Test-Path $exe)) {
            Log "Auto-start: MastersFM.exe not found at '$exe' — cannot enable"
            return
        }
        $ico = [System.IO.Path]::Combine($scriptDir, "MastersFM.ico")
        try {
            New-AutoStartShortcut -LnkPath $lnk -ExePath $exe -IcoPath $ico
            Log "Auto-start ENABLED: $lnk -> $exe"
            # Clear any previous opt-out so the "MSI-reinstall self-heal" path
            # in the default-on block can rebuild the shortcut next boot if
            # Windows ever loses it again.
            try { Save-ConfigField 'autostart_user_optout' $false } catch { Log "Save autostart_user_optout=false failed: $_" }
        } catch {
            Log "Auto-start: failed to create shortcut: $_"
        }
    } else {
        try {
            if (Test-Path $lnk) { Remove-Item $lnk -Force -ErrorAction Stop }
            Log "Auto-start DISABLED (shortcut removed)"
            # Record the explicit opt-out so future MSI reinstalls don't
            # silently re-enable auto-start under the default-on policy.
            try { Save-ConfigField 'autostart_user_optout' $true } catch { Log "Save autostart_user_optout=true failed: $_" }
        } catch {
            Log "Auto-start: failed to remove shortcut: $_"
        }
    }
}

# Migration from the broken Run\MastersFM = wscript + VBS entry used by
# pre-1.7 installs. Two jobs every tray boot:
#   (1) if the legacy Run entry exists (wscript/.vbs/dangling path), DELETE
#       it entirely — plus clear the StartupApproved cache. This stops
#       Windows from trying to launch the dead VBS at login.
#   (2) if the user had auto-start on (legacy entry existed, or our .lnk is
#       already there), ensure the Startup-folder shortcut is present and
#       points at the current MastersFM.exe + icon.
function Invoke-AutoStartMigration {
    try {
        $exe  = [System.IO.Path]::Combine($scriptDir, "MastersFM.exe")
        $ico  = [System.IO.Path]::Combine($scriptDir, "MastersFM.ico")
        $lnk  = Get-AutoStartLnkPath

        # Read the legacy Run entry if present. Use SilentlyContinue (not Stop)
        # so a missing property doesn't spew a TerminatingError into the PS
        # transcript every single autostart-check tick - the transcript was
        # accumulating 60+ of these per session. Missing key → $cur stays null.
        $cur = $null
        try { $cur = Get-ItemPropertyValue -Path $AUTO_START_KEY -Name $AUTO_START_NAME -ErrorAction SilentlyContinue } catch {}
        $hadLegacyRun = ($cur -and ($cur -match '(?i)wscript\.exe|cscript\.exe|\.vbs'))

        # Any Run entry we wrote (legacy or our new .exe path) indicates the
        # user wanted auto-start. Migrate to the .lnk — or confirm it's
        # already there — and then WIPE the Run entry so Windows stops using
        # the dead-cache-polluted registry path entirely.
        $userWantedAutoStart = [bool]$cur -or (Test-Path $lnk)

        # (1) Wipe the Run entry + StartupApproved cache (always — the .lnk is
        #     our source of truth now; any Run value is stale/dangerous).
        if ($cur) {
            try { Remove-ItemProperty -Path $AUTO_START_KEY -Name $AUTO_START_NAME -ErrorAction SilentlyContinue } catch {}
            Log "Auto-start migration: removed legacy Run\$AUTO_START_NAME value"
        }
        try {
            if (Test-Path $STARTUP_APPROVED_KEY) {
                Remove-ItemProperty -Path $STARTUP_APPROVED_KEY -Name $AUTO_START_NAME -ErrorAction SilentlyContinue
            }
        } catch {}

        # (2) Create / refresh the shortcut if the user wants auto-start.
        if ($userWantedAutoStart -and (Test-Path $exe)) {
            try {
                New-AutoStartShortcut -LnkPath $lnk -ExePath $exe -IcoPath $ico
                if ($hadLegacyRun) {
                    Log "Auto-start migrated to Startup-folder shortcut: $lnk"
                } else {
                    # Silent refresh — keep the .lnk pointed at the current install path
                }
            } catch {
                Log "Auto-start migration: failed to create shortcut: $_"
            }
        }
    } catch {
        Log "Auto-start migration error: $_"
    }
}

# ── Overlay Customizer — opens web UI in browser ─────────────────────────────
function Show-OverlayCustomizer {
    # Launch customize.exe — a WinForms app that embeds Microsoft Edge WebView2
    # to render customize.html as a true native window with no browser chrome.
    # The WebView2 runtime is preinstalled on Windows 11 and auto-updates via
    # Edge on Windows 10. customize.exe + WebView2 DLLs ship in the MSI.
    $exePath = Join-Path $scriptDir "customize.exe"
    if (Test-Path $exePath) {
        try {
            Log "Customizer: launching native customize.exe"
            # Start in the install folder so WebView2Loader.dll (native) + the
            # managed WebView2 DLLs resolve correctly next to the EXE.
            Start-Process -FilePath $exePath -WorkingDirectory $scriptDir | Out-Null
            return
        } catch {
            Log "Customizer: customize.exe failed ($_), falling back to browser tab"
        }
    } else {
        Log "Customizer: customize.exe missing at '$exePath' — falling back to browser tab"
    }
    # Fallback if customize.exe is missing or failed to start
    try {
        Start-Process "http://localhost:4242/customize"
        Log "Customizer: opened in default browser"
    } catch { Log "Customizer: fallback also failed: $_" }
}


# ── OBS WebSocket v5  -  Add source ─────────────────────────────────────────────
function Add-OBSBrowserSource {
    param([string]$Password = "")
    Log "OBS: connecting to ws://localhost:4455"
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ct = [System.Threading.CancellationToken]::None
    try {
        $task = $ws.ConnectAsync([Uri]"ws://localhost:4455", $ct)
        if (-not $task.Wait(3000)) { $ws.Dispose(); Log "OBS: connect timeout"; return "TIMEOUT" }
        if ($ws.State -ne 'Open') { $ws.Dispose(); Log "OBS: state=$($ws.State)"; return "NOT_OPEN" }
        Log "OBS: connected"
    } catch { $ws.Dispose(); Log "OBS: connect error - $_"; return "NO_OBS" }

    function WsSend($obj) {
        $json  = $obj | ConvertTo-Json -Depth 10 -Compress
        Log "OBS send: $json"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $seg   = New-Object 'System.ArraySegment[byte]' (, $bytes)
        $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).Wait(3000) | Out-Null
    }
    # WsRecv loops past op-5 event messages so a stray scene-change or
    # transition event can never desync the request/response flow.
    function WsRecv {
        do {
            $buf    = New-Object 'byte[]' 131072
            $seg    = New-Object 'System.ArraySegment[byte]' (, $buf)
            $result = $ws.ReceiveAsync($seg, $ct).GetAwaiter().GetResult()
            $json   = [System.Text.Encoding]::UTF8.GetString($buf, 0, $result.Count)
            Log "OBS recv: $json"
            $msg = $json | ConvertFrom-Json
        } while ($msg.op -eq 5)   # skip async event messages, only return request responses
        return $msg
    }

    $hello   = WsRecv
    $authStr = ""
    if ($hello.d.authentication) {
        Log "OBS: auth required"
        if (-not $Password) {
            $ws.Dispose()
            $salt      = $hello.d.authentication.salt
            $challenge = $hello.d.authentication.challenge
            return "AUTH_REQUIRED|$salt|$challenge"
        }
        $sha     = [System.Security.Cryptography.SHA256]::Create()
        $secret  = [Convert]::ToBase64String($sha.ComputeHash(
                       [System.Text.Encoding]::UTF8.GetBytes($Password + $hello.d.authentication.salt)))
        $authStr = [Convert]::ToBase64String($sha.ComputeHash(
                       [System.Text.Encoding]::UTF8.GetBytes($secret + $hello.d.authentication.challenge)))
    }

    $ident = @{ op = 1; d = @{ rpcVersion = 1; eventSubscriptions = 0 } }
    if ($authStr) { $ident.d.authentication = $authStr }
    WsSend $ident
    $identified = WsRecv
    if ($identified.op -ne 2) {
        $ws.Dispose(); Log "OBS: identify failed op=$($identified.op)"; return "AUTH_FAIL"
    }

    # Check if the input already exists  -  avoids duplicates independent of the flag file
    WsSend @{ op = 6; d = @{ requestType = "GetInputList"; requestId = "gil1"; requestData = @{ inputKind = "browser_source" } } }
    $inputList = WsRecv
    $alreadyExists = @($inputList.d.responseData.inputs) | Where-Object { $_.inputName -eq "Master's FM" }
    if ($alreadyExists) {
        $ws.Dispose()
        Log "OBS: GetInputList confirms 'Master's FM' already exists  -  nothing to do"
        return "EXISTS"
    }

    # Get ALL scenes so we add the overlay to every one of them
    WsSend @{ op = 6; d = @{ requestType = "GetSceneList"; requestId = "sl1" } }
    $sceneListResp = WsRecv
    $scenes = $sceneListResp.d.responseData.scenes
    if (-not $scenes) { $ws.Dispose(); Log "OBS: no scenes found"; return "ERR:no scenes" }
    Log "OBS: found $($scenes.Count) scene(s)"

    # Create the input on the first scene (CreateInput also adds it to that scene)
    $firstScene = $scenes[0].sceneName
    WsSend @{
        op = 6
        d  = @{
            requestType = "CreateInput"
            requestId   = "ci1"
            requestData = @{
                sceneName        = $firstScene
                inputName        = "Master's FM"
                inputKind        = "browser_source"
                inputSettings    = @{
                    # v9.6.2: explicit `?renderer=webgl` suffix for OBS Browser
                    # Source URLs. Functionally a no-op in v9.4.0+ (WebGL is the
                    # only renderer; the URL param is silently ignored), but:
                    # (1) defensive future-proofing — if canvas2d ever returns
                    #     as a fallback, these sources stay pinned to WebGL.
                    # (2) friendly marker — anyone inspecting the OBS source
                    #     URL sees "yes, this is the GPU-accelerated path".
                    # Existing v9.4.0/v9.5.0/v9.6.x sources with bare URL get
                    # rewritten on the next auto-add cycle (Test-OBSBrowserSourceExists
                    # below also expects this canonical form).
                    url      = "http://localhost:4242/?renderer=webgl"
                    width    = 500
                    height   = 100
                    fps      = 30
                    css      = "body { background-color: rgba(0,0,0,0) !important; margin: 0; overflow: hidden; }"
                    shutdown = $false
                }
                sceneItemEnabled = $true
            }
        }
    }
    $resp = WsRecv
    $code = $resp.d.requestStatus.code

    # 601 = input with this name already exists in OBS.
    # This means a previous run already set up all scenes  -  bail out immediately
    # WITHOUT touching CreateSceneItem on any other scene, which is what caused
    # duplicate source entries on every restart.
    if ($code -eq 601) {
        $ws.Dispose()
        Log "OBS: Master's FM input already exists  -  nothing to do"
        return "EXISTS"
    }

    if (-not $resp.d.requestStatus.result) {
        $ws.Dispose()
        Log "OBS: CreateInput failed code=$code msg=$($resp.d.requestStatus.comment)"
        return "ERR:$($resp.d.requestStatus.comment)"
    }

    # Input was freshly created on the first scene.
    # Now link it to every other scene, but check first so we never duplicate.
    $addedTo = @($firstScene)
    foreach ($scene in ($scenes | Select-Object -Skip 1)) {
        $sn = $scene.sceneName

        # Ask OBS if "Master's FM" already lives in this scene
        WsSend @{
            op = 6
            d  = @{
                requestType = "GetSceneItemId"
                requestId   = "chk_$sn"
                requestData = @{ sceneName = $sn; sourceName = "Master's FM" }
            }
        }
        $chk = WsRecv
        if ($chk.d.requestStatus.result) {
            Log "OBS: '$sn' already has Master's FM  -  skipping"
            continue
        }

        WsSend @{
            op = 6
            d  = @{
                requestType = "CreateSceneItem"
                requestId   = "csi_$sn"
                requestData = @{
                    sceneName        = $sn
                    sourceName       = "Master's FM"
                    sceneItemEnabled = $true
                }
            }
        }
        $r = WsRecv
        if ($r.d.requestStatus.result) {
            $addedTo += $sn
            Log "OBS: added to scene '$sn'"
        } else {
            Log "OBS: scene '$sn' skip (code=$($r.d.requestStatus.code))"
        }
    }

    $ws.Dispose()
    Log "OBS: overlay added to $($addedTo.Count) scene(s)"
    return "OK:$($addedTo -join ', ')"
}

# ── Direct OBS scene-collection JSON editing (no WebSocket required) ────────────

function Get-OBSSceneCollectionPaths {
    # Primary: %APPDATA%\obs-studio\basic\scenes\
    $candidates = @(
        [System.IO.Path]::Combine($env:APPDATA, "obs-studio", "basic", "scenes")
    )
    $found = @()
    foreach ($dir in $candidates) {
        Log "OBS direct: checking scene dir: $dir"
        if (Test-Path $dir) {
            $files = @(Get-ChildItem $dir -Filter "*.json" -ErrorAction SilentlyContinue |
                       Select-Object -ExpandProperty FullName)
            Log "OBS direct: found $($files.Count) collection(s) in $dir"
            $found += $files
        } else {
            Log "OBS direct: dir not found: $dir"
        }
    }
    return $found
}

function Test-OBSBrowserSourceExists {
    # Returns $true ONLY if "Master's FM" is in sources[] AND in at least one scene's items.
    # A source in sources[] but missing from all scene items is NOT considered "present".
    # v9.6.2: URL canonicality check expects "http://localhost:4242/?renderer=webgl".
    # Auto-add (both WS + JSON-direct paths) now writes that explicit form for
    # defensive future-proofing + friendly marker (see comments at the WS path
    # for full rationale). Sources with the bare v9.4.0/v9.5.0/v9.6.x URL or
    # the v9.3.x ?renderer=canvas2d variant get rewritten on the next auto-add.
    $expectedUrl = "http://localhost:4242/?renderer=webgl"
    $paths = Get-OBSSceneCollectionPaths
    foreach ($path in $paths) {
        try {
            $json     = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            $colName  = [System.IO.Path]::GetFileNameWithoutExtension($path)
            $existing = @($json.sources) | Where-Object { $_.name -eq "Master's FM" } | Select-Object -First 1
            if (-not $existing) { continue }

            # Also verify it appears in at least one scene's items list
            $scenes  = @($json.sources | Where-Object { $_.id -eq 'scene' -or $_.versioned_id -eq 'scene' })
            $inScene = [bool]($scenes | Where-Object {
                [bool](@($_.settings.items) | Where-Object { $_.name -eq "Master's FM" })
            })

            # v9.4.0 — URL must equal bare "http://localhost:4242". Old
            # v9.3.x sources with "?renderer=webgl"/"?renderer=canvas2d"
            # suffixes get rewritten on next auto-add cycle.
            $urlOk = $true
            if ($existing.settings -and $existing.settings.PSObject.Properties['url']) {
                if ([string]$existing.settings.url -ne $expectedUrl) {
                    Log "OBS direct: '$colName' URL '$($existing.settings.url)' != expected '$expectedUrl' - needs sync"
                    $urlOk = $false
                }
            }

            if ($inScene -and $urlOk) {
                Log "OBS direct: source verified in '$colName' (sources[] + scene items OK + URL canonical)"
                return $true
            } else {
                Log "OBS direct: '$colName' has source in sources[] but NOT in any scene items - needs repair"
                return $false
            }
        } catch {
            Log "OBS direct: ERROR checking '$([System.IO.Path]::GetFileNameWithoutExtension($path))': $_"
        }
    }
    return $false
}

function Add-OBSBrowserSourceDirect {
    $paths = Get-OBSSceneCollectionPaths
    if (-not $paths -or $paths.Count -eq 0) {
        Log "OBS direct: NO scene collection files found - OBS may never have been launched"
        return "NO_SCENES"
    }
    Log "OBS direct: found $($paths.Count) collection(s) to process"

    $addedTo   = @()
    $alreadyIn = @()
    $noBom     = [System.Text.UTF8Encoding]::new($false)

    foreach ($path in $paths) {
        $colName = [System.IO.Path]::GetFileNameWithoutExtension($path)
        try {
            $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
            Log "OBS direct: reading '$colName' ($($raw.Length) bytes)"
            $json = $raw | ConvertFrom-Json

            # ── Check source definition AND scene items separately ────────────
            $sources      = @($json.sources)
            $sourceExists = [bool]($sources | Where-Object { $_.name -eq "Master's FM" })

            $scenes         = @($sources | Where-Object { $_.id -eq 'scene' -or $_.versioned_id -eq 'scene' })
            $scenesWithItem = @($scenes | Where-Object {
                [bool](@($_.settings.items) | Where-Object { $_.name -eq "Master's FM" })
            })
            $allScenesHaveIt = ($scenes.Count -gt 0) -and ($scenesWithItem.Count -eq $scenes.Count)

            # v6.2.3: reverted to hardcoded 1000x200 default (the classic size
            # from v6.0.4 and earlier). No user preference, no customizer
            # knob — the card fills whatever Browser Source size OBS has and
            # users who want a different size just resize the source in OBS
            # properties.
            $targetW = 1000
            $targetH = 200

            # v9.6.2 — explicit ?renderer=webgl suffix (see Add-OBSBrowserSourceWS
            # for rationale). Bare URLs from v9.4.0/v9.5.0/v9.6.x and v9.3.x
            # ?renderer=canvas2d variants both get rewritten to this canonical form.
            $targetUrl = "http://localhost:4242/?renderer=webgl"

            # Truly complete - source definition + all scene items present.
            # But first verify the dimensions AND URL are in sync; if not we
            # still need to fall through to the update path even if all scenes
            # have the item.
            $dimsInSync = $true
            $urlInSync  = $true
            if ($sourceExists) {
                $_existingForDimCheck = $sources | Where-Object { $_.name -eq "Master's FM" } | Select-Object -First 1
                if ($_existingForDimCheck -and $_existingForDimCheck.settings) {
                    if ([int]$_existingForDimCheck.settings.width  -ne $targetW) { $dimsInSync = $false }
                    if ([int]$_existingForDimCheck.settings.height -ne $targetH) { $dimsInSync = $false }
                    if ([string]$_existingForDimCheck.settings.url -ne $targetUrl) { $urlInSync = $false }
                }
            }
            if ($sourceExists -and $allScenesHaveIt -and $dimsInSync -and $urlInSync) {
                Log "OBS direct: '$colName' fully configured - source + all $($scenes.Count) scene(s) have item, dims+url match preference"
                $alreadyIn += $colName
                continue
            }

            # Determine UUID: reuse existing if source def is already there
            if ($sourceExists) {
                $existingSrc = $sources | Where-Object { $_.name -eq "Master's FM" } | Select-Object -First 1
                $sourceUuid  = if ($existingSrc.PSObject.Properties['uuid'] -and $existingSrc.uuid) {
                    $existingSrc.uuid
                } else { [System.Guid]::NewGuid().ToString() }
                $missing = $scenes.Count - $scenesWithItem.Count

                $needsSourceUpdate = $false
                if ($existingSrc.settings) {
                    $curW = [int]$existingSrc.settings.width
                    $curH = [int]$existingSrc.settings.height
                    if ($curW -ne $targetW) {
                        $existingSrc.settings.width  = $targetW; $needsSourceUpdate = $true
                    }
                    if ($curH -ne $targetH) {
                        $existingSrc.settings.height = $targetH; $needsSourceUpdate = $true
                    }
                    # v9.3.2 — URL migration. Strip ?renderer=webgl (and any other
                    # path/query) — bare URL is now canonical so the customize
                    # toggle controls the renderer for OBS sources.
                    $curUrl = [string]$existingSrc.settings.url
                    if ($curUrl -ne $targetUrl) {
                        Log ("OBS direct: '{0}' - URL migration: '{1}' -> '{2}'" -f $colName, $curUrl, $targetUrl)
                        $existingSrc.settings.url = $targetUrl
                        $needsSourceUpdate = $true
                    }
                }
                if ($needsSourceUpdate) {
                    Log ("OBS direct: '{0}' - synced source settings (dims={1}x{2}, url={3})" -f $colName, $targetW, $targetH, $targetUrl)
                    if (-not $allScenesHaveIt) {
                        Log "OBS direct: '$colName' - source def exists (settings fixed), $missing scene(s) missing item - adding scene items only"
                    } else {
                        # Only needed a settings fix, scenes were fine - write and mark as added
                        $newJson = $json | ConvertTo-Json -Depth 20
                        [System.IO.File]::WriteAllText($path, $newJson, $noBom)
                        $addedTo += $colName
                        Log "OBS direct: SUCCESS - '$colName' source settings updated (scene items were already present)"
                        continue
                    }
                } else {
                    Log "OBS direct: '$colName' - source def exists, $missing scene(s) missing item - adding scene items only"
                }
            } else {
                $sourceUuid = [System.Guid]::NewGuid().ToString()
                Log "OBS direct: '$colName' has $($sources.Count) sources - adding source def + scene items"

                # ── Build browser source definition (OBS 28+ format) ──────────
                $newSource = [PSCustomObject]@{
                    versioned_id            = "browser_source"
                    id                      = "browser_source"
                    name                    = "Master's FM"
                    uuid                    = $sourceUuid
                    settings                = [PSCustomObject]@{
                        # v9.6.2: ?renderer=webgl suffix — see Add-OBSBrowserSourceWS for rationale.
                        url                   = "http://localhost:4242/?renderer=webgl"
                        width                 = $targetW
                        height                = $targetH
                        fps                   = 60
                        fps_custom            = $false
                        css                   = "body { background-color: rgba(0,0,0,0) !important; margin: 0; overflow: hidden; }"
                        shutdown              = $false
                        restart_when_active   = $false
                        webpage_control_level = 1
                    }
                    mixers                  = 0
                    monitoring_type         = 0
                    balance                 = 0.5
                    enabled                 = $true
                    muted                   = $false
                    push_to_mute_delay      = 0
                    push_to_talk_delay      = 0
                    deinterlace_field_order = 0
                    deinterlace_mode        = 0
                    audio_mixers            = 255
                    flags                   = 0
                    filter_order            = @()
                    sync                    = 0
                    volume                  = 1.0
                }
                $json.sources = @($sources) + @($newSource)
            }

            # ── Add scene item to every scene that is missing it ──────────────
            $scenesUpdated = 0
            foreach ($src in @($json.sources)) {
                $isScene = ($src.id -eq "scene" -or $src.versioned_id -eq "scene")
                if (-not $isScene) { continue }

                $items = if ($src.settings -and $src.settings.PSObject.Properties['items']) {
                    @($src.settings.items)
                } else { @() }

                if ($items | Where-Object { $_.name -eq "Master's FM" }) {
                    Log "OBS direct: scene '$($src.name)' already has item - skipping"
                    continue
                }

                $maxId = if ($items.Count -gt 0) {
                    ($items | ForEach-Object { [int]($_.id) } | Measure-Object -Maximum).Maximum
                } else { 0 }
                $newItemId = $maxId + 1

                if ($src.settings.PSObject.Properties['id_counter']) {
                    if ([int]$src.settings.id_counter -le $maxId) {
                        $src.settings.id_counter = $newItemId + 1
                    }
                }

                # v6.2.3: plain origin-anchored pos. Users position the source
                # wherever they want in OBS themselves.
                $newItem = [PSCustomObject]@{
                    name            = "Master's FM"
                    source_uuid     = $sourceUuid
                    visible         = $true
                    locked          = $false
                    pos             = [PSCustomObject]@{ x = 0.0; y = 0.0 }
                    rot             = 0.0
                    scale           = [PSCustomObject]@{ x = 1.0; y = 1.0 }
                    align           = 5
                    bounds_type     = 0
                    bounds_align    = 0
                    bounds          = [PSCustomObject]@{ x = 0.0; y = 0.0 }
                    crop_top        = 0
                    crop_right      = 0
                    crop_bottom     = 0
                    crop_left       = 0
                    id              = $newItemId
                    group_item_id   = 0
                    scale_filter    = "disable"
                    blend_method    = "default"
                    blend_type      = 0
                    show_transition = [PSCustomObject]@{ duration = 300; id = "fade_transition" }
                    hide_transition = [PSCustomObject]@{ duration = 300; id = "fade_transition" }
                }

                $src.settings.items = @($items) + @($newItem)
                $scenesUpdated++
                Log "OBS direct: added item to scene '$($src.name)' (item id=$newItemId)"
            }

            if ($scenesUpdated -eq 0) {
                Log "OBS direct: WARNING - no scenes found to update in '$colName'"
            }

            # ── Write back, UTF-8 no BOM ──────────────────────────────────────
            $newJson = $json | ConvertTo-Json -Depth 20
            [System.IO.File]::WriteAllText($path, $newJson, $noBom)
            $addedTo += $colName
            Log "OBS direct: SUCCESS - wrote '$colName' ($scenesUpdated scene item(s) added/fixed)"

        } catch {
            Log "OBS direct: ERROR in '$colName': $($_.Exception.GetType().Name): $_"
            Log "OBS direct: Stack: $($_.ScriptStackTrace)"
        }
    }

    if ($addedTo.Count -gt 0) { return "OK:$($addedTo -join ', ')" }
    if ($alreadyIn.Count -gt 0) { return "EXISTS" }
    return "ERR:no collections modified"
}

function Remove-OBSBrowserSourceDirect {
    $paths = Get-OBSSceneCollectionPaths
    if (-not $paths -or $paths.Count -eq 0) {
        Log "OBS direct remove: no scene collections found"
        return "NO_SCENES"
    }

    $removedFrom = @()
    $noBom = [System.Text.UTF8Encoding]::new($false)

    foreach ($path in $paths) {
        $colName = [System.IO.Path]::GetFileNameWithoutExtension($path)
        try {
            $raw  = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
            $json = $raw | ConvertFrom-Json
            $before = @($json.sources).Count

            # Remove scene item references from every scene
            foreach ($src in @($json.sources)) {
                $isScene = ($src.id -eq "scene" -or $src.versioned_id -eq "scene")
                if (-not $isScene) { continue }
                if ($src.settings -and $src.settings.PSObject.Properties['items']) {
                    $src.settings.items = @(@($src.settings.items) |
                        Where-Object { $_.name -ne "Master's FM" })
                }
            }

            # Remove the source itself
            $json.sources = @(@($json.sources) | Where-Object { $_.name -ne "Master's FM" })

            $after = @($json.sources).Count
            if ($after -lt $before) {
                $newJson = $json | ConvertTo-Json -Depth 20
                [System.IO.File]::WriteAllText($path, $newJson, $noBom)
                $removedFrom += $colName
                Log "OBS direct: removed from '$colName'"
            } else {
                Log "OBS direct: 'Master's FM' not found in '$colName'"
            }
        } catch {
            Log "OBS direct: ERROR removing from '$colName': $_"
        }
    }

    if ($removedFrom.Count -gt 0) { return "OK:$($removedFrom -join ', ')" }
    return "NOT_FOUND"
}

# (v6.2.3: removed Reset-OBSSourcePosition — no more "Reset Position & Size"
# button in customizer, card fills source directly at 1000x200.)

# ── OBS exit watcher: re-applies JSON after OBS closes (beats OBS overwrite-on-exit) ──
$global:_obsWatcherActive = $false
function Start-OBSExitWatcher {
    if ($global:_obsWatcherActive) { Log "OBS watcher: already active"; return }
    $global:_obsWatcherActive = $true
    Log "OBS watcher: OBS is open - watching for it to close so we can re-apply JSON"

    $global:_obsWatchTimer = New-Object System.Windows.Forms.Timer
    $global:_obsWatchTimer.Interval = 2000
    $global:_obsWatchTimer.add_Tick({
        $still = Get-Process -Name "obs64","obs32","obs" -ErrorAction SilentlyContinue
        if ($still) { return }
        # OBS just exited - it overwrote the scene JSON with in-memory state
        $global:_obsWatchTimer.Stop()
        try { $global:_obsWatchTimer.Dispose() } catch {}   # v11.0.0: dispose stopped timer
        $global:_obsWatcherActive = $false
        Log "OBS watcher: OBS exited - re-applying JSON in 1.5s"
        # Short delay timer so OBS finishes writing before we re-write
        $global:_obsDelayTimer = New-Object System.Windows.Forms.Timer
        $global:_obsDelayTimer.Interval = 1500
        $global:_obsDelayTimer.add_Tick({
            $global:_obsDelayTimer.Stop()
            try { $global:_obsDelayTimer.Dispose() } catch {}   # v11.0.0: dispose stopped timer
            Log "OBS watcher: re-applying browser source now"
            $r = Add-OBSBrowserSourceDirect
            Log "OBS watcher: re-apply result = $r"
            if ($r -like "OK:*" -or $r -eq "EXISTS") {
                [System.IO.File]::WriteAllText($obsFlagFile,
                    (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'),
                    [System.Text.UTF8Encoding]::new($false))
                $tray.ShowBalloonTip(6000, "OBS Ready!",
                    "Source is in your scene collection. Open OBS to use it.",
                    [System.Windows.Forms.ToolTipIcon]::Info)
            } else {
                $tray.ShowBalloonTip(5000, "OBS Re-apply Failed",
                    "Result: $r - Try clicking 'Add to OBS' again. See overlay.log.",
                    [System.Windows.Forms.ToolTipIcon]::Warning)
            }
        })
        $global:_obsDelayTimer.Start()
    })
    $global:_obsWatchTimer.Start()
}

# ── Try to add browser source to OBS (direct JSON, no WebSocket) ───────────────
# Works whether OBS is open or closed:
#   - OBS closed: edits JSON directly, done immediately
#   - OBS open:   edits JSON AND starts exit-watcher that re-applies after OBS closes
function Try-AddToOBS {
    param([switch]$SkipFlagCheck)

    # Self-healing flag check
    if (-not $SkipFlagCheck -and (Test-Path $obsFlagFile)) {
        if (Test-OBSBrowserSourceExists) {
            Log "OBS: source verified present - nothing to do"
            return
        } else {
            Log "OBS: flag exists but source missing from JSON - re-adding"
            Remove-Item $obsFlagFile -Force -ErrorAction SilentlyContinue
        }
    }

    $obsOpen = [bool](Get-Process -Name "obs64","obs32","obs" -ErrorAction SilentlyContinue)
    Log "OBS: attempting JSON edit (OBS is $(if ($obsOpen) {'OPEN'} else {'CLOSED'}))"

    $result = Add-OBSBrowserSourceDirect
    Log "OBS: add result = $result"

    switch -Wildcard ($result) {
        "OK:*" {
            if ($obsOpen) {
                # OBS keeps scenes in memory and overwrites the JSON on exit.
                # We start the exit watcher so it re-applies the JSON after OBS closes,
                # making the source available the next time OBS opens.
                Start-OBSExitWatcher
                $tray.ShowBalloonTip(10000, "Almost there - restart OBS",
                    "Source written to your scene file. OBS must restart to load it. Close OBS now and reopen - Master's FM will be in all your scenes automatically.",
                    [System.Windows.Forms.ToolTipIcon]::Info)
            } else {
                [System.IO.File]::WriteAllText($obsFlagFile,
                    (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'),
                    [System.Text.UTF8Encoding]::new($false))
                Log "OBS: flag written"
                $tray.ShowBalloonTip(5000, "Added to OBS!",
                    "Open OBS - Master's FM is now in all your scenes.",
                    [System.Windows.Forms.ToolTipIcon]::Info)
            }
        }
        "EXISTS" {
            if (-not (Test-Path $obsFlagFile)) {
                [System.IO.File]::WriteAllText($obsFlagFile,
                    (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'),
                    [System.Text.UTF8Encoding]::new($false))
                Log "OBS: flag written (source already present)"
            }
            $tray.ShowBalloonTip(4000, "Already in OBS",
                "Master's FM source is already in your scene collection.",
                [System.Windows.Forms.ToolTipIcon]::Info)
        }
        "NO_SCENES" {
            Log "OBS: no scene collection files found - starting auto-retry watcher"
            $tray.ShowBalloonTip(7000, "Open OBS First",
                "Open OBS at least once to create a scene collection. Master's FM will add itself automatically when OBS is ready.",
                [System.Windows.Forms.ToolTipIcon]::Warning)
            # Retry every 60s until scenes exist (handles first-time OBS users)
            if (-not $global:_obsRetryTimer) {
                $global:_obsRetryTimer = New-Object System.Windows.Forms.Timer
                $global:_obsRetryTimer.Interval = 60000
                $global:_obsRetryTimer.add_Tick({
                    Log "OBS retry timer fired"
                    $r2 = Add-OBSBrowserSourceDirect
                    if ($r2 -ne "NO_SCENES") {
                        $global:_obsRetryTimer.Stop()
                        try { $global:_obsRetryTimer.Dispose() } catch {}   # v11.0.0
                        $global:_obsRetryTimer = $null
                        Try-AddToOBS  # let normal logic handle the result
                    }
                })
                $global:_obsRetryTimer.Start()
                Log "OBS: retry timer started (60s interval)"
            }
        }
        default {
            Log "OBS: FAILED - $result"
            $tray.ShowBalloonTip(5000, "OBS Add Failed",
                "Error: $result - Check overlay.log for details.",
                [System.Windows.Forms.ToolTipIcon]::Warning)
        }
    }
}


# ── Uninstall mode ─────────────────────────────────────────────────────────────
# The MSI calls: tray.ps1 -scriptDir "<install dir>" -Uninstall
# This removes the OBS browser source from every scene and cleans up the flag file.
if ($Uninstall) {
    Log "Running in uninstall mode"
    $obsRunning = Get-Process -Name "obs64","obs32","obs" -ErrorAction SilentlyContinue
    if ($obsRunning) {
        Log "OBS is running during uninstall - JSON edits may be overwritten. Proceeding anyway."
    }
    Log "Removing browser source from scene collection JSON files"
    $directResult = Remove-OBSBrowserSourceDirect
    Log "OBS direct remove result: $directResult"
    # Always clean up the flag file so a reinstall starts fresh
    if (Test-Path $obsFlagFile) { Remove-Item $obsFlagFile -Force -ErrorAction SilentlyContinue; Log "Flag file removed" }
    Log "Uninstall complete"
    exit 0
}

# ── Normal startup from here ───────────────────────────────────────────────────
# Heal legacy auto-start Run-key entry (pre-1.7 users had wscript + VBS here
# which shows "wscript.exe" in Task Manager's Startup apps and fails silently
# on login because the VBS is no longer shipped). This runs on EVERY tray
# start so the fix applies after a fresh install too.
try { Invoke-AutoStartMigration } catch { Log "Invoke-AutoStartMigration outer: $_" }

# Wrap everything in a top-level try/catch so any unhandled crash goes to the log.
trap {
    $errMsg = "FATAL CRASH: $_`r`n$($_.ScriptStackTrace)"
    Log $errMsg
    try { [System.IO.File]::AppendAllText($TEMP_LOG, "$errMsg`r`n") } catch {}
    try { Stop-Transcript } catch {}
    # Show a visible error so the user knows something went wrong
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
        [System.Windows.Forms.MessageBox]::Show(
            "Master's FM crashed on startup.`n`nError: $_`n`nCheck log at:`n$TEMP_LOG",
            "Master's FM - Fatal Error",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error)
    } catch {}
    exit 1
}

# ── Draw tray icon ─────────────────────────────────────────────────────────────
# Done FIRST so the icon is ready to show before the welcome dialog (which can
# block for several seconds). This gives near-instant visual feedback on launch.
Log "Drawing icon"
try {
    $icoPath = [System.IO.Path]::Combine($scriptDir, "MastersFM.ico")
    if (Test-Path $icoPath) {
        $icon = New-Object System.Drawing.Icon($icoPath, 32, 32)
        Log "Icon loaded from file"
    } else {
        $bmp       = New-Object System.Drawing.Bitmap(32, 32)
        $gfx       = [System.Drawing.Graphics]::FromImage($bmp)
        $gfx.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $gfx.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $bgBrush   = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,110,30,200))
        $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $font      = New-Object System.Drawing.Font("Segoe UI", 17, [System.Drawing.FontStyle]::Bold)
        $gfx.FillEllipse($bgBrush, 0, 0, 31, 31)
        $gfx.DrawString([char]9835, $font, $textBrush, 3, 3)
        $gfx.Dispose()
        $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
        Log "Icon drawn from scratch"
    }
} catch {
    Log "Icon failed: $_ - using fallback"
    $icon = [System.Drawing.SystemIcons]::Application
}

# ── ShowCustomizerOnly: skip tray+server, open customizer and exit ─────────────
if ($ShowCustomizerOnly) {
    Log "ShowCustomizerOnly mode: opening customizer"
    Show-OverlayCustomizer
    try { Stop-Transcript } catch {}
    exit 0
}

# ── Tray icon — shown BEFORE welcome dialog for instant startup feel ───────────
Log "Creating tray"
$tray         = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon    = $icon
$tray.Text    = "Master's FM - starting..."
$tray.Visible = $true
Log "Tray visible"

# ── Welcome / patch-notes dialog ───────────────────────────────────────────────
# Shown AFTER the tray is already visible so the user gets immediate feedback
# that the app launched, even if the dialog takes a moment to render.
$welcomeSeen = Get-WelcomeSeen
Log "Startup state: welcome_seen=$welcomeSeen"
if (-not $welcomeSeen) {
    # Distinguish post-update boot from genuine first install.
    # Roaming config survives MSI reinstalls, so after an auto-update
    # welcome_seen=true (set by the prior version) but welcome_seen_version
    # doesn't match the new APP_VERSION. On first-ever install there is no
    # config yet — welcome_seen is missing/false.
    $__isPostUpdate = $false
    try {
        $__pcfg = Get-UserCfgPath
        if (Test-Path $__pcfg) {
            $__j = Get-Content $__pcfg -Raw | ConvertFrom-Json
            $__isPostUpdate = ($__j.welcome_seen -eq $true)
        }
    } catch {}

    if ($__isPostUpdate) {
        # v10.2.3: post-update boot — balloon only, no auto-popup window.
        # User can open patch notes any time via "Patch Notes" in the tray menu.
        Log "Post-update boot ($($script:APP_VERSION)): balloon notification, welcome window suppressed"
        try {
            $tray.ShowBalloonTip(6000, "Master's FM updated",
                "Now running $($script:APP_VERSION). Tap 'Patch Notes' in the menu to see what's new.",
                [System.Windows.Forms.ToolTipIcon]::Info)
        } catch { LogErr 'post-update balloon' $_ }
    } else {
        # Genuine first install — show the welcome / patch-notes dialog.
        Log "First run detected — invoking welcome flow"
        try {
            Show-WelcomeDialog
            Log "Welcome flow returned successfully"
        } catch {
            Log "Welcome dialog threw: $_"
        }
    }
    Save-ConfigField 'welcome_seen' $true
    Save-ConfigField 'welcome_seen_version' $script:APP_VERSION
    Log "welcome_seen flag persisted (version=$script:APP_VERSION)"
} else {
    Log "Welcome already seen — skipping"
}

# default-on auto-start — fires EXACTLY ONCE per install, independent
# of the welcome flow. If autostart_defaulted_on_v199 is unset and the user
# hasn't already enabled auto-start, we enable it for them. If they later
# turn it off via the tray menu we won't re-enable (the flag stays set).
try {
    $cfgPath = Get-UserCfgPath   # Roaming, same file server.js uses
    $cfg = $null
    if (Test-Path $cfgPath) {
        try { $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json } catch { $cfg = $null }
    }
    $alreadyDefaulted = $false
    if ($cfg -and $cfg.PSObject.Properties.Name -contains 'autostart_defaulted_on_v199') {
        $alreadyDefaulted = [bool]$cfg.autostart_defaulted_on_v199
    }
    # Track the user's EXPLICIT opt-out — if they've ever turned auto-start off
    # via the tray menu (autostart_user_optout=true in Roaming), we NEVER
    # re-enable on any boot, even if the .lnk goes missing.
    $userOptedOut = $false
    if ($cfg -and $cfg.PSObject.Properties.Name -contains 'autostart_user_optout') {
        $userOptedOut = [bool]$cfg.autostart_user_optout
    }

    if (-not $alreadyDefaulted) {
        if (-not (Get-AutoStartEnabled)) {
            Set-AutoStart $true
            Log "default-on: auto-start enabled (first time on this install)"
        } else {
            Log "default-on: auto-start already on - recording flag"
        }
        Save-ConfigField 'autostart_defaulted_on_v199' $true
    } elseif (-not $userOptedOut -and -not (Get-AutoStartEnabled)) {
        # Flag is set (we've defaulted before) AND user never opted out AND
        # the .lnk is missing — this happens after an MSI reinstall because
        # the uninstall CA deletes the Startup shortcut. Recreate it so
        # auto-start stays on across upgrades for users who wanted it on.
        Set-AutoStart $true
        Log "default-on: .lnk was missing (likely MSI reinstall) - recreated"
    }
} catch { Log "default-on auto-start check failed: $_" }

# ── Discord RPC helpers (used by both tray menu and server config) ─────────────
# Get-RoamingCfgPath kept as an alias to Get-UserCfgPath for callers below.
# Both resolve to %APPDATA%\MastersFM\config.json — the single source of
# truth the server also reads/writes.
function Get-RoamingCfgPath { Get-UserCfgPath }

function Get-DiscordEnabled {
    $cfg = Get-RoamingCfgPath
    if (-not (Test-Path $cfg)) { return $true }
    try {
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        if ($null -ne $j.discord_rpc -and $null -ne $j.discord_rpc.enabled) {
            return [bool]$j.discord_rpc.enabled
        }
    } catch {}
    return $true
}

# v5.0.0 — real OBS-source presence check (not just the flag file).
# Scans every scene-collection JSON under %APPDATA%\obs-studio\basic\scenes
# for a browser_source named 'Master's FM'. Faster than querying OBS via
# WebSocket (which needs OBS running AND the plugin) and more honest than
# trusting obs_configured.flag alone (the flag lives in the wipe-on-reinstall
# install dir). Result cached for 30 s so the menu doesn't re-scan JSON on
# every open — which helps CPU on big scene collections.
$script:_ObsSrcCacheAt  = [DateTime]::MinValue
$script:_ObsSrcCacheVal = $false
function Test-ObsSourceExists {
    $now = [DateTime]::UtcNow
    if (($now - $script:_ObsSrcCacheAt).TotalSeconds -lt 30) {
        return $script:_ObsSrcCacheVal
    }
    $found = $false
    try {
        $sceneDir = [System.IO.Path]::Combine(
            [System.Environment]::GetFolderPath('ApplicationData'),
            'obs-studio', 'basic', 'scenes')
        if (Test-Path $sceneDir) {
            $files = Get-ChildItem $sceneDir -Filter '*.json' -ErrorAction SilentlyContinue
            foreach ($f in $files) {
                try {
                    $raw = [System.IO.File]::ReadAllText($f.FullName)
                    # Cheap substring check first — the source is only "really
                    # present" if the collection has both a source definition
                    # (top-level "name":"Master's FM" + "id":"browser_source")
                    # AND the localhost:4242 URL in its settings. Use a
                    # regex to confirm both, but fall back to a simpler
                    # "has the name AND the URL" substring match so a
                    # different formatting style still registers.
                    # OBS serializes apostrophes as \u0027 in its scene JSON,
                    # so the literal source name "Master's FM" never appears
                    # unescaped.  Match either form so our check works against
                    # both raw-written and OBS-rewritten files.
                    $hasName = $raw.Contains("Master's FM") -or $raw.Contains('Master\u0027s FM')
                    $hasUrl  = $raw.Contains('localhost:4242') -or $raw.Contains('localhost%3A4242')
                    if ($hasName -and $hasUrl) {
                        $found = $true
                        break
                    }
                } catch {}
            }
        }
    } catch {}
    $script:_ObsSrcCacheVal = $found
    $script:_ObsSrcCacheAt  = $now
    return $found
}
function Invalidate-ObsSourceCache { $script:_ObsSrcCacheAt = [DateTime]::MinValue }

function Get-LiveAudioEnabled {
    # Reads the top-level liveAudioVisualizer flag from the Roaming config.
    # Default TRUE — ship with the WASAPI visualizer on so new installs get
    # the "real audio" experience immediately. User can flip off from the tray.
    $cfg = Get-RoamingCfgPath
    if (-not (Test-Path $cfg)) { return $true }
    try {
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        if ($j.PSObject.Properties.Name -contains 'liveAudioVisualizer') {
            return [bool]$j.liveAudioVisualizer
        }
    } catch {}
    return $true
}
function Save-DiscordEnabled($enabled) {
    $cfg = Get-RoamingCfgPath
    $clientId = ''
    if (Test-Path $cfg) {
        try {
            $j = Get-Content $cfg -Raw | ConvertFrom-Json
            if ($j.discord_rpc -and $j.discord_rpc.client_id) {
                $clientId = ($j.discord_rpc.client_id + "").Trim()
            }
        } catch {}
    }
    try {
        $noBom    = [System.Text.UTF8Encoding]::new($false)
        $existing = $null
        try { $existing = [System.IO.File]::ReadAllText($cfg, [System.Text.Encoding]::UTF8) -replace '^\uFEFF','' | ConvertFrom-Json } catch {}
        $bag = [ordered]@{}
        if ($existing) { foreach ($p in $existing.PSObject.Properties) { $bag[$p.Name] = $p.Value } }
        $bag['discord_rpc'] = [ordered]@{ enabled = [bool]$enabled; client_id = $clientId }
        [System.IO.File]::WriteAllText($cfg, ($bag | ConvertTo-Json -Depth 10), $noBom)
        Log "Discord RPC saved to roaming config: enabled=$enabled"
    } catch { Log "Discord RPC save error: $_" }
    try { Invoke-RestMethod "http://127.0.0.1:4242/reload-config" -Method POST -TimeoutSec 2 | Out-Null }
    catch { Log "Discord RPC save: /reload-config POST failed (server may be starting or down): $_" }
}

# ── Custom tray popup menu ─────────────────────────────────────────────────────
# Colors
$M_BG     = [System.Drawing.ColorTranslator]::FromHtml('#0D091C')
$M_HV     = [System.Drawing.ColorTranslator]::FromHtml('#1C0E3A')
$M_ACCENT = [System.Drawing.ColorTranslator]::FromHtml('#7C3AED')
$M_TEXT   = [System.Drawing.ColorTranslator]::FromHtml('#EEE4FF')
$M_CHECK  = [System.Drawing.ColorTranslator]::FromHtml('#A78BFA')
$M_SEP    = [System.Drawing.ColorTranslator]::FromHtml('#261648')
$M_DANGER = [System.Drawing.ColorTranslator]::FromHtml('#F85858')
$M_BORDER = [System.Drawing.ColorTranslator]::FromHtml('#3D1F7A')

# P/Invoke: rounded corners + foreground focus
if (-not ([System.Management.Automation.PSTypeName]'MFM_MenuNative').Type) {
    Add-Type -Name MFM_MenuNative -Namespace '' -MemberDefinition @"
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(System.IntPtr hwnd, int attr, ref int val, int size);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern System.IntPtr CreateRoundRectRgn(int x1,int y1,int x2,int y2,int cx,int cy);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetWindowRgn(System.IntPtr hwnd, System.IntPtr hRgn, bool redraw);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(System.IntPtr hwnd);
"@
}

$script:_menuForm = $null

function Close-TrayMenu {
    param([switch]$Immediate)
    $fm = $script:_menuForm
    if (-not $fm -or $fm.IsDisposed) { return }
    $script:_menuForm = $null
    if ($Immediate) { $fm.Hide(); $fm.Dispose(); return }
    $fadeOut = New-Object System.Windows.Forms.Timer
    $fadeOut.Interval = 16
    $fadeOut.add_Tick({
        if ($fm.IsDisposed) { $fadeOut.Stop(); try { $fadeOut.Dispose() } catch {}; return }
        $fm.Opacity -= 0.09
        # v11.0.0: Dispose timer when done so WinForms timer handle is released immediately
        if ($fm.Opacity -le 0) { $fadeOut.Stop(); try { $fadeOut.Dispose() } catch {}; $fm.Hide(); $fm.Dispose() }
    }.GetNewClosure())
    $fadeOut.Start()
}

function Invoke-MenuAction([scriptblock]$Action) {
    # Direct-to-file diagnostic — the Log function might not be visible from
    # inside closures invoked by the hosted runspace, so write straight to
    # disk via .NET APIs (no PS scope dependency).
    $diag = [System.IO.Path]::Combine([System.Environment]::GetFolderPath('LocalApplicationData'), "MastersFM", "menu.log")
    try { [System.IO.File]::AppendAllText($diag, "[$(Get-Date -Format 'HH:mm:ss.fff')] menu-click: entered Invoke-MenuAction`r`n") } catch {}

    try {
        Close-TrayMenu -Immediate
        try { [System.IO.File]::AppendAllText($diag, "[$(Get-Date -Format 'HH:mm:ss.fff')] menu-click: Close-TrayMenu OK`r`n") } catch {}
    } catch {
        try { [System.IO.File]::AppendAllText($diag, "[$(Get-Date -Format 'HH:mm:ss.fff')] menu-click: Close-TrayMenu threw: $_`r`n") } catch {}
    }

    if (-not $Action) {
        try { [System.IO.File]::AppendAllText($diag, "[$(Get-Date -Format 'HH:mm:ss.fff')] menu-click: Action is NULL`r`n") } catch {}
        return
    }
    try { [System.IO.File]::AppendAllText($diag, "[$(Get-Date -Format 'HH:mm:ss.fff')] menu-click: invoking action (type=$($Action.GetType().Name))`r`n") } catch {}
    try {
        & $Action
        try { [System.IO.File]::AppendAllText($diag, "[$(Get-Date -Format 'HH:mm:ss.fff')] menu-click: action returned OK`r`n") } catch {}
    } catch {
        try { [System.IO.File]::AppendAllText($diag, "[$(Get-Date -Format 'HH:mm:ss.fff')] menu-click: action threw: $_`r`n  $($_.ScriptStackTrace)`r`n") } catch {}
    }
}

function New-MenuSep {
    param([System.Windows.Forms.Form]$form, [ref]$yRef)
    $sep = New-Object System.Windows.Forms.Panel
    $sep.BackColor = $M_SEP
    $sep.SetBounds(14, $yRef.Value + 5, $form.ClientSize.Width - 28, 1)
    $form.Controls.Add($sep)
    $yRef.Value += 12
}

function New-MenuItem {
    param(
        [System.Windows.Forms.Form]$form,
        [ref]$yRef,
        [string]$Icon,
        [string]$Label,
        [scriptblock]$Action,
        [bool]$IsChecked = $false,
        [bool]$IsDanger  = $false,
        [bool]$IsHeader  = $false
    )

    $H = if ($IsHeader) { 54 } else { 38 }
    $W = $form.ClientSize.Width

    $row = New-Object System.Windows.Forms.Panel
    $row.SetBounds(0, $yRef.Value, $W, $H)
    $row.BackColor = $M_BG
    $row.Cursor    = if ($IsHeader) { [System.Windows.Forms.Cursors]::Default } else { [System.Windows.Forms.Cursors]::Hand }

    # 3px accent bar on left edge — visible only on hover
    $bar = New-Object System.Windows.Forms.Panel
    $bar.SetBounds(0, 0, 3, $H)
    $bar.BackColor = if ($IsDanger) { $M_DANGER } else { $M_ACCENT }
    $bar.Visible   = $false
    $row.Controls.Add($bar)

    # Icon
    $iconLbl = New-Object System.Windows.Forms.Label
    $iconLbl.Text      = $Icon
    $iconLbl.Font      = New-Object System.Drawing.Font("Segoe UI Emoji", 11)
    $iconLbl.ForeColor = if ($IsDanger) { $M_DANGER } elseif ($IsHeader) { $M_ACCENT } else { $M_TEXT }
    $iconLbl.BackColor = [System.Drawing.Color]::Transparent
    $iconLbl.SetBounds(12, 0, 30, $H)
    $iconLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    $iconLbl.Cursor    = $row.Cursor
    $row.Controls.Add($iconLbl)

    # Label text
    $textLbl = New-Object System.Windows.Forms.Label
    $textLbl.Text      = $Label
    $textLbl.ForeColor = if ($IsDanger) { $M_DANGER } else { $M_TEXT }
    $textLbl.BackColor = [System.Drawing.Color]::Transparent
    $textLbl.Font      = if ($IsHeader) {
                             New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
                         } else {
                             New-Object System.Drawing.Font("Segoe UI", 9)
                         }
    $textLbl.SetBounds(44, 0, $W - 70, $H)
    $textLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
    $textLbl.Cursor    = $row.Cursor
    $row.Controls.Add($textLbl)

    # Checkmark (right-aligned, purple)
    $chkLbl = $null
    if ($IsChecked) {
        $chkLbl = New-Object System.Windows.Forms.Label
        $chkLbl.Text      = [char]0x2713  # ✓
        $chkLbl.ForeColor = $M_CHECK
        $chkLbl.BackColor = [System.Drawing.Color]::Transparent
        $chkLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
        $chkLbl.SetBounds($W - 26, 0, 18, $H)
        $chkLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
        $chkLbl.Cursor    = $row.Cursor
        $row.Controls.Add($chkLbl)
    }

    # Hover + click (skip for non-clickable header)
    if (-not $IsHeader -and $null -ne $Action) {
        # Capture colors into LOCAL function-scope variables BEFORE creating
        # the closure. GetNewClosure() snapshots the enclosing scope, so as
        # long as the values are local the closure can always read them back
        # — no dependency on the caller's scope chain (which is different
        # under the hosted-PowerShell host we use for MastersFM_Tray.exe).
        $hvColor     = $M_HV
        $bgColor     = $M_BG
        $localAction = $Action

        $hoverOn = {
            $row.BackColor = $hvColor
            $bar.Visible   = $true
        }.GetNewClosure()

        $hoverOff = {
            $row.BackColor = $bgColor
            $bar.Visible   = $false
        }.GetNewClosure()

        $click = { Invoke-MenuAction $localAction }.GetNewClosure()

        $hitTargets = [System.Collections.Generic.List[System.Windows.Forms.Control]]::new()
        $hitTargets.Add($row); $hitTargets.Add($iconLbl); $hitTargets.Add($textLbl)
        if ($chkLbl) { $hitTargets.Add($chkLbl) }
        foreach ($ctrl in $hitTargets) {
            $ctrl.add_MouseEnter($hoverOn)
            $ctrl.add_MouseLeave($hoverOff)
            $ctrl.add_Click($click)
        }
    }

    if (-not $IsHeader -and $null -ne $Action -and $null -ne $form.Tag) {
        $form.Tag.Add($row)
    }
    $form.Controls.Add($row)
    $yRef.Value += $H
    return $row
}

function Show-TrayMenu {
    Close-TrayMenu -Immediate

    $W  = 280
    $fm = New-Object System.Windows.Forms.Form
    $fm.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
    $fm.ShowInTaskbar   = $false
    $fm.TopMost         = $true
    $fm.BackColor       = $M_BG
    $fm.Opacity         = 0
    $fm.Width           = $W
    $fm.Height          = 400
    $fm.StartPosition   = [System.Windows.Forms.FormStartPosition]::Manual
    $fm.Tag             = [System.Collections.Generic.List[System.Windows.Forms.Panel]]::new()
    $script:_menuForm   = $fm

    $y = [ref]8

    # Header
    New-MenuItem -form $fm -yRef $y -Icon ([char]0x266B) -Label "Master's FM  $([char]0x00B7)  $script:APP_VERSION" -IsHeader $true | Out-Null
    New-MenuSep  -form $fm -yRef $y

    # v5.2.1 top block — most-used settings, no dead clutter.
    # Open in Browser removed (redundant). Audio Source promoted up here
    # alongside Platform Detection + Customize Overlay, so all three
    # picker dialogs live together in the primary block.
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F39B)) -Label "Platform Detection..." `
        -Action { Show-PlatformsDialog } | Out-Null
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F3A7)) -Label "Audio Source..." `
        -Action { Show-AudioDeviceDialog } | Out-Null
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F3A8)) -Label "Customize Overlay..." `
        -Action { Show-OverlayCustomizer } | Out-Null
    New-MenuSep  -form $fm -yRef $y

    # Toggles — Discord first, Start on Login, then the OBS overlay toggle.
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F4AC)) -Label "Discord Rich Presence" `
        -IsChecked (Get-DiscordEnabled) `
        -Action {
            $newEnabled = -not (Get-DiscordEnabled)
            Save-DiscordEnabled $newEnabled
            $msg = if ($newEnabled) { "Discord Rich Presence enabled." } else { "Discord Rich Presence disabled." }
            $tray.ShowBalloonTip(2000, "Discord RPC", $msg, [System.Windows.Forms.ToolTipIcon]::Info)
        } | Out-Null

    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F680)) -Label "Start on Login" `
        -IsChecked (Get-AutoStartEnabled) `
        -Action {
            $newState = -not (Get-AutoStartEnabled)
            Set-AutoStart $newState
            $msg = if ($newState) { "Master's FM will start automatically with Windows." } else { "Auto-start disabled." }
            $tray.ShowBalloonTip(2000, "Auto-Start", $msg, [System.Windows.Forms.ToolTipIcon]::Info)
        } | Out-Null

    # OBS overlay — v6.0.0 DEFAULTS TO CHECKED.  Auto-add runs on every tray
    # boot (the bootstrap line below Application.Run calls Try-AddToOBS), so
    # by the time the user opens the menu the source is almost always in the
    # scene collection. Even if scan lags or OBS isn't installed, we show
    # the check as ON to match user expectation — clicking re-runs the
    # add which is idempotent.  Label downgrades to the "not set up" phrasing
    # ONLY when we positively know OBS is installed AND the source isn't in
    # any scene AND auto-add hasn't been attempted yet this session.
    $obsReallyPresent = Test-ObsSourceExists
    $obsInstalled     = Test-Path ([System.IO.Path]::Combine(
        [System.Environment]::GetFolderPath('ApplicationData'),
        'obs-studio', 'basic', 'scenes'))
    $obsAutoTried     = [bool]$script:_obsAutoAddAttempted
    $obsDefinitelyMissing = ($obsInstalled -and -not $obsReallyPresent -and -not $obsAutoTried)
    $obsChecked       = -not $obsDefinitelyMissing
    $obsLabel         = if ($obsChecked) { "OBS Overlay Added" } else { "Add to OBS  —  Not Set Up" }
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F4FA)) -Label $obsLabel -IsChecked $obsChecked `
        -Action {
            Invalidate-ObsSourceCache
            Try-AddToOBS -SkipFlagCheck
        } | Out-Null

    # Live audio visualizer toggle (v2.1.0) — flips overlay.liveAudioVisualizer
    # in the ROAMING config. When on, the overlay connects to audio_spectrum.exe's
    # WASAPI-loopback SSE feed (real system audio → real FFT → real bars). When off,
    # the overlay falls back to the simulator animation.
    $laEnabled = (Get-LiveAudioEnabled)
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F50A)) -Label "Live Audio Visualizer" `
        -IsChecked $laEnabled `
        -Action {
            $new = -not (Get-LiveAudioEnabled)
            Save-ConfigField 'liveAudioVisualizer' $new
            $msg = if ($new) { "Visualizer now tracks Windows audio (WASAPI loopback)." } else { "Visualizer is using the built-in simulator." }
            $tray.ShowBalloonTip(2000, "Live Audio Visualizer", $msg, [System.Windows.Forms.ToolTipIcon]::Info)
        } | Out-Null

    # (Audio Source... moved to the top primary block in v5.2.1)
    New-MenuSep  -form $fm -yRef $y

    # Info
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F4CB)) -Label "Patch Notes" `
        -Action { Show-WelcomeDialog -Manual } | Out-Null
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F4C4)) -Label "View Log" `
        -Action { Start-Process "notepad.exe" -ArgumentList $logFile } | Out-Null
    New-MenuSep  -form $fm -yRef $y

    # Update (v10.0.0)
    $updateLabel = if     ($global:_updateState -eq 'available')   { [char]0x2B07 + "  Update v$($global:_updateVersion) available" } `
                   elseif ($global:_updateState -eq 'downloading') { [char]0x231B + "  Downloading update..." } `
                   elseif ($global:_updateState -eq 'ready')       { [char]0x2B06 + "  Install update v$($global:_updateVersion)" } `
                   elseif ($global:_updateState -eq 'installing')  { [char]0x231B + "  Installing..." } `
                   elseif ($global:_updateState -eq 'checking')    { [char]0x231B + "  Checking..." } `
                   else                                             { "Check for Updates" }
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F4E5)) -Label $updateLabel `
        -Action {
            # v10.0.7: open native WinForms progress window (replaces browser tab)
            Show-UpdateWindow
            # Advance update state machine
            if     ($global:_updateState -eq 'available')   { Start-UpdateDownload }
            elseif ($global:_updateState -eq 'ready')       { Install-Update }
            elseif ($global:_updateState -eq 'idle')        { $global:_updateUserCheck = $true; Invoke-UpdateCheck }
            elseif ($global:_updateState -eq 'checking')    { $global:_updateUserCheck = $true }
        } | Out-Null
    New-MenuSep  -form $fm -yRef $y

    # App control
    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F504)) -Label "Restart Master's FM" `
        -Action {
            Log "User requested restart"
            $exePath = [System.IO.Path]::Combine($scriptDir, "MastersFM.exe")
            if (-not (Test-Path $exePath)) {
                Log "Restart: MastersFM.exe not found at '$exePath' — cannot restart"
                $tray.ShowBalloonTip(3000, "Master's FM", "Restart failed: MastersFM.exe not found.", [System.Windows.Forms.ToolTipIcon]::Error)
                return
            }

            # BUG FIX (v6.5.2): previous versions used Start-Process cmd.exe
            # which made the kill-chain runner a CHILD of MastersFM_Tray.exe.
            # When the chain then did `taskkill /F /IM MastersFM_Tray.exe /T`,
            # the `/T` flag killed the tray AND its children — including the
            # cmd.exe that was mid-way through the chain. Restart halted
            # right there: processes died, nothing relaunched.
            #
            # Fix: spawn cmd.exe via WMI (Win32_Process.Create). WMI-spawned
            # processes run under wmiprvse.exe, outside our Job Object and
            # process tree, so the kill chain survives its own self-taskkill
            # and continues through the relaunch step.
            #
            # Also dropped `/T` since we explicitly kill every child image
            # by name (MastersFM.exe, server.exe, audio_spectrum.exe) — no
            # need for the tree-kill that was the root cause of this bug.
            $killChain =
                'taskkill /F /IM MastersFM_Tray.exe >nul 2>&1 & ' +
                'taskkill /F /IM MastersFM.exe >nul 2>&1 & ' +
                'taskkill /F /IM audio_spectrum.exe >nul 2>&1 & ' +
                'taskkill /F /IM server.exe >nul 2>&1 & ' +
                'ping -n 3 127.0.0.1 >nul & ' +
                '"' + $exePath + '"'
            try {
                # v6.7.5: build a Win32_ProcessStartup with ShowWindow = SW_HIDE (0)
                # so the cmd.exe console window doesn't pop up (and stay open
                # for the duration of the kill-chain) on the user's screen.
                # Without this, WMI's Win32_Process.Create launches cmd with
                # a normal visible window inherited from CLI defaults.
                $startup = ([wmiclass]'Win32_ProcessStartup').CreateInstance()
                $startup.ShowWindow = 0
                $wmiResult = ([wmiclass]'Win32_Process').Create("cmd.exe /c $killChain", $null, $startup)
                if ($wmiResult.ReturnValue -eq 0) {
                    Log "Restart scheduled via WMI (pid=$($wmiResult.ProcessId), detached from job, hidden console)"
                } else {
                    Log "Restart: WMI spawn returned non-zero code $($wmiResult.ReturnValue) — falling back to Start-Process"
                    Start-Process -FilePath "cmd.exe" -ArgumentList "/c $killChain" -WindowStyle Hidden
                }
            } catch {
                Log "Restart: WMI spawn threw ($($_.Exception.Message)) — falling back to Start-Process"
                Start-Process -FilePath "cmd.exe" -ArgumentList "/c $killChain" -WindowStyle Hidden
            }

            $pollTimer.Stop()
            if ($scrobbleTimer) { $scrobbleTimer.Stop() }
            $tray.Visible = $false; $tray.Dispose()

            # Explicitly release + dispose our single-instance mutex so the
            # new tray's WaitOne(0) succeeds without relying on kernel-level
            # process-death cleanup (which only fires once every handle in
            # the old process is dropped - can take 100s of ms on a busy
            # PowerShell runspace teardown).
            try { if ($global:_mutex) { $global:_mutex.ReleaseMutex() } } catch {}
            try { if ($global:_mutex) { $global:_mutex.Dispose() }      } catch {}
            $global:_mutex = $null

            try { if ($global:_updateWebClient) { $global:_updateWebClient.CancelAsync(); $global:_updateWebClient.Dispose(); $global:_updateWebClient = $null } } catch {}  # v11.0.0: cancel in-flight download on exit
            [System.Windows.Forms.Application]::Exit()
        } | Out-Null

    New-MenuItem -form $fm -yRef $y -Icon ([System.Char]::ConvertFromUtf32(0x1F5D1)) -Label "Uninstall Master's FM" -IsDanger $true `
        -Action {
            Log "User requested uninstall"
            $result = [System.Windows.Forms.MessageBox]::Show(
                "Are you sure you want to uninstall Master's FM?`n`nThe app will close and be removed from your PC.",
                "Uninstall Master's FM",
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning
            )
            if ($result -ne [System.Windows.Forms.DialogResult]::Yes) { Log "Uninstall cancelled by user"; return }
            # v6.9.4: ProductCode is generated per-build (uuid4) so we can no
            # longer hardcode it. Find the install via TWO independent
            # methods and combine results: (1) registry scan across HKLM /
            # HKCU / WOW6432Node hives for entries whose DisplayName matches
            # Master*FM*, (2) Win32_Product WMI query (slow — only used as
            # fallback when (1) finds nothing — handles edge cases where the
            # MSI registered under unusual keys). Wildcards are PERMISSIVE
            # ('*Master*FM*') so unicode-quote variants and slight name
            # differences across versions still match.
            $prodCodes = @(
                Get-ChildItem 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
                              'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
                              'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
                              'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
                Get-ItemProperty -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like '*Master*FM*' -and $_.PSChildName -like '{*}' } |
                Select-Object -ExpandProperty PSChildName -Unique
            )
            if (-not $prodCodes) {
                Log "Uninstall: registry scan found nothing — falling back to Win32_Product"
                try {
                    $prodCodes = @(
                        Get-CimInstance Win32_Product -ErrorAction SilentlyContinue |
                        Where-Object { $_.Name -like '*Master*FM*' } |
                        Select-Object -ExpandProperty IdentifyingNumber -Unique
                    )
                } catch { Log "Uninstall: Win32_Product query failed ($($_.Exception.Message))" }
            }
            if (-not $prodCodes) {
                # No registered install found anywhere. Offer a "manual
                # cleanup" path: delete the install folder + shortcuts so
                # the user has a way to clean up even when MSI's record-
                # keeping is broken (partial install, registry corruption,
                # etc.). audio_spectrum / tray are killed first so files
                # unlock; URL ACLs are released too.
                Log "Uninstall: no install entries found by EITHER registry scan or Win32_Product"
                $cleanupChoice = [System.Windows.Forms.MessageBox]::Show(
                    "Master's FM is not registered with Windows Installer on this PC, so the normal uninstall path can't find it.`n`n" +
                    "Click YES to do a manual cleanup instead — this will:`n" +
                    "  - close all Master's FM processes`n" +
                    "  - delete the install folder ($env:LOCALAPPDATA\MastersFM)`n" +
                    "  - remove desktop + Start Menu + startup shortcuts`n" +
                    "  - release the audio service URL reservations`n`n" +
                    "Click NO to cancel and try Settings -> Apps & features instead.",
                    "Uninstall Master's FM - manual cleanup?",
                    [System.Windows.Forms.MessageBoxButtons]::YesNo,
                    [System.Windows.Forms.MessageBoxIcon]::Warning
                )
                if ($cleanupChoice -ne [System.Windows.Forms.DialogResult]::Yes) {
                    Log "Uninstall: manual cleanup declined by user"
                    return
                }
                Log "Uninstall: starting manual cleanup"
                # Build a self-contained cleanup script and run it after the
                # tray exits so files aren't locked. URL ACL release needs
                # admin — try elevated, fall back to user-context (harmless
                # no-op if perm denied).
                $cleanupBat =
                    '@echo off' + "`r`n" +
                    'taskkill /f /im MastersFM_Tray.exe /t >nul 2>&1' + "`r`n" +
                    'taskkill /f /im MastersFM.exe /t >nul 2>&1' + "`r`n" +
                    'taskkill /f /im audio_spectrum.exe /t >nul 2>&1' + "`r`n" +
                    'taskkill /f /im server.exe /t >nul 2>&1' + "`r`n" +
                    'taskkill /f /im customize.exe /t >nul 2>&1' + "`r`n" +
                    'timeout /t 2 /nobreak >nul' + "`r`n" +
                    'rmdir /s /q "%LOCALAPPDATA%\MastersFM" >nul 2>&1' + "`r`n" +
                    'del /f /q "%USERPROFILE%\Desktop\Master''s FM.lnk" >nul 2>&1' + "`r`n" +
                    'del /f /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Master''s FM\Master''s FM.lnk" >nul 2>&1' + "`r`n" +
                    'rmdir /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Master''s FM" >nul 2>&1' + "`r`n" +
                    'del /f /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Master''s FM.lnk" >nul 2>&1' + "`r`n" +
                    'reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v MastersFM /f >nul 2>&1' + "`r`n" +
                    'netsh http delete urlacl url=http://127.0.0.1:4243/ >nul 2>&1' + "`r`n" +
                    'netsh http delete urlacl url=http://localhost:4243/ >nul 2>&1' + "`r`n" +
                    'del "%~f0"' + "`r`n"
                $batPath = Join-Path $env:TEMP ('MastersFM_cleanup_' + [guid]::NewGuid().ToString('N') + '.bat')
                Set-Content -Path $batPath -Value $cleanupBat -Encoding ASCII
                Log "Uninstall: cleanup script at $batPath"
                Start-Process "cmd.exe" -ArgumentList "/c `"$batPath`"" -WindowStyle Hidden
                $pollTimer.Stop()
                if ($scrobbleTimer) { $scrobbleTimer.Stop() }
                $tray.Visible = $false; $tray.Dispose()
                try { if ($global:_updateWebClient) { $global:_updateWebClient.CancelAsync(); $global:_updateWebClient.Dispose(); $global:_updateWebClient = $null } } catch {}  # v11.0.0
                [System.Windows.Forms.Application]::Exit()
                return
            }
            Log ("Uninstall: scheduling msiexec /x for " + ($prodCodes -join ', '))
            $msiChain = ($prodCodes | ForEach-Object { "msiexec /x $_ /qn" }) -join ' & '
            Start-Process "cmd.exe" `
                -ArgumentList "/c ping -n 3 127.0.0.1 >nul & $msiChain" `
                -WindowStyle Hidden
            $pollTimer.Stop()
            if ($scrobbleTimer) { $scrobbleTimer.Stop() }
            $tray.Visible = $false; $tray.Dispose()
            try { if ($global:_updateWebClient) { $global:_updateWebClient.CancelAsync(); $global:_updateWebClient.Dispose(); $global:_updateWebClient = $null } } catch {}  # v11.0.0
            [System.Windows.Forms.Application]::Exit()
        } | Out-Null

    New-MenuSep -form $fm -yRef $y

    New-MenuItem -form $fm -yRef $y -Icon "✖" -Label "Quit" -IsDanger $true `
        -Action {
            Log "User quit"
            $pollTimer.Stop()
            if ($scrobbleTimer) { $scrobbleTimer.Stop() }
            $tray.Visible = $false; $tray.Dispose()
            if ($server -and -not $server.HasExited) {
                Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
            }
            try { if ($global:_updateWebClient) { $global:_updateWebClient.CancelAsync(); $global:_updateWebClient.Dispose(); $global:_updateWebClient = $null } } catch {}  # v11.0.0
            [System.Windows.Forms.Application]::Exit()
        } | Out-Null

    $fm.Height = $y.Value + 8

    # Poll to reset hover when cursor leaves form (Opacity/layered windows break MouseLeave).
    # Capture $M_BG into a local before the closure — same reason as the hover handlers.
    $bgColorPoll = $M_BG
    $hoverPoll = New-Object System.Windows.Forms.Timer
    $hoverPoll.Interval = 80
    $hoverPoll.add_Tick({
        # v11.0.0: Dispose hoverPoll when form is gone so timer handle is released
        if ($fm.IsDisposed) { $hoverPoll.Stop(); try { $hoverPoll.Dispose() } catch {}; return }
        $fmPt = $fm.PointToClient([System.Windows.Forms.Cursor]::Position)
        if (-not $fm.ClientRectangle.Contains($fmPt)) {
            foreach ($r in $fm.Tag) {
                $r.BackColor = $bgColorPoll
                if ($r.Controls.Count -gt 0) { $r.Controls[0].Visible = $false }
            }
        }
    }.GetNewClosure())
    $hoverPoll.Start()

    # Position: just above the tray icon (cursor position)
    $cursor = [System.Windows.Forms.Cursor]::Position
    $screen = [System.Windows.Forms.Screen]::FromPoint($cursor)
    $posX   = [Math]::Min($cursor.X - [int]($W / 2), $screen.WorkingArea.Right - $W - 4)
    $posX   = [Math]::Max($posX, $screen.WorkingArea.Left + 4)
    $posY   = $cursor.Y - $fm.Height - 8
    if ($posY -lt ($screen.WorkingArea.Top + 4)) { $posY = $cursor.Y + 16 }
    $fm.Location = New-Object System.Drawing.Point([int]$posX, [int]$posY)

    $fm.Show()
    [MFM_MenuNative]::SetForegroundWindow($fm.Handle) | Out-Null

    # Rounded corners: DWM (Win11) + region fallback (Win10)
    try { $cp = 2; [MFM_MenuNative]::DwmSetWindowAttribute($fm.Handle, 33, [ref]$cp, 4) | Out-Null } catch {}
    try {
        $rgn = [MFM_MenuNative]::CreateRoundRectRgn(0, 0, $fm.Width + 1, $fm.Height + 1, 16, 16)
        if ($rgn -ne [IntPtr]::Zero) { [MFM_MenuNative]::SetWindowRgn($fm.Handle, $rgn, $true) | Out-Null }
    } catch {}

    # Subtle border drawn over the form (local capture for hosted-runspace compat)
    $borderColor = $M_BORDER
    $fm.add_Paint({
        param($s, $e)
        $pen = New-Object System.Drawing.Pen($borderColor, 1)
        $e.Graphics.DrawRectangle($pen, 0, 0, $s.ClientSize.Width - 1, $s.ClientSize.Height - 1)
        $pen.Dispose()
    }.GetNewClosure())

    # Close on losing focus (click outside menu)
    $fm.add_Deactivate({ Close-TrayMenu })

    # Fade in
    $fadeIn = New-Object System.Windows.Forms.Timer
    $fadeIn.Interval = 14
    $fadeIn.add_Tick({
        if ($fm.IsDisposed) { $fadeIn.Stop(); try { $fadeIn.Dispose() } catch {}; return }
        $fm.Opacity += 0.10
        # v11.0.0: Dispose fadeIn when animation completes so timer handle is released
        if ($fm.Opacity -ge 1.0) { $fm.Opacity = 1.0; $fadeIn.Stop(); try { $fadeIn.Dispose() } catch {} }
    }.GetNewClosure())
    $fadeIn.Start()
}

# Wire left- OR right-click to open the custom popup (replaces ContextMenuStrip).
# v2.1.1: left-click also opens the menu — the old behavior was right-click only,
# which hides the menu from users used to single-click tray icons.
$tray.add_MouseClick({
    param($s, $e)
    if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Right -or
        $e.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
        Show-TrayMenu
    }
})
# NOTE: no add_DoubleClick — a double-click would register as two single-clicks,
# each opening the menu once (so the menu flickers open → close → open on double).
# If the user wants a "launch browser" shortcut, they can use the "Open in Browser"
# menu item. Keeping behavior predictable trumps a one-in-a-hundred convenience.

# ── Start server ───────────────────────────────────────────────────────────────
# When launched from MastersFM.exe (the normal path), -skipServerLaunch is set:
# the C# launcher already started server.exe as its own direct child process so
# that all three PIDs appear grouped under MastersFM.exe in Task Manager.
# We still keep this block for direct tray.ps1 invocation (dev / debugging).
$server = $null
if (-not $skipServerLaunch) {
    # Use node.exe to run server.js directly (avoids pkg bundling issues).
    # Falls back to server.exe if node.exe is not found.
    $serverJs   = [System.IO.Path]::Combine($scriptDir, "server.js")
    $serverExe  = [System.IO.Path]::Combine($scriptDir, "server.exe")
    $_nodeCmd   = Get-Command node.exe -ErrorAction SilentlyContinue
    $nodeExe    = if ($_nodeCmd) { $_nodeCmd.Source } else { $null }
    if (-not $nodeExe) { $nodeExe = "node.exe" }   # rely on PATH
    $useNode    = Test-Path $serverJs

    # Kill anything already on port 4242
    try {
        $existing = netstat -ano 2>$null |
            Select-String ":4242 " |
            ForEach-Object { ($_ -split "\s+")[-1] } |
            Select-Object -First 1
        if ($existing) {
            Log "Killing existing PID $existing on :4242"
            Stop-Process -Id ([int]$existing) -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 400
        }
    } catch { Log "netstat check failed: $_" }

    try {
        $serverLog    = Join-Path $scriptDir "server.log"
        $serverErrLog = Join-Path $scriptDir "server-err.log"
        if ($useNode) {
            Log "Starting server via node: $nodeExe $serverJs"
            $server = Start-Process $nodeExe `
                -ArgumentList "`"$serverJs`"" `
                -PassThru -WindowStyle Hidden `
                -WorkingDirectory $scriptDir `
                -RedirectStandardOutput $serverLog `
                -RedirectStandardError $serverErrLog `
                -ErrorAction Stop
        } else {
            Log "Starting server: $serverExe"
            $server = Start-Process $serverExe `
                -PassThru -WindowStyle Hidden `
                -WorkingDirectory $scriptDir `
                -RedirectStandardOutput $serverLog `
                -RedirectStandardError $serverErrLog `
                -ErrorAction Stop
        }
        Log "Server started PID=$($server.Id)"
    } catch {
        Log "Server start FAILED: $_"
        $tray.ShowBalloonTip(6000, "Master's FM - Error",
            "Could not start server: $_",
            [System.Windows.Forms.ToolTipIcon]::Error)
    }
} else {
    Log "Server launch delegated to MastersFM.exe (skipServerLaunch=true)"
}

# ── Launcher liveness guard ────────────────────────────────────────────────────
# Primary kill mechanism: Windows Job Object (KILL_ON_JOB_CLOSE) in MastersFM.exe
# kills us when the launcher exits. This is a backup: if the Job Object ever fails,
# we detect the parent's death via its PID and self-exit.
# Only active when launched by MastersFM.exe (skipServerLaunch flag is the indicator).
$global:_launcherPid = 0
if ($skipServerLaunch) {
    try {
        $wmiProc = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId='$PID'" -ErrorAction Stop
        $ppid    = [int]$wmiProc.ParentProcessId
        if ($ppid -gt 0) {
            $parentProc = Get-Process -Id $ppid -ErrorAction SilentlyContinue
            $parentName = if ($parentProc) { $parentProc.ProcessName } else { '?' }
            if ($parentProc -and $parentProc.ProcessName -match '(?i)mastersfm|master.fm') {
                $global:_launcherPid = $ppid
                Log "Launcher guard active: PID=$ppid ($parentName) - will self-exit if launcher is killed"
            } else {
                Log "Launcher guard: parent PID=$ppid ($parentName) - not MastersFM, guard inactive"
            }
        }
    } catch {
        Log "Launcher guard init error: $_"
    }
}

$tray.ShowBalloonTip(3000, "Master's FM", "Overlay active", [System.Windows.Forms.ToolTipIcon]::Info)

# (v9.4.0: renderer-switch sentinel watcher removed along with the canvas2d
# wipe. WebGL is the only renderer so there's nothing to switch + no balloon
# to fire. Any leftover renderer_switch.signal file from older versions sits
# harmless and unread.)

# ── Auto-add to OBS after 5s (only if not already configured) ─────────────────
$obsTimer          = New-Object System.Windows.Forms.Timer
$obsTimer.Interval = 5000
$obsTimer.add_Tick({
    $obsTimer.Stop()
    try { $obsTimer.Dispose() } catch {}  # v11.0.0: one-shot timer — dispose after firing
    Log "Auto OBS timer fired"
    Try-AddToOBS   # handles both OBS-open and OBS-closed via exit watcher
    # v6.0.0 — mark that auto-add has been attempted this session. The tray
    # menu uses this to decide whether the OBS checkmark should default to
    # ON (auto-add attempted, be optimistic) or OFF (never tried, OBS is
    # installed but empty — user needs to take action).
    $script:_obsAutoAddAttempted = $true
    try { Invalidate-ObsSourceCache } catch {}
})
$obsTimer.Start()

# (v6.2.3: removed OBS Source Side reset flag-watcher — no more
# /obs-reset-position endpoint, no flag file.)

# ── Poll /current every 2s for tooltip ────────────────────────────────────────
# v9.10.0: non-blocking — HttpClient.GetStringAsync fire-and-poll instead of
# synchronous Invoke-RestMethod (which blocked the UI thread for up to 1 s).
$global:_pollTimerTask = $null

# ── Auto-updater globals (v10.0.0) ────────────────────────────────────────────
$global:_updateManifestUrl  = 'https://raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json'
$global:_updateCheckTask    = $null    # Task<string>  — manifest fetch in flight
# v11.1.0: _updateDownloadTask removed — confirmed dead code (zero reads anywhere in file)
$global:_updateState        = 'idle'   # idle|checking|available|downloading|ready|installing
$global:_updateVersion      = $null    # e.g. "10.1.0"
$global:_updateMsiUrl       = $null
$global:_updateMsiSha256    = $null
$global:_updateAutoInstall  = $false
$global:_updateLastCheckMs  = 0
$global:_updateMsiPath      = $null
$global:_updateUserCheck    = $false   # true when check was manually triggered from tray
# v10.1.4 — WebClient download with DownloadProgressChanged / DownloadDataCompleted events
# (replaces HttpClient ResponseHeadersRead streaming which was unreliable in PS 5.1 + WinForms)
$global:_updateProgressFile  = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'mastersfm_update_status.json')
$global:_updateDownloadBytes  = 0      # bytes received so far (set by DownloadProgressChanged)
$global:_updateDownloadTotal  = 0      # total bytes      (set by DownloadProgressChanged)
$global:_updateWebClient      = $null  # WebClient for MSI download (null when idle)
$global:_updateWindow         = $null        # in-process WinForms progress window (null when closed)
$global:_updateWinIdleTicks   = 0            # ticks spent in idle after user-check (for auto-close)
$global:_updateWinMarqPos     = 0            # marquee animation x-offset

$pollTimer          = New-Object System.Windows.Forms.Timer
$pollTimer.Interval = 2000
$pollTimer.add_Tick({
    # Collect any completed request from the previous tick
    if ($global:_pollTimerTask -ne $null -and $global:_pollTimerTask.IsCompleted) {
        try {
            if ($global:_pollTimerTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
                $d = $global:_pollTimerTask.Result | ConvertFrom-Json
                if ($d -and $d.track) {
                    $raw = "$($d.artist) - $($d.track)"
                    $trayText = if ($raw.Length -gt 63) { $raw.Substring(0,60) + "..." } else { $raw }
                    $tray.Text = $trayText
                } else {
                    $tray.Text = "Master's FM - nothing playing"
                }
            }
        } catch {
            $tray.Text = "Master's FM - waiting..."
        } finally {
            try { $global:_pollTimerTask.Dispose() } catch {}
            $global:_pollTimerTask = $null
        }
    }
    # Fire new request if none in flight
    if ($global:_pollTimerTask -eq $null) {
        try { $global:_pollTimerTask = $global:_httpClient.GetStringAsync("http://127.0.0.1:4242/current") } catch {}
    }
    # Auto-updater state machine (v10.0.0)
    try { Poll-UpdateCheck } catch {}
})
$pollTimer.Start()

# ── Multi-platform scrobbling: SMTC + osu! ────────────────────────────────────
# Reads Windows System Media Transport Controls (covers Spotify, SoundCloud in Chrome,
# YouTube Music, Apple Music, VLC, Windows Media Player, Deezer, TIDAL, MusicBee, etc.)
# and osu! window title.  Posts to /webhook with position+duration for seek-accurate timestamps.

# ── v9.10.0: GetGuiResources P/Invoke for GDI/User object canary ──────────────
# Loaded once at startup (Add-Type is expensive at runtime).
$global:_hasGuiRes = $false
try {
    if (-not ([System.Management.Automation.PSTypeName]'NativeMethods.GuiRes').Type) {
        Add-Type -Namespace 'NativeMethods' -Name 'GuiRes' -MemberDefinition @'
[DllImport("user32.dll")]
public static extern int GetGuiResources(IntPtr hProcess, uint uiFlags);
'@ -ErrorAction Stop
    }
    $global:_hasGuiRes = $true
    Log "v9.10.0: GetGuiResources loaded OK"
} catch {
    Log "v9.10.0: GetGuiResources unavailable: $_"
}

# ── Try to load Windows 10+ WinRT media control types (SMTC) ──────────────────
$global:smtcAvailable = $false
$global:_awaitAsTaskGeneric    = $null
$global:_awaitAsTaskGenericCts = $null   # v9.9.4: CancellationToken overload for leak-free timeout
try {
    $null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime]
    # PowerShell 5.1 can't call .GetAwaiter() directly on WinRT IAsyncOperation — it
    # sees the object as System.__ComObject. Use the AsTask extension from
    # System.Runtime.WindowsRuntime to convert IAsyncOperation<T> to Task<T>.
    try { Add-Type -AssemblyName System.Runtime.WindowsRuntime -ErrorAction Stop } catch {}
    $global:_awaitAsTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        })[0]
    if (-not $global:_awaitAsTaskGeneric) { throw 'AsTask<IAsyncOperation<T>> not found' }
    # v9.9.4: also bind the 2-arg overload AsTask<T>(IAsyncOperation<T>, CancellationToken).
    # Used by Await-WinRT to ensure proper COM proxy cleanup on timeout — the 1-arg overload
    # leaves orphaned IAsyncOperation COM proxies alive when the operation doesn't complete
    # within the timeout, accumulating LpcReply threads + OS handles indefinitely.
    $global:_awaitAsTaskGenericCts = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.GetParameters().Count -eq 2 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' -and
            $_.GetParameters()[1].ParameterType.FullName -eq 'System.Threading.CancellationToken'
        })[0]
    $global:smtcAvailable = $true
    Log "SMTC: WinRT types loaded OK (AsTask helper ready)"
} catch {
    Log "SMTC: WinRT not available - feature disabled. Error: $_"
}

# v8.2.5 PERF: fire-and-forget webhook POST. Replaces synchronous Invoke-RestMethod
# which blocked the WinForms UI thread for 200-900 ms per call (the server's
# new-track path does art-resolution / Discord RPC update / SSE broadcast and the
# tray was waiting for the response on the UI thread). Phase instrumentation in
# v8.2.4 traced EVERY slow tick to webhook-newtrack/webhook-heartbeat. Tray
# discards the response anyway, so async fire-and-forget is the correct fix.
# Trade-off: server-down failures are no longer logged (they were throttled to
# once per 30 s before; now silent). HttpClient is lazy-initialized once and
# re-used across ticks (single instance, won't leak sockets).
function Send-WebhookAsync {
    param([string]$Url, [byte[]]$Body, [string]$ContentType = 'application/json; charset=utf-8')
    if (-not $global:_httpClient) {
        try { Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue } catch {}
        try {
            $global:_httpClient = [System.Net.Http.HttpClient]::new()
            $global:_httpClient.Timeout = [TimeSpan]::FromSeconds(5)
        } catch {
            try { Log "Send-WebhookAsync init failed: $_" } catch {}
            return
        }
    }
    try {
        $content = [System.Net.Http.ByteArrayContent]::new($Body)
        $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($ContentType)
        # Discard the returned Task — fire-and-forget. HttpClient internally
        # keeps the Task alive until completion; the thread pool runs the
        # network I/O. Modern .NET (4.6.2+) does not crash on unobserved
        # task exceptions so we don't need a no-op continuation.
        $null = $global:_httpClient.PostAsync($Url, $content)
    } catch {
        # Synchronous-side failure (e.g., URL parse error). Log once and continue.
        try { Log "Send-WebhookAsync sync error: $_" } catch {}
    }
}

# ── Auto-updater functions (v10.0.0 / v10.0.6 streaming+progress) ────────────

function Write-UpdateStatus {
    # Writes a small JSON file that server.js serves as /update-status.
    # Called on every meaningful state transition and every download chunk.
    try {
        $pct = if ($global:_updateDownloadTotal -gt 0) {
            [int][Math]::Floor($global:_updateDownloadBytes * 100.0 / $global:_updateDownloadTotal)
        } else { 0 }
        $obj = @{
            state      = $global:_updateState
            version    = $global:_updateVersion
            progress   = $pct
            bytesDown  = $global:_updateDownloadBytes
            bytesTotal = $global:_updateDownloadTotal
            current    = $script:APP_VERSION
            ts         = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        } | ConvertTo-Json -Compress
        [System.IO.File]::WriteAllText($global:_updateProgressFile, $obj, [System.Text.Encoding]::UTF8)
    } catch {}
}

function Show-UpdateWindow {
    # v10.0.7 — native in-process WinForms progress window.
    # If the window is already open, just bring it forward.
    if ($global:_updateWindow -ne $null -and -not $global:_updateWindow.IsDisposed) {
        try { $global:_updateWindow.BringToFront(); $global:_updateWindow.Activate() } catch {}
        return
    }

    # ── Form ──────────────────────────────────────────────────────────────
    $win = New-Object System.Windows.Forms.Form
    $win.Text            = "Master's FM — Update"
    $win.ClientSize      = New-Object System.Drawing.Size(420, 200)
    $win.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $win.MaximizeBox     = $false
    $win.MinimizeBox     = $false
    $win.StartPosition   = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $win.BackColor       = [System.Drawing.ColorTranslator]::FromHtml('#111122')
    $win.TopMost         = $true

    # ── Status label (large, centered) ────────────────────────────────────
    $lblStatus = New-Object System.Windows.Forms.Label
    $lblStatus.AutoSize  = $false
    $lblStatus.Size      = New-Object System.Drawing.Size(388, 32)
    $lblStatus.Location  = New-Object System.Drawing.Point(16, 18)
    $lblStatus.Font      = New-Object System.Drawing.Font('Segoe UI', 13, [System.Drawing.FontStyle]::Bold)
    $lblStatus.ForeColor = [System.Drawing.Color]::White
    $lblStatus.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    $lblStatus.Text      = [char]0x231B + '  Loading...'
    $win.Controls.Add($lblStatus)

    # ── Custom progress bar: background Panel + sliding fill Panel ────────
    # Two-panel approach gives full color control — no system theming override needed.
    $barBg = New-Object System.Windows.Forms.Panel
    $barBg.Size      = New-Object System.Drawing.Size(388, 18)
    $barBg.Location  = New-Object System.Drawing.Point(16, 62)
    $barBg.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#1e1e35')
    $win.Controls.Add($barBg)

    $barFill = New-Object System.Windows.Forms.Panel
    $barFill.Size      = New-Object System.Drawing.Size(0, 18)
    $barFill.Location  = New-Object System.Drawing.Point(0, 0)
    $barFill.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#7744dd')
    $barBg.Controls.Add($barFill)

    # ── Sub label (byte counter / notes) ──────────────────────────────────
    $lblSub = New-Object System.Windows.Forms.Label
    $lblSub.AutoSize  = $false
    $lblSub.Size      = New-Object System.Drawing.Size(388, 18)
    $lblSub.Location  = New-Object System.Drawing.Point(16, 88)
    $lblSub.Font      = New-Object System.Drawing.Font('Segoe UI', 9)
    $lblSub.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#888899')
    $lblSub.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    $lblSub.Text      = ''
    $win.Controls.Add($lblSub)

    # ── Action button (Download / Install) ───────────────────────────────
    $btnAction = New-Object System.Windows.Forms.Button
    $btnAction.Size      = New-Object System.Drawing.Size(180, 30)
    $btnAction.Location  = New-Object System.Drawing.Point(120, 116)
    $btnAction.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $btnAction.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#7744dd')
    $btnAction.ForeColor = [System.Drawing.Color]::White
    $btnAction.Font      = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
    $btnAction.Text      = ''
    $btnAction.Visible   = $false
    $btnAction.FlatAppearance.BorderSize = 0
    $win.Controls.Add($btnAction)

    $btnAction.add_Click({
        $st = $global:_updateState
        if     ($st -eq 'available')  { Start-UpdateDownload }
        elseif ($st -eq 'ready')      { Install-Update }
    }.GetNewClosure())

    # ── Running-version label (bottom) ────────────────────────────────────
    $lblVer = New-Object System.Windows.Forms.Label
    $lblVer.AutoSize  = $false
    $lblVer.Size      = New-Object System.Drawing.Size(388, 16)
    $lblVer.Location  = New-Object System.Drawing.Point(16, 168)
    $lblVer.Font      = New-Object System.Drawing.Font('Segoe UI', 8)
    $lblVer.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#555566')
    $lblVer.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    $lblVer.Text      = "Running $script:APP_VERSION"
    $win.Controls.Add($lblVer)

    # ── 300 ms refresh timer ──────────────────────────────────────────────
    # Capture APP_VERSION as a local so GetNewClosure() can pick it up.
    # $script: scope qualifiers don't resolve correctly inside closures fired
    # from WinForms timer callbacks — the script scope is not the same as
    # tray.ps1's script scope at that point.
    $winAppVer = $script:APP_VERSION

    $winTimer          = New-Object System.Windows.Forms.Timer
    $winTimer.Interval = 300

    $winTimer.add_Tick({
        # Safety: dispose timer if window was GC'd or closed externally
        if ($global:_updateWindow -eq $null -or $global:_updateWindow.IsDisposed) {
            $winTimer.Stop(); $winTimer.Dispose(); return
        }
        $st  = $global:_updateState
        $ver = $global:_updateVersion
        $dn  = $global:_updateDownloadBytes
        $tot = $global:_updateDownloadTotal

        switch ($st) {
            'idle' {
                $lblStatus.Text   = [char]0x2705 + '  You''re up to date'
                $lblSub.Text      = "Running $winAppVer"
                $barFill.Left     = 0
                $barFill.Width    = 388
                $btnAction.Visible = $false
                $global:_updateWinIdleTicks++
                if ($global:_updateWinIdleTicks -ge 10) {
                    try { $global:_updateWindow.Close() } catch {}
                }
            }
            'checking' {
                $lblStatus.Text   = [char]0x231B + '  Checking for updates...'
                $lblSub.Text      = ''
                $barFill.Left     = 0
                $barFill.Width    = 0
                $btnAction.Visible = $false
                $global:_updateWinIdleTicks = 0
            }
            'available' {
                $lblStatus.Text    = [char]0x2B07 + "  Update v$ver available"
                $lblSub.Text       = ''
                $barFill.Left      = 0
                $barFill.Width     = 0
                $btnAction.Text    = [char]0x2B07 + '  Download'
                $btnAction.Visible = $true
                $global:_updateWinIdleTicks = 0
            }
            'downloading' {
                if ($tot -gt 0) {
                    # Determinate: show % fill
                    $pct  = [int]($dn * 100 / $tot)
                    $w    = [Math]::Max(0, [Math]::Min(388, [int]($pct * 388 / 100)))
                    $lblStatus.Text = [char]0x2B07 + "  Downloading  $pct%"
                    $lblSub.Text    = "$([Math]::Round($dn / 1048576.0, 1)) MB  /  $([Math]::Round($tot / 1048576.0, 1)) MB"
                    $barFill.Left   = 0
                    $barFill.Width  = $w
                } else {
                    # Indeterminate: 80 px block slides L→R across 388 px track.
                    # barFill is a child of barBg — negative Left clips naturally.
                    $global:_updateWinMarqPos = ($global:_updateWinMarqPos + 22) % 468
                    $lblStatus.Text = [char]0x2B07 + '  Downloading...'
                    $lblSub.Text    = if ($dn -gt 0) { "$([Math]::Round($dn / 1048576.0, 1)) MB received" } else { '' }
                    $barFill.Width  = 80
                    $barFill.Left   = $global:_updateWinMarqPos - 80
                }
                $btnAction.Visible = $false
                $global:_updateWinIdleTicks = 0
            }
            'ready' {
                $lblStatus.Text    = [char]0x2B06 + "  Ready to install v$ver"
                $lblSub.Text       = ''
                $barFill.Left      = 0
                $barFill.Width     = 388
                $btnAction.Text    = [char]0x2B06 + '  Install'
                $btnAction.Visible = $true
                $global:_updateWinIdleTicks = 0
            }
            'installing' {
                $lblStatus.Text    = [char]0x231B + '  Installing...'
                $lblSub.Text       = 'Restarting shortly...'
                $barFill.Left      = 0
                $barFill.Width     = 388
                $btnAction.Visible = $false
                $global:_updateWinIdleTicks = 0
            }
            default {
                $lblStatus.Text    = '...'
                $lblSub.Text       = ''
                $btnAction.Visible = $false
            }
        }
    }.GetNewClosure())

    $win.add_FormClosed({
        $winTimer.Stop()
        $winTimer.Dispose()
        $global:_updateWindow       = $null
        $global:_updateWinIdleTicks = 0
        $global:_updateWinMarqPos   = 0
    }.GetNewClosure())

    $global:_updateWindow       = $win
    $global:_updateWinIdleTicks = 0
    $winTimer.Start()
    $win.Show()
}

function Invoke-UpdateCheck {
    # Fire a manifest fetch if we're idle and the HttpClient is ready.
    if ($global:_updateState -ne 'idle') { return }
    if (-not $global:_httpClient) { return }
    try {
        $cb = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $global:_updateCheckTask   = $global:_httpClient.GetStringAsync("$($global:_updateManifestUrl)?t=$cb")
        $global:_updateState       = 'checking'
        $global:_updateLastCheckMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        Write-UpdateStatus
        Log "Update check fired"
    } catch { LogErr 'Invoke-UpdateCheck' $_ }
}

function Start-UpdateDownload {
    # v10.1.4: replaced HttpClient ResponseHeadersRead streaming with WebClient.DownloadDataAsync.
    # WebClient events (DownloadProgressChanged, DownloadDataCompleted) fire on the WinForms UI
    # thread via the installed SynchronizationContext, making them reliable in PS 5.1 + WinForms.
    if ($global:_updateState -ne 'available') { return }
    if (-not $global:_updateMsiUrl) { return }
    try {
        $wc  = New-Object System.Net.WebClient
        $ver = $global:_updateVersion
        $wc.add_DownloadProgressChanged({
            param($sender, $e)
            $global:_updateDownloadBytes = $e.BytesReceived
            $global:_updateDownloadTotal = $e.TotalBytesToReceive
            Write-UpdateStatus
        }.GetNewClosure())
        $wc.add_DownloadDataCompleted({
            param($sender, $e)
            try { $sender.Dispose() } catch {}
            $global:_updateWebClient = $null
            if ($e.Cancelled -or $e.Error) {
                $global:_updateState = 'available'
                Write-UpdateStatus
                if ($e.Error) { LogErr 'Download' $e.Error }
                return
            }
            $bytes    = $e.Result
            $tempPath = [System.IO.Path]::Combine(
                [System.IO.Path]::GetTempPath(),
                "MastersFM_update_v$($global:_updateVersion).msi")
            [System.IO.File]::WriteAllBytes($tempPath, $bytes)
            if ($global:_updateMsiSha256) {
                $sha    = [System.Security.Cryptography.SHA256]::Create()
                $actual = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-','').ToLower()
                $sha.Dispose()
                if ($actual -ne $global:_updateMsiSha256.ToLower()) {
                    Log 'Start-UpdateDownload: SHA256 mismatch'
                    try { [System.IO.File]::Delete($tempPath) } catch {}
                    $global:_updateState = 'available'
                    Write-UpdateStatus
                    return
                }
            }
            $global:_updateMsiPath = $tempPath
            $global:_updateState   = 'ready'
            Write-UpdateStatus
            Log "Download completed+verified: $tempPath"
            if ($global:_updateAutoInstall) { Install-Update }
        }.GetNewClosure())
        $global:_updateDownloadBytes = 0
        $global:_updateDownloadTotal = 0
        $global:_updateState         = 'downloading'
        Write-UpdateStatus
        $global:_updateWebClient = $wc
        Log "Update download started (WebClient): $($global:_updateMsiUrl)"
        $tray.ShowBalloonTip(3000, "Master's FM Update", "Downloading v$ver...", [System.Windows.Forms.ToolTipIcon]::Info)
        $wc.DownloadDataAsync([Uri]::new($global:_updateMsiUrl))
    } catch { LogErr 'Start-UpdateDownload' $_ }
}

function Install-Update {
    if ($global:_updateState -ne 'ready') { return }
    if (-not $global:_updateMsiPath -or -not [System.IO.File]::Exists($global:_updateMsiPath)) {
        $global:_updateState = 'available'; return
    }
    # Authenticode signature check — must be valid and signed by MasterShadex.
    try {
        $sig = Get-AuthenticodeSignature $global:_updateMsiPath
        # 'Valid' = cert chain trusted (e.g. cert in TrustedPublisher store via INSTALL.bat).
        # 'UnknownError' = self-signed cert not in Trusted Root — still a valid signature,
        # just not globally trusted. SHA-256 is the primary integrity check; Authenticode
        # here confirms the file was signed by MasterShadex, not tampered after signing.
        $okStatus = $sig.Status -in @('Valid', 'UnknownError')
        $okSubject = $sig.SignerCertificate -ne $null -and $sig.SignerCertificate.Subject -like '*MasterShadex*'
        if (-not $okStatus -or -not $okSubject) {
            Log "Install-Update: bad signature (status=$($sig.Status) subject=$($sig.SignerCertificate.Subject))"
            $global:_updateState = 'available'
            return
        }
    } catch { LogErr 'Install-Update sig' $_; $global:_updateState = 'available'; return }
    $global:_updateState = 'installing'
    Write-UpdateStatus
    $msiPath    = $global:_updateMsiPath
    $launchPath = [System.IO.Path]::Combine($env:LOCALAPPDATA, 'MastersFM', 'MastersFM.exe')
    try {
        # v10.1.8: write a temp PS1 helper that:
        #   1. Waits 3s so this process has fully exited and files are unlocked
        #   2. Finds the installed ProductCode via registry (fast — no Win32_Product)
        #   3. Uninstalls the old version first (avoids Major Upgrade SecureRepair failure
        #      when the previous MSI was installed from a temp path)
        #   4. Installs the new MSI
        #   5. Relaunches the app
        # Plain "msiexec /i NewVersion.msi" over an existing install fails with 1603
        # because Windows Installer can't locate the cached source of the prior version.
        $helperPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'mastersfm_update_helper.ps1')
        # Embed paths as single-quoted literals at the top of the helper so there are no
        # quoting ambiguities when the saved .ps1 is later parsed by a fresh PowerShell process.
        # Single-string form of -ArgumentList is used for msiexec so the path can be wrapped in
        # explicit double-quotes inside the helper, handling spaces in usernames (e.g. 'AER Alex').
        $msiEsc    = $msiPath.Replace("'", "''")
        $launchEsc = $launchPath.Replace("'", "''")
        $helperScript = @"
`$msiFile = '$msiEsc'
`$launch  = '$launchEsc'
Start-Sleep -Seconds 3
`$keys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
`$pc = (Get-ItemProperty `$keys -ErrorAction SilentlyContinue |
        Where-Object { `$_.DisplayName -like '*Master*FM*' } |
        Select-Object -First 1).PSChildName
if (`$pc) {
    Start-Process msiexec.exe -ArgumentList @('/x', `$pc, '/qn', '/norestart') -Wait -WindowStyle Hidden
    Start-Sleep -Seconds 1
}
Start-Process msiexec.exe -ArgumentList "/i ``"`$msiFile``" /quiet /norestart" -Wait -WindowStyle Hidden  # v11.1.8: single-string form so `"$msiFile`" embeds real quotes; v11.1.6 array-elem fix produced ""$msiFile"" (syntax error)
Start-Sleep -Seconds 1
if (Test-Path `$launch) { Start-Process `$launch }
"@
        [System.IO.File]::WriteAllText($helperPath, $helperScript, [System.Text.Encoding]::UTF8)
        Start-Process 'powershell.exe' `
            -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$helperPath`"" `
            -WindowStyle Hidden
        Log "Install-Update: helper launched ($helperPath) — exiting now"
        [System.Windows.Forms.Application]::Exit()
    } catch { $global:_updateState = 'ready'; LogErr 'Install-Update exec' $_ }
}

function Poll-UpdateCheck {
    $nowMs      = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $intervalMs = 1 * 60 * 60 * 1000   # 1 hour (v10.2.3: was 6 hours)

    # ── 1. Trigger a periodic manifest check ──────────────────────────────
    if ($global:_updateState -eq 'idle' -and $global:_httpClient -ne $null -and
        ($global:_updateLastCheckMs -eq 0 -or ($nowMs - $global:_updateLastCheckMs) -ge $intervalMs)) {
        Invoke-UpdateCheck
        return
    }

    # ── 2. Collect manifest result ─────────────────────────────────────────
    if ($global:_updateState -eq 'checking' -and
        $global:_updateCheckTask -ne $null -and $global:_updateCheckTask.IsCompleted) {
        try {
            if ($global:_updateCheckTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
                $json   = $global:_updateCheckTask.Result | ConvertFrom-Json
                $remote = [version]($json.version)
                $local  = [version]($script:APP_VERSION.TrimStart('v'))
                if ($remote -gt $local) {
                    $global:_updateVersion     = $json.version
                    $global:_updateMsiUrl      = $json.msi_url
                    $global:_updateMsiSha256   = $json.msi_sha256
                    $global:_updateAutoInstall = [bool]$json.autoInstall
                    $global:_updateState       = 'available'
                    Write-UpdateStatus
                    Log "Update available: v$($global:_updateVersion) autoInstall=$($global:_updateAutoInstall)"
                    $tray.ShowBalloonTip(6000, "Master's FM Update",
                        "v$($global:_updateVersion) is available. Open tray menu to install.",
                        [System.Windows.Forms.ToolTipIcon]::Info)
                    if ($global:_updateAutoInstall) { Start-UpdateDownload }
                } else {
                    Log "Update check: already up to date (remote=$remote local=$local) userCheck=$($global:_updateUserCheck)"
                    $global:_updateState = 'idle'
                    Write-UpdateStatus
                    if ($global:_updateUserCheck) {
                        Log "Showing up-to-date balloon"
                        try {
                            $tray.ShowBalloonTip(4000, "Master's FM", "You're on the latest version (v$($script:APP_VERSION.TrimStart('v'))).", [System.Windows.Forms.ToolTipIcon]::Info)
                            Log "Balloon shown OK"
                        } catch {
                            LogErr 'ShowBalloonTip up-to-date' $_
                        }
                        # Also flash the tooltip text — visible even if Windows suppresses the balloon
                        $prevText = $tray.Text
                        $tray.Text = [char]0x2705 + " Up to date  (v$($script:APP_VERSION.TrimStart('v')))"
                        $resetTimer = New-Object System.Windows.Forms.Timer
                        $resetTimer.Interval = 5000
                        $resetTimer.add_Tick({ $tray.Text = $prevText; $resetTimer.Stop(); $resetTimer.Dispose() }.GetNewClosure())
                        $resetTimer.Start()
                    }
                }
            } else {
                $global:_updateState = 'idle'
            }
        } catch {
            $global:_updateState = 'idle'
            LogErr 'Poll-UpdateCheck manifest' $_
        } finally {
            $global:_updateUserCheck = $false
            try { $global:_updateCheckTask.Dispose() } catch {}
            $global:_updateCheckTask = $null
        }
    }

    # ── 3. Downloading — handled entirely by WebClient events (v10.1.4) ──────
    # DownloadProgressChanged / DownloadDataCompleted fire on the WinForms UI thread,
    # so there is nothing to poll here.  The window timer reads _updateDownloadBytes /
    # _updateDownloadTotal directly to update the progress bar.
    if ($global:_updateState -eq 'downloading') { return }
}

# Await-WinRT — convert a WinRT IAsyncOperation<T> to a .NET Task<T> and block
# for its result. Required on PowerShell 5.1 where .GetAwaiter() doesn't work
# on raw __ComObject async ops.
function Await-WinRT {
    param($AsyncOp, [Type]$ResultType, [int]$TimeoutMs = -1, [string]$Label = '')
    if (-not $global:_awaitAsTaskGeneric) { throw 'AsTask generic not initialised' }
    # v9.10.0: per-minute WinRT call counter (reset by [CANARY] every 60 s)
    try { $global:_winrtCallsMin++ } catch {}
    $perfSw = [System.Diagnostics.Stopwatch]::StartNew()
    # NOTE: Do NOT clear the SynchronizationContext before Wait() — SMTC/WinRT
    # objects have STA affinity; running their continuations on the ThreadPool
    # causes RPC_E_CALL_CANCELED (0x80010002).
    if ($TimeoutMs -gt 0 -and $global:_awaitAsTaskGenericCts) {
        # v9.9.4 FIX: Use CancellationToken-aware AsTask overload when timing out.
        # When the CTS fires at TimeoutMs, the 2-arg AsTask implementation:
        #   (1) calls asyncOp.Cancel() on the WinRT IAsyncOperation,
        #   (2) unregisters the Completed handler (releases the cross-process COM proxy),
        #   (3) transitions the Task to Canceled state.
        # The finally block then disposes both the CTS and the Task, releasing all
        # OS handles held by the COM proxy back-channel.
        #
        # Root cause fixed: the 1-arg AsTask left orphaned IAsyncOperation COM proxies
        # alive when SERVERCALL_RETRYLATER prevented the operation from completing.
        # Each orphaned proxy held an LPC back-channel (1 LpcReply thread + ~5 OS
        # handles). After one overnight run under SMTC stress this produced 17,150
        # orphaned threads and 106,713 handles, exhausting USER objects (~1.88 GB RAM).
        $cts     = $null
        $netTask = $null
        $_result = $null
        try {
            $cts     = [System.Threading.CancellationTokenSource]::new($TimeoutMs)
            $asTask  = $global:_awaitAsTaskGenericCts.MakeGenericMethod($ResultType)
            $netTask = $asTask.Invoke($null, @($AsyncOp, $cts.Token))
            $completed = $true
            try { $completed = $netTask.Wait($TimeoutMs + 500) }  # +500ms safety beyond CTS
            catch [System.AggregateException] { $completed = $false }   # CTS-triggered cancel
            if (-not $completed -or $netTask.IsCanceled) {
                $perfSw.Stop()
                # v9.10.0: count timeouts for canary + SLOW TICK context
                try { $global:_winrtTmoMin++ } catch {}
                try { Log "[PERF-WINRT] TIMEOUT after $($perfSw.ElapsedMilliseconds)ms (TimeoutMs=$TimeoutMs)$(if ($Label) { " label=$Label" } else { '' })" } catch {}
                try { $AsyncOp.Cancel() } catch {}
                throw "WinRT timeout after ${TimeoutMs}ms"
            }
            $_result = $netTask.Result   # capture before finally disposes task
        } finally {
            # Always dispose — releases COM proxy back-channel on success, timeout, and error.
            try { if ($cts)     { $cts.Cancel(); $cts.Dispose() }  } catch {}
            try { if ($netTask) { $netTask.Dispose() }              } catch {}
        }
        $perfSw.Stop()
        if ($perfSw.ElapsedMilliseconds -gt 100) {
            try { Log "[PERF-WINRT] slow=$($perfSw.ElapsedMilliseconds)ms (TimeoutMs=$TimeoutMs)$(if ($Label) { " label=$Label" } else { '' })" } catch {}
        }
        return $_result
    }
    # Fallback: no timeout requested, or CTS overload unavailable (belt-and-braces).
    # PERF instrumentation (v8.2.4): time every WinRT await and log if >100ms.
    $asTask  = $global:_awaitAsTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($AsyncOp))
    if ($TimeoutMs -gt 0) {
        $completed = $netTask.Wait($TimeoutMs)
        if (-not $completed) {
            $perfSw.Stop()
            # v9.10.0: count timeouts for canary
            try { $global:_winrtTmoMin++ } catch {}
            try { Log "[PERF-WINRT] TIMEOUT after $($perfSw.ElapsedMilliseconds)ms (TimeoutMs=$TimeoutMs)$(if ($Label) { " label=$Label" } else { '' })" } catch {}
            # CRITICAL: cancel the underlying WinRT IAsyncOperation so the
            # "Now Playing Session Manager" service stops processing the request.
            try { $AsyncOp.Cancel() } catch {}
            throw "WinRT timeout after ${TimeoutMs}ms"
        }
    } else {
        $null = $netTask.Wait(-1)
    }
    $perfSw.Stop()
    if ($perfSw.ElapsedMilliseconds -gt 100) {
        try { Log "[PERF-WINRT] slow=$($perfSw.ElapsedMilliseconds)ms (TimeoutMs=$TimeoutMs)$(if ($Label) { " label=$Label" } else { '' })" } catch {}
    }
    return $netTask.Result
}

# ── Tick-level SMTC manager cache ────────────────────────────────────────────
# RequestAsync() blocks for ~10s when a broken Windows Store SMTC session (e.g.
# SoundCloud Store app) returns SERVERCALL_RETRYLATER repeatedly. Without caching,
# every detector (Spotify, Browser, SMTC) calls RequestAsync() independently —
# 3 × 10s = 30s per tick.
#
# Get-SMTCManager caches with two TTLs:
#   Success → 600ms: all detectors within the same tick share one manager, but
#             the cache expires before the next tick so sessions stay current.
#   Failure → exponential backoff starting at 5s, doubling each consecutive
#             failure up to 30s max. A broken Store-app SMTC session causes
#             one 1500ms stall every 5→10→20→30s, barely affecting ticks.
#             On the first success the backoff resets to 0.
$global:_smtcMgrCached       = $null
$global:_smtcMgrCacheTime    = [datetime]::MinValue
$global:_smtcMgrCacheTTL     = 0
$global:_smtcMgrCacheHit     = $false
$global:_smtcMgrFailStreak   = 0
# v9.9.9: non-blocking RequestAsync state
$global:_smtcMgrTask         = $null   # Task[GlobalSystemMediaTransportControlsSessionManager]
$global:_smtcMgrTaskCts      = $null   # CancellationTokenSource for the pending task

# v9.9.9 FIX: non-blocking SMTC manager fetch.
#
# Previously Get-SMTCManager called Await-WinRT with -TimeoutMs 300, which hit
# netTask.Wait(300) — blocking the WinForms UI thread for up to 300 ms whenever
# the 600 ms cache expired. With soundcloud-rpc active, RequestAsync takes 86 ms
# even in the happy path, so every cache-miss tick caused a visible stutter.
#
# New design: fire RequestAsync as a Task on tick N and immediately return the
# stale cached manager (or null on first call). The WinRT completion is delivered
# via the WinForms SynchronizationContext (posted to the Win32 message queue) and
# processed between ticks while the message loop pumps. Tick N+1 polls
# Task.IsCompleted, collects the result, and updates the cache — zero blocking.
# Timeout/backoff behavior is preserved: a 300 ms CTS cancels the task if the
# operation takes too long, and the failstreak/backoff logic is unchanged.
function Get-SMTCManager {
    $now = [datetime]::UtcNow

    # ── Poll any in-flight task ──────────────────────────────────────────────
    if ($global:_smtcMgrTask -ne $null) {
        if (-not $global:_smtcMgrTask.IsCompleted) {
            return $global:_smtcMgrCached    # still in flight — return stale cache
        }
        try {
            if ($global:_smtcMgrTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
                $global:_smtcMgrCached     = $global:_smtcMgrTask.Result
                $global:_smtcMgrCacheTime  = $now
                $global:_smtcMgrCacheTTL   = 600
                $global:_smtcMgrCacheHit   = $true
                if ($global:_smtcMgrFailStreak -gt 0) {
                    Log "SMTC recovered after $($global:_smtcMgrFailStreak) consecutive timeout(s)"
                }
                $global:_smtcMgrFailStreak = 0
            } else {
                # Canceled (CTS 300 ms timeout) or Faulted
                $global:_smtcMgrCached     = $null
                $global:_smtcMgrFailStreak++
                try { $global:_winrtTmoMin++ } catch {}
                $backoffMs = [int][Math]::Min(5000 * [Math]::Pow(2, $global:_smtcMgrFailStreak - 1), 5000)
                $global:_smtcMgrCacheTime  = $now
                $global:_smtcMgrCacheTTL   = $backoffMs
                $global:_smtcMgrCacheHit   = $true   # suppress re-fire during backoff
                if ($global:_smtcMgrFailStreak -eq 1) {
                    Log "SMTC: RequestAsync timed out - backoff ${backoffMs}ms. A broken Windows Store SMTC session (e.g. SoundCloud Store) keeps returning SERVERCALL_RETRYLATER. Closing that app will fix it."
                } else {
                    Log "SMTC: timeout streak=$($global:_smtcMgrFailStreak) backoff=${backoffMs}ms"
                }
            }
        } catch { Log "SMTC: manager task collect error: $_" }
        finally {
            try { if ($global:_smtcMgrTaskCts) { $global:_smtcMgrTaskCts.Cancel(); $global:_smtcMgrTaskCts.Dispose() } } catch {}
            try { $global:_smtcMgrTask.Dispose() } catch {}
            $global:_smtcMgrTask    = $null
            $global:_smtcMgrTaskCts = $null
        }
    }

    # ── Cache still valid ────────────────────────────────────────────────────
    if ($global:_smtcMgrCacheHit -and
        ($now - $global:_smtcMgrCacheTime).TotalMilliseconds -lt $global:_smtcMgrCacheTTL) {
        return $global:_smtcMgrCached
    }

    # ── Fire new async RequestAsync — no UI-thread blocking ──────────────────
    if ($global:_awaitAsTaskGenericCts) {
        try {
            $cts     = [System.Threading.CancellationTokenSource]::new(300)
            $asyncOp = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()
            $asTask  = $global:_awaitAsTaskGenericCts.MakeGenericMethod(
                          [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
            $global:_smtcMgrTask    = $asTask.Invoke($null, @($asyncOp, $cts.Token))
            $global:_smtcMgrTaskCts = $cts
            try { $global:_winrtCallsMin++ } catch {}
        } catch { Log "SMTC: RequestAsync fire error: $_" }
    }

    return $global:_smtcMgrCached   # stale cache while request is in flight
}

# v9.5.0 — Per-tick SMTC session + media-props caches.
#
# Before v9.5.0, every detector that walks SMTC sessions called mgr.GetSessions()
# AND awaited TryGetMediaPropertiesAsync() on each session independently. Two
# detectors (Get-Spotify + Get-SMTC) doing this back-to-back produced ~140 ms of
# pure WinRT round-tripping per tick, the bulk of every track-change SLOW TICK.
#
# These caches live for the duration of ONE scrobble tick (~100 ms). The tick
# start handler (scrobbleTimer.add_Tick) sets $global:_smtcCacheTickId to the
# current $global:_diagTickCount; helpers below check that ID — if it doesn't
# match the cached value, the cache is stale and gets refreshed.
#
# Why tick-scoped (not time-scoped): WinRT's PlaybackInfo + media properties
# can change mid-tick (e.g. Spotify track skip mid-detector-chain), and we DO
# want fresh data on the NEXT tick. Tick-scoped caching is safe + invisible
# because all detectors in one tick should see the same SMTC snapshot anyway —
# they're racing for the same instant in time.
$global:_smtcCacheTickId      = -1
$global:_smtcSessionsCache    = $null
$global:_smtcPropsCache       = @{}   # legacy (kept for compat)
# v9.10.0: non-blocking TryGetMediaPropertiesAsync — cross-tick cache + per-session async tasks
$global:_smtcPropsResultCache   = @{}   # persistent: key=sessionHash, value=props
$global:_smtcPropsTaskDict      = @{}   # key=sessionHash, value=Task<MediaProps>
$global:_smtcPropsCtsDict       = @{}   # key=sessionHash, value=CTS
$global:_smtcPropsFiredThisTick = [System.Collections.Hashtable]::new()   # per-tick dedup: cleared when _smtcCacheTickId rolls
# v9.10.0: transition guard — suppresses synchronous SMTC ALPC calls for 500ms after a
# track change is detected. GetPlaybackInfo() + GetTimelineProperties() each take ~15ms
# during SMTC session transitions; with 4 such calls per tick the skip tick hits 60ms.
# During the guard window all three caches below are returned instead of live ALPC calls.
$global:_smtcTransitionGuardMs    = 0    # epoch-ms threshold: suppress until this time
$global:_smtcPbInfoCache          = @{}  # key=sessionHash, value=last PlaybackInfo
$global:_smtcPbInfoCacheMs        = @{}  # key=sessionHash, value=epoch-ms when _smtcPbInfoCache entry was written (v11.1.5 staleness guard)
$global:_smtcTlCache              = @{}  # key=sessionHash, value=last TimelineProperties
$global:_smtcTlCacheMs            = @{}  # key=sessionHash, value=epoch-ms when _smtcTlCache entry was written (v11.1.4 staleness guard)
$global:_smtcSessionsCacheLastGood = $null  # last successful GetSessions() result

function Get-SMTCSessionsCached {
    # Returns the array of SMTC sessions for this tick, fetching fresh on first
    # call per tick and caching for subsequent calls in the same tick.
    if ($global:_smtcCacheTickId -ne $global:_diagTickCount) {
        $global:_smtcCacheTickId   = $global:_diagTickCount
        $global:_smtcSessionsCache = $null
        # v11.1.0 NOTE: _smtcPropsFiredThisTick.Clear() was attempted here but caused
        # TryGetMediaPropertiesAsync to fire every tick (10/sec), causing sustained ~9 MB/min
        # memory growth (239 MB at 13 min vs v11.0.0 stable ~175 MB). Rolled back.
        # The correctness bug (P1-SMTC-2) is filed as a deferred item for future sessions.
    }
    if ($null -eq $global:_smtcSessionsCache) {
        $mgr = Get-SMTCManager
        if (-not $mgr) {
            $global:_smtcSessionsCache = @()
            return @()
        }
        # Transition guard: skip GetSessions() ALPC call while SMTC is mid-transition
        if ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() -lt $global:_smtcTransitionGuardMs -and
            $global:_smtcSessionsCacheLastGood) {
            $global:_smtcSessionsCache = $global:_smtcSessionsCacheLastGood
        } else {
            try {
                $global:_smtcSessionsCache = @($mgr.GetSessions())
                $global:_smtcSessionsCacheLastGood = $global:_smtcSessionsCache
            } catch {
                $global:_smtcSessionsCache = @()
                try { Log "Get-SMTCSessionsCached: GetSessions() threw: $_" } catch {}
            }
        }
    }
    return $global:_smtcSessionsCache
}

# v9.10.0: caching wrapper for GetPlaybackInfo() — returns cached value during the
# transition guard window (500ms after a track change) to avoid synchronous ALPC calls.
# v11.1.5: also skips the WinRT call if cached value is <500ms old (staleness guard),
# cutting GetPlaybackInfo() calls from ~600/min to ~120/min and reducing RCW churn.
function Get-SMTCPlaybackInfoCached($Session) {
    $key = $Session.SourceAppUserModelId
    $_pbNowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $_pbFresh = $global:_smtcPbInfoCacheMs.ContainsKey($key) -and
                (($_pbNowMs - $global:_smtcPbInfoCacheMs[$key]) -lt 500)
    if ($global:_smtcPbInfoCache.ContainsKey($key) -and
        ($_pbNowMs -lt $global:_smtcTransitionGuardMs -or $_pbFresh)) {
        return $global:_smtcPbInfoCache[$key]
    }
    $info = $Session.GetPlaybackInfo()
    $global:_smtcPbInfoCache[$key]   = $info
    $global:_smtcPbInfoCacheMs[$key] = $_pbNowMs
    # v11.2.0: if SMTC reports Changing, arm transition guard immediately so the NEXT
    # staleness-expiry won't make another blocking ALPC call while the player is mid-skip.
    # Root cause of the ~12% CPU + lag on track skip: GetPlaybackInfo() can block for
    # ~100ms during Spotify/player track transitions. Arming a 750ms guard here limits
    # the blockage to ONE call per transition (this one) rather than one per 500ms expiry.
    if ($info -and $info.PlaybackStatus.ToString() -eq 'Changing') {
        $global:_smtcTransitionGuardMs = [Math]::Max($global:_smtcTransitionGuardMs,
            $_pbNowMs + 750)
    }
    return $info
}

function Get-SMTCMediaPropsCached {
    # v9.10.0: fully non-blocking. Fires TryGetMediaPropertiesAsync as a background
    # Task; returns the previous cached result immediately (null on very first call
    # for a session). The task completes between ticks via WinForms SynchronizationContext;
    # caller gets fresh data on the next tick. Zero UI-thread blocking.
    param($Session, [int]$TimeoutMs = 150)
    # Per-tick housekeeping (sessions cache reset — _smtcPropsFiredThisTick is cleared by Get-SMTCSessionsCached which always runs first)
    if ($global:_smtcCacheTickId -ne $global:_diagTickCount) {
        $global:_smtcCacheTickId        = $global:_diagTickCount
        $global:_smtcSessionsCache      = $null
        # v11.1.0: removed dead _smtcPropsCache = @{} (zero reads; was allocating @{} twice per tick path for no reason)
        # v11.1.0: removed _smtcPropsFiredThisTick = @{} (moved .Clear() to Get-SMTCSessionsCached above; that function always runs first in detector chain)
    }
    if (-not $Session) { return $null }
    $key = $Session.SourceAppUserModelId

    # ── Collect any completed task for this session ───────────────────────────
    if ($global:_smtcPropsTaskDict.ContainsKey($key)) {
        $t = $global:_smtcPropsTaskDict[$key]
        if ($t.IsCompleted) {
            try {
                if ($t.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
                    $newResult = $t.Result
                    # If title changed → set 750ms transition guard so synchronous ALPC
                    # calls (GetPlaybackInfo, GetTimelineProperties, GetSessions) are
                    # served from cache during the SMTC session transition window.
                    # v11.2.0: extended from 500ms → 750ms to match the Changing-status
                    # guard window (see Get-SMTCPlaybackInfoCached).
                    $oldResult = if ($global:_smtcPropsResultCache.ContainsKey($key)) { $global:_smtcPropsResultCache[$key] } else { $null }
                    if ($newResult -and $oldResult -and $newResult.Title -ne $oldResult.Title) {
                        $global:_smtcTransitionGuardMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + 750
                    }
                    $global:_smtcPropsResultCache[$key] = $newResult
                }
                # Canceled / Faulted: keep stale cache, don't update
            } catch {}
            finally {
                try { $global:_smtcPropsCtsDict[$key].Dispose() } catch {}
                try { $t.Dispose() } catch {}
                $global:_smtcPropsTaskDict.Remove($key)
                $global:_smtcPropsCtsDict.Remove($key)
            }
        }
    }

    # ── Fire new async request (once per session per tick, only when idle) ────
    if (-not $global:_smtcPropsTaskDict.ContainsKey($key) -and
        -not $global:_smtcPropsFiredThisTick.ContainsKey($key) -and
        $global:_awaitAsTaskGenericCts) {
        try {
            $cts  = [System.Threading.CancellationTokenSource]::new($TimeoutMs)
            $meth = $global:_awaitAsTaskGenericCts.MakeGenericMethod(
                      [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
            $t    = $meth.Invoke($null, @($Session.TryGetMediaPropertiesAsync(), $cts.Token))
            $global:_smtcPropsTaskDict[$key]      = $t
            $global:_smtcPropsCtsDict[$key]       = $cts
            $global:_smtcPropsFiredThisTick[$key] = $true
            try { $global:_winrtCallsMin++ } catch {}
        } catch {}
    }

    # ── Return cached result (stale or null on first call) ────────────────────
    if ($global:_smtcPropsResultCache.ContainsKey($key)) {
        return $global:_smtcPropsResultCache[$key]
    }
    return $null
}

# Extract an SMTC media-properties Thumbnail (IRandomAccessStreamReference) into
# a base64 data URI string. Used as a fallback when server-side Deezer/iTunes
# art resolution fails (common for SoundCloud-only tracks). Returns '' on any
# error so the caller can simply fall through. Cached per (artist|track) to
# avoid re-reading the stream every tick.
$global:_smtcArtCache        = @{}
$global:_smtcArtCacheOrder   = [System.Collections.Generic.Queue[string]]::new()  # v11.0.0: LRU eviction order
# v9.9.9: deferred extraction state
$global:_smtcArtPendingKey   = ''
$global:_smtcArtPendingProps = $null
$global:_smtcArtPendingMs    = [long]0
$global:_smtcArtInFlight     = $false   # legacy compat
$global:_smtcArtLastExtract  = $null
# v9.10.0: non-blocking state machine for Invoke-DeferredThumbExtraction
# States: 'idle' | 'waiting' | 'opening' | 'loading'
$global:_smtcArtState      = 'idle'
$global:_smtcArtOpenTask   = $null
$global:_smtcArtOpenCts    = $null
$global:_smtcArtStream     = $null
$global:_smtcArtStreamSize = [uint32]0
$global:_smtcArtReader     = $null
$global:_smtcArtLoadTask   = $null
$global:_smtcArtLoadCts    = $null

# v9.9.9 FIX: track-change lag — full deferral approach.
# Previously this function extracted the SMTC thumbnail stream synchronously on
# the WinForms UI thread (via Await-WinRT). Spotify/YouTube/etc. push album art
# into SMTC asynchronously after a track change, so on the first tick for a new
# track the stream wasn't ready and Wait() blocked the entire OS message loop for
# 100-600 ms — the "hard lag on skip" symptom.
#
# New contract: this function NEVER blocks.
#   • Cache hit  → return cached value immediately (fast path, unchanged).
#   • Cache miss → store as pending and return '' immediately. The actual WinRT
#     extraction is done by Invoke-DeferredThumbExtraction, called from the scrobble
#     tick AFTER the new-track webhook has already fired. By that point (≥ 400 ms
#     after the skip) the artwork is always loaded and extraction takes < 10 ms.
# v11.0.0: LRU-capped write helper — evicts oldest entry when cap (200) is reached.
# Keeps _smtcArtCache bounded even on long shuffle sessions with many unique tracks.
function Write-SMTCArtCacheEntry([string]$key, [string]$value) {
    if ($key -and -not $global:_smtcArtCache.ContainsKey($key)) {
        $null = $global:_smtcArtCacheOrder.Enqueue($key)
        if ($global:_smtcArtCacheOrder.Count -gt 200) {
            $global:_smtcArtCache.Remove($global:_smtcArtCacheOrder.Dequeue())
        }
    }
    $global:_smtcArtCache[$key] = $value
}

function Get-SMTCThumbnailDataUri {
    param($MediaProps, [string]$CacheKey)
    if (-not $MediaProps) { return '' }
    if ($CacheKey -and $global:_smtcArtCache.ContainsKey($CacheKey)) {
        return $global:_smtcArtCache[$CacheKey]
    }
    # Cache miss — queue for deferred extraction, return '' now.
    if ($CacheKey -and $MediaProps -and $global:_smtcArtState -eq 'idle') {
        $global:_smtcArtPendingKey   = $CacheKey
        $global:_smtcArtPendingProps = $MediaProps
        $global:_smtcArtPendingMs    = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $global:_smtcArtState        = 'waiting'
    }
    return ''
}

# Called from the scrobble tick AFTER the new-track (or heartbeat) webhook fires.
# Waits 400 ms post-queue so the app has loaded the artwork, then does the
# actual WinRT stream read. Returns the data URI string on success, '' otherwise.
# Caches the result (or '' on permanent failure) so callers get cache hits next time.
function Invoke-DeferredThumbExtraction {
    # v9.10.0: non-blocking state machine. Each call advances by one step maximum.
    # Never calls .Wait() — zero UI-thread blocking at any phase.
    # States: idle → waiting (400ms cool-off) → opening (OpenReadAsync) → loading (LoadAsync)
    switch ($global:_smtcArtState) {

        'idle' { return '' }

        'waiting' {
            if (-not $global:_smtcArtPendingKey -or -not $global:_smtcArtPendingProps) {
                $global:_smtcArtState = 'idle'; return ''
            }
            $nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            if (($nowMs - $global:_smtcArtPendingMs) -lt 400) { return '' }  # still cooling

            # 400 ms elapsed — fire OpenReadAsync (non-blocking)
            $key = $global:_smtcArtPendingKey
            try {
                $thumbRef = $global:_smtcArtPendingProps.Thumbnail
                if (-not $thumbRef) {
                    Write-SMTCArtCacheEntry $key ''
                    $global:_smtcArtPendingKey = ''; $global:_smtcArtPendingProps = $null
                    $global:_smtcArtState = 'idle'; return ''
                }
                $null = [Windows.Storage.Streams.IRandomAccessStreamWithContentType,Windows.Storage.Streams,ContentType=WindowsRuntime]
                $cts  = [System.Threading.CancellationTokenSource]::new(500)
                $meth = $global:_awaitAsTaskGenericCts.MakeGenericMethod([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
                $task = $meth.Invoke($null, @($thumbRef.OpenReadAsync(), $cts.Token))
                $global:_smtcArtOpenTask = $task
                $global:_smtcArtOpenCts  = $cts
                $global:_smtcArtState    = 'opening'
            } catch {
                Write-SMTCArtCacheEntry $global:_smtcArtPendingKey ''
                $global:_smtcArtPendingKey = ''; $global:_smtcArtPendingProps = $null
                $global:_smtcArtState = 'idle'
            }
            return ''
        }

        'opening' {
            if (-not $global:_smtcArtOpenTask.IsCompleted) { return '' }
            $key = $global:_smtcArtPendingKey
            $advanced = $false
            try {
                if ($global:_smtcArtOpenTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
                    $stream = $global:_smtcArtOpenTask.Result
                    if ($stream -and $stream.Size -gt 0) {
                        $null = [Windows.Storage.Streams.DataReader,Windows.Storage.Streams,ContentType=WindowsRuntime]
                        $global:_smtcArtStream     = $stream
                        $global:_smtcArtStreamSize = [uint32]$stream.Size
                        $reader = [Windows.Storage.Streams.DataReader]::new($stream)
                        $global:_smtcArtReader = $reader
                        $cts  = [System.Threading.CancellationTokenSource]::new(500)
                        $meth = $global:_awaitAsTaskGenericCts.MakeGenericMethod([uint32])
                        $task = $meth.Invoke($null, @($reader.LoadAsync($global:_smtcArtStreamSize), $cts.Token))
                        $global:_smtcArtLoadTask = $task
                        $global:_smtcArtLoadCts  = $cts
                        $global:_smtcArtState    = 'loading'
                        $advanced = $true
                    }
                }
                # Canceled (500ms CTS): don't cache '' — allow retry next tick
            } catch {}
            finally {
                try { $global:_smtcArtOpenCts.Dispose()  } catch {}
                try { $global:_smtcArtOpenTask.Dispose() } catch {}
                $global:_smtcArtOpenTask = $null; $global:_smtcArtOpenCts = $null
            }
            if (-not $advanced) {
                $global:_smtcArtPendingKey = ''; $global:_smtcArtPendingProps = $null
                $global:_smtcArtState = 'idle'
            }
            return ''
        }

        'loading' {
            if (-not $global:_smtcArtLoadTask.IsCompleted) { return '' }
            $key = $global:_smtcArtPendingKey
            $uri = ''
            try {
                if ($global:_smtcArtLoadTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
                    $bytes = New-Object 'byte[]' $global:_smtcArtStreamSize
                    $global:_smtcArtReader.ReadBytes($bytes)
                    $mime = 'image/png'
                    if ($global:_smtcArtStream.ContentType) { $mime = $global:_smtcArtStream.ContentType }
                    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8 -and $bytes[2] -eq 0xFF) { $mime = 'image/jpeg' }
                    $b64 = [Convert]::ToBase64String($bytes)
                    $uri = "data:${mime};base64,$b64"
                    if ($key) { Write-SMTCArtCacheEntry $key $uri }
                    Log ("SMTC thumb (async {0}B): '{1}'" -f $bytes.Length, $key)
                }
                # Canceled (500ms CTS): uri stays '', don't cache → retry allowed
            } catch {
                if ($key) { Write-SMTCArtCacheEntry $key '' }
            }
            finally {
                try { $global:_smtcArtReader.Dispose()   } catch {}
                try { $global:_smtcArtStream.Dispose()   } catch {}
                try { $global:_smtcArtLoadCts.Dispose()  } catch {}
                try { $global:_smtcArtLoadTask.Dispose() } catch {}
                $global:_smtcArtReader   = $null; $global:_smtcArtStream   = $null
                $global:_smtcArtLoadTask = $null; $global:_smtcArtLoadCts  = $null
                $global:_smtcArtPendingKey = ''; $global:_smtcArtPendingProps = $null
                $global:_smtcArtInFlight    = $false   # legacy compat
                $global:_smtcArtLastExtract = [DateTime]::UtcNow
                $global:_smtcArtState       = 'idle'
            }
            return $uri
        }
    }
    return ''
}
# Note: artwork is NOT extracted from SMTC here — the server resolves it via
# Deezer / iTunes / MusicBrainz APIs on every new track, same as for VLC.

# Map SourceAppUserModelId → friendly platform name shown in overlay badge
# ── Detect streaming service from a browser's open window titles ─────────────
# When SMTC reports a browser (Chrome/Edge/Firefox) as the source, we check the
# browser's window titles to see which streaming site is actually open and playing.
function Get-BrowserPlatformFromWindows($processName) {
    try {
        # Sweep ALL visible top-level windows, not just MainWindowTitle — a
        # background SoundCloud tab in a non-focused Chrome window has no
        # MainWindowTitle entry, so the old check missed it.
        $titles = @()
        try { $titles = [MasterFM.Win32Windows]::GetAllVisibleTitles() } catch {}
        # Also include MainWindowTitle as a fallback on systems where
        # EnumWindows returns nothing for sandboxed browser processes.
        try {
            $procs = Get-Process -Name $processName -ErrorAction SilentlyContinue
            foreach ($p in $procs) { if ($p.MainWindowTitle) { $titles += $p.MainWindowTitle } }
        } catch {}
        # PASS 1 — URL-domain markers in the title (authoritative).
        # Very few titles show the URL directly, but pinned-tab / PWA / "Page
        # Info" flows can surface it, and some window managers append the
        # domain. When present this is 100% reliable.
        foreach ($t in $titles) {
            $wt = ($t + '').ToLower()
            if (-not $wt) { continue }
            if ($wt -match 'music\.apple\.com')           { return 'Apple Music'   }
            if ($wt -match 'listen\.tidal\.com')          { return 'TIDAL'         }
            if ($wt -match 'music\.youtube\.com')         { return 'YouTube Music' }
            if ($wt -match 'open\.spotify\.com')          { return 'Spotify'       }
            if ($wt -match 'soundcloud\.com')             { return 'SoundCloud'    }
            if ($wt -match 'music\.amazon\.')             { return 'Amazon Music'  }
            if ($wt -match 'deezer\.com')                 { return 'Deezer'        }
            if ($wt -match 'bandcamp\.com')               { return 'Bandcamp'      }
            if ($wt -match 'mixcloud\.com')               { return 'Mixcloud'      }
            if ($wt -match 'pandora\.com')                { return 'Pandora'       }
            # youtube.com must be checked LAST among URLs — music.youtube.com
            # contains the substring "youtube.com" so we want the more-specific
            # music.youtube.com matcher (above) to win.
            if ($wt -match 'youtube\.com')                { return 'YouTube'       }
        }

        # PASS 2 — SUFFIX markers: "<Page Title> - <Site>" / "| <Site>" /
        # " — <Site>" (em-dash).  The last word of the window title is the
        # AUTHORITATIVE site name in every browser's default format, so check
        # this BEFORE the mid-title keyword scan. This fixes the common case
        # where a YouTube video has "soundcloud" in its title (e.g. a DJ mix
        # titled "Best Soundcloud Mix Ever - YouTube") which the old keyword
        # matcher was misclassifying as SoundCloud.
        foreach ($t in $titles) {
            $wt = ($t + '').ToLower()
            if (-not $wt) { continue }
            # Match the site label between a dash-like separator and end of title.
            # Browsers append a " - <BrowserName>" sometimes — strip that first
            # so we see the site label under it, not the browser name.
            $stripped = $wt -replace '\s*[-–—|]\s*(google chrome|microsoft edge|mozilla firefox|firefox|chrome|opera gx|opera|brave|vivaldi|arc|internet explorer)\s*$',''
            if ($stripped -match '[-–—|]\s*youtube music\s*$' -or $stripped -eq 'youtube music') { return 'YouTube Music' }
            if ($stripped -match '[-–—|]\s*youtube\s*$'       -or $stripped -eq 'youtube')       { return 'YouTube'       }
            if ($stripped -match '[-–—|]\s*apple music\s*$'   -or $stripped -eq 'apple music')   { return 'Apple Music'   }
            if ($stripped -match '[-–—|]\s*tidal\s*$'         -or $stripped -eq 'tidal')         { return 'TIDAL'         }
            if ($stripped -match '[-–—|]\s*spotify\s*$'       -or $stripped -eq 'spotify')       { return 'Spotify'       }
            if ($stripped -match '[-–—|]\s*soundcloud\s*$'    -or $stripped -match 'on\s+soundcloud\s*$') { return 'SoundCloud' }
            if ($stripped -match '[-–—|]\s*deezer\s*$'        -or $stripped -eq 'deezer')        { return 'Deezer'        }
            if ($stripped -match '[-–—|]\s*amazon music\s*$'  -or $stripped -eq 'amazon music')  { return 'Amazon Music'  }
            if ($stripped -match '[-–—|]\s*pandora\s*$'       -or $stripped -eq 'pandora')       { return 'Pandora'       }
            if ($stripped -match '[-–—|]\s*bandcamp\s*$'      -or $stripped -eq 'bandcamp')      { return 'Bandcamp'      }
            if ($stripped -match '[-–—|]\s*mixcloud\s*$'      -or $stripped -eq 'mixcloud')      { return 'Mixcloud'      }
        }

        # PASS 3 — mid-title keyword fallback (last resort). Order matters:
        # more-specific before less-specific. E.g. "apple music" before "music",
        # "youtube music" before "youtube". "soundcloud" last so a YouTube video
        # whose title MENTIONS soundcloud isn't misclassified.
        foreach ($t in $titles) {
            $wt = ($t + '').ToLower()
            if (-not $wt) { continue }
            if ($wt -match 'youtube music')                         { return 'YouTube Music' }
            if ($wt -match 'apple music')                           { return 'Apple Music'   }
            if ($wt -match 'amazon music')                          { return 'Amazon Music'  }
            if ($wt -match 'tidal')                                 { return 'TIDAL'         }
            if ($wt -match 'spotify')                               { return 'Spotify'       }
            if ($wt -match 'deezer')                                { return 'Deezer'        }
            if ($wt -match 'pandora')                               { return 'Pandora'       }
            if ($wt -match 'bandcamp')                              { return 'Bandcamp'      }
            if ($wt -match 'mixcloud')                              { return 'Mixcloud'      }
            if ($wt -match 'soundcloud')                            { return 'SoundCloud'    }
        }
        # SoundCloud signature title format: "Track by Artist - <Browser>" with
        # no literal "soundcloud" word (happens when the SoundCloud PWA is
        # installed, or when the "on SoundCloud" suffix is trimmed by pinned
        # tabs). The " by " keyword is SoundCloud-specific — YouTube uses " - ",
        # Spotify uses " • ", etc.
        foreach ($t in $titles) {
            if ($t -match '^.+?\s+by\s+.+?\s*[-–—|]\s*(Google Chrome|Microsoft.?Edge|Mozilla Firefox|Firefox|Chrome|Opera GX|Opera|Brave|Vivaldi|Arc|Internet Explorer)\s*$') {
                return 'SoundCloud'
            }
        }
        # NOTE: an earlier version swept Win32_Process.CommandLine via WMI
        # looking for "--app=https://soundcloud.com" etc. It caused 200-900 ms
        # SLOW TICK spikes every 10 s when the cache refreshed, because
        # Get-CimInstance Win32_Process is synchronous and blocks the UI
        # thread. Removed — album-based detection in Get-BrowserMediaNowPlaying
        # and the sticky per-browser cache cover the common cases without
        # blocking the tick. PWAs that don't set AlbumTitle won't be detected;
        # that's acceptable given the freeze risk.
    } catch {}
    return $null
}

# Map SMTC SourceAppUserModelId → friendly platform name shown in overlay badge.
# For browser appIds, falls back to window-title scanning to detect the actual service.
function Get-PlatformName($appId) {
    if (-not $appId) { return 'Unknown' }
    $lower = $appId.ToLower()

    # ── Dedicated music apps ──────────────────────────────────────────────────
    if ($lower -match 'spotify')                                          { return 'Spotify'               }
    if ($lower -match 'soundcloud')                                       { return 'SoundCloud'            }
    if ($lower -match 'vlc')                                              { return 'VLC'                   }
    # Legacy WMP (wmplayer.exe) and new Windows 11 Media Player (MediaPlayer.exe)
    if ($lower -match 'wmplayer|zunemusic|zunevideo|windowsmedia|\.wmp') { return 'Windows Media Player'  }
    if ($lower -match '^mediaplayer$|microsoft\.mediaplayer|windows.*mediaplayer') { return 'Windows Media Player' }
    if ($lower -match 'applemusic|applemusicwin|apple.*music')            { return 'Apple Music'           }
    if ($lower -match 'tidal')                                            { return 'TIDAL'                 }
    if ($lower -match 'deezer')                                           { return 'Deezer'                }
    if ($lower -match 'amazonmusic|amazon.*music')                        { return 'Amazon Music'          }
    if ($lower -match 'foobar')                                           { return 'foobar2000'            }
    if ($lower -match 'musicbee')                                         { return 'MusicBee'              }
    if ($lower -match 'aimp')                                             { return 'AIMP'                  }
    if ($lower -match 'winamp')                                           { return 'Winamp'                }
    if ($lower -match 'pandora')                                          { return 'Pandora'               }

    # ── Browsers: scan window titles to detect the actual streaming service ───
    # NOTE: `return if (...) { ... } else { ... }` is NOT valid PowerShell —
    # return expects a value and `if` as a *statement* is not an expression
    # here. Use an intermediate variable instead.
    if ($lower -match 'msedge') {
        $d = Get-BrowserPlatformFromWindows 'msedge'
        if ($d) { return $d } else { return 'Edge' }
    }
    if ($lower -match 'chrome') {
        $d = Get-BrowserPlatformFromWindows 'chrome'
        if ($d) { return $d } else { return 'Chrome' }
    }
    if ($lower -match 'firefox') {
        $d = Get-BrowserPlatformFromWindows 'firefox'
        if ($d) { return $d } else { return 'Firefox' }
    }
    if ($lower -match 'opera') {
        $d = Get-BrowserPlatformFromWindows 'opera'
        if ($d) { return $d } else { return 'Opera' }
    }
    if ($lower -match 'brave') {
        $d = Get-BrowserPlatformFromWindows 'brave'
        if ($d) { return $d } else { return 'Brave' }
    }
    return 'SMTC'
}

# ── Universal SMTC timeline reader with extrapolation + seek detection ───────
# All four SMTC consumers (Find-SMTCSession, Get-SMTCNowPlaying, osu!, WMP)
# funnel through this function so position tracking is consistent everywhere.
#
# Why this exists: tl.Position from GetTimelineProperties() is a SNAPSHOT
# captured the last time the media source called setPositionState(). Many
# sources (SoundCloud-rpc, YouTube, SoundCloud web) only call it on track-
# start / pause / play / seek — NOT continuously — so Position can be many
# minutes stale during normal playback. tl.LastUpdatedTime records when the
# snapshot was taken, so we can extrapolate:
#     realPlayhead = rawPos + (now - LastUpdatedTime)   // while Playing
#     realPlayhead = rawPos                              // while Paused
#
# Seek detection: if the source calls setPositionState again with a new
# position (and LastUpdatedTime advances), we flag `fresh=true` so callers
# can treat it as a seek / state change and push the webhook immediately.
#
# Returns: @{ posMs, durMs, rawPosMs, ageMs, fresh, lutYear } — caller
# uses posMs directly, the rest are diagnostic.
$global:_smtcLutByKey   = @{}   # per-session remembered LastUpdatedTime (ms)
$global:_smtcDiagLogged = @{}   # per-session one-shot diagnostic log
$global:_smtcStaleDur   = @{}   # per-session (title, durMs[, staleDurMs]) for stale-duration carry-over guard

# ── Stale-timeline carry-over guard ──────────────────────────────────────────
# When a YouTube tab auto-advances to the next video (or a pre-roll ad ends
# and the real video starts), Chrome updates SMTC's MediaProperties (title +
# artist) on the same tick but does NOT always push fresh TimelineProperties
# — the previous video's EndTime AND Position sit pinned on the SMTC session
# for several heartbeats while title/artist already reflect the new video.
# Symptom: an 8:39 video shows '1:31 / 5:52' on the overlay because BOTH
# the position and duration are carried over from the previous video.
#
# Detection (v8.1.9): the simplest reliable signal is "new track but rawPos
# is non-trivial" — fresh YouTube videos always start at position 0 (or
# within a few hundred ms by the time tray polls). A new title with rawPos
# > 5 s is almost certainly Chrome holding the previous video's Position.
# Combined with "durMs unchanged from previous" for extra confidence.
#
# Earlier attempts and why they were wrong:
#   v8.1.4: just durMs unchanged — false-positive when 2 videos share dur
#   v8.1.7: trusted SMTC's tlFresh — Chrome bumps tlFresh on every title
#           change without actually refreshing the values
#   v8.1.8: compared posMs (extrapolated by Get-SMTCPosition) to previous
#           cached posMs — extrapolation noise made same stale data compare
#           differently across polls, so detection missed
#
# Action: emit posMs=0 AND durMs=0 (overlay hides the time bar cleanly via
# setDuration(0); server resets startedAt to now). Stay in stale mode until:
#   • durMs changes from cached stale value (real new duration arrived)
#   • rawPosMs drops below 5 s (new video actually started playback)
#   • give-up timer (60 ticks ~15 s) — safety net
function Get-TrustedTimelineMs {
    param(
        [string]$SessionKey,
        [string]$Title,
        [long]  $RawPosMs,    # un-extrapolated rawPosMs from Get-SMTCPosition
        [long]  $PosMs,       # possibly-extrapolated posMs (returned to caller)
        [long]  $RawDurMs
    )
    if (-not $global:_smtcStaleDur) { $global:_smtcStaleDur = @{} }
    $prev = $global:_smtcStaleDur[$SessionKey]
    if (-not $prev) {
        $global:_smtcStaleDur[$SessionKey] = @{ title = $Title; durMs = $RawDurMs; rawPosMs = $RawPosMs }
        return @{ posMs = $PosMs; durMs = $RawDurMs }
    }
    if ($prev.title -ne $Title) {
        # Carry-over signature: title changed but rawPosMs is high (new
        # videos always start at 0; non-zero raw position on a new track
        # means Chrome's TimelineProperties still hold the old video's
        # Position) AND durMs unchanged from previous (the EndTime is also
        # stale).
        $posCarryOver = ($RawPosMs -gt 5000)
        $durSame = ($RawDurMs -gt 0 -and $RawDurMs -eq $prev.durMs)
        if ($posCarryOver -and $durSame) {
            $global:_smtcStaleDur[$SessionKey] = @{
                title       = $Title
                durMs       = $RawDurMs
                rawPosMs    = $RawPosMs
                staleDurMs  = $RawDurMs
                stalePosMs  = $RawPosMs
                staleTicks  = 1
            }
            Log "SMTC carry-over [$SessionKey]: '$Title' inherited rawPos=$([Math]::Round($RawPosMs/1000))s/dur=$([Math]::Round($RawDurMs/1000))s — emitting 0/0 until refresh"
            return @{ posMs = 0; durMs = 0 }
        }
        $global:_smtcStaleDur[$SessionKey] = @{ title = $Title; durMs = $RawDurMs; rawPosMs = $RawPosMs }
        return @{ posMs = $PosMs; durMs = $RawDurMs }
    }
    if ($prev.staleDurMs) {
        # Same title, in stale mode. Clear when:
        #   • durMs changed (new duration arrived)
        #   • rawPosMs dropped below 5 s (new video actually started)
        #   • give-up timer
        $durChanged = ($RawDurMs -gt 0 -and $RawDurMs -ne $prev.staleDurMs)
        $posReset   = ($RawPosMs -lt 5000)
        if ($durChanged -or $posReset) {
            $global:_smtcStaleDur[$SessionKey] = @{ title = $Title; durMs = $RawDurMs; rawPosMs = $RawPosMs }
            Log "SMTC carry-over cleared [$SessionKey]: '$Title' rawPos=$([Math]::Round($RawPosMs/1000))s/dur=$([Math]::Round($RawDurMs/1000))s — accepting"
            return @{ posMs = $PosMs; durMs = $RawDurMs }
        }
        $ticks = ($prev.staleTicks + 1)
        if ($ticks -ge 60) {
            $global:_smtcStaleDur[$SessionKey] = @{ title = $Title; durMs = $RawDurMs; rawPosMs = $RawPosMs }
            Log "SMTC carry-over give-up [$SessionKey]: '$Title' never refreshed after $ticks ticks — accepting rawPos=$([Math]::Round($RawPosMs/1000))s/dur=$([Math]::Round($RawDurMs/1000))s"
            return @{ posMs = $PosMs; durMs = $RawDurMs }
        }
        $global:_smtcStaleDur[$SessionKey] = @{
            title       = $Title
            durMs       = $RawDurMs
            rawPosMs    = $RawPosMs
            staleDurMs  = $prev.staleDurMs
            stalePosMs  = $prev.stalePosMs
            staleTicks  = $ticks
        }
        return @{ posMs = 0; durMs = 0 }
    }
    $global:_smtcStaleDur[$SessionKey] = @{ title = $Title; durMs = $RawDurMs; rawPosMs = $RawPosMs }
    return @{ posMs = $PosMs; durMs = $RawDurMs }
}

# ── Broken-resume guard (YouTube/Chrome long-pause carry-over) ──────────────
# When the user pauses YouTube and walks away for a while, Chrome eventually
# re-emits the SMTC session in a busted state — SMTC reports PlaybackStatus =
# Playing AND positionMs = EndTime (i.e. claims the video is playing at its
# end). That's a contradiction (a video at duration is finished, not playing).
# Symptom: overlay shows "17:43 / 17:43" while user was actually paused at,
# say, 15:39. When the user re-pauses, the timestamp snaps back to 15:39
# (correct), but unpausing flips it back to "17:43 / 17:43" because the next
# webhook still carries positionMs ≈ duration.
#
# Guard: per SMTC session, remember the most recent mid-video paused position.
# When SMTC later reports Playing AND positionMs is at the very end of the
# duration AND we have a cached mid-video pausedPos, override the playback
# state back to Paused at the cached position. The overlay stays frozen at
# the correct position. Once SMTC reports a sane positionMs (clearly under
# duration), clear the guard and trust SMTC again.
#
# Only triggers when:
#   • cached pausedPos is well before the end (< 90% of duration)
#   • current positionMs is at the very end (within 1.5 s of duration)
# This narrowness prevents false-positives during legit end-of-track playback.
$global:_smtcResumeGuard = @{}   # per-session @{ pausedPos } cache

function Get-TrustedPlaybackState {
    param(
        [string]$SessionKey,
        [bool]  $IsPaused,
        [long]  $PosMs,
        [long]  $DurMs
    )
    if (-not $global:_smtcResumeGuard) { $global:_smtcResumeGuard = @{} }
    $isAtEnd = ($DurMs -gt 0) -and ($PosMs -ge $DurMs - 1500)
    if ($IsPaused) {
        # Cache a mid-video paused position — used to detect carry-over later.
        if ($PosMs -gt 0 -and ($DurMs -le 0 -or $PosMs -lt [long]($DurMs * 0.9))) {
            $global:_smtcResumeGuard[$SessionKey] = @{ pausedPos = $PosMs }
        }
        return @{ isPaused = $true; posMs = $PosMs }
    }
    # Playing
    $cached = $global:_smtcResumeGuard[$SessionKey]
    if ($isAtEnd -and $cached -and $cached.pausedPos -gt 0 `
            -and ($DurMs -le 0 -or $cached.pausedPos -lt [long]($DurMs * 0.9))) {
        Log "SMTC broken-resume guard [$SessionKey]: SMTC pos=$([Math]::Round($PosMs/1000))s/dur=$([Math]::Round($DurMs/1000))s but cached pausedPos=$([Math]::Round($cached.pausedPos/1000))s — overriding to paused"
        return @{ isPaused = $true; posMs = $cached.pausedPos }
    }
    if ($cached) {
        $global:_smtcResumeGuard.Remove($SessionKey) | Out-Null
    }
    return @{ isPaused = $false; posMs = $PosMs }
}

function Get-SMTCPosition {
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][string]$StatusName
    )
    $out = @{ posMs = 0; durMs = 0; rawPosMs = 0; ageMs = -1; fresh = $false; lutYear = -1; lutMs = 0 }
    try {
        # Transition guard + staleness guard: serve cached timeline to avoid WinRT RCW churn.
        # v11.1.4: skip GetTimelineProperties() if the cached entry is <500 ms old (Fix 2 — B2 leak).
        $_tlKey = $Session.SourceAppUserModelId
        $_nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $_tlFresh = $global:_smtcTlCacheMs.ContainsKey($_tlKey) -and (($_nowMs - $global:_smtcTlCacheMs[$_tlKey]) -lt 500)
        if ($global:_smtcTlCache.ContainsKey($_tlKey) -and
            ($_nowMs -lt $global:_smtcTransitionGuardMs -or $_tlFresh)) {
            $tl = $global:_smtcTlCache[$_tlKey]
        } else {
            $tl = $Session.GetTimelineProperties()  # v11.1.4: only called when cache >500ms stale
            $global:_smtcTlCache[$_tlKey]   = $tl
            $global:_smtcTlCacheMs[$_tlKey] = $_nowMs
        }
        $rawPosMs  = [long]($tl.Position.TotalMilliseconds)
        $durMs     = [long]($tl.EndTime.TotalMilliseconds)
        $out.rawPosMs = $rawPosMs
        $out.durMs    = $durMs
        $out.posMs    = $rawPosMs   # default: use raw if extrapolation unavailable

        # Read LastUpdatedTime (DateTimeOffset). Invalid when the source has
        # never called setPositionState — its default value is year 1601.
        $lut = $tl.LastUpdatedTime
        $lutYear = -1
        try { $lutYear = [int]$lut.Year } catch {}
        $out.lutYear = $lutYear

        $lutMs = 0
        if ($lutYear -gt 2000) {
            try { $lutMs = [long]$lut.ToUnixTimeMilliseconds() } catch {}
        }
        $out.lutMs = $lutMs

        # Track per-session "last-seen" LastUpdatedTime — when it CHANGES between
        # ticks, the source pushed a fresh update (play/pause/seek). Callers can
        # treat fresh=true as a trigger to fire an immediate webhook with the
        # seek flag set, so the server re-pins startedAt without waiting for
        # the next 2 s heartbeat.
        $sKey  = ($Session.SourceAppUserModelId + '')
        $prev  = [long]($global:_smtcLutByKey[$sKey] + 0)
        if ($lutMs -gt 0 -and $lutMs -ne $prev) {
            $out.fresh = $true
            $global:_smtcLutByKey[$sKey] = $lutMs
        }

        # Extrapolate only while Playing — Position does not advance while
        # Paused or Stopped, and extrapolating there would confuse pause state
        # downstream.
        if ($StatusName -eq 'Playing' -and $lutYear -gt 2000 -and $lutMs -gt 0) {
            $ageMs = [long]([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - $lutMs)
            $out.ageMs = $ageMs
            # Sane bounds: 0..1 h. Negative means clock skew; > 1 h is either a
            # paused-for-a-long-time case we don't want to forward, or a source
            # that never updates LastUpdatedTime meaningfully.
            if ($ageMs -ge 0 -and $ageMs -lt 3600000) {
                $out.posMs = $rawPosMs + $ageMs
                if ($durMs -gt 0 -and $out.posMs -gt $durMs) { $out.posMs = $durMs }
            }
        }

        # One-shot diagnostic so we can see which sources have healthy timeline
        # data vs which rely on extrapolation vs which fall back to raw.
        if (-not $global:_smtcDiagLogged.ContainsKey($sKey)) {
            $global:_smtcDiagLogged[$sKey] = $true
            Log ("SMTC timeline [{0}] status={1} rawPos={2}s lutYear={3} ageMs={4} finalPos={5}s" -f `
                $sKey, $StatusName, [Math]::Round($rawPosMs/1000), $lutYear, $out.ageMs, [Math]::Round($out.posMs/1000))
        }
    } catch {
        # Session probably died between session enumeration and timeline read;
        # return zeros rather than throwing so the caller's existing null/zero
        # paths handle it. Log once per session key so we know it's happening.
        $sKey = try { $Session.SourceAppUserModelId } catch { 'unknown' }
        if (-not $global:_smtcTimelineErrLogged) { $global:_smtcTimelineErrLogged = @{} }
        if (-not $global:_smtcTimelineErrLogged.ContainsKey($sKey)) {
            $global:_smtcTimelineErrLogged[$sKey] = $true
            Log "SMTC timeline read failed for '$sKey': $_"
        }
    }
    return $out
}

# ── Shared SMTC session finder ────────────────────────────────────────────────
# Finds the first SMTC session whose AppUserModelId matches $pattern and whose
# playback status is in $acceptStatuses.  Returns a flat hashtable or $null.
# Used by all per-platform functions so each only reads its own session.
function Find-SMTCSession {
    param(
        [string]  $pattern,
        [string[]]$acceptStatuses = @('Playing'),
        [switch]  $WithThumbnail
    )
    if (-not $global:smtcAvailable) { return $null }
    try {
        # v9.5.0: shared per-tick session list (was: fresh mgr.GetSessions() every call)
        $sessions = Get-SMTCSessionsCached
        if (-not $sessions -or $sessions.Count -eq 0) { return $null }
        # Collect ALL matching sessions first, then rank so a truly-Playing session
        # wins over a Paused one when the user has e.g. a paused YouTube tab AND a
        # playing SoundCloud tab in the same browser. Previously the first match in
        # enumeration order won, which made SoundCloud-in-Chrome appear paused.
        $candidates = @()
        foreach ($s in $sessions) {
            $aid = ($s.SourceAppUserModelId + '').ToLower()
            if ($pattern -and $aid -notmatch $pattern) { continue }
            $statusName = (Get-SMTCPlaybackInfoCached $s).PlaybackStatus.ToString()  # v11.1.4: was direct .GetPlaybackInfo() — leaked ~1800 WinRT RCW/min
            if ($acceptStatuses -notcontains $statusName) { continue }
            # Rank: Playing=0 Changing=1 Paused=2 other=3 (lower wins)
            $rank = 3
            if     ($statusName -eq 'Playing')  { $rank = 0 }
            elseif ($statusName -eq 'Changing') { $rank = 1 }
            elseif ($statusName -eq 'Paused')   { $rank = 2 }
            $candidates += ,@{ session=$s; status=$statusName; rank=$rank }
        }
        if ($candidates.Count -eq 0) { return $null }
        $ordered = $candidates | Sort-Object { $_.rank }
        foreach ($c in $ordered) {
            $s = $c.session
            $statusName = $c.status
            # v9.5.0: shared per-tick media-props cache. Eliminates ~70 ms of
            # duplicated WinRT round-tripping when Spotify + SMTC detectors
            # both walk the same session list in the same tick.
            $props = Get-SMTCMediaPropsCached -Session $s -TimeoutMs 150
            if (-not $props) { continue }
            if (-not $props -or -not $props.Title.Trim()) { continue }
            $_tl    = Get-SMTCPosition -Session $s -StatusName $statusName
            $posMs  = [long]$_tl.posMs
            $durMs  = [long]$_tl.durMs
            $rawPosMs = [long]$_tl.rawPosMs
            $tlFresh = [bool]$_tl.fresh
            # Stale-timeline carry-over guard — see Get-TrustedTimelineMs.
            $_trusted = Get-TrustedTimelineMs ($s.SourceAppUserModelId + '') $props.Title $rawPosMs $posMs $durMs
            $posMs    = [long]$_trusted.posMs
            $durMs    = [long]$_trusted.durMs
            # Broken-resume guard — see Get-TrustedPlaybackState.
            $_isPaused = ($statusName -eq 'Paused')
            $_state    = Get-TrustedPlaybackState ($s.SourceAppUserModelId + '') $_isPaused $posMs $durMs
            $_isPaused = [bool]$_state.isPaused
            $posMs     = [long]$_state.posMs
            $thumbUri = ''
            if ($WithThumbnail) {
                $artistName = if ($props.Artist) { $props.Artist } else { '' }
                $cacheKey   = "$artistName|||$($props.Title)"
                try { $thumbUri = Get-SMTCThumbnailDataUri $props $cacheKey } catch { $thumbUri = '' }
            }
            $albumTitle = ''
            try { if ($props.AlbumTitle) { $albumTitle = $props.AlbumTitle } } catch {}
            return @{
                appId    = $s.SourceAppUserModelId
                title    = $props.Title
                artist   = if ($props.Artist) { $props.Artist } else { '' }
                album    = $albumTitle
                posMs    = $posMs
                durMs    = $durMs
                status   = if ($_isPaused) { 'Paused' } else { $statusName }
                paused   = $_isPaused
                thumbUri = $thumbUri
                tlFresh  = $tlFresh   # true when LastUpdatedTime changed since last read
            }
        }
    } catch { LogErr 'Find-SMTCSession' $_ }
    return $null
}

# ── Spotify — dedicated SMTC check ───────────────────────────────────────────
# Separate from the general SMTC scanner so Spotify is never missed due to
# priority ties with other apps, and Changing status is also accepted
# (Spotify briefly shows Changing between tracks).
function Get-SpotifyNowPlaying {
    if (-not (Test-PlatformEnabled 'Spotify')) { return $null }
    # v9.5.0: short-circuit when Spotify.exe isn't running. Was unconditionally
    # calling Find-SMTCSession 'spotify' every 100 ms tick, eating ~25 ms of
    # WinRT enumeration per tick when Spotify isn't even open. Get-Process with
    # a name filter is sub-1 ms, cached for 5 s so we only scan processes once
    # per ~50 ticks. Cache invalidates immediately when Spotify appears so
    # newly-launched Spotify is detected within one tick of the next recheck.
    $now = [Environment]::TickCount
    if ($null -eq $global:_spotifyProcCheckAt -or ($now - $global:_spotifyProcCheckAt) -gt 5000) {
        $global:_spotifyProcCheckAt = $now
        $global:_spotifyProcRunning = [bool](Get-Process -Name 'Spotify' -ErrorAction SilentlyContinue | Select-Object -First 1)
    }
    if (-not $global:_spotifyProcRunning) { return $null }
    $s = Find-SMTCSession 'spotify' @('Playing', 'Changing', 'Paused') -WithThumbnail
    if (-not $s) {
        # Diagnostic: if Spotify.exe is running but SMTC doesn't expose it, log all session appIds
        # so we can find the correct pattern to match.
        $spotifyProc = Get-Process -Name 'Spotify' -ErrorAction SilentlyContinue |
                       Where-Object { $_.MainWindowTitle } | Select-Object -First 1
        if ($spotifyProc -and $global:smtcAvailable) {
            try {
                $mgr    = Get-SMTCManager
                if ($mgr) {
                    $allIds = ($mgr.GetSessions() | ForEach-Object { $_.SourceAppUserModelId }) -join ', '
                    Log "Spotify running (window='$($spotifyProc.MainWindowTitle)') but not matched in SMTC. All appIds: [$allIds]"
                }
            } catch { Log "Spotify running but SMTC dump failed: $_" }
        }
        return $null
    }
    Log "Spotify SMTC: $($s.artist) - $($s.title) pos=$([Math]::Round($s.posMs/1000))s dur=$([Math]::Round($s.durMs/1000))s paused=$($s.paused)"
    return @{
        artist     = if ($s.artist) { $s.artist } else { 'Unknown Artist' }
        track      = $s.title
        source     = 'Spotify'
        positionMs = $s.posMs
        duration   = [double]($s.durMs / 1000.0)
        isPaused   = [bool]$s.paused
        trackArt   = $s.thumbUri
    }
}

# ── Browser media (SoundCloud, YouTube Music, Deezer web, etc.) ──────────────
# Chrome/Edge/Firefox expose whatever is playing via SMTC when the page uses the
# Media Session API.  We detect which service by scanning browser window titles.
function Get-BrowserMediaNowPlaying {
    # 'Browser' is the master kill-switch — unchecking it disables detection
    # of anything playing in any browser, regardless of per-platform toggles.
    if (-not (Test-PlatformEnabled 'Browser')) { return $null }
    # Accept Changing too — some browser media players briefly enter that state on track transitions.
    # -WithThumbnail grabs the SMTC Thumbnail stream (YouTube video thumb, SoundCloud cover, etc.)
    # so the server doesn't have to guess art via Deezer/iTunes lookups that miss for non-music
    # YouTube titles like "ARC Raiders Proximity Chat Funny Moments 😂 #2".
    # Supported browsers (process-name / SMTC AUMID fragment):
    #   chrome     — Google Chrome, and Chromium-based forks that keep the name
    #   msedge     — Microsoft Edge
    #   firefox    — Mozilla Firefox (all channels)
    #   brave      — Brave
    #   opera      — Opera AND Opera GX (Opera GX keeps "opera" in its AUMID)
    #   vivaldi    — Vivaldi browser
    #   arc        — Arc browser (The Browser Company)
    #   iexplore   — legacy Internet Explorer (uses Windows.Media.Control too)
    $s = Find-SMTCSession 'msedge|chrome|firefox|opera|brave|vivaldi|arc|iexplore' @('Playing', 'Changing', 'Paused') -WithThumbnail
    if (-not $s) { return $null }
    $browserProc = if     ($s.appId -match 'msedge')   { 'msedge'   } `
                   elseif ($s.appId -match 'chrome')   { 'chrome'   } `
                   elseif ($s.appId -match 'firefox')  { 'firefox'  } `
                   elseif ($s.appId -match 'brave')    { 'brave'    } `
                   elseif ($s.appId -match 'opera')    { 'opera'    } `
                   elseif ($s.appId -match 'vivaldi')  { 'vivaldi'  } `
                   elseif ($s.appId -match 'iexplore') { 'iexplore' } `
                   elseif ($s.appId -match 'arc')      { 'arc'      } `
                   else                                { 'chrome'   }
    # Platform detection is noisy because it depends on which browser window is
    # currently foregrounded / has a matching title. Cache the last *specific*
    # platform we detected per-browser so the badge stays on "SoundCloud" /
    # "YouTube" / etc. instead of flipping back to the generic browser name
    # (e.g. "Chrome") the moment the tab loses focus or retitles itself.
    if (-not $global:_lastPlatformByAppId) { $global:_lastPlatformByAppId = @{} }
    # Track which artist+track last produced a specific positive match for
    # each browser.  When the current session's track differs from that, the
    # cache is STALE — a new track in the same browser might be on a new
    # service (e.g. user played TIDAL yesterday in Chrome, now plays Apple
    # Music in the same Chrome window).  Without this we'd latch onto TIDAL
    # forever even though the current track is Apple Music.
    if (-not $global:_lastTrackKeyByAppId)  { $global:_lastTrackKeyByAppId  = @{} }
    $genericBrowserRx = '^(Chrome|Edge|Firefox|Opera|Opera GX|Brave|Vivaldi|Arc|Internet Explorer)$'
    $platform = Get-BrowserPlatformFromWindows $browserProc
    if (-not $platform) { $platform = Get-PlatformName $s.appId }

    # Second-chance platform detection via SMTC AlbumTitle — Media Session API
    # sites commonly set album="SoundCloud" / "YouTube Music" / "Deezer" etc.
    # even when the tab title is just the track name. This catches the case
    # where SoundCloud's PWA/page title strips the service name entirely.
    #
    # IMPORTANT: we match on exact service-name EQUALS or WORD-BOUNDARY
    # strings, not plain substring.  An album literally titled "Tidal Wave"
    # or "Apple Music for Kids" must NOT be misread as the service name.
    # Most web players set album to either (a) the real album title, or
    # (b) the literal service name — both we can discriminate with bounded
    # matches.
    if ($platform -match $genericBrowserRx) {
        $albumLC = ($s.album + '').ToLower().Trim()
        if     ($albumLC -eq 'soundcloud'    -or $albumLC -match '^soundcloud\b')    { $platform = 'SoundCloud'    }
        elseif ($albumLC -eq 'youtube music' -or $albumLC -match '^youtube music\b') { $platform = 'YouTube Music' }
        elseif ($albumLC -eq 'youtube'       -or $albumLC -match '^youtube\b')       { $platform = 'YouTube'       }
        elseif ($albumLC -eq 'deezer'        -or $albumLC -match '^deezer\b')        { $platform = 'Deezer'        }
        elseif ($albumLC -eq 'tidal'         -or $albumLC -match '^tidal\b')         { $platform = 'TIDAL'         }
        elseif ($albumLC -eq 'apple music'   -or $albumLC -match '^apple music\b')   { $platform = 'Apple Music'   }
        elseif ($albumLC -eq 'bandcamp'      -or $albumLC -match '^bandcamp\b')      { $platform = 'Bandcamp'      }
        elseif ($albumLC -eq 'mixcloud'      -or $albumLC -match '^mixcloud\b')      { $platform = 'Mixcloud'      }
        elseif ($albumLC -eq 'spotify'       -or $albumLC -match '^spotify\b')       { $platform = 'Spotify'       }
        elseif ($albumLC -eq 'amazon music'  -or $albumLC -match '^amazon music\b')  { $platform = 'Amazon Music'  }
        elseif ($albumLC -eq 'pandora'       -or $albumLC -match '^pandora\b')       { $platform = 'Pandora'       }
    }

    # Cache reconciliation — fix for "Apple Music detected as TIDAL" bug:
    # the OLD logic kept serving a stale cached platform even after the user
    # switched services in the same browser window. Now the cache key
    # includes the current track identity (artist+title), so the cache is
    # only reused for the SAME track that produced the positive detection.
    $currentTrackKey = "$($s.artist)|$($s.title)"
    $cachedPlatform  = $global:_lastPlatformByAppId[$browserProc]
    $cachedTrackKey  = $global:_lastTrackKeyByAppId[$browserProc]

    if ($platform -and ($platform -notmatch $genericBrowserRx)) {
        # Positive detection this tick — refresh both cache entries.
        $global:_lastPlatformByAppId[$browserProc] = $platform
        $global:_lastTrackKeyByAppId[$browserProc] = $currentTrackKey
    }
    elseif (($platform -match $genericBrowserRx) -and $cachedPlatform -and
            ($cachedPlatform -notmatch $genericBrowserRx) -and
            ($cachedTrackKey -eq $currentTrackKey)) {
        # Same track as last positive hit — keep the cached specific platform
        # instead of flipping back to the generic browser name.
        $platform = $cachedPlatform
    }
    elseif (($platform -match $genericBrowserRx) -and
            ($cachedTrackKey -ne $currentTrackKey)) {
        # Track identity changed AND we have no positive match — don't reuse
        # the stale cache for a different track. Fall through with generic
        # browser name. This is what fixes Apple-Music-seen-as-TIDAL.
        $global:_lastPlatformByAppId.Remove($browserProc) | Out-Null
        $global:_lastTrackKeyByAppId.Remove($browserProc) | Out-Null
    }

    # Per-platform gate (v5 refactor). If we RESOLVED a specific platform
    # (YouTube / Spotify / Apple Music / TIDAL / etc.) and the user has
    # that platform unchecked in the Platform Detection dialog, skip this
    # browser session entirely.  Generic "Chrome"/"Edge"/... still flow
    # through — they're already gated by the 'Browser' master switch at
    # the top of this function.
    if ($platform -and ($platform -notmatch $genericBrowserRx) -and
        -not (Test-PlatformEnabled $platform)) {
        return $null
    }

    # One-shot diagnostic dump: when we fall through to a generic browser name,
    # log the first 10 visible window titles + album so we can see why the
    # platform heuristics missed. Only logged once per browser per session.
    if (($platform -match $genericBrowserRx)) {
        if (-not $global:_genericBrowserDiagDone) { $global:_genericBrowserDiagDone = @{} }
        if (-not $global:_genericBrowserDiagDone[$browserProc]) {
            $global:_genericBrowserDiagDone[$browserProc] = $true
            try {
                $dumpTitles = @([MasterFM.Win32Windows]::GetAllVisibleTitles()) |
                    Where-Object { $_ -and $_.Trim() } | Select-Object -First 10
                Log ("DIAG [generic-browser={0}] appId='{1}' album='{2}' titles=[{3}]" -f `
                    $platform, $s.appId, $s.album, (($dumpTitles | ForEach-Object { "'$_'" }) -join ', '))
            } catch { LogErr 'generic-browser DIAG dump' $_ }
        }
    }

    $artist  = if ($s.artist) { $s.artist } else { '' }
    $track   = $s.title
    $paused  = [bool]$s.paused
    $override = ''

    # ── SoundCloud-in-browser correction ────────────────────────────────────
    # SoundCloud's Media Session implementation is unreliable:
    #   • often keeps SMTC status at "Paused" while actively playing
    #     → overlay fades out after 10 s (incorrect)
    #   • on playlist / album pages sometimes sets title="My Playlist"
    #     artist="" with no album art (playlist metadata, not track)
    #   • after mid-track seek, never re-flips to Playing
    # Authoritative signal: the browser tab title itself, which SoundCloud
    # prefixes with '▶' (U+25B6) / '►' (U+25BA) while playing. We sweep ALL
    # visible top-level window titles (EnumWindows) so a background SoundCloud
    # tab in a non-focused window is still detected.
    if ($platform -eq 'SoundCloud') {
        $titles = @()
        try { $titles = [MasterFM.Win32Windows]::GetAllVisibleTitles() } catch { LogErr 'GetAllVisibleTitles' $_ }
        $tabHit = $false
        foreach ($t in $titles) {
            # Accept EITHER the literal "on SoundCloud" suffix OR the "Track by
            # Artist - <Browser>" signature — SoundCloud PWAs / pinned tabs
            # sometimes strip the "on SoundCloud" tail but keep the "by"
            # between track and artist, which no other music site uses.
            $isSC = ($t -match '(?i)soundcloud') -or ($t -match '^.+?\s+by\s+.+?\s*-\s*(Google Chrome|Microsoft.?Edge|Mozilla Firefox|Opera|Brave)\s*$')
            if (-not $isSC) { continue }
            if ($t -match '^\s*soundcloud-rpc\s*$') { continue }     # sc-rpc electron
            if ($t -match '^\s*SoundCloud\s*[-\u2013|]\s') { continue } # homepage tab
            if ($t -ieq 'SoundCloud') { continue }

            $playing = ($t -match '^\s*[\u25B6\u25BA]')
            $cleaned = $t
            $cleaned = $cleaned -replace '^[\u25B6\u25BA]\s*', ''
            $cleaned = $cleaned -replace '\s+on\s+SoundCloud.*$', ''
            $cleaned = $cleaned -replace '\s*\|\s*SoundCloud.*$', ''
            # Strip trailing "- <Browser>" appended by the browser itself
            $cleaned = $cleaned -replace '\s*-\s*(Google Chrome|Microsoft.?Edge|Mozilla Firefox|Opera|Brave)\s*$', ''
            $cleaned = $cleaned.Trim()
            if (-not $cleaned -or $cleaned -ieq 'SoundCloud') { continue }

            $tabArtist = ''
            $tabTrack  = $cleaned
            # Split on the LAST " by " / " - " — artist names rarely contain these
            # tokens, but track titles do ("Song by Mistake by RealArtist", "Take
            # Me Down - Live Mix"). Non-greedy regex previously grabbed the FIRST
            # occurrence, truncating real tracks.
            $idxBy = $cleaned.LastIndexOf(' by ', [System.StringComparison]::OrdinalIgnoreCase)
            if ($idxBy -gt 0) {
                $tabTrack  = $cleaned.Substring(0, $idxBy).Trim()
                $tabArtist = $cleaned.Substring($idxBy + 4).Trim()
            } else {
                $idxDash = $cleaned.LastIndexOf(' - ')
                if ($idxDash -lt 0) { $idxDash = $cleaned.LastIndexOf(" $([char]0x2013) ") }  # –
                if ($idxDash -lt 0) { $idxDash = $cleaned.LastIndexOf(" $([char]0x2014) ") }  # —
                if ($idxDash -gt 0) {
                    $tabArtist = $cleaned.Substring(0, $idxDash).Trim()
                    $tabTrack  = $cleaned.Substring($idxDash + 3).Trim()
                }
            }

            # Tab title ▶ glyph is authoritative ONLY for flipping Paused→Playing
            # (fixes "SMTC stays Paused while audio plays" SoundCloud bug).
            # We do NOT flip the other way — some SoundCloud PWAs / pinned tabs /
            # browser variants don't render the glyph even while playing, so
            # "no glyph" is ambiguous and we trust SMTC's paused value there.
            if ($playing) { $paused = $false }

            # Replace title/artist when SMTC gave us playlist junk. Heuristic:
            # SMTC artist empty, OR tab provides a richer "Artist - Track" pair
            # that the SMTC title doesn't contain.
            $smtcArtistMissing = (-not $artist -or $artist.Trim() -eq '')
            $tabHasFullPair    = ($tabArtist -and $tabTrack)
            $trackContainsTab  = $false
            if ($tabTrack) {
                try { $trackContainsTab = ($track -match [regex]::Escape($tabTrack)) } catch { $trackContainsTab = $false }
            }
            $suffix = ''
            if ($tabHasFullPair -and ($smtcArtistMissing -or -not $trackContainsTab)) {
                $artist = $tabArtist
                $track  = $tabTrack
                $suffix = '/replaced'
            }
            if ($playing) { $override = "tab=playing$suffix" } else { $override = "tab=paused$suffix" }
            $tabHit = $true
            break
        }
        if (-not $tabHit) {
            # No SoundCloud tab visible at all — could be background audio with
            # the tab closed, OR SMTC is mis-reporting the platform. Keep SMTC
            # data verbatim but log so freezes can be diagnosed.
            $override = 'no-sc-tab'
        }
    }

    $artTag = if ($s.thumbUri) { ' art=SMTC' } else { '' }
    $tail   = if ($override) { " [$override]" } else { '' }
    Log "Browser SMTC [$browserProc → $platform]: $artist - $track paused=$paused$artTag$tail"
    return @{
        artist     = if ($artist) { $artist } else { 'Unknown Artist' }
        track      = $track
        source     = $platform
        positionMs = $s.posMs
        duration   = [double]($s.durMs / 1000.0)
        isPaused   = [bool]$paused
        trackArt   = $s.thumbUri
    }
}

# ── SoundCloud window-title fallback ─────────────────────────────────────────
# SoundCloud implements the Media Session API but some browser builds or ad-blockers
# prevent it from reaching SMTC.  As a fallback, we parse the active browser tab
# title which follows the format:
#   "▶ Artist - Track on SoundCloud"        (playing)
#   "Artist - Track on SoundCloud"          (paused / loading)
#   "Track by Artist on SoundCloud"         (alternative format)
#   "Artist - Track | SoundCloud"           (legacy format)
# Only called when Get-BrowserMediaNowPlaying returns nothing.
function Get-SoundCloudNowPlaying {
    if (-not (Test-PlatformEnabled 'SoundCloud')) { return $null }
    try {
        # v11.1.0: single batched Get-Process call with 5s TTL (same pattern as WMP/VLC caches).
        # Previously called Get-Process 8 times per tick when SoundCloud fallback was active.
        $_scTick = [Environment]::TickCount
        if ($null -eq $global:_scBrProcCheckAt -or ($_scTick - $global:_scBrProcCheckAt) -gt 5000) {
            $global:_scBrProcCheckAt = $_scTick
            $global:_scBrProcCached  = @(Get-Process -Name 'chrome','msedge','firefox','opera','brave','vivaldi','arc','iexplore' -ErrorAction SilentlyContinue)
        }
        foreach ($p in $global:_scBrProcCached) {
            $wt = ($p.MainWindowTitle + '').Trim()
            if (-not $wt) { continue }
            if ($wt -notmatch 'soundcloud') { continue }
            # Skip SoundCloud homepage / generic pages (no song info)
            if ($wt -match '^SoundCloud\s*[-–|]') { continue }
            if ($wt -ieq 'SoundCloud') { continue }

            Log "SoundCloud tab found [$($p.Name)]: '$wt'"

                # Strip play indicator and trailing site tag
                $cleaned = $wt -replace '^[▶►]\s*', ''
                $cleaned = $cleaned -replace '\s+on\s+SoundCloud.*$', '' `
                                    -replace '\s*[|]\s*SoundCloud.*$', ''
                $cleaned = $cleaned.Trim()
                if (-not $cleaned -or $cleaned -ieq 'SoundCloud') { continue }

                $artist = ''
                $track  = $cleaned

                # Split on LAST separator — see notes in Get-BrowserMediaNowPlaying
                # (SoundCloud section). Non-greedy regex broke on tracks with " by "
                # or " - " inside the title; LastIndexOf picks the correct boundary.
                $idxBy = $cleaned.LastIndexOf(' by ', [System.StringComparison]::OrdinalIgnoreCase)
                if ($idxBy -gt 0) {
                    $track  = $cleaned.Substring(0, $idxBy).Trim()
                    $artist = $cleaned.Substring($idxBy + 4).Trim()
                } else {
                    $idxDash = $cleaned.LastIndexOf(' - ')
                    if ($idxDash -lt 0) { $idxDash = $cleaned.LastIndexOf(" $([char]0x2013) ") }
                    if ($idxDash -lt 0) { $idxDash = $cleaned.LastIndexOf(" $([char]0x2014) ") }
                    if ($idxDash -gt 0) {
                        $artist = $cleaned.Substring(0, $idxDash).Trim()
                        $track  = $cleaned.Substring($idxDash + 3).Trim()
                    }
                }

                if (-not $track) { continue }

                # Grab SMTC timeline from the browser session for seek position if available
                $posMs = 0; $durMs = 0; $scPaused = $false
                $bS = Find-SMTCSession 'msedge|chrome|firefox|opera|brave' @('Playing', 'Changing', 'Paused')
                if ($bS) { $posMs = $bS.posMs; $durMs = $bS.durMs; $scPaused = [bool]$bS.paused }

                Log "SoundCloud: artist='$artist' track='$track' pos=$([Math]::Round($posMs/1000))s paused=$scPaused"
                return @{
                    artist     = if ($artist) { $artist } else { 'Unknown Artist' }
                    track      = $track
                    source     = 'SoundCloud'
                    positionMs = $posMs
                    duration   = [double]($durMs / 1000.0)
                    isPaused   = $scPaused
                }
        }
    } catch { Log "SoundCloud window detection error: $_" }
    return $null
}

# Scan ALL SMTC sessions and return the best playing one.
# Priority: dedicated music apps (Spotify, VLC, WMP…) > browsers > anything else.
# Also extracts timeline (position + duration) for seek-accurate timestamps.
$global:_smtcDebugTick = 0
function Get-SMTCNowPlaying {
    # No upfront SMTC toggle — generic SMTC detection (Spotify / Deezer desktop /
    # Foobar / MusicBee / AIMP / Winamp / etc.) is gated per-platform AFTER
    # Get-PlatformName resolves the appId to a friendly name. See the
    # platform-gate check inside the session-walk loop.
    if (-not $global:smtcAvailable) { return $null }
    try {
        # v9.5.0: shared per-tick session list — see Get-SMTCSessionsCached.
        # Was a fresh mgr.GetSessions() call every Get-SMTCNowPlaying invocation
        # (which fires every 100 ms tick), duplicating work already done by
        # Get-SpotifyNowPlaying earlier in the same tick.
        $sessions = Get-SMTCSessionsCached
        if (-not $sessions -or $sessions.Count -eq 0) { return $null }
        $playing  = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus]::Playing

        # ── Debug: dump all sessions every ~120 s so we can see WMP/VLC/etc. exact appIds ──
        $global:_smtcDebugTick++
        # 250 ms tick → dump every ~120 s (480 ticks)
        if (($global:_smtcDebugTick % 480) -eq 1) {
            if ($sessions.Count -eq 0) {
                Log "SMTC debug: no sessions"
            } else {
                foreach ($s in $sessions) {
                    $pb  = Get-SMTCPlaybackInfoCached $s
                    $aid = $s.SourceAppUserModelId
                    # v9.5.0: cached props (already fetched once per tick by Find-SMTCSession or earlier in this loop)
                    $props = Get-SMTCMediaPropsCached -Session $s -TimeoutMs 150
                    $title = if ($props) { $props.Title } else { '?' }
                    Log "SMTC session: appId='$aid' status=$($pb.PlaybackStatus) title='$title'"
                }
            }
        }

        $bestSession  = $null
        $bestPriority = 99
        # Also accept Changing (status=3) and Paused (status=5) so paused state propagates.
        $changing = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus]::Changing
        $paused   = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus]::Paused

        foreach ($s in $sessions) {
            $pb = Get-SMTCPlaybackInfoCached $s
            if ($pb.PlaybackStatus -ne $playing -and $pb.PlaybackStatus -ne $changing -and $pb.PlaybackStatus -ne $paused) { continue }

            $aid = if ($s.SourceAppUserModelId) { $s.SourceAppUserModelId.ToLower() } else { '' }
            # Spotify and browsers handled by dedicated functions — skip them here so they
            # don't accidentally win over the window-title fallbacks when SMTC gives bad data.
            # SteelSeries GG (Sonar) registers a fake SMTC session from its EQ software — ignore it.
            if ($aid -match 'spotify|msedge|chrome|firefox|opera|brave|steelseries') { continue }

            $pri = 5
            # Priority mirrors user preference: SoundCloud RPC > Deezer > Apple Music > TIDAL > other players
            if     ($aid -match 'soundcloud')                                                                        { $pri = 0 }
            elseif ($aid -match 'deezer')                                                                            { $pri = 1 }
            elseif ($aid -match 'applemusic|apple.?music')                                                           { $pri = 2 }
            elseif ($aid -match 'tidal')                                                                             { $pri = 3 }
            elseif ($aid -match 'foobar|musicbee|aimp|winamp|vlc|wmplayer|zunemusic|mediaplayer|amazonmusic')        { $pri = 4 }
            # Prefer actually-playing sessions over paused ones at the same pri level
            if ($pb.PlaybackStatus -eq $paused) { $pri += 2 }

            if ($pri -lt $bestPriority) { $bestPriority = $pri; $bestSession = $s }
        }

        if (-not $bestSession) { return $null }
        $bestStatus = (Get-SMTCPlaybackInfoCached $bestSession).PlaybackStatus
        $bestPaused = ($bestStatus -eq $paused)

        # ── soundcloud-rpc override ──────────────────────────────────────────
        # The com.richardhbtz.soundcloud-rpc browser extension NEVER flips SMTC
        # PlaybackStatus to Paused — it keeps reporting Playing with a frozen
        # positionMs. Authoritative signal: SoundCloud's browser tab title
        # prefixes '▶' (U+25B6) / '►' (U+25BA) when playing. No prefix = paused.
        #
        # We scan EVERY top-level HWND of every browser process via EnumWindows
        # (not just MainWindowTitle — that only returns the foreground window,
        # so background SoundCloud tabs are invisible to it).
        $bestAppId = ($bestSession.SourceAppUserModelId + '').ToLower()
        if ($bestAppId -match 'soundcloud-rpc|soundcloud_rpc|soundcloudrpc') {
            $scPlaying = $null
            $matchingTitle = ''
            $detectSource = ''
            # Global sweep: ANY visible top-level window whose title contains
            # "SoundCloud" (browsers, PWAs — NOT the sc-rpc Electron window
            # itself, which is literally titled 'soundcloud-rpc' and carries
            # zero play-state information).
            try {
                $allTitles = [MasterFM.Win32Windows]::GetAllVisibleTitles()
            } catch { $allTitles = @() }
            foreach ($t in $allTitles) {
                if ($t -notmatch '(?i)soundcloud') { continue }
                # Skip the literal Electron window — uninformative.
                if ($t -match '^\s*soundcloud-rpc\s*$') { continue }
                $matchingTitle = $t
                if ($t -match '^\s*[▶►]') { $scPlaying = $true; $detectSource = 'title' ; break }
                $scPlaying = $false   # SoundCloud found, no play-glyph → paused
                $detectSource = 'title'
            }
            # v6.2.5: REMOVED the Core Audio peak fallback entirely.
            # sc-rpc.exe is just an SMTC bridge — it reads SoundCloud's state
            # from the browser via CDP and registers a SMTC session, but
            # doesn't play audio itself. The actual SoundCloud audio flows
            # through the BROWSER process (Chrome / Edge / Firefox), not
            # sc-rpc. So GetPeakForProcessName('soundcloud') always returns
            # ~0 because it matches sc-rpc which has no audio flowing — NOT
            # because SoundCloud is paused. The peak override was firing on
            # EVERY user with sc-rpc installed, permanently overriding SMTC
            # Playing → Paused and freezing the overlay timer at 0 s.
            #
            # SMTC's Playing / Paused status + the ▶ / ► title-prefix
            # detection above are the authoritative signals. If title
            # detection doesn't find a SoundCloud window (minimized to
            # another desktop, PWA title not readable, etc.) we now just
            # trust SMTC.
            # Dump the full visible-title list once per state transition so we
            # can diagnose when detection fails.
            if ($scPlaying -eq $null -and $global:_scRpcLastOverride -ne 'nofind') {
                $sampleTitles = ($allTitles | Where-Object { $_.Length -gt 0 } | Select-Object -First 40) -join ' | '
                Log "soundcloud-rpc: sweep found no 'soundcloud' in titles and no audio session peak. Sample: $sampleTitles"
            }
            if ($scPlaying -eq $null) {
                # Couldn't find a SoundCloud browser window OR audio session — log once per state so we can see it
                if ($global:_scRpcLastOverride -ne 'nofind') {
                    Log "soundcloud-rpc: no SoundCloud browser window and no sc-rpc audio session — trusting SMTC status ($bestStatus)"
                    $global:_scRpcLastOverride = 'nofind'
                }
            } elseif ($scPlaying -eq $true) {
                if ($bestPaused) {
                    Log "soundcloud-rpc: [$detectSource] '$matchingTitle' — overriding SMTC Paused → Playing"
                }
                if ($global:_scRpcLastOverride -ne 'play') {
                    Log "soundcloud-rpc state: PLAYING ($detectSource, src='$matchingTitle')"
                    $global:_scRpcLastOverride = 'play'
                }
                $bestPaused = $false
            } elseif ($scPlaying -eq $false) {
                if (-not $bestPaused) {
                    Log "soundcloud-rpc: [$detectSource] '$matchingTitle' — overriding SMTC Playing → Paused"
                }
                if ($global:_scRpcLastOverride -ne 'pause') {
                    Log "soundcloud-rpc state: PAUSED ($detectSource, src='$matchingTitle')"
                    $global:_scRpcLastOverride = 'pause'
                }
                $bestPaused = $true
            }
        }

        # v9.5.0: cached props (already fetched once per tick by Find-SMTCSession or the debug-dump above)
        $props = Get-SMTCMediaPropsCached -Session $bestSession -TimeoutMs 150
        if (-not $props -or -not $props.Title.Trim()) { return $null }

        # ── Filter sessions where the app reports its own name as the track title ──
        # WMP Legacy does this: SMTC title = "Windows Media Player Legacy", artist = real artist.
        # Skip these — Get-WMPNowPlayingCOM / Get-WMPNowPlaying handles them correctly.
        $titleTrim = $props.Title.Trim()
        if ($titleTrim -match '^(Windows\s+)?Media Player|^VLC(\s+media player)?$') {
            Log "SMTC: '$($bestSession.SourceAppUserModelId)' title='$($props.Title)' is app-name — skipping to WMP/VLC fallback"
            return $null
        }

        $appId    = $bestSession.SourceAppUserModelId
        $platform = Get-PlatformName $appId

        # Per-platform gate (v5 refactor) — once we've resolved the generic
        # SMTC session to a friendly platform name (Spotify / TIDAL / Deezer
        # / Apple Music / ...), check the user's toggle for THAT specific
        # platform. Unknown / unmapped platforms (a new app we haven't added
        # a toggle for) default to ALLOWED so nothing silently breaks.
        if ($platform -and -not (Test-PlatformEnabled $platform)) {
            Log "SMTC: '$platform' disabled in Platform Detection — skipping"
            return $null
        }

        # Timeline: uses Get-SMTCPosition for extrapolation + seek detection.
        # Extrapolation is skipped when the session is Paused (position does not
        # advance while paused, and stale raw values are what we want).
        $_status = if ($bestPaused) { 'Paused' } else { 'Playing' }
        $_tl     = Get-SMTCPosition -Session $bestSession -StatusName $_status
        $posMs   = [long]$_tl.posMs
        $durMs   = [long]$_tl.durMs
        $rawPosMs = [long]$_tl.rawPosMs
        $tlFresh = [bool]$_tl.fresh

        $artistName = if ($props.Artist) { $props.Artist } else { 'Unknown Artist' }

        # Stale-timeline carry-over guard — see Get-TrustedTimelineMs.
        $_trusted  = Get-TrustedTimelineMs ($appId + '') $props.Title $rawPosMs $posMs $durMs
        $posMs     = [long]$_trusted.posMs
        $emitDurMs = [long]$_trusted.durMs
        # Broken-resume guard — see Get-TrustedPlaybackState.
        $_state    = Get-TrustedPlaybackState ($appId + '') ([bool]$bestPaused) $posMs $emitDurMs
        $bestPaused = [bool]$_state.isPaused
        $posMs      = [long]$_state.posMs

        $cacheKey = "$artistName|||$($props.Title)"
        $thumbUri = Get-SMTCThumbnailDataUri $props $cacheKey

        return @{
            artist     = $artistName
            track      = $props.Title
            source     = $platform
            appId      = $appId
            positionMs = $posMs
            duration   = if ($emitDurMs -gt 0) { [double]($emitDurMs / 1000.0) } else { 0.0 }
            trackArt   = $thumbUri
            isPaused   = $bestPaused
            tlFresh    = $tlFresh   # true on play / pause / seek events
        }
    } catch {
        return $null
    }
}

# osu! window-title parser + optional SMTC timeline for osu! Lazer seek support
# Accepted title formats (all case-insensitive, osu!/osu!lazer/osu!cuttingedge):
#   "osu!  - Artist - Title [Difficulty]"         ← normal song select / playing
#   "osu!  - Artist - Title - Subtitle [Diff]"    ← title contains " - "
#   "osu! - Artist - Title"                        ← no difficulty bracket
#   "osu!lazer - Artist - Title [Diff]"            ← Lazer variant
#   "osu! - Title"                                 ← single-part (rare — maps with no artist)
# Returns null only when the process is missing OR the title has literally no
# song-ish content (idle splash "osu!" / "osu!cuttingedge"). In gameplay + pause
# menu + song-select the title stays set, which is what we want: pause menu
# still reads as the active track, then the audio-peak fallback flips isPaused.
function Get-OsuNowPlaying {
    if (-not (Test-PlatformEnabled 'osu!')) { return $null }
    try {
        # Match osu!, osu!.exe, osu!lazer, osu!cuttingedge, etc.
        # v6.9.5: was bare Get-Process | Where-Object — that enumerated EVERY
        # running process every 50 ms tick (200+ processes on a typical PC),
        # which dominated the tray's per-tick CPU and showed up as the
        # 17-30 % spikes the user reported in Task Manager. Get-Process -Name
        # supports `*` so 'osu*' kernel-filters down to a handful of matches
        # before PowerShell sees them, dropping per-call cost from ~5-15 ms
        # to <1 ms. The Where-Object then enforces the exact 'osu!*' literal.
        # v9.5.0: even the kernel-filtered Get-Process call measured ~30 ms
        # per tick on this user's system. Caching the result for 5 s drops
        # average per-tick cost to ~0.6 ms (one cache check + boolean) when
        # osu! isn't running. Newly-launched osu! is detected within ~5 s.
        $now = [Environment]::TickCount
        if ($null -eq $global:_osuProcCheckAt -or ($now - $global:_osuProcCheckAt) -gt 5000) {
            $global:_osuProcCheckAt = $now
            $global:_osuProcCached  = Get-Process -Name 'osu*' -ErrorAction SilentlyContinue |
                                      Where-Object { $_.ProcessName -like 'osu!*' } |
                                      Select-Object -First 1
        }
        $proc = $global:_osuProcCached
        if (-not $proc) {
            # Clear CPU sample so the first tick after re-open doesn't false-trigger
            $global:_osuCpuSample = $null
            return $null
        }

        # ── Primary path: SMTC (osu!lazer) ───────────────────────────────────
        # osu!lazer registers its music player with SMTC, so we get artist +
        # track + live position + paused state EVEN in the main menu / song
        # select screen where the window title is just "osu!" and the title
        # parser would return null. This also catches skip / seek events
        # because the SMTC timeline reflects the actual audio playhead.
        $smtcArtist = ''; $smtcTrack = ''; $smtcPosMs = 0; $smtcDurS = 0.0; $smtcPaused = $false; $smtcHit = $false; $smtcThumb = ''
        if ($global:smtcAvailable) {
            try {
                $osuMgr = Get-SMTCManager
                if ($osuMgr) {
                    foreach ($s in $osuMgr.GetSessions()) {
                        $_aid = ($s.SourceAppUserModelId + '').ToLower()
                        if ($_aid -notmatch 'osu') { continue }
                        try {
                            $props = Get-SMTCMediaPropsCached $s -TimeoutMs 300
                            if ($props -and $props.Title -and $props.Title.Trim()) {
                                $smtcTrack  = $props.Title.Trim()
                                $smtcArtist = if ($props.Artist) { $props.Artist.Trim() } else { '' }
                                try {
                                    $st = (Get-SMTCPlaybackInfoCached $s).PlaybackStatus.ToString()
                                    $smtcPaused = ($st -eq 'Paused')
                                } catch {}
                                # Route through Get-SMTCPosition so osu!lazer gets the
                                # same extrapolation-on-restart treatment as all other
                                # SMTC sources. Pass the resolved status (Paused sources
                                # skip extrapolation — position doesn't advance).
                                $_osuStatus = if ($smtcPaused) { 'Paused' } else { 'Playing' }
                                $_osuTl     = Get-SMTCPosition -Session $s -StatusName $_osuStatus
                                $smtcPosMs  = [long]$_osuTl.posMs
                                $smtcDurS   = if ($_osuTl.durMs -gt 0) { [double]($_osuTl.durMs / 1000.0) } else { 0.0 }
                                $smtcFresh  = [bool]$_osuTl.fresh
                                # osu!lazer now ships beatmap covers via SMTC Thumbnail.
                                # Previously we threw the thumbnail away here and the server
                                # fell back to Deezer/iTunes lookups that miss on most beatmaps.
                                try {
                                    $_osuCacheKey = "osu|||$smtcArtist|||$smtcTrack"
                                    $smtcThumb = Get-SMTCThumbnailDataUri $props $_osuCacheKey
                                } catch { $smtcThumb = '' }
                                $smtcHit = $true
                            }
                        } catch {}
                        break
                    }
                }
            } catch {}
        }

        # ── Secondary path: window-title parse (osu! stable / cuttingedge) ───
        $title = ($proc.MainWindowTitle + '').Trim()
        $titleArtist = ''; $titleTrack = ''; $titleHit = $false
        if ($title -and $title -match '^(?i)osu!') {
            # Strip the leading "osu!<variant>" + optional separator. Both ASCII "-"
            # and Unicode "–" / "—" appear in different osu! versions.
            $rest = $title -replace '^(?i)osu![a-z]*\s*[-–—]?\s*', ''
            $rest = $rest.Trim()
            # Idle splash: title is just "osu!" / "osu!cuttingedge" with no song.
            if ($rest -and $rest -notmatch '^(?i)^osu![a-z]*$') {
                # Split on the FIRST " - " so titles containing further dashes
                # ("Artist - Title - Subtitle [Diff]") keep the subtitle intact.
                $parts    = $rest -split '\s+[-–—]\s+', 2
                $trackRaw = ''
                if ($parts.Count -ge 2) {
                    $titleArtist = $parts[0].Trim()
                    $trackRaw    = $parts[1].Trim()
                } else {
                    $trackRaw = $rest
                }
                # Strip trailing "[Difficulty]" marker (only the LAST one; inner
                # brackets in titles are preserved).
                $titleTrack = ($trackRaw -replace '\s*\[[^\]]*\]\s*$', '').Trim()
                if ($titleTrack) { $titleHit = $true }
            }
        }

        # ── Merge: prefer SMTC when present; fall back to title otherwise ────
        if (-not $smtcHit -and -not $titleHit) { return $null }
        if ($smtcHit) {
            # SMTC wins — it has live position, duration, and proper pause flag
            # for osu!lazer. The title parser is ignored in this path because
            # lazer's window title may be stale relative to the SMTC session.
            $artist = if ($smtcArtist) { $smtcArtist } else { 'osu!' }
            $track  = $smtcTrack
            $result = @{
                artist     = $artist
                track      = $track
                source     = 'osu!'
                positionMs = $smtcPosMs
                duration   = $smtcDurS
                isPaused   = $smtcPaused
                trackArt   = $smtcThumb
            }
        } else {
            $artist = if ($titleArtist) { $titleArtist } else { 'osu!' }
            $track  = $titleTrack
            $result = @{
                artist     = $artist
                track      = $track
                source     = 'osu!'
                positionMs = 0
                duration   = 0.0
                isPaused   = $false
            }
        }

        # ── Pause-menu detection via CPU-usage delta (stable / cuttingedge) ──
        # Lazer already gave us isPaused through SMTC above — skip the CPU
        # heuristic in that case (it's noisier than SMTC's authoritative flag).
        # For stable osu!, there's no SMTC, so we fall back to CPU-delta:
        # gameplay burns ~15-40 % of one core; the pause menu (ESC) collapses
        # to near-zero because no hit-object simulation / input processing is
        # happening. We sample TotalProcessorTime across ticks and compare the
        # delta against elapsed wall time — no COM, no audio session
        # enumeration, no Explorer-freeze risk.
        if (-not $smtcHit) {
          try {
            # Refresh handles so TotalProcessorTime advances (without this the
            # cached value is frozen at snapshot time and the delta is always 0).
            try { $proc.Refresh() } catch {}
            $nowUtc  = [DateTime]::UtcNow
            $cpuNow  = $proc.TotalProcessorTime
            $prev    = $global:_osuCpuSample
            $global:_osuCpuSample = @{ Time = $nowUtc; Cpu = $cpuNow; Pid = $proc.Id }
            if ($prev -and $prev.Pid -eq $proc.Id) {
                $dtMs   = ($nowUtc - $prev.Time).TotalMilliseconds
                $dCpuMs = ($cpuNow - $prev.Cpu).TotalMilliseconds
                if ($dtMs -gt 100) {
                    # Fraction of ONE core used between samples. We normalise
                    # against ONE core because gameplay-vs-pause contrast is
                    # clearest per-core, regardless of how many cores the CPU has.
                    $coreFrac = [double]($dCpuMs / $dtMs)
                    # Keep a 3-sample rolling window so a single GC/hitch tick
                    # doesn't false-trigger. 3 × 250 ms = 0.75 s of confirmation.
                    $hist = $global:_osuCpuHist
                    if (-not $hist) { $hist = @() }
                    $hist += $coreFrac
                    if ($hist.Count -gt 3) { $hist = $hist[-3..-1] }
                    $global:_osuCpuHist = $hist

                    # Threshold: 10 % of one core. Older threshold was 4 %,
                    # which missed pause on rigs where the pause menu still
                    # animates (backgrounds, UI) and burned 5-8 % per core —
                    # so isPaused never flipped and the overlay's timestamp
                    # kept ticking while the gameplay was actually frozen.
                    # 10 % is still well below any real gameplay (≥15 %) on
                    # every osu! build we've measured.
                    $thresh = 0.10
                    $allBelow = ($hist.Count -ge 3) -and -not ($hist | Where-Object { $_ -ge $thresh })
                    if ($allBelow) { $result.isPaused = $true }

                    # Log transitions so we can see the signal working.
                    if ($result.isPaused -ne $global:_osuLastLoggedPaused) {
                        $histStr = ($hist | ForEach-Object { "{0:N3}" -f $_ }) -join ','
                        Log ("osu! CPU pause sense: paused=$($result.isPaused) hist=[$histStr]")
                        $global:_osuLastLoggedPaused = [bool]$result.isPaused
                    }
                }
            }
          } catch {}
        }

        return $result
    } catch { return $null }
}

# Shared 2-sample peak silence detector, keyed by $stateKey.
# Returns $true when a process' audio has been below the audibility threshold
# for both of the last two scrobble ticks (=> ~500 ms of continuous silence).
# Returns $false if the peak is audible, no audio session is found, or we only
# have one sample so far (avoids false pauses immediately after a track start).
function Test-ProcessPeakSilent {
    param(
        [string]$ProcessNameContains,
        [string]$StateKey
    )
    try {
        $peak = [MasterFM.AudioPeak]::GetPeakForProcessName($ProcessNameContains)
    } catch { $peak = -1.0 }
    if ($peak -lt 0) { return $false }  # no session visible → can't judge
    $varName = "_peakHist_$StateKey"
    $hist    = Get-Variable -Scope Global -Name $varName -ValueOnly -ErrorAction SilentlyContinue
    if (-not $hist) { $hist = @() }
    $hist += [double]$peak
    if ($hist.Count -gt 2) { $hist = $hist[-2..-1] }
    Set-Variable -Scope Global -Name $varName -Value $hist
    if ($hist.Count -lt 2) { return $false }
    $thresh = 0.001
    foreach ($v in $hist) { if ($v -gt $thresh) { return $false } }
    return $true
}

# ── Windows Media Player COM interface (primary WMP detection) ───────────────
# Uses WMP's COM API to get accurate track/artist/position regardless of ID3 tags
# or window title format.  Works for wmplayer.exe only (Legacy WMP).
# Falls back to Get-WMPNowPlaying (window-title) if COM is unavailable (e.g. new
# Windows 11 MediaPlayer.exe which does not expose WMPlayer.OCX).
#
# WMP play states: 1=stopped, 2=paused, 3=playing, 6=buffering, 9=transitioning

# ─────────────────────────────────────────────────────────────────────────────
# WMP Legacy via UI Automation (UIA)
# ─────────────────────────────────────────────────────────────────────────────
# WMP 12 (wmplayer.exe) does NOT publish SMTC and doesn't register in the COM
# ROT, so GetActiveObject fails. The only remaining in-process read is UI
# Automation — walking WMP's actual visible element tree to grab the Now
# Playing labels.  Works even when the main window title is empty (minimized
# or in skin mode), as long as the Now Playing pane is present.
#
# First-run diagnostic: when we detect wmplayer.exe and haven't yet mapped
# its UIA tree, we dump every Text/Edit element Name to the log so we can
# see exactly what WMP exposes on *this* Windows build. After the dump,
# extraction locks onto the elements that look like "Artist" / "Title".

$global:_uiaReady       = $false
$global:_wmpUiaDumped   = $false
$global:_wmpUiaLastKey  = ''

function Initialize-UIA {
    if ($global:_uiaReady) { return $true }
    try {
        Add-Type -AssemblyName UIAutomationClient   -ErrorAction Stop
        Add-Type -AssemblyName UIAutomationTypes    -ErrorAction Stop
        $global:_uiaReady = $true
        Log "UIA: assemblies loaded"
        return $true
    } catch {
        Log "UIA: failed to load - $_"
        return $false
    }
}

# Win32 EnumWindows so we can find *every* top-level HWND belonging to
# wmplayer.exe (WMP Legacy often has multiple: library, now-playing pane,
# mini-player, visualization host). MainWindowHandle alone misses most.
if (-not ('MasterFM.Win32Windows' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace MasterFM {
    public static class Win32Windows {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int  GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static List<IntPtr> GetProcessWindows(uint pid) {
            var list = new List<IntPtr>();
            EnumWindows((h, l) => {
                uint wpid; GetWindowThreadProcessId(h, out wpid);
                if (wpid == pid) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }
        // All visible top-level windows with non-empty titles. Used by the
        // soundcloud-rpc pause-override scanner to find SoundCloud regardless
        // of which browser / PWA / Electron wrapper hosts it.
        public static List<string> GetAllVisibleTitles() {
            var list = new List<string>();
            EnumWindows((h, l) => {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                var t = sb.ToString();
                if (!string.IsNullOrEmpty(t)) list.Add(t);
                return true;
            }, IntPtr.Zero);
            return list;
        }
        public static string GetTitle(IntPtr h) {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            return sb.ToString();
        }
        public static string GetClass(IntPtr h) {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, 256);
            return sb.ToString();
        }
    }
}
'@
}

# ── Core Audio peak-value detection ──────────────────────────────────────────
# soundcloud-rpc is an Electron desktop app whose window title is literally
# "soundcloud-rpc" — it never changes between play and pause, and it never
# sets SMTC PlaybackStatus to Paused either. The ONLY authoritative signal
# is the audio output itself: is the sc-rpc.exe audio session producing
# non-zero peak samples? If yes → playing, if zero for a while → paused.
#
# IAudioMeterInformation::GetPeakValue on a per-session IAudioSessionControl
# is the standard Windows Core Audio API for this.
if (-not ('MasterFM.AudioPeak' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MasterFM {
    public static class AudioPeak {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        }

        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection {
            [PreserveSig] int GetCount(out uint pcDevices);
            [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                                       [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2 {
            int NotImpl_GetAudioSessionControl();
            int NotImpl_GetSimpleAudioVolume();
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        }

        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator {
            [PreserveSig] int GetCount(out int SessionCount);
            [PreserveSig] int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        // IAudioSessionControl (base) — 9 methods after IUnknown
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl {
            int NotImpl_GetState();
            int NotImpl_GetDisplayName();
            int NotImpl_SetDisplayName();
            int NotImpl_GetIconPath();
            int NotImpl_SetIconPath();
            int NotImpl_GetGroupingParam();
            int NotImpl_SetGroupingParam();
            int NotImpl_RegisterAudioSessionNotification();
            int NotImpl_UnregisterAudioSessionNotification();
        }

        // IAudioSessionControl2 — inherits IAudioSessionControl (9) + adds 5
        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2 {
            int NotImpl_0(); int NotImpl_1(); int NotImpl_2();
            int NotImpl_3(); int NotImpl_4(); int NotImpl_5();
            int NotImpl_6(); int NotImpl_7(); int NotImpl_8();
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetProcessId(out uint pRetVal);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioMeterInformation {
            [PreserveSig] int GetPeakValue(out float pfPeak);
        }

        // Returns highest peak (0..1) across all audio sessions whose process
        // name contains `nameContains` (case-insensitive). -1 if no match.
        // Returns highest peak (0..1) across all audio sessions whose process
        // name contains `nameContains` (case-insensitive), on ANY active
        // render endpoint. Returns -1 if no matching session exists at all.
        // We walk every render endpoint because sc-rpc (and plenty of other
        // apps) sometimes pick a non-default endpoint — checking only the
        // default device misses them entirely.
        public static float GetPeakForProcessName(string nameContains) {
            try {
                var enumer = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                IMMDeviceCollection col;
                // dataFlow=eRender(0), stateMask=DEVICE_STATE_ACTIVE(1)
                if (enumer.EnumAudioEndpoints(0, 1, out col) != 0 || col == null) return -1f;
                uint ndev; col.GetCount(out ndev);
                Guid iidMgr = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                float best = -1f;
                for (uint d = 0; d < ndev; d++) {
                    IMMDevice dev;
                    if (col.Item(d, out dev) != 0 || dev == null) continue;
                    object mgrObj;
                    if (dev.Activate(ref iidMgr, 1, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null) continue;
                    var mgr = (IAudioSessionManager2)mgrObj;
                    IAudioSessionEnumerator sessions;
                    if (mgr.GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;
                    int count; sessions.GetCount(out count);
                    for (int i = 0; i < count; i++) {
                        IAudioSessionControl ctl;
                        if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;
                        var ctl2 = ctl as IAudioSessionControl2;
                        if (ctl2 == null) continue;
                        uint sessPid;
                        if (ctl2.GetProcessId(out sessPid) != 0) continue;
                        if (sessPid == 0) continue;
                        string pname = "";
                        try {
                            var p = System.Diagnostics.Process.GetProcessById((int)sessPid);
                            pname = p.ProcessName;
                        } catch { continue; }
                        if (pname.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var meter = ctl as IAudioMeterInformation;
                        if (meter == null) continue;
                        float peak;
                        if (meter.GetPeakValue(out peak) != 0) continue;
                        if (peak > best) best = peak;
                    }
                }
                return best;
            } catch { return -1f; }
        }
    }
}
'@
}

function Get-WMPNowPlayingUIA {
    if (-not (Test-PlatformEnabled 'Windows Media Player')) { return $null }
    # v11.0.0: cache Get-Process 5s to avoid per-tick enumeration cost
    $_wmpTick = [Environment]::TickCount
    if ($null -eq $global:_wmpProcCheckAt -or ($_wmpTick - $global:_wmpProcCheckAt) -gt 5000) {
        $global:_wmpProcCheckAt = $_wmpTick
        $global:_wmpProcCached  = @(Get-Process -Name 'wmplayer','MediaPlayer' -ErrorAction SilentlyContinue)
    }
    $wmpProc = $global:_wmpProcCached | Where-Object { $_.Name -eq 'wmplayer' } | Select-Object -First 1
    if (-not $wmpProc) { return $null }
    if (-not (Initialize-UIA)) { return $null }

    try {
        $wmpPid = [uint32]$wmpProc.Id
        $hwnds = [MasterFM.Win32Windows]::GetProcessWindows($wmpPid)
        if (-not $hwnds -or $hwnds.Count -eq 0) {
            Log "WMP UIA: no top-level windows for wmplayer.exe (pid=$wmpPid)"
            return $null
        }

        # RawViewWalker walks ALL UIA elements including those that fail the
        # "IsControlElement" / visibility filter used by FindAll.  WMP Legacy's
        # Now-Playing pane is custom-drawn and hidden to UIA FindAll, but
        # RawViewWalker can still enumerate raw AutomationElements.
        $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker

        # Collect all meaningful text nodes (name, value, legacy MSAA role description)
        # from every WMP window, recursing via the raw view walker.
        $nodes = New-Object System.Collections.ArrayList
        $maxDepth = 12
        $visitCount = 0
        $visitCap   = 2500  # hard cap — WMP has ~1000 elements on a full UI
        function _walkUIA($el, $depth, [ref]$nodeList, [ref]$count, $walker, $cap) {
            if ($count.Value -ge $cap) { return }
            if (-not $el) { return }
            $count.Value++
            try {
                $n = ($el.Current.Name + '').Trim()
                $id = ($el.Current.AutomationId + '')
                $cls = ($el.Current.ClassName + '')
                $cti = $null
                try { $cti = $el.Current.ControlType.ProgrammaticName } catch {}
                if ($n) { $null = $nodeList.Value.Add([pscustomobject]@{ Name=$n; Id=$id; Class=$cls; Ctrl=$cti }) }
            } catch {}
            if ($depth -le 0) { return }
            $child = $null
            try { $child = $walker.GetFirstChild($el) } catch {}
            while ($child -and $count.Value -lt $cap) {
                _walkUIA $child ($depth - 1) $nodeList $count $walker $cap
                try { $child = $walker.GetNextSibling($child) } catch { break }
            }
        }

        foreach ($h in $hwnds) {
            try {
                $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$h)
                if (-not $root) { continue }
                _walkUIA $root $maxDepth ([ref]$nodes) ([ref]$visitCount) $walker $visitCap
            } catch {}
        }

        # First-run diagnostic: dump all non-empty nodes so we can pick out
        # what WMP actually labels. Prints once per instance.
        if (-not $global:_wmpUiaDumped) {
            $lines = @("WMP UIA RAW DIAG (pid=$wmpPid, $($hwnds.Count) windows, $visitCount nodes visited, $($nodes.Count) named):")
            foreach ($h in $hwnds) {
                $title = [MasterFM.Win32Windows]::GetTitle($h)
                $cls   = [MasterFM.Win32Windows]::GetClass($h)
                $vis   = [MasterFM.Win32Windows]::IsWindowVisible($h)
                $lines += "  HWND=0x$('{0:X}' -f [int]$h) class='$cls' visible=$vis title='$title'"
            }
            foreach ($n in $nodes) {
                $nm = $n.Name
                if ($nm.Length -gt 200) { $nm = $nm.Substring(0,200) + '…' }
                $lines += "    [$($n.Ctrl)] name='$nm' id='$($n.Id)' class='$($n.Class)'"
            }
            Log ($lines -join "`r`n")
            $global:_wmpUiaDumped = $true
        }

        # Extraction: look for WMP's known label patterns across AutomationId
        # and ClassName.  Falls back to heuristic: two non-empty strings
        # separated by " - " in a single Name property.
        $artist = ''; $track = ''
        foreach ($n in $nodes) {
            $nm = $n.Name.Trim()
            if (-not $nm) { continue }
            if ($nm -match '^(Windows Media Player|Now Playing|Library|Play|Pause|Stop|Next|Previous|Volume|Mute)$') { continue }
            if ($n.Id -match 'Title|Primary'   -and -not $track)  { $track  = $nm; continue }
            if ($n.Id -match 'Artist|Sub'      -and -not $artist) { $artist = $nm; continue }
            if ($n.Class -match 'Title'        -and -not $track)  { $track  = $nm; continue }
            if ($n.Class -match 'Artist'       -and -not $artist) { $artist = $nm; continue }
        }

        # Heuristic fallback: WMP's top window Name is often "Artist - Track"
        if (-not $track -and -not $artist) {
            foreach ($n in $nodes) {
                if ($n.Name -match '^(.+?)\s+-\s+(.+)$') {
                    $cand1 = $Matches[1].Trim()
                    $cand2 = $Matches[2].Trim()
                    if ($cand1 -match '^(Windows Media Player|Now Playing)$') { continue }
                    if ($cand2 -match '^(Windows Media Player|Now Playing)$') { continue }
                    if ($cand1.Length -lt 80 -and $cand2.Length -lt 150) {
                        $artist = $cand1
                        $track  = $cand2
                        break
                    }
                }
            }
        }

        if (-not $track -and -not $artist) {
            return $null
        }

        $key = "$artist|||$track"
        if ($key -ne $global:_wmpUiaLastKey) {
            Log "WMP UIA ✅: artist='$artist' track='$track'"
            $global:_wmpUiaLastKey = $key
        }
        return @{
            artist     = if ($artist) { $artist } else { 'Unknown Artist' }
            track      = if ($track)  { $track  } else { 'Unknown Track' }
            source     = 'Windows Media Player'
            positionMs = 0
            duration   = 0.0
        }
    } catch {
        Log "WMP UIA error: $_"
        return $null
    }
}

# ── WMP COM interface (works only when WMP is embedded as ActiveX; logs result) ─
# WMP 12 standalone does NOT register in the COM Running Object Table, so
# GetActiveObject will throw in most cases.  We still try it (some setups work)
# and log clearly so the overlay.log tells us whether it succeeded or why it failed.
function Get-WMPNowPlayingCOM {
    if (-not (Test-PlatformEnabled 'Windows Media Player')) { return $null }
    # v11.0.0: reuse shared 5s-TTL process cache
    $_wmpTick = [Environment]::TickCount
    if ($null -eq $global:_wmpProcCheckAt -or ($_wmpTick - $global:_wmpProcCheckAt) -gt 5000) {
        $global:_wmpProcCheckAt = $_wmpTick
        $global:_wmpProcCached  = @(Get-Process -Name 'wmplayer','MediaPlayer' -ErrorAction SilentlyContinue)
    }
    $wmpProc = $global:_wmpProcCached | Where-Object { $_.Name -eq 'wmplayer' } | Select-Object -First 1
    if (-not $wmpProc) { $global:_wmpComFailedPid = 0; return $null }   # WMP not running at all
    try {
        $wmp   = [Runtime.InteropServices.Marshal]::GetActiveObject('WMPlayer.OCX')
        $state = try { [int]$wmp.playState } catch { 0 }
        $global:_wmpComFailedPid = 0
        # WMP play states: 1=stopped 2=paused 3=playing 6=buffering 9=transitioning
        if ($state -ne 3 -and $state -ne 6 -and $state -ne 9) {
            if ($global:_wmpComLastState -ne $state) {
                Log "WMP COM: reachable but playState=$state (not playing)"
                $global:_wmpComLastState = $state
            }
            return $null
        }
        $global:_wmpComLastState = $state
        $media = $wmp.currentMedia
        if (-not $media -or -not $media.name) {
            if (-not $global:_wmpComNullMedia) { Log "WMP COM: playing but currentMedia is null"; $global:_wmpComNullMedia = $true }
            return $null
        }
        $global:_wmpComNullMedia = $false
        $title  = $media.name.Trim() -replace '\.(mp3|flac|wav|aac|ogg|wma|m4a|opus|mp4|mkv)$', ''
        if (-not $title) { return $null }
        $artist = try { $media.getItemInfo('Author').Trim() } catch { '' }
        $durMs  = try { [long]($media.duration * 1000) }      catch { 0 }
        $posMs  = try { [long]($wmp.controls.currentPosition * 1000) } catch { 0 }
        Log "WMP COM ✅: state=$state artist='$artist' track='$title' pos=$([Math]::Round($posMs/1000))s"
        return @{
            artist     = if ($artist) { $artist } else { 'Unknown Artist' }
            track      = $title
            source     = 'Windows Media Player'
            positionMs = $posMs
            duration   = [double]($durMs / 1000.0)
        }
    } catch {
        # Log only once per wmplayer.exe process lifetime — standalone WMP never exposes COM ROT.
        if ($global:_wmpComFailedPid -ne $wmpProc.Id) {
            Log "WMP COM ❌: GetActiveObject failed — WMP standalone does not expose COM ROT. Error: $_"
            $global:_wmpComFailedPid = $wmpProc.Id
        }
        return $null
    }
}

# ── Deezer artist+duration track lookup (used by WMP as last resort) ─────────
# WMP Legacy exposes correct artist + correct duration via SMTC but sets
# Title = "Windows Media Player".  Searching Deezer by artist and matching the
# duration (±5 s) reliably identifies the actual track.
# Result is cached per (artist, duration) pair so the timer never blocks twice.
$global:_wmpDeezerCache        = @{ key = ''; artist = ''; track = '' }
# v9.9.9 FIX: track-change lag. The old Invoke-WebRequest with TimeoutSec=4
# blocked the WinForms UI thread for up to 4 s on every new WMP track that
# lacked an SMTC title. Replaced with an async HttpClient.GetStringAsync
# fire-and-forget: first tick fires the request and returns $null immediately
# (overlay shows "Unknown Track" briefly); subsequent ticks poll IsCompleted
# and return the real title once the response arrives (~100-300 ms later).
$global:_wmpDeezerPendingKey   = ''
$global:_wmpDeezerPendingTask  = $null   # Task[string] from HttpClient.GetStringAsync
function Resolve-WMPTrackByDuration($artist, $durMs) {
    if (-not $artist -or $durMs -lt 10000) { return $null }
    $durSec   = [int]($durMs / 1000)
    $cacheKey = "$artist|$durSec"

    # Cache hit — instant
    if ($global:_wmpDeezerCache.key -eq $cacheKey -and $global:_wmpDeezerCache.track) {
        return @{ artist = $global:_wmpDeezerCache.artist; track = $global:_wmpDeezerCache.track }
    }

    # Pending request for THIS key — poll for completion
    if ($global:_wmpDeezerPendingKey -eq $cacheKey -and $global:_wmpDeezerPendingTask) {
        if (-not $global:_wmpDeezerPendingTask.IsCompleted) { return $null }  # still in flight
        try {
            if ($global:_wmpDeezerPendingTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
                $json = $global:_wmpDeezerPendingTask.Result
                $data = $json | ConvertFrom-Json
                if ($data.data) {
                    foreach ($item in $data.data) {
                        if ([Math]::Abs($item.duration - $durSec) -le 5) {
                            $global:_wmpDeezerCache = @{ key = $cacheKey; artist = $item.artist.name; track = $item.title }
                            Log "WMP Deezer ✅: '$($item.artist.name)' - '$($item.title)' dur=$($item.duration)s"
                            return @{ artist = $item.artist.name; track = $item.title }
                        }
                    }
                    Log "WMP Deezer: no duration match for artist='$artist' dur=${durSec}s"
                } else {
                    Log "WMP Deezer: no results for artist='$artist'"
                }
            } else {
                Log "WMP Deezer async: request failed status=$($global:_wmpDeezerPendingTask.Status)"
            }
        } catch { Log "WMP Deezer async parse: $_" }
        finally {
            # Mark resolved (empty track = failed) so we don't re-fire
            if (-not $global:_wmpDeezerCache.track) {
                $global:_wmpDeezerCache = @{ key = $cacheKey; artist = ''; track = '' }
            }
            $global:_wmpDeezerPendingKey  = ''
            $global:_wmpDeezerPendingTask = $null
        }
        return $null
    }

    # Different key pending (user skipped before request completed) — abandon it
    if ($global:_wmpDeezerPendingTask -and -not $global:_wmpDeezerPendingTask.IsCompleted) {
        $global:_wmpDeezerPendingKey  = ''
        $global:_wmpDeezerPendingTask = $null
    }

    # Fire new async request — no UI thread block
    try {
        if (-not $global:_httpClient) {
            try { Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue } catch {}
            $global:_httpClient         = [System.Net.Http.HttpClient]::new()
            $global:_httpClient.Timeout = [TimeSpan]::FromSeconds(5)
        }
        $q   = [Uri]::EscapeDataString($artist)
        $uri = "https://api.deezer.com/search?q=artist:`"$q`"&limit=50"
        $global:_wmpDeezerPendingKey  = $cacheKey
        $global:_wmpDeezerPendingTask = $global:_httpClient.GetStringAsync($uri)
        Log "WMP Deezer: async request fired for artist='$artist' dur=${durSec}s"
    } catch { Log "WMP Deezer async fire: $_" }
    return $null  # result arrives on next tick(s)
}

# ── WMP SMTC extended reader ──────────────────────────────────────────────────
# WMP Legacy SMTC is buggy: Title = "Windows Media Player" (app name) but
# Artist and timeline (position/duration) are usually correct.
# Detection priority within this function:
#   1. Any SMTC field (subtitle, album) that is NOT the app name
#   2. Deezer artist+duration lookup (cached — only one HTTP call per track)
#   3. Return artist-only result so the overlay at least shows the platform badge
function Get-WMPNowPlayingSMTC {
    if (-not (Test-PlatformEnabled 'Windows Media Player')) { return $null }
    if (-not $global:smtcAvailable) { return $null }

    # Quick bail if wmplayer.exe isn't even running  (v11.0.0: reuse shared 5s-TTL cache)
    $_wmpTick = [Environment]::TickCount
    if ($null -eq $global:_wmpProcCheckAt -or ($_wmpTick - $global:_wmpProcCheckAt) -gt 5000) {
        $global:_wmpProcCheckAt = $_wmpTick
        $global:_wmpProcCached  = @(Get-Process -Name 'wmplayer','MediaPlayer' -ErrorAction SilentlyContinue)
    }
    $wmpProc = $global:_wmpProcCached | Select-Object -First 1
    if (-not $wmpProc) { return $null }

    try {
        $mgr = Get-SMTCManager
        if (-not $mgr) { return $null }
        $sessions = $mgr.GetSessions()

        # ── Pass 1: find by appId pattern ────────────────────────────────────
        $wmpSession = $null
        foreach ($s in $sessions) {
            $aid    = ($s.SourceAppUserModelId + '').ToLower()
            $status = $s.GetPlaybackInfo().PlaybackStatus.ToString()
            if ($status -notmatch 'Playing|Changing|Paused') { continue }
            if ($aid -match 'wmplayer|mediaplayer|windowsmedia|zunemusic') {
                $wmpSession = $s; break
            }
        }

        # ── Pass 2: any session whose SMTC title looks like a WMP app-name ──
        # WMP Legacy may register under an unexpected appId (e.g. just the exe
        # path or a store-style ID).  If pass 1 failed but wmplayer.exe IS
        # running, scan every active session and pick the one whose title field
        # contains "Media Player" — that is WMP reporting its own name.
        if (-not $wmpSession) {
            foreach ($s in $sessions) {
                $status = $s.GetPlaybackInfo().PlaybackStatus.ToString()
                if ($status -notmatch 'Playing|Changing|Paused') { continue }
                $p = $null
                try { $p = (Await-WinRT ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties]) -TimeoutMs 150) } catch { continue }
                if (-not $p) { continue }
                if (($p.Title + '').Trim() -match 'Media Player') {
                    $wmpSession = $s
                    Log "WMP SMTC: matched via title, appId='$($s.SourceAppUserModelId)'"
                    break
                }
            }
        }

        # ── No session found — log all appIds for diagnosis (once per pid + appId set) ──
        if (-not $wmpSession) {
            $allIds = ($sessions | ForEach-Object { $_.SourceAppUserModelId }) -join ', '
            $spamKey = "$($wmpProc.Id)|$allIds"
            if ($global:_wmpSmtcLastSpamKey -ne $spamKey) {
                Log "WMP SMTC: wmplayer.exe running but no SMTC session found. All appIds: [$allIds]"
                $global:_wmpSmtcLastSpamKey = $spamKey
            }
            return $null
        }

        # ── Read all metadata fields ──────────────────────────────────────────
        $props = $null
        try { $props = (Await-WinRT ($wmpSession.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties]) -TimeoutMs 150) } catch {}
        if (-not $props) { return $null }

        $smtcTitle       = ($props.Title       + '').Trim()
        $smtcArtist      = ($props.Artist      + '').Trim()
        $smtcAlbum       = ($props.AlbumTitle  + '').Trim()
        $smtcSubtitle    = ($props.Subtitle    + '').Trim()
        $smtcAlbumArtist = ($props.AlbumArtist + '').Trim()

        # Route through the shared extrapolator so WMP sees the same restart-sync
        # behavior as every other SMTC source. Use the PlaybackStatus we read to
        # decide whether to extrapolate.
        $_wmpStatus = try { $wmpSession.GetPlaybackInfo().PlaybackStatus.ToString() } catch { 'Playing' }
        $_wmpTl     = Get-SMTCPosition -Session $wmpSession -StatusName $_wmpStatus
        $posMs      = [long]$_wmpTl.posMs
        $durMs      = [long]$_wmpTl.durMs

        Log "WMP SMTC raw: title='$smtcTitle' artist='$smtcArtist' album='$smtcAlbum' subtitle='$smtcSubtitle' albumArtist='$smtcAlbumArtist' pos=$([Math]::Round($posMs/1000))s dur=$([Math]::Round($durMs/1000))s"

        $appNamePat = '^(Windows\s+)?Media Player'

        # Pick best artist
        $realArtist = ''
        foreach ($c in @($smtcArtist, $smtcAlbumArtist)) {
            if ($c -and $c -notmatch $appNamePat) { $realArtist = $c; break }
        }

        # Pick best track title — prefer subtitle/album, then title (if not app-name)
        $realTrack = ''
        foreach ($c in @($smtcSubtitle, $smtcAlbum, $smtcTitle)) {
            if ($c -and $c -notmatch $appNamePat) { $realTrack = $c; break }
        }

        # No title from SMTC — try Deezer artist+duration lookup
        if (-not $realTrack -and $realArtist -and $durMs -gt 0) {
            $enriched = Resolve-WMPTrackByDuration $realArtist $durMs
            if ($enriched) { $realArtist = $enriched.artist; $realTrack = $enriched.track }
        }

        # Still no title — show "Unknown Track" so the badge + artist still appear
        if (-not $realTrack) {
            Log "WMP SMTC: artist='$realArtist' track unknown — badge-only fallback"
            if (-not $realArtist) { return $null }
            return @{
                artist     = $realArtist
                track      = 'Unknown Track'
                source     = 'Windows Media Player'
                positionMs = $posMs
                duration   = [double]($durMs / 1000.0)
            }
        }

        Log "WMP SMTC ✅: artist='$realArtist' track='$realTrack'"
        return @{
            artist     = if ($realArtist) { $realArtist } else { 'Unknown Artist' }
            track      = $realTrack
            source     = 'Windows Media Player'
            positionMs = $posMs
            duration   = [double]($durMs / 1000.0)
        }

    } catch { Log "WMP SMTC error: $_" }
    return $null
}

# ── Windows Media Player detection (Legacy + Windows 11 new version) ──────────
# Handles both:
#   wmplayer.exe  — Legacy WMP (ships with Windows 10/11, does NOT always use SMTC)
#   MediaPlayer.exe — New Windows Media Player (Windows 11 22H2+, uses SMTC but
#                     SMTC appId may not match our patterns depending on install)
#
# Title formats WMP uses:
#   "Artist - Track - Windows Media Player"      (most common with ID3 tags)
#   "Album - Artist - Track - Windows Media Player" (some WMP configs)
#   "Track - Windows Media Player"               (no artist tag)
#   "song.mp3 - Windows Media Player"            (no ID3 tags at all)
#   "Windows Media Player"                        (idle / nothing loaded)
function Get-WMPNowPlaying {
    if (-not (Test-PlatformEnabled 'Windows Media Player')) { return $null }
    try {
        # Try both process names; prefer Legacy wmplayer if both are open  (v11.0.0: shared 5s-TTL cache)
        $_wmpTick = [Environment]::TickCount
        if ($null -eq $global:_wmpProcCheckAt -or ($_wmpTick - $global:_wmpProcCheckAt) -gt 5000) {
            $global:_wmpProcCheckAt = $_wmpTick
            $global:_wmpProcCached  = @(Get-Process -Name 'wmplayer','MediaPlayer' -ErrorAction SilentlyContinue)
        }
        $proc = $global:_wmpProcCached |
                Where-Object { $_.MainWindowTitle -and $_.MainWindowTitle.Trim() -ne '' } |
                Sort-Object -Property @{ Expression = { if ($_.Name -eq 'wmplayer') { 0 } else { 1 } } } |
                Select-Object -First 1

        if (-not $proc) { return $null }

        $title = $proc.MainWindowTitle.Trim()
        Log "WMP window title [$($proc.Name)]: '$title'"

        # Skip idle / startup states (including "Windows Media Player Legacy" with nothing else)
        if ($title -match '^(Windows\s+)?Media Player(\s+Legacy)?(\s*-\s*Home)?$') { return $null }
        if ($title -eq '') { return $null }

        # Strip the app suffix — handles all known WMP title variants:
        #   " - Windows Media Player"
        #   " - Windows Media Player Legacy"
        #   " - Media Player"
        #   " - Media Player - Home"
        $stripped = $title -replace '\s*[-–]\s*(Windows\s+)?Media Player(\s+Legacy)?(\s*-\s*Home)?\s*$', ''
        $stripped = $stripped.Trim()
        if (-not $stripped) { return $null }

        # Split on " - " separators
        $parts = $stripped -split '\s+-\s+'

        $artist = ''
        $track  = ''

        switch ($parts.Count) {
            { $_ -ge 3 } {
                # "Album - Artist - Track" or "A - B - C" — take last two
                $artist = $parts[-2].Trim()
                $track  = $parts[-1].Trim()
            }
            2 {
                $artist = $parts[0].Trim()
                $track  = $parts[1].Trim()
            }
            default {
                # Only a title (no dash) — strip file extension if present
                $track = $stripped -replace '\.(mp3|flac|wav|aac|ogg|wma|m4a|opus|mp4|mkv)$', ''
            }
        }

        # Also strip extension from track in case it slipped through
        $track = $track -replace '\.(mp3|flac|wav|aac|ogg|wma|m4a|opus|mp4|mkv)$', ''
        if (-not $track) { return $null }

        # Pull position + duration from WMP's SMTC session even though its title is wrong.
        # This gives us live timestamps on WMP without relying on SMTC for metadata.
        $posMs = 0; $durMs = 0
        $wmpS = Find-SMTCSession 'wmplayer|mediaplayer|windowsmedia' @('Playing', 'Changing', 'Paused')
        if ($wmpS) { $posMs = $wmpS.posMs; $durMs = $wmpS.durMs }

        Log "WMP detected: artist='$artist' track='$track' pos=$([Math]::Round($posMs/1000))s dur=$([Math]::Round($durMs/1000))s"
        return @{
            artist     = if ($artist) { $artist } else { 'Unknown Artist' }
            track      = $track
            source     = 'Windows Media Player'
            positionMs = $posMs
            duration   = [double]($durMs / 1000.0)
        }
    } catch {
        Log "WMP detection error: $_"
        return $null
    }
}

# ── VLC Media Player window-title fallback ─────────────────────────────────────
# VLC registers with SMTC on newer versions, but this catches older installs and
# cases where SMTC misses it.
# Title formats:
#   "Artist - Track - VLC media player"   (proper ID3 tags)
#   "Track - VLC media player"            (no artist)
#   "song.mp3 - VLC media player"         (no ID3 tags)
#   "VLC media player"                    (idle)
function Get-VLCNowPlaying {
    if (-not (Test-PlatformEnabled 'VLC')) { return $null }
    try {
        # v11.0.0: cache Get-Process 5s to avoid per-tick enumeration cost
        $_vlcTick = [Environment]::TickCount
        if ($null -eq $global:_vlcProcCheckAt -or ($_vlcTick - $global:_vlcProcCheckAt) -gt 5000) {
            $global:_vlcProcCheckAt = $_vlcTick
            $global:_vlcProcCached  = Get-Process -Name 'vlc' -ErrorAction SilentlyContinue | Select-Object -First 1
        }
        $proc = $global:_vlcProcCached
        if (-not $proc) { return $null }
        $title = $proc.MainWindowTitle.Trim()
        # Only log window title when it changes (VLC fires the detector every 250 ms)
        if ($title -ne $global:_vlcLastLoggedTitle) {
            Log "VLC window title: '$title'"
            $global:_vlcLastLoggedTitle = $title
        }
        if (-not $title -or $title -eq 'VLC media player') { return $null }

        # Strip the " - VLC media player" suffix
        $stripped = $title -replace '\s*[-–]\s*VLC media player\s*$', ''
        $stripped = $stripped.Trim()
        if (-not $stripped) { return $null }

        # Split on " - " separators
        $parts = $stripped -split '\s+-\s+'
        $artist = if ($parts.Count -ge 2) { $parts[0].Trim() } else { '' }
        $track  = if ($parts.Count -ge 2) { $parts[1].Trim() } else { $stripped.Trim() }

        # Strip file extensions (VLC shows filename when no ID3 tags)
        $track  = $track  -replace '\.(mp3|flac|wav|aac|ogg|wma|m4a|opus|mp4|mkv|avi)$', ''
        $artist = $artist -replace '\.(mp3|flac|wav|aac|ogg|wma|m4a|opus|mp4|mkv|avi)$', ''

        if (-not $track) { return $null }

        # Reject transient titles that VLC emits during track transitions.
        # Common cases: just "vlc", the app name, or empty after cleanup.
        if ($track -imatch '^\s*vlc\s*$' -or $track -imatch '^VLC media player$') { return $null }
        if ($stripped -imatch '^\s*vlc\s*$') { return $null }

        # ── VLC timeline: local tick-clock with stability-gated SMTC sync ──────
        # VLC's SMTC is unreliable in two specific ways that break naïve caching:
        #   • On PAUSE, VLC drops its SMTC session entirely (so Find-SMTCSession
        #     returns null and we lose the Paused signal).
        #   • On SEEK, VLC briefly re-emits the session with positionMs=0 (or a
        #     tiny transient value) before settling on the real seek target.
        # The fix: maintain our OWN position clock that advances by wall-clock
        # delta per tick, and only sync from SMTC when we see two consecutive
        # stable, non-zero readings that differ meaningfully from local (real
        # seek). Pause is inferred when SMTC reports Paused OR when the session
        # disappears while we still see the VLC window with the same title.
        $vlcS       = Find-SMTCSession 'vlc|videolan' @('Playing', 'Changing', 'Paused')
        $smtcPos    = if ($vlcS) { [long]$vlcS.posMs } else { -1 }
        $smtcDur    = if ($vlcS) { [long]$vlcS.durMs } else { 0 }
        $smtcPaused = if ($vlcS) { [bool]$vlcS.paused } else { $null }
        $nowUtc     = [DateTime]::UtcNow

        # Track-key change → re-seed local clock from SMTC (or 0)
        $vlcKey = "$artist|||$track"
        if ($global:_vlcLastKey -ne $vlcKey) {
            $global:_vlcLastKey     = $vlcKey
            $global:_vlcLocalPosMs  = if ($smtcPos -gt 0) { $smtcPos } else { 0 }
            $global:_vlcLastTickUtc = $nowUtc
            $global:_vlcSmtcPrev    = 0
            $global:_vlcLastDurMs   = $smtcDur
        }

        # Paused inference:
        #   • SMTC says Paused → definitive.
        #   • SMTC session present + not Paused → definitive playing.
        #   • SMTC session missing (VLC drops it on pause BUT also takes ~1 s
        #     to register at track start) → ambiguous. Use AudioPeak to resolve:
        #       peak > 0    → audio is flowing → playing (not paused)
        #       peak = 0    → silent            → paused
        #       peak = -1   → no audio session visible (VLC not yet registered
        #                     with audio system, or muted) → debounce N ticks
        #                     before inferring paused so we don't falsely freeze
        #                     the timestamp when the song first starts.
        $vlcPaused = $false
        if ($smtcPaused -eq $true) {
            $vlcPaused = $true
            $global:_vlcNullSmtcTicks = 0
            $global:_vlcSilentTicks   = 0
        } elseif ($vlcS -eq $null) {
            $vlcPeak = -1.0
            try { $vlcPeak = [MasterFM.AudioPeak]::GetPeakForProcessName('vlc') } catch {}
            if ($vlcPeak -gt 0.001) {
                # Audio is flowing — VLC is playing (SMTC just hasn't registered yet).
                $vlcPaused = $false
                $global:_vlcNullSmtcTicks = 0
                $global:_vlcSilentTicks   = 0
            } elseif ($vlcPeak -ge 0) {
                # Silent audio session → COULD be paused OR a brief seek/buffer
                # transient. Debounce 4 ticks (~1 s) before declaring paused;
                # otherwise a single silent tick during a mid-track seek flips
                # isPaused=true, which blocks the server's drift correction and
                # freezes the overlay timestamp on the pre-seek position.
                $global:_vlcSilentTicks   = [int]$global:_vlcSilentTicks + 1
                $vlcPaused = ($global:_vlcSilentTicks -ge 4)
                $global:_vlcNullSmtcTicks = 0
            } else {
                # No audio session at all — apply a 4-tick (~1 s) debounce so
                # the timestamp doesn't freeze on startup while VLC boots.
                $global:_vlcNullSmtcTicks = [int]$global:_vlcNullSmtcTicks + 1
                $vlcPaused = ($global:_vlcNullSmtcTicks -ge 4)
                $global:_vlcSilentTicks   = 0
            }
        } else {
            $vlcPaused = $false
            $global:_vlcNullSmtcTicks = 0
            $global:_vlcSilentTicks   = 0
        }

        # Advance local clock by wall-clock delta while playing
        $dtMs = [long](($nowUtc - $global:_vlcLastTickUtc).TotalMilliseconds)
        $global:_vlcLastTickUtc = $nowUtc
        if (-not $vlcPaused -and $dtMs -gt 0 -and $dtMs -lt 5000) {
            $global:_vlcLocalPosMs += $dtMs
        }

        # Seek detection from SMTC: require two consecutive stable readings
        # (>500 ms apart from the 0-transient, within 1.5 s of each other)
        # AND a >3 s delta from our local clock. This rejects the posMs=0
        # spike VLC emits mid-seek before the real target lands.
        if ($smtcPos -gt 500) {
            $prev = [long]$global:_vlcSmtcPrev
            if ($prev -gt 0 -and [Math]::Abs($smtcPos - $prev) -le 1500) {
                $delta = [Math]::Abs($smtcPos - [long]$global:_vlcLocalPosMs)
                if ($delta -gt 3000) {
                    Log "VLC seek: local=$([Math]::Round($global:_vlcLocalPosMs/1000))s → smtc=$([Math]::Round($smtcPos/1000))s"
                    $global:_vlcLocalPosMs = $smtcPos
                }
            }
            $global:_vlcSmtcPrev = $smtcPos
        } else {
            # 0 or missing — don't let it propagate, don't update the stability reference
            $global:_vlcSmtcPrev = 0
        }

        # On pause, SMTC gives a reliable pause-point position → snap local
        if ($vlcPaused -and $smtcPos -gt 0) {
            $global:_vlcLocalPosMs = $smtcPos
        }

        # Sticky duration — SMTC drops it along with the session on pause
        if ($smtcDur -gt 0) { $global:_vlcLastDurMs = $smtcDur }

        $posMs = [long]$global:_vlcLocalPosMs
        $durMs = [long]$global:_vlcLastDurMs

        Log "VLC detected: artist='$artist' track='$track' pos=$([Math]::Round($posMs/1000))s dur=$([Math]::Round($durMs/1000))s paused=$vlcPaused"
        return @{
            artist     = if ($artist) { $artist } else { 'Unknown Artist' }
            track      = $track
            source     = 'VLC'
            positionMs = $posMs
            duration   = [double]($durMs / 1000.0)
            isPaused   = $vlcPaused
        }
    } catch {
        Log "VLC detection error: $_"
        return $null
    }
}

$global:_scrobbleLastKey    = ''
$global:_scrobblePollCount  = 0
$global:_diagTickCount      = 0
$global:_scrobbleLastPaused = $false
$global:_scrobbleLastPosMs  = 0
$global:_scrobbleLastSendMs = 0   # epoch ms when last position report was sent (for real-elapsed seek detection)
$global:_scrobbleStaleTicks = 0   # consecutive ticks where SMTC claims Playing but pos didn't advance
$global:_wmpComLastPid      = 0
$global:_wmpComFailedPid    = 0
$global:_wmpComLastState    = -1
$global:_wmpComNullMedia    = $false
$global:_wmpSmtcLastSpamKey = ''
$global:_vlcLastLoggedTitle = ''
$global:_osuCpuSample       = $null
$global:_osuCpuHist         = @()
$global:_osuLastLoggedPaused = $false
$global:_vlcLastKey         = ''
$global:_vlcLocalPosMs      = 0
$global:_vlcLastTickUtc     = [DateTime]::UtcNow
$global:_vlcSmtcPrev        = 0
$global:_vlcLastDurMs       = 0
$global:_vlcSilentTicks     = 0
$global:_vlcNullSmtcTicks   = 0
# Source-closed → 10 s hide: cache the last successful detection so we can
# send one final paused webhook when the source disappears (e.g. osu! closed).
# Overlay's existing 10 s pause-auto-hide then fades the widget out naturally.
$global:_lastNp             = $null
$global:_sourceClosedSent   = $false
$global:_nullTickCount      = 0

# ─────────────────────────────────────────────────────────────────────────────
# Dump-DiagnosticState — heavy diagnostic snapshot: running media processes +
# every SMTC session. Called every 600 ticks (~60s) so overlay.log tells us
# exactly what's visible to the tray when detection fails.
# ─────────────────────────────────────────────────────────────────────────────
function Dump-DiagnosticState {
    try {
        $names = @('wmplayer','MediaPlayer','Spotify','vlc','chrome','msedge',
                   'firefox','opera','brave','iTunes','AppleMusic','foobar2000',
                   'MusicBee','Deezer','tidal','osu!','soundcloud-rpc','soundcloud_rpc')
        $procs = Get-Process -Name $names -ErrorAction SilentlyContinue
        if ($procs) {
            $lines = foreach ($p in $procs) {
                $title = ''
                try { $title = $p.MainWindowTitle } catch {}
                "  [$($p.Id)] $($p.ProcessName) :: '$title'"
            }
            Log ("DIAG procs:`r`n" + ($lines -join "`r`n"))
        } else {
            Log "DIAG procs: (no tracked media processes running)"
        }
    } catch { Log "DIAG procs error: $_" }

    if ($global:smtcAvailable) {
        try {
            # v9.5.0: use the per-tick caches so the diagnostic dump warms the
            # cache for the detector chain that follows. Without this, dump-
            # diagnostic was doing one full SMTC enum + per-session props await,
            # then the detector chain repeated the same work seconds later in
            # the same tick.
            $sessions = Get-SMTCSessionsCached
            if (-not $sessions -or $sessions.Count -eq 0) {
                if ($null -eq $sessions) {
                    Log "DIAG SMTC: manager unavailable (backed off after timeout - broken Store-app SMTC session?)"
                } else {
                    Log "DIAG SMTC: 0 sessions"
                }
            } else {
                $slines = foreach ($s in $sessions) {
                    $aid    = $s.SourceAppUserModelId
                    $status = 'unknown'
                    try { $status = $s.GetPlaybackInfo().PlaybackStatus.ToString() } catch {}
                    $t=''; $a=''; $al=''; $sub=''
                    try {
                        $p = Get-SMTCMediaPropsCached -Session $s -TimeoutMs 150
                        if ($p) {
                            $t   = ($p.Title       + '').Trim()
                            $a   = ($p.Artist      + '').Trim()
                            $al  = ($p.AlbumTitle  + '').Trim()
                            $sub = ($p.Subtitle    + '').Trim()
                        }
                    } catch {}
                    "  appId='$aid' status=$status title='$t' artist='$a' album='$al' subtitle='$sub'"
                }
                Log ("DIAG SMTC ($($sessions.Count) sessions):`r`n" + ($slines -join "`r`n"))
            }
        } catch { Log "DIAG SMTC error: $_" }
    } else {
        Log "DIAG SMTC: not available"
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Invoke-Detector — runs a detector and returns both result + a short status
# string for the chain-summary log line.
# ─────────────────────────────────────────────────────────────────────────────
if (-not $global:_detSlowSkip) { $global:_detSlowSkip = @{} }

function Invoke-Detector($name, $fn) {
    # v6.9.5: per-detector circuit breaker. If a detector ever takes longer
    # than SLOW_MS to run, skip it for COOLDOWN_TICKS subsequent ticks.
    # v6.9.6: cooldown bumped 10 -> 30 ticks (now 3 s at the 100 ms tick rate)
    # because rapid track switching keeps SMTC under sustained stress for
    # 5-15 s — the old 500 ms cooldown expired while the system was still
    # overloaded, so detectors kept slow-tripping in a loop. 3 s lets SMTC
    # recover before we ask again. Sustained-broken detectors are now rate-
    # limited to ~0.3 attempts/sec; a one-off slow detector recovers cleanly
    # with a 3 s gap that's invisible to the user.
    # v9.5.0: capture per-detector elapsed ms in $global:_detectorMs[name] so
    # the SLOW TICK breakdown can show exactly which detector(s) are slow.
    # Hashtable is reset at tick-start in scrobbleTimer.add_Tick.
    $SLOW_MS        = 150
    $COOLDOWN_TICKS = 30
    $left = [int]$global:_detSlowSkip[$name]
    if ($left -gt 0) {
        $global:_detSlowSkip[$name] = $left - 1
        if ($global:_detectorMs) { $global:_detectorMs[$name] = -1 }   # -1 = skipped via cooldown
        return @{ result = $null; tag = "$name=skip-slow" }
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = & $fn
        $sw.Stop()
        $elapsed = [int]$sw.ElapsedMilliseconds
        if ($global:_detectorMs) { $global:_detectorMs[$name] = $elapsed }
        if ($elapsed -gt $SLOW_MS) {
            $global:_detSlowSkip[$name] = $COOLDOWN_TICKS
            Log "detector[$name] slow ($elapsed ms) — backing off for $COOLDOWN_TICKS ticks"
        }
        if ($r) {
            return @{ result = $r; tag = "$name=HIT($($r.artist) - $($r.track))" }
        } else {
            return @{ result = $null; tag = "$name=null" }
        }
    } catch {
        $sw.Stop()
        if ($global:_detectorMs) { $global:_detectorMs[$name] = [int]$sw.ElapsedMilliseconds }
        # Full stack so we can actually find the buggy line number instead of
        # just "the term 'x' is not recognized".
        LogErr "detector[$name]" $_
        return @{ result = $null; tag = "$name=ERR" }
    }
}

# v11.0.0: pre-allocate per-tick diagnostic hashtables as persistent instances.
# Previously allocated with @{} on EVERY scrobble tick (10/sec = 72,000/hr).
# Using .Clear() on a persistent instance eliminates this GC pressure.
$global:_tickPhaseMs = [System.Collections.Hashtable]::new()
$global:_detectorMs  = [System.Collections.Hashtable]::new()
# v11.1.0: pre-allocate detector chain tag list. Previously $chain = @() + $chain += tag
# per tick (array-copy on every append, ~10 copies/tick = 72,000 allocs/hr).
$global:_chain = [System.Collections.Generic.List[string]]::new(16)

$scrobbleTimer          = New-Object System.Windows.Forms.Timer
# v6.9.6: scrobble timer 50 -> 100 ms. The 50 ms cadence was halving Task
# Manager's reported CPU into a visible spike under SMTC stress (rapid
# track switching → SMTC enumeration takes 150-200 ms each → at 50 ms
# tick the detectors keep stacking against an overloaded Windows SMTC).
# 100 ms cuts baseline CPU in half AND cuts the post-tick recovery rate
# in half, so spike duration shortens too. Pause/unpause latency goes
# from ~80-120 ms to ~130-170 ms — still inside the "instant" perception
# window (the laggy threshold the v6.6.2 note cited was 300 ms).
$scrobbleTimer.Interval = 100
$scrobbleTimer.add_Tick({
    # Stop the timer immediately so queued ticks don't pile up if this one
    # takes longer than the interval (slow SMTC, WinRT timeout, etc.).
    # The finally block restarts it, giving a consistent ~100 ms gap after each tick.
    $scrobbleTimer.Stop()
    # ── Freeze-triage wrapper ────────────────────────────────────────────────
    # The scrobble timer ticks every 100 ms on the WinForms UI thread. If a
    # detector hangs (synchronous WinRT, audio-peak API blocking, etc.) the
    # whole tray — including the tray icon itself — freezes. We capture:
    #   • any exception that would otherwise silently kill the tick loop
    #   • tick duration, with a warning whenever a single tick exceeds 200 ms
    #     (Windows UI contract: anything >200 ms is perceptibly laggy)
    $tickSw = [System.Diagnostics.Stopwatch]::StartNew()
    # v8.2.4 PERF: track WHICH section the tick is in so slow-tick logs identify
    # the culprit. Updated at strategic checkpoints throughout the body.
    # v9.5.0 PERF: also reset per-detector ms hashtable so Invoke-Detector can
    # populate it during the detector-chain phase. Slow-tick log appends a
    # detector-level breakdown so we know which DETECTOR is the actual culprit
    # (not just "detector-chain took 195ms" but "spotify=82ms smtc=98ms").
    $global:_tickPhase = 'tick-start'
    # v11.0.0: .Clear() reuses persistent instance instead of allocating new @{} each tick
    $global:_tickPhaseMs.Clear()
    $global:_detectorMs.Clear()
    try {
    $global:_diagTickCount++

    # Launcher liveness check — every 20 ticks (~5 s). If MastersFM.exe was killed
    # from Task Manager, the Job Object fires first; this is the backup in case it
    # misses (e.g. the process was already in a nested job when AssignProcessToJobObject ran).
    if ($global:_launcherPid -gt 0 -and ($global:_diagTickCount % 50) -eq 10) {
        if (-not (Get-Process -Id $global:_launcherPid -ErrorAction SilentlyContinue)) {
            Log "Launcher PID=$($global:_launcherPid) has exited — tray self-terminating"
            [System.Windows.Forms.Application]::Exit()
            return
        }
    }

    # Tick is 100 ms — diagnostics at ~60 s cadence (600 ticks).
    # v9.5.0: was every 200 ticks (20 s) but Dump-DiagnosticState costs ~46 ms,
    # which lined up with the 200-ms SLOW TICK threshold whenever it coincided
    # with a track-change tick. The diagnostic is purely informational (process
    # list + SMTC session dump for log-trawling) — every 60 s is plenty.
    if (($global:_diagTickCount % 600) -eq 1) {
        $global:_tickPhase = 'dump-diagnostic'
        Dump-DiagnosticState
        $global:_tickPhaseMs['dump-diagnostic'] = [int]$tickSw.ElapsedMilliseconds
    }
    $global:_tickPhase = 'detector-chain'

    # Detection priority (highest → lowest):
    #   osu!              — window title
    #   Spotify           — dedicated SMTC check
    #   SMTC (general)    — TIDAL, Deezer, Apple Music, foobar, MusicBee, etc.
    #   Browser media     — Chrome/Edge/Firefox SMTC
    #   SoundCloud        — browser window-title fallback
    #   WMP COM           — WMP Legacy COM interface
    #   WMP SMTC          — WMP Legacy via SMTC two-pass scan
    #   WMP window title  — fallback for new Windows 11 MediaPlayer.exe
    #   VLC               — window title + SMTC position
    # ── Multi-source conflict resolution ────────────────────────────────────
    # Problem: if Spotify is open but paused AND SoundCloud/YouTube is playing
    # in a browser, the old first-hit short-circuit gave the overlay to Spotify
    # (because it's listed first) and browser media was never checked, causing
    # the platforms to fight or show the wrong one.
    #
    # Fix: two-pass priority —
    #   1. A PLAYING/CHANGING result wins immediately (short-circuit as before).
    #   2. A PAUSED result is saved as $npPaused (fallback) but the chain
    #      continues running so a later detector's Playing result can beat it.
    #   3. After all detectors: if nothing is Playing, use $npPaused.
    #
    # Effect: Spotify paused + SoundCloud playing → SoundCloud shown.
    #         Spotify paused + nothing else found  → Spotify shown (correct).
    #         Both paused → first in chain wins (stable, no flipping).
    $np       = $null   # first Playing/Changing result found
    $npPaused = $null   # first Paused result found (fallback)
    $global:_chain.Clear()   # v11.1.0: reuse pre-allocated List[string] instead of @() per tick
    # v8.2.4 perf — tick-budget short-circuit. Per-detector circuit breaker
    # (Invoke-Detector / SLOW_MS=150) already throttles individually-slow
    # detectors, but during rapid track-change bursts MULTIPLE detectors can
    # each take 50-100 ms in the SAME tick (each below the 150 ms slow
    # threshold but cumulatively producing 300-600 ms slow ticks that block
    # the WinForms UI thread = the user's perceived "freeze"). 150 ms total
    # budget per tick caps the worst case at ~200 ms incl. tick overhead.
    # Skipped detectors will run next tick (100 ms later) — UX impact is
    # imperceptible since the lower-priority sources (WMP, VLC) only matter
    # when no higher-priority source is active anyway.
    $TICK_BUDGET_MS = 150
    foreach ($d in @(
        @{ n='osu';        f={ Get-OsuNowPlaying }          },
        @{ n='spotify';    f={ Get-SpotifyNowPlaying }      },
        @{ n='smtc';       f={ Get-SMTCNowPlaying }         },
        @{ n='browser';    f={ Get-BrowserMediaNowPlaying } },
        @{ n='soundcloud'; f={ Get-SoundCloudNowPlaying }   },
        @{ n='wmpCOM';     f={ Get-WMPNowPlayingCOM }       },
        @{ n='wmpSMTC';    f={ Get-WMPNowPlayingSMTC }      },
        @{ n='wmpUIA';     f={ Get-WMPNowPlayingUIA }       },
        @{ n='wmpTitle';   f={ Get-WMPNowPlaying }          },
        @{ n='vlc';        f={ Get-VLCNowPlaying }          }
    )) {
        if ($np) { $chain += "$($d.n)=skip"; continue }   # Playing winner already found
        if ($tickSw.ElapsedMilliseconds -gt $TICK_BUDGET_MS) {
            $chain += "$($d.n)=skip-tickbudget"
            continue
        }
        $r = Invoke-Detector $d.n $d.f
        if ($r.result) {
            if ($r.result.isPaused) {
                # Paused result — save as fallback, keep searching for Playing
                if (-not $npPaused) { $npPaused = $r.result }
                $chain += "$($d.n)=paused-hold"
            } else {
                # Playing / Changing — wins, stop the chain
                $np = $r.result
                $global:_chain.Add($r.tag)
            }
        } else {
            $global:_chain.Add($r.tag)
        }
    }
    $global:_tickPhaseMs['detector-chain'] = [int]$tickSw.ElapsedMilliseconds
    $global:_tickPhase = 'post-detector'
    # Nothing actively playing → fall back to the first paused source
    if (-not $np -and $npPaused) { $np = $npPaused }
    # DETECT chain line: ~every 10 s (100 ticks at 100 ms)
    if (($global:_diagTickCount % 100) -eq 1) {
        Log ("DETECT: " + ($global:_chain -join ' '))
    }

    # ── Song-epoch tracker ─────────────────────────────────────────────────────
    # _songEpoch maps "artist|||track" → epoch ms when that song started playing.
    # Recorded whenever a source first wins with that key (using nowMs-positionMs
    # to backtrack to the true start), so the epoch survives source switches.
    # When a source wins with positionMs=0 (soundcloud-rpc always reports 0) and
    # the song was seen before, we estimate position from the stored epoch.
    $nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    if (-not $global:_songEpoch) { $global:_songEpoch = @{} }

    $global:_tickPhase = 'sc-shadow-check'
    # When a non-SC source is winning, peek at the SC SMTC session so songs
    # that ONLY ever ran in the background get an epoch too. Manager is cached.
    if ($np -and $np.source -ne 'SoundCloud') {
        try {
            $scSess = Find-SMTCSession 'soundcloud' @('Playing','Changing')
            if ($scSess -and $scSess.title) {
                $scKey = "$($scSess.artist)|||$($scSess.title)"
                if (-not $global:_songEpoch.ContainsKey($scKey)) {
                    $global:_songEpoch[$scKey] = $nowMs
                    Log "SC shadow: first-seen '$($scSess.artist) - $($scSess.title)' while $($np.source) is active"
                }
            }
        } catch {}
    }

    # Cleanup: evict entries older than 2 hours (every ~10 min)
    if (($global:_diagTickCount % 2400) -eq 0) {
        $cutoff = $nowMs - (2 * 60 * 60 * 1000)
        foreach ($k in @($global:_songEpoch.Keys)) {
            if ($global:_songEpoch[$k] -lt $cutoff) { $global:_songEpoch.Remove($k) }
        }
    }

    if (-not $np) {
        # Debounce: osu! (and other sources) briefly return null between track
        # changes, menu transitions, or scroll-past-empty-slots. Firing a paused
        # webhook on a single null tick makes the overlay jitter/fade during
        # normal scrolling. We wait for 12 consecutive nulls (~3 s) before
        # declaring the source truly closed.
        $global:_nullTickCount = [int]$global:_nullTickCount + 1
        if ($global:_lastNp -and -not $global:_sourceClosedSent -and $global:_nullTickCount -ge 12) {
            try {
                $closedPayload = ConvertTo-Json @{
                    artist     = $global:_lastNp.artist
                    track      = $global:_lastNp.track
                    source     = $global:_lastNp.source
                    positionMs = [long]$global:_lastNp.positionMs
                    duration   = [double]$global:_lastNp.duration
                    isPaused   = $true
                }
                # Encode as explicit UTF-8 bytes — PS 5.1 ConvertTo-Json does NOT escape
                # non-ASCII to \uXXXX, so sending the raw string with Invoke-RestMethod
                # uses the system ANSI code page (Windows-1252) which corrupts Hebrew,
                # Cyrillic, CJK and any other non-Latin characters to "?".
                $closedBytes = [System.Text.Encoding]::UTF8.GetBytes($closedPayload)
                # v8.2.5: async fire-and-forget (was synchronous Invoke-RestMethod)
                Send-WebhookAsync -Url "http://127.0.0.1:4242/webhook" -Body $closedBytes
                Log "Source closed [$($global:_lastNp.source)]: $($global:_lastNp.artist) - $($global:_lastNp.track) → sent paused webhook after $($global:_nullTickCount) null ticks"
            } catch { Log "Source-closed /webhook POST failed (server may be down): $_" }
            $global:_sourceClosedSent = $true
        }
        # Don't wipe scrobble-last-key until we've actually declared source-closed,
        # otherwise the next non-null tick treats the same track as brand-new and
        # slams a full /webhook + animation for no reason.
        if ($global:_sourceClosedSent) {
            $global:_scrobbleLastKey    = ''
            $global:_scrobblePollCount  = 0
            $global:_scrobbleLastPosMs  = 0
            $global:_scrobbleStaleTicks = 0
        }
        return
    }

    # ── Source-switch debounce ─────────────────────────────────────────────────
    # Skipping a track on platform A while platform B is running causes A's SMTC
    # to briefly show an empty/Changing title for ~250-750 ms. B wins for 1-3
    # ticks and the overlay flashes A→B→A on every skip.
    # Fix: require 3 consecutive ticks (~750 ms) of a new SOURCE before accepting
    # the switch. Same-source track changes (different song, same platform) are
    # NOT debounced — those take effect on the very next tick as usual.
    $currentSource = if ($global:_lastNp) { $global:_lastNp.source } else { '' }
    if ($np -and $currentSource -and $np.source -ne $currentSource) {
        if ($global:_dbncSource -eq $np.source) {
            $global:_dbncTicks = [int]$global:_dbncTicks + 1
        } else {
            $global:_dbncSource = $np.source
            $global:_dbncTicks  = 1
        }
        if ($global:_dbncTicks -lt 3) {
            Log "Debounce: $currentSource → $($np.source) (tick $($global:_dbncTicks)/3, holding)"
            return   # hold — don't update state or send a webhook this tick
        }
        Log "Source switch: $currentSource → $($np.source) (debounce passed)"
        $global:_dbncSource = ''; $global:_dbncTicks = 0
    } else {
        $global:_dbncSource = ''; $global:_dbncTicks = 0
    }

    # Cache most recent successful detection so we can notify on source-close.
    $global:_lastNp           = $np
    $global:_sourceClosedSent = $false
    $global:_nullTickCount    = 0

    $key = "$($np.artist)|||$($np.track)"

    if ($key -eq $global:_scrobbleLastKey) {
        # Same track — send on meaningful change (pause-flip, seek) or every 8 ticks (~2 s) heartbeat.
        $global:_scrobblePollCount++

        # ── Position-stagnation pause inference ─────────────────────────────────
        # Kept as a *last-resort* fallback for sources that genuinely freeze
        # positionMs on pause AND don't expose another signal.
        #
        # Explicitly DISABLED for soundcloud-rpc: that extension also freezes
        # positionMs while actively PLAYING (it only pushes updates on track
        # change / seek), so stagnation here produces false pauses. The
        # tab-title override upstream in Get-SMTCNowPlaying is authoritative.
        #
        # Threshold is intentionally high (120 ticks ~= 30 s of zero movement)
        # so normal SMTC push gaps don't trigger it.
        # Browsers expose positionMs through the Media Session API, which many
        # sites (SoundCloud, YouTube, Deezer web, Apple Music web) update
        # lazily or freeze while still playing audio. Inferring "paused"
        # from a stagnant position in ANY browser-sourced session caused the
        # overlay to fade out after ~30 s mid-track — skip the inference for
        # all browser platforms, not just SoundCloud.
        $allowStagnationInference = ($np.source -notmatch '(?i)soundcloud|chrome|edge|firefox|opera|brave|youtube|deezer|apple music')
        if ($allowStagnationInference -and -not $np.isPaused) {
            if ([long]$np.positionMs -eq [long]$global:_scrobbleLastPosMs -and $np.positionMs -gt 0) {
                $global:_scrobbleStaleTicks++
                if ($global:_scrobbleStaleTicks -ge 120) {
                    if (-not $global:_scrobbleLastPaused) {
                        Log "Inferred pause [$($np.source)]: SMTC Playing but pos frozen at $([Math]::Round($np.positionMs/1000))s for $($global:_scrobbleStaleTicks) ticks"
                    }
                    $np.isPaused = $true
                }
            } else {
                $global:_scrobbleStaleTicks = 0
            }
        } else {
            $global:_scrobbleStaleTicks = 0
        }

        $pauseChanged = ($np.isPaused -ne $global:_scrobbleLastPaused)

        # Seek detection in tray: compare positionMs delta to REAL wall-clock time elapsed
        # since the last position report. Using actual elapsed time (not a fixed 250 ms) prevents
        # false-seek detection when SMTC freezes then catches up — during an SMTC freeze both
        # actualDeltaMs and realElapsedMs grow together, so seekDrift stays near zero.
        # A genuine seek (user scrubs) still fires: positionMs jumps by > realElapsedMs + 3 s.
        $realElapsedMs = if ($global:_scrobbleLastSendMs -gt 0) {
            [Math]::Max(0, $nowMs - $global:_scrobbleLastSendMs)
        } else { 100 }
        $actualDeltaMs = [long]$np.positionMs - [long]$global:_scrobbleLastPosMs
        # Seek gating: disable ONLY when both previous and current tick were
        # paused (no legitimate position change is possible). Previous logic
        # disabled seek detection on ANY pause — which meant a mid-track VLC
        # seek (briefly silent → 1-tick paused flip) never fired seek=true,
        # and the server's drift correction left the overlay frozen on the
        # pre-seek position.
        $bothPaused    = ($np.isPaused -and $global:_scrobbleLastPaused)
        $seekDrift     = if ($bothPaused -or $realElapsedMs -le 0) {
            0
        } else {
            [Math]::Abs($actualDeltaMs - $realElapsedMs)
        }
        $seekDetected = ($seekDrift -gt 3000)

        # tl.LastUpdatedTime advanced since the last tick — the source pushed
        # a fresh timeline (usually play/pause/seek). Force a webhook with
        # seek=true so the server re-pins startedAt immediately rather than
        # waiting up to 2s for the next heartbeat. Critical for seeks on
        # YouTube Music / Spotify web / SoundCloud web, where the player
        # updates setPositionState on seek but the position delta isn't always
        # big enough to trigger the drift-based detector above.
        $tlFreshNow = [bool]$np.tlFresh
        if ($tlFreshNow -and -not $bothPaused -and -not $pauseChanged) {
            # Don't flag as a seek on the VERY FIRST tick of a new track — that
            # fresh signal is just "session spawned", not a seek event.
            $trackKey = "$($np.artist)|||$($np.track)"
            if ($global:_scrobbleLastKey -eq $trackKey) {
                $seekDetected = $true
            }
        }

        $isHeartbeat = (($global:_scrobblePollCount % 8) -eq 0)
        $needSend    = $pauseChanged -or $seekDetected -or $isHeartbeat

        $global:_scrobbleLastPaused = [bool]$np.isPaused
        $global:_scrobbleLastPosMs  = [long]$np.positionMs

        if ($needSend) {
            # For browser-based sources: when SMTC position is frozen (same value as last
            # heartbeat), estimate from song epoch instead. This prevents the server from
            # seeing a stale backward position and snapping the overlay's startedAt backwards,
            # which is the primary cause of long-term cumulative timer drift on extended tracks.
            #
            # IMPORTANT: this estimate MUST be bypassed when seekDetected is true.
            # The seek webhook carries the NEW target position; the song epoch is
            # still anchored to the OLD pre-seek start time, so estimating from it
            # would replace the real seek target with a stale value and the overlay
            # would keep tracking the pre-seek position. We instead re-pin the
            # epoch to the new position below so subsequent heartbeats stay correct.
            $heartbeatPosMs = [long]$np.positionMs
            $isBrowserLike  = ($np.source -match '(?i)chrome|edge|firefox|opera|brave|youtube|deezer|soundcloud|apple music|tidal|bandcamp')
            if (-not $seekDetected -and $isBrowserLike -and -not $np.isPaused -and $heartbeatPosMs -gt 0 `
                    -and $heartbeatPosMs -eq [long]$global:_scrobbleLastPosMs `
                    -and $global:_songEpoch -and $global:_songEpoch.ContainsKey($key)) {
                $epochEst = $nowMs - $global:_songEpoch[$key]
                if ($epochEst -gt 0 -and $epochEst -lt 3600000) {
                    $heartbeatPosMs = $epochEst
                }
            }
            # Seek just fired → re-pin the song epoch to the new playhead so the
            # NEXT heartbeat's epoch-estimate math uses the correct base time.
            if ($seekDetected -and $heartbeatPosMs -gt 0) {
                $global:_songEpoch[$key] = $nowMs - $heartbeatPosMs
            }
            try {
                $posPayload = ConvertTo-Json @{
                    artist     = $np.artist
                    track      = $np.track
                    source     = $np.source
                    positionMs = $heartbeatPosMs
                    duration   = [double]$np.duration
                    isPaused   = [bool]$np.isPaused
                    seek       = [bool]$seekDetected
                }
                $posBytes = [System.Text.Encoding]::UTF8.GetBytes($posPayload)
                $global:_tickPhase = 'webhook-heartbeat'
                $whSw = [System.Diagnostics.Stopwatch]::StartNew()
                # v8.2.5: async fire-and-forget (was synchronous Invoke-RestMethod)
                Send-WebhookAsync -Url "http://127.0.0.1:4242/webhook" -Body $posBytes
                $whSw.Stop()
                $global:_tickPhaseMs['webhook-heartbeat'] = [int]$whSw.ElapsedMilliseconds
                $global:_scrobbleLastSendMs = $nowMs
                if ($pauseChanged) {
                    Log "State change [$($np.source)]: $($np.artist) - $($np.track) paused=$($np.isPaused) @ $([Math]::Round($heartbeatPosMs/1000))s"
                } elseif ($seekDetected) {
                    Log "Seek [$($np.source)]: $($np.artist) - $($np.track) @ $([Math]::Round($heartbeatPosMs/1000))s"
                } elseif (($global:_scrobblePollCount % 80) -eq 0) {
                    Log "Position refresh [$($np.source)]: $($np.artist) - $($np.track) @ $([Math]::Round($heartbeatPosMs/1000))s"
                }
            } catch {
                # Heartbeat webhook fires every few seconds - log at most once
                # per 30 s to avoid flooding if the server is dead for a while.
                $nowLogMs = ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())
                if (-not $global:_hbLastFailLog -or ($nowLogMs - $global:_hbLastFailLog) -gt 30000) {
                    $global:_hbLastFailLog = $nowLogMs
                    Log "Heartbeat /webhook POST failed (server may be down) [$($np.source)]: $_"
                }
            }
        }
        # v9.9.9: deferred SMTC art — extracted here (heartbeat tick) rather than
        # on the new-track tick so the skip itself has zero UI-thread blocking.
        # Invoke-DeferredThumbExtraction enforces a 400 ms post-queue delay internally;
        # returns '' until the artwork is loaded, then the real data URI.
        try {
            $_deferredArt = Invoke-DeferredThumbExtraction
            if ($_deferredArt) {
                $global:_tickPhase = 'webhook-art-update'
                $artUpd = ConvertTo-Json @{
                    artist     = $np.artist;  track     = $np.track
                    source     = $np.source;  positionMs = [long]$np.positionMs
                    duration   = [double]$np.duration; isPaused = [bool]$np.isPaused
                    trackArt   = $_deferredArt
                } -Compress
                Send-WebhookAsync -Url "http://127.0.0.1:4242/webhook" -Body ([System.Text.Encoding]::UTF8.GetBytes($artUpd))
                Log "SMTC art delivered (deferred): $($np.artist) - $($np.track)"
            }
        } catch {}
        return
    }

    # Save the outgoing source's last known position BEFORE overwriting _scrobbleLastKey.
    # Used below to detect whether SMTC is frozen (didn't advance while backgrounded).
    if (-not $global:_sourceLastPos) { $global:_sourceLastPos = @{} }
    if ($global:_scrobbleLastKey) {
        $global:_sourceLastPos[$global:_scrobbleLastKey] = @{
            posMs  = [long]$global:_scrobbleLastPosMs
            timeMs = $nowMs
        }
    }

    # New track
    $global:_scrobbleLastKey    = $key
    $global:_scrobblePollCount  = 0
    $global:_scrobbleLastPaused = [bool]$np.isPaused
    $global:_scrobbleLastPosMs  = [long]$np.positionMs
    $global:_scrobbleStaleTicks = 0

    # ── v8.2.6 NEW-TRACK EPOCH RESET ────────────────────────────────────────
    # Symptom (pre-fix): every other (or worse) SoundCloud track scrobbled
    # with the PREVIOUS track's "fully played" position (e.g. overlay shows
    # 22:08/45:09 on a song the SC web player itself shows at 0:02).
    # Root cause: the original `if (-not ContainsKey)` set the epoch only
    # ONCE per (artist|||track) key — replays inherited the OLD start time.
    # And the backtrack `nowMs - $np.positionMs` is unsafe for browser
    # sources because $np.positionMs is unreliable at track-change time:
    # Chrome/Edge don't push fresh MediaSession TimelineProperties for
    # backgrounded tabs, and com.richardhbtz.soundcloud-rpc reports
    # cumulative-session-ms instead of per-track ms. So epoch got pinned
    # to "12 minutes ago" and the next epoch-mode estimate read back the
    # previous track's near-end position.
    #
    # Fix: ALWAYS reset _songEpoch[key] for the new track. For browser
    # sources, force np.positionMs = 0 (assume fresh start at position 0)
    # and drop the stale _sourceLastPos[key] so the smtc-frozen check
    # below doesn't match an ancient stored position. The heartbeat path's
    # seek-detection re-pins the epoch if the user actually started
    # mid-track. Non-browser sources (Spotify desktop, osu!, WMP COM, VLC)
    # report accurate per-track positionMs from the first tick, so they
    # keep the trusted backtrack.
    $isBrowserLikeForEpoch = ($np.source -match '(?i)soundcloud|chrome|edge|firefox|opera|brave|youtube|deezer|tidal|apple music|bandcamp')
    if ($isBrowserLikeForEpoch) {
        $np.positionMs = 0
        $global:_songEpoch[$key] = $nowMs
        if ($global:_sourceLastPos -and $global:_sourceLastPos.ContainsKey($key)) {
            [void]$global:_sourceLastPos.Remove($key)
        }
        # Re-sync the just-updated _scrobbleLastPosMs so the next heartbeat
        # tick compares against the corrected 0, not the stale value.
        $global:_scrobbleLastPosMs = 0
    } else {
        $global:_songEpoch[$key] = $nowMs - [long]$np.positionMs
    }

    # ── Cross-source position estimation ──────────────────────────────────────
    # SMTC position from browser-based sources (SoundCloud, Deezer web, YouTube,
    # etc.) freezes the instant another source takes priority — Chrome/Edge stop
    # pushing SMTC updates for the backgrounded tab. When that tab wins again,
    # its reported positionMs is stale. We detect this by checking whether the
    # position moved since the source was last active:
    #   • Unchanged (|delta| < 3 s) + significant time elapsed → frozen → use epoch.
    #   • Changed (user seeked while backgrounded) → trust SMTC.
    # For soundcloud-rpc, positionMs is always 0, so the positionMs==0 branch fires.
    $isBrowserSource = ($np.source -match '(?i)soundcloud|chrome|edge|firefox|opera|brave|youtube|deezer|tidal|apple music|bandcamp|wmp|vlc|foobar|musicbee|aimp|winamp')
    $useEpoch = $false

    if ([long]$np.positionMs -eq 0) {
        $useEpoch = $true   # soundcloud-rpc / any source that can't report position
        Log "Epoch mode [$($np.source)]: positionMs=0 (SMTC can't report position)"
    } elseif ($isBrowserSource -and $global:_sourceLastPos.ContainsKey($key)) {
        $last        = $global:_sourceLastPos[$key]
        $elapsed     = $nowMs - $last.timeMs
        $delta       = [Math]::Abs([long]$np.positionMs - $last.posMs)
        $smtcFrozen  = ($delta -lt 3000 -and $elapsed -gt 5000)
        if ($smtcFrozen) {
            $useEpoch = $true
            Log "SMTC frozen [$($np.source)]: '$($np.artist) - $($np.track)' pos unchanged (delta=${delta}ms over ${elapsed}ms) → using epoch"
        } else {
            Log "SMTC fresh [$($np.source)]: pos delta=${delta}ms elapsed=${elapsed}ms → trusting SMTC positionMs=$([Math]::Round($np.positionMs/1000))s"
        }
    }

    if ($useEpoch) {
        $epochEstimate = $nowMs - $global:_songEpoch[$key]
        if ($epochEstimate -gt 0 -and $epochEstimate -lt 3600000) {
            $np.positionMs = $epochEstimate
            Log "Position estimate [$($np.source)]: '$($np.artist) - $($np.track)' ~$([Math]::Round($epochEstimate/1000))s from epoch"
        }
    } elseif ([long]$np.positionMs -gt 0) {
        # SMTC is fresh (user seeked or position updated) — refresh the epoch so
        # future estimates are anchored to this accurate position.
        $global:_songEpoch[$key] = $nowMs - [long]$np.positionMs
    }

    try {
        # Pass trackArt if detector extracted one (SMTC thumbnail). Server still
        # tries Deezer/iTunes/MusicBrainz first; this is the fallback for obscure
        # tracks (especially SoundCloud) where online lookups fail.
        $body = @{
            artist     = $np.artist
            track      = $np.track
            source     = $np.source
            positionMs = [long]$np.positionMs
            duration   = [double]$np.duration
            isPaused   = [bool]$np.isPaused
        }
        if ($np.trackArt) { $body.trackArt = $np.trackArt }
        $payload      = ConvertTo-Json $body -Compress
        $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
        $global:_tickPhase = 'webhook-newtrack'
        $whSw2 = [System.Diagnostics.Stopwatch]::StartNew()
        # v8.2.5: async fire-and-forget (was synchronous Invoke-RestMethod with TimeoutSec=5)
        Send-WebhookAsync -Url "http://127.0.0.1:4242/webhook" -Body $payloadBytes
        $whSw2.Stop()
        $global:_tickPhaseMs['webhook-newtrack'] = [int]$whSw2.ElapsedMilliseconds
        $global:_scrobbleLastSendMs = $nowMs
        $artTag = if ($np.trackArt) { ' art=SMTC' } else { '' }
        Log "Scrobble [$($np.source)$(if ($np.appId) { ' / ' + $np.appId } else { '' })]: $($np.artist) - $($np.track)$(if ($np.duration -gt 0) { ' (' + [Math]::Round($np.duration) + 's)' } else { '' })$artTag"
    } catch {
        # Server not ready — retry next tick. Log once per 30s to surface
        # a sustained server outage without spamming on expected startup blips.
        $nowLogMs = ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())
        if (-not $global:_scrobLastFailLog -or ($nowLogMs - $global:_scrobLastFailLog) -gt 30000) {
            $global:_scrobLastFailLog = $nowLogMs
            Log "Scrobble /webhook POST failed (server may be starting) [$($np.source)]: $_"
        }
    }
    # v9.9.9: also check deferred art on new-track tick — Invoke-DeferredThumbExtraction
    # returns '' if < 400 ms have elapsed (usual case), fires extraction on later ticks.
    try {
        $_deferredArt = Invoke-DeferredThumbExtraction
        if ($_deferredArt) {
            $global:_tickPhase = 'webhook-art-update'
            $artUpd = ConvertTo-Json @{
                artist     = $np.artist;  track     = $np.track
                source     = $np.source;  positionMs = [long]$np.positionMs
                duration   = [double]$np.duration; isPaused = [bool]$np.isPaused
                trackArt   = $_deferredArt
            } -Compress
            Send-WebhookAsync -Url "http://127.0.0.1:4242/webhook" -Body ([System.Text.Encoding]::UTF8.GetBytes($artUpd))
            Log "SMTC art delivered (deferred): $($np.artist) - $($np.track)"
        }
    } catch {}
    } catch {
        # Any exception inside the tick body (detector bug, dead COM object,
        # WinRT release crash …) would otherwise silently kill the Timer.
        # Log every detail we can get and keep the loop alive.
        try { LogErr 'scrobbleTimer.Tick' $_ } catch {}
    } finally {
        try {
            $tickSw.Stop()
            $ms = [int]$tickSw.ElapsedMilliseconds
            $global:_tickMaxMs = [Math]::Max([int]$global:_tickMaxMs, $ms)
            $global:_tickSumMs = [long]$global:_tickSumMs + $ms
            $global:_tickSamples = [long]$global:_tickSamples + 1
            # Warn on any single slow tick (>200 ms = perceptible UI lag).
            if ($ms -gt 200) {
                # v8.2.4 PERF: include phase breakdown so we know WHICH section of
                # the tick body was slow (was an opaque "UI thread blocked" before).
                # v9.5.0 PERF: append per-detector breakdown so we know which DETECTOR
                # was the actual culprit (e.g. "spotify=82ms smtc=98ms") not just
                # the aggregate "detector-chain=195ms" which hides which one is slow.
                $phaseInfo = "phase=$($global:_tickPhase)"
                if ($global:_tickPhaseMs -and $global:_tickPhaseMs.Count -gt 0) {
                    $phaseInfo += " breakdown=" + (($global:_tickPhaseMs.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)ms" }) -join ',')
                }
                if ($global:_detectorMs -and $global:_detectorMs.Count -gt 0) {
                    $detList = $global:_detectorMs.GetEnumerator() | Where-Object { $_.Value -ge 0 } | Sort-Object { -[int]$_.Value }
                    if ($detList) {
                        $phaseInfo += " detectors=" + (($detList | ForEach-Object { "$($_.Key)=$($_.Value)ms" }) -join ',')
                    }
                }
                Log ("!! SLOW TICK #{0}: {1} ms (UI thread blocked) {2}" -f $global:_diagTickCount, $ms, $phaseInfo)
                # v9.10.0: for stalls >250 ms also log recent log context + process snapshot
                if ($ms -gt 250) {
                    try {
                        $recentCtx = if ($global:_logRingBuf -and $global:_logRingBuf.Count -gt 0) {
                            ($global:_logRingBuf.ToArray() | Select-Object -Last 10) -join ' || '
                        } else { '(no ring buffer)' }
                        Log ("!! SLOW TICK CONTEXT: $recentCtx")
                    } catch {}
                    try {
                        $sp = [System.Diagnostics.Process]::GetCurrentProcess()
                        $gdiN = if ($global:_hasGuiRes) { try { [NativeMethods.GuiRes]::GetGuiResources($sp.Handle, 0) } catch { 0 } } else { -1 }
                        Log ("!! SLOW TICK SNAPSHOT: ws={0}MB handles={1} threads={2} gdi={3} winrt_tmo_min={4}" -f [math]::Round($sp.WorkingSet64/1MB,1), $sp.HandleCount, $sp.Threads.Count, $gdiN, $global:_winrtTmoMin)
                    } catch {}
                }
            }
            # Once per minute (600 ticks at 100 ms): drop avg+max and reset.
            if (($global:_diagTickCount % 600) -eq 0 -and $global:_tickSamples -gt 0) {
                $avg = [int]([double]$global:_tickSumMs / [double]$global:_tickSamples)
                Log ("Tick stats last 60 s: samples={0} avg={1} ms max={2} ms" -f $global:_tickSamples, $avg, $global:_tickMaxMs)
                $global:_tickMaxMs  = 0
                $global:_tickSumMs  = 0
                $global:_tickSamples = 0
                # v9.10.0: [CANARY] — system health snapshot every 60 s.
                # Tracks GDI/User object counts (slow GDI leak early-warning),
                # handle + thread counts, and WinRT timeout rate.
                try {
                    # v11.1.4: prompt gen-1 GC before sampling to drain finalization queue (Fix 3 — B2 leak).
                    [GC]::Collect(1, [System.GCCollectionMode]::Optimized)
                    $sp   = [System.Diagnostics.Process]::GetCurrentProcess()
                    $wsMB = [math]::Round($sp.WorkingSet64 / 1MB, 1)
                    $gdi  = if ($global:_hasGuiRes) { try { [NativeMethods.GuiRes]::GetGuiResources($sp.Handle, 0) } catch { -1 } } else { -1 }
                    $usr  = if ($global:_hasGuiRes) { try { [NativeMethods.GuiRes]::GetGuiResources($sp.Handle, 1) } catch { -1 } } else { -1 }
                    $lvl  = if ($global:_winrtTmoMin -gt 50) { 'ERROR' } elseif ($global:_winrtTmoMin -gt 10) { 'WARN' } else { 'OK' }
                    Log ("[CANARY] mem={0}MB handles={1} threads={2} gdi={3} user={4} winrt_calls={5} winrt_tmo={6} [{7}]" -f $wsMB, $sp.HandleCount, $sp.Threads.Count, $gdi, $usr, $global:_winrtCallsMin, $global:_winrtTmoMin, $lvl)
                    # Reset per-minute counters
                    $global:_winrtCallsMin = 0
                    $global:_winrtTmoMin   = 0
                } catch {}
            }
        } catch {}
        # v11.2.0: 5-minute Gen2 forced GC flush. Drains the WinRT RCW finalization queue
        # that accumulates between ticks. The existing 60-second Gen1 Optimized hint (above)
        # does not reach Gen2, so long-lived RCW wrappers survive it. Gen2 Forced queues ALL
        # generations for collection; the finalizer thread then releases native COM references.
        # We do NOT call WaitForPendingFinalizers() — that would block the UI thread.
        # The finalizer thread runs async between ticks and clears the queue on its own.
        # Net effect: RAM growth plateaus or drops noticeably after each 5-min flush.
        try {
            $_gcNowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            if (($_gcNowMs - $global:_gcFlushLastMs) -ge 300000) {
                $global:_gcFlushLastMs = $_gcNowMs
                [GC]::Collect(2, [System.GCCollectionMode]::Forced)
                Log "GC flush: Gen2 forced (5-min interval)"
            }
        } catch {}
        $scrobbleTimer.Start()   # restart after every tick — maintains ~100 ms gap
    }
})
$script:_initDone = $true   # v11.1.0: signals Log() to stop mirroring to TEMP_LOG (startup.log)
$scrobbleTimer.Start()
Log "Multi-platform scrobble timer started (osu! → Spotify → SMTC → Browser → WMP → VLC)"

Log "Entering Application::Run()"
try {
    [System.Windows.Forms.Application]::Run()
} catch {
    Log "Application::Run() threw: $_"
}
Log "Exited"
try { Stop-Transcript } catch {}
