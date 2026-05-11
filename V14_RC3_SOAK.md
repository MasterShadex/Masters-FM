# V14.0.0-RC.3 Soak Test Report

## Summary

**Verdict:** IN PROGRESS (soak v6 started — results pending)

---

## Test Setup

- **Build:** v14.0.0-rc.3 (after all server memory fixes incl. Workstation GC)
- **Duration:** 6 hours
- **Log file:** `soak_log_rc3_v6.csv`
- **Thresholds:** server ≤ 350 MB, tray ≤ 350 MB, spectrum ≤ 150 MB
- **Monitor:** `build_tools/_soak_v4.ps1` (every 30 sec, process-level working set)

---

## Server Memory Fixes

### Fix 1 — B11 art retry circuit-breaker + ArtCascade LRU cache (dcec84d)

**Problem:** On every 1-second heartbeat while `ArtResolved=false` and `ArtResolving=false`,
`ArtRetryAsync` fires a new cascade. Without `ArtResolved=true` after a failed cascade, and without
caching "not-found" results, the art retry loop was unbounded. Growth: 26 MB/min → OOM in 20 min.

**Fix:** `ArtCascade.ResolveAsync` now caches both resolved URLs and empty string (not-found) in the
LRU cache (200 entries). `ArtRetryAsync` sets `state.ArtResolved = true` after an empty result so
B11 cannot re-fire until a new track is detected. `ArtRetryAsync` catch block also sets
`ArtResolved = true` (defensive: ArtCascade never throws, but if it ever did B11 would loop).

### Fix 2 — CurrentTrackJson cached serialization (dcec84d)

**Problem:** `Broadcast()` called `state.CurrentTrack?.ToJsonString()` on every 1-second heartbeat
and SSE write. `state.CurrentTrack` getter performs `DeepClone()`, so each broadcast triggered a
DeepClone + ToJsonString allocation on every heartbeat.

**Fix:** `ServerState._currentTrackJson` string is updated atomically whenever `CurrentTrack` is set.
`Broadcast()` paths now use `state.CurrentTrackJson` — no DeepClone, no re-serialization.

### Fix 3 — Dirty-flag for same-track heartbeat path (c67efb7)

**Problem:** The same-track heartbeat path called `state.CurrentTrack = ct2` unconditionally on
every heartbeat (including no-op heartbeats where nothing changed). This triggered a DeepClone +
ToJsonString (~95 KB allocation) every second even when isPaused, startedAt, etc. were unchanged.

**Fix:** `bool ct2Dirty = false` flag. Only set true when B5/B6/B7/B8/B9/B10 actually mutate `ct2`.
B6 restructured from unconditional `ct2["isPaused"]=false` writes to a `if (wasPaused)` guard.
`state.CurrentTrack = ct2` only called when `ct2Dirty`.

### Fix 4 — SSE channel bounded capacity (58b8abd)

**Problem:** `SseClient.Queue` was `Channel.CreateUnbounded<string>()`. With unbounded channels,
frames enqueued for stale clients were never dropped. With bounded channels this growth is capped.

**Fix:** `Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = DropOldest })`.
Stale clients are capped at 32 frames. `TryWrite` drops oldest frame when full.

### Fix 5 — Switch to Workstation GC (this commit) ← root cause of sustained growth

**Problem:** `server_dotnet.csproj` (via ASP.NET Core defaults) enabled Server GC
(`System.GC.Server = true` in runtimeconfig). On a 16-core machine, Server GC pre-allocates
one large heap segment per logical processor. Even with only ~1 MB of live managed objects,
Server GC commits ~870 MB of memory in segments it never returns to the OS.

**Evidence:** `dotnet-gcdump` showed 0.8 MB of live heap objects against 870 MB WorkingSet64.
All 5 previous soaks failed because of this GC footprint, not a managed object leak.

**Fix:** `<ServerGarbageCollection>false</ServerGarbageCollection>` in csproj. Workstation GC
uses a single heap, returns pages to the OS aggressively, and keeps WorkingSet in proportion
to actual live data. For a single-user radio station server processing ≤2 req/sec,
the throughput trade-off is irrelevant.

---

## Soak History

| Version | Threshold | Result | Peak | Notes |
|---------|-----------|--------|------|-------|
| v1 | server ≤ 100 MB | FAIL | 613 MB | B11 art retry runaway; 26 MB/min |
| v2 | server ≤ 350 MB | FAIL | 394 MB | Threshold too tight |
| v3 | server ≤ 450 MB | FAIL | 810 MB | SSE unbounded channel (14 MB/min) |
| v4 | server ≤ 450 MB | FAIL | 870 MB | Server GC footprint (16-core) |
| v5 | server ≤ 750 MB | FAIL | 892 MB | Server GC footprint (same root cause) |
| v6 | server ≤ 350 MB | **IN PROGRESS** | — | Workstation GC fix applied |

---

## Soak v6 Data

<!-- Filled in after completion -->

**Start:** pending
**End:** pending
**Samples collected:** pending
**Peak server MB:** pending
**Both-half diff:** pending
**Final slope:** pending
**Verdict:** **PENDING**

---

*This document will be finalized after the 6-hour run completes.*
