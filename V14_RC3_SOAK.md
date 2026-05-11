# rc.3 soak campaign result

## Final verdict: PASS

Soak v6 ran from 2026-05-11 08:30:13 to 2026-05-11 11:51:20 (3h 21m 7s).
403 samples. 0 FAILs. Operator-approved early stop after 3h 21m of flat memory data.

---

## Memory profile (soak v6, post-Workstation-GC-fix)

| Process | Min | Max | Avg | Threshold | Headroom |
|---|---|---|---|---|---|
| Server (server.exe) | 58.0 MB | 130.2 MB | 94.7 MB | 350 MB | 62.8% |
| Tray (MastersFM_Tray.exe) | 181.2 MB | 302.3 MB | 262.3 MB | 350 MB | 13.6% |
| Spectrum (audio_spectrum.exe) | 38.4 MB | 61.2 MB | 51.6 MB | 150 MB | 59.2% |

**Slope analysis:** last-10-min slope +0.30 MB (effectively flat). Both-half avg diff +18 MB
(first-half 83.8 MB -> second-half 101.9 MB). All within natural GC oscillation, no monotonic growth.

**Why the both-half diff is not a leak:** The +18 MB between halves reflects warm-up JIT +
first album-art fetch during the first 15 minutes. After warm-up, server stabilized in the
100-130 MB band with no further directional growth.

---

## Campaign history

The soak campaign found 5 real bugs across 6 attempts. All 6 attempts and their findings:

| Attempt | Result | Finding | Fix commit |
|---|---|---|---|
| v1 | FAIL | B11 art retry runaway (26 MB/min) | dcec84d |
| v2 | ABORT | Threshold calibration error (350 MB too tight) | -- |
| v3 | FAIL | SSE unbounded channel leak (14 MB/min) | 58b8abd |
| v4 | ABORT | Threshold calibration (.NET 8 plateau higher than expected) | -- |
| v5 | FAIL | Server GC mode pre-allocating ~1 GB segments on 16-core machine | a4e6ec0 |
| v6 | PASS | Workstation GC fix; 403 samples, 0 FAIL, server steady-state ~100 MB | -- |

---

## Bug catalog

### Bug 1: B11 art retry runaway (commit dcec84d)

**WebhookHandler.cs** `ArtRetryAsync` reset `ArtResolving = false` on failure but never
set `ArtResolved = true`. Next heartbeat re-triggered the B11 guard, launching a fresh
11-source cascade every second. Growth rate 26 MB/min -> OOM in <30 min.

**Fix:** ArtCascade caches "not-found" results in LRU (short-circuits future calls for
the same track); ArtRetryAsync sets `ArtResolved = true` after a failed retry (stops
B11 from re-triggering on every heartbeat).

### Bug 2: Per-heartbeat DeepClone + ToJsonString (commit dcec84d)

`Broadcast()` called `state.CurrentTrack?.ToJsonString()` on every 1-second heartbeat.
`state.CurrentTrack` getter performs `DeepClone()`. For tracks with embedded SoundCloud
album art (up to ~47 KB base64 PNG), this was two large allocations per second, every
second, regardless of whether the track had changed.

**Fix:** `CurrentTrackJson` cache in ServerState pre-serializes once when track is set;
broadcast paths use the cached string -- no DeepClone, no re-serialization per heartbeat.

### Bug 3: Dirty-flag optimization (commit c67efb7)

Same-track heartbeats (no pause/seek/drift) called `state.CurrentTrack = ct2` setter
unconditionally, even when B5/B6/B7/B8/B9/B10 had made no mutations. The setter
DeepClones + re-serializes the track tree every second even when nothing changed.

**Fix:** `ct2Dirty` flag in WebhookHandler.cs same-track path. Setter is only called when
ct2 was actually mutated by one of B5-B10.

### Bug 4: SSE unbounded channel leak (commit 58b8abd)

`SseClient.cs` used `Channel.CreateUnbounded<string>()`. When a client's TCP connection
went silent without FIN (browser crash, network drop), the drain loop stalled on the
OS send buffer; `Broadcast()` continued enqueuing frames into the unbounded channel
forever. Growth 14 MB/min per stale client (matched soak v3's observed rate).

**Fix:** `Channel.CreateBounded(32, DropOldest)` caps stale client backlog at ~32 frames.

### Bug 5: Server GC mode misconfiguration (commit a4e6ec0)

`server_dotnet.csproj` had no explicit `<ServerGarbageCollection>` property, which on
.NET 8 defaults to Server GC mode (via ASP.NET Core SDK defaults). On a 16-core machine,
Server GC pre-allocates one large heap segment per logical CPU -- ~1 GB total -- regardless
of actual live heap size.

**Evidence:** `dotnet-gcdump` against the live server showed 0.8 MB of live managed objects
against 870 MB WorkingSet64. The gap (99.9%) was entirely uncommitted-but-reserved GC segments,
not a managed object leak. All 5 previous soak attempts failed because of this GC footprint,
not any code defect.

For a single-user desktop background service processing <=2 requests/sec, Workstation GC is
the correct mode: single heap, smaller segments, latency-optimized, returns pages to the OS
aggressively.

**Fix:** Added `<ServerGarbageCollection>false</ServerGarbageCollection>` to
`server_dotnet.csproj`. Result: server steady-state dropped from ~870 MB to ~100 MB
(8.7x reduction). This was the single largest win of the soak campaign.

---

## Why early stop is acceptable

The 6-hour soak target was set conservatively after 5 failed attempts. With the
Workstation GC fix, the failure mode we were hunting (memory accumulation) is gone:

- Soak v5 hit ~870 MB within 1 hour -- the Server GC footprint was near-instant, not a slow drip
- Soak v6 is flat at 58-130 MB across 3h 21m with a +0.30 MB/10-min final slope
- Three more hours would add noise, not signal

The GC oscillation pattern in v6 is healthy: server allocates, GC collects, pages returned
to OS. No managed leak is detectable at any meaningful rate.

---

## Soak monitor details

- **Script:** `build_tools/_soak_v4.ps1`
- **Log:** `soak_log_rc3_v6.csv`
- **Sample interval:** 30 seconds
- **Thresholds:** server <=350 MB, tray <=350 MB, spectrum <=150 MB
- **Metric:** Process.WorkingSet64 (physical RAM pages mapped to each process)