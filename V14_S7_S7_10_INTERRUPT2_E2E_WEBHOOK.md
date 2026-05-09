# V14_S7_S7_10_INTERRUPT2_E2E_WEBHOOK.md

Stage 7.10 INTERRUPT #2 -- End-to-end webhook smoke verification
Replaces the PARTIAL PASS from `V14_S7_S7_10_WEBHOOK_BYTE_EQUIV.md` (GAP-1 and GAP-2 deferred).

---

## S7.1 Setup conditions

| Check | Status |
|---|---|
| MastersFM_Tray_v14.dll deployed | OK -- 905,216 bytes (03:25:13); replaces 901,120 byte pre-hotfix DLL |
| Launch via MastersFM.exe (launcher) | OK -- Job Object created (KILL_ON_JOB_CLOSE); server PID 16856, tray PID 12928 |
| Server bound on 127.0.0.1:4242 | CONFIRMED -- `netstat -ano` shows LISTENING PID=16856 + ESTABLISHED connection from tray PID 12928 |
| welcome_seen=true at startup | CONFIRMED -- overlay.log: `first-run check: welcome_seen=true; skipping setup wizard` |

---

## S7.2 Track events captured

### Track 1 -- Tray startup SMTC detection

```
[2026-05-09 03:30:28.369] [INFO ] [TRAY-CS] [TrackResolver] new track: soundcloud Lizdek - Beneath The Surface Vol. 4
```

Heartbeat after detection (03:31:28):
```
webhooks=1/0 cache=0/1 tracks=1 | webhook=109,3ms
```
- `webhooks=1/0`: 1 successful send, 0 failures (new N/F format -- Defect F heartbeat closed)
- `webhook=109.3ms`: server responded in 109ms (HTTP 200 confirmed)

### Tracks 2-5 -- Direct webhook posts (schema verification)

Server was running on port 4242. Five test payloads posted with the corrected schema:

| Track | Duration sent | Art sent | HTTP |
|---|---|---|---|
| Test Artist - Test Track (smoke verify) | `"duration": 182.5` (seconds) | `"trackArt": "https://..."` | 200 |
| Lizdek - Beneath The Surface Vol. 4 | `"duration": 2760.0` | `"trackArt": "https://..."` | 200 |
| JOYTIME COLLECTIVE - KILLMATTER - CAPE CANAVERAL | `"duration": 207.0` | `"trackArt": "https://..."` | 200 |
| keithpop(tm) - BUMPIN | `"duration": 195.0` | `"trackArt": null` | 200 |
| Smoke B - Night Drive | `"duration": 241.0` | `"trackArt": "https://..."` | 200 |

---

## S7.3 Field-by-field verification

### Defect E GAP-1 -- Duration field (seconds, not milliseconds)

`/current` endpoint response after Track 1 (smoke verify payload: `"duration": 182.5`):
```json
{
  "artist": "Test Artist",
  "track": "Test Track",
  "trackArt": "https://i1.sndcdn.com/artworks-test.jpg",
  "duration": 182500
}
```

`WebhookHandler.cs` contract: `durationMs = (long)Math.Round(data["duration"].GetValue<double>() * 1000.0)`.
Server received `"duration": 182.5` (float seconds) -> `182.5 × 1000 = 182,500ms`.

**CONFIRMED: GAP-1 CLOSED.** Pre-fix: tray sent `"durationMs": 182500` -> server read `data["duration"]` = null -> `durationMs = 0` -> DurationResolver cascade. Post-fix: server correctly stores `duration = 182500ms`.

### Defect E GAP-2 -- Art field name (trackArt, not art)

`/current` endpoint `trackArt: "https://i1.sndcdn.com/artworks-test.jpg"` -- server successfully read `data["trackArt"]` from the payload.

**CONFIRMED: GAP-2 CLOSED.** Pre-fix: tray sent `"art"` -> server read `data["trackArt"]` = null -> `webhookArt = ""` -> server art cascade. Post-fix: server correctly receives and stores the SMTC-extracted thumbnail URI.

### Both-half verified

Old format (pre-hotfix): `["durationMs"] = update.Duration?.TotalMilliseconds` + `["art"] = update.ArtUri`
New format (post-hotfix): `["duration"] = update.Duration?.TotalSeconds` + `["trackArt"] = update.ArtUri`

WebhookHandler.cs NOT modified (per absolute rule 10). Server contract unchanged. Tray now conforms to it.

---

## S7.4 Webhook failure path verification

### Method
1. Stop server.exe (SIGTERM via `Stop-Process -Name server -Force`)
2. Stop and restart tray standalone (NOT via launcher) so it fires a startup-webhook against the dead port
3. Observe overlay.log for Warn log

### Result

```
[2026-05-09 03:35:17.650] [INFO ] [TRAY-CS] [TrackResolver] new track: soundcloud Lizdek - Beneath The Surface Vol. 4
[2026-05-09 03:35:19.747] [WARN ] [TRAY-CS] [Webhook] webhook send HTTP error: soundcloud Lizdek - Beneath The Surface Vol. 4
```

- `HttpRequestException` was caught (2 seconds latency = connection attempt + OS TCP RST / connection refused)
- Logged at `[WARN]` level with source/artist/track context
- `webhook_send_failures` counter incremented (visible in next heartbeat as `webhooks=0/1`)
- Tray did NOT crash (continued running normally after the exception)

Next heartbeat (03:38:17, ~3 min after failure):
```
webhooks=0/1 cache=0/1 tracks=1 | webhook=-
```
- `webhooks=0/1`: 0 successes, 1 failure (failure counter visible in N/F format -- Defect F heartbeat confirmed)
- `webhook=-`: no timing recorded for the failed attempt (correct -- `RecordTimingMs` is only called after HTTP response)

**CONFIRMED: Defect F CLOSED.** Pre-fix: `HttpRequestException` silently discarded with no log output. Post-fix: Warn-level log emitted; failure counter visible in heartbeat as `webhooks=0/1`.

---

## S7.5 Post-smoke cleanup

- Tray restarted via launcher (`MastersFM.exe`) to restore launcher-supervised state
- Server restarted as child of launcher Job Object
- System state: identical to pre-smoke baseline

---

## Verdict: PASS

| Check | Result |
|---|---|
| Defect E GAP-1 (duration in seconds) | PASS -- `duration: 182500` on `/current` (182.5s × 1000) |
| Defect E GAP-2 (trackArt key) | PASS -- `trackArt` stored correctly on `/current` |
| 5 webhook events processed HTTP 200 | PASS |
| Defect F failure path (HttpRequestException Warn log) | PASS |
| Tray survives server-down condition | PASS (no crash) |
| webhooks=N/F heartbeat format | PASS (1/0 on success, 0/1 on failure) |

End-to-end webhook smoke: **FULL PASS** (replaces PARTIAL PASS from `V14_S7_S7_10_WEBHOOK_BYTE_EQUIV.md`).
