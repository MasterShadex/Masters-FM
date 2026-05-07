# V12.3.0 server.exe Endpoint Inventory

**Source:** `G:\Project Folder\Master FM\src\server.js` (1,610 LOC)
**Captured against:** server.exe PID=14112, port=4242, v12.3.0 (bootId=1777880701695)
**Analysis date:** 2026-05-04
**Method:** Full source read (lines 1083-1579) + empirical HTTP captures

---

## Route Summary Table

| Method | Path | Description | Consumers |
|--------|------|-------------|-----------|
| POST | `/webhook` | Primary track-state receiver from tray.ps1 | tray.ps1 |
| POST | `/client-log` | Relay overlay/customize log messages to server.log | overlay.html, customize.html |
| POST | `/reload-config` | Re-read config.json + hot-reload Discord RPC settings | tray.ps1 |
| GET | `/overlay-config` | Read overlay config section from config.json | overlay.html (polling), customize.html |
| GET | `/art` | Proxy current track art (handles data: URI + HTTPS CDN) | overlay.html |
| GET | `/current` | Return current track state JSON | overlay.html (poll fallback), tray.ps1 (tooltip) |
| GET | `/version` | Return server boot ID for overlay auto-reload detection | overlay.html, customize.html |
| GET | `/screenshot` | Trigger screenshot round-trip via SSE capture event | diagnostic/external |
| POST | `/screenshot-response` | Overlay posts back PNG after receiving capture SSE event | overlay.html |
| GET | `/events` | SSE stream - real-time track updates + config changes | overlay.html, customize.html (via preview iframe) |
| GET | `/MastersFM.ico` | Serve app icon | browser, customize.html |
| GET | `/favicon.ico` | Serve app icon (alias) | browser |
| GET | `/manifest.json` | PWA web app manifest | customize.html (`<link rel="manifest">`) |
| GET | `/update-status` | Update progress JSON (reads tmp file) | update.html |
| GET | `/update` | Serve update.html progress page | tray.ps1 (Start-Process URL) |
| GET | `/` | Serve overlay.html | OBS Browser Source |
| GET | `/?*` | Serve overlay.html (accepts any query params) | OBS Browser Source |
| GET | `/preview-config` | In-memory preview config or falls back to saved | overlay.html (preview mode) |
| POST | `/preview-config` | Set in-memory preview config + SSE broadcast `event: preview-config` | customize.html |
| GET | `/list-presets` | Return array of preset names from presets dir | customize.html |
| POST | `/save-preset` | Save named preset JSON to disk | customize.html |
| GET | `/load-preset` | Load preset by name (`?name=`) | customize.html |
| DELETE | `/delete-preset` | Delete preset by name (`?name=`) | customize.html |
| POST | `/save-overlay-config` | Write overlay config to disk + SSE broadcast `event: overlay-config` + clear previewConfig | customize.html |
| GET | `/customize` | Serve customize.html | tray.ps1 (Start-Process), customize.exe |
| * | any known path, wrong method | 405 Method Not Allowed + Allow header | - |
| * | unknown path | 404 Not Found | - |
| OPTIONS | any | CORS preflight - 204 No Content | browsers |

---

## Global Response Headers (all routes)

```
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, POST, OPTIONS
Access-Control-Allow-Headers: Content-Type
```

Server binds to `127.0.0.1:4242` (loopback only).

---

## Per-Route Detail

---

### OPTIONS * (CORS preflight)

- **File:line:** server.js:1088
- **Handler:** `if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return; }`
- **Request:** any path, method OPTIONS
- **Response 204:** empty body, CORS headers
- **Consumers:** browsers making cross-origin requests
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/options_preflight.json`

---

### POST /webhook

- **File:line:** server.js:1090-1099
- **Request:**
  - Content-Type: any (raw body read as text, then JSON.parse'd)
  - Body JSON fields:
    - `artist` (string) — track artist
    - `track` (string) — track title
    - `duration` (number, seconds float) — converted to ms internally (`Math.round(data.duration * 1000)`)
    - `positionMs` (number, ms) — current playback position
    - `trackArt` (string, optional) — art URL or data:image/ URI from SMTC
    - `originUrl` (string, optional) — deep-link URL to track page
    - `source` (string, optional) — platform name ("Spotify", "Chrome", "osu!", etc.); defaults to "Webhook"
    - `isPaused` (boolean) — pause state
    - `seek` (boolean, optional) — explicit seek event flag
- **Response 200:** `OK` (text/plain)
- **Response 400:** `Bad Request` (JSON parse failed)
- **Side effects:**
  - Calls `handleWebhook(body)` which: resolves artwork (async cascade), resolves duration, updates `currentTrack`, calls `sseBroadcast()` (which also calls `pushDiscord()`)
  - On new track: fires `setTrack()` which runs artwork + duration resolution in parallel; SSE broadcast on completion
  - On same track: updates pause/resume/seek/duration state; SSE broadcast
- **Consumers:** tray.ps1 (every ~2s heartbeat + track-change events)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/post_webhook_newtrack.json`, `post_webhook_heartbeat.json`

---

### POST /client-log

- **File:line:** server.js:1102-1113
- **Request:**
  - Content-Type: application/json (expected)
  - Body: `{ "level": "INFO"|"WARN"|"ERROR", "msg": "..." }`
- **Response 204:** empty body (fire-and-forget pattern)
- **Side effects:** appends `[OVERLAY/LEVEL] msg` to server.log via `flog()`
- **Error handling:** falls back to `[OVERLAY/RAW] <raw body>` if JSON parse fails; still 204
- **Consumers:** overlay.html, customize.html (diagnostic logging relay)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/post_client-log.json`

---

### POST /reload-config

- **File:line:** server.js:1115-1141
- **Request:** no body required
- **Response 200:** `OK`
- **Response 500:** `Config read error: <message>`
- **Side effects:**
  - Reads `%APPDATA%\MastersFM\config.json`
  - Updates `DISCORD_RPC_ENABLED` and `DISCORD_RPC_CLIENTID` from `cfg.discord_rpc`
  - If Discord settings changed: calls `discord.init()` or `discord.destroy()` accordingly
  - Logs "Config reloaded"
- **Consumers:** tray.ps1 (after Discord RPC config save in Settings pane, line 4479)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/post_reload-config.json`

---

### GET /overlay-config

- **File:line:** server.js:1143-1166
- **Request:** no params
- **Response 200:** `application/json` — contents of `cfg.overlay` plus `liveAudioVisualizer` top-level flag
  - Shape: `{ ...overlayConfig, liveAudioVisualizer?: boolean }`
  - Returns `{}` on any read/parse error (never errors to client)
- **Notes:**
  - Re-reads config.json on EVERY request (hot-reload for live editing)
  - `liveAudioVisualizer` is injected from top-level config into overlay config shape
- **Consumers:** overlay.html (polls every 5s), customize.html (one-shot on load)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_overlay-config.json`

---

### GET /art

- **File:line:** server.js:1169-1190
- **Request:**
  - Query: `?t=<timestamp>` (cache-bust; ignored by server, accepted without error)
  - No body
- **Response 200:** image data (MIME from source URL or `image/jpeg`)
  - For data: URIs: decodes base64, returns raw bytes with detected MIME
  - For HTTPS URLs: proxies via Node `fetch()`, streams as-is
  - Headers: `Cache-Control: no-store`, `Access-Control-Allow-Origin: *`
- **Response 404:** empty — no current track or no art
- **Response 500:** empty — base64 decode error
- **Response 502:** empty — upstream CDN fetch error
- **Notes:**
  - Always serves CURRENT track's art (no param to choose)
  - Proxies CDN art through localhost to avoid CORS issues in OBS CEF
  - data: URIs decoded and served as binary (overlay uses this for SMTC thumbnails)
- **Consumers:** overlay.html (`/art?t=<now>` for palette extraction + main image)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_art.json`

---

### GET /current

- **File:line:** server.js:1192-1196
- **Request:** no params
- **Response 200:** `application/json` — `currentTrack` object or `null`
  - Shape when playing:
    ```json
    {
      "artist": "string",
      "track": "string",
      "trackArt": "string (URL or data:image/ or '')",
      "duration": "number (ms, 0 if unknown)",
      "originUrl": "string (URL or '')",
      "startedAt": "number (epoch ms)",
      "source": "string (platform name)",
      "isPaused": "boolean",
      "pausedAt": "number (epoch ms, 0 if not paused)",
      "_setAt": "number (epoch ms, internal timestamp)",
      "durationGaveUp": "boolean (optional, true if all duration resolvers failed)"
    }
    ```
  - `null` when no track playing
- **Notes:**
  - `_setAt` is an internal field exposed to clients; Phase 2 must match it
  - `durationGaveUp` only present when set
- **Consumers:** overlay.html (poll fallback, transition refresh), tray.ps1 (tooltip poll every 2s, line 5224)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_current.json`

---

### GET /version

- **File:line:** server.js:1201-1205
- **Request:** no params
- **Response 200:** `application/json`, `Cache-Control: no-store`
  - Body: `{ "bootId": <number> }` — `Date.now()` at server start, mutable via `let`
- **Notes:**
  - `SERVER_BOOT_ID` is declared as `let` (not `const`) because a now-removed `/obs-reset-position` endpoint bumped it. The let-binding is preserved even though nothing bumps it currently.
  - Overlay polls every 3s; reloads if bootId changes (server restart detection)
  - customize.html polls for same reason
- **Consumers:** overlay.html, customize.html
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_version.json`

---

### GET /screenshot

- **File:line:** server.js:1209-1239
- **Request:** no params
- **Response 200:** `image/png` binary data, `Cache-Control: no-cache`
- **Response 503:** `No overlay connected (sseClients.size === 0)` — text/plain
- **Response 504:** `Overlay did not respond within 2500 ms` — text/plain, 2.5s timeout
- **Mechanism:**
  1. Server stores pending `{ requestId, resolve, fail }` slot
  2. Broadcasts SSE `event: capture\ndata: {"requestId":"<N>"}\n\n` to all SSE clients
  3. overlay.html receives the capture event, renders canvas to PNG data URI, POSTs to `/screenshot-response`
  4. Server's pending slot `.resolve(pngBuffer)` writes the image response
- **Notes:** Diagnostic tool added in v9.2.0. `_ssScreenshot` state lives at module scope (fresh per request).
- **Consumers:** diagnostic use (curl/scripts), not a regular UI flow
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_screenshot.json` (503 case — no overlay in test env)

---

### POST /screenshot-response

- **File:line:** server.js:1242-1278
- **Request:**
  - Content-Type: application/json
  - Body: `{ "requestId": "string", "png": "data:image/png;base64,..." }` on success
  - Body: `{ "requestId": "string", "error": "string" }` on failure
- **Response 200:** `ok` or `ok (forwarded error)` or `ok (forwarded bad-format error)`
- **Response 400:** `parse error: <message>` — malformed JSON
- **Response 409:** `No screenshot pending` or `requestId mismatch (stale response)`
- **Side effects:** resolves or rejects the pending GET /screenshot request
- **Consumers:** overlay.html (only called in response to SSE capture event)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/post_screenshot-response_nopending.json`

---

### GET /events (SSE)

- **File:line:** server.js:1282-1294
- **Request:** no params, `Accept: text/event-stream` implied by browser EventSource
- **Response 200:**
  - Headers: `Content-Type: text/event-stream`, `Cache-Control: no-cache, no-transform`, `Connection: keep-alive`, `X-Accel-Buffering: no`
  - Immediately writes: `data: <JSON of currentTrack>\n\n` (current state on connect)
  - Then streams events as they occur
- **Event types emitted on this stream:**
  1. **Default (no event: line)** — `data: <currentTrack JSON>\n\n` — broadcast by `sseBroadcast()` on every webhook arrival
  2. **`event: preview-config`** — `data: <overlayConfig JSON>\n\n` — broadcast by POST /preview-config
  3. **`event: overlay-config`** — `data: <overlayConfig JSON>\n\n` — broadcast by POST /save-overlay-config
  4. **`event: capture`** — `data: {"requestId":"<N>"}\n\n` — broadcast by GET /screenshot
  5. **Heartbeat** — `: ping\n\n` (comment, no event name) — every 15 seconds
- **Disconnect:** `req.on('close', () => sseClients.delete(res))` — removed from set on close
- **Reconnection:** no replay — reconnecting client gets current state immediately (first write on connect)
- **Consumers:** overlay.html (`new EventSource('/events')`)
- **Full capture:** `V14_S4_P1_BASELINE_CAPTURES/sse_events_60s.txt`
- See also: `V14_S4_P1_SSE_CONTRACT.md` for full analysis

---

### GET /MastersFM.ico and GET /favicon.ico

- **File:line:** server.js:1297-1305
- **Request:** no params
- **Response 200:** `image/x-icon`, `Cache-Control: public,max-age=86400` — binary file read from CWD
- **Response 404:** `icon not found`
- **Consumers:** browser (tab favicon), customize.html window
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_favicon.json`

---

### GET /manifest.json

- **File:line:** server.js:1307-1321
- **Request:** no params
- **Response 200:** `application/manifest+json`
  - Body (hardcoded, not from file):
    ```json
    {
      "name": "Master's FM",
      "short_name": "Master's FM",
      "description": "Now Playing Overlay for OBS",
      "start_url": "/customize",
      "display": "standalone",
      "background_color": "#0d0420",
      "theme_color": "#6d28d9",
      "icons": [{ "src": "/MastersFM.ico", "sizes": "256x256", "type": "image/x-icon" }]
    }
    ```
- **Consumers:** customize.html (`<link rel="manifest" href="/manifest.json">`)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_manifest.json`

---

### GET /update-status

- **File:line:** server.js:1324-1334
- **Request:** no params
- **Response 200:** `application/json`, `Cache-Control: no-store`
  - Body: contents of `%TEMP%\mastersfm_update_status.json` (written by tray.ps1 during update)
  - Default when file missing: `{"state":"idle","version":null,"progress":0,"bytesDown":0,"bytesTotal":0,"current":null,"ts":0}`
- **Notes:**
  - tray.ps1 writes status updates to the temp file; update.html polls this endpoint
  - States: "idle", "checking", "downloading", "verifying", "installing", "done", "error"
- **Consumers:** update.html (JavaScript polling)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_update-status.json`

---

### GET /update

- **File:line:** server.js:1336-1346
- **Request:** no params
- **Response 200:** `text/html` — contents of `update.html` from CWD
- **Response 404:** `update.html not found`
- **Consumers:** tray.ps1 (`Start-Process "http://localhost:4242/update"` when update starts)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_update_page.json`

---

### GET / and GET /?*

- **File:line:** server.js:1348-1359
- **Request:** path `/` or `/?<anything>` (query params accepted but ignored by server; overlay.html reads them client-side)
  - Known query param: `?renderer=webgl` (OBS Browser Source URL)
- **Response 200:** `text/html` — contents of `overlay.html` from CWD
- **Response 500:** `overlay.html not found`
- **Consumers:** OBS Browser Source (`http://localhost:4242/?renderer=webgl`)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_root.json`

---

### GET /preview-config

- **File:line:** server.js:1362-1379
- **Request:** no params
- **Response 200:** `application/json`
  - If `previewConfig !== null` (live preview mode): returns in-memory preview object
  - Otherwise: reads `cfg.overlay` from config.json and returns it
  - Returns `{}` on any error
- **Notes:**
  - `previewConfig` is set by POST /preview-config, cleared by POST /save-overlay-config
  - overlay.html uses this when loaded with `?preview=1` (inside customize.html iframe)
- **Consumers:** overlay.html (preview mode, `_CFG_URL = '/preview-config'`), anim-demo path
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_preview-config.json`

---

### POST /preview-config

- **File:line:** server.js:1386-1404
- **Request:**
  - Content-Type: application/json
  - Body: overlay config object (arbitrary JSON object)
- **Response 200:** `OK`
- **Response 400:** `Bad JSON`
- **Side effects:**
  - Sets `previewConfig = JSON.parse(body)` (module-level state)
  - If SSE clients connected: broadcasts `event: preview-config\ndata: <config JSON>\n\n`
- **Notes:**
  - Posted on every control-change event (slider drag, color pick, toggle)
  - Does NOT write to disk — use /save-overlay-config to persist
- **Consumers:** customize.html (every live control change)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/post_preview-config.json`

---

### GET /list-presets

- **File:line:** server.js:1407-1418
- **Request:** no params
- **Response 200:** `application/json` — sorted array of preset name strings (without .json extension)
  - Returns `[]` on any error (directory unreadable, etc.)
- **Notes:**
  - Reads `%APPDATA%\MastersFM\presets\` directory
  - Filters `.json` files, strips extension, sorts alphabetically (localeCompare)
- **Consumers:** customize.html (preset picker load)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_list-presets.json`

---

### POST /save-preset

- **File:line:** server.js:1421-1450
- **Request:**
  - Content-Type: application/json
  - Body: `{ "name": "string", "config": <overlay config object> }`
- **Response 200:** `application/json` — `{ "saved": "<sanitized name>" }`
- **Response 400:** `Bad JSON` (malformed body) | `Name required` | `Config required` | `Invalid name` (empty after sanitization)
- **Response 500:** `<error message>` (file write error)
- **Name sanitization:** `name.replace(/[^a-zA-Z0-9 _\-]/g, '').trim().slice(0, 64)`
- **Notes:**
  - Writes to `%APPDATA%\MastersFM\presets\<safe>.json`
  - `JSON.stringify(config, null, 2)` — pretty-printed
- **Consumers:** customize.html
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/post_save-preset.json`

---

### GET /load-preset

- **File:line:** server.js:1453-1463
- **Request:**
  - Query: `?name=<preset name>`
- **Response 200:** `application/json` — raw file contents (the config object)
- **Response 404:** `Not found`
- **Name sanitization:** `name.replace(/[^a-zA-Z0-9 _\-]/g, '').trim()`
- **Notes:** Returns raw file content (no re-parse + re-stringify)
- **Consumers:** customize.html
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_load-preset.json`

---

### DELETE /delete-preset

- **File:line:** server.js:1466-1475
- **Request:**
  - Method: DELETE
  - Query: `?name=<preset name>`
- **Response 200:** `OK`
- **Response 404:** `Not found`
- **Side effects:** `fs.unlinkSync()` on preset file
- **Consumers:** customize.html
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/delete_preset.json`

---

### POST /save-overlay-config

- **File:line:** server.js:1478-1514
- **Request:**
  - Content-Type: application/json
  - Body: overlay config object (full `cfg.overlay` replacement)
- **Response 200:** `OK`
- **Response 400:** `Bad JSON` | `Body required`
- **Response 500:** `Save error: <message>`
- **Side effects:**
  1. Reads current `config.json`
  2. Replaces `cfg.overlay` with the posted object
  3. Writes back `JSON.stringify(cfg, null, 2)` (full config, pretty-printed)
  4. Sets `previewConfig = null` (clears in-memory preview)
  5. Broadcasts SSE `event: overlay-config\ndata: <overlay JSON>\n\n` to all clients
- **Notes:**
  - This is a FULL REPLACEMENT of `cfg.overlay`, not a merge
  - The `/save-overlay-config` → disk + SSE broadcast pattern is the canonical "Apply" action
  - After this call, GET /overlay-config returns the newly saved overlay
- **Consumers:** customize.html ("Apply" button)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/post_save-overlay-config.json`

---

### GET /customize

- **File:line:** server.js:1525-1536
- **Request:** no params
- **Response 200:** `text/html` — contents of `customize.html` from CWD
- **Response 500:** `customize.html not found`
- **Consumers:** tray.ps1 (`Start-Process "http://localhost:4242/customize"`), customize.exe WebView2 host
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/get_customize.json`

---

### 405 Method Not Allowed Handler

- **File:line:** server.js:1543-1577
- **Trigger:** Known path, wrong HTTP method
- **Response 405:** `Allow: <comma-separated methods>`, `Content-Type: text/plain`, body: `Method Not Allowed. Allowed: <methods>`
- **Route tables used:**
  - Exact-match: `/`, `/webhook`, `/client-log`, `/reload-config`, `/overlay-config`, `/current`, `/version`, `/events`, `/MastersFM.ico`, `/favicon.ico`, `/manifest.json`, `/preview-config`, `/list-presets`, `/save-preset`, `/save-overlay-config`, `/customize`
  - Prefix-match: `/art` (GET), `/load-preset` (GET), `/delete-preset` (DELETE)
- **Notes:** `/screenshot` and `/screenshot-response` and `/update`, `/update-status` are NOT in the route tables — wrong-method requests to these get 404, not 405
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/method_not_allowed.json`

---

### 404 Not Found (fallthrough)

- **File:line:** server.js:1578
- **Response 404:** `Not found` (text/plain)
- **Captured baseline:** `V14_S4_P1_BASELINE_CAPTURES/not_found.json`

---

## Webhook Body Shape (complete reference)

```json
{
  "artist":     "Artist Name",
  "track":      "Track Title",
  "duration":   180.5,
  "positionMs": 45000,
  "trackArt":   "https://cdn.example.com/art.jpg",
  "originUrl":  "https://soundcloud.com/artist/track",
  "source":     "SoundCloud",
  "isPaused":   false,
  "seek":       false
}
```

Fields `trackArt`, `originUrl`, `source`, `isPaused`, `seek` are all optional.
`duration` is in **seconds** (float), converted to ms inside `handleWebhook`.
`positionMs` is in **milliseconds**.

---

## /current Track Object Shape (complete reference)

```json
{
  "artist":    "string",
  "track":     "string",
  "trackArt":  "string (empty string if not yet resolved)",
  "duration":  0,
  "originUrl": "string",
  "startedAt": 1777882000000,
  "source":    "string",
  "isPaused":  false,
  "pausedAt":  0,
  "_setAt":    1777882000000,
  "durationGaveUp": true
}
```

`durationGaveUp` only present when all duration resolvers exhausted.
`trackArt` starts as `''`, populated async after artwork resolution completes (second SSE broadcast).

---

## Config Storage Architecture

- **Config file:** `%APPDATA%\MastersFM\config.json` (Roaming — survives reinstall)
- **Default template:** `<install dir>\config_default.json`
- **Deep-merge strategy:** `deepMergeConfig(userConfig, defaults)` — user values win; new keys from defaults added; arrays are never element-merged
- **Migration:** on startup, migrates Roaming → legacy LocalAppData → uninstall backup → fresh defaults
- **Overlay section:** `cfg.overlay` — the section exposed by `/overlay-config` and written by `/save-overlay-config`
- **Discord section:** `cfg.discord_rpc.enabled`, `cfg.discord_rpc.client_id`
- **Top-level flags exposed to overlay:** `cfg.liveAudioVisualizer`

---

## Removed/Commented-Out Routes (not implemented)

- `/obs-reset-position` — removed in v6.2.3 (placement stack removed). `SERVER_BOOT_ID` remains `let` because this used to bump it.
- `/switch-renderer` — removed in v9.4.0 (canvas2d removed; WebGL is only renderer now)

---

## Routes NOT in 405 Method Table (get 404 on wrong method)

These routes exist in code but were not added to `routeTable` in the 405 handler:
- `/screenshot` (GET only)
- `/screenshot-response` (POST only)
- `/update` (GET only)
- `/update-status` (GET only)

This is a minor inconsistency in server.js. Phase 2 should replicate this behavior exactly (not fix it) to maintain byte-for-byte parity for the 405 harness.

---

## Consumer Summary (what cannot break in Phase 2)

| Consumer | Endpoints Used |
|----------|---------------|
| **tray.ps1** | POST /webhook (every ~2s), POST /reload-config (after Discord config save), GET /current (tooltip poll) |
| **overlay.html** | GET /events (SSE), GET /current, GET /overlay-config (every 5s), GET /art, GET /version (every 3s), POST /client-log, POST /screenshot-response |
| **customize.html** | GET /events (preview iframe via overlay.html), GET /overlay-config, POST /preview-config, POST /save-overlay-config, GET /list-presets, POST /save-preset, GET /load-preset, DELETE /delete-preset, GET /manifest.json, GET /version, POST /client-log |
| **OBS Browser Source** | GET / (overlay.html) |
| **update.html** | GET /update-status |
| **customize.exe** | GET /customize |

---

*Total routes: 26 distinct handlers (excluding OPTIONS catch-all and 404/405 fallthrough)*
