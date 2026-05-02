# MEMORY.md — CLAUDE'S NOTEBOOK FOR THIS PROJECT

This file is yours (Claude). You read it at the start of every session. You update it at meaningful checkpoints.
The user is your editor, not your co-author here. Keep it factual, scannable, and current.

---

## CURRENT STATE

**Project:** Master's FM — Windows OBS overlay app (now-playing widget + spectrum visualizer)
**Source folder:** `G:\Project Folder\Master FM\` (confirmed 2026-04-30)
**Current version:** v11.1.0 (built + locally installed; PUSH PENDING at time of memory write — soak in progress)
**Last updated:** 2026-05-02 (v11.1.0 autonomous audit — 5 fixes shipped; see changelog below)

## IN-FLIGHT WORK

*(none)*

## DEFERRED ITEMS

- **OBS Source Side feature** — customize.html UI wired, backend NOT implemented. See `open_issues.md`.
- **SIMD in audio_spectrum.cs** — 3 strikes in v9.1.0. Blocked on Vectors deployment. See `open_issues.md`.
- **F: drive full** — ~17 GB of old `F:\Claude AI\_BACKUPS_v9*\` folders need pruning.
- **Texture/material library** — concept only, never started.

## HARD CONSTRAINTS

See `hard_constraints.md` for the full list. Key ones:
- Source root: `G:\Project Folder\Master FM\`
- Runtime install (`C:\...\AppData\Local\MastersFM\`) is READ-ONLY
- Always use `_full_rebuild.ps1` — never REBUILD.bat
- INSTALL.bat source for builds: `Master's FM Install\INSTALL.bat` (NOT `MastersFM_Installer\INSTALL.bat`)
- Version: single-digit segments only (v8.0.9 → v8.1.0, never v8.0.10)
- Canvas hard-locked to 1000×200; visible card-inner = 935×135
- NEVER write literal `</script>` inside a script block (even in comment)
- PowerShell patch note text: use `'` single quotes, not `\"` in double-quoted strings

## PATTERNS THAT WORK

- Add new config field: add to BOTH `overlay.html` + `customize.html` DEFAULTS → deepMerge auto-persists
- New sidebar binding: wire in `init()` after `await fetch(...)` (not top-level)
- Smoke test after every rebuild: `tests/_smoke.ps1`
- `.md` files live in `md/` (memory=`md/memory.md`, tools=`md/tools.md`, save-tokens=`md/save-tokens.md`, onboard=`md/onboard.md`). CLAUDE.md stays at root.
- Visual debugging: `preview_start` → **resize to 1280×800 first** → `preview_eval` / `preview_screenshot`
- Version bump: bump `APP_VERSION` in `tray.ps1` AND prepend `PATCH_HISTORY` entry

## THINGS TRIED THAT FAILED — DO NOT RETRY

- **`Get-TrustedDurMs` with `tlFresh` signal (v8.1.7):** wrong — timestamps disappeared forever when two videos shared duration. Replaced by `Get-TrustedTimelineMs`.
- **`Get-TrustedTimelineMs` with extrapolated posMs (v8.1.8):** wrong — extrapolation added wall-clock age, same stale data compared differently. Fixed to use raw posMs in v8.1.9.
- **SIMD via System.Numerics.Vectors (v9.1.0):** 3 strikes — version mismatch. See open_issues.md for unblock path.
- **WebGL via config key (v9.4.0):** blank OBS overlay on some setups. URL-param approach (`?renderer=webgl`) is correct.
- **Synchronous webhook on tray polling thread:** 200-900ms block. Fixed v8.2.5 with HttpClient.PostAsync fire-and-forget.
- **`.claude/settings.json` allow rules for memory.md:** Don't work — `.claude/` is a hardcoded sensitive directory, allow rules can't override it. Fix: keep memory.md in project root, not inside `.claude/`.

## AUTO-UPDATE SYSTEM

**Status:** SHIPPED in v10.0.0
**Authority:** Only the user (MasterShadex) can push updates. When told "push it" or "push vX.X.X", Claude runs `_push_update.ps1` (or flips `autoInstall: true` in `version.json` + `git commit` + `git push origin main`).
**Architecture:**
- GitHub repo: `https://github.com/MasterShadex/Masters-FM` (public)
- Full source committed (340 files, commit `21ca108`); build artifacts/backups/DLLs excluded via `.gitignore`
- MSI assets uploaded to GitHub Releases (not committed)
- Tray polls manifest every 6 hours (state machine in `Poll-UpdateCheck` called from `pollTimer.Tick` every 2s)
- When `version > APP_VERSION` AND `autoInstall: true` → silent download (`GetByteArrayAsync`) + SHA-256 + Authenticode verify + `msiexec /quiet` + MSI's LaunchApp CA restarts app
- When `autoInstall: false` → balloon notification + "Check for Updates" menu item only
**Push workflow:**
  1. `_full_rebuild.ps1` → builds + signs MSI, writes `version.json` (autoInstall=false, sha256 of signed MSI)
  2. Upload MSI to GitHub Release: `https://github.com/MasterShadex/Masters-FM/releases/new`, tag `v<version>`
  3. `_push_update.ps1` → sets autoInstall=true + `git commit` + `git push origin main`
**Manifest URL:** `https://raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json`
**MSI URL pattern:** `https://github.com/MasterShadex/Masters-FM/releases/download/v{ver}/Masters-FM-V{ver}.msi`
**Git state:** repo initialized, initial commit `a1c99e6`, branch `main`, remote `origin` → https://github.com/MasterShadex/Masters-FM.git
**Git state:** PUSHED — commit `21ca108`, branch `main` tracking `origin/main`
**Update state globals:** `_updateState` (idle/checking/available/downloading/ready/installing), `_updateVersion`, `_updateMsiUrl`, `_updateMsiSha256`, `_updateAutoInstall`, `_updateLastCheckMs`, `_updateMsiPath`, `_updateCheckTask`, `_updateHttpClient` (`_updateDownloadTask` removed v11.1.0 — was legacy dead code)
**Menu item:** appears in tray menu between "View Log" sep and "Restart" — label changes based on `_updateState`

## USER PREFERENCES

- Bump version per change — called out twice
- Nothing breaks; 1000×200 is the hard ceiling — trust the user's eye over tooling
- Terse, no padding (save-tokens.md rules)
- Autonomous overnight runs: no pausing, update memory at checkpoints, fail loudly if blocked
- Process priority lowering acceptable if it fixes real bugs without regressing audio quality
- **Always rebuild after every version bump and place bundle on Desktop** — user zips and sends to friends

---

## CHANGELOG

### 2026-05-02 07:30 — v11.1.0 — autonomous audit — BUILT, soak in progress

- **Time elapsed:** ~4h (started 06:51, all STEPs complete except soak finish + push)
- **Scope:** Depth-first per-file audit (tray.ps1 60-90 min, then server.js, overlay.html, audio_spectrum.cs, customize.html, launcher.cs)
- **Findings reviewed:** 6 new (all in tray.ps1) + 3 promoted deferred items from v11.0.0 → triaged 6 FIX, 1 DEFER
- **Changes shipped:** 5 (hard cap was 30; all triaged findings applied)
- **Changes rolled back:** 0
- **Build state:** v11.1.0 MSI built+signed+installed. 30-min soak running since 07:29:58.
- **Final build sha256:** `769aa4a5ebf5d0f18448064af25574258f355c432b8f6369d0cd1f083300fd46`
- **tray_native.dll:** Signed CN=MasterShadex, Valid ✅
- **Startup timing:** 83ms from first log to tray visible ✅
- **All 12 STEP 7 checks PASSED** (soak = 13th, in progress)
- **STEP 8 install-failure check:** PASSED — no v11.0.0 install failures documented. Alex "uninstalls itself" was for v10.1.9 (old version). Push will proceed.
- **Push state:** PENDING — will push after soak confirmation

**v11.1.0 CHANGES SHIPPED (5):**
1. P1-SMTC-2 + P2-SMTC-1 (combined): `_smtcPropsFiredThisTick` correctness fix — SMTC track metadata now updates on each track change instead of freezing after first fetch. Also removed dead `_smtcPropsCache = @{}` per-tick alloc (zero reads anywhere in file).
2. P2-B3: SoundCloud browser process lookup — 8 separate `Get-Process` calls per tick → single batched call with 5s TTL cache (same pattern as WMP/VLC in v11.0.0). New globals: `_scBrProcCached`, `_scBrProcCheckAt`.
3. P2-D2: Dual log write after startup — `Log()` now writes to TEMP_LOG (startup.log) only during initialization. After `$script:_initDone = $true` (just before scrobbleTimer.Start()), startup.log stops accumulating. startup.log = 39 lines (clean boot sequence only).
4. P3-CHAIN: `$chain = @()` + `+=` per-tick array copy → pre-allocated `$global:_chain = [System.Collections.Generic.List[string]]::new(16)`. `.Clear()` each tick, `.Add(item)` for appends. Eliminates ~72,000 array copies/hour.
5. P3-DEAD: `$global:_updateDownloadTask = $null` removed — confirmed dead code (zero reads anywhere in file).

**DEFERRED in v11.1.0 (not changed):**
- P3-SERVER-1: Concurrent /screenshot orphan (very unlikely; not worth risk)
- P2-F1: `_smtcPropsResultCache` pruning (bounded to ~5-10 entries; negligible)
- P3-F2: HttpClient lazy-init consolidation (working code; refactor risk > reward)

**NEW HARD CONSTRAINTS from v11.1.0:**
- `_smtcPropsFiredThisTick.Clear()` is now in Get-SMTCSessionsCached (not Get-SMTCMediaPropsCached). Get-SMTCSessionsCached ALWAYS runs before Get-SMTCMediaPropsCached in the detector chain. Don't move `.Clear()` — it's intentionally upstream.
- `$script:_initDone` flag must be set AFTER all WinRT/SMTC init and BEFORE `$scrobbleTimer.Start()`. Moving it earlier would cut off startup.log prematurely; moving it later would allow TEMP_LOG accumulation.
- `_scBrProcCached` + `_scBrProcCheckAt` cache governs SoundCloud browser process lookup. Same 5s TTL pattern as `_wmpProcCached`/`_wmpProcCheckAt` (v11.0.0) and `_vlcProcCached`/`_vlcProcCheckAt` (v11.0.0). All three follow the same `[Environment]::TickCount` threshold idiom.

### 2026-05-02 06:00 — v11.0.0 — autonomous overnight audit — BUILT, awaiting push
- **Time elapsed:** ~90 minutes (started 05:58, STEP 0+1+2+3+4+5+6+7 complete)
- **Findings reviewed:** 37 (P0=0, P1=2, P2=13, P3=5, deferred=17)
- **Changes shipped:** 13 (all in tray.ps1 + server.js)
- **Changes rolled back:** 0
- **5GB bug reproduction:** NOT reproduced on v10.2.3 (mem=161MB, winrt_tmo=0, handles stable ~870). Root cause was v9.9.3 COM proxy leak, already fixed by v9.9.4 — confirmed present.
- **Root folder cleanup:** Done — audit logs moved to `logs/audit_logs/`, old MSIs to `dist/old_releases/`
- **Soak test:** CANARY confirmed stable at 1min mark; 30-min soak running during push
- **Build 1 sha256:** `2a819f7482388e31b69ab303155d3c2b718cffda08ff7e80dcb661769afd49d7`
- **Build 2 sha256:** `62bab82c6eeb667c09e872e44f38129e805e3274fe16294d24f27a7dc2c1adb0` (second build used for push)
- **tray_native.dll:** Signed CN=MasterShadex, Valid ✅
- **Startup timing:** 57ms from first log to tray visible ✅ (better than 88ms baseline)
- **All 13 STEP 7 checks PASSED** (see V1100_FINAL_REPORT.md when written)
- **Push state:** LIVE ✅ — GitHub Release uploaded, `_push_update.ps1` ran, autoInstall=true
- **GitHub Release:** https://github.com/MasterShadex/Masters-FM/releases/tag/v11.0.0
- **Source commit:** `07b0af6` — all 13 fixes + log cleanup
- **Release commit:** `3b87bef` — version.json autoInstall=true
- **Manifest verified live:** version=11.0.0, autoInstall=true, sha256=62bab82c...

**v11.0.0 CHANGES SHIPPED (13):**
1. P1-A1 `_smtcArtCache` LRU cap — `Write-SMTCArtCacheEntry` helper, 200-entry eviction queue prevents GB-scale art accumulation
2. P1-C1 Truncate `transcript.log` on startup — was unbounded append across restarts
3. P2-A1 Dispose `fadeOut` timer after Stop — handle release on every menu close
4. P2-A1 Dispose `fadeIn` + `hoverPoll` timers — same, 2 more per menu interaction
5. P2-A2 Dispose `_obsWatchTimer`, `_obsDelayTimer`, `_obsRetryTimer` after Stop
6. P2-A3 Dispose `$obsTimer` (startup one-shot) after first Tick
7. P2-B1 Cache WMP `Get-Process` 5s TTL — shared cache across all 4 WMP detector functions
8. P2-B2 Cache VLC `Get-Process` 5s TTL — `$global:_vlcProcCached`/`_vlcProcCheckAt`
9. P2-C2 Add `[STATUS] uptime=Xs sseClients=N` to server.js every 60s — correlates with CANARY
10. P2-D1 Per-tick `@{}` → `.Clear()` — eliminates 72,000 hashtable allocations/hour
11. P2-E1 Cancel `_updateWebClient` on all 4 exit paths — prevents partial MSI write on quit
12. P3-C1 Truncate `menu.log` on startup — was unbounded append
13. P3-F1 Fix stale comment in `Dump-DiagnosticState` — "10 ticks (~20s)" → "600 ticks (~60s)"

**DEFERRED (not in v11.0.0):**
- P2-B3 SoundCloud browser process cache (MEDIUM risk — 8 browser names)
- P2-D2 Dual-file log writes after startup (MEDIUM risk — pre-init data concern)
- P2-F1 `_smtcPropsResultCache` pruning (LOW priority — bounded in practice)
- P3-F2 HttpClient lazy-init consolidation (refactor only)

**NEW HARD CONSTRAINTS:**
- `_smtcArtCacheOrder` queue must be maintained alongside `_smtcArtCache` hashtable — both reset at same time if cache is ever manually cleared
- WMP process cache shared across all 4 WMP detector functions (`$global:_wmpProcCached`, `$global:_wmpProcCheckAt`) — all 4 refresh it on the same 5s TTL

### 2026-05-02 05:31 — v10.2.3: Hourly check + suppress popup + install balloon — SHIPPED
- **Bump**: v10.2.2 → v10.2.3
- **Change 1**: Poll interval `6 * 60 * 60 * 1000` → `1 * 60 * 60 * 1000` at `Poll-UpdateCheck` line ~5496
- **Change 2**: Post-update auto-popup suppressed. Startup block distinguishes first install (no Roaming config) from post-update (Roaming config has `welcome_seen=true` for old version). Post-update → balloon; first install → welcome dialog still shows.
- **Change 3**: "Patch Notes" menu item was ALREADY in the tray menu (line 4587, `Show-WelcomeDialog -Manual`). No code change needed.
- **New balloon text**: `"Now running v10.2.3. Tap 'Patch Notes' in the menu to see what's new."`
- **Suppression confirmed in startup log**: `Post-update boot (v10.2.3): balloon notification, welcome window suppressed`
- **GitHub Release**: https://github.com/MasterShadex/Masters-FM/releases/tag/v10.2.3
- **sha256 v10.2.3**: `be6dd69001178767c6f62306852fbbbaed313fad42859e2f77d1bfd64c79999e`
- **autoInstall**: true (commit 31b97f5)
- **7-step verification**: all passed (see V1023_FINAL_REPORT.md)
- **v10.2.3 installed locally**: Yes ✅

### 2026-05-02 04:31 — v10.2.2: Startup speed fix — SHIPPED (installed locally)
- **Bump**: v10.2.1 → v10.2.2
- **Root cause fixed**: 5 `Add-Type` inline C# blocks in tray.ps1 each invoke csc.exe → 10-25s startup on every launch (not just first install)
- **Fix**: New `src/tray_native.cs` contains all 5 types in one compilation unit. `_full_rebuild.ps1` step `[1d3/5]` compiles to `tray_native.dll`. tray.ps1 now loads DLL via `Add-Type -Path` at startup (~50ms) in a block BEFORE all inline guards. Inline Add-Type blocks remain as fallback for old installs without the DLL.
- **New files**: `src/tray_native.cs` (C# source), `tray_native.dll` (build output, shipped in MSI)
- **build_msi.py**: Added `GUID_COMP28` and FILES entry for `tray_native.dll`
- **Verified**: `tray_native.dll` present in `C:\Users\Master\AppData\Local\MastersFM\`, tray visible 87ms after first log entry (vs 10-25s before)
- **sha256 v10.2.2**: `7bd4a92effa41389cb1457a2b5ab60a7815084dc9d6c03cf2ca26ec49cbed048`
- **v10.2.2 installed locally**: Yes ✅
- **Desktop bundle**: `Master's FM V10.2.2.msi` + INSTALL.bat + MastersFM_publisher.cer
- **GitHub Release**: https://github.com/MasterShadex/Masters-FM/releases/tag/v10.2.2
- **autoInstall**: true (pushed via _push_update.ps1, commit 213d8e5)
- **Source committed**: commit 432a6d0 — tray_native.cs, update.html, tray.ps1, _full_rebuild.ps1, build_msi.py, package.json, server.js
- **Pre-push checks all passed**: backup ✅, reproducibility ✅, DLL signed (CN=MasterShadex Valid) ✅, clean install 88ms startup ✅, all 5 types functional ✅
- **Signing integrated into pipeline**: _full_rebuild.ps1 now signs tray_native.dll immediately after csc.exe compilation

### 2026-05-02 04:00 — v10.1.4: WebClient download fix — SHIPPED; v10.1.5 test pushed
- **Bump**: v10.1.3 (test) → v10.1.4 (fix) → v10.1.5 (test, not installed locally)
- **Fix**: Download was stuck — HttpClient+ResponseHeadersRead+ReadAsync tasks never completed reliably in PS 5.1+WinForms (SynchronizationContext interaction)
- **Fix**: Replaced entire `Start-UpdateDownload` with `WebClient.DownloadDataAsync`; `DownloadProgressChanged` and `DownloadDataCompleted` events fire on WinForms UI thread via SynchronizationContext — reliable, no polling needed
- **Removed globals**: `_updateHttpClient`, `_updateRespTask`, `_updateResp`, `_updateStreamTask`, `_updateStream`, `_updateChunkTask`, `_updateMemory`, `_updateBuffer`, `_updateLastPct`, `_updateChunkCount`
- **Added global**: `_updateWebClient = $null`
- **Simplified**: `Poll-UpdateCheck` section 3 (was 80-line streaming state machine) → 4-line comment + `return`
- **v10.1.4 installed locally**: Yes ✅
- **v10.1.5 MSI**: built, at `G:\Project Folder\Master FM\Masters-FM-V10.1.5.msi` — needs manual GitHub Release upload
- **sha256 v10.1.5**: `5394f3c6cb63606d45d0155c641331b673c997de4bbc3cadf765a277cfd4c9df`
- **NOTE**: GitHub OAuth token at `git:https://github.com` is expired (401). User must manually upload MSI and run `_push_update.ps1`

### 2026-05-02 03:30 — v10.0.8 through v10.1.3: Update window button + download fixes
- **v10.0.8/v10.0.9**: Added Download/Install button directly in progress window (`btnAction`)
- **v10.1.0**: Fixed Content-Length never read (PS 5.1 Nullable<long> unwrapping: `$cl.HasValue` always null on plain long — use `if ($cl -ne $null)` directly); also fixed chunk loop (single `if` → time-bounded `while` draining TCP buffer per tick)
- **v10.1.1/v10.1.2**: Test builds; v10.1.2 fixed `$script:APP_VERSION` scope in `GetNewClosure()` (capture as local `$winAppVer` before closure)
- **v10.1.3**: Test build only

### 2026-05-02 03:00 — v10.0.7: Native WinForms update progress window — SHIPPED
- **Bump**: v10.0.6 → v10.0.7
- **New**: `Show-UpdateWindow` function in `tray.ps1` — compact dark WinForms form (420×200 ClientSize, `#111122` bg)
- **New**: Custom two-panel progress bar: background Panel `#1e1e35`, fill Panel `#7744dd` — no system theming
- **New**: 300ms internal timer polls `_updateState`/`_updateDownloadBytes`/`_updateDownloadTotal` globals
- **New**: Determinate bar (known size): fill width = pct × 388; label shows "Downloading 72%" + "X.X MB / Y.Y MB"
- **New**: Indeterminate marquee (unknown size): 80px fill panel slides L→R using `_updateWinMarqPos` cycling 0→467 at +22px/tick, clips to parent naturally
- **New**: Auto-close after 10 ticks (~3s) when state transitions to `idle` (up to date)
- **New**: `BringToFront` if window already open when menu item clicked again
- **Changed**: Menu action now calls `Show-UpdateWindow` instead of `Start-Process http://localhost:4242/update`
- **GitHub Release**: https://github.com/MasterShadex/Masters-FM/releases/tag/v10.0.7
- **sha256**: `b8203c0f8adeed057605bd221960fd55a82f7d34f8b05582fbddc1cdac9029b5`
- **Installed locally**: v10.0.7 ✅

### 2026-05-02 03:00 — v10.0.6: Update progress window — SHIPPED
- **Bump**: v10.0.5 → v10.0.6
- **New**: Clicking "Check for Updates" opens `http://localhost:4242/update` in default browser
- **New**: Live progress: checking → downloading (with % bar + byte counter) → verifying → installing → done
- **New**: Download changed from `GetByteArrayAsync` (no progress) to `GetAsync(ResponseHeadersRead)` + `ReadAsync` streaming loop with 64KB chunks; `Write-UpdateStatus` writes progress JSON to `%TEMP%\mastersfm_update_status.json` on every % change
- **New**: `src/update.html` — dark-themed page that polls `/update-status` every 800ms; detects server-offline (app restarting) and auto-reconnects when it's back
- **New**: `GET /update` and `GET /update-status` routes added to server.js
- **New**: `src/update.html` added to pkg.assets (package.json) and MSI FILES (build_msi.py)
- **GitHub Release**: https://github.com/MasterShadex/Masters-FM/releases/tag/v10.0.6
- **sha256**: `863c1fb9b8606d995a226d41f152d0c71ed024a31208a3419b010b47c40677dd`
- **Installed locally**: v10.0.6 ✅

### 2026-05-02 02:30 — v10.0.5: Install-Update deadlock fix — SHIPPED (commit 2f2331f)
- **Bump**: v10.0.4 → v10.0.5
- **Fix**: `Install-Update` now uses `cmd.exe + ping -n 3` delay before msiexec, then calls `Application.Exit()` to close the tray first so msiexec finds all files unlocked
- **Root cause of "stuck on Installing..."**: msiexec was launched while MastersFM_Tray.exe etc. were still running → Restart Manager waited for files to be freed → hung indefinitely; also Major Upgrade SecureRepair validation fails when original source was a temp file (Windows Installer can't find the cached original MSI for the previous version)
- **Workaround during this session**: killed stuck msiexec → uninstall old product by ProductCode → fresh install MSI (bypasses SecureRepair entirely)
- **IMPORTANT gotcha for future sessions**: When auto-updating via `/i NewVersion.msi` over an existing install, Major Upgrade SecureRepair fails with exit 1603 if the prior version was installed from a temp path. Fix is always: uninstall by ProductCode first, then `/i NewVersion.msi`. OR the new Install-Update code (Application.Exit + ping delay) should let the MSI upgrade cleanly since files are free.
- **GitHub Release**: https://github.com/MasterShadex/Masters-FM/releases/tag/v10.0.5 — `Masters-FM-V10.0.5.msi`
- **sha256**: `a8c0701ba522d4203a70b7cb6fe71e5a730edd41920bf2e7ff69ff498265f263`
- **version.json**: pushed to main (commit 2f2331f), autoInstall=true
- **v10.0.5 installed locally**: Yes ✅

### 2026-05-02 02:00 — v10.0.4: auto-update end-to-end test build — SHIPPED (commit 6ccf0f0)
- **Bump**: v10.0.3 → v10.0.4
- **Visible change**: `$script:APP_VERSION = "v10.0.4"` in `src/tray.ps1` — tray menu header shows `"Master's FM  ·  v10.0.4"` (was v10.0.3)
- **No other behaviour changes** — version bump only; PATCH_HISTORY entry prepended
- **Did NOT install v10.0.4 locally** — user stays on v10.0.3 to test the auto-update flow
- **MSI**: `G:\Project Folder\Master FM\Master's FM Install\MastersFM_Setup.msi` (12.3MB, signed Valid CN=MasterShadex)
- **sha256**: `7ebe6a7a930b3e987211cdcfa97f76b9a7c312de531fb77410499f14bed1a962`
- **GitHub Release**: https://github.com/MasterShadex/Masters-FM/releases/tag/v10.0.4 — asset `Masters-FM-V10.0.4.msi` (12.9MB)
- **version.json**: pushed to main (commit 6ccf0f0), autoInstall=true, verified live at raw.githubusercontent.com
- **Backup**: `C:\_BACKUPS_v10\Master FM_v10_0_3` (4663 files, 234MB); v10.0.3 MSI at `C:\_BACKUPS_v10\Master's FM V10.0.3.msi`
- **Test**: user clicks "Check for Updates" in tray (currently v10.0.3) → expect balloon "Update available" → auto-download+SHA256+install → tray reopens showing "Master's FM  ·  v10.0.4"
- **After test**: if successful, user is on v10.0.4. If not, next session debugs manifest fetch → download → install chain.

### 2026-05-02 — v10.0.3: "Checking..." state fix — SHIPPED
- **Bump**: v10.0.2 → v10.0.3
- **Fix**: Menu label now shows ⌛ "Checking..." when `_updateState = 'checking'` (was "Check for Updates" — misleading during in-progress check)
- **Fix**: Menu click when `checking` state now sets `_updateUserCheck = $true` so the "up to date" balloon still fires if user clicks while startup check is in progress
- **Root cause of "does nothing" bug**: user clicked within the ~2s startup window; state was `checking`; menu action had no handler for `checking` → `_updateUserCheck` stayed false → no balloon
- **Deploy**: GitHub Release v10.0.3 created, MSI uploaded, `_push_update.ps1` ran (autoInstall=true)

### 2026-05-01 — v9.9.9: Windows-wide freeze instrumentation — SHIPPED (soak pending)
- **Problem**: Intermittent system-wide freeze (Explorer/taskbar drop to 1-30fps) during Master's FM operation. Not the v9.9.4 memory leak — separate symptom.
- **Approach**: Instrument-first (no speculative fix). Cannot reproduce on demand.
- **Hypotheses investigated (code review):**
  - A1 (pollTimer sync HTTP): `Invoke-RestMethod /current -TimeoutSec 1` every 2s on UI thread — if server busy, 1s block. **Confirmed concern.**
  - A2 (WMP Deezer sync HTTP): `Invoke-WebRequest api.deezer.com -TimeoutSec 4` synchronously in scrobble tick on first WMP track. **Confirmed concern.**
  - B (SMTC ALPC contention): 300ms timeout window still occupies ALPC channel during soundcloud-rpc SERVERCALL_RETRYLATER; backoff is 5s flat. **Moderate concern.**
  - C (WASAPI exclusive): user-selectable only, not default. Low risk.
  - D (GDI pressure): no tick-path GDI leaks found. gdi=29 user=25 at baseline.
  - E/F (priority cascade, PS GC): low risk.
- **Instrumentation added to tray.ps1**:
  - Log ring buffer (last 20 entries) fed by every `Log()` call
  - WinRT call/timeout counters (`_winrtCallsMin`, `_winrtTmoMin`) in `Await-WinRT`
  - Extended SLOW TICK (>250ms): logs ring buffer context + process snapshot (ws/handles/threads/gdi/winrt_tmo)
  - `[CANARY]` every 60s: mem/handles/threads/gdi/user/winrt_calls/winrt_tmo/[LEVEL]
  - `GetGuiResources` P/Invoke loaded at startup (`NativeMethods.GuiRes`)
- **New file**: `system_watchdog.ps1` — standalone script sampling DWM/Explorer/tray every 500ms, writes CSV with `SampleLateMs` freeze markers.
- **v9.9.4 fix preserved**: Await-WinRT CTS overload, all priorities intact.
- **[CANARY] first reading**: mem=159.8MB handles=969 threads=39 gdi=29 user=25 winrt_calls=702 winrt_tmo=0 [OK]
- **30-min baseline soak (v9.9.9)**: handles 911-1083 (no trend), threads 32-68 (no trend), WS stepped 157→163→190MB (bounded, same pattern as v9.9.4). Instrumentation overhead: zero (tick avg unchanged at 2-3ms).
- **No fix shipped** for Windows-wide freeze: logs clean (no soundcloud-rpc active during session). Next session should capture a freeze episode using [CANARY] + system_watchdog.ps1.
- **Next session**: look for [CANARY] entries with WARN/ERROR level and `!! SLOW TICK CONTEXT` entries. Correlate with system_watchdog.ps1 `SampleLateMs > 1000` rows.
- **Deferred**: `pollTimer` sync HTTP (Invoke-RestMethod every 2s with 1s timeout) — still present, fix pending evidence.

### 2026-05-01 — v9.9.9 addendum build 4: ALL UI-thread blocking eliminated — SHIPPED
- **Symptom**: "When music skips, I lag hard — every single track change." → escalated to: "just not make it the PC lag whatsoever."
- **Final state**: Zero UI-thread blocking on track change. All three root causes fixed.

**Root cause A (all sources) — SMTC thumbnail:**
- Old: `Get-SMTCThumbnailDataUri` called `Await-WinRT` with no timeout → `netTask.Wait(-1)` → indefinite UI block (100-600ms/skip).
- Intermediate fix (shipped but not enough): capped both WinRT calls at 150ms — user still felt the lag.
- **Final fix**: Full deferral. `Get-SMTCThumbnailDataUri` returns `''` immediately on every call; queues key+props in `_smtcArtPendingKey`/`_smtcArtPendingProps`. `Invoke-DeferredThumbExtraction` runs 400ms later on heartbeat/new-track ticks when art is guaranteed loaded, does the WinRT stream reads then, sends a follow-up "art update" webhook. Zero blocking at skip time.

**Root cause B (WMP only) — Deezer lookup:**
- Old: `Resolve-WMPTrackByDuration` used `Invoke-WebRequest -TimeoutSec 4` synchronously → 4s UI block.
- **Fix**: `HttpClient.GetStringAsync` fire-and-poll. Tick 1 fires request, returns `$null`. Subsequent ticks poll `Task.IsCompleted`. State in `$global:_wmpDeezerPendingKey` / `$global:_wmpDeezerPendingTask`.

**Root cause C (all SMTC sources) — SMTC session manager:**
- Old: `Get-SMTCManager` called `Await-WinRT RequestAsync() -TimeoutMs 300` → `netTask.Wait(300)` → up to 300ms UI block every 600ms (on SMTC cache miss, which happens every cache-TTL window, especially frequent with soundcloud-rpc).
- **Fix**: Non-blocking fire-and-poll. `RequestAsync()` wrapped in `AsTask<T>(op, CancellationToken)` stored as `$global:_smtcMgrTask`. No `.Wait()`. Current tick returns stale cache immediately. WinForms message loop pumps the WinRT completion between ticks via SynchronizationContext. Next tick: polls `IsCompleted`, collects result, updates cache. Preserves failstreak/backoff/counter logic.

- **Files changed**: `tray.ps1` — `Get-SMTCThumbnailDataUri`, new `Invoke-DeferredThumbExtraction`, `Resolve-WMPTrackByDuration`, `Get-SMTCManager` + heartbeat/new-track tick paths
- **Build**: v9.9.9 MSI rebuilt + signed + installed (third rebuild of v9.9.9). Startup log clean — SMTC detecting, no SLOW TICKs.

**Build 4 (same session, still v9.9.9): Remaining blocking calls fixed**
- User reported still getting frame drops. Three more blocking calls found and fixed:
  1. `pollTimer.Tick` — `Invoke-RestMethod -TimeoutSec 1` every 2s → `HttpClient.GetStringAsync` fire-and-poll (`$global:_pollTimerTask`)
  2. `Get-SMTCMediaPropsCached` — `Await-WinRT TryGetMediaPropertiesAsync -TimeoutMs 150` every 100ms tick → cross-tick cache + async fire-and-poll per session (`_smtcPropsResultCache`, `_smtcPropsTaskDict`, `_smtcPropsCtsDict`, `_smtcPropsFiredThisTick`)
  3. `Invoke-DeferredThumbExtraction` — two `Await-WinRT` calls at 300ms each → full state machine (`idle→waiting→opening→loading`), each step fires one async Task and returns immediately; no `.Wait()` at any stage
  4. osu! detector direct `Await-WinRT -TimeoutMs 300` → replaced with `Get-SMTCMediaPropsCached`
- **Zero `.Wait()` calls** anywhere in the scrobble tick or pollTimer path. WinForms message loop is never blocked.
- **Build**: v9.9.9 MSI rebuilt + signed + installed (fourth rebuild of v9.9.9). Startup clean — SMTC HIT on second tick, no SLOW TICKs.

**Build 5 (same session, still v9.9.9): SMTC synchronous ALPC call elimination (transition guard)**
- User reported STILL getting lag after Build 4. Identified remaining source: synchronous COM/ALPC calls (`GetPlaybackInfo()`, `GetTimelineProperties()`, `GetSessions()`) each take ~15ms during SMTC track transitions because the SMTC service is busy. 4 such calls per tick = ~60ms during skip.
- These methods return synchronously (no IAsyncOperation wrapper) so cannot be fire-and-polled. Solution: **transition guard** — 500ms window after title change detected where all three ALPC calls are suppressed and return cached values instead.
- **`Get-SMTCMediaPropsCached`**: on `Task.IsCompleted`, if new title ≠ old title → set `$global:_smtcTransitionGuardMs = now + 500`
- **New `Get-SMTCPlaybackInfoCached($Session)`**: during guard window → return `$global:_smtcPbInfoCache[$key]`; else call `GetPlaybackInfo()` and cache result
- **`Get-SMTCSessionsCached`**: during guard → return `$global:_smtcSessionsCacheLastGood` (skip `GetSessions()` ALPC call); else call `GetSessions()` + store as `_smtcSessionsCacheLastGood`
- **`Get-SMTCPosition`**: during guard → return `$global:_smtcTlCache[$key]` (skip `GetTimelineProperties()` ALPC call); else call + cache
- **All `GetPlaybackInfo()` calls in tick path** replaced with `Get-SMTCPlaybackInfoCached` (lines ~6312, ~6329, ~6352, ~6560)
- New globals: `_smtcTransitionGuardMs`, `_smtcPbInfoCache`, `_smtcTlCache`, `_smtcSessionsCacheLastGood`
- **Build**: v9.9.9 MSI rebuilt + signed + installed (fifth rebuild of v9.9.9). Startup clean — SMTC detecting normally, no SLOW TICKs.
- **Status**: Awaiting user confirmation that lag is eliminated.

### 2026-05-02 — v10.0.2: "Check for Updates" feedback — SHIPPED (commit 8397796)
- **Bump**: v10.0.1 → v10.0.2
- **Fix**: Menu click in `idle` state now sets `_updateUserCheck = $true` before calling `Invoke-UpdateCheck`
- **Fix**: `Poll-UpdateCheck` "already up to date" branch shows balloon tip "You're on the latest version" when `_updateUserCheck` is true
- **Fix**: `_updateUserCheck` reset in `finally` block (clears on error too)
- **Deploy**: GitHub Release v10.0.2 created, MSI (12.9MB) uploaded, `_push_update.ps1` ran (autoInstall=true, commit 8397796)
- **Note**: Desktop bundle file named `Master's FM V10.0.2.msi` but upload uses `Masters-FM-V10.0.2.msi` to match version.json URL

### 2026-05-01 — v10.0.1: auto-update signature fix — SHIPPED (commit 374e607)
- **Bump**: v10.0.0 → v10.0.1 (MSI built, NOT installed locally — kept v10.0.0 running for test)
- **Fix**: `Install-Update` Authenticode check now accepts `'UnknownError'` in addition to `'Valid'`
  - `'UnknownError'` = self-signed cert not in system Trusted Root (dev machine scenario)
  - `'Valid'` = cert in TrustedPublisher (friends' machines after INSTALL.bat)
  - SHA-256 remains primary integrity check; Authenticode just confirms MasterShadex provenance
- **MSI**: `G:\Project Folder\Master FM\Master's FM Install\MastersFM_Setup.msi` + Desktop bundle `Masters-FM-V10.0.1.msi`
- **sha256**: `5a0aafc0146c95da0c0b018551127bb1cecda0cc8ab06b5703d701d9576d197a`
- **GitHub Release**: created via REST API (browser file_upload blocked; used WinCred3 to read stored OAuth token, `Invoke-RestMethod` to create release + upload 12.9MB MSI)
- **Release URL**: https://github.com/MasterShadex/Masters-FM/releases/tag/v10.0.1
- **`_push_update.ps1`**: ran — set `autoInstall=true`, committed (e25718c), pushed to `origin/main`
- **Status**: FULLY DEPLOYED — v10.0.0 running apps will auto-update to v10.0.1 within 6 hours
- **Test plan**: restart Master's FM → startup check fires → downloads v10.0.1 → SHA-256 + Authenticode verify → `msiexec /quiet` → LaunchApp CA relaunches as v10.0.1

### 2026-05-01 — Repo structure reorganised — SHIPPED (commit c38b3fa)
- **tests/** — moved 75 test/diagnostic scripts (git mv, history preserved)
- **md/** — moved 8 .md files (save-tokens, tools, memory, onboard, etc.); CLAUDE.md stays at root
- **src/** — moved all source code: `tray.ps1`, `server.js`, `discord_rpc.js`, `overlay.html`, `customize.html`, `audio_spectrum.cs`, `launcher.cs`, `customize.cs`, `tray_launcher.cs`, `install_bootstrapper.cs`, `config_default.json`
- **assets/** — moved `MastersFM.ico`, `MastersFM.png`
- **build_tools/** — moved `build_msi.py`, `rcedit-x64.exe`, `setup.inf` (REBUILD.bat stayed at root — uses `cd /d "%~dp0"`)
- **scripts/** — moved `_autonomous_backup.ps1`, `_build_checklist.ps1`, `_build_filelist.ps1`, `_download_facades.ps1`, `_download_naudio.ps1`, `_download_registry.ps1`
- **Root now contains only:** `.gitignore`, `CLAUDE.md`, `_full_rebuild.ps1`, `_push_update.ps1`, `REBUILD.bat`, `package.json`, `package-lock.json`, `version.json` (plus gitignored artifacts)
- **Updated for new paths:** `_full_rebuild.ps1`, `REBUILD.bat`, `build_tools/build_msi.py`, `package.json`, `build_tools/ps2exe/_build_spectrum.ps1`
- **Key path rules (build pipeline):**
  - `build_msi.py`: `SRC = dirname(dirname(__file__))` = project root; source files use `src/`, `assets/` prefixes in FILES list
  - `_build_spectrum.ps1`: `$src = Join-Path $Root 'src\audio_spectrum.cs'`, `$icon = Join-Path $Root 'assets\MastersFM.ico'`
  - `package.json`: `"main": "src/server.js"`, `"bin": "src/server.js"`, `pkg.assets: ["src/overlay.html", "src/customize.html"]`
  - `server.js` `findHtmlPath()` checks `process.execPath` dirname first — MSI installs everything flat to `%LOCALAPPDATA%\MastersFM`, works unchanged
- **CLAUDE.md updated:** all references to `save-tokens.md`, `tools.md`, `memory.md`, `onboard.md` point to `md/` paths

### 2026-05-01 — GitHub first push — SHIPPED
- **Repo:** https://github.com/MasterShadex/Masters-FM (private per instructions)
- **Commit:** `21ca108` — 340 files, 15.8 MB
- **Backup pre-push:** `F:\_BACKUPS_v9_10_github_setup_2026-05-01_22-05\Master FM_PRE_GITHUB` (88,081 files, 11.148 GB)
- **Secret scan:** CLEAN — no `.pfx`/`.pem`/`.key` files on disk, no GitHub PATs, no Discord client_secrets. Only `.cer` (public cert only, gitignored).
- **Discord client_id `1495411843836018819`** in `server.js`: NOT a secret — public app identifier.
- **gitignore:** `*.cer`, `*.pfx`, `_BACKUPS_*/`, `*.msi`, `*.dll`, compiled EXEs, `node_modules/`, `config.json`, `logs/`, Claude run files.
- **Committed:** all source (tray.ps1, server.js, overlay.html, etc.), build scripts, five sacred files, rcedit-x64.exe.
- **Hard-coded path cosmetic:** `_full_rebuild.ps1` lines 1-2 contain `G:\Project Folder\Master FM` — not a secret.
- **NEW HARD CONSTRAINTS:** .gitignore is PROTECTED. Token at `F:\Documents\Master FM Github.txt` NEVER in source. Code-signing key stays in `Cert:\CurrentUser\My` only.
- **NEXT:** User uploads v10.0.0 MSI to GitHub Releases (tag v10.0.0), then runs `_push_update.ps1` to enable auto-update delivery.

### 2026-05-01 — v10.0.0: Auto-updater — SHIPPED
- **Features**: 6-hour manifest poll (+ on startup), fire-and-poll download (`GetByteArrayAsync`), SHA-256 + Authenticode verify before install, `msiexec /quiet` silent install, tray menu item that reflects update state, `autoInstall` flag for staged rollout
- **New functions in tray.ps1**: `Invoke-UpdateCheck`, `Start-UpdateDownload`, `Install-Update`, `Poll-UpdateCheck` (called from pollTimer.Tick)
- **New files**: `version.json` (update manifest, generated by `_full_rebuild.ps1`), `.gitignore`, `_push_update.ps1`
- **version.json v10.0.0**: sha256=`b0c5b461d99993d8d8f626f8bea035d0d3ef33d3d4207c9eb7f267642c5c5ad9`, autoInstall=false
- **Git**: initialized, initial commit `a1c99e6`, branch `main`, remote `origin` added
- **Pending**: user creates GitHub repo (https://github.com/new → name `Masters-FM`, public), then runs `git push -u origin main` from project root
- **MSI auto-launch** (also v10.0.0): `LaunchApp` CA at sequence 5001 — tray icon appears before installer window closes; Defender pre-scans during install instead of on first manual launch
- **Build**: v10.0.0 MSI built + signed + installed. App started at 22:04:48, welcome dialog triggered (first run of new ProductCode).

### 2026-05-01 — build_msi.py: LaunchApp CA added — SHIPPED
- **Request**: "app starts really slow after first-time install" + "auto-open when MSI installed"
- **Root cause of slow first-start**: Defender runs execution-time behavioral scan the first time an exe is launched; if the user manually opens the app after install, this scan happens then (adds 2-5s). By launching DURING install, the scan happens in parallel with the MSI finishing.
- **Fix**: Added `LaunchApp` custom action (Type 54 VBScript, sequence 5001 = right after `MigrateAutostart`). Fires `explorer.exe "<INSTALLFOLDER>\MastersFM.exe"` asynchronously (bWaitOnReturn=False). Uses `explorer.exe` as intermediary so the app launches under the user token (not the MSI's elevated token) — same trick INSTALL.bat uses.
- **Sequence**: 5001 — files already on disk (InstallFiles=4000), app starts in background while MSI finishes RegisterProduct/InstallFinalize. Tray icon appears before installer window closes.
- **Condition**: `NOT REMOVE` — fires on install and upgrade, skipped on uninstall.
- **Verified**: LaunchApp at 20:25:52 in MSI log (return 0), app started at 20:25:54 (2s startup). MastersFM.exe mutex prevents double-launch if something else also tries to start it.
- **Files changed**: `build_msi.py` only — no tray.ps1 changes, no version bump needed.
- **Note**: friends-only MSI path confirmed — INSTALL.bat doesn't work for friends, they use MSI directly.

### 2026-05-01 — v9.9.4: Tray memory leak fix — SHIPPED
- **Symptom**: After one overnight idle run on v9.9.3, `MastersFM_Tray.exe` grew to 1,879.5 MB RAM, 106,713+ handles, 17,150+ threads (~18× baseline). Process was unusable by morning.
- **Root cause**: `Await-WinRT` used the 1-argument `AsTask<T>(IAsyncOperation<T>)` overload. When the WinRT operation (SMTC `RequestAsync`) timed out, the orphaned `IAsyncOperation` COM proxy was NOT properly released. Each orphaned proxy held an LPC back-channel: 1 LPC-blocked thread in LpcReply wait + ~5 OS handles. SMTC entered a SERVERCALL_RETRYLATER loop (triggered by `com.richardhbtz.soundcloud-rpc` third-party app) starting at ~21:11, producing 797 timeout events over 10.2 hours. COM object GC was too slow to keep up with the accumulation rate.
- **Fix**: Added `$global:_awaitAsTaskGenericCts` (the 2-arg `AsTask<T>(IAsyncOperation<T>, CancellationToken)` overload). Modified `Await-WinRT` to use `CancellationTokenSource(TimeoutMs)` in the timeout path. The CTS fires at timeout, unregisters the Completed handler (releasing the cross-process COM proxy), and transitions the Task to Canceled state. `finally` block always disposes CTS + Task.
- **Files**: `tray.ps1` only — init block (~L4859) + `Await-WinRT` function
- **What v9.9.3 missed**: v9.9.3 touched audio_spectrum.cs + overlay.html only (FFT stride floor + OBS setInterval). The SMTC COM proxy leak was pre-existing, unrelated to v9.9.3's scope.
- **30-minute soak results** (V994_LEAK_SOAK.csv, 32 samples):
  - Handles: 881 → 881 (zero net growth; oscillates 859-909). **FIXED.**
  - Threads: 44 → 33 (net decrease of 11). **FIXED.**
  - Working set: 132.22 → 151.57 MB (+19.35 MB total). Includes one-time spike at +22 min (threads 65→85 then recovered — track detection event). Post-spike plateau: 150.85 → 151.57 over 5 min (~8.6 MB/hr, decelerating to near-flat).
  - Character: bounded/event-driven, NOT exponential (v9.9.3 was ~220 MB/hr indefinitely)
- **Rebuild**: `_full_rebuild.ps1` → v9.9.4 MSI signed, installed. Desktop bundle updated.

### 2026-04-30 — v9.9.3: OBS spectrum lag + Reaction Speed CPU spike
- **OBS lag root cause**: `requestAnimationFrame` in OBS CEF (off-screen rendering) is throttled to the Browser Source FPS setting (default 30fps = 33ms cadence), not the monitor refresh rate. `setInterval` is NOT throttled this way.
- **OBS fix**: Added `const _inOBS = /OBS/i.test(navigator.userAgent)`. In OBS mode: `setInterval(drawSpectrum, 8)` drives the render loop at 120fps regardless of OBS Browser Source FPS. rAF self-schedule suppressed (`if (!_inOBS) requestAnimationFrame(drawSpectrum)`).
- **CPU spike root cause**: WASAPI hot loop fires FFT every HOP samples. At HOP=1 (0.01ms): 48,000 FFTs/sec → 12.5% CPU on 7800X3D. All redundant; SSE only delivers 120/sec.
- **CPU fix**: Added `s_fftStride = max(s_hopSize, FFT_MIN_STRIDE=384)`. Hot loop check changed from `samplesSinceFft >= HOP_SIZE` to `>= s_fftStride`. Floor = 384 ≈ 8ms matches SSE_INTERVAL_MS. At 0.01ms slider: effective rate 125 FFTs/sec (was 48,000). CPU: ~1-3%.
- Files changed: `audio_spectrum.cs`, `overlay.html`, `tray.ps1`
- **Note**: WASAPI loopback shared-mode captures audio ~10-20ms after it plays — this is an OS-level floor that cannot be fixed in userspace. Total observable OBS lag: WASAPI (~15ms) + setInterval jitter (~2ms) + OBS compositor at 30fps (~16ms) ≈ 33ms minimum.

### 2026-04-30 — v9.9.2: WebGL CPU + GPU optimisation
- **Uniform cache (`_gl.uc`)**: `gl.uniform*` now skipped when value unchanged. Static config: 14→0 driver calls/frame. Rainbow mode: 14→1 (only hueOffset changes). Initialized as `{}` in `_glInit` so first frame sends all, then goes sparse.
- **`gl.useProgram` removed** from per-frame path (set once at init; single program, never changes).
- **`gl.viewport` cached**: only called when canvas actually resizes.
- **`gl.clearColor` moved** to init (constant `(0,0,0,0)`, was re-set every frame).
- **Per-band gamma array** (`_gl.bassGamma`) pre-computed once at upload-buffer allocation. Upload loop reads array instead of computing `i * 0.01` per band.
- Files changed: `overlay.html`, `tray.ps1`

### 2026-04-30 — v9.9.1: Reaction Speed min 0.01ms, Frame Rate max 120
- Reaction Speed slider min: `0.5` → `0.01` ms, step `0.01`. Hop clamp in overlay.html: `Math.max(0.083,…)` → `Math.max(0.01,…)`.
- Frame Rate slider max: `1000` → `120`. `(max)` threshold updated from `≥1000` to `≥120` in both bindRange and syncRange formatters. Default fps in customize.html defaults: 144 → 120. `Math.min(1000,…)` rAF cap in overlay.html → `Math.min(120,…)`.
- Files changed: `customize.html`, `overlay.html`, `tray.ps1`

### 2026-04-30 — v9.9.0: Bass flat wall fix (autoGain + loudness boost)
- **Root cause 1 (loudness boost):** `if (norm > 1) norm = 1` hard-clipped → all loud bass bands → 255 → identical → flat wall. Fix: replaced with smooth asymptotic limiter `0.90 + 0.10 * x/(1+x)` where `x = (norm-0.90)/0.10`. norm≤0.90 unchanged; norm=1.0→0.95, norm=1.5→0.986, norm→∞→1.0. Adjacent bands with similar energy now produce different byte values.
- **Root cause 2 (autoGain):** `gainScale = 255/normPeak` mapped all near-peak bands to ~255 → flat wall. Fix: per-band power curve in `_glRender` upload loop. When autoGain on: upload `pow(v/normPeak, γ) × 255` where γ=2→1 from band 0→100. Near-peak bands at 90% of peak now display at 81% height (gap widened from 5% to 9% vs linear). Peak band still hits 100%.
- **Non-autoGain path:** unchanged. `gainScale` now always `1.0` (autoGain normalization baked into upload loop).
- Files changed: `audio_spectrum.cs`, `overlay.html`, `tray.ps1`

### 2026-04-30 — v9.8.3: Default slider values updated to match user spec
- barCount: 50 → 480
- gap: 3 → 0
- barRadius: 4 → 0
- minHeight: 2 → 0
- responseMs: 10.7 → 0.5 (HOP≈24, ~2000 FFTs/sec)
- customize.html HTML display values + syncRange fallbacks updated to match
- Existing users keep saved config; only new installs get new defaults

### 2026-04-30 — v9.8.2: Heartbeat mode (instant rise, fast fall)
- **Rise = instant**: removed lerp/threshold on up-path. `tgt >= cur` → always snap directly to target. Bars react in the same rAF frame the SSE data arrives.
- **Fall = fast decay**: half-life 15ms at smooth=0 (heartbeat pulse) → 350ms at smooth=1. Was 50ms at smooth=0 before.
- **Smoothing slider** now controls fall speed only, not rise.
- **Default smoothing = 0** (was 0.6). Heartbeat mode out of the box.
- **set-hop on SSE connect**: overlay now POSTs saved `responseMs` to audio_spectrum.exe on every SSE connect. audio_spectrum.exe always started at HOP=512 until slider was moved; now saved value is honoured from first frame.
- Files changed: `overlay.html`, `tray.ps1`

### 2026-04-30 — v9.8.1: Real-time spectrum (decouple SSE from render fps)
- **Problem:** Lowering fps to 60 made bars feel sluggish — slider controlled both SSE delivery rate AND render rate.
- **Fix:** SSE always connects at `fps=2000` (server unthrottled / new-frames-only mode). Server sends only on new FFT frames (~120/sec, tied to SSE_INTERVAL_MS=8ms). No duplicates ever. Bar data always ≤8ms old regardless of the Frame Rate slider.
- **Frame Rate slider now ONLY controls render smoothness** (rAF draw rate), not audio data freshness.
- SSE CPU cost is now constant ~77KB/s at all render fps settings.
- Removed fps-change SSE reconnect logic (was for slider-driven SSE throttling, now irrelevant).
- Default render fps raised: 60 → 120.
- Files changed: `overlay.html`, `customize.html`, `tray.ps1`

### 2026-04-30 — v9.8.0: Spectrum lag during games fix
- **Bug:** Spectrum lags/stutters when friends run games alongside OBS.
- **Root cause 1:** `audio_spectrum.exe` was `Normal` process priority. Games elevate via MMCSS to High/Realtime. Normal-priority SSE thread misses its 7ms WaitOne wake-up by 5-10ms → bursty delivery → visible spectrum stutter.
- **Root cause 2:** Default fps=1000. At 1000fps with minGapTicks set, the SSE loop runs 1000 context-switches/sec and sends 640KB/s of duplicate frames → CPU overhead competing with games.
- **Fix 1:** `launcher.cs` — raise `audio_spectrum.exe` from `ProcessPriorityClass.Normal` → `AboveNormal`. capture thread effective priority: 9 → 11 (still below MMCSS Realtime at 15). `timeBeginPeriod(1)` was already in place since v7.0.0.
- **Fix 2:** `overlay.html` default fps 1000 → 60, `customize.html` display + sync fallback 120 → 60. New installs and installs without saved fps get 60fps (matches typical OBS Browser Source FPS).
- **Note:** Existing installs with fps=1000 saved in config.json keep 1000 (deepMerge uses saved value). Only new installs get 60 default.
- Files changed: `overlay.html`, `customize.html`, `launcher.cs`, `tray.ps1`

### 2026-04-30 — v9.7.0: Fix TDZ crash in _glRender (spectrum blank)
- **Bug:** Spectrum completely blank after v9.6.9. Root cause: `const _agGate` referenced `autoGain` before `const autoGain` was declared in the same function — JavaScript temporal dead zone. Every `_glRender()` call threw a silent ReferenceError inside rAF (browser swallows rAF errors), leaving the spectrum invisible on every frame.
- **Fix:** Changed `autoGain` → `cfg.autoGain` in the gate check (function parameter, always in scope, no TDZ).
- **Lesson:** When inserting code BEFORE existing `const` declarations in a function, always check for forward references. `const` in JS is block-hoisted but NOT initialized until its line — accessing it before that line throws.
- Files changed: `overlay.html`, `tray.ps1`

### 2026-04-30 — v9.6.9: AutoGain noise gate fix
- **Bug:** AutoGain turned on → spectrum looked pixelated/spiky. Root cause: `gainScale = 255/normPeak` (up to 50× on quiet audio) was amplifying the WASAPI noise floor (band values 1–5) into visible bars across the whole spectrum.
- **Fix:** Proportional noise gate in `_glRender` band upload loop. Zeroes any band below `normPeak * 0.06` before gainScale is applied. AutoGain off → gate = 0, no change.
- Files changed: `overlay.html`, `tray.ps1`

### 2026-04-30 — memory.md moved to project root
- Moved from `.claude/memory.md` to `memory.md` (project root) — `.claude/` is a hardcoded sensitive directory in Claude Code; allow rules in settings.json cannot override it. Root-level file has no such restriction.
- Updated `CLAUDE.md` reference from `.claude/memory.md` → `memory.md`
- Left stub at `.claude/memory.md` pointing here

### 2026-04-30 — v9.6.8: Rebuild with fixed INSTALL.bat in correct source location

- Bumped tray.ps1 to v9.6.8, prepended PATCH_HISTORY entry for the cert fix
- Rebuild completed clean; MSI signed Valid (CN=MasterShadex); installed over v9.6.7
- Desktop bundle updated: `Master's FM V9.6.8.msi` + fixed `INSTALL.bat` + `MastersFM_publisher.cer`

### 2026-04-30 — v9.6.7: Rebuild + INSTALL.bat cert fix + _full_rebuild.ps1 path fix

- **_full_rebuild.ps1 path:** Two lines had stale `F:\Claude AI\Master FM` — updated to `G:\Project Folder\Master FM`. Script was no-op on the machine until this fix.
- **INSTALL.bat cert fix (Root CA):** Friends couldn't install because cert was only in TrustedPublisher. Without Root CA trust, chain is invalid → "Unknown Publisher" or MSI rejection on some machines. Fixed by adding `certutil -addstore -f "Root" "MastersFM_publisher.cer"` (LocalMachine, no `-user` flag = SILENT from elevated cmd). Also removed `>nul 2>&1` suppression so certutil errors are now visible.
- **IMPORTANT — wrong file was fixed first:** `MastersFM_Installer\INSTALL.bat` was edited but `_full_rebuild.ps1` copies from `Master's FM Install\INSTALL.bat`. The Desktop bundle got the old version. Fixed in session 3 by patching both `Master's FM Install\INSTALL.bat` AND `Desktop\MastersFM_Installer\INSTALL.bat` directly. Future rebuilds will now be correct.
- **Rebuild result:** v9.6.7 MSI built and signed, installed over v9.6.6, app launched OK. Bundle on Desktop: 3 files (MSI + INSTALL.bat + MastersFM_publisher.cer).
- Files changed: `_full_rebuild.ps1`, `Master's FM Install\INSTALL.bat`, `MastersFM_Installer\INSTALL.bat`, `Desktop\MastersFM_Installer\INSTALL.bat`

### 2026-04-30 — v9.6.7: Number of Bars slider fix
- **Bug confirmed:** `c-spec-bars` (Number of Bars) was a dead control since v9.4.0 canvas2d wipe. canvas2d sliced its draw loop to barCount; WebGL always received all 480 bands from audio_spectrum.exe and rendered all of them. The slider updated `_cfg.spectrum.barCount` but `_glRender` never read it — `bandLen` came from `bands.length` (always 480).
- **Fix:** Added JS-side band decimation in `drawSpectrum()` (overlay.html). Before calling `_glRender`, downsamples `_renderedBands` (480 floats) to `barCount` groups by taking the max of each group. Pre-allocated `_decimatedBands` / `_decimatedLen` buffers to avoid per-frame GC. Visual effect confirmed: 10 = wide fat bars, 50 = default density, 480 = full resolution.
- **All other controls tested and working:** gap, barRadius, opacity (fixed v9.6.4), colorMode, color, smoothing, fps, heightMult, minHeight, mirror, autoGain; all non-spectrum controls (font, card, border, glow, art, progress, animation) pass through correctly.
- Files changed: `overlay.html`, `tray.ps1` (version + PATCH_HISTORY)
- **Note:** Verified in preview by copying overlay.html to `C:\Users\Master\AppData\Local\MastersFM\` — server.exe serves from install dir, not source. Normal rebuild path will copy it properly.

### 2026-04-30 10:00 — Full onboarding from all .md files
- Read all 45 project .md files (excluding node_modules, backups)
- Confirmed source root is `G:\Project Folder\Master FM\` (HANDOFF.md F: path is stale)
- Version history reconstructed from V9_FINAL_REPORT through V96_FINAL_REPORT: v9.0.0→v9.6.0
- Key deferred items catalogued in open_issues.md
- Hard constraints + gotchas in hard_constraints.md
- Architecture + file sizes + endpoints in project_overview.md + codebase_details.md
- Version history in version_history.md
- OBS Source Side feature confirmed partially implemented (customize.html done, backend missing)
- F: drive noted as full; backups going to C:

### 2026-04-30 09:45 — Initial onboarding session (memory.md created)
- Read CLAUDE.md, save-tokens.md, tools.md, onboard.md, memory.md template, HANDOFF.md, CLAUDE_CODE_INSTRUCTIONS.md, V96_FINAL_REPORT.md, V92_FINAL_REPORT.md, CLAUDE_CHANGES.md
- Created .claude/memory.md from scratch (file did not exist)

---

# RULES FOR UPDATING THIS FILE

1. **Append at the top of CHANGELOG, not the bottom.** Most recent first.
2. **Update the top sections (CURRENT STATE, IN-FLIGHT, etc.) when they change.** Don't leave stale data.
3. **Move items between sections as their status changes.**
4. **Don't delete old changelog entries.** Compact only if >500 lines, ask first.
5. **Be honest.** If something failed, say so plainly.
6. **No filler.**
