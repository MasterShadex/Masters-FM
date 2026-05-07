# V12.3.0 server.exe SSE Contract

**Endpoint:** `GET /events` (server.js:1282-1294)
**Captured against:** server.exe PID=14112, port=4242, v12.3.0
**Analysis date:** 2026-05-04
**Live capture file:** `V14_S4_P1_BASELINE_CAPTURES/sse_events_30s.txt`

---

## SSE Endpoint: GET /events

### Connect Handshake

**Request:**
```
GET /events HTTP/1.1
Host: 127.0.0.1:4242
```

**Response headers (empirically captured):**
```
Content-Type: text/event-stream
Cache-Control: no-cache, no-transform
Connection: keep-alive
X-Accel-Buffering: no
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, POST, OPTIONS
Access-Control-Allow-Headers: Content-Type
Transfer-Encoding: chunked
Date: <server date>
```

**Immediately on connect:** server writes the current track state frame before client sends any data:
```
data: <JSON of currentTrack or null>\n\n
```
This ensures a freshly-connected overlay (or OBS Browser Source that just started) gets an immediate track display rather than waiting for the next webhook event.

---

## Event Types

All events are sent over the single `/events` endpoint. The stream multiplexes four distinct event types:

### 1. Track State Update (default event)

**Trigger:** `sseBroadcast()` called from:
- `handleWebhook()` — on every POST /webhook arrival
- `setTrack()` after artwork is resolved
- Art retry completion in same-track webhook handler

**Wire format:**
```
data: {"artist":"...","track":"...","trackArt":"...","duration":337648,"originUrl":"","startedAt":1777883148330,"source":"SoundCloud","isPaused":false,"pausedAt":0,"_setAt":1777883148114}\n
\n
```

**Notes:**
- No `event:` line — consumers handle with `eventSource.onmessage` or `eventSource.addEventListener('message', ...)`
- Always a single `data:` line (no multi-line data)
- JSON is `JSON.stringify(currentTrack)` — compact, no whitespace
- Value is `null` when no track is playing: `data: null\n\n`
- Two broadcasts per new track are common: first with `trackArt: ""` (immediate), second with resolved art URL (after 2-15s depending on lookup cascade)

### 2. Preview Config (named event)

**Trigger:** POST /preview-config handler

**Wire format:**
```
event: preview-config\n
data: {"fontFamily":"Inter","fontSize":16,...}\n
\n
```

**Notes:**
- Fired on EVERY control change in customize.html (slider drag, color pick, toggle)
- Carries the FULL overlay config object (not a delta)
- Overlay in preview mode (`?preview=1`) applies this immediately for real-time preview

### 3. Overlay Config (named event)

**Trigger:** POST /save-overlay-config handler (the "Apply" button)

**Wire format:**
```
event: overlay-config\n
data: {"fontFamily":"Inter","fontSize":16,...}\n
\n
```

**Notes:**
- Fired once when user clicks "Apply" in customize
- Carries the saved overlay config (what was just written to config.json)
- Both the OBS overlay AND the customize preview iframe receive this
- After this event, GET /overlay-config returns the same value
- This is the authoritative "saved config" notification — clients should persist this to their active config

### 4. Capture Request (named event)

**Trigger:** GET /screenshot endpoint

**Wire format:**
```
event: capture\n
data: {"requestId":"1"}\n
\n
```

**Notes:**
- `requestId` is a monotonically-incrementing string counter (starts at "1" per server run)
- overlay.html receives this, renders its canvas to PNG, POSTs to `/screenshot-response` with the PNG data URI

### 5. Heartbeat (SSE comment)

**Trigger:** `setInterval(() => { res.write(': ping\n\n') }, 15000)` (server.js:253-258)

**Wire format:**
```
: ping\n
\n
```

**Notes:**
- SSE comment lines (starting with `:`) are ignored by EventSource clients per SSE spec
- Purpose: keep TCP connection alive through proxies, load balancers, and OBS CEF which may close idle connections
- Frequency: exactly 15,000 ms (15 seconds)
- Only sent when `sseClients.size > 0` (no wasted writes when no overlay connected)

---

## Reconnection Semantics

- **No replay:** Reconnecting clients get only the current track state (immediate first-frame write on connect). There is no event history or replay buffer.
- **Immediate state delivery:** The `data: <currentTrack JSON>\n\n` write happens synchronously on connect, before the next webhook event.
- **Client-side reconnection:** Browser EventSource auto-reconnects with exponential backoff. OBS CEF does the same.
- **Server-side cleanup:** `req.on('close', () => sseClients.delete(res))` — the server removes the client from the broadcast set when the connection closes.
- **No Last-Event-ID:** Server does not send `id:` lines, does not implement the `Last-Event-ID` header replay mechanism.

---

## Connection State Management

```javascript
const sseClients = new Set();  // module-level, shared state

// On connect:
sseClients.add(res);

// On disconnect:
req.on('close', () => sseClients.delete(res));

// On broadcast write error:
try { res.write(payload); } catch { sseClients.delete(res); }
```

- The server detects dead connections on write (exception caught) and removes them
- No explicit ping-pong health check — relying on the 15s heartbeat to keep connections alive and the write-error path to clean up

---

## SSE Frame Examples (from live capture)

### On connect (immediate state delivery)
```
data: {"artist":"Dustvoxx","track":"Dustvoxx, Laur - FireLight (Neokontrol Remix) ***OUT SOON***","trackArt":"https://i1.sndcdn.com/artworks-000337243101-5gzm97-t500x500.jpg","duration":337648,"originUrl":"","startedAt":1777883148330,"source":"SoundCloud","isPaused":false,"pausedAt":0,"_setAt":1777883148114}

```

### Heartbeat (every 15s)
```
: ping

```

### Track update (same track, heartbeat from tray)
```
data: {"artist":"Dustvoxx","track":"Dustvoxx, Laur - FireLight (Neokontrol Remix) ***OUT SOON***","trackArt":"https://i1.sndcdn.com/artworks-000337243101-5gzm97-t500x500.jpg","duration":337648,"originUrl":"","startedAt":1777883148330,"source":"SoundCloud","isPaused":false,"pausedAt":0,"_setAt":1777883148114}

```

### Preview config update (from customize.html control change)
```
event: preview-config
data: {"fontFamily":"Inter","fontSize":16,"showArtist":true,"showAlbumArt":true}

```

### Overlay config saved (from customize.html Apply)
```
event: overlay-config
data: {"fontFamily":"Inter","fontSize":16,...}

```

### Screenshot capture request
```
event: capture
data: {"requestId":"1"}

```

---

## Status Log (server.js:261-263)

Every 60 seconds the server logs:
```
[STATUS] uptime=<seconds>s sseClients=<count>
```
This is to `server.log` only (not an SSE event).

---

## Phase 2 Implementation Notes

### Headers to replicate exactly
The following response headers must be set on the `/events` response in ASP.NET Core:
```csharp
Response.Headers.Add("Content-Type", "text/event-stream");
Response.Headers.Add("Cache-Control", "no-cache, no-transform");
Response.Headers.Add("Connection", "keep-alive");
Response.Headers.Add("X-Accel-Buffering", "no");
// (CORS headers already on all routes)
```

ASP.NET Core's `IServerSentEventsWriter` or manual `Response.Body.WriteAsync()` both work. Key: must disable response buffering (`IHttpResponseBodyFeature.DisableBuffering()`).

### Keep-alive timing
- 15-second heartbeat must be replicated. ASP.NET Core's default idle timeout may interfere; set `SseHeartbeatInterval = TimeSpan.FromSeconds(15)`.
- OBS CEF is known to drop connections silently without a heartbeat.

### Immediate state delivery
On every new SSE connection, write `data: <JSON.Serialize(currentTrack)>\n\n` BEFORE yielding to the request pipeline. This is a behavioral contract that overlay.html relies on to display state immediately.

### Event multiplexing
All four event types share one endpoint. The ASP.NET port must handle concurrent writes from multiple call sites (webhook handler, screenshot handler, preview-config handler, save-overlay-config handler, heartbeat timer) safely under concurrency. Consider `ConcurrentQueue<SseFrame>` drained by a dedicated per-connection write loop.

### JSON serialization
- `JSON.stringify(currentTrack)` in Node.js produces compact JSON (no whitespace)
- ASP.NET's `JsonSerializer.Serialize()` defaults to compact as well — match this
- Field names are **camelCase** (JavaScript convention) — `System.Text.Json` with `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }` is the correct setting
- `null` is serialized as the JSON literal `null`, not omitted — `data: null\n\n` when no track

### Two-broadcast pattern for new tracks
When a new track arrives: server broadcasts `trackArt: ""` immediately, then re-broadcasts with resolved art URL after async lookup completes (2-15 seconds). Phase 2 must replicate this sequence. Overlay.html handles `trackArt: ""` gracefully (shows music-note fallback icon).

---

## Open Questions for Phase 2

1. **ASP.NET Core response buffering:** IIS/Kestrel default response buffering may buffer the chunked transfer. Must verify `DisableBuffering()` call actually prevents buffering in all deployment scenarios.
2. **Concurrent writes thread safety:** `sseClients` in Node is single-threaded (event loop). ASP.NET Core is multi-threaded — need a thread-safe write queue per client or a `SemaphoreSlim` to serialize writes.
3. **OBS CEF EventSource behavior:** OBS Browser Source uses Chromium's EventSource which follows the W3C SSE spec. The 15s heartbeat interval was chosen empirically. Verify it's still sufficient with the ASP.NET Core host.

---

*See also: `V14_S4_P1_BASELINE_CAPTURES/sse_events_30s.txt` for raw timestamped capture*
