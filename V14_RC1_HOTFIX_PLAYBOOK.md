# V14_RC1_HOTFIX_PLAYBOOK.md

For when an RC1 tester reports something that needs fast-turnaround correction.

## RC2 cut process

1. **Branch from the v14.0.0-rc.1 tag.**
   ```
   git checkout -b hotfix/v14.0.0-rc.2 v14.0.0-rc.1
   ```

2. **Apply the targeted fix.** Keep it surgical. If the change touches more than 3 files OR
   any of the five protected files (tray.ps1, tray_native.cs, launcher.cs, server.js,
   memory.md), stop and reconsider whether this is really a hotfix vs a minor.

3. **Bump InformationalVersion to `14.0.0-rc.2`.**
   - `src\tray.ps1:253`: `$script:APP_VERSION = "v14.0.0-rc.2"`
   - `src\tray.ps1` PatchNotes: prepend a new entry for v14.0.0-rc.2 with what changed
   - `src\server_dotnet\Program.cs:108` and `:159`: UA `MastersFM/14.0.0-rc.2`
   - The build_msi.py pre-release-suffix strip already handles `-rc.2` -> MSI ProductVersion `14.0.0`

4. **Run abbreviated validation:** STEP 5.1, 5.2, 5.4 (smoke), 5.5 (webhook B1-B11), and a 2h
   soak (NOT 12h, NOT 6h -- hotfix urgency). The 2h soak floor is fine because the changeset
   is small by definition (steps 1-2). If 2h shows materially worse memory growth than RC1,
   halt and reconsider.

5. **Tag, push, GitHub Pre-release.**
   ```
   git add -A
   git commit -m "v14.0.0-rc.2: <one-line summary>"
   git tag -a v14.0.0-rc.2 -m "RC2 hotfix: <summary>"
   git push origin hotfix/v14.0.0-rc.2
   git push origin v14.0.0-rc.2
   gh release create v14.0.0-rc.2 --prerelease --title "v14.0.0-rc.2 (Hotfix)" --notes-file RELEASE_NOTES_v14.0.0-rc.2.md "Master's FM Install\Masters-FM-V14.0.0-rc.2.msi"
   ```

6. **Update tester announcement / Discord channel.** Post in `#v14-rc-feedback` with: "RC2 is
   live with a fix for `<symptom>`. Download here, replaces RC1 in place." Include the same
   rollback link to v12.0.1.

## Likely failure modes (pre-staged diagnostics)

### 1. Discord RPC connection failures

**Symptom:** Discord status not updating; `host.log` or `transcript.log` shows
`"discord: ready timeout"` or `"discord: reconnect loop"`.

**Diagnostic:**
```
Get-Content "$env:LOCALAPPDATA\MastersFM\transcript.log" | Select-String -Pattern 'discord' -Context 0,3
```

**Likely root cause area:** Lachee.DiscordRPC reconnect handling. `DiscordRpcService.cs` has
a 30s reconnect loop (ID-33 vs server.js's 5s/10s escalation). If a tester reports
"sometimes Discord doesn't reconnect after sleep/wake", the 30s window may be too long.

**Files:** `src/server_dotnet/DiscordRpcService.cs`, `src/server_dotnet/DiscordRpcThrottle.cs`.

**Rollback option:** if the fix takes >2h to triage, set Discord RPC to disabled in
`config.json`: `{ "discord_rpc": { "enabled": false } }`. The user keeps everything else
working; Discord-side just goes dark.

### 2. Art cascade returns wrong art for ambiguous track titles

**Symptom:** Track shows but album art is for a different song (e.g. searching "Yesterday"
returns The Beatles when the actual track is a different artist's "Yesterday").

**Diagnostic:**
```
curl 'http://127.0.0.1:4242/__debug/art?artist=...&track=...'
curl 'http://127.0.0.1:4242/__debug/source/Deezer?artist=...&track=...'
```

(Note: `/__debug/art` and `/__debug/source/{name}` are still present in v14.0.0-rc.1 because
the brief STEP 4.11 "remove debug endpoints" gate has not yet completed. They will be
removed in stable v14.0.0.)

**Likely root cause area:** `src/server_dotnet/TextNormalization.cs` (cleanArtist /
cleanTrack regex), `src/server_dotnet/ArtSources/DeezerSource.cs` and `ItunesSource.cs`
(search query normalization).

**Rollback option:** A temporary fix is to constrain the cascade -- comment out enrichByTitle
in `WebhookHandler.cs` so the Deezer/iTunes title-search path is bypassed. Falls back to
webhookArt or SMTC source only.

### 3. Webhook deep-merge dropping fields

**Symptom:** Saved presets lose fields after reload (e.g. `font` resets to default after
reopening customize).

**Diagnostic:**
```
$cfg = Get-Content "$env:APPDATA\MastersFM\config.json" -Encoding UTF8
$cfg | Select-String -Pattern '\bfont\b'
```

If the file has a UTF-8 BOM (first 3 bytes `EF BB BF`), `JsonNode.Parse` may misbehave on
later reads.

**Likely root cause area:** `src/server_dotnet/ConfigHandler.cs` (uses `s_utf8NoBom = new
UTF8Encoding(false)` for writes; reads should still be tolerant of BOM presence). Also
`ConfigDeepMerge.cs`.

**Rollback option:** Delete `%APPDATA%\MastersFM\config.json` to force regen from
defaults. Tester loses preset customizations but no hang.

### 4. SSE heartbeat gaps under network jitter

**Symptom:** Overlay shows "disconnected" or stops updating; F12 console shows EventSource
`onerror` after >30s of silence.

**Diagnostic:**
```
$resp = Invoke-WebRequest -Uri 'http://127.0.0.1:4242/events' -UseBasicParsing -TimeoutSec 30
# Watch for ': ping' lines every ~15s
```

**Likely root cause area:** `src/server_dotnet/HeartbeatService.cs` (15s heartbeat). Per-
client `Channel<string>` (SingleReader=true). If a client's `WriteAsync` blocks (slow client
reading), the channel buffer may fill and writes back-pressure into the heartbeat sender.

**Rollback option:** restart the server; the SSE state machine recovers cleanly on reconnect.
Tester can reload the overlay browser tab.

### 5. tray_native PS5.1 load failures on tester machines with older .NET Framework

**Symptom:** tray.ps1 fails at startup with `Add-Type : Could not load file or assembly
'tray_native.dll'`. host.log shows the FATAL line.

**Diagnostic:**
```
& "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -ExecutionPolicy Bypass -File "G:\Project Folder\Master FM\test-ps51-load.ps1"
```

**Likely root cause area:** Stage 5.1's netstandard2.0 target requires .NET Framework
4.7.2+ on the tester's machine for full netstandard2.0 facade support. Some testers may be
on 4.7.1 or older.

**Rollback option:** set `$UseDotnetTrayNative = $false` in `_full_rebuild.ps1` and re-issue
RC2 with the csc.exe-built tray_native.dll. PS5.1 load test PASS confirmed for both paths in
sub-stage 5.5.

### 6. Missing native dependency at runtime

**Symptom:** server.exe (or any .NET 8 binary) PID dies within seconds of launch; port 4242
shows no listener; `server.log` is empty or has only a single line; manual run shows a
`System.IO.FileNotFoundException: Could not load file or assembly '<NuGet name>'` early in
stdout. This is the exact failure mode that surfaced during RC1 STEP 5 (DiscordRPC.dll +
Newtonsoft.Json.dll were missing from the install dir because the build pipeline's foreach
copy and `build_msi.py` FILES list both omitted them). It would have hard-bricked first
install on every tester if it had reached the public ship.

**Diagnostic commands:**
```
# (a) which DLLs landed in the install dir
Get-ChildItem "$env:LOCALAPPDATA\MastersFM" -Filter *.dll | Select-Object Name, Length

# (b) what dotnet publish actually produced
Get-ChildItem "G:\Project Folder\Master FM\dist\server_dotnet_release" -Filter *.dll | Select-Object Name

# (c) manual server start with stdout/stderr captured
$proc = Start-Process -FilePath "$env:LOCALAPPDATA\MastersFM\server.exe" `
    -RedirectStandardError "$env:TEMP\server_err.txt" `
    -RedirectStandardOutput "$env:TEMP\server_out.txt" `
    -PassThru -NoNewWindow
Start-Sleep -Seconds 4
Get-Content "$env:TEMP\server_out.txt"
Get-Content "$env:TEMP\server_err.txt"
```

**Likely root cause area:** the build pipeline omitted a NuGet runtime DLL from the install
dir. There are TWO files that need updating when adding a NuGet runtime dep:

1. `_full_rebuild.ps1` post-publish copy block (e.g. lines ~42-46 for server). Add the new
   DLL filename to the `foreach ($ff in @('server.exe','server.dll',...))` array.
2. `build_tools/build_msi.py` -- add a new `GUID_COMPNN` constant near the existing block,
   and add a `(file, file, GUID_COMPNN)` tuple entry to the relevant `_optional` list (Stage
   4 server uses `_net10_server_dll` conditional block around line ~158).

If only one of those two files is updated, the dotnet publish output COPIES the DLL to root
but the MSI does NOT include it -- so the install dir is missing it and the running binary
crashes.

**Rollback recommendation:** push a hotfix RC2 with the missing DLL added to BOTH files. If
the fix would take >2h to triage and ship, instead push a corrective version.json keeping
testers on the last known good (v12.0.1 or last stable) until RC2 is ready -- testers will
not auto-update because the corrective version.json reverts the manifest target.

**Repeat-prevention rule:** when adding ANY NuGet PackageReference to a .csproj, immediately
update the file copy list in `_full_rebuild.ps1` AND the FILES list in `build_msi.py` in the
same commit. Do not rely on `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`
alone -- it copies to dotnet publish output but does NOT cause the MSI to ship the DLL.

---

### 7. Server crashes / misbehaves with no diagnostic log

**Symptom:** Tester reports server.exe died or behaved badly, but `%LOCALAPPDATA%\MastersFM\
server.log` is empty, missing, or shows only pre-v14 timestamps. The .NET 8 server emits
structured logs to stdout via `Microsoft.Extensions.Logging`, but launcher.cs spawns server.exe
with `ProcessStartInfo.CreateNoWindow=true` and `RedirectStandardOutput=false` (the legacy
Node-server pattern was preserved without re-evaluation). Net result: ALL .NET 8 server output
is discarded -- INFO/WARN/ERROR lines, exception stack traces, the `Application started`/
`Application shutting down` lifecycle events, all gone.

This was the surface that the DiscordRPC.dll near-miss exploited: server died silently with
no log entries because the FileNotFoundException only printed to the dropped stdout. Tester
reports of "server died" or "Discord RPC stopped working" or "/version returns connection
refused" have **no diagnostic surface** in RC1.

**Diagnostic (tester-side workarounds):**

(a) Ask the tester to manually run server.exe from a `cmd` window so stdout is visible:
```
cd %LOCALAPPDATA%\MastersFM
server.exe
```
This will only work if no other server.exe is already bound to port 4242, so they need to
quit the running install first via the tray Quit menu OR by killing MastersFM.exe in Task
Manager.

(b) If the tester has the .NET SDK installed, capture a runtime trace:
```
dotnet trace collect --process-id <pid> --providers Microsoft-DotNETCore-SampleProfiler
```

(c) Windows Event Viewer -> Application log: any unhandled exception that crashes the host
process emits a "Watson" event with a stack trace fragment. Filter Source contains
"server.exe" or ".NET Runtime".

(d) Process Monitor (Sysinternals) capturing file access + thread events around the crash
window can sometimes reveal which assembly load failed (the DLL miss case).

**Root cause area:** `src/launcher.cs` `ProcessStartInfo` for the server spawn block
(approximately lines 360-410, the section commented `// Spawn server.exe`). The fix is two
lines:
```
psi.RedirectStandardOutput = true;
psi.RedirectStandardError = true;
```
Plus a small thread or async loop that reads each line and appends to
`%LOCALAPPDATA%\MastersFM\server-dotnet.log` with simple size-based rotation.

**Why it's not fixed in RC1:** launcher.cs is a protected file (Absolute Rule 1 carry-forward
list). Edits to it require explicit user direction. The shipping change set for RC1 was
intentionally minimum-viable; logging is a quality-of-life improvement, not a ship-blocker.
This deferral is documented as a future-work priority item in V14_RC1_FINAL_REPORT.md.

**Rollback recommendation:** not applicable. Logging absence is not a bug per se -- the .NET
8 server runs fine. It is a diagnostic blind spot. Without it, "server died" tester reports
require manual stdout reproduction (workaround a) before any other diagnostic step. Continue
to investigate any specific RC1 server-misbehavior reports via the four workarounds above.

**RC2 candidate fix:** add the launcher.cs file-redirect change to the v14.0.0-rc.2 cut if
RC1 surfaces multiple "server died, no log" reports. 2-4h to implement + soak. Not blocking
unless triggered.

---

### 8. Auto-update path serving RC1 to general testers

**Symptom:** A tester reports "I clicked update and now I'm on RC1 but I didn't ask for it."

**Diagnostic:**
- Check `%LOCALAPPDATA%\MastersFM\transcript.log` for `Update available: v14.0.0-rc.1`. If
  present, the [version] cast did NOT throw -- which would mean the version-gate logic in
  tray.ps1 changed unexpectedly.

**Likely root cause area:** `src/tray.ps1:5742-5790` (`Poll-UpdateCheck` function). The
RC1 protection is the `[version]` cast strictness; if a future tray.ps1 patch ever softens
this (e.g. switches to a regex or a tolerant comparator), an old version of `version.json`
content could now flow through to old testers.

**Rollback option:** push a corrective version.json to main with a clean numeric version and
no SemVer suffix. The auto-update path then picks up the corrected version on the next
hourly poll cycle.

## What is NOT a hotfix candidate

If a reported issue requires:

- Modifying any of the five protected files (`tray.ps1`, `tray_native.cs`, `launcher.cs`,
  `server.js`, `memory.md`) for non-version bumps
- Adding a new runtime dependency
- Changing the auto-update protocol
- Re-enabling install_bootstrapper.exe

...then it is NOT an RC2 hotfix. It is either: (a) a stable v14.0.0 ship blocker (defer RC2
in favor of a longer cycle), (b) a Stage 7 / Stage 8 task, or (c) a feature request masquerading
as a bug. Halt and assess before cutting RC2.
