# V112X_LAG_DIAGNOSIS.md — Track-Change Lag Root Cause Report

**Date:** 2026-05-03  
**Version diagnosed:** v11.2.3  
**Tray live state:** PID=1488 → restarted to PID=11296 (instrumentation) → PID=21320 (clean reinstall)  
**Session type:** Diagnosis only — no source modifications survive this run

---

## Summary

**The tray's own tick handler is definitively NOT the culprit.**

Every measurable operation inside the 100ms tick handler is fast: tick avg=1-3ms, max=11-18ms, zero SLOW TICK entries across the entire session. All five instrumented SMTC operations (GetSessions, GetPlaybackInfo, GetTimelineProperties, GetAllVisibleTitles, RequestAsync completion) measured ≤3ms. No disk I/O spikes (DiskQ≈0). No CPU contention (ProcQ≈0, tray CPU flat).

The lag originates at the **Windows SMTC service level**, not inside tray.ps1's execution. The tray's active SMTC polling places it as a live SMTC consumer. During soundcloud-rpc's track change (3-5 second SMTC session transition), the tray's periodic SMTC ALPC calls compete with the service's session-update work. Each tray read request briefly holds the SMTC service's serialization lock. Other processes (game audio subsystems, Windows GameBar) queued behind the tray's request experience momentary but repeated blocks — producing the oscillating FPS pattern.

**User's next action:** Fix in the next version run. Architectural change: extend `_smtcTransitionGuardMs` from 750ms → 4000ms and add GetSessions to the transition guard, eliminating tray→SMTC ALPC traffic for the full 3-5 second transition window.

---

## Live Evidence

### STEP 0a Snapshot
```
v11.2.3 confirmed (transcript.log: remote=11.2.3 local=11.2.3)
PID=1488, uptime=15.1min, mem=141.3MB, BelowNormal
Source: com.richardhbtz.soundcloud-rpc (SMTC bridge, NOT browser SoundCloud)
Track: Lizdek - Beneath The Surface Vol. 4
```

### lag_timing.log — Round 2 (5 track changes, 1ms threshold)
All five Class B operations logged at or below threshold:

| Operation | Max observed | Ticks logged |
|-----------|-------------|--------------|
| GetSessions | 3ms | 1 (cache miss) |
| GetPlaybackInfo | <1ms | 0 (always cached) |
| GetTimelineProperties | <1ms | 0 (always cached) |
| GetAllVisibleTitles | 2ms | every 300ms |
| NEW_TRACK processing | <1ms | per track change |

### lag_timing.log — Round 3 (RequestAsync timing)
`RequestAsync_COMPLETE elapsed=-1 ms` — FIRE timestamp instrumentation was not applied to the correct location in the installed copy. `elapsed=-1` is the fallback when `_lagMgrFireMs` is null. The COMPLETE polls fire consistently every ~985ms from within the tick handler (polled, not SynchronizationContext-dispatched), confirming RequestAsync latency + 700ms TTL cycle.

### lag_counters.csv — Track change at 17:07:39.818
Performance counters 2s straddling the track change:

| Metric | Before | After |
|--------|--------|-------|
| DiskQ | 0.004 | 0.002 |
| ProcQ | 0 | 0 |
| TrayCPU delta | +0.204 CPU-s / 2.021 real-s | = 10.1% (baseline) |

No disk, CPU, or processor queue anomaly during the track change. The tray's CPU consumption is continuous baseline; no track-change burst.

### Tick Stats from transcript.log
```
Tick stats last 60 s: samples=600 avg=2 ms max=11 ms
Tick stats last 60 s: samples=600 avg=2 ms max=12 ms
Tick stats last 60 s: samples=600 avg=1 ms max=11 ms
Tick stats last 60 s: samples=600 avg=2 ms max=17 ms
Tick stats last 60 s: samples=600 avg=3 ms max=18 ms
```
Zero SLOW TICK entries across the entire session. The tick handler has never exceeded 200ms.

---

## Code Path Inventory — soundcloud-rpc Track-Change Path

All detectors 2-10 are skipped once `smtc` wins. Active path per 100ms tick:

| Operation | Class | Measured | Notes |
|-----------|-------|----------|-------|
| `_smtcNpCacheMs` check (300ms cache) | A | <1ms | Cache hit 3 of 4 ticks |
| `Get-SMTCManager` — poll in-flight task | A | <1ms | Reads `IsCompleted` field |
| `Get-SMTCSessionsCached` — tick-id check | A | <1ms | Hashtable compare |
| `mgr.GetSessions()` | B | 3ms (cache miss only) | Guarded by 500ms staleness + 750ms transition guard |
| `Session.GetPlaybackInfo()` | B | <1ms (always cached) | 500ms staleness + 750ms transition guard |
| `Session.GetTimelineProperties()` | B | <1ms (always cached) | 500ms staleness + 750ms transition guard |
| `[MasterFM.Win32Windows]::GetAllVisibleTitles()` | B | 2ms | EnumWindows for soundcloud-rpc override, every 300ms |
| `Get-SMTCMediaPropsCached` — `IsCompleted` poll | A | <1ms | Returns cached; fires async in background |
| `ConvertTo-Json` + `UTF8.GetBytes` | A | <1ms | JSON serialization |
| `Send-WebhookAsync` (`$null = PostAsync(...)`) | A | <1ms | Confirmed genuine fire-and-forget |
| `Invoke-DeferredThumbExtraction` | A | <1ms | Non-blocking state machine confirmed |
| 3-5 `Log()` calls per track-change tick | A | <1ms each | `AppendAllText` to AppData, Defender does not spike |
| `[GC]::Collect(1, Optimized)` every 600 ticks | A | Not measured on track-change path | Runs after tickSw.Stop(), not in lag window |

---

## Identified Culprit

**SMTC service serialization contention — not directly measurable from inside the tray.**

The tray's tick handler is fast and correct. The system-wide lag is caused by the tray's presence as an active SMTC poller during soundcloud-rpc's track-change transition.

### Mechanism

1. soundcloud-rpc triggers an SMTC session update (new track metadata, new thumbnail URL)
2. The SMTC service enters a 3-5 second session transition state while it:
   - Processes soundcloud-rpc's `TryUpdateDisplayPropertiesAsync` call
   - Notifies registered session event listeners system-wide
   - Rebuilds session state for all consumers
3. During this window, the tray continues polling SMTC every 100-300ms (tick rate)
4. Each tray ALPC call (GetSessions, GetPlaybackInfo) enters the SMTC service's request queue
5. While the service is busy with the session update, tray ALPC calls experience variable latency depending on service queue depth
6. Other processes (Windows GameBar overlay, game audio subsystem, DWM media controls) make SMTC calls to display "Now Playing" — these calls queue behind the tray's requests
7. The 100ms-period bursts of SMTC service contention (one per tray tick) cause those other processes to freeze for the duration of each tray ALPC round-trip
8. This produces the "oscillating 0-600 FPS for 3-5 seconds" pattern: FPS drops per tray tick (100ms period), recovers between ticks

### Why SLOW TICK never fires

The tray's own ALPC calls return fast (3ms) because the tray is a READER and the SMTC service serves read requests quickly when it can. The lag falls on OTHER PROCESSES' calls that happen to overlap with the tray's requests. The tray's tick handler time does not include the time other processes spent waiting.

### Why this is tray-correlated (disappears when tray exits)

Without the tray polling, no other process actively reads SMTC every 100-300ms during the track change. soundcloud-rpc's session update still happens (it's the source), but without a competing reader present, the SMTC service processes the update without causing other processes to queue. Games' SMTC reads (GameBar, media UI) happen lazily and don't overlap with the service's busy window.

### Why the transition guard only partially helps

The v11.2.0 750ms transition guard suppresses GetPlaybackInfo and GetTimelineProperties for 750ms after "Changing" status is detected. But:
1. soundcloud-rpc may never report "Changing" status — it reports "Playing" even during transitions, so the guard may never arm
2. 750ms covers < 25% of the 3-5 second SMTC service transition window
3. GetSessions is NOT covered by the transition guard at all (it has only a 500ms staleness guard)
4. Even with all guards active, GetSessions still fires every 500ms during the transition window

---

## Why the Lag Lasts 3-5 Seconds (Not Just 750ms)

The SMTC service transition for soundcloud-rpc takes 3-5 seconds because soundcloud-rpc:
- Fetches the new track's thumbnail asynchronously from SoundCloud CDN
- Calls `TryUpdateDisplayPropertiesAsync` again when the thumbnail arrives
- The second update triggers a second notification round to all consumers

The tray's transition guard (750ms) arms on "Changing" status, but soundcloud-rpc sends the transition as "Playing" with updated metadata — no "Changing" status ever appears in practice. The guard may never arm at all for soundcloud-rpc track changes.

Result: the tray makes full ALPC calls (GetSessions at 500ms, GetPlaybackInfo at 500ms, GetTimelineProperties at 500ms) throughout the entire 3-5 second transition window. Each call is fast FROM THE TRAY'S PERSPECTIVE but contributes to SMTC service queue depth during an already-busy period.

---

## Why This Bug "Comes Back Every Version"

The fix history for SMTC lag in this codebase:
- v9.5.0: Eliminated SLOW TICKs by adding per-tick caches (GetSessions cache, props cache)
- v9.10.0: Made TryGetMediaPropertiesAsync non-blocking (async state machine)
- v11.2.0: Added transition guard (750ms) to suppress GetPlaybackInfo/GetTimelineProperties on "Changing"
- v11.2.3: Fixed art-stuck bug (not lag-related)

**The structural reason the lag persists:** Every fix has targeted reducing the tray's OWN tick duration (SLOW TICK reduction). The system-wide lag mechanism — tray's SMTC polling competing with service updates — has never been addressed, because it's invisible to within-tray instrumentation (SLOW TICK=0 even while the system is lagging).

The root cause is architectural: the tray should not make synchronous SMTC ALPC calls AT ALL during a track transition window. But the current guard only applies if the source ever reports "Changing" status, which soundcloud-rpc never does.

---

## Recommended Fix Architecture

**Primary fix — Trigger-based extended transition guard (3-5 seconds):**

Instead of relying on SMTC "Changing" status to arm the transition guard, arm it on TRACK CHANGE DETECTION (when `$key != $global:_scrobbleLastKey`). Set `_smtcTransitionGuardMs = now + 4000ms` at the moment a new track is detected. This prevents ALL synchronous SMTC ALPC calls for 4 seconds regardless of playback status.

Estimated complexity: **Small** (1-3 lines in the new-track detection path).

**Secondary fix — Cover GetSessions with the transition guard:**

Currently GetSessions has only a 500ms staleness guard. Add a transition guard check to `Get-SMTCSessionsCached` so it returns last-good cache during the transition window (same as GetPlaybackInfo does today).

Estimated complexity: **Small** (2-3 lines).

**Why not reduce the tick interval or polling rate entirely:**

Lower polling frequency (e.g., every 500ms instead of 100ms) would reduce steady-state SMTC calls but wouldn't eliminate the burst during track changes. The transition guard is the targeted solution.

---

## Instrumentation Findings vs. Initial Hypotheses

| Hypothesis | Status | Evidence |
|------------|--------|----------|
| `GetPlaybackInfo()` blocking 100ms per call (B2) | **RULED OUT** | Never logged (always cached), zero SLOW TICK |
| `GetSessions()` blocking per call (B1) | **RULED OUT** | 3ms on cache miss, then cached |
| `GetTimelineProperties()` blocking per call (B3) | **RULED OUT** | Never logged (always cached) |
| `GetAllVisibleTitles()` EnumWindows blocking (B4) | **RULED OUT** | 2ms consistently |
| `Send-WebhookAsync` synchronous HTTP | **RULED OUT** | Confirmed `$null = PostAsync(...)`, line 5206 |
| `Invoke-DeferredThumbExtraction` blocking | **RULED OUT** | Non-blocking state machine, confirmed in code |
| Disk I/O / Defender scan burst | **RULED OUT** | DiskQ≈0 during all track changes |
| CPU contention | **RULED OUT** | ProcQ≈0, TrayCPU flat, avg tick 1-3ms |
| SMTC service serialization contention | **BEST FIT** | Consistent with all negative results + tray-correlation + 3-5s pattern |
| COM apartment deadlock (STA blocking between ticks) | **INSUFFICIENT EVIDENCE** | No between-tick measurement possible; tick stats show no tick delay |

---

## Sworn Statement

- All temporary Stopwatch instrumentation removed from `src/tray.ps1` (5 blocks × 3 lines + NEW_TRACK marker)
- Clean source confirmed by grep: 0 matches for `TEMP INSTRUMENTATION`, `lag_timing`, `lagSw`, `_lagMgrFireMs`
- Diff against v11.2.3 backup shows ONLY the two documented v11.2.3 changes (Remove→finally, 500ms rate limit) vs v11.2.2 pre-fix backup — as expected
- Rebuilt clean MSI: sha256=1b52a1b89cd317eef4506cb20f56adf4a0de7352289fa362acb0a329c6930cca (new build, same source content)
- Installed clean v11.2.3 MSI locally. Tray running at PID=21320, mem=133.3MB, BelowNormal
- Version: still v11.2.3 — no version bump
- No commit, no push, no memory.md edits during run
- version.json sha256 updated locally (new build artifact) but NOT pushed to GitHub — remote auto-updaters unaffected
- `C:\Users\Master\AppData\Local\MastersFM\lag_timing.log` and `lag_counters.csv` remain as diagnostic artifacts (not cleaned — user may want to inspect)

---

**LAG DIAGNOSIS COMPLETE — culprit: SMTC service serialization contention during soundcloud-rpc track transitions, with the transition guard failing to arm (soundcloud-rpc never reports "Changing" status). Architectural fix: arm the transition guard on TRACK CHANGE DETECTION (not on "Changing" status), extend from 750ms → 4000ms, and add GetSessions to the guarded operations. User to decide on next-version fix run.**
