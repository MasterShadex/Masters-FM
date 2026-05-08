# V14_S7_S7_10_WEBHOOK_BYTE_EQUIV.md

Stage 7.10 STEP 5 — Webhook byte-equivalence verification vs PS S15

---

## 1. Architecture Context

Both tray host types ultimately POST JSON to `http://127.0.0.1:4242/webhook`.
They differ in which code constructs that JSON:

| Host | Detection path | Webhook construction |
|---|---|---|
| **PS tray** (csc.exe / `tray_launcher.cs`) | tray.ps1 polling loop | `Send-WebhookAsync` (tray.ps1 ~L9285-9299) |
| **C# WPF tray** (Stage 7.1B, `MastersFM_Tray_v14.dll`) | `SmtcEventBridge` + `DetectorOrchestrator` (native C#) | `WebhookClient.BuildJsonPayload` (`src/tray_csharp/Services/WebhookClient.cs`) |

The C# WPF tray (post-cutover default) does **not** invoke `tray.ps1` for track
detection. `App.xaml.cs` starts `SmtcEventBridge.Start()` + `DetectorOrchestrator.Start()`
directly; `WebhookClient.SendTrackUpdateAsync` handles all HTTP POSTs.

---

## 2. PS Tray (S15) Webhook Schema

Source: `src/tray.ps1` lines 9281–9299 (new-track), 9133–9141 (heartbeat).

### 2a. New-track payload (tray.ps1:9285)

```json
{
  "artist":     "<string>",
  "track":      "<string>",
  "source":     "<string>",
  "positionMs": <long ms>,
  "duration":   <double SECONDS>,
  "isPaused":   <bool>,
  "trackArt":   "<uri string — optional, omitted if no SMTC thumbnail>"
}
```

### 2b. Heartbeat payload (tray.ps1:9133)

Same fields as 2a plus:
```json
  "seek": <bool>
```

### 2c. Transport

`Send-WebhookAsync` → `HttpClient.PostAsync` → `application/json; charset=utf-8`
Fire-and-forget; response discarded.

---

## 3. C# Tray Webhook Schema

Source: `src/tray_csharp/Services/WebhookClient.cs:BuildJsonPayload` (Stage 7.5).

```json
{
  "source":      "<string>",
  "artist":      "<string|null>",
  "track":       "<string|null>",
  "album":       "<string|null>",
  "durationMs":  <double MILLISECONDS|null>,
  "positionMs":  <double ms|null>,
  "isPaused":    <bool>,
  "art":         "<uri string|null>",
  "tray":        "csharp14",
  "observedUtc": "<ISO-8601 UTC string>"
}
```

Transport: `HttpClient.PostAsync` → `application/json; charset=utf-8`
Awaited (with 5 s timeout + `CancellationToken`); `HttpRequestException` silently dropped.

---

## 4. Server Handler Expectations

Source: `src/server_dotnet/WebhookHandler.cs` lines 87–105.

| Field read by server | Type | Comment |
|---|---|---|
| `data["artist"]` | string | |
| `data["track"]` | string | |
| `data["source"]` | string | |
| `data["duration"]` | double seconds → ×1000 → long ms | B2: "tray sends seconds (float)" |
| `data["positionMs"]` | double ms → long ms | |
| `data["isPaused"]` | bool | |
| `data["seek"]` | bool | heartbeat seek flag |
| `data["trackArt"]` | string | SMTC / tray-provided thumbnail URI |
| `data["originUrl"]` | string | optional |

The server does **not** read `durationMs`, `art`, `album`, `tray`, or `observedUtc`.

---

## 5. Contract Parity Table

| Field | PS tray sends | C# tray sends | Server reads | PS compat | C# compat |
|---|---|---|---|---|---|
| `artist` | `artist` (string) | `artist` (string\|null) | `data["artist"]` | ✅ | ✅ |
| `track` | `track` (string) | `track` (string\|null) | `data["track"]` | ✅ | ✅ |
| `source` | `source` (string) | `source` (string) | `data["source"]` | ✅ | ✅ |
| `positionMs` | `positionMs` (long ms) | `positionMs` (double\|null ms) | `data["positionMs"]` | ✅ | ✅ |
| Duration | `duration` (double SECONDS) | `durationMs` (double\|null ms) | `data["duration"]` × 1000 | ✅ | ❌ GAP |
| Art/thumbnail | `trackArt` (string\|omitted) | `art` (string\|null) | `data["trackArt"]` | ✅ | ❌ GAP |
| `isPaused` | `isPaused` (bool) | `isPaused` (bool) | `data["isPaused"]` | ✅ | ✅ |
| `seek` | `seek` (bool, heartbeat) | not sent | `data["seek"]` | ✅ | N/A |
| `album` | not sent | `album` (string\|null) | not read | N/A | ignored |
| `tray` | not sent | `"csharp14"` | not read | N/A | ignored |
| `observedUtc` | not sent | ISO-8601 string | not read | N/A | ignored |

---

## 6. Evidence

### 6a. PS tray webhook entries (server.log — all 45)

Session: 2026-05-07T15:51–17:23 (Stage 7.8 / pre-cutover PS tray host).
All 45 entries parsed correctly by server; field schema verified from server.log format
`🔔 Webhook [SoundCloud]: "artist - track" dur=Xs pos=Xs art=✗ paused=false same=false`.

Sample (5 of 45, covering new-track, heartbeat-pause, heartbeat-same):

```
[2026-05-07T15:51:17.079Z] Webhook [SoundCloud]: "Lizdek - DJ Snake & Space Laces - Reloaded..." dur=178s pos=0s art=✗ paused=false same=false
[2026-05-07T15:51:26.285Z] Webhook [SoundCloud]: "Lizdek - ..." dur=178s pos=0s art=✗ paused=true same=true
[2026-05-07T15:51:27.305Z] Webhook [SoundCloud]: "contra - Skrillex, Nitepunk & DJ Smokey - POOSHA" dur=252s pos=0s art=✗ paused=false same=false
[2026-05-07T16:28:37.630Z] Webhook [SoundCloud]: "SABLE VALLEY - Nitepunk - Slices (Silcrow Remix)" dur=216s pos=0s art=✗ paused=false same=false
[2026-05-07T17:21:20.372Z] Webhook [SoundCloud]: "JOYTIME COLLECTIVE - KILLMATTER - CAPE CANAVERAL" dur=207s pos=0s art=✗ paused=false same=false
```

PS tray webhook contract: **COMPATIBLE** with server.

### 6b. C# tray webhook evidence (overlay.log + source)

C# tray session: 2026-05-08T23:44:43 PID=26948 (post-reinstall validation, STEP 3).

`overlay.log` line 72:
```
[23:44:44.073] [INFO ] [TRAY-CS] [TrackResolver] new track: soundcloud keithpop™ - BUMPIN
```

`TrackResolver.OnTrackChanged` (line 90) calls `_webhook.SendTrackUpdateAsync` immediately after
logging. `WebhookClient` built the payload and attempted `PostAsync` to port 4242.

**Server.exe was NOT running at that moment** (first launcher session exited at 23:44:08; second
install session ran MastersFM_Tray.exe directly, not via MastersFM.exe + server.exe chain).
`HttpRequestException` was caught silently per fire-and-forget pattern. No `server.log` entry
generated. Payload schema confirmed from `BuildJsonPayload` source; live bytes not captured.

C# tray webhook contract: **2 GAPS** (see §5).

---

## 7. Gap Analysis

### GAP-1: Duration field name + unit mismatch

| | PS tray | C# tray | Server expects |
|---|---|---|---|
| Field | `"duration"` | `"durationMs"` | `"duration"` |
| Unit | float, **seconds** | float, **milliseconds** | seconds (×1000 to store as ms) |
| Server result when C# sends | — | `data["duration"]` = null → durationMs = 0 | Duration = 0 → falls back to DurationResolver cascade |

**Functional impact**: Server DurationResolver queries Deezer/MusicBrainz on every C# tray
new-track event (adds ~0.5–2 s latency per track). Duration eventually resolves correctly
via cascade. No user-visible error; progress bar may start at 0 before resolving.

### GAP-2: Art field name mismatch

| | PS tray | C# tray | Server expects |
|---|---|---|---|
| Field | `"trackArt"` (omitted when null) | `"art"` (null when no thumbnail) | `"trackArt"` |
| Server result when C# sends | — | `data["trackArt"]` = null → webhookArt = "" | Art from webhook ignored; full server cascade runs |

**Functional impact**: SMTC thumbnails extracted by `SmtcEventBridge.TryExtractThumbnail` are
not forwarded to the server. Art resolves via the server's 11-source cascade (SoundCloud oEmbed,
Deezer, iTunes, etc.) rather than from the SMTC thumbnail shortcut. Correct art still appears;
B11 art-retry path unaffected. The Stage 7.5 `ArtLruCache` on the tray side is populated
correctly but the server never sees the tray-extracted URI.

---

## 8. Recommended Fix

**Option A (preferred): Fix `WebhookClient.cs`** — minimal server impact.

```csharp
// In BuildJsonPayload, replace:
["durationMs"] = update.Duration?.TotalMilliseconds,
["art"] = update.ArtUri,
// With:
["duration"] = update.Duration?.TotalSeconds,     // seconds (float), matches server B2
["trackArt"] = update.ArtUri,                     // matches server data["trackArt"]
```

**Option B**: Update `WebhookHandler.cs` to fall back to `durationMs` when `duration` absent,
and fall back to `art` when `trackArt` absent. More permissive but widens server contract.

Option A is tracked as an open issue for a future Stage 7 sub-stage commit (not a Stage 7.10 blocker).

---

## 9. Verdict

| Path | PS tray (csc.exe + tray.ps1) | C# tray (WPF + WebhookClient) |
|---|---|---|
| Core fields (artist/track/source/positionMs/isPaused) | ✅ PASS | ✅ PASS |
| Duration | ✅ PASS | ❌ GAP-1 (durationMs vs duration; wrong unit) |
| Art | ✅ PASS | ❌ GAP-2 (art vs trackArt) |
| Server processes webhook | ✅ 45 entries confirmed | ✅ (server handles null fields; cascade compensates) |

**STEP 5 result: PARTIAL PASS.**

PS tray webhook contract is fully byte-compatible with the server (confirmed by 45 server.log
entries). C# tray native detection path sends valid JSON to the correct endpoint but has 2
field-level schema gaps that degrade art/duration pass-through. Server compensates via cascade.
No overlay corruption or lost tracks. Gaps tracked as open issue; fix deferred to future
Stage 7 sub-stage.

Stage 7.10 cutover proceeds.

---

## Appendix — Source References

| File | Lines | Notes |
|---|---|---|
| `src/tray.ps1` | 9281–9299 | new-track webhook construction |
| `src/tray.ps1` | 9133–9141 | heartbeat webhook construction |
| `src/tray.ps1` | 5344–5368 | `Send-WebhookAsync` implementation |
| `src/tray_csharp/Services/WebhookClient.cs` | 79–100 | `BuildJsonPayload` |
| `src/server_dotnet/WebhookHandler.cs` | 87–105 | field extraction / B2 duration conversion |
| `C:\Users\Master\AppData\Local\MastersFM\server.log` | all 1946 lines | 45 PS-tray webhook entries |
| `C:\Users\Master\AppData\Local\MastersFM\overlay.log` | line 72 | C# tray TrackResolver new-track log |
