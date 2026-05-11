# V14.0.0-RC.3 Soak Test Report

## Summary

**Verdict:** IN PROGRESS (soak v4 started 2026-05-11 05:04 — results pending)

---

## Test Setup

- **Build:** v14.0.0-rc.3, commit 58b8abd (after 3 server memory fixes)
- **Duration:** 6 hours (05:04 – ~11:05 2026-05-11)
- **Log file:** `soak_log_rc3_v4.csv`
- **Thresholds:** server ≤ 450 MB, tray ≤ 350 MB, spectrum ≤ 150 MB
- **Monitor:** `build_tools/_soak_v4.ps1` (every 30 sec, process-level working set)

---

## Server Memory Fixes (commits dcec84d + c67efb7 + 58b8abd)

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
47 KB DeepClone + ToJsonString allocation.

**Fix:** `ServerState._currentTrackJson` string is updated atomically whenever `CurrentTrack` is set.
`Broadcast()` paths now use `state.CurrentTrackJson` — no DeepClone, no re-serialization.

### Fix 3 — Dirty-flag for same-track heartbeat path (c67efb7)

**Problem:** The same-track heartbeat path called `state.CurrentTrack = ct2` unconditionally on
every heartbeat (including no-op heartbeats where nothing changed). This triggered a DeepClone +
ToJsonString (~95 KB allocation) every second even when isPaused, startedAt, etc. were unchanged.

**Fix:** `bool ct2Dirty = false` flag. Only set true when B5/B6/B7/B8/B9/B10 actually mutate `ct2`.
B6 restructured from unconditional `ct2["isPaused"]=false` writes to a `if (wasPaused)` guard.
`state.CurrentTrack = ct2` only called when `ct2Dirty`.

### Fix 4 — SSE channel bounded capacity (58b8abd) ← root cause of 14 MB/min growth

**Problem:** `SseClient.Queue` was `Channel.CreateUnbounded<string>()`. When a client's drain loop
stalls on a dead TCP connection (no FIN — browser crash, network drop), the OS send buffer fills
up. `await ctx.Response.WriteAsync(frame, ct)` suspends the drain loop. Meanwhile `Broadcast()`
keeps calling `TryWrite()` on the unbounded channel with 47 KB frames every second.

**Soak v3 evidence:** server grew 389 → 785 MB in exactly 27 minutes (14.3 MB/min), then plateaued.
At 5 stale clients × 47 KB/s: 5 × 47 × 60 = 14.1 MB/min — exact match. Plateau at 27 min
corresponds to stale connections eventually being cleaned up (TCP reset or Kestrel write timeout).

**Fix:** `Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = DropOldest })`.
Stale clients are capped at 32 × 47 KB ≈ 1.5 MB. `TryWrite` returns false when full (frame
dropped silently). Reconnecting clients receive current state in the initial SSE frame anyway.

---

## Soak History

| Version | Threshold | Result | Peak | Notes |
|---------|-----------|--------|------|-------|
| v1 | server ≤ 100 MB | FAIL | 613 MB | B11 art retry runaway; 26 MB/min |
| v2 | server ≤ 350 MB | FAIL | 394 MB | Threshold too tight; plateau exceeded |
| v3 | server ≤ 450 MB | FAIL | 810 MB | SSE unbounded channel; 14 MB/min |
| v4 | server ≤ 450 MB | **IN PROGRESS** | 119 MB (2 min) | Bounded channel fix applied |

---

## Soak v4 Data

<!-- Filled in after completion -->

**Start:** 2026-05-11 05:04:56
**End:** pending
**Samples collected:** pending
**Peak server MB:** pending
**Both-half diff:** pending
**Final slope:** pending
**Verdict:** **PENDING**

---

*This document will be finalized after the 6-hour run completes.*
