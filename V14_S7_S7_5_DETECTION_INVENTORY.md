# V14_S7_S7_5_DETECTION_INVENTORY.md

Stage 7.5 -- STEP 1 deliverable. Fresh inventory of PS detection
chain in `tray.ps1` lines 5187-8929 (S12 update + S13 SMTC manager +
S14 art + S15 detector chain + S16 diagnostics + S17 main loop).
Cross-reference baseline for the C# port. Source-driven citations
throughout.

This document complements `V14_S7_REPLAN_DETECTION_INVENTORY.md`
(re-plan's behavioral inventory; design-phase). Where the re-plan
described WHAT, this document describes the EXACT CITATION points.

---

## 1. Update-check (S12, line 5187-5798)

Already ported in Stage 7.2 (UpdateCheckService). Not part of 7.5
scope. References listed for completeness.

## 2. SMTC manager + caching (S13, line 5262-6198)

`Get-SMTCManager` at `tray.ps1:5918` -- v12.0.0 singleton with 24h
TTL cache. Pre-v12.0.0 was 600ms TTL (line 5919-5925 historical
comment). C# 7.5 SmtcEventBridge re-uses this v12.0.0 watcher.

Per-tick caches (`tray.ps1:5993-6030`):
- `_smtcCacheTickId` -- per-tick cache invalidation marker
- `_smtcSessionsCache` -- one cache per tick
- `_smtcPropsResultCache` -- cross-tick MediaProperties cache
- `_smtcPropsTaskDict` -- in-flight TryGetMediaPropertiesAsync tasks
- `_smtcPropsCtsDict` -- CancellationTokenSources for above
- `_smtcPropsFiredThisTick` -- B-002/B-016 lifecycle tracking;
  v11.2.3 fix moved Remove to finally block
- `_smtcPropsLastFiredMs` -- v11.2.3 500ms rate-limit per SAUMID
- `_smtcTransitionGuardMs` -- 500ms ALPC suppression after track change
- `_smtcPbInfoCache`/`_smtcTlCache` -- per-session PlaybackInfo +
  TimelineProperties caches

C# 7.5 architectural answer: SMTCWatcher in `tray_native.cs` already
manages all of this internally (BurstWindow, ReadCool, RCW lifetime).
SmtcEventBridge just polls `DrainEvents()` and routes to ITrackResolver.
The whole machinery in S13 collapses to "trust the watcher."

## 3. SMTC art extraction (S14, line 6199-6381)

`Get-SMTCThumbnailDataUri` at `tray.ps1:6243`. RAS-state-machine for
deferred extraction. LRU 200 art cache at `tray.ps1` (need grep for
exact line; PS S14 docstring references it).

C# 7.5 architectural answer: `ArtLruCache` (200 entries; matches PS).
TrackResolver coordinates art access during OnTrackChanged. Single
art-resolution path (closes B-013).

## 4. Detector chain (S15, line 6382-8612)

Order per `tray.ps1:8873-8884`:
1. osu! (window title; `Get-OsuNowPlaying` at 7493)
2. spotify (SMTC; `Get-SpotifyNowPlaying` at 6890)
3. smtc-generic (SMTC; `Get-SMTCNowPlayingCached` at 7256)
4. browser (SMTC + window detection; `Get-BrowserMediaNowPlaying` at 6936)
5. soundcloud (SMTC + sc-rpc; `Get-SoundCloudNowPlaying` at 7182)
6. wmpCOM (`Get-WMPNowPlayingCOM` at 8083)
7. wmpSMTC (`Get-WMPNowPlayingSMTC` at 8222)
8. wmpUIA (`Get-WMPNowPlayingUIA` at 7946)
9. wmpTitle (`Get-WMPNowPlaying` at 8361)
10. vlc (`Get-VLCNowPlaying` at 8447)

C# 7.5 collapse:
- Detectors 2/3/4/5/7 (Spotify/smtc-generic/browser/soundcloud/wmpSMTC)
  -> all consolidated into Arm 1 SmtcEventBridge (event-driven from
  watcher; no per-detector polling)
- Detector 1 (osu) -> Arm 2 OsuDetector (window title parsing)
- Detector 10 (vlc) -> Arm 2 VlcHttpDetector (HTTP control)
- Detectors 6/8/9 (wmpCOM/wmpUIA/wmpTitle) -> Arm 2 WmpLegacyDetector
  (title-bar parsing; the simplest portable fallback per re-plan)

The chain walker (`tray.ps1:8859-8908`) is replaced by:
- Arm 1 SmtcEventBridge fires asynchronously from watcher events
- Arm 2 DetectorOrchestrator runs gap-fillers in priority order
  on 1s timer; first non-null wins

## 5. Diagnostics + circuit breaker (S16, line 8612-8754)

`Invoke-Detector` at `tray.ps1:8710-8754` -- 150ms slow threshold,
30-tick cooldown.

C# 7.5: per-detector slow-tick threshold 200ms (matches Stage 7.3
SlowTickWatchdog convention; +33% slack for C# JIT warmup).
Per-detector telemetry counters (`{name}_slow_ticks`,
`{name}_poll_errors`, `detector_hit_{name}`) replace the PS
`_detectorMs` hashtable. No 30-tick cooldown needed since detectors
self-cancel via async timeout (VLC HTTP) or process-existence check
(VLC, osu, WMP).

## 6. Main scrobble loop (S17, line 8765-8929)

`scrobbleTimer.add_Tick` at `tray.ps1:8775` runs every 100ms. Per-tick
phases at lines 8780-8929: freeze-triage wrapper, detector chain,
SC-shadow song-epoch, webhook send.

C# 7.5: NO equivalent 100ms timer. Detection is event-driven (Arm 1)
or 1-second-polled (Arm 2). The PS architecture's 100ms tick is
specifically what the redesign EXPLICITLY replaced (per re-plan
B-014 architectural pattern).

## 7. Webhook contract (S15, line 5344-5368 + invocation at 8910+)

`Send-Webhook` at `tray.ps1:5344-5368` -- shared HttpClient,
fire-and-forget POST.

Endpoint: `http://127.0.0.1:4242/webhook` (verified by `tray.ps1` grep).
Method: POST.
Content-Type: `application/json; charset=utf-8`.

JSON payload fields (from PS S15 emission):
- `source` (string; e.g., "spotify", "soundcloud")
- `artist`, `track`, `album` (strings; null if unknown)
- `durationMs`, `positionMs` (numbers; null if unknown)
- `isPaused` (bool)
- `art` (string; data URI or http URI)
- + various source-specific extras

C# 7.5 WebhookClient JSON: matches above shape. ADDS `tray="csharp14"`
field (Q3 default) to let server.js distinguish C# emission during
parallel period. ADDS `observedUtc` ISO-8601 timestamp.

Byte-equivalent for the same logical track: not strictly tested in
7.5 smoke (PS tray not running for direct comparison); deferred to
7.10 cutover validation when both trays will be exercised.

---

## 8. Architectural changes summary

| PS pattern | C# 7.5 equivalent | Rationale |
|---|---|---|
| `scrobbleTimer` 100ms tick | SmtcEventBridge event-driven (Arm 1) + DetectorOrchestrator 1s tick (Arm 2) | Re-plan B-014 closure |
| `Invoke-Detector` chain walker | DetectorOrchestrator first-non-null-wins | Same pattern, async-friendly |
| 5 SMTC caches in S13 | SMTCWatcher internal coalescing + bridge polls DrainEvents | v12.0.0 architectural win preserved |
| `_smtcPropsFiredThisTick` 3-strike lifecycle | Not present (event-driven; no fire-and-poll mechanism) | B-002/B-016 closed by construction |
| `[version]` cast accidental rejection (line 5745) | Explicit SemVerComparer.IsPreRelease | R6 closure (Stage 7.2) |
| Webhook fire-and-forget | WebhookClient with rate-limit telemetry | Same pattern |
| Art LRU 200 | ArtLruCache 200 entries | Same cap |
| Single chain walker mutates global state | TrackResolver lock-protected single state | B-002 lesson: serialize mutations |
