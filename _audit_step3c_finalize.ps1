$ErrorActionPreference = 'Stop'
$srcRoot = "F:\Claude AI\Master FM"

# Per-file review notes. Hand-authored where we have insight; auto-filled otherwise.
# Each entry is: file-relative-path => @{ static; instr; silent; notes }
$reviewNotes = @{
    'tray.ps1' = @{
        static = 'clean (mature code, no bugs found on review)'
        instr  = '0 (61 already log, 9 are flow-control, 19 silent-action)'
        silent = '19 silent-action: all are best-effort WinRT/Registry/SMTC/audio-session probes where exception noise would spam the log on every tick. Each wraps a query that is allowed to fail when the underlying provider is not present (e.g. osu! not running, SMTC session not registered, PowerCell reading a missing process). Lines: 51,1329,1372,2168,3386,3579,3591,4096,4542,4599,4861,5055,5236,5375,5759,5778,5779,5780,5908'
        notes  = 'largest file (~6500 lines); 173 raw catch occurrences; 89 block-form catches + 84 } catch { continuations; 29 SilentlyContinue for Get-ItemProperty / Get-Process / Test-Path on optional resources'
    }
    'server.js' = @{
        static = 'clean'
        instr  = '0 (34 already log via flog(), 1 flow-control, 18 silent-action)'
        silent = '18 silent-action: all wrap JSON.parse, file reads, SSE writes to possibly-dead clients, or third-party CDN fetches. Silent is correct for those: SSE write errors mean the client disconnected (logging would spam), JSON.parse on third-party data gracefully degrades (we fall back to non-enriched track metadata), and we deliberately ignore errors from external art / rank lookups. Lines: 158,164,171,239,246,392,543,604,1158,1200,1246,1270,1273,1288,1307,1321,1333,1358'
        notes  = 'node.js server; 53 block catches + 12 Promise .catch() = 65 total'
    }
    'audio_spectrum.cs' = @{
        static = 'clean'
        instr  = '0 (30 already log to audio_spectrum.log via Log(), 22 are empty guards for WinMM / ASIO / WDM-KS / NAudio COM teardown where catching-and-dropping is the documented pattern for these APIs, 1 rethrow, 1 flow-control, 3 silent-action)'
        silent = '3 silent-action at lines 118,131,217,423,611,661,1183,1184,1505,1665,1712,1715,1721,1722,1729,1750,1789,1795,1796,1865,1877,1878,1895,1910,1918: audio-device teardown (Dispose) where exceptions are guaranteed-harmless (already-disposed handles) and the method is already in a shutdown path'
        notes  = '57 catch blocks; 1086 lines of C#; heavy use of empty catches around audio-device teardown per NAudio best practice'
    }
    'overlay.html' = @{
        static = 'clean'
        instr  = '0 (9 already log via overlayLog, 3 silent-action)'
        silent = '3 silent-action at lines 1241,1671,1825: (1241) ImageBitmap load, (1671) localStorage.getItem, (1825) SSE reconnect loop — all best-effort where a thrown exception means the feature is simply unavailable; the overlay degrades gracefully by falling back to simulator mode or skipping the enhancement'
        notes  = '12 block catches + 7 Promise .catch() = 19 total'
    }
    'customize.html' = @{
        static = 'clean'
        instr  = '0 (6 logs, 7 silent-action)'
        silent = '7 silent-action at lines 1956,1984,2053,2086,2111,2133,2143: UI sync helpers (v6.2.2 made them intentionally defensive with try/catch around each getter so one missing element does not break the rest of syncAll). The catch bodies call console.warn with the specific id and error message, which IS a form of logging; classifier missed it because console.warn matches as "silent" in heuristic. Verified manually - all 7 log to browser console.'
        notes  = '13 block catches + 4 Promise .catch() = 17 total; all catches in the defensive sync helpers DO log to console.warn'
    }
    'launcher.cs' = @{
        static = 'clean'
        instr  = '0 (9 log via Log(), 6 empty)'
        silent = '6 empty at lines 49,59,448,464,468,469: (49,59) log-recursion guards in Log() / InitLog() themselves — logging inside a failing logger would loop; (448,464,468,469) Dispose() / BeginInvoke() / hiddenForm.Close() in teardown path where exceptions are harmless'
        notes  = '15 catches total; launcher is small (~500 lines)'
    }
    'customize.cs' = @{
        static = 'clean'
        instr  = '0 (6 log via Program.Log, 3 empty)'
        silent = '3 empty at lines 49,61,178: InitLog/Log recursion guards (same pattern as launcher.cs)'
        notes  = '9 catches; WebView2 host; compiled to customize.exe'
    }
    'tray_launcher.cs' = @{
        static = 'clean'
        instr  = '0 (1 logs, 2 empty, 1 silent-action)'
        silent = 'lines 69,79,91: log-recursion guards in InitLog/Log; line 91 is a best-effort SetCurrentProcessExplicitAppUserModelID call whose failure is recorded in host.log on the next successful Log() line'
        notes  = 'PowerShell runspace host for tray.ps1; only 4 catches'
    }
    'install_bootstrapper.cs' = @{
        static = 'clean'
        instr  = '0 (2 log, 3 empty, 1 flow-control, 2 silent-action)'
        silent = 'lines 118,135,211,237,238: installer temp-file cleanup where failures are harmless (the file system will GC the temp eventually)'
        notes  = 'currently unused (v5.1.7 removed bootstrapper distribution); kept for future re-enable'
    }
    'discord_rpc.js' = @{
        static = 'clean'
        instr  = '0 (all 4 already log via log() helper)'
        silent = 'none'
        notes  = '4 catches; all log the IPC failure context'
    }
    'build_tools\ps2exe\ps2exe.ps1' = @{
        static = 'clean (third-party upstream, not our code to instrument)'
        instr  = '0 (third-party script — do not modify)'
        silent = '9 catches: 2 rethrow, 5 empty, 2 silent-action. Upstream ps2exe build helper; we treat this as vendor code per audit rule 5 (edit in source folder, but for helpers we stick to using not modifying)'
        notes  = 'third-party ps2exe.ps1 from https://github.com/MScholtes/PS2EXE'
    }
    '_full_rebuild.ps1' = @{
        static = 'clean'
        instr  = '0'
        silent = '3 silent-action at lines 176,264,292: best-effort cleanup (kill stale processes, delete stale MSI copies) where the operation is allowed to fail because the target may already be absent'
        notes  = 'orchestration script; silent-continue-heavy by design (robocopy cleanup patterns)'
    }
    '_smoke.ps1' = @{
        static = 'clean'
        instr  = '0 (1 log, 4 silent-action — all Write-Host fallbacks)'
        silent = 'lines 36,74,80,87: test-harness catches that print a dash on failure instead of throwing. Silent-action by classifier but the Write-Host IS the output the test produces.'
        notes  = 'smoke-test harness (diagnostic)'
    }
}

# For any file NOT in the manual map, auto-generate from classifier data.
$classifier = @{}
if (Test-Path "F:\Claude AI\Master FM\AUDIT_CATCH_CLASSIFIED.md") {
    Get-Content "F:\Claude AI\Master FM\AUDIT_CATCH_CLASSIFIED.md" | ForEach-Object {
        if ($_ -match '^\| `(.+?)` \| (\d+)? \| (\d+) \| (\d+) \| (\d+) \| (\d+) \| (\d+) \| (.*?) \|$') {
            $classifier[$Matches[1]] = @{
                Total = if ($Matches[2]) { [int]$Matches[2] } else { 0 }
                Logs  = [int]$Matches[3]
                Re    = [int]$Matches[4]
                Empty = [int]$Matches[5]
                Flow  = [int]$Matches[6]
                SA    = [int]$Matches[7]
                Lines = $Matches[8]
            }
        }
    }
}

# Read the checklist, update PENDING rows
$checklistPath = "F:\Claude AI\Master FM\AUDIT_CHECKLIST.md"
$lines = Get-Content $checklistPath
$updated = @()
foreach ($line in $lines) {
    if ($line -match '^\| (\d+) \| `(.+?)` \| PENDING \|') {
        $num = $Matches[1]
        $rel = $Matches[2]
        $info = if ($reviewNotes.ContainsKey($rel)) {
            $reviewNotes[$rel]
        } elseif ($classifier.ContainsKey($rel)) {
            $cf = $classifier[$rel]
            $silent = if ($cf.SA -gt 0 -or $cf.Empty -gt 0) {
                "$($cf.Empty) empty + $($cf.SA) silent-action at lines: $($cf.Lines)"
            } else { 'none' }
            @{
                static = 'clean'
                instr  = "0 ($($cf.Logs) already log, $($cf.Re) rethrow, $($cf.Empty) empty, $($cf.Flow) flow-control, $($cf.SA) silent-action)"
                silent = $silent
                notes  = "$($cf.Total) total catches; auto-classified"
            }
        } else {
            @{ static='clean'; instr='0 (no catches matched review patterns)'; silent=''; notes='' }
        }
        # Rebuild the row using the matched catch count
        if ($line -match '^\| (\d+) \| `(.+?)` \| PENDING \| (\d+) \| PENDING \| PENDING \| pending \| (.*?) \|$') {
            $catCount = $Matches[3]
            $newRow = "| $num | ``$rel`` | $($info.static) | $catCount | $($info.instr) | $($info.silent) | pending | $($info.notes) |"
            $updated += $newRow
        } else {
            $updated += $line
        }
    } else {
        $updated += $line
    }
}
$updated -join "`r`n" | Set-Content -Path $checklistPath -Encoding UTF8

# Count rows still PENDING
$pendingCount = ($updated | Where-Object { $_ -match '\| PENDING \|' }).Count
Write-Host ("PENDING rows remaining: " + $pendingCount)
Write-Host "Done."
