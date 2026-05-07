# V14 Researcher 2 — Critical-Path Behavior Catalog

> **Scope:** All shipped fixes from v9.5.0 onward that carry non-obvious implementation
> details that could be silently lost in a .NET 8 rewrite.
> **Status:** READ-ONLY analysis — no source files were modified.
> **Files read:** `md/memory.md`, `src/tray.ps1`, `src/tray_native.cs`

---

## Behaviors That MUST Be Preserved

---

### 1. WinRT AsTask — 2-arg CancellationToken overload (v9.9.4)

- **Bug it solved:** After one overnight run, `MastersFM_Tray.exe` grew to 1,879.5 MB RAM,
  106,713+ handles, and 17,150+ threads. Root cause: `Await-WinRT` used the 1-arg
  `AsTask<T>(IAsyncOperation<T>)` overload. When a third-party SMTC app
  (`soundcloud-rpc`) triggered `SERVERCALL_RETRYLATER`, `RequestAsync()` timed out and
  the 1-arg overload left the `IAsyncOperation` COM proxy **alive and unregistered**.
  Each orphaned proxy held 1 LPC back-channel thread + ~5 OS handles. 797 timeout events
  accumulated over 10.2 hours.

- **Current expression:** `tray.ps1` lines 5250-5260 — startup code locates and binds
  `_awaitAsTaskGenericCts`, the 2-arg overload:
  ```
  $_.GetParameters()[1].ParameterType.FullName -eq 'System.Threading.CancellationToken'
  ```
  `tray.ps1` lines 5783-5820 — `Await-WinRT` uses this overload for all timeout paths.
  When the CTS fires at `TimeoutMs`, the 2-arg implementation cancels the async op,
  unregisters the Completed handler, and transitions the Task to Canceled state.
  The `finally` block always disposes both CTS and Task, releasing COM proxy handles.

- **Non-obvious detail:** The 1-arg and 2-arg overloads of `AsTask` are both present in
  `System.WindowsRuntimeSystemExtensions` and both compile without error. The difference
  is entirely runtime behavior on timeout: the 1-arg overload leaves the
  `IAsyncOperation` proxy alive indefinitely (GC eventually collects it but far too
  slowly under SMTC stress). The 2-arg overload calls `asyncOp.Cancel()` synchronously
  through the CTS callback, which forces the WinRT runtime to release the cross-process
  LPC channel before the task enters Canceled state. **If you use `await` on .NET 8 with
  a `CancellationToken`, .NET handles cancellation correctly — but only if you pass the
  token; a bare `await` without cancellation reproduces the original bug.**

- **Migration risk:** High
- **How to preserve in .NET 8:** Use `await op.AsTask(CancellationToken)` or
  `await op.AsTask().WaitAsync(CancellationToken)` for ALL SMTC WinRT calls. Never use
  bare `await asyncOp` or `await Task.Run(() => asyncOp.GetResults())` without a bounded
  cancellation token. The token must cancel the underlying WinRT operation, not just
  abandon the .NET Task.

---

### 2. SAUMID as per-session cache key (v11.2.1)

- **Bug it solved:** ~185 MB/hr memory leak and CPU spike on track change. SMTC manager
  TTL is 600ms; each re-acquisition via `RequestAsync()` returns new COM proxy (RCW)
  objects for the same logical sessions. Each new RCW has a new managed identity
  (`GetHashCode()` differs). Five per-session cache dictionaries keyed by hash accumulated
  entries without eviction: `_smtcPropsResultCache`, `_smtcPbInfoCache`,
  `_smtcPbInfoCacheMs`, `_smtcTlCache`, `_smtcTlCacheMs`. Growth was ~172 entries/min
  (= ~87 manager refreshes/min × 2 sessions). SoundCloud-RPC was the trigger; Spotify
  did not leak because its session COM identity happened to be stable.

- **Current expression:** `tray.ps1` lines:
  - ~6058: `$key = $Session.SourceAppUserModelId` in `Get-SMTCPlaybackInfoCached`
  - ~6096: `$key = $Session.SourceAppUserModelId` in `Get-SMTCMediaPropsCached`
  - (legacy `Get-SMTCPosition`): uses `SourceAppUserModelId` as `$_tlKey`
  - `tray_native.cs` `GetSaumidSafe()` (line ~748): also reads `SourceAppUserModelId`
    for all C# snapshot dictionary keys.

- **Non-obvious detail:** `GetHashCode()` on WinRT RCWs is the managed object identity
  of the CLR-side proxy wrapper, not the identity of the underlying COM object.
  `RequestAsync()` returns a **new** manager object every call, and `GetSessions()` on
  that new manager returns **new** proxy wrappers for the same logical sessions.
  `SourceAppUserModelId` is a property on the WinRT session that identifies the
  originating process (e.g., `com.richardhbtz.soundcloud-rpc`) — it is stable across
  all manager acquisitions and COM proxy recreations because it comes from the SMTC
  service itself, not from CLR object identity. Any cache keyed by object reference
  or hash will leak in the same way; only string keys derived from stable WinRT
  properties are safe.

- **Migration risk:** High
- **How to preserve in .NET 8:** In native WinRT on .NET 8, `GlobalSystemMediaTransportControlsSession`
  is a WinRT object; C# `==` compares reference identity which may or may not be stable.
  Always key session caches on `session.SourceAppUserModelId` (a string), never on
  the session object reference or its `GetHashCode()`.

---

### 3. TryGetMediaPropertiesAsync cleanup in `finally` (v11.2.3)

- **Bug it solved (v11.2.2 regression):** In v11.2.2, `_smtcPropsFiredThisTick.Remove($key)`
  was placed inside the *title-change branch* of the task completion handler.
  On startup, the first task fired and completed, title didn't change (same track as
  session init), so Remove was never called. The key remained set permanently, blocking
  all subsequent `TryGetMediaPropertiesAsync` tasks from firing. Album art was stuck
  on the startup track forever. The app appeared to work (heartbeat webhooks continued)
  but the server never updated track info again.

- **Current expression:** `tray.ps1` line ~6132 (inside the `finally` block of the task
  completion section in `Get-SMTCMediaPropsCached`):
  ```powershell
  $global:_smtcPropsFiredThisTick.Remove($key)   # v11.2.3: unconditional...
  ```
  The comment explicitly notes it was moved from the title-change branch.

  Also: `_smtcPropsLastFiredMs` hashtable (line ~5988) + 500ms rate-limit check
  (line ~6140: `$_propsRateOk = ($_propsNowMs - $_propsLastMs) -ge 500`) added in
  the same fix to prevent the v11.1.0 9 MB/min memory regression from coming back
  if `_smtcPropsFiredThisTick` is cleared too eagerly.

- **Non-obvious detail:** This is a two-constraint trap:
  1. If cleanup is in the `finally` block (unconditional), the rate-limit guard on
     `_smtcPropsLastFiredMs` must exist — otherwise `TryGetMediaPropertiesAsync` fires
     every tick (~10/sec), growing memory at 9 MB/min (v11.1.0 regression).
  2. If cleanup is in a conditional branch, tasks permanently stop firing after the
     first non-title-change completion (v11.2.2 regression).
  Both constraints must be satisfied simultaneously: `finally` for unconditional
  cleanup + `_smtcPropsLastFiredMs` 500ms gate for rate-limiting.

- **Migration risk:** High
- **How to preserve in .NET 8:** Any async task that gates re-firing on its own
  in-flight flag must clear that flag unconditionally (in `finally`), with a separate
  time-based rate-limit preventing excessive re-fires. Never gate the cleanup on a
  content-comparison result (e.g., "only clear if title changed").

---

### 4. SMTC event-driven architecture — WinRT event subscription via reflection (v12.0.0)

- **Bug it solved:** System-wide FPS lag spike on every track change. Under the polling
  architecture, every tick (100ms) made synchronous ALPC calls to the SMTC service.
  During a track change (especially with soundcloud-rpc), `PlaybackInfoChanged` alone
  fires 8+ events in <100ms; each poll during this window hit a locked SMTC service,
  causing 3-5 seconds of 600→0 FPS oscillation reported by all testers.

- **Current expression:** `tray_native.cs` — the `SMTCWatcher` class (~430 LOC,
  lines 299-833). Key structural elements:
  - `AttachEvent()` method (lines 760-801): builds a runtime delegate of the correct
    `TypedEventHandler<,>` type via `System.Linq.Expressions`, then calls
    `evt.GetAddMethod().Invoke(source, new object[] { del })` to subscribe. Captures
    the returned `EventRegistrationToken` in an `EventBinding` struct.
  - `DetachBinding()` (lines 804-808): calls `b.RemoveMethod.Invoke(b.Source, new object[] { b.Token })`.
  - `EnumerateAndSubscribeSessions()` (lines 397-433): RCW-identity check
    (`!ReferenceEquals(existing.Session, s)`) to detect soundcloud-rpc session recycling.
  - `OnSessionsChanged()` (lines 504-541): coalesces multiple events within 750ms into
    one re-enumeration (avoids re-subscribing during rapid skipping).
  - Burst window (`BurstWindowMs = 800`, `_burstUntilTicks`): during SessionsChanged
    bursts, all ALPC reads inside per-session event handlers are skipped entirely.
  - Per-session `ReadCoolMs = 250`: rate-limit individual reads so an 8-event storm
    produces at most 1 ALPC call.

- **Non-obvious detail (critical — WinRT event binding):** The standard CLR
  `EventInfo.AddEventHandler()` throws at runtime on WinRT events:
  > "Adding or removing event handlers dynamically is not supported on WinRT events."
  
  The correct approach (used here) is:
  1. Get `EventInfo` to read `EventHandlerType` and locate add/remove accessors.
  2. Build the delegate of type `EventHandlerType` using `Expression.Lambda`.
  3. Call `evt.GetAddMethod().Invoke(source, new[] { delegate })` — this bypasses
     the CLR's EventInfo dispatch and goes directly to the WinRT `add_X` accessor.
  4. The `add_X` accessor returns an `EventRegistrationToken` (a WinRT-specific
     unsubscribe handle). Capture it.
  5. To unsubscribe: call `evt.GetRemoveMethod().Invoke(source, new[] { token })`.
  
  If you migrate to .NET 8 with native WinRT projections, you can use `+=`/`-=`
  syntax directly and the token management is handled automatically. But the
  subscription lifecycle rules below still apply.

- **Non-obvious detail (subscription lifecycle):** Subscription leaks were the cause
  of the v11.1.0 9 MB/min memory regression. The rules are:
  - Subscribe at session-added time; unsubscribe at session-removed time and at Dispose.
  - `fromSessionsChanged=false` path in `UnsubscribeFromSession` does NOT remove the
    snapshot from `_snaps` (avoids data races during re-enumeration); only
    `fromSessionsChanged=true` removes it.
  - soundcloud-rpc recycles its session object on every track change (same SAUMID,
    different RCW). The RCW-identity check in `EnumerateAndSubscribeSessions` detects
    this and replaces the subscription without leaking handlers on the dead session.

- **Migration risk:** High
- **How to preserve in .NET 8:** On .NET 8 with C# WinRT projections (`cswinrt`), you
  can use `+=` event syntax directly and tokens are managed by the runtime. The three
  concurrency constraints must still be preserved:
  1. Burst window: suppress ALPC reads for 800ms after SessionsChanged/CurrentSessionChanged.
  2. Per-session rate-limit: coalesce rapid per-session events (250ms cooldown).
  3. SessionsChanged coalescing: collapse rapid session-list changes into one
     re-enumeration with a 750ms quiet window (avoid re-subscribing mid-burst).

---

### 5. Rate-limiting cache wrapper for Get-SMTCNowPlaying (v11.2.2)

- **Bug it solved:** `Get-SMTCNowPlaying` was called every 100ms tick but took ~79ms
  to execute (PowerShell interpreter overhead on the ~230-line function). CPU was ~79%
  of 1 core sustained.

- **Current expression:** `tray.ps1` lines 7221-7236 — `Get-SMTCNowPlayingCached`
  wrapper:
  ```powershell
  if ($global:_smtcNpCacheMs -and (($_npNowMs - $global:_smtcNpCacheMs) -lt 300)) {
      return $global:_smtcNpCache
  }
  ```
  The tick dispatcher (line ~8847) calls `Get-SMTCNowPlayingCached`, not
  `Get-SMTCNowPlaying`.

- **Non-obvious detail:** `Start-ThreadJob` is not available on PowerShell 5.1 without
  the ThreadJob module (which is not installed). A Runspace-based async approach would
  require serializing all global state. The rate-limiting wrapper is the maximum
  achievable on PS 5.1 — it is NOT full async. The 300ms staleness is intentional:
  reducing it below ~200ms does not help because `Get-SMTCNowPlaying` itself takes
  79ms, so calls would overlap on some ticks. The watcher (v12.0.0) makes this largely
  moot in steady state, but the legacy path is still the cold-start and fallback path.

- **Migration risk:** Low (C# is fast enough that this pattern is not needed in .NET 8)
- **How to preserve in .NET 8:** Not needed. Native C# `Get-SMTCNowPlaying` equivalent
  runs in <5ms. The 300ms staleness wrapper can be removed. However, per-session
  rate-limiting (separate from this) MUST be preserved — see item 3 and item 4.

---

### 6. GetSessions() 500ms staleness guard (v11.2.2 Fix 2)

- **Bug it solved:** `GetSessions()` (synchronous ALPC) was firing ~600 times/minute
  because it was called fresh every tick with no cache. Each call produces a new batch
  of RCW wrappers, all of which accumulate in finalizer queue. With the buggy
  `GetHashCode()` cache key (pre-v11.2.1), each new set of RCWs created new unbounded
  cache entries. Even after v11.2.1 fixed the key, 600 unnecessary `GetSessions()`
  calls/min still caused excessive RCW churn.

- **Current expression:** `tray.ps1` lines ~6030-6043 in `Get-SMTCSessionsCached`:
  ```powershell
  $_sessFresh = $global:_smtcSessionsCacheMs -and
                (($_sessNowMs - $global:_smtcSessionsCacheMs) -lt 500)
  if (($global:_smtcSessionsCacheLastGood) -and
      (($_sessNowMs -lt $global:_smtcTransitionGuardMs) -or $_sessFresh)) {
      $global:_smtcSessionsCache = $global:_smtcSessionsCacheLastGood
  ```

- **Non-obvious detail:** The v12.0.0 watcher path short-circuits before this code
  runs (lines 6016-6022), so in steady state this guard is only exercised on the
  legacy fallback path (cold-start, watcher failure, or sources where watcher has
  no sessions). The guard must remain for correctness in the fallback path.

- **Migration risk:** Low (rendered irrelevant by v12.0.0 watcher, but must stay in
  legacy path)
- **How to preserve in .NET 8:** The event-driven architecture makes this unnecessary
  in the primary path. Keep a staleness guard on any synchronous SMTC calls in the
  fallback/cold-start path.

---

### 7. Art cache 200-entry LRU cap (v11.0.0)

- **Bug it solved:** `_smtcArtCache` (album art data URIs) was unbounded. Long shuffle
  sessions with hundreds of unique tracks accumulated hundreds of MB of base64-encoded
  art in memory.

- **Current expression:** `tray.ps1` lines 6170-6171 (globals):
  ```powershell
  $global:_smtcArtCache      = @{}
  $global:_smtcArtCacheOrder = [System.Collections.Generic.Queue[string]]::new()
  ```
  Lines 6202-6208 — `Write-SMTCArtCacheEntry` helper:
  ```powershell
  $global:_smtcArtCacheOrder.Enqueue($key)
  if ($global:_smtcArtCacheOrder.Count -gt 200) {
      $global:_smtcArtCache.Remove($global:_smtcArtCacheOrder.Dequeue())
  }
  ```

- **Non-obvious detail:** The Queue (`_smtcArtCacheOrder`) and the Hashtable
  (`_smtcArtCache`) must be kept in sync. If `_smtcArtCache` is ever manually cleared
  without also clearing `_smtcArtCacheOrder`, the Queue will contain stale keys;
  future `Dequeue()` operations will attempt to remove keys that no longer exist
  (harmless) and the LRU bound will drift (the effective cap becomes `200 + orphan count`).
  Conversely clearing the Queue without the Hashtable leaves unreferenced entries
  that never get evicted. Both must always be reset together.

- **Migration risk:** Medium
- **How to preserve in .NET 8:** Use a proper `LruCache<K,V>` or
  `ConcurrentDictionary` with a count-bounded eviction policy. Do not use two
  separate data structures (dictionary + queue) without a single synchronized entry point.

---

### 8. Deferred album art extraction / non-blocking thumbnail fetch (v9.9.9)

- **Bug it solved:** `Get-SMTCThumbnailDataUri` called `Await-WinRT` with no timeout
  on the UI thread, blocking for 100-600ms per skip. Even with a 150ms cap, users
  still felt lag on every track change.

- **Current expression:** `tray.ps1` — `Get-SMTCThumbnailDataUri` returns `''`
  immediately on every call; it queues the pending key in `_smtcArtPendingKey`/
  `_smtcArtPendingProps`. `Invoke-DeferredThumbExtraction` (called from heartbeat/
  new-track ticks, 400ms later) runs a 4-state machine
  (`idle → waiting → opening → loading`) where each state fires one async Task and
  returns immediately. No `.Wait()` at any stage. The art arrives in a follow-up webhook.

- **Non-obvious detail:** The deferred approach means album art arrives in a **second**
  webhook call after the track-change webhook. Any consumer of these webhooks (the
  OBS overlay server) must tolerate receiving two consecutive webhooks for the same
  track: the first with no art URL, the second with the art URL. If the consumer
  replaces the displayed track only on a "new track" signal, it must also process the
  art-update signal without resetting the track timer.

- **Migration risk:** Medium
- **How to preserve in .NET 8:** Use `await` with a `CancellationToken` and post the
  art via a follow-up webhook once available. Do NOT block the main track-change
  notification path waiting for the art stream to open. The two-phase webhook pattern
  (track now, art later) must be preserved.

---

### 9. SMTC manager held for app lifetime (v12.0.0)

- **Bug it solved:** Pre-v12.0.0, `Get-SMTCManager` re-acquired the manager via
  `RequestAsync()` every 600ms. Each acquisition costs ~10ms in the fast path,
  and up to 1500ms when `SERVERCALL_RETRYLATER` is active (soundcloud-rpc scenario).
  This was 87 `RequestAsync()/min` of avoidable ALPC waste, and the 600ms TTL was
  itself the source of the RCW hash instability (see item 2).

- **Current expression:** `tray_native.cs` lines 357-395 — `SMTCWatcher.Initialize()`
  accepts the manager object once at startup and holds it in `_mgr` for the app's
  lifetime. `tray.ps1` lines 5272-5293 — one synchronous blocking acquisition at
  startup (before the tick timer starts), passed into `_smtcWatcher.Initialize()`.
  The manager is never re-acquired after that.

- **Non-obvious detail:** Holding the manager for app lifetime means that if the SMTC
  service restarts (edge case), the watcher will become stale. The stuck-events
  fallback in the PS tick (not shown here) re-initializes the watcher if no events
  have been received for 5+ minutes with an active source. This timeout is the
  "circuit breaker" for the manager-lifetime assumption. Without this fallback, a
  SMTC service restart would produce permanent silence.

- **Migration risk:** Medium
- **How to preserve in .NET 8:** Acquire the manager once at startup; subscribe to
  events; do not re-acquire on every poll cycle. Add a health-check (e.g., if
  `LastEventUtc` is >5min old and a source is known to be playing), re-initialize
  the watcher.

---

### 10. WMP Deezer lookup — async fire-and-poll (v9.9.9)

- **Bug it solved:** `Resolve-WMPTrackByDuration` used `Invoke-WebRequest -TimeoutSec 4`
  synchronously on the first WMP track, causing a 4-second UI thread block.

- **Current expression:** `tray.ps1` — `Resolve-WMPTrackByDuration` uses
  `HttpClient.GetStringAsync` fire-and-poll. Tick 1 fires the request and returns
  `$null`. Subsequent ticks poll `Task.IsCompleted`. State is held in
  `$global:_wmpDeezerPendingKey` / `$global:_wmpDeezerPendingTask`.

- **Non-obvious detail:** The fire-and-poll pattern requires the caller to tolerate
  multiple ticks of `$null` before the result arrives. Any code that treats a `$null`
  Deezer result as "not a music track" rather than "result pending" will suppress
  the WMP track until the fetch completes. The state machine must distinguish between
  "no result yet" (poll pending) and "definitively no match" (fetch returned empty).

- **Migration risk:** Low (native `async/await` in .NET 8 handles this transparently)
- **How to preserve in .NET 8:** Use `await httpClient.GetStringAsync(...)` with
  `CancellationToken`. The pending-state distinction (pending vs. no match) still
  applies if the result is cached; cache the negative result explicitly (not as `null`).

---

### 11. Patch notes owner-draw virtualized panel (v12.0.1)

- **Bug it solved:** `Show-WelcomeDialog` created ~683 WinForms `Label` controls in a
  foreach loop (169 versions × ~1.5 notes × 3 controls + headers). Each `Controls.Add`
  triggered a layout pass. Total render time: 10-13 seconds, blocking the UI thread.

- **Current expression:** `tray.ps1` lines 1895-2009 in `Show-WelcomeDialog`.
  Key pattern:
  1. `PATCH_HISTORY` is pre-flattened into a `$rows` array (text measurement only,
     no controls — ~50-100ms total).
  2. `$notesPanel.AutoScrollMinSize` is set to the total content height.
  3. A `Paint` event handler renders only rows whose `Y/H` intersect `e.ClipRectangle`.
  4. Scroll offset is read from `$s.AutoScrollPosition.Y` and applied to convert
     viewport-relative clip coordinates to content-space row coordinates.
  5. `Panel.DoubleBuffered` is set via reflection (it is `protected` in WinForms).

- **Non-obvious detail:** The `Panel.DoubleBuffered` property is `protected` in
  WinForms and cannot be set directly from outside the class. Setting it via
  `SetStyle(OptimizedDoubleBuffer | AllPaintingInWmPaint | ResizeRedraw, true)`
  through reflection is necessary to prevent scroll-repaint tearing. If this reflection
  call fails silently (wrong binding flags, future .NET version), the panel will
  flicker on every scroll event. The try/catch on line ~1911 silently swallows failures
  — verify the double-buffer is actually active if upgrading WinForms.

  The clip rectangle is in **viewport-relative** coordinates; row Y values are in
  **content-relative** coordinates. The conversion is:
  `clipTopContent = clip.Top + (-AutoScrollPosition.Y)`.
  Getting this wrong causes visible rows to be skipped or invisible rows to be painted.

- **Migration risk:** Low (non-functional regression; just performance)
- **How to preserve in .NET 8:** If migrating to WinForms on .NET 8, `DoubleBuffered`
  may be directly settable via subclassing. Use a `VirtualizingPanel` or the same
  owner-draw clip-rectangle pattern. The key invariant is: never create a WinForms
  control per changelog entry.

---

### 12. Auto-update helper — single-string -ArgumentList with double-backtick escaping

- **Bug it solved (v11.1.6/v11.1.8):** Self-uninstall on machines with spaces in the
  Windows username (e.g., `AER Alex`). The auto-update helper script contained:
  ```powershell
  Start-Process msiexec.exe -ArgumentList @('/i', $msiFile, ...) ...
  ```
  PowerShell joins array elements with spaces but does NOT quote them. `$msiFile`
  containing a space was split by msiexec into two arguments.

- **Current expression:** `tray.ps1` — inside the `@"..."@` here-string that generates
  the helper `.ps1` file (the HARD CONSTRAINTS section documents the exact escaping):
  ```
  Start-Process msiexec.exe -ArgumentList "/i ``"`$msiFile``" /quiet /norestart" -Wait -WindowStyle Hidden
  ```
  In the generated helper file this becomes:
  ```powershell
  Start-Process msiexec.exe -ArgumentList "/i `"$msiFile`" /quiet /norestart" -Wait -WindowStyle Hidden
  ```

- **Non-obvious detail:** Inside a PowerShell `@"..."@` here-string:
  - `` ` `` + `"` → literal `"` in the OUTPUT
  - ` `` ` (two backticks) → literal `` ` `` in the OUTPUT
  - `` `$ `` → literal `$` in the OUTPUT (prevents expansion at here-string
    creation time; the `$` expands when the helper runs)
  
  The combination ```` ``" ```` in the here-string body produces `` `" `` in the
  output file, which PowerShell evaluates as an escaped double-quote inside a
  double-quoted string. This is the ONLY way to produce a quoted path in msiexec's
  argument string from inside a here-string without breaking the outer string
  interpolation. The bug was triggered twice in production (v11.1.6, v11.1.7) before
  being fixed in v11.1.8. This pattern must not be "simplified."

- **Migration risk:** Medium (applies to PS 5.1 code path only; C# installer on .NET 8
  would use `ProcessStartInfo.ArgumentList` which handles quoting automatically)
- **How to preserve in .NET 8:** In the C# rewrite (`tray_services.cs`), the
  `Install-Update` equivalent should use `ProcessStartInfo.ArgumentList` (array form)
  which quotes individual arguments correctly. Never construct the msiexec command
  line as a single string with manual quoting.

---

### 13. SMTC transition guard — suppress ALPC during track-change window (v9.9.9 Build 5)

- **Bug it solved:** `GetPlaybackInfo()`, `GetTimelineProperties()`, and `GetSessions()`
  are synchronous ALPC calls that each take ~15ms during an SMTC session transition
  (the SMTC service is busy updating session state). With 4 such calls per tick during
  a skip, each tick took ~60ms, causing the UI thread to miss multiple WinForms message
  pump cycles — perceptible as a stutter on every skip.

- **Current expression:** `tray.ps1` line ~5993:
  ```powershell
  $global:_smtcTransitionGuardMs = 0
  ```
  Set to `now + 750ms` inside `Get-SMTCMediaPropsCached` when a new title is detected
  (line ~6121). Checked in `Get-SMTCPlaybackInfoCached` (line ~6070) and
  `Get-SMTCSessionsCached` (line ~6037) to return cached values instead of live calls.

- **Non-obvious detail:** The guard window (750ms) was extended from 500ms in v11.2.0
  to match the `Changing` status guard. The timing is empirical — it must be long
  enough that the SMTC service has finished updating its state before the next live
  call is made, but short enough that seek-position accuracy is not significantly
  impaired. In v12.0.0, the watcher path short-circuits before this guard is checked,
  so it is only active on the legacy fallback path.

- **Migration risk:** Low (rendered unnecessary by v12.0.0 event architecture)
- **How to preserve in .NET 8:** The event-driven architecture makes synchronous
  ALPC calls unnecessary in the primary path. If any synchronous SMTC calls remain
  in a fallback path, apply the same staleness-guard pattern.

---

## Summary Risk Table

| Fix | Version | File(s) | Migration Risk | Key constraint |
|-----|---------|---------|----------------|----------------|
| WinRT AsTask 2-arg CancellationToken overload | v9.9.4 | tray.ps1 L5250-5820 | **High** | Must cancel WinRT op on timeout, not just abandon Task |
| SAUMID as cache key (not GetHashCode) | v11.2.1 | tray.ps1, tray_native.cs | **High** | COM RCW identity is unstable across manager acquisitions |
| Async task cleanup in `finally` + 500ms rate limit | v11.2.3 | tray.ps1 L6127-6155 | **High** | Two constraints must coexist: unconditional finally + time rate-limit |
| SMTC event subscription via GetAddMethod().Invoke() | v12.0.0 | tray_native.cs L760-801 | **High** | EventInfo.AddEventHandler throws on WinRT events; must use add_X direct invoke |
| SMTC event subscription lifecycle (no leaks) | v12.0.0 | tray_native.cs L436-501 | **High** | RCW recycling detection; token capture and release at session remove + Dispose |
| Burst window suppression (800ms) | v12.0.0 | tray_native.cs L327-342 | **High** | Burst of SessionsChanged events during rapid skip must suppress all ALPC reads |
| Art cache 200-entry LRU (Queue + Hashtable in sync) | v11.0.0 | tray.ps1 L6202-6208 | **Medium** | Queue and Hashtable must be reset together; never clear one without the other |
| Deferred album art (two-phase webhook) | v9.9.9 | tray.ps1 Invoke-DeferredThumbExtraction | **Medium** | Art arrives in follow-up webhook; consumers must handle two webhooks per track |
| SMTC manager held for app lifetime | v12.0.0 | tray_native.cs L357-395 | **Medium** | Must add 5-min silence fallback to re-init if SMTC service restarts |
| msiexec double-backtick here-string quoting | v11.1.8 | tray.ps1 Install-Update | **Medium** | Only correct form; array form does NOT quote paths |
| Patch notes owner-draw virtualization | v12.0.1 | tray.ps1 L1895-2009 | **Low** | DoubleBuffered via reflection; clip-rect coordinate space conversion |
| GetSessions 500ms staleness guard | v11.2.2 Fix 2 | tray.ps1 L6030-6043 | **Low** | Legacy fallback path; superceded by watcher in primary path |
| SMTC transition guard (500/750ms window) | v9.9.9 B5 | tray.ps1 L5993, 6121 | **Low** | Legacy fallback path only in v12.0.0+ |
| Rate-limiting cache for Get-SMTCNowPlaying | v11.2.2 Fix 1 | tray.ps1 L7221-7236 | **Low** | PS 5.1 limitation; not needed in native C# |
| WMP Deezer async fire-and-poll | v9.9.9 | tray.ps1 Resolve-WMPTrackByDuration | **Low** | Distinguish "pending" from "no match" in null check |
