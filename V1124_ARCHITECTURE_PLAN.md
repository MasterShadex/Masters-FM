# V1124_ARCHITECTURE_PLAN.md — SMTC Event-Driven Refactor

**Target:** v11.2.4
**Author:** Opus planning run (2026-05-03)
**Status:** Implementation blueprint — ready for Sonnet execution
**Constraint:** No source modifications during planning. This document is the deliverable.

---

## TL;DR

The 3-5 second system-wide FPS lag on every track change is caused by tray.ps1 making continuous synchronous SMTC ALPC reads against the same shared Windows service that soundcloud-rpc is writing to. Empirically confirmed: zero SMTC reads → single 10-20 ms blip; full SMTC reads → 600→0 FPS oscillation for 3-5 s.

**Architectural fix:** Replace polling with WinRT event subscriptions. Add a small C# class to `tray_native.dll` (`MasterFM.SMTCWatcher`) that subscribes to `SessionsChanged` / `CurrentSessionChanged` / `MediaPropertiesChanged` / `PlaybackInfoChanged` / `TimelinePropertiesChanged` events, captures snapshots into a thread-safe cache and a `ConcurrentQueue` of change records, and exposes them to PowerShell. The tick handler reads from the cache (no SMTC ALPC) and drains the change queue once per tick.

Result: zero SMTC ALPC traffic from tray.ps1 in steady state. No contention with soundcloud-rpc. No transition guards needed (because nothing transitions in the tray). All metadata updates real-time. All v11.2.1/2/3 fixes preserved (their symptoms can no longer occur because the failure modes they patched no longer exist).

Estimated total execution time: **3-4 hours across 5 staged commits**.

---

## SECTION 1 — CURRENT STATE MAP

Every place tray.ps1 currently touches SMTC, with file:line, what it does, and what reads it.

### 1.1 WinRT setup (one-time, at startup)

| Location | Lines | What |
|----------|-------|------|
| `_awaitAsTaskGenericCts` binding | 5145-5176 | Reflects `WindowsRuntimeSystemExtensions.AsTask<T>(IAsyncOperation<T>, CancellationToken)` once. Used by the two existing async helpers. **PRESERVE**. |

### 1.2 Existing async patterns (to extend, not break)

#### Get-SMTCManager (lines 5761-5823)

```
Tick handler →
  (any caller) →
    Get-SMTCManager →
      IF in-flight task exists AND not completed → return stale cache
      IF in-flight task exists AND completed → collect .Result, set TTL=600 ms
      IF cache valid (< TTL ms old) → return cache
      ELSE → fire RequestAsync() + AsTask + 300 ms CTS, return stale cache
```

- Pattern: fire-and-poll. Not pure event-driven, but non-blocking.
- Cost: ~185 ms wall-clock per RequestAsync, but on a ThreadPool thread (not the tick).
- Frequency: one re-acquisition every ~985 ms (TTL + completion latency + poll alignment).
- TICK COST: ~1 ms per call (just .IsCompleted + .Result on a completed Task).

#### Get-SMTCMediaPropsCached (lines 5933-6005)

```
Tick handler → detector → Get-SMTCMediaPropsCached(session) →
  IF in-flight task for session AND completed → collect .Result, populate _smtcPropsResultCache[SAUMID]
  IF rate limit OK (≥ 500 ms since last fire) AND not in-flight AND not fired this tick →
      fire TryGetMediaPropertiesAsync + AsTask + 150 ms CTS
  return _smtcPropsResultCache[SAUMID]  (stale cache while task runs)
```

- Pattern: same fire-and-poll, per-session keyed by `SourceAppUserModelId`.
- Title-change side-effect: when a completed task reveals a new Title, sets `_smtcTransitionGuardMs = now + 750 ms`.
- TICK COST: ~1 ms (cache read).

### 1.3 Synchronous ALPC reads (the actual bottleneck)

These are the calls causing system-wide contention:

| Function | Line | Synchronous call | Frequency in steady state | Cache key | Guards |
|----------|------|-------------------|---------------------------|-----------|--------|
| `Get-SMTCSessionsCached` | 5893 | `$mgr.GetSessions()` | ≤2/sec/tick | (n/a, single list) | 500 ms staleness + transition guard |
| `Get-SMTCPlaybackInfoCached` | 5918 | `$Session.GetPlaybackInfo()` | ≤2/sec/session | SAUMID | 500 ms staleness + transition guard. Arms transition guard on Changing status. |
| `Get-SMTCPosition` | 6536 | `$Session.GetTimelineProperties()` | ≤2/sec/session | SAUMID | 500 ms staleness + transition guard |
| `Get-SMTCNowPlayingCached` | 7059-7068 | (none — outer rate-limiter) | ≤3.3/sec | (n/a) | 300 ms TTL on whole `Get-SMTCNowPlaying` result |
| `Get-SMTCNowPlaying` | 7074-7283 | builds `$np` from the above | called by detector at most 3.3/sec | (n/a) | (relies on inner caches) |

Each ALPC call is fast in isolation (~1-3 ms) but contends with the same Windows SMTC service that soundcloud-rpc writes to during track transitions. Tray's continuous reads + soundcloud-rpc's writes → service queue depth → other consumers (game GameBar, Windows audio UI) wait.

### 1.4 Win32 calls

| Function | Line | Native call | Frequency | Notes |
|----------|------|--------------|-----------|-------|
| soundcloud-rpc override | 7159 | `[MasterFM.Win32Windows]::GetAllVisibleTitles()` | every 300 ms when soundcloud-rpc wins | EnumWindows. Cheap (~2 ms). NOT in scope (see SECTION 7 — keep unchanged). |

### 1.5 Per-session caches keyed by SAUMID (v11.2.1 fix preserved)

| Cache | Decl line | Used in |
|-------|-----------|---------|
| `_smtcPropsResultCache` | 5847 | `Get-SMTCMediaPropsCached` |
| `_smtcPbInfoCache` | 5856 | `Get-SMTCPlaybackInfoCached` |
| `_smtcPbInfoCacheMs` | 5857 | `Get-SMTCPlaybackInfoCached` (staleness) |
| `_smtcTlCache` | 5858 | `Get-SMTCPosition` |
| `_smtcTlCacheMs` | 5859 | `Get-SMTCPosition` (staleness) |
| `_smtcSessionsCache` | 5843 | `Get-SMTCSessionsCached` (per-tick) |
| `_smtcSessionsCacheLastGood` | 5860 | last-good fallback |
| `_smtcSessionsCacheMs` | 5861 | (staleness) |
| `_smtcNpCache` | 5862 | `Get-SMTCNowPlayingCached` 300 ms TTL |
| `_smtcNpCacheMs` | 5863 | (staleness) |
| `_smtcTransitionGuardMs` | 5855 | suppress reads during transitions |
| `_smtcPropsTaskDict` | 5848 | in-flight tracking |
| `_smtcPropsCtsDict` | 5849 | timeout CTS tracking |
| `_smtcPropsFiredThisTick` | 5850 | dedup (cleared on completion in `finally`) |
| `_smtcPropsLastFiredMs` | 5850 | rate limit (500 ms/SAUMID) |

### 1.6 Tick-handler integration

```
scrobbleTimer.add_Tick (line 8578)
  → detector chain (line 8690) iterates 10 detectors
    → 'smtc' = Get-SMTCNowPlayingCached (line 8693)
    → 'spotify' = Get-SpotifyNowPlaying — calls Find-SMTCSession (line 8692)
    → 'browser' = Get-BrowserMediaNowPlaying — calls Find-SMTCSession (line 8694)
    → 'wmpSMTC' = Get-WMPNowPlayingSMTC — calls SMTC functions (line 8697)
  → sc-shadow-check (line 8743) — calls Find-SMTCSession
  → new-track or heartbeat path → Send-WebhookAsync, Invoke-DeferredThumbExtraction
```

ALL detectors that consume SMTC route through the same 4 cached helpers (`Get-SMTCSessionsCached`, `Get-SMTCPlaybackInfoCached`, `Get-SMTCPosition`, `Get-SMTCMediaPropsCached`). Replacing the bodies of those four functions to read from the event-driven cache automatically fixes every detector.

### 1.7 Threading model (from tray_launcher.cs)

- `[STAThread]` Main →
- `InitialSessionState.CreateDefault()` with `ApartmentState.STA` and `ThreadOptions.UseCurrentThread` →
- runspace runs on the same Main thread →
- `Application.Run` (implicit via WinForms in tray.ps1) on same thread →
- `scrobbleTimer.Tick` fires on this thread.

**Implication:** the entire PowerShell script runs on a single STA thread, which is also the WinForms message-pump thread. WinRT event handlers fired from a thread pool thread CANNOT directly invoke PowerShell scriptblocks (PowerShell engine isn't multi-thread-safe across runspace boundaries). Events must communicate to PS via thread-safe data structures (ConcurrentQueue, locked dict, etc.) and the tick polls them.

---

## SECTION 2 — TARGET ARCHITECTURE

### 2.1 Decision: pure event-driven via a C# helper class

**Decision:** add `MasterFM.SMTCWatcher` to `tray_native.cs`. PowerShell instantiates it once after the manager is acquired. The watcher:

1. Subscribes to `SessionsChanged` and `CurrentSessionChanged` on the manager.
2. For each session, subscribes to `MediaPropertiesChanged`, `PlaybackInfoChanged`, `TimelinePropertiesChanged`.
3. On every event, captures a snapshot (PlaybackStatus, position, duration, title, artist, etc.) into a per-SAUMID `ConcurrentDictionary<string, SMTCSessionSnapshot>`.
4. Also enqueues a typed change record into a `ConcurrentQueue<SMTCChangeRecord>`.
5. Exposes `GetSnapshot(saumid)` (lock-free read) and `DrainEvents()` (returns and clears queued records as an array) to PowerShell.
6. Handles MediaProperties asynchronously: `MediaPropertiesChanged` fires → handler calls `TryGetMediaPropertiesAsync` (off the STA, on its native event thread) → on completion, updates the snapshot and enqueues an event.

PowerShell side (every tick):
1. `$pendingEvents = $global:_smtcWatcher.DrainEvents()` (microseconds)
2. For each event, update PS-side state (or just trust the C# snapshot directly — see 2.5)
3. Detector calls `Get-SMTCNowPlayingCached` → returns hashtable built from `$global:_smtcWatcher.GetSnapshot(...)` — zero ALPC

### 2.2 Alternative considered: pure-PS background runspace

**Considered:** spin up a second PowerShell runspace in MTA on its own thread. That runspace polls SMTC at full speed without blocking the STA tick. Communicates results via a thread-safe variable.

**Rejected because:**
- Doesn't fix root cause. Tray STILL polls SMTC, still contends with soundcloud-rpc at the SMTC service. Just moves the contention off the tick thread inside tray.ps1 — but soundcloud-rpc's notifications to other consumers still queue behind us. The user's empirical test (TTL=10000 → still partial lag) confirms reducing polling doesn't fully fix it.
- Complex: requires serialising globals across runspaces, careful disposal.
- Doesn't satisfy the user's "real-time" requirement: still has a polling cadence delay.

### 2.3 Alternative considered: `Register-ObjectEvent` from PowerShell

**Considered:** `Register-ObjectEvent -InputObject $mgr -EventName SessionsChanged -Action { ... }`.

**Rejected because:**
- The `-Action` scriptblock is queued for execution on the runspace's thread (STA tick thread) via PowerShell's event subsystem. This means events get processed when `Wait-Event` runs OR when the engine yields. In practice that's during the same tick that's already busy. Doesn't solve the threading problem.
- WinRT events don't cleanly map through `Register-ObjectEvent` because the WinRT delegate types (TypedEventHandler<,>) need explicit binding. Possible but fragile.
- Event handlers run inside PowerShell engine — slower than C# delegate fires, more allocation.

### 2.4 Alternative considered: PowerShell `.Add_X()` direct subscribe

**Considered:** `$mgr.Add_SessionsChanged({ ... })` from PS.

**Rejected because:**
- Same issue — PS scriptblock can't safely run on the WinRT thread pool callback thread.
- Even if it worked, would block the WinRT thread pool while the script runs (small, but still bad).

### 2.5 Decision: keep snapshot data in C#, expose object refs to PS

**Decision:** the snapshot dictionary lives in the C# watcher. The PowerShell-side cached hashtables (`_smtcPropsResultCache`, `_smtcPbInfoCache`, `_smtcTlCache`) become thin views over the watcher's snapshot. Helpers like `Get-SMTCPlaybackInfoCached` change their bodies to:

```powershell
function Get-SMTCPlaybackInfoCached($Session) {
    $key = $Session.SourceAppUserModelId
    $snap = $global:_smtcWatcher.GetPlaybackInfo($key)
    if (-not $snap) { return $null }
    return $snap   # returns a structured object with .PlaybackStatus, .Controls.IsPauseEnabled, etc.
}
```

The watcher exposes whatever shape PS already consumes (status, position, duration, title, artist, album, thumbnail-ref). No round-trip to SMTC.

### 2.6 Initial state population

WinRT events fire only on CHANGES. To populate initial state when the watcher starts:

- `Initialize(manager)` enumerates `manager.GetSessions()` ONCE on the C# init thread.
- For each session, fire synthetic "added" events: subscribe + capture initial snapshot via `Session.GetPlaybackInfo()` (sync, but only once per session, off the tick thread).
- For initial MediaProperties: fire `TryGetMediaPropertiesAsync` (no rate limit, only once per new session).

This initial enumeration is the ONLY synchronous SMTC ALPC the tray will ever make in steady state. After that, everything is event-driven.

### 2.7 Manager lifecycle

**Decision:** the manager is a singleton in the SMTC service. Once acquired, hold the reference for the entire app lifetime. **Stop re-acquiring it.**

Current code re-acquires every ~985 ms because of the `_smtcMgrCacheTTL = 600` design (lines 5773, 5786). This was paranoia (we worried the manager might go stale). Empirically, the manager is stable. Re-acquisition is wasted work that adds 87 RequestAsync calls/min to baseline.

The new `Get-SMTCManager` body becomes:
```powershell
function Get-SMTCManager {
    if ($global:_smtcMgrCached) { return $global:_smtcMgrCached }
    # First-time acquisition only — fire RequestAsync, poll on subsequent ticks
    # (existing fire-and-poll pattern, but only ever runs once)
    ...existing async code, kept for cold-start path only...
}
```

**Failsafe:** if events stop firing for N seconds despite known activity (CANARY check), force a manager re-acquisition. See SECTION 5 mitigation 4.

### 2.8 Removed machinery

Once events drive cache invalidation:
- `_smtcSessionsCacheMs` (500 ms staleness) — REMOVED
- `_smtcPbInfoCacheMs` + 500 ms staleness — REMOVED (snapshot updated by event)
- `_smtcTlCacheMs` + 500 ms staleness — REMOVED
- `_smtcTransitionGuardMs` + arming logic — REMOVED (no transitions to guard against — we don't read during them)
- `_smtcPropsLastFiredMs` 500 ms rate limit — REMOVED (no PS-driven TryGetMediaPropertiesAsync firing)
- `_smtcPropsTaskDict` / `_smtcPropsCtsDict` / `_smtcPropsFiredThisTick` — REMOVED
- `_smtcMgrCacheTTL` re-acquisition — REMOVED (manager held forever)
- `_smtcNpCacheMs` 300 ms TTL — REDUCED to 50 ms (or removed entirely — snapshot reads are free; the rate-limit was protecting from sync ALPC, no longer needed)

The v11.2.1/2/3 fixes (which patched bugs in this machinery) are PRESERVED in the sense that the bugs they fixed cannot recur — the machinery doesn't exist anymore.

### 2.9 SAUMID preservation (v11.2.1 fix)

The watcher's `ConcurrentDictionary<string, SMTCSessionSnapshot>` is keyed by `SourceAppUserModelId`. This is the v11.2.1 fix made permanent: the cache key is the only stable identity. Each `SessionsChanged` event re-walks `manager.GetSessions()` and updates the dict by SAUMID. New COM RCWs for the same SAUMID overwrite the old snapshot — no accumulation, no leak.

### 2.10 Session subscription disposal

When `SessionsChanged` fires, the watcher diffs the session list:
- New SAUMID → subscribe to its three property-change events, capture initial snapshot.
- Removed SAUMID → unsubscribe (release the event handler delegate to allow RCW finalization), remove from dict.

Failure to unsubscribe = leak of native event registrations + retained RCWs. The watcher MUST track subscriptions per SAUMID and release them on session removal and on `Dispose()`.

---

## SECTION 3 — EXISTING ASYNC PATTERNS TO LEVERAGE

The new architecture extends, not replaces, two existing v9.9.x patterns.

### 3.1 The `_awaitAsTaskGenericCts` infrastructure (lines 5145-5176)

**Currently used by:** Get-SMTCManager (line 5816), Get-SMTCMediaPropsCached (line 5991), Invoke-DeferredThumbExtraction (lines 6115, 6142).

**Reuse for v11.2.4:** `MasterFM.SMTCWatcher` does NOT need this infrastructure. Inside C#, we use `IAsyncOperation<T>.AsTask(CancellationToken)` directly — it's a plain extension method when called from C#. No reflection needed.

**Caveat:** PS-side wrappers (`Invoke-DeferredThumbExtraction`) continue to use `_awaitAsTaskGenericCts`. Don't touch them — they're out of scope for this refactor.

### 3.2 The fire-and-poll Task pattern (Get-SMTCManager, lines 5761-5823)

**Currently:** PS fires `RequestAsync()`, wraps in Task via reflection, polls `IsCompleted` from the tick.

**v11.2.4 use:** ONLY for cold-start manager acquisition. After first acquisition, the manager is held forever and this pattern becomes a no-op (returns the cached reference).

### 3.3 The non-blocking state machine (Invoke-DeferredThumbExtraction, lines 6076-6189)

**Out of scope.** This is the album-art extraction pattern. Don't touch. SECTION 7.

### 3.4 New pattern: C#-side delegate + ConcurrentQueue/Dict

**New, but it's the same shape as `_awaitAsTaskGenericCts`** — set up reflection bindings once at startup, use them many times. The bindings here are: a managed C# class (loaded from `tray_native.dll`), with thread-safe collections, exposing methods callable from PS.

This is structurally identical to how PS already uses `[MasterFM.Win32Windows]::GetAllVisibleTitles()` and `[MasterFM.AudioPeak]::GetPeakForProcessName(...)`. Just a new class with new methods.

---

## SECTION 4 — MIGRATION PLAN (5 stages)

Each stage is independently buildable, smoke-testable, and reversible. Each stage results in a tray that still works (just incompletely converted in the middle stages).

### STAGE 1 — Build the SMTCWatcher class (no PS integration)

**Files changed:**
- `src/tray_native.cs` (additive — new class)
- `_full_rebuild.ps1` (no change — already compiles tray_native.cs)

**Lines added:** ~250 lines of C#, all in a new namespace `MasterFM.SMTC`.

**New class layout:**
```csharp
namespace MasterFM.SMTC {
  public class SMTCSessionSnapshot {
    public string Saumid;
    public int PlaybackStatus;       // 0=Closed,1=Opened,2=Changing,3=Stopped,4=Playing,5=Paused
    public long PositionMs;
    public long DurationMs;
    public long LastUpdatedTicks;    // for staleness detection
    public string Title;
    public string Artist;
    public string AlbumTitle;
    public object MediaPropertiesRcw; // raw WinRT object — PS pulls Thumbnail off it lazily
    public bool IsPauseEnabled;
    public bool IsPlayEnabled;
    public bool IsNextEnabled;
    public bool IsPreviousEnabled;
    public DateTime SnapshotUtc;
  }

  public enum SMTCEventKind {
    SessionAdded, SessionRemoved,
    PlaybackInfoChanged, MediaPropertiesChanged, TimelinePropertiesChanged,
    CurrentSessionChanged
  }

  public class SMTCChangeRecord {
    public SMTCEventKind Kind;
    public string Saumid;
    public DateTime Utc;
  }

  public class SMTCWatcher : IDisposable {
    private object _mgr;             // the IGlobalSystemMediaTransportControlsSessionManager
    private readonly ConcurrentDictionary<string, SMTCSessionSnapshot> _snaps;
    private readonly ConcurrentQueue<SMTCChangeRecord> _events;
    // per-saumid subscription bookkeeping for Dispose:
    private readonly ConcurrentDictionary<string, SessionSubscriptions> _subs;

    public void Initialize(object manager);
    public SMTCSessionSnapshot GetSnapshot(string saumid);
    public string[] GetSaumids();
    public string GetCurrentSaumid();
    public SMTCChangeRecord[] DrainEvents();   // returns and clears queue
    public long EventsReceivedTotal { get; }   // diagnostic counter
    public DateTime LastEventUtc { get; }      // CANARY: detect stuck-events
    public void Dispose();
  }
}
```

**Why pass `object manager` instead of the typed WinRT class?** PowerShell-loaded WinRT types are accessible from C# only with the right reference assemblies. Using `object` + dynamic dispatch via `dynamic` keyword (or reflection) lets the C# code work without a Windows.winmd reference at compile time. `tray_native.cs` already follows this pattern for the audio peak code (uses `object` for IUnknown).

**Implementation notes:**
- All event handlers are `TypedEventHandler<TSender, TArgs>` delegates. Since we accept `object manager`, we use C# `dynamic` to bind events: `dynamic mgr = manager; mgr.SessionsChanged += new TypedEventHandler<...>(OnSessionsChanged);`. Actually simpler: use reflection to wire the events. See Stage 1 implementation guide below for the exact incantation.
- The `MediaPropertiesChanged` handler calls `TryGetMediaPropertiesAsync()` (also via reflection/dynamic) on the WinRT thread pool, awaits the result with `.GetAwaiter().GetResult()` (we ARE on a thread pool thread, no STA blocking), and stuffs the result into the snapshot.
- All snapshot writes are via `ConcurrentDictionary.AddOrUpdate(key, addValue, (k,v) => updateValue)` so they're lock-free.
- `DrainEvents()` builds an array from `Interlocked.Exchange` of the queue (or uses `TryDequeue` in a loop).

**Verification (does NOT require PS integration):**
- Build `_full_rebuild.ps1` step `[1d3/5]` compiles tray_native.cs → tray_native.dll
- DLL signed Valid CN=MasterShadex
- Existing PS code is untouched, still works exactly as before.

**Smoke:** existing 7-step gate still passes (functional smoke 9/9, etc). The new class is dormant.

**Rollback:** revert tray_native.cs to v11.2.3 state.

---

### STAGE 2 — Initialize the watcher at startup, read events into a side-channel log (no behavior change)

**Files changed:**
- `src/tray.ps1` startup section (after WinRT init around line 5176): add ~10 lines to instantiate the watcher.
- `src/tray.ps1` tick handler: add ~5 lines to drain events and write to a debug log.

**Behavior:** the existing polling code STILL runs unchanged. The watcher runs in parallel, observed-only. Used to validate events are firing correctly before we depend on them.

**Pseudocode for startup hook:**
```powershell
# After existing _awaitAsTaskGenericCts binding succeeds:
try {
    # First-time blocking acquisition (one-shot — just for the watcher init)
    $cts = [System.Threading.CancellationTokenSource]::new(2000)
    $asyncOp = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()
    $asTask  = $global:_awaitAsTaskGenericCts.MakeGenericMethod(
                  [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
    $task = $asTask.Invoke($null, @($asyncOp, $cts.Token))
    $task.Wait()  # one-shot, only at startup, before tick starts — OK to block briefly
    $mgr = $task.Result
    $global:_smtcMgrCached = $mgr
    $global:_smtcMgrCacheTime = [DateTime]::UtcNow
    $global:_smtcMgrCacheTTL = 86400000   # 24h — effectively forever
    $global:_smtcMgrCacheHit = $true

    $global:_smtcWatcher = [MasterFM.SMTC.SMTCWatcher]::new()
    $global:_smtcWatcher.Initialize($mgr)
    Log "SMTC watcher initialized (event-driven mode)"
} catch {
    Log "SMTC watcher init failed: $_ — falling back to polling"
    $global:_smtcWatcher = $null
}
```

**Pseudocode for tick (additive, observation only):**
```powershell
# At very top of tick (BEFORE detector chain), drain events to a log
if ($global:_smtcWatcher) {
    $events = $global:_smtcWatcher.DrainEvents()
    if ($events.Count -gt 0) {
        Log ("SMTC events: " + (($events | ForEach-Object { "$($_.Kind):$($_.Saumid)" }) -join ' '))
    }
}
```

**Verification:**
- Skip 2-3 tracks in SoundCloud
- `transcript.log` should show event lines like `SMTC events: PlaybackInfoChanged:com.richardhbtz.soundcloud-rpc MediaPropertiesChanged:com.richardhbtz.soundcloud-rpc`
- Verify no SLOW TICK from the new code (drain should be < 1 ms)
- 30-min memory soak — must show the same memory profile as v11.2.3 (event subscriptions don't leak)
- Existing detection still works (because old polling is still running)

**Rollback:** revert the two startup + tick edits. Watcher class stays in DLL but is unused.

---

### STAGE 3 — Wire the watcher snapshot through the existing cached helpers (cache-aside, polling fallback retained)

**Files changed:**
- `src/tray.ps1`: change the bodies of 4 helpers — `Get-SMTCSessionsCached`, `Get-SMTCPlaybackInfoCached`, `Get-SMTCMediaPropsCached`, `Get-SMTCPosition`.

**Pattern for each:** read from the watcher snapshot first; if miss, fall back to the existing polling code.

**Get-SMTCSessionsCached (revised, ~20 lines):**
```powershell
function Get-SMTCSessionsCached {
    if ($global:_smtcCacheTickId -ne $global:_diagTickCount) {
        $global:_smtcCacheTickId   = $global:_diagTickCount
        $global:_smtcSessionsCache = $null
    }
    if ($null -ne $global:_smtcSessionsCache) { return $global:_smtcSessionsCache }

    if ($global:_smtcWatcher) {
        # Build the per-tick session-list view from the watcher's snapshot dict.
        # The watcher holds session refs internally; expose them via a method.
        $sessions = $global:_smtcWatcher.GetSessions()
        if ($sessions) {
            $global:_smtcSessionsCache = @($sessions)
            $global:_smtcSessionsCacheLastGood = $global:_smtcSessionsCache
            return $global:_smtcSessionsCache
        }
    }

    # Fallback: legacy polling path (unchanged from v11.2.3)
    $mgr = Get-SMTCManager
    if (-not $mgr) { $global:_smtcSessionsCache = @(); return @() }
    try {
        $global:_smtcSessionsCache = @($mgr.GetSessions())
        $global:_smtcSessionsCacheLastGood = $global:_smtcSessionsCache
    } catch {
        $global:_smtcSessionsCache = if ($global:_smtcSessionsCacheLastGood) { $global:_smtcSessionsCacheLastGood } else { @() }
    }
    return $global:_smtcSessionsCache
}
```

**Get-SMTCPlaybackInfoCached (revised, ~10 lines):**
```powershell
function Get-SMTCPlaybackInfoCached($Session) {
    $key = $Session.SourceAppUserModelId
    if ($global:_smtcWatcher) {
        $info = $global:_smtcWatcher.GetPlaybackInfo($key)
        if ($info) { return $info }
    }
    # Fallback (legacy)
    $info = $Session.GetPlaybackInfo()
    return $info
}
```

(The transition-guard arming on Changing-status is REMOVED — there's nothing to guard against because there's no synchronous polling left to suppress. The snapshot has whatever the latest state is.)

**Get-SMTCPosition cache section (revised, ~8 lines for the cache check):**
The existing function is large (~80 lines, lines 6520-6605). Only the cache check at the top changes:
```powershell
$_tlKey = $Session.SourceAppUserModelId
if ($global:_smtcWatcher) {
    $tl = $global:_smtcWatcher.GetTimeline($_tlKey)
    if ($tl) {
        # Bypass the legacy cache + staleness check entirely
        # (jump to the position-extrapolation logic below using $tl)
        ...
    }
}
# Legacy fallback path follows
```

**Get-SMTCMediaPropsCached (revised, ~15 lines):**
```powershell
function Get-SMTCMediaPropsCached {
    param($Session, [int]$TimeoutMs = 150)
    if (-not $Session) { return $null }
    $key = $Session.SourceAppUserModelId
    if ($global:_smtcWatcher) {
        $props = $global:_smtcWatcher.GetMediaProperties($key)
        if ($props) { return $props }
        # Watcher knows of session but props not yet captured — defer to legacy fire-and-poll for cold-start
    }
    # Legacy fire-and-poll path (unchanged) — only runs on cold start
    ...existing v11.2.3 body...
}
```

**Verification:**
- All existing detection works (functional smoke 9/9)
- `winrt_calls/min` drops from ~85 (v11.2.3) to under 5/min (only initial cold-start calls). The `winrt_calls` counter increments only when PS fires a WinRT call — events don't bump it.
- Track-skip FPS test (mandatory): user runs game with FPS counter, skips 3 tracks. **Expect: zero or sub-5ms blip per skip.**
- Art refresh test: skip a track, verify Discord/OBS show new art within 200ms.
- 30-min memory soak: target growth < 5 MB.
- Memory soak verifies the watcher doesn't leak event subscriptions.

**Rollback:** revert the 4 function bodies to v11.2.3. Watcher continues running but isn't consulted.

---

### STAGE 4 — Remove the polling machinery and freeze the manager

**Files changed:**
- `src/tray.ps1`: remove rate-limit/staleness machinery from `Get-SMTC*` functions (the legacy fallback paths). Remove `_smtcMgrCacheTTL` re-acquisition.

**Removals:**
- `Get-SMTCManager` body simplified: if `_smtcMgrCached` exists, return it. No re-acquisition.
- Remove `_smtcSessionsCacheMs`, the 500ms staleness check, the transition guard read in `Get-SMTCSessionsCached`.
- Remove `_smtcPbInfoCacheMs`, the 500ms staleness check, the transition guard read, the Changing-status arm in `Get-SMTCPlaybackInfoCached`.
- Remove `_smtcTlCacheMs`, the 500ms staleness check in `Get-SMTCPosition`.
- Remove `_smtcPropsLastFiredMs`, `_smtcPropsTaskDict`, `_smtcPropsCtsDict`, `_smtcPropsFiredThisTick` and their cleanup in `Get-SMTCMediaPropsCached`. The function becomes a one-line passthrough to the watcher.
- Remove `_smtcTransitionGuardMs` global and all references.
- Remove `_smtcNpCacheMs` 300ms TTL (or keep at very small value like 50ms — diminishing returns).

**Estimated removed lines:** ~120 lines of guards, rate-limits, in-flight tracking.

**Estimated remaining lines in the 4 helpers:** ~30 lines total (down from ~250).

**The new bodies are tiny and obviously correct.** That is the architectural goal: machinery that doesn't exist can't regress.

**Verification:**
- Same as Stage 3
- Plus: 4-hour soak — memory growth target < 5 MB total
- Plus: track skip every 5s for 5 min — verify no FPS spike
- Plus: leave running 8h with various detected sources (Spotify, SoundCloud, browsers) — verify all detect correctly

**Rollback:** restore from v11.2.3 backup (Stage 4 is the breaking-changes stage).

---

### STAGE 5 — Add CANARY safety net + verification gate

**Files changed:**
- `src/tray.ps1`: extend the existing CANARY (line 9213) with watcher health metrics.
- `src/tray.ps1`: add a manager re-acquisition fallback for the unlikely case events stop firing.

**New CANARY fields:**
```
[CANARY] mem=XMB ... smtc_events=N smtc_lag=Sm [OK]
```
where `smtc_events` is `_smtcWatcher.EventsReceivedTotal` delta over 60s, and `smtc_lag` is seconds since `_smtcWatcher.LastEventUtc`.

**Stuck-events fallback:**
```powershell
# Once per 60s in the CANARY block:
if ($global:_smtcWatcher) {
    $silentSec = ([DateTime]::UtcNow - $global:_smtcWatcher.LastEventUtc).TotalSeconds
    if ($silentSec -gt 300 -and $hasActiveSource) {
        Log "SMTC watcher silent for ${silentSec}s with active source — re-initializing"
        try { $global:_smtcWatcher.Dispose() } catch {}
        $global:_smtcWatcher = $null
        $global:_smtcMgrCached = $null
        # next tick will re-acquire manager and re-init watcher
    }
}
```

**Final verification gate (must pass before push):**
- 9/9 functional smoke
- 7-step gate
- track-skip FPS test: 0-5ms blip across 5 skips at 10s intervals (user testing in-game)
- art refresh test 5b
- 4-hour memory soak with target < 5 MB growth
- All 4 detector backends still detect (manual test: SoundCloud, Spotify, foobar/WMP/VLC if available)
- Discord RPC update latency: visually < 200ms after track change
- No SLOW TICK entries
- `winrt_calls/min` < 5 in steady state (down from ~85)
- `smtc_events/min` > 0 when track is changing

---

## SECTION 5 — RISK REGISTER

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|------------|--------|------------|
| 1 | WinRT event handler signature wrong via reflection in C# (events don't fire) | Medium | High (no metadata updates) | Stage 2 logs every event. If logs show no events for 60s with active source, rebind via dynamic dispatch with explicit `TypedEventHandler<,>` types. Have a working PoC pre-committed in a unit test. |
| 2 | Event subscription leak (handlers not unsubscribed on session removal) | Medium | Medium (memory creep) | `SMTCWatcher.Dispose()` and per-session `Unsubscribe(saumid)` invoked from `SessionsChanged` handler with diff logic. Memory soak in Stage 2/4 verifies. |
| 3 | Manager goes stale (events stop firing without notice) | Low | High (silent failure) | Stage 5 CANARY: if no events for 5 min during active source → re-init watcher. Diagnostic log entry. |
| 4 | Race condition on initial enumeration vs. `SessionsChanged` event arriving early | Medium | Medium (duplicate or missed session) | `Initialize()` does the enumeration BEFORE attaching `SessionsChanged` handler — but then might miss events arriving during enum. Solution: use `lock(_subs)` to guard the diff-and-subscribe section so SessionsChanged callbacks queue until init completes. |
| 5 | `MediaPropertiesChanged` fires on a thread that can't run `TryGetMediaPropertiesAsync` | Low | Medium (no media props) | The handler is C# code on a thread pool thread. `TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult()` works fine in MTA. Pre-test in Stage 1 with a console app. |
| 6 | v11.2.1 SAUMID fix regression | Low | Critical (unbounded leak) | Watcher uses `SourceAppUserModelId` as the dictionary key. Same key everywhere. Code review checklist includes "verify SAUMID is the key in C# class fields and PS-side helper indices". |
| 7 | Detector failure: one of 4 backends silently stops | Medium | High | Stage 5 verification includes manual test of each backend. Add a small "smtc_detect_lag" counter: time from event arrival to webhook send. If > 500ms, flag. |
| 8 | Performance regression: event handler overhead exceeds polling overhead | Very Low | Low | Events fire at most ~5/sec in steady state (one per session per signal). Each handler is a C# write to ConcurrentDictionary — ~1 microsecond. Negligible. |
| 9 | Cold-start race: detector runs before watcher has any snapshot yet | Medium | Low (1 tick of "no track" then resolves) | Acceptable. The fallback in Stage 3 still calls `mgr.GetSessions()` if the watcher snapshot is empty, ensuring a valid first detection. After Stage 4 the fallback is gone, but the watcher's `Initialize()` enumerates synchronously, so by the time the first tick fires the snapshot IS populated. |
| 10 | C# event handler exception kills subsequent events | Medium | High | Wrap every event handler body in try/catch. Log via a dedicated log file (`smtc_watcher.log`) so PS still sees errors. Failures don't unsubscribe the handler. |
| 11 | tray_native.dll signing fails after schema change | Low | Medium (build breaks) | Same signing flow as today. Verify in Stage 1 that the new class compiles + signs cleanly before Stage 2. |
| 12 | RuntimeError in PS calling new methods if DLL not present (old install) | Very Low | Low (auto-update will replace it) | Wrap watcher init in try/catch. If init fails (e.g. DLL is the old version mid-update), fall back to legacy polling. After auto-update the DLL is replaced. |
| 13 | `dynamic` keyword not available without `Microsoft.CSharp.dll` reference | Medium | Low | Use reflection (`MethodInfo.Invoke`) instead of `dynamic` to bind WinRT events. More verbose but bulletproof. |
| 14 | Old WinRT API misuse — TypedEventHandler<,> signature slightly different per event | Low | Medium | Each of the 5 event types has its own delegate signature. Bind each separately with explicit reflection. Document each in code with the exact signature. |
| 15 | Tester on Windows 10 build < 17763 has incomplete SMTC API | Very Low | Low | App already requires Windows 10 1809+ for SMTC. New events were available since 1709. Drop oldest support window check at watcher init: if event-bind fails, fallback to polling. |

---

## SECTION 6 — SUCCESS CRITERIA

Measurable, with thresholds.

| Metric | Target | How measured |
|--------|--------|--------------|
| FPS drop on track change | **< 5 ms blip** (target: 0 — equivalent to "Master's FM closed" baseline) | User test in-game with FPS overlay, 5 skips at 10s+ intervals |
| Memory leak rate | **< 2 MB/hr** active (was 0.3 MB/min in v11.2.3) | 4-hour soak, before/after Working Set delta |
| Idle CPU | **< 1 %** of 1 core (was 1.5% in v11.2.2) | 5-min idle measurement on Ryzen 7 7800X3D |
| Art refresh latency | **< 250 ms** from SMTC track change to OBS overlay update | Wall-clock timing: skip → observe overlay change |
| Discord RPC update latency | **< 250 ms** from track change | Wall-clock |
| All 4 audio backends detect | 4/4 detectors functional | Manual test each (SoundCloud-rpc, Spotify, browser, foobar/WMP/VLC if avail) |
| `winrt_calls/min` (CANARY) | **< 5/min** in steady state | transcript.log CANARY entries |
| `smtc_events/min` (new) | **>= 1/min** when track playing | new CANARY field |
| `smtc_lag` (new, seconds since last event) | **< 60s** when source active | new CANARY field |
| SLOW TICK count over 1h | **0** | transcript.log scan |
| 7-step gate | **all pass** | gate runner script |
| Track-skip "rapid skip burst" stress test | 0 SLOW TICKs, no error log entries, all skips processed | manual: 10 skips in 10 seconds in SoundCloud |
| Watcher survives 24h continuous run | events still firing on track change | overnight soak |

---

## SECTION 7 — WHAT NOT TO CHANGE

These are intentionally out of scope. Do not refactor them, even if the diff is tempting:

1. **`Send-WebhookAsync` (line 5187)** — already non-blocking via `HttpClient.PostAsync`. Confirmed by V112X_WEBHOOK_TEST_RESULT.md (disabling it changes nothing).
2. **`Invoke-DeferredThumbExtraction` (line 6076)** — clean state machine for album-art extraction. Album-art extraction is already deferred and non-blocking. Keep as-is.
3. **`Get-SMTCThumbnailDataUri` (line 6056)** — works in tandem with `Invoke-DeferredThumbExtraction`. Not on the SMTC ALPC path.
4. **`Write-SMTCArtCacheEntry` LRU helper (line 6046)** — bounded 200-entry cache, working as intended.
5. **The detector chain dispatch logic (line 8690)** — keep all 10 detectors. Don't unify them. SMTC stays its own detector. Spotify/Browser/WMPSMTC continue to call `Find-SMTCSession`/`Get-SMTC*Cached` — they automatically benefit from the watcher.
6. **CANARY logging (line 9213)** — extend, don't replace. Add new fields to existing `[CANARY]` line format. Keep all v11.x metrics.
7. **Auto-update infrastructure (`Poll-UpdateCheck`, `Show-UpdateWindow`, `Install-Update`, the helper here-string)** — sacred, especially the msiexec line. See memory.md HARD CONSTRAINTS.
8. **Build pipeline files** (`_full_rebuild.ps1`, `build_msi.py`, `_sign_msi.ps1`, `INSTALL.bat`, `build_tools/`) — sacred per CLAUDE_CODE_INSTRUCTIONS constraint #2.
9. **The five protected files** (CLAUDE.md, save-tokens.md, tools.md, onboard.md, memory.md) — read-only during the run, append-only after.
10. **`tray_launcher.cs` STA setup** — keep `[STAThread]` + `ApartmentState.STA` + `UseCurrentThread`. The new architecture relies on the watcher events firing OFF the STA — that's the WinRT runtime's behavior for MTA singletons (which the SMTC manager is). Don't change apartment to MTA — would break WinForms.
11. **PowerShell 5.1 boundaries** — no Start-ThreadJob, no `?.`/`??`, no em-dashes in script (encoding traps).
12. **Soundcloud-rpc title-prefix override (line 7140-7217)** — orthogonal logic that overrides PlaybackStatus from a window-title scan. Continues to work because the override reads from the watcher snapshot's PlaybackStatus, which is event-fed.
13. **`_awaitAsTaskGenericCts` reflection setup (line 5145)** — keep. Used by Invoke-DeferredThumbExtraction (in scope to KEEP unchanged) and by the new one-shot manager-init in Stage 2.
14. **Process priority** — stays BelowNormal for tray. Don't bump to fix anything.

---

## EXECUTION GUIDANCE FOR THE NEXT SESSION

The next session (Sonnet, fresh context) will read this plan plus tray.ps1. They should:

1. Read this entire document
2. Read SECTION 1 of memory.md (CHANGELOG entries v11.2.0 through v11.2.3) for fix-preservation details
3. Read tray.ps1:5145-5176 (WinRT init), 5761-5823 (Get-SMTCManager), 5933-6005 (Get-SMTCMediaPropsCached) to understand the existing async patterns
4. Execute Stage 1 (C# class) IN ISOLATION — full rebuild + DLL signed + tray functional via existing polling. Stop. Verify. Commit.
5. Execute Stage 2 (init + observe) — verify events fire via log inspection. Stop. Verify. Commit.
6. Execute Stage 3 (cache-aside) — verify track-skip FPS in-game. Stop. Verify. Commit.
7. Execute Stage 4 (remove polling) — verify track-skip FPS again. Stop. Verify. Commit.
8. Execute Stage 5 (CANARY) — run final 7-step gate + 4-hour soak. Stop. Commit.
9. Push as v11.2.4 ONLY after all stages pass and the user confirms in-game track-skip blip is < 5 ms.

If any stage fails verification, revert that stage and stop. Do not continue.

If WinRT event binding via reflection fails, fall back to: keep watcher running but as a polling cache filler (separate runspace MTA polling at high frequency, see SECTION 2.2 alternative) — this is partial fix, would still help if events outright don't work. Escalate to user before deciding.

---

## DOCUMENT REFERENCES (for future sessions)

- `V1116_FINAL_REPORT.md` — empirical confirmation of v11.2.1 SAUMID instability. Keep using SAUMID as cache key.
- `V1121_FINAL_REPORT.md` — v11.2.1 SAUMID fix details, SLOW TICK=0 baseline confirmed.
- `V1122_FINAL_REPORT.md` — v11.2.2 staleness guard + rate-limit + transition guard. All being removed; symptoms preserved by event-driven design.
- `V1123_FINAL_REPORT.md` — v11.2.3 art refresh fix. Preserved by `MediaPropertiesChanged` event firing immediately on title change.
- `V112X_LAG_DIAGNOSIS.md` — instrumentation showed all tick ops < 3 ms. Confirms lag is at the SMTC service contention layer, not in the tray.
- `V112X_WEBHOOK_TEST_RESULT.md` — confirmed webhook send is NOT the cause.
- (in-flight) "FPS drop with SMTC fully disabled = single 10-20 ms blip" — confirmed root cause is SMTC polling. Empirical baseline for "what zero polling looks like".

---

ARCHITECTURE PLAN COMPLETE — ready for execution. Estimated execution time: **3-4 hours across 5 staged commits**. Recommended next session: **Sonnet** (architectural complexity is contained to one C# class plus thin PS bodies; the heavy lifting was the design done here). Use Opus only if the WinRT reflection event-binding turns out to need creative debugging — Stage 1 is the critical proof-of-concept; if it works, the rest is mechanical.
