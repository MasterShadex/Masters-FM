# V112X_TRACK_CHANGE_DIAGNOSIS.md
## Track-Change CPU Spike — Component B

---

## One-Paragraph Summary

The remaining ~1-second, 15%-CPU spike on track skips is **not caused by a single blocking network or I/O call**. All the obvious suspects — album art fetch, scrobble HTTP, Discord RPC, webhook POST — were made async in prior versions. The actual culprit is **PowerShell interpreter overhead distributed across the entire `Get-SMTCNowPlaying` function** (~177ms of the 191ms SMTC detector time), with the synchronous ALPC calls (GetSessions, GetPlaybackInfo, GetTimelineProperties, GetAllVisibleTitles) accounting for only 14ms (~7%). A full fix requires moving `Get-SMTCNowPlaying` to run asynchronously on a background task (same pattern as `Get-SMTCManager` v9.9.9), which would drop per-tick SMTC cost from ~200ms to <5ms. Quick wins (pre-compile regex patterns, cache `Get-PlatformName`/`Test-PlatformEnabled`) save ~25-40ms but leave the function above the 150ms slow threshold.

---

## Live Measurement Data

Three track-change events captured across three builds (one pre-instrumentation, two with timing instrumentation). Each track-change tick triggers the new-track webhook path and fires the SMTC circuit breaker, which backs the detector off for 30 ticks (3 seconds).

| Event | Total tick (ms) | DetectorChain (ms) | smtc (ms) | WebhookInit (ms) | Note |
|-------|----------------|-------------------|-----------|-----------------|------|
| Soak (21:12, v11.2.1 build 1) | ~335 | 247 | 230 | 42 | pre-instrumentation, section data N/A |
| Build 1 restart (00:39) | ~200 | 163 | ~161 | 27 | first instrumented build, no SMTC sections |
| Build 2 restart (00:44) | 312 | 237 | 215 | 44 | full section timing available |

### SMTC Detector Section Breakdown (Build 2 measurement)

From the `[TRACK_CHANGE_TIMING]` log entry at 00:44:26:

| Section | Elapsed (ms) | Key operations |
|---------|-------------|----------------|
| GetSMTCSessionsCached entry → done | 35 | Get-SMTCManager poll+fire (~28ms) + `$mgr.GetSessions()` (~7ms) |
| Session loop (find bestSession) | 52 | `GetPlaybackInfo()` live call (~2ms) + PS loop/regex overhead (~50ms) |
| SoundCloud-RPC override block | 12 | `GetAllVisibleTitles()` EnumWindows (~3ms) + string/log overhead (~9ms) |
| `Get-SMTCMediaPropsCached` call | 13 | `TryGetMediaPropertiesAsync()` async-start IPC (~10ms) + reflection (~3ms) |
| Post-props: GetSMTCPosition + GetTrustedTimeline + GetTrustedPlayback + PlatformName + return hash | 79 | `GetTimelineProperties()` (~2ms) + multiple PS function call overhead (~77ms) |
| **Total in Get-SMTCNowPlaying** | **191** | |
| Invoke-Detector wrapper overhead | 24 | stopwatch create/read, `& $fn` invocation |
| **smtc per SLOW TICK log** | **215** | |

#### Synchronous blocking calls vs PS overhead

| Call | Time (ms) | % of 191ms |
|------|-----------|-----------|
| `$mgr.GetSessions()` (ALPC) | 7 | 3.7% |
| `$Session.GetPlaybackInfo()` (ALPC) | 2 | 1.0% |
| `$Session.GetTimelineProperties()` (ALPC) | 2 | 1.0% |
| `[Win32Windows]::GetAllVisibleTitles()` (EnumWindows) | 3 | 1.6% |
| **All synchronous blocking calls** | **14** | **7.3%** |
| PowerShell interpreter overhead (loop, regex, function calls, hash construction) | ~177 | ~92.7% |

**The synchronous ALPC calls are NOT the bottleneck.** The function's 200-line interpreted PS body is.

---

## Static Analysis Findings

### Operations ruled out (async / non-blocking)

| Operation | Verdict | Evidence |
|-----------|---------|---------|
| Album art fetch + decode (`Get-SMTCThumbnailDataUri` / `Invoke-DeferredThumbExtraction`) | ✅ async | v9.9.9/v9.10.0 non-blocking state machine; returns '' immediately on new-track tick |
| Last.fm scrobble | ✅ removed | PATCH_HISTORY v8.5.x: "Last.fm dependency removed entirely" — no code found |
| Discord RPC presence update | ✅ not in tray | tray reads `discord_rpc.enabled` config only; all RPC calls happen in server.js |
| New-track webhook POST (`Send-WebhookAsync`) | ✅ fire-and-forget | v8.2.5: `$null = $global:_httpClient.PostAsync()` — task discarded |
| SMTC manager (`Get-SMTCManager`) | ✅ async | v9.9.9: fires `RequestAsync()` as background Task, returns stale cache immediately |
| SMTC media props (`Get-SMTCMediaPropsCached`) | ✅ async | v9.10.0: fires `TryGetMediaPropertiesAsync()` as background Task; returns stale props immediately. NOTE: the IPC call to START the async op takes ~10ms |

### Operations that are synchronous (but measured small)

| Operation | Location | Measured time | Notes |
|-----------|----------|--------------|-------|
| `$mgr.GetSessions()` | `Get-SMTCSessionsCached` line 5876 | **7ms** | Synchronous ALPC; runs every tick when cache expired. Transition guard can suppress it but doesn't arm until GetPlaybackInfo detects Changing |
| `$Session.GetPlaybackInfo()` | `Get-SMTCPlaybackInfoCached` line 5900 | **2ms** | Synchronous ALPC; 500ms staleness cache + 750ms transition guard both work correctly post-v11.2.1 |
| `$Session.GetTimelineProperties()` | `Get-SMTCPosition` line 6512 | **2ms** | Synchronous ALPC; same guards as above |
| `[Win32Windows]::GetAllVisibleTitles()` | `Get-SMTCNowPlaying` line 7118 | **3ms** | Win32 EnumWindows sweep; runs every tick when sc-rpc is active. Fast. |

### The real overhead: PowerShell interpreter per-tick cost

The `Get-SMTCNowPlaying` function (lines 7051–7282, ~230 effective lines) runs on every tick where SMTC is active. Each execution involves:

1. **Session loop** (~52ms): `foreach` loop over sessions, per-iteration: `Get-SMTCPlaybackInfoCached`, `$aid.ToLower()`, 2 string `-match` regex operations (patterns `'spotify|msedge|chrome|...'` and `'soundcloud|deezer|applemusic|...'`). Regex patterns are compiled from string literals on every call.

2. **Post-props chain** (~79ms): sequential calls to `Get-SMTCPosition` (which calls `Get-SMTCPlaybackInfoCached` again + `GetTimelineProperties`), `Get-TrustedTimelineMs`, `Get-TrustedPlaybackState`, `Get-PlatformName`, `Test-PlatformEnabled`. Each is a PS function call with its own overhead.

3. **GetSMTCSessionsCached overhead** (~28ms): `Get-SMTCManager` polls/fires async WinRT task; `MakeGenericMethod` + `RequestAsync()` startup overhead even though it returns stale cache immediately.

4. **GetSMTCMediaPropsCached overhead** (~13ms): `MakeGenericMethod` + `[System.Threading.CancellationTokenSource]::new(150)` + `TryGetMediaPropertiesAsync()` IPC start.

### WebhookInit (PostAsync, 27–44ms)

The `Send-WebhookAsync` stopwatch captures `PostAsync()` call time. On first track-change after tray restart, the HttpClient establishes a new TCP connection to 127.0.0.1:4242 (loopback), taking 27–44ms. On subsequent new-track events (heartbeats), this would be near-0ms (connection pooled). Not a primary bottleneck; no fix needed.

### SMTC session re-enumeration post-v11.2.1

The `GetSessions()` call happens every tick via `Get-SMTCSessionsCached` (tick-scoped, first-call-per-tick). The transition guard (active 750ms after a `PlaybackStatus=Changing` detection) suppresses `GetSessions()` on subsequent ticks within the window. On the FIRST track-change tick, the guard is not yet active, so `GetSessions()` runs synchronously — but only takes 7ms.

---

## Identified Culprit

**PowerShell interpreter overhead in `Get-SMTCNowPlaying` (lines 7051–7282).**

- 191ms per track-change tick
- 92.7% of that time is PS interpreter cost (loop execution, regex compilation, function call overhead, hash table operations, object property accesses via COM reflection)
- Only 7.3% is synchronous ALPC calls

The circuit breaker in `Invoke-Detector` (line 8472) limits the damage: after one slow tick (>150ms), SMTC backs off for 30 ticks (3 seconds). So there is ONE slow tick per track skip, not sustained slowness.

**This is why the user observes "~1 second":** Task Manager samples CPU at ~1-second intervals. A 312ms tick (containing a 215ms SMTC detector run) shows up as ~31% of one core busy during that 1-second sample window. Expressed as per-process % on a 16-core machine: ~2% per-tick. The observed 15% likely reflects both the slow tick AND the Gen2 GC that coincidentally fired in the same tick (see log context: "GC flush: Gen2 forced (5-min interval)") plus async WinRT thread pool work.

---

## Recommended Fix

### Fix Target: `Get-SMTCNowPlaying` (lines 7051–7282) — move to async background task

**Architecture:** Same pattern used for `Get-SMTCManager` (v9.9.9) and `Get-SMTCMediaPropsCached` (v9.10.0).

- On tick N: check if a pending SMTC task exists and has completed. If yes, collect result and cache it. Then fire a new background task (via `Start-ThreadJob` or a .NET Runspace) to run `Get-SMTCNowPlaying` on a thread pool thread.
- Return the PREVIOUSLY CACHED result immediately (stale by one tick = 100ms max, acceptable).
- The slow 191ms runs on a thread pool thread, never blocking the UI timer.

**Impact:** Reduces per-tick SMTC detector time from ~200ms to <5ms. Eliminates the need for the 30-tick circuit breaker. Eliminates the SLOW TICK on track change.

**Estimated effort:** Moderate restructure (~50–80 lines). The function itself doesn't change; a wrapper manages the async task lifecycle.

### Quick-win alternative (if async is too large for the next fix run)

**A. Pre-compile regex patterns** — the session loop uses string literals for `-match` which compile a new `Regex` object each call. Cache them as `$global:` variables at startup:
```powershell
$global:_smtcSkipPattern  = [regex]::new('spotify|msedge|chrome|firefox|opera|brave|steelseries', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$global:_smtcPriPattern   = [regex]::new('soundcloud|deezer|applemusic|...')
```
**File:line:** `Get-SMTCNowPlaying`, line ~7094 and ~7100. Change `$aid -match 'string'` → `$global:_smtcSkipPattern.IsMatch($aid)`.
**Estimated savings:** 15–25ms per track-change tick.
**Effort:** 3–5 line change.

**B. Cache `Get-PlatformName` / `Test-PlatformEnabled` per SAUMID** — these are called every tick for the same stable appId. Cache result in a `$global:` hashtable; invalidate when platform config changes.
**File:line:** `Get-SMTCNowPlaying`, lines ~7225–7235 (post-props chain).
**Estimated savings:** 10–20ms per tick.
**Effort:** 10–15 line change.

Combined A+B would save ~25–45ms, reducing SMTC from ~200ms to ~155-175ms — still above the 150ms slow threshold, so the circuit breaker would still trip. The async-task approach (main fix) is needed to fully eliminate the spike.

---

## Sworn Statement

- All temporary instrumentation removed — verified with grep for `TEMP INSTRUMENTATION V112X`, zero matches
- Source diff against v11.2.1 commit shows ONLY the expected v11.2.1 changes (version string, PATCH_HISTORY entries, 3 SAUMID cache-key edits) — no instrumentation lines remain
- Clean rebuild after removal: exit 0, DLL Valid (CN=MasterShadex), MSI signed, sha256=90f60853fc9890ef222fbae731a861940815b5fabb178810e9beaa9deccb51af (version=11.2.1)
- No version bumped, no commit, no push, no memory.md edits during this run
- Installed tray is confirmed v11.2.1 clean (post-removal build)

---

TRACK_CHANGE COMPONENT B DIAGNOSIS COMPLETE — recommended fix: move `Get-SMTCNowPlaying` to async background task (~50-80 line restructure). User to decide on next-version fix run.
