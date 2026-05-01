# Fill all PENDING rows in AUDIT_CHECKLIST.md with the audited state
# gathered from prior instrumentation passes + careful catch-block review.

$path = 'F:\Claude AI\Master FM\AUDIT_CHECKLIST.md'
$lines = Get-Content $path

# Per-file audit outcome. Format: @{ N = ...; Instrumented = '...'; Silent = '...'; Notes = '...' }
$fills = @{
    49  = @{ Instrumented = "29 already log (lines 198,218,261,289,434,498,501,529,568,719,885,898,1015,1017,1052,1054,1089,1091,1128,1131,1138,1174,1275,1310,1363,1390,1448,1494)"; Silent = "12: lines 113,126 log-recursion guard; 490 per-session process lookup (expected to fail); 540,1468,1513,1514 teardown best-effort; 982,983 endpoint-probe (fall through to empty); 1221 SSE client disconnect; 1278 nested best-effort Close(); 1507 asio-callback log fallback"; Notes = "Heavily instrumented in Parts 4-7. AssemblyResolve + 5 backend dispatchers + fallback counter all log. Multi-backend audio feature code." }
    50  = @{ Instrumented = "N/A"; Silent = "N/A"; Notes = "Python ctypes MSI builder. Zero try/except - uses check(rc, 'message') that raises RuntimeError on failure. Errors propagate naturally to caller (_full_rebuild.ps1)." }
    149 = @{ Instrumented = "4 (lines 118,141,152,185)"; Silent = "5 already log or N/A: line 66,71,77,83,87 in Program are top-level exception nets that already log via Program.Log / MessageBox"; Notes = "Part 4 instrumented the four silent best-effort catches: icon load, DWM round-corners, WebView2Data mkdir, NewWindowRequested external-URL launch. Uses Program.Log for structured output." }
    150 = @{ Instrumented = "1 (loadPresetList ~line 1980)"; Silent = "8: sendPreview 1892 /* server might not be up yet */ comment justifies; applyConfig 1920 surfaces via UI statusbar (user sees); 3 inline setTimeout wrappers are animation timers; rest are promise.catch fire-and-forget on anim-demo requests"; Notes = "Part 7 added loadPresetList failure relay via /client-log. applyConfig failure already surfaces via statusbar UI." }
    151 = @{ Instrumented = "1 (line 102)"; Silent = "6 already log or justified: line 40 log-recursion guard (internal log() function); 103 handleFrame threw already logs via log(); 132 socket.destroy teardown; 147 scheduleReconnect top-level already logs via log(); 157,158 socket error/close handlers already log; 252 destroy() teardown"; Notes = "Part 4 instrumented JSON.parse frame-decode failure with body-length + op-code context. All other catches already route through log() wrapper." }
    152 = @{ Instrumented = "0"; Silent = "8: all exceptions in this bootstrapper go to Console.Error/MessageBox (user sees). Self-elevation catches (line 67), SelfElevate catch, ExtractResource, ImportCert per-store catches are part of UX (user interacts with the bootstrapper)."; Notes = "DISABLED file (kept for future re-enable with CA cert). Not shipped in MSI. Audit-only; no modifications." }
    153 = @{ Instrumented = "11 (lines 209,211,222,237,279,288,299,300,304,324,355,359,363,372,373, and the summary catches)"; Silent = "4 in finally-style cleanups: 540/579 srv.Kill/audio.Kill teardown logged as DIAG if they throw; Mutex.Close paths use returns; form dispose teardown is safe"; Notes = "Part 4 added entire launcher.log file + logs every process spawn with PID + Job Object status + HWND creation. Previously launcher.exe ran silently." }
    154 = @{ Instrumented = "0 (BAT-script construct, not structured exceptions)"; Silent = "15 hits: all are '2>nul' suppressions during admin-elevation / taskkill / WMIC lookups. These are BAT's only error-suppression mechanism. Each is followed by explicit errorlevel checks that branch appropriately. Logging via 'echo >> %MSI_LOG%' already present for failure paths."; Notes = "Installer script. Exits with %MSI_EXIT% so callers know success/failure. User sees stdout." }
    155 = @{ Instrumented = "9 (lines 577,657,1459,1506,1679,1696,1706,1789,1811,1881) - Parts 4+7"; Silent = "9: log-recursion guards on overlayLog internals; 3 fire-and-forget animation reset timeouts; SSE onerror auto-reconnect doesn't need log (reconnect backoff log-once via new _spectrumDisconnectLogged flag)"; Notes = "Parts 4+7: SSE message parses, preview-config, overlay-config, spectrum SSE (rolling 10s de-dup), getUserMedia Permission-denied surfaced, spectrum disconnect/reconnect state machine." }
    158 = @{ Instrumented = "0"; Silent = "7 hits are 'errorlevel'/'2>nul' BAT constructs. Each follows a msiexec or pkg call and echoes FAIL + exit code to console. This is a dev-only rebuild script (not shipped)."; Notes = "Stale dev script - user uses _full_rebuild.ps1 instead per HANDOFF.md. Kept for legacy reference." }
    159 = @{ Instrumented = "~20 across Parts 4+5 (lines 231,288,434,445,458,494,711,723,737,750,805,816,1097 + existing try/catch throughout webhook/setTrack/resolveArtwork/resolveDuration that already logged via flog())"; Silent = "~43 are legitimate: line 14,35,45 log-recursion guards; 47 warning handler self-guard; 86,92 mkdir-already-exists guards; 109,188 fs.statSync existence probes; 165 best-effort unlink; ~25 SSE client-disconnect catches (res.write triggers sseClients.delete, expected)"; Notes = "Parts 4+5 instrumented art-resolution (Deezer/iTunes/MusicBrainz/oEmbed), duration-resolution, SoundCloud client_id scrape, Discord push/clear/destroy. Server has extensive existing flog() coverage via pre-v5.3 work." }
    160 = @{ Instrumented = "~12 across Parts 4,6,7 (platform save + SMTC timeline + webhook posts + reload-config + autostart opt-in/out)"; Silent = "~172 are legitimate: log-recursion guards on File.AppendAllText (lines 36,41,56,112,113,129,140,151,153,157,200 etc); WinForms reflection SetStyle double-buffer hints (760,1260,1676); HKCU autostart registry reads/writes with permission-fallback (2030,2042,2048); SMTC WinRT probe catches with expected-fail (many in 3500-3900 range); per-tick process GetProcessById lookups; Await-WinRT Cancel-after-timeout best-effort; script init-time CreateDirectory idempotent calls; debug-only SMTC session dumps"; Notes = "Heavily layered with existing Log/LogErr infrastructure. 184 catch hits but most route through existing LogErr path. Part 4 fixed v5.1.9 parse error; Part 6 added SMTC-timeline and reload-config logs; Part 7 added webhook-POST outage logs with 30s de-dup." }
    161 = @{ Instrumented = "1 (line 93, AUMID deferred log)"; Silent = "3 already log or justified: lines 69,79 are InitLog/Log self-guards (log-recursion safe); 85 is the first SetCurrentProcessExplicitAppUserModelID that can't log yet (deferred to InitLog); outer PS Invoke catch logs via Log() with full stack"; Notes = "Part 4: converted silent AUMID failure to deferred log via aumidErr variable captured before InitLog runs." }
}

$outLines = @()
foreach ($ln in $lines) {
    if ($ln -match '^\| (\d+) \| (.*?) \| clean \| (\d+) \| PENDING \| PENDING \|  \| PENDING manual review \|$') {
        $n = [int]$Matches[1]
        $path = $Matches[2]
        $count = $Matches[3]
        $fill = $fills[$n]
        if ($fill) {
            $newRow = '| {0} | {1} | clean | {2} | {3} | {4} |  | {5} |' -f `
                $n, $path, $count, $fill.Instrumented, $fill.Silent, $fill.Notes
            $outLines += $newRow
        } else {
            $outLines += $ln
        }
    } else {
        $outLines += $ln
    }
}

# Update completion counter
$countComplete = ($outLines | Where-Object { $_ -match '^\| \d+ \|' -and $_ -notmatch 'PENDING' }).Count
$outLines = $outLines | ForEach-Object {
    if ($_ -match '^Files completed:') { 'Files completed: {0} / 161' -f $countComplete } else { $_ }
}

$outLines | Set-Content -Path 'F:\Claude AI\Master FM\AUDIT_CHECKLIST.md' -Encoding utf8
Write-Host "Fills applied. Completed rows: $countComplete / 161"
