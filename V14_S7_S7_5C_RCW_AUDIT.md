# V14_S7_S7_5C_RCW_AUDIT.md

Stage 7.5C Workstream 1.STEP 2 -- RCW lifecycle audit of
`src/tray_native/tray_native.cs` SMTCWatcher (lines 225-833) under
the brief's instruction "READ then DECIDE."

Triggered by 7.5B's observed ~216 MB/h growth and 7.5C's reproducing
trajectory (~3-4 MB/min through the first 9 minutes of the
60-90 min soak). Audit aims to determine whether the leak is a
clear obvious bug fixable in <30 lines (in which case it lands here)
or an architectural / framework-level issue (in which case it goes
to Brief 2 for deeper investigation).

---

## 1. WinRT call sites in SMTCWatcher (full inventory)

| Line | Site | Call | Returns RCW? | Stored where? |
|---:|---|---|---|---|
| 374-377 | `Initialize` | `AttachEvent` for `SessionsChanged` + `CurrentSessionChanged` (manager-level) | EventRegistrationToken (struct, value type) | `_mgrSessionsChangedBinding`, `_mgrCurrentSessionChangedBinding` |
| 384 | `Initialize` | `mgrT.GetMethod("GetCurrentSession").Invoke(manager, null)` | Yes (a `GlobalSystemMediaTransportControlsSession` RCW) | Local `cur` (transient; `GetSaumidSafe` extracts string then `cur` is GC-eligible) |
| 400 | `EnumerateAndSubscribeSessions` | `mgrT.GetMethod("GetSessions").Invoke(_mgr, null)` | Yes (an `IReadOnlyList<...Session>` RCW + N session RCWs) | Local `sessionsList` (transient; iterated once then GC-eligible) |
| 440-445 | `SubscribeToSession` | `AttachEvent` for `MediaPropertiesChanged` + `PlaybackInfoChanged` + `TimelinePropertiesChanged` (per-session) | EventRegistrationToken structs | `subs.MediaHandler`, `subs.PlaybackHandler`, `subs.TimelineHandler` |
| 449-451 | `SubscribeToSession` | `_snaps.GetOrAdd` then `snap.SessionRef = session` | Stores the session RCW | `_snaps[saumid].SessionRef` |
| 468 | `SubscribeToSession` (deferred init Task.Run body) | `CapturePlaybackInfo(session, snap)` | Stores `snap.PlaybackInfoRcw` | `_snaps[saumid].PlaybackInfoRcw` |
| 469 | same | `CaptureTimeline(session, snap)` | Stores `snap.TimelinePropertiesRcw` | `_snaps[saumid].TimelinePropertiesRcw` |
| 471 | same | `FetchMediaPropsSync(session, 1500)` | Yes (an `MediaProperties` RCW) | Local `props`; passed to `ApplyMediaProperties` -> stored in `snap.MediaPropertiesRcw` |
| 553-556 | `OnCurrentSessionChanged` | `_mgr.GetType().GetMethod("GetCurrentSession").Invoke(_mgr, null)` | Yes (transient session RCW) | Local `cur`; SAUMID extracted; `cur` GC-eligible |
| 582 | `OnMediaPropertiesChanged` | `FetchMediaPropsSync(sender, 1500)` | Yes (MediaProperties RCW) | Stored in `snap.MediaPropertiesRcw` (overwrites prior) |
| 598 | `OnPlaybackInfoChanged` | `CapturePlaybackInfo(sender, snap)` -> `session.GetType().GetMethod("GetPlaybackInfo").Invoke(...)` | Yes (PlaybackInfo RCW) | Stored in `snap.PlaybackInfoRcw` (overwrites prior) |
| 613 | `OnTimelinePropertiesChanged` | `CaptureTimeline(sender, snap)` -> `session.GetType().GetMethod("GetTimelineProperties").Invoke(...)` | Yes (TimelineProperties RCW) | Stored in `snap.TimelinePropertiesRcw` (overwrites prior) |
| 685 | `FetchMediaPropsSync` | `m.Invoke(session, null)` (`TryGetMediaPropertiesAsync`) | Yes (IAsyncOperation RCW) | Local `asyncOp`; polled to completion; `GetResults` extracts MediaProperties; asyncOp GC-eligible after function returns |
| 696 | `FetchMediaPropsSync` | `resultsM.Invoke(asyncOp, null)` (`GetResults`) | Yes (MediaProperties RCW) | Returned to caller |
| 700 | `FetchMediaPropsSync` | `cancelM.Invoke(asyncOp, null)` | None (void) | -- |
| 720-727 | `GetSessions` (public) | iterates `_snaps`; reads `kv.Value.SessionRef` | Returns existing RCWs (no new ones) | -- |

**Note: there is NO `GetThumbnailAsync` call site anywhere in
SMTCWatcher.** Brief STEP 2.1 asks about "GetThumbnailAsync results
released (potentially leaking RandomAccessStream handles)." Answer:
not exercised here; thumbnail extraction is a 7.5-deferred item
(per-app metadata enrichment) and the C# bridge doesn't pull
thumbnails. Zero leak surface from that vector.

---

## 2. Cross-reference with architectural lessons

### 2.1 B-002 / B-016 (state mutation in `finally` only)

**Lesson**: state mutations on async-task completion MUST go in
`finally`; conditional branches in completion handlers create
circular deadlocks (PS S15's `_smtcPropsFiredThisTick` saga).

**Audit findings:**

- The C# port has NO `_smtcPropsFiredThisTick`-equivalent global
  hashtable. `RateLimitSkip` (line 563) uses `Interlocked.Exchange`
  (atomic; no conditional branch dependency). The whole "fire once
  per session per tick" / "Remove on title change" / "deadlock from
  conditional Remove" failure mode is structurally absent.

- Mutations on the coalesced enumerate task body (line 535-537) put
  `Interlocked.Exchange(ref _enumerateInFlight, 0)` in `finally`.
  **B-002 lesson followed.**

- `OnCurrentSessionChanged` at line 556 writes `_currentSaumid =
  (cur == null) ? null : GetSaumidSafe(cur);` outside any try
  finally. This is a single string assignment to a member field --
  on x64 this is atomic and not lifecycle-critical. Acceptable
  deviation; not a B-002-pattern bug.

**Verdict: B-002/B-016 architectural lessons honoured.** No
conditional Remove pattern; no deadlocked-Remove bug; no resurrection
risk.

### 2.2 B-004 (no COM proxy hash keys)

**Lesson**: never key caches by COM-proxy `GetHashCode()`; use
intrinsic identifiers like SAUMID.

**Audit findings:**

- `_snaps` keyed by `Saumid` (string). Line 300-301:
  `new ConcurrentDictionary<string, SMTCSessionSnapshot>(StringComparer.OrdinalIgnoreCase)`.
- `_subs` keyed by `Saumid` (string). Line 304-305: same shape.
- `_events` queue is FIFO; no key.
- `EnumerateAndSubscribeSessions` builds a HashSet<string> of
  current SAUMIDs (line 405) and uses it for set-difference detection.
- The session-identity check at line 418 uses
  `ReferenceEquals(existing.Session, s)` -- this is the only place
  that depends on COM proxy identity, and it's used to DETECT
  changes (which is correct), not as a CACHE KEY.

**Verdict: B-004 architectural lesson followed.** Zero use of COM
proxy hash codes as cache keys.

### 2.3 B-008 (RCW finalizer ratchet from `GetSessions()`)

**Lesson**: every WinRT call site that allocates RCWs needs a
staleness guard; PS v11.x's `GetSessions()` was being called 600/min
(pure ratchet). v11.2.2 added 500ms staleness guard. v12.0.0
architecturally replaced polling with events.

**Audit findings:**

- `GetSessions()` is called in TWO places:
  1. `Initialize()` at line 400 (called once at startup).
  2. `EnumerateAndSubscribeSessions` from `OnSessionsChanged` (line 531).
- `OnSessionsChanged` is gated by 750ms coalescing (line 514: deadline)
  AND by `Interlocked.CompareExchange(ref _enumerateInFlight, 1, 0)`
  serialization (line 516).
- During a track-skip burst, multiple SessionsChanged events fire in
  rapid succession; coalescing collapses them into ONE
  `EnumerateAndSubscribeSessions` call after the deadline stops moving.
- Burst window of 800ms suppresses ALPC reads from per-session events
  during the burst.

So `GetSessions()` is bounded to ~1 call per coalesced burst, max
~80/min during sustained rapid skipping. v11.x's 600/min ratchet is
absent.

The transient session-list RCW (line 400) is a local; iteration
finishes; locals go out of scope; GC-eligible.

**Verdict: B-008 architectural lesson honoured.** GetSessions call
rate is bounded to ~1 per coalesced burst.

### 2.4 B-013 (cache wrapper with stale art)

**Irrelevant** -- this is a C# resolver layer concern (TrackResolver),
not a native SMTCWatcher concern. SMTCWatcher does not touch art
URIs at all.

### 2.5 _smtcMgrCacheTTL = 24h sentinel

**Brief STEP 2.1 asks: verify the 24h sentinel is still there.**

Audit finding: SMTCWatcher.cs has NO TTL on `_mgr`. The manager is
acquired once in `Initialize()` (line 370) and held for the life of
the watcher instance. Per memory.md v12.0.0 entry, the C# port
strengthened the PS tray's 24h sentinel to "manager held for app
lifetime" -- a stronger guarantee. NO re-acquisition path exists.

**Verdict: stronger than the 24h sentinel.** No B-015 (manager
re-acquisition every ~686ms) risk in the C# port.

### 2.6 _smtcSessionsCache lifecycle

**Brief STEP 2.1 asks: verify the v11.2.2 staleness guard.**

Audit finding: SMTCWatcher.cs has NO `_smtcSessionsCache` equivalent.
The PS-tray pattern was: cache `GetSessions()` result for 500ms to
avoid the per-tick ratchet. The C# port replaced that entire pattern
with the event-driven `_snaps` dictionary, which is updated by
SessionsChanged events directly. There is no `Get-SMTCSessionsCached`
function-equivalent.

**Verdict: pattern obsoleted by architecture.** No staleness-cache
to verify; the architectural replacement (per-SAUMID event-driven
snapshots) makes the cache unnecessary.

---

## 3. Snapshot RCW retention analysis

`SMTCSessionSnapshot` (lines 234-262) holds 4 RCW slots:

1. `MediaPropertiesRcw` -- set by `ApplyMediaProperties` (line 673)
2. `PlaybackInfoRcw` -- set by `CapturePlaybackInfo` (line 622)
3. `TimelinePropertiesRcw` -- set by `CaptureTimeline` (line 644)
4. `SessionRef` -- set by `SubscribeToSession` (line 450) and
   `_snaps.GetOrAdd` event-handler factories (lines 577, 593, 608)

Lifecycle:
- Each new event in its category OVERWRITES the prior RCW with a
  fresh one
- The OLD RCW becomes unreachable; GC eventually finalizes it; the
  finalizer thread releases the underlying COM proxy via IUnknown::Release

Maximum live RCWs:
- 4 RCW slots x max ~3-4 SAUMIDs concurrently observable = ~12-16
  live RCWs at any moment

This is **NOT the v11.x ratchet pattern**. The v11.x 600/min
GetSessions had each call producing a FRESH `IReadOnlyList<...Session>`
RCW PLUS N fresh session RCWs, with NONE released, accumulating
without bound between Gen2 GCs (5-min flush interval). The C# port
replaces with bounded RCW retention (one slot per metric per session).

**Open observation**: the RCW slots `MediaPropertiesRcw`,
`PlaybackInfoRcw`, `TimelinePropertiesRcw` appear NOT to be read
externally by `SmtcEventBridge` or any consumer. The values extracted
into snap.Title/Artist/etc. (and snap.PlaybackStatusValue, snap.PositionMs,
snap.DurationMs) are what gets read. The RCW slots are ornamental
on the snapshot. They are HELD UNTIL OVERWRITTEN, which delays GC
finalization by one event cycle.

This is NOT a leak (count is bounded). It DOES delay the GC of OLD
RCWs by ~ReadCoolMs (250ms) up to a few seconds during quiescent
periods. Likely irrelevant to the 216 MB/h growth.

---

## 4. AttachEvent dynamic-method allocation

`AttachEvent` (lines 760-802) allocates a new compiled
`Expression.Lambda` per call:

```csharp
var lambda = Expression.Lambda(delegateType, safe, p1, p2);
Delegate del = lambda.Compile();   // <-- DynamicMethod allocation
```

`DynamicMethod` instances are AppDomain-lifetime: they live until
process exit, even if the delegate becomes garbage. Per call:
- ~1-5 KB of IL bytecode
- ~1-5 KB of JIT'd native after first invocation

**Call sites:**
- `Initialize`: 2 calls (manager-level events; once per process)
- `SubscribeToSession`: 3 calls per session subscription

**Frequency analysis:**
- Initial setup: 5 lambdas allocated
- Each `EnumerateAndSubscribeSessions` triggered by SessionsChanged
  may call `SubscribeToSession` for each new/replaced session.
  soundcloud-rpc replaces its session per track change so each track
  change = 3 fresh lambdas.
- Observed 7.5B rate: 6 track changes / 13 min = ~28/hour.
- Lambda accumulation rate: 28 x 3 = ~84 lambdas/hour.
- Per-lambda cost ~5-10 KB.
- Steady accumulation: ~0.4-0.8 MB/hour.

**Conclusion**: dynamic-method allocation is REAL but ~0.4-0.8 MB/hour,
not 216 MB/hour. Two orders of magnitude too small to explain the
observed growth.

If desired, the lambdas could be cached in a static field keyed by
`(sourceType, eventName, handler-method)`. That would be a 5-10 line
fix. But it is NOT the dominant leak source.

---

## 5. Hypotheses for the 216 MB/h growth (NOT in SMTCWatcher.cs)

After the audit above, no SMTCWatcher.cs lifecycle issue
plausibly explains 216 MB/h. The likely loci are outside this file:

### 5.1 .NET 8 finalizer queue saturation

If finalizers are scheduled faster than the finalizer thread runs
them, work items pile up. The native COM Release() invocation can
involve cross-process ALPC into the SMTC service. Under heavy track-
change load, the finalizer thread might lag.

Test: run a `GC.WaitForPendingFinalizers()` periodically (e.g. once
per minute via the diagnostic heartbeat) and re-measure. If WS drops
sharply after the call, the finalizer queue WAS the culprit.

This would be a build-time instrumentation change (out of locked-list).

### 5.2 WPF Dispatcher / event handler chain

NowPlayingViewModel subscribes to ITrackResolver.TrackChanged event.
TrackResolver subscribes to `_resolver.OnTrackChanged` from
SmtcEventBridge.ProcessEvent. PropertyChanged notifications fire on
the UI thread.

If a binding chain accumulates state (e.g. WPF caching old bound
values in a CollectionView or ListView), this could leak. The
skeleton MainWindow is hidden though, and the visible UI surface is
just the tray icon's tooltip plus a tray menu populated lazily on
right-click. Likely small, but possible.

### 5.3 Microsoft.Windows.SDK.NET.dll JIT warm-up

The CSWinRT projection assembly is 23.7 MB. Each WinRT type access
JIT-compiles its projection-side stubs. .NET 8 tiered JIT means each
hot method is recompiled at higher tiers as call counters cross
thresholds. JIT memory allocation is permanent (unloadable AssemblyLoadContexts
notwithstanding).

For a soak that exercises tens of unique WinRT entry points, JIT
memory could grow ~20-50 MB during initial warm-up. After ~15-30
minutes of sustained operation, JIT should plateau.

If 7.5B's 13-min soak ended before plateau, the 216 MB/h could be
JIT warm-up still in progress. **Test**: run a 60-90 min soak (this
brief's STEP 1) and check whether growth rate decelerates after
30-45 minutes.

### 5.4 WinRT TypedEventHandler<,> projection allocations

CSWinRT-projected events convert WinRT IInspectable arguments to .NET
event-handler signatures. Each event firing potentially allocates an
`object` boxed wrapper for the args. For 30-118 events per track
change, that's ~30-118 small allocations per change. Likely <1 KB
per change. Not a leak.

### 5.5 Logger ring buffer + log file growth

Logger writes EACH log line via `File.AppendAllText` (per Logger.cs
line ~135). Each call opens a FileStream, writes, closes. FileStream
allocation is ~few KB; transient.

The ring buffer at line 29 of Logger.cs is `Queue<string>` capped
at RingCap (likely 20 per heartbeat output). Bounded.

The log FILE grows on disk per line, but not the process heap.

Not a leak.

---

## 6. Decision: NO RCW fix lands in this brief

**Per brief STEP 2 / OPEN QUESTION 2** ("If RCW audit finds multiple
potential bugs: fix the simplest one and document the rest.
Default: don't try to fix multiple bugs in one brief"):

**Audit finds NO obvious bug in SMTCWatcher.cs** that could explain
the 216 MB/h growth. The architectural lessons (B-002, B-004, B-008,
B-016) are honoured. RCW retention is bounded. Dynamic-method
accumulation is real but two orders of magnitude too small.

The most likely loci of the 216 MB/h growth are OUTSIDE
SMTCWatcher.cs:
- .NET 8 finalizer queue dynamics
- WPF binding chain (less likely; minimal UI surface)
- CSWinRT projection JIT warm-up (possible but should plateau)

None of these are fixable in <30 lines of `tray_native.cs`.

**Decision: NO `tray_native.cs` modification in this brief.**
sha256 of `tray_native.cs` MUST stay at the 7.5C baseline
(`6B9804A1AB70000652A2754E886BE3F05167F40EC136EB2CC6CDD62D8EFA9148`).

---

## 7. Recommendations for Brief 2

If the 60-90 min soak (STEP 1) confirms LEAK:

1. **Add `GC.GetTotalMemory(false)` to DiagnosticHeartbeat output**
   (DiagnosticHeartbeat.cs is OUT of 7.5C locked-list; needs a
   dedicated brief or expansion). This separates managed-heap growth
   from total-WS growth (which includes unmanaged native/JIT).

2. **Add `GC.WaitForPendingFinalizers()` test**: temporarily call
   it once per minute in DiagnosticHeartbeat; observe whether WS
   drops. If yes -> finalizer queue saturation; investigation
   target. If no -> growth is in non-finalizable allocations
   (managed heap).

3. **Add `dotnet-counters` external instrumentation**: run alongside
   the tray for 60 min capturing heap snapshots. Identifies which
   types are growing.

4. **Soak under idle** (no active SoundCloud) -- already in
   Workstream 3 plan. If idle plateau is FLAT and active-listening
   shows growth, the leak is correlated with WinRT event volume. If
   idle ALSO grows, the leak is process-lifetime regardless of
   event traffic.

5. **Compare against PS tray under same conditions**: does PS tray
   exhibit similar growth on the same hardware/playlist? If yes,
   it's a SoundCloud-RPC bridge interaction issue; if no, it's
   C#-tray-specific.

6. **Try without WPF-UI**: Workstream 2's framework-recommendation
   document evaluates a switch. If WPF-UI is implicated (e.g., its
   theme system holds resources tied to track-change notifications),
   removing it would be a clean test.

---

## 8. Audit conclusion

`src/tray_native/tray_native.cs` SMTCWatcher follows the v12.0.0
design intent and the four documented architectural lessons (B-002,
B-004, B-008, B-016). Bounded RCW retention. Bounded GetSessions
calls via burst-coalescing. Manager-held-forever in lieu of 24h TTL.
No conditional-Remove deadlock pattern.

**No clear bug fixable in <30 lines.** The 216 MB/h growth is
elsewhere. Brief 2 should expand the locked-list to investigate
DiagnosticHeartbeat instrumentation, finalizer dynamics, and
framework-level allocation patterns.

---

## 9. sha256 confirmation

Per protocol, `tray_native.cs` sha256 was captured at STEP 0:
`6B9804A1AB70000652A2754E886BE3F05167F40EC136EB2CC6CDD62D8EFA9148`.

This audit makes ZERO modifications to the file. STEP 10 verification
will confirm the sha256 still matches.
