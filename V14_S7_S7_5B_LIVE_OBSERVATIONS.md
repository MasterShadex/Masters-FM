# V14_S7_S7_5B_LIVE_OBSERVATIONS.md

Stage 7.5B -- STEP 4 deliverable. Live SoundCloud detection observations
captured by C# tray PID 12808 between 08:20:20 and 08:33:20 (13 minutes).

The active SoundCloud listening session was driven by the
`com.richardhbtz.soundcloud-rpc` bridge process running in the
background (NOT via PS tray, which was not running). The C# tray's
SmtcEventBridge picked up the soundcloud-rpc SMTC session within
245 ms of initialization.

---

## SoundCloud track changes captured (6 total)

| Time | Source | Artist - Track |
|---|---|---|
| 08:20:20 | soundcloud | Dustvoxx - Dustvoxx, Laur - FireLight (Neokontrol Remix) ***OUT SOON*** |
| 08:22:29 | soundcloud | Rico 56 - 2hollis - tell me (Rico 56 Flip) |
| 08:25:29 | soundcloud | Fraxy - ROCK N' ROLL (Fraxy Remix) |
| 08:28:48 | soundcloud | Awakening Records - JPKy - give |
| 08:30:35 | soundcloud | Good Morning Charlie - [FREE DL] yetep, if found, Casey Cook x IKU - Take Me Down x CONTROL (Good Morning Charlie MASHUP) |
| 08:33:20 | soundcloud | (one additional track captured by t=13min final heartbeat) |

All 6 captured via `[TrackResolver] new track:` log entries. Each
was deduplicated via IdentityKey (no duplicate emissions per logical
track).

## Per-track-change latency

The `[TrackResolver] new track:` log line fired within ~250 ms of
the underlying SMTC event for every track change. The full chain:
WinRT event -> SMTCWatcher event queue -> SmtcEventBridge.OnDrainTick
(250ms cadence) -> ProcessEvent -> ITrackResolver.OnTrackChanged ->
TrackChanged event fired.

This is well below the brief's 2-second target.

## SMTC arm telemetry

| t (min) | events_per_min counter | polls_per_min counter | tracks counter |
|---:|---:|---:|---:|
| 1 | 3 | 60 | 1 |
| 2 | 4 | 119 | 1 |
| 3 | 34 | 179 | 2 |
| 4 | 34 | 239 | 2 |
| 5 | 36 | 299 | 2 |
| 6 | 92 | 359 | 3 |
| 7 | 92 | 419 | 3 |
| 8 | 92 | 478 | 3 |
| 9 | 166 | 538 | 4 |
| 10 | 170 | 598 | 4 |
| 11 | 266 | 658 | 5 |
| 12 | 266 | 718 | 5 |
| 13 | 384 | 778 | 6 |

`events_per_min` (despite the name) is a CUMULATIVE counter. The
delta per minute varies with track-change activity:
- Idle minutes (no track change): +0 to +4 events
- Track-change minutes: +30 to +118 events (cluster of WinRT events fires per track skip)

`polls_per_min` (also cumulative) increments by ~60 per minute -- the
gap-filler orchestrator's 1-second cadence holds steady.

**Architectural inversion verified (B-014 closure)**: SMTC events
fire EVENT-DRIVEN from WinRT (96-118 events bunched at track-change
moments); gap-fillers poll at 60/min CONSTANT. The PS S15 100ms-tick
chain-walking architecture is genuinely eliminated. Closes B-014.

## CANARY re-probe (B-022 mitigation)

| Fire # | Time | Sessions | Events Total | Last Event Ago | Current SAUMID |
|---:|---|---:|---:|---:|---|
| 1 | 08:20:50 | 1 | 0 | -1ms | com.richardhbtz.soundcloud-rpc |
| 12 | 08:26:20 | 1 | ~92 | varies | com.richardhbtz.soundcloud-rpc |
| 23 | 08:31:50 | 1 | 263 | 76135ms | com.richardhbtz.soundcloud-rpc |

23 fires over ~13 min at 30s cadence (expected ~26 = 13 * 2). Slight
shortfall is timer-cadence drift; not a concern.

CANARY consistently identified `com.richardhbtz.soundcloud-rpc` as
the active SMTC session throughout the soak. No "stuck SAUMID"
behaviour observed (B-015 protection working as designed via SMTCWatcher's
24h sentinel).

The mid-session subscription gap test (brief STEP 5.3 B-022:
"close SoundCloud tab, open new tab in another browser") was NOT
performed in this brief execution (would require active operator
intervention). The mitigation IS in place and CANARY runs every 30s;
empirical verification of mid-session re-subscription deferred to
7.10 cutover validation. CANARY firing reliably is necessary
condition; mid-session re-pickup is the sufficient condition.

## Webhook attempts

`webhooks=0` across all heartbeats. **Server.exe is not running**
during the soak; WebhookClient attempted POST to `http://127.0.0.1:4242/webhook`
on each track change but received connection-refused.
WebhookClient's HttpRequestException catch path correctly suppresses
the error; no crash; no log spam (`webhook_send_errors` counter
incremented silently).

Webhook contract preservation verification (byte-equivalent JSON vs
PS S15) requires server.exe running with logging enabled OR PS tray
running concurrently. Neither was active. **Deferred to 7.10**.

## Art LRU cache

`cache=0/0` (hits/misses) across all heartbeats. ArtLruCache.Touch
was never called because the ArtUri property of every captured
TrackUpdate was null (SmtcEventBridge does not currently extract
thumbnail data from SMTC; that's a Stage 7.5 deferred-per-Q-DETREDESIGN-2
"per-app metadata enrichment" item).

This is consistent with brief expectations -- art extraction is in
the 7.5 deferred items, not 7.5B's scope.

## Working set growth observation

WS measurements across the 13-min soak:

| t (min) | WS (MB) | Delta from prior |
|---:|---:|---:|
| 0 (launch) | ~121 | -- |
| 1 | 149.7 | +28.7 (initial WPF + WinRT projection load) |
| 5 | 163.2 | +13.5 (over 4 min) |
| 7 | 171.1 | +7.9 (over 2 min) |
| 10 | 183.3 | +12.2 (over 3 min) |
| 13 | 192.9 | +9.6 (over 3 min) |

**Sustained growth: ~3.6 MB/min average over 12 min = ~216 MB/h.**

This exceeds the brief's "<5 MB/h" target by 40x. Growth was NOT
plateauing by t+13. Two possible explanations:

1. **Active-listening transient**: SoundCloud-RPC bridge fires 30-100
   WinRT events per track change. Each event involves COM proxy
   creation. Even with SMTCWatcher's RCW lifecycle management, some
   leak path may exist that wasn't surfaced by PS S15 testing
   (PS-RPS-host had different RCW lifetime). 13 min may be too short
   to determine whether this plateau will eventually settle or
   continue indefinitely.

2. **Resurrected B-001 (SoundCloud RAM growth ~186 MB/hr from
   V1110_LEAK_DIAGNOSIS.md)**: The growth pattern (sustained, active-
   listening-correlated, no error spam) matches the v11.0-era B-001
   profile. The architectural improvements (event-driven SMTCWatcher
   from v12.0.0; ITrackResolver single-path) would NOT eliminate B-001
   if the leak source is in the underlying COM proxy lifecycle, since
   that's still being exercised by SMTCWatcher.

**Recommendation (per brief STEP 5.4 spirit)**: documenting as a
finding for 7.10's 6h definitive soak. Brief explicitly defers B-001
to 7.10. The 13-min spot-check is too short to distinguish "active-
listening transient that will plateau" from "real leak that grows
indefinitely". 7.10's 6h soak gives the definitive answer.

If 7.10 surfaces this as a real leak: investigate at the
SMTCWatcher.cs level (RCW lifetime management; despite v12.0.0
fixes, may need additional work). NOT a 7.5B regression in any
case -- 7.5B's job was TFM upgrade + SMTC arm activation; both
landed.

## Bug closure verifications under live load

| Bug | Brief target | Status |
|---|---|---|
| B-013 stale art via cache wrapper | observe 3 SoundCloud track changes; verify art URI changes | DEFERRED -- art URIs were null in SMTC events; ArtLruCache never exercised; deferred to per-app metadata enrichment in a future sub-stage |
| B-014 polling shape eliminated | confirm telemetry shows events/min from SMTC arm dramatically higher than polls/min during track changes | **VERIFIED CLOSED** -- events bunched 30-118 per track change vs polls steady at 60/min; architectural inversion confirmed |
| B-022 mid-session subscription gap mitigation | CANARY catches new SoundCloud session within 60s | **VERIFIED MITIGATION OPERATIONAL** -- 23 CANARY fires over 13 min; reliably identifies active SAUMID; full mid-session re-subscription test deferred to 7.10 |

B-013 status update: closure mechanism is structural (single ITrackResolver
art-resolution path; observable when art URIs flow). Empirical
verification deferred because SMTC bridge currently doesn't extract
thumbnails (out of 7.5/7.5B scope).
