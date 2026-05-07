# V14 Stage 4 Phase 2 — Port Plan

**Based on:** Phase 1 empirical captures (2026-05-04)
**Target version:** v13.0.0 (justifies major bump per V14_NET8_MIGRATION_PLAN.md Section 5)
**Predecessor document:** V14_S4_P1_ENDPOINT_INVENTORY.md, V14_S4_P1_SSE_CONTRACT.md

---

## Executive Summary

Stage 4 ports `server.js` (1,610 LOC, Node.js) + `discord_rpc.js` (337 LOC) to a single ASP.NET Core minimal API project on .NET 8. The port is broken into **10 independently-testable sub-stages**, each validated against the Phase 1 baseline corpus before proceeding to the next. The rollback path (`$UseDotnet8Server=$false`) remains available until sub-stage 4.10 passes.

**Total estimate:** 60-100 hours (realistic 80h)
**Phase 2 entry point:** Sub-stage 4.1 (skeleton project)
**Shipping version:** v13.0.0

---

## Section 1 — Sub-Stage Decomposition

---

### Sub-stage 4.1 — Skeleton ASP.NET Core project + port binding

**Deliverables:**
- `src/server_dotnet/server_dotnet.csproj` (net8.0-windows, minimal API)
- Binds to `127.0.0.1:4242`, returns 404 for all routes
- CORS headers on every response (mirror server.js global headers)
- Server error handler (EADDRINUSE equivalent)
- Logging framework (Serilog or Microsoft.Extensions.Logging → flat file `server.log`)
- Build pipeline integration: `$UseDotnet8Server=$false` flag in `_full_rebuild.ps1`

**Estimated hours:** 6-8h
**Validation criteria:**
- `dotnet publish` produces `server.exe`
- `server.exe` binds 127.0.0.1:4242 without Firewall prompt
- `GET /anything` returns 404
- `_full_rebuild.ps1` switches between Node pkg and .NET server via flag

**Rollback:** `$UseDotnet8Server=$false` uses original Node pkg path unchanged

---

### Sub-stage 4.2 — Read-only endpoints

**Endpoints:**
- `GET /current` — return `currentTrack` JSON or `null`
- `GET /version` — return `{ bootId: <long> }`
- `GET /` and `GET /?*` — serve `overlay.html` from CWD
- `GET /customize` — serve `customize.html` from CWD
- `GET /update` — serve `update.html` from CWD
- `GET /update-status` — read temp status file
- `GET /manifest.json` — return hardcoded PWA manifest
- `GET /MastersFM.ico` and `GET /favicon.ico` — serve icon file
- `OPTIONS *` — CORS preflight 204
- Method-not-allowed 405 handler (with Allow header)
- 404 fallthrough

**State introduced:** `currentTrack` (nullable object), `SERVER_BOOT_ID` (long)
**Estimated hours:** 6-8h

**Validation criteria (diff against baseline captures):**
- `GET /version` body: `{"bootId":<long>}` — Content-Type: application/json, Cache-Control: no-store
- `GET /current` body: `null` when no track (Content-Type: application/json)
- `GET /manifest.json` body: byte-for-byte match to `V14_S4_P1_BASELINE_CAPTURES/get_manifest.json`
- `GET /` Content-Type: text/html, body length matches baseline
- 405: `Allow` header present, body matches baseline

**JSON serialization notes:**
- All JSON must be camelCase (`System.Text.Json` with `PropertyNamingPolicy.CamelCase`)
- No trailing whitespace in JSON output
- `null` serializes as `null` literal, not omitted

---

### Sub-stage 4.3 — SSE endpoint + heartbeat

**Endpoints:**
- `GET /events` — SSE stream

**State introduced:**
- `sseClients`: `ConcurrentDictionary<Guid, HttpResponse>` (or similar thread-safe collection)
- `sseBroadcast()`: broadcasts `data: <JSON>\n\n` to all connected clients
- Heartbeat timer: 15-second interval, writes `: ping\n\n`
- Status log: 60-second interval logs `sseClients.size`

**Estimated hours:** 10-12h (most time on ASP.NET Core response buffering + thread safety)

**Validation criteria:**
- `GET /events` response headers EXACTLY match baseline:
  ```
  Content-Type: text/event-stream
  Cache-Control: no-cache, no-transform
  Connection: keep-alive
  X-Accel-Buffering: no
  Transfer-Encoding: chunked
  ```
- On connect: immediately writes `data: null\n\n` (no current track yet)
- Heartbeat `: ping\n\n` arrives at ~15s intervals (verified by 20s curl capture)
- Multiple concurrent connections: all receive the same broadcasts
- On disconnect: client removed from broadcast set (no write to closed socket)

**Critical implementation notes:**
1. Disable ASP.NET Core response buffering: `context.Response.Body.FlushAsync()` after each write, OR use `IHttpResponseBodyFeature.DisableBuffering()` at the start of the handler
2. Thread safety: `currentTrack` is read by SSE broadcast AND written by webhook handler; use `volatile` or `ReaderWriterLockSlim` or an immutable reference swap pattern
3. The immediate-state-on-connect write must use the CURRENT value of `currentTrack` at the moment the connection is accepted, not a snapshot captured at server start

---

### Sub-stage 4.4 — Webhook handler + pause/seek/drift logic

**Endpoints:**
- `POST /webhook` — primary track state receiver

**State modified:** `currentTrack`, `artResolved`, `artResolving`

**Estimated hours:** 12-15h (the most complex single endpoint)

**Behavioral requirements (all must be replicated from server.js:903-1071):**

1. **New track detection:** `isSameTrack(artist, track)` — case-insensitive trim-normalized key `${artist.trim().lower()}|||${track.trim().lower()}`
2. **Duration unit conversion:** webhook sends seconds (float), store as milliseconds (`Math.Round(data.duration * 1000)`)
3. **startedAt calculation:** `positionMs > 0 ? (now - positionMs) : now`
4. **Stale position guard:** On track CHANGE (not first webhook after server start), ignore `positionMs > 5000` — guard `isTrackChange` vs `hadPrevTrack && isNewTrack`
5. **Pause handling:** On pause-flip, re-pin `startedAt = now - positionMs`, set `pausedAt = now`
6. **Resume handling:** Re-pin `startedAt = now - positionMs`, clear `pausedAt = 0`
7. **Seek handling:** `isSeek = true` flag — re-pin `startedAt = now - positionMs` only when NOT paused
8. **Duration update:** Update `currentTrack.duration` on every heartbeat if changed; log if delta > 1000ms
9. **First-heartbeat correction:** Within 2s of track accept, if drift > 5s, re-pin startedAt
10. **Continuous drift correction:**
    - Forward correction (SMTC > overlay by > 4s): re-pin
    - Backward correction (overlay > SMTC by > 30s): re-pin
    - Only when `!isSeek && !isPaused && positionMs > 1000`
11. **Art retry:** If no art, try resolveArtwork on retry when new trackArt arrives

**Validation criteria:**
- `POST /webhook` with same-track body → 200 `OK` (no crash)
- `POST /webhook` with new-track body → 200 `OK`, GET /current shows new track
- `POST /webhook` bad JSON → 400 `Bad Request`
- Pause webhook → GET /current shows `isPaused: true`, `pausedAt > 0`
- Resume webhook → GET /current shows `isPaused: false`, `pausedAt: 0`

---

### Sub-stage 4.5 — Config endpoints

**Endpoints:**
- `GET /overlay-config` — read `cfg.overlay` from config.json
- `POST /save-overlay-config` — write `cfg.overlay`, clear previewConfig, SSE broadcast `event: overlay-config`
- `GET /preview-config` — return in-memory previewConfig or saved overlay
- `POST /preview-config` — set in-memory previewConfig, SSE broadcast `event: preview-config`
- `POST /reload-config` — reload Discord RPC settings from config.json

**State introduced:**
- `previewConfig`: nullable in-memory object
- Config file path: `%APPDATA%\MastersFM\config.json`

**Estimated hours:** 8-10h

**Deep-merge semantics (MUST replicate exactly from server.js:120-134):**
```
deepMergeConfig(target, source):
  out = {...target}
  for k in source.keys:
    if k not in out: out[k] = source[k]  // new key: use default
    elif both are non-null, non-array objects: out[k] = deepMergeConfig(target[k], source[k])
    // else: user value wins (no overwrite)
  return out
```
Arrays are NEVER merged element-by-element; array values are kept wholesale.

**Validation criteria (diff against baseline):**
- `GET /overlay-config` body matches baseline (including `liveAudioVisualizer` injection from top-level)
- `POST /save-overlay-config` with current config → 200 OK, GET /overlay-config body unchanged
- `GET /preview-config` before any POST → returns same as GET /overlay-config
- `POST /preview-config` with test config → GET /preview-config returns test config
- `POST /save-overlay-config` → GET /preview-config returns saved config (previewConfig cleared)
- `POST /save-overlay-config` triggers `event: overlay-config` SSE broadcast
- `POST /preview-config` triggers `event: preview-config` SSE broadcast

---

### Sub-stage 4.6 — Preset management endpoints

**Endpoints:**
- `GET /list-presets`
- `POST /save-preset`
- `GET /load-preset?name=`
- `DELETE /delete-preset?name=`

**Preset storage:** `%APPDATA%\MastersFM\presets\<name>.json`

**Name sanitization (must match exactly):**
```
name.Replace(/[^a-zA-Z0-9 _\-]/g, '').Trim().Slice(0, 64)
```
C# equivalent:
```csharp
var safe = Regex.Replace(name, @"[^a-zA-Z0-9 _\-]", "").Trim();
if (safe.Length > 64) safe = safe.Substring(0, 64);
```

**Estimated hours:** 4-5h

**Validation criteria (diff against baseline):**
- Full CRUD sequence: save → list (contains) → load → delete → load (404)
- Name sanitization: `"!!!"` → `Invalid name` (400); `"Test Name"` → `test-name.json`? No: ` Test Name ` → `Test Name` (trimmed only, spaces preserved)
- `POST /save-preset` response: `{"saved":"<sanitized name>"}` (application/json)
- `GET /list-presets` returns sorted alphabetical array

---

### Sub-stage 4.7 — Art proxy endpoint + client-log

**Endpoints:**
- `GET /art` — proxy current track art
- `POST /client-log` — relay log messages

**Art proxy behavior:**
- If `currentTrack == null || trackArt == ""`: 404
- If `trackArt.startsWith("data:")`: decode base64, return binary with detected MIME
- Else: proxy via HttpClient, stream bytes to response

**Estimated hours:** 4-6h

**Validation criteria:**
- `GET /art?t=<now>` → 200 with image/jpeg content when track has HTTPS art URL
- `GET /art` when no track → 404
- `POST /client-log` with valid JSON → 204
- `POST /client-log` with invalid JSON → 204 (fire-and-forget, always succeeds)

---

### Sub-stage 4.8 — Screenshot endpoint

**Endpoints:**
- `GET /screenshot`
- `POST /screenshot-response`

**State introduced:**
- `_screenshotPending`: nullable slot `{ requestId, resolve, fail }`
- `_screenshotCounter`: atomically-incrementing long

**Estimated hours:** 4-5h

**Validation criteria:**
- `GET /screenshot` with 0 SSE clients → 503 `No overlay connected (sseClients.size === 0)`
- `POST /screenshot-response` with no pending → 409 `No screenshot pending`
- `POST /screenshot-response` with requestId mismatch → 409 `requestId mismatch (stale response)`
- `GET /screenshot` with SSE client → sends `event: capture\ndata: {"requestId":"N"}\n\n`, waits 2.5s for response

**Note on 2.5s timeout:** The Node version uses `setTimeout(..., 2500)`. ASP.NET equivalent: `CancellationTokenSource` with `CancelAfter(2500)`.

---

### Sub-stage 4.9 — Artwork resolver + duration resolver (art cascade)

**Estimated hours:** 18-22h (largest single sub-stage)

**This sub-stage ports the entire async art lookup cascade from server.js:678-798:**

1. SMTC thumbnail passthrough (data: URIs for browser platforms)
2. SoundCloud direct search (SoundCloud source)
3. osu! direct search (osu! source)
4. Webhook art URL (with upgrade to t500x500, accessibility check)
5. SoundCloud oEmbed (originUrl contains soundcloud.com)
6. Deezer API (exact + cleaned variants)
7. iTunes API (exact + cleaned variants)
8. MusicBrainz + Cover Art Archive (exact + cleaned variants)
9. SMTC thumbnail fallback (data: URIs, non-browser)
10. YouTube video thumbnail scrape (YouTube source)
11. Bing image search scrape (all remaining)

Also ports:
- `resolveDuration()` (Deezer → iTunes → MusicBrainz, server.js:422-486)
- `enrichByTitle()` for unknown/placeholder artists (Deezer → iTunes, server.js:801-839)
- `cleanTrack()` and `cleanArtist()` text normalization (server.js:365-378)
- `isPlaceholderArtist()` detection (server.js:349-362)
- `isValidArt()` validation (server.js:411-417)
- SoundCloud client_id scraper with 6h TTL (server.js:488-518)
- `httpsGet()` with redirect-follow + 5s timeout (server.js:379-393)
- `isUrlAccessible()` HEAD request check with 4s timeout (server.js:395-408)

**HttpClient strategy (IMPORTANT):**
- Node uses `https.get()` (one-request-at-a-time per call site; no pooling)
- ASP.NET port must use `IHttpClientFactory` with named clients
- `rejectUnauthorized: false` in Node → `HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }` in C# (required for some CDN endpoints that have cert chain issues)
- MusicBrainz requires `User-Agent: MastersFM/1.7` per their rate-limit policy
- Bing requires a browser User-Agent string

**Parallel art+duration resolution:**
```csharp
var (art, _) = await (
    resolveArtwork(artist, track, webhookArt, originUrl, source),
    duration > 0 ? Task.CompletedTask : resolveDuration(artist, track)
).WhenAll();
```

**Validation criteria:**
- Art lookup for a known SoundCloud track returns a SoundCloud CDN URL
- Art lookup for a known Spotify/iTunes track returns a Deezer or iTunes CDN URL
- For placeholder artist ("?????????") → enrichByTitle called
- Duration lookup for known track → duration field populated within 10s of new-track webhook

---

### Sub-stage 4.10 — Discord RPC port (Lachee.DiscordRPC)

**Estimated hours:** 10-12h

This sub-stage ports `discord_rpc.js` entirely to C# using `Lachee.DiscordRPC` NuGet (1.1+, targets .NET 6/8).

#### Discord RPC Analysis (from discord_rpc.js full read)

**Discord application ID:** `1495411843836018819` (hardcoded; overrideable via `config.discord_rpc.client_id`)

**Activity payload fields:**
```json
{
  "name": "Master's FM",
  "details": "<track title, max 128 chars>",
  "state": "by <artist>",
  "timestamps": { "start": <epoch_sec>, "end": <epoch_sec> },
  "assets": {
    "large_image": "<https art URL or 'mastersfm_logo'>",
    "large_text": "<source name>",
    "small_image": "mastersfm_logo",
    "small_text": "⏸ Paused | Master's FM"
  },
  "buttons": [{ "label": "Listen on <source>", "url": "<https origin URL>" }],
  "type": 2
}
```

**Rate-limit handling:**
- `MIN_SEND_INTERVAL_MS = 2000` — minimum gap between SET_ACTIVITY writes
- Latest-wins coalescer: rapid track changes collapse to ONE write per 2s window
- On Discord ERROR with `rate/too many/4006` text: back off extra 10s (`rateLimitedUntil`)
- On reconnect: reset `lastSentAt = 0` and `rateLimitedUntil = 0` for immediate post-READY push

**IPC mechanism:** Named pipes `\\.\pipe\discord-ipc-0` through `\\.\pipe\discord-ipc-9` (walk until connected; handles Discord Stable/Canary/PTB coexistence)

**Frame format:** 8-byte header `[opcode:uint32LE][length:uint32LE]` + UTF-8 JSON payload

**Opcodes:** HANDSHAKE=0, FRAME=1, CLOSE=2, PING=3, PONG=4

**Initialization/handshake:**
1. Try pipes 0-9 until connected
2. Send HANDSHAKE frame: `{ v: 1, client_id: "<id>" }`
3. Wait for DISPATCH/READY response
4. On READY: flush pendingState, fire onReadyFn

**Reconnection:** 10s retry when pipe not found, 5s retry on socket close

**Dedup logic in server.js:**
- Signature = artist|track|source|startedAt|duration|isPaused|art|originUrl
- Skip if same signature AND age < 30s
- After 30s, push anyway (self-heal in case Discord missed a frame)
- On new track: always reset signature to force immediate push

#### Lachee.DiscordRPC Mapping

| discord_rpc.js behavior | Lachee.DiscordRPC equivalent |
|---|---|
| `discord.init(clientId)` | `new DiscordRpcClient(clientId)` + `client.Initialize()` |
| `discord.update(state)` | `client.SetPresence(new RichPresence { ... })` |
| `discord.clear()` | `client.ClearPresence()` |
| `discord.destroy()` | `client.Dispose()` |
| Pipe walk 0-9 | Lachee handles automatically via `INamedPipeClient` |
| READY callback | `client.OnReady += handler` |
| ERROR callback | `client.OnError += handler` |
| Throttle/coalescer | Manual: `System.Threading.Timer` + latest-wins queue |
| 30s dedup age refresh | Same dedup logic in C# |
| `type: 2` (Listening) | `RichPresence { Type = ActivityType.Listening }` (Lachee 1.1+) |
| `assets.large_image` = HTTPS URL | `Assets.LargeImageKey = httpsUrl` (Discord accepts URLs since 2022) |
| `buttons[0]` | `Buttons = new[] { new Button { Label="...", Url="..." } }` |
| `timestamps.start/end` | `Timestamps = new Timestamps { Start = DateTimeOffset.FromUnixTimeSeconds(startSec), End = ... }` |

**Notes on Lachee.DiscordRPC:**
- Version 1.1.1 targets .NET 6; verify .NET 8 compatibility (should be fine — no TFM changes)
- `ActivityType.Listening` is supported since Lachee 1.1.0
- Rate-limit handling: Lachee does NOT implement rate-limiting — we must replicate the 2000ms throttle ourselves (same as discord_rpc.js)
- Named pipe walk: Lachee's default `ManagedNamedPipeClient` walks pipes 0-9 automatically

**Critical behavior to preserve:**
- On `onReadyFn()` callback: reset dedup signature `_lastDiscordSig = ""` + call `pushDiscord()` immediately — ensures Discord shows current track after reconnect
- `discord.init()` is idempotent (same clientId = reconnect; different clientId = destroy + reconnect)
- During art resolution (trackArt = ""): push immediately with `mastersfm_logo` placeholder, then re-push with real art — ensures Discord shows something within milliseconds of track change

**Validation criteria:**
- Discord shows current track within 2s of new track webhook
- Rapid track skips → only ONE SET_ACTIVITY per 2s window (coalescing works)
- Paused state → no timestamps in Discord activity
- Art URL → shows as large_image thumbnail (requires HTTPS URL)
- Track URL → "Listen on SoundCloud" button appears

---

### Sub-stage 4.11 — Side-by-side validation + 24h soak

**Estimated hours:** 8-10h

**Validation methodology:**
1. Replay the Phase 1 baseline corpus (all `V14_S4_P1_BASELINE_CAPTURES/*.json`) against the .NET server
2. Diff every JSON response field-by-field
3. Document any intentional differences (e.g. `Date` header format, `Server` header added by Kestrel)
4. Run 24h soak: all processes running, music playing, Discord RPC connected

**Diff harness (to be written in Phase 2):**
```powershell
# For each captured baseline:
$baseline = Get-Content "V14_S4_P1_BASELINE_CAPTURES/get_current.json" | ConvertFrom-Json
$live = Invoke-WebRequest -Uri "http://127.0.0.1:4242/current" | ConvertFrom-Json
# Compare non-volatile fields (exclude startedAt, _setAt, pausedAt which change)
```

**Pass criteria:**
- All JSON body fields match OR difference is documented in intentional-difference table
- SSE heartbeat arrives within 15.5s of last heartbeat (±500ms tolerance)
- tray.ps1 POST /webhook returns 200 on every tick
- overlay.html SSE stays connected across 24h
- customize.html saves/loads presets correctly
- Discord RPC shows correct activity, no rate-limit errors in server.log

---

## Section 2 — Estimated Hours Per Sub-Stage

| Sub-stage | Endpoints / Scope | Hours |
|---|---|---|
| 4.1 | Skeleton project + port binding | 6-8 |
| 4.2 | Read-only endpoints (8 routes) | 6-8 |
| 4.3 | SSE endpoint + heartbeat | 10-12 |
| 4.4 | Webhook handler (pause/seek/drift) | 12-15 |
| 4.5 | Config endpoints | 8-10 |
| 4.6 | Preset management | 4-5 |
| 4.7 | Art proxy + client-log | 4-6 |
| 4.8 | Screenshot endpoint | 4-5 |
| 4.9 | Art cascade + duration resolver | 18-22 |
| 4.10 | Discord RPC (Lachee) | 10-12 |
| 4.11 | Validation + soak | 8-10 |
| **Total** | | **90-113h** |

*Realistic estimate: 100h. The high end of the V14_NET8_MIGRATION_PLAN.md range (60-100h). Sub-stage 4.9 (art cascade) is the primary uncertainty — Bing and SoundCloud scraping are inherently fragile and may need retry logic not in the original Node code.*

---

## Section 3 — Risks Specific to Stage 4

### R1 — HTTP Header Differences (HIGH)

ASP.NET Core's Kestrel adds default headers that server.js does not emit:
- `Server: Kestrel` — present by default in Kestrel; must suppress: `builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false)`
- `Transfer-Encoding: chunked` — Kestrel default for streaming; already present in Node, match is fine
- `Date` header format: Kestrel emits RFC 7231 format (same as Node/http), should match

**Action:** Suppress `Server` header. Document any remaining header differences in the intentional-difference table.

### R2 — JSON Serialization Case-Sensitivity (HIGH)

The current `currentTrack` JSON uses camelCase field names (`trackArt`, `startedAt`, `_setAt`, etc.). overlay.html and tray.ps1 read these by name. `System.Text.Json` defaults to preserving .NET property casing (PascalCase) unless `PropertyNamingPolicy.CamelCase` is set.

**Action:** `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }` — but wait: `_setAt` and `durationGaveUp` are fields, not properties. The C# model must either use `[JsonPropertyName("_setAt")]` attributes or use a Dictionary-based representation.

**Recommendation:** Use `JsonObject` / `Dictionary<string, JsonElement>` for `currentTrack` state to avoid field name translation complexity, OR define a C# record/class with `[JsonPropertyName]` attributes on every field.

### R3 — SSE Concurrent Write Safety (HIGH)

Node.js is single-threaded — no concurrent SSE write contention possible. ASP.NET Core handlers execute on thread pool threads; multiple webhooks could arrive simultaneously, each calling `sseBroadcast()`. Under concurrency, two threads writing to the same HTTP response stream simultaneously would corrupt the SSE frame.

**Action:** Per-client write queue (`Channel<string>`) drained by a dedicated async loop in the SSE handler. Broadcasts enqueue to all clients' channels; each client drains its own queue to its response stream.

### R4 — SoundCloud client_id Scraper Fragility (MEDIUM)

The SoundCloud homepage scrape is inherently fragile (HTML structure changes). The 6h TTL means one bad scrape blocks art lookup for 6 hours.

**Action:** Port exactly as-is. Add a `server.log` warning when scrape fails. The osu!, YouTube, and Bing fallbacks still work. Document as a known-fragile dependency.

### R5 — Rate-Limit Handling in Discord RPC (MEDIUM)

Lachee.DiscordRPC does not implement rate-limiting. The 2000ms throttle must be implemented in C# on top of Lachee's API.

**Action:** `System.Threading.Timer` + `SemaphoreSlim` to enforce minimum 2000ms between `SetPresence` calls. Latest-wins coalescing via `Interlocked.Exchange` on a pending-state reference.

### R6 — Unicode in Track/Artist Names (LOW but NOTABLE)

Live capture shows artist names with U+200E (LEFT-TO-RIGHT MARK) from SMTC metadata. The art lookup must preserve these characters (pass them through to HTTP requests encoded). `HttpUtility.UrlEncode` / `Uri.EscapeDataString` in C# handle Unicode correctly.

**Action:** Use `Uri.EscapeDataString()` for all query string encoding in the art cascade.

### R7 — `_setAt` Internal Field Exposure (LOW)

The `currentTrack` object includes `_setAt` (underscore-prefixed internal timestamp). overlay.html's first-heartbeat correction logic reads this field. Phase 2 must expose it in the JSON response.

**Action:** Include `_setAt` in the C# model with `[JsonPropertyName("_setAt")]`.

### R8 — ASP.NET Core Response Buffering for SSE (HIGH)

Kestrel may buffer responses before writing to the socket. For SSE, each `\n\n` delimited frame must be flushed immediately.

**Action:** After each `await Response.Body.WriteAsync(...)`, call `await Response.Body.FlushAsync()`. Alternatively use `IHttpResponseBodyFeature.DisableBuffering()` at handler start.

---

## Section 4 — Discord RPC Port Plan Summary

See Section 1 Sub-stage 4.10 for full detail. Summary:

- **NuGet:** `Lachee.DiscordRPC` 1.1.1 (or latest compatible with .NET 8)
- **Client ID:** `1495411843836018819` (constant), overrideable via `config.discord_rpc.client_id`
- **Throttle:** 2000ms minimum interval; latest-wins coalescer (C# `Timer` + `Interlocked.Exchange`)
- **Dedup:** Signature = `artist|track|source|startedAt|duration|isPaused|art|originUrl`; skip if same sig AND age < 30s
- **Activity type:** `ActivityType.Listening` (type: 2)
- **Art:** HTTPS URLs sent as `large_image` key directly; data: URIs filtered out (`mastersfm_logo` fallback)
- **Config reload:** `POST /reload-config` triggers re-init if client_id or enabled flag changed
- **Lifecycle:** Same process as the ASP.NET server (not a separate process); `DiscordRpcClient` instance created at server start, disposed at server stop

---

## Section 5 — Open Questions (could not answer from Phase 1 captures)

1. **`update.html` behavior:** The page content was captured (7249 bytes) but not analyzed. Does it use EventSource or polling for `/update-status`? This affects whether Phase 2 needs SSE on `/update-status` or plain polling. Need to read `update.html` before implementing sub-stage 4.2.

2. **`config_default.json` schema:** The `deepMergeConfig` behavior depends on which fields exist in `config_default.json`. The `liveAudioVisualizer` field is injected by GET /overlay-config from the top-level config. Does it also exist in `config_default.json`? Need to read the file before implementing sub-stage 4.5.

3. **SoundCloud client_id scrape under Kestrel IP:** The SoundCloud homepage may behave differently based on IP geolocation/rate-limiting. The test environment had no issues, but the scrape is known-fragile. No action needed before Phase 2 starts, but note this for monitoring.

4. **Bing image search current behavior:** Bing frequently changes its HTML structure. The capture environment (2026-05-04) had `"murl":"..."` pattern working. Cannot guarantee this still works in Phase 2; treat as best-effort.

5. **MusicBrainz rate-limit:** MusicBrainz requires `User-Agent` and enforces 1 request/second. The Node code does not throttle. Phase 2 should add per-hostname rate-limiting to the HttpClient calls (or accept occasional 429 responses).

6. **tray.ps1 `Send-WebhookAsync` concurrency:** tray.ps1 uses `HttpClient.PostAsync` fire-and-forget. Multiple ticks may arrive in rapid succession (e.g. track change + heartbeat within 100ms). The webhook handler must handle concurrent POST /webhook calls safely.

---

## Section 6 — Rollback Plan

```powershell
# In _full_rebuild.ps1:
$UseDotnet8Server = $false  # use original Node pkg server.exe
$UseDotnet8Server = $true   # use ASP.NET Core server.exe
```

Both produce a binary named `server.exe` in the install directory. The flag controls which build pipeline runs. Both are code-signed with CN=MasterShadex. The rollback path is available until sub-stage 4.11 (side-by-side validation) passes.

---

## Section 7 — Recommended Phase 2 Brief Structure

Phase 2 should be a single brief covering all 11 sub-stages in order. Each sub-stage completes with:
1. Code written
2. Validation against baseline corpus run (`V14_S4_P1_BASELINE_CAPTURES/`)
3. Pass/fail documented in a Phase 2 run log

The Phase 2 brief should reference:
- `V14_S4_P1_ENDPOINT_INVENTORY.md` as the API contract
- `V14_S4_P1_SSE_CONTRACT.md` as the SSE contract
- `V14_S4_P1_BASELINE_CAPTURES/` as the diff corpus

**Recommended starting point for Phase 2:** Sub-stage 4.1 (skeleton project). The csproj pattern is identical to launcher.csproj and audio_spectrum.csproj from Stages 1-3 — the team already has this template.

---

*Phase 1 complete. No v12.4.0 code written. No source modifications. Ready for Phase 2 brief.*
