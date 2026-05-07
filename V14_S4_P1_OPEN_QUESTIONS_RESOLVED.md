# Phase 1 Open Questions — Resolved

**Date:** 2026-05-04
**Source:** `src/update.html` (261 LOC) + `src/config_default.json` (99 LOC), read verbatim

---

## Section 1: update.html behavior

### Mechanism: **Polling** (not SSE)

`const POLL_MS = 800;` — the page polls `/update-status` every **800 ms** using `setTimeout(poll, POLL_MS)` recursion, not a fixed `setInterval`. On offline detection it switches to a separate `tryReload()` loop polling `/version` every 2000 ms.

### Polling intervals

| Phase | Endpoint | Interval | Mechanism |
|-------|----------|----------|-----------|
| Normal operation | `/update-status` | 800 ms | recursive `setTimeout` |
| Server offline detection | `/version` | 2000 ms | recursive `setTimeout` |

### Endpoints consumed

| Method | URL | Cache | Response shape |
|--------|-----|-------|---------------|
| `GET` | `/update-status` | `no-store` | JSON object (see shape below) |
| `GET` | `/version` | `no-store` | `{"bootId": <number>}` — used only for offline-detection reconnect check |

**`/update-status` response shape (from server.js default + tray.ps1 writer):**
```json
{
  "state":      "idle | checking | available | downloading | ready | installing",
  "version":    "<string or null>",
  "progress":   0,
  "bytesDown":  0,
  "bytesTotal": 0,
  "current":    "<current app version string or null>",
  "ts":         0
}
```

States handled in update.html's `render()` function:
- `"idle"` — "You're up to date"
- `"checking"` — "Checking for updates…"
- `"available"` — "v{version} available"
- `"downloading"` — progress bar with `bytesDown`/`bytesTotal`/`progress`
- `"ready"` — "Verified. Installing now…"
- `"installing"` — "Installing v{version}…"
- `default` (any other string) — "Waiting…"

**Note:** `"error"` is listed as a possible state in server.js's default JSON but is NOT handled as a named case in update.html's `switch` — falls to `default` ("Waiting…"). This is a minor behavior to replicate exactly in Phase 2.

### User flow

1. tray.ps1 calls `Start-Process "http://localhost:4242/update"` when an auto-update begins
2. Browser/customize.exe WebView2 opens the `/update` page (served as HTML by server.exe)
3. Page immediately calls `poll()` → fetches `/update-status` at 800 ms intervals
4. tray.ps1 writes progress JSON to `%TEMP%\mastersfm_update_status.json` during update
5. server.js `/update-status` reads that file and serves it
6. As tray.ps1 progresses (checking → downloading → verifying → installing), page re-renders
7. When server goes offline (msiexec restarting the app): after 2 consecutive `/update-status` failures (`offlineCount >= 2`), renders "App restarting…" message
8. Then polls `/version` every 2000 ms until server is back → `location.reload()` refreshes the page

### Implications for sub-stage 4.2

- `/update-status` needs **no SSE** — plain polling, 800 ms interval client-side
- Server implementation is trivial: read a temp file, serve JSON, 200 or default JSON on file-not-found
- `/update` needs to serve `update.html` as `text/html` (same pattern as `/` serving `overlay.html`)
- The `"error"` state from tray.ps1 is valid but update.html just shows "Waiting…" for it
- **No changes needed to the SSE endpoint for this page** — update.html never connects to `/events`

---

## Section 2: config_default.json schema

**File:** `G:\Project Folder\Master FM\src\config_default.json` (99 lines)

### Full Schema

```json
{
  "lastfm_username": "",
  "welcome_seen": false,
  "liveAudioVisualizer": true,
  "audioSpectrumBackend": "wasapi_loopback",
  "audioSpectrumDevice": "default",
  "discord_rpc": {
    "enabled": true,
    "client_id": ""
  },
  "overlay": {
    "font": "Inter",
    "card": {
      "borderRadius": 56,
      "borderThickness": 5,
      "backgroundTop": "rgba(18,6,36,0.99)",
      "backgroundBottom": "rgba(12,4,24,0.99)",
      "backgroundAngle": 148,
      "backgroundBlur": 0
    },
    "border": {
      "enabled": true,
      "spinDuration": 4,
      "colors": ["#ff1085","#ff60c8","#d040ff","#8020e0","#4a0ab8"]
    },
    "glow": {
      "enabled": true,
      "color1": "#e632b4",
      "color2": "#961ee6",
      "intensity": 1.0,
      "pulseDuration": 3
    },
    "art": {
      "enabled": true,
      "width": 310,
      "fadeWidth": 130,
      "position": "left"
    },
    "nowPlaying": {
      "text": "Now Playing",
      "fontSize": 44,
      "color": "#c060ff",
      "letterSpacing": 5
    },
    "bars": {
      "enabled": true,
      "color": "#c060ff",
      "count": 4,
      "speed": 0.85
    },
    "platformBadge": {
      "enabled": true,
      "soundcloudLabel": "SoundCloud"
    },
    "title": {
      "fontSize": 68,
      "fontWeight": 800,
      "color": "#ffffff",
      "marqueeSpeed": 68,
      "marqueePause": 2
    },
    "artist": {
      "fontSize": 44,
      "fontWeight": 500,
      "color": "rgba(220,185,255,0.52)"
    },
    "spectrum": {
      "enabled": true,
      "barCount": 50,
      "gap": 3,
      "barRadius": 4,
      "colorMode": "rainbow",
      "color": "#c060ff",
      "smoothing": 0.6,
      "mirrorMode": false,
      "heightMult": 1.0,
      "minHeight": 2,
      "fps": 120,
      "autoGain": false
    },
    "progressBar": {
      "enabled": true,
      "height": 9,
      "trackColor": "rgba(160,70,255,0.10)",
      "fillColors": ["#8020c0","#ff60c8","#c040ff"]
    },
    "timestamps": {
      "fontSize": 46,
      "color": "#e090ff"
    },
    "showAnimation": {
      "duration": 0.5,
      "slideDistance": 28
    },
    "overlay": {
      "opacity": 1.0
    }
  }
}
```

### Top-Level Keys

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `lastfm_username` | string | `""` | Last.fm username (feature removed in v8.x, stub remains) |
| `welcome_seen` | boolean | `false` | Whether first-run welcome dialog has been shown |
| `liveAudioVisualizer` | **boolean** | `true` | **TOP-LEVEL** — injected into GET /overlay-config response by server.js |
| `audioSpectrumBackend` | string | `"wasapi_loopback"` | Audio capture backend (`wasapi_loopback`, `mme`, `wdmks`, `asio`) |
| `audioSpectrumDevice` | string | `"default"` | Audio device name or `"default"` |
| `discord_rpc` | object | — | Discord RPC settings |
| `overlay` | object | — | All overlay visual settings |

### `liveAudioVisualizer` Location

**CONFIRMED TOP-LEVEL** — `"liveAudioVisualizer": true` at the root of `config_default.json`, NOT inside `overlay`. This matches server.js's behavior:

```javascript
// server.js:1155-1158
if (cfg.liveAudioVisualizer !== undefined) {
    overlayCfg.liveAudioVisualizer = !!cfg.liveAudioVisualizer;
}
```

The server reads it from the top-level config and injects it into the `overlay` section of the GET /overlay-config response. It is NOT stored inside `cfg.overlay` on disk.

### `discord_rpc` Sub-Object Schema

```json
{
  "enabled": true,        // boolean — master RPC toggle
  "client_id": ""         // string — empty = use DEFAULT_DISCORD_CLIENT_ID (1495411843836018819)
}
```

### `overlay` Sub-Object Schema (full, typed)

```
overlay:
  font: string ("Inter")
  card:
    borderRadius: number (56)
    borderThickness: number (5)
    backgroundTop: string — rgba color
    backgroundBottom: string — rgba color
    backgroundAngle: number (148)
    backgroundBlur: number (0)
  border:
    enabled: boolean
    spinDuration: number (4) — seconds
    colors: string[] — array of hex colors (5 elements default)
  glow:
    enabled: boolean
    color1: string — hex color
    color2: string — hex color
    intensity: number (1.0)
    pulseDuration: number (3) — seconds
  art:
    enabled: boolean
    width: number (310) — px
    fadeWidth: number (130) — px
    position: string ("left")
  nowPlaying:
    text: string ("Now Playing")
    fontSize: number (44)
    color: string — hex color
    letterSpacing: number (5)
  bars:
    enabled: boolean
    color: string — hex color
    count: number (4)
    speed: number (0.85)
  platformBadge:
    enabled: boolean
    soundcloudLabel: string ("SoundCloud")
  title:
    fontSize: number (68)
    fontWeight: number (800)
    color: string ("#ffffff")
    marqueeSpeed: number (68)
    marqueePause: number (2) — seconds
  artist:
    fontSize: number (44)
    fontWeight: number (500)
    color: string — rgba color
  spectrum:
    enabled: boolean
    barCount: number (50)
    gap: number (3) — px
    barRadius: number (4) — px
    colorMode: string ("rainbow")
    color: string — hex color
    smoothing: number (0.6)
    mirrorMode: boolean
    heightMult: number (1.0)
    minHeight: number (2) — px
    fps: number (120)
    autoGain: boolean
  progressBar:
    enabled: boolean
    height: number (9) — px
    trackColor: string — rgba color
    fillColors: string[] — array of hex/rgba colors (3 elements default)
  timestamps:
    fontSize: number (46)
    color: string — hex color
  showAnimation:
    duration: number (0.5) — seconds
    slideDistance: number (28) — px
  overlay:                 <<<< NOTE: nested "overlay" inside "overlay"
    opacity: number (1.0)
```

**Notable: `overlay.overlay.opacity`** — there is a nested `overlay` sub-object INSIDE the top-level `overlay` object. This is `cfg.overlay.overlay.opacity` on disk. Phase 2 must not flatten this — the deepMerge must handle the nesting correctly.

### Other Top-Level Config Keys (consumed by tray.ps1, not by server.js directly)

| Key | Notes |
|-----|-------|
| `lastfm_username` | Consumed by tray.ps1; server.js ignores it |
| `welcome_seen` | Consumed by tray.ps1; server.js ignores it |
| `audioSpectrumBackend` | Consumed by tray.ps1 + audio_spectrum.exe; server.js ignores it |
| `audioSpectrumDevice` | Consumed by tray.ps1 + audio_spectrum.exe; server.js ignores it |

Server.js only reads: `overlay`, `discord_rpc`, `liveAudioVisualizer`. The rest pass through the deepMerge transparently.

### Implications for sub-stage 4.5

**1. `liveAudioVisualizer` location: TOP-LEVEL** (not inside `overlay`)

GET /overlay-config must inject it at response time:
```csharp
var overlayCfg = cfg.overlay; // read cfg.overlay section
if (cfg.liveAudioVisualizer != null)
    overlayCfg["liveAudioVisualizer"] = cfg.liveAudioVisualizer;
return overlayCfg;
```
This value is NOT written back into `cfg.overlay` on disk by POST /save-overlay-config — it remains top-level only.

**2. `overlay.overlay.opacity` nesting**

The config has `cfg.overlay.overlay = { opacity: 1.0 }`. deepMerge must handle this correctly — the nested `overlay` key inside the `overlay` object is a real config field, not a mistake. C# model must preserve it.

**3. `colors` and `fillColors` arrays**

deepMerge does NOT element-merge arrays. If user has `border.colors = ["#red"]` and default has 5 elements, the user's single-element array wins wholesale. Phase 2 deepMerge must replicate this exactly.

**4. C# config model design recommendation: Dictionary / JsonObject**

Given the deeply-nested arbitrary structure, a `Dictionary<string, JsonElement>` (or `JsonObject` from System.Text.Json) is the correct C# representation for the config file. A strongly-typed record/class would require attributes on every nested field AND would lose any user-added keys not in config_default.json (deepMerge preserves unknown user keys). Use `JsonNode` / `JsonObject` for both reading and merging.

The deepMerge algorithm in C# using `JsonNode`:

```csharp
JsonNode DeepMerge(JsonNode target, JsonNode source) {
    // target wins on all present keys
    // source fills keys missing from target
    // arrays: never merged element-by-element
    var result = target.DeepClone();
    if (source is JsonObject srcObj && target is JsonObject tgtObj) {
        foreach (var (key, srcVal) in srcObj) {
            if (!tgtObj.ContainsKey(key)) {
                result[key] = srcVal?.DeepClone();
            } else if (srcVal is JsonObject && tgtObj[key] is JsonObject) {
                result[key] = DeepMerge(tgtObj[key], srcVal);
            }
            // else: target value wins (no overwrite)
        }
    }
    return result;
}
```

**5. Config file path**

`%APPDATA%\MastersFM\config.json` (Roaming). C#: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MastersFM", "config.json")`.

---

OPEN QUESTIONS RESOLVED — sub-stages 4.2 and 4.5 are now unblocked for Phase 2.
