# V14_S7_S7_4_CONFIG_SCHEMA.md

Stage 7.4 -- STEP 1 deliverable. Config schema inventory from
`src/tray.ps1:1447-1640` (S3) plus runtime read/write sites elsewhere
in `tray.ps1`. This is the source-of-truth spec for the C# ConfigService.

---

## 1. File location

**Path: `%APPDATA%\MastersFM\config.json`** (ROAMING AppData).

`tray.ps1:1456`: `$roaming = [System.Environment]::GetFolderPath('ApplicationData')`
which is `%APPDATA%` (Roaming) on Windows, NOT `%LOCALAPPDATA%`.

**Brief discrepancy noted:** the Stage 7.4 brief STEP 3.1 states
`%LOCALAPPDATA%\MastersFM\config.json per PS`. This is incorrect; PS
uses ROAMING. C# ConfigService MUST use the ROAMING path for round-trip
parity with the PS tray. ROAMING is also necessary for the original
design intent (`tray.ps1:1448-1455`): "ROAMING appdata survives MSI
uninstalls."

## 2. Encoding + format

- **Encoding**: UTF-8 without BOM. PS writes via
  `[System.Text.UTF8Encoding]::new($false)` (`tray.ps1:1587`, 1600).
- **BOM stripping on read**: PS reads via `[System.IO.File]::ReadAllText` +
  defensive `-replace '^﻿',''` (`tray.ps1:1572`, 1604). The defensive
  strip means PS handles BOM-prefixed files gracefully even though it
  itself never writes BOM.
- **Format**: indented JSON via `ConvertTo-Json -Depth 10` (default
  PowerShell formatting; equivalent to `WriteIndented = true` in
  System.Text.Json).
- **Key ordering**: stable (PS uses `[ordered]@{}` to preserve insertion
  order; `tray.ps1:1576`, 1581, 1608). System.Text.Json's `JsonObject`
  preserves insertion order natively, so C# round-trip is order-stable.

## 3. Cache TTL

- **PS platforms-specific cache TTL**: 5 seconds (`tray.ps1:1522`).
- **C# ConfigService TTL**: 1 second (per Stage 7.4 brief STEP 3.1).
- Justification for divergence: 1 s is tighter (more responsive to
  external writes); the brief specifies it explicitly. PS's 5 s was a
  pragmatic choice for the 100 ms scrobble-tick hot path. C# at 1 s
  is fine since detection-tick budget moves to event-driven in 7.5.

## 4. Concurrency

PS reads/writes serialized only by file system (no in-process lock).
C# ConfigService uses a single `lock` for cache + write operations
(per Stage 7.4 brief STEP 3.2).

PS does NOT have a FileSystemWatcher; reads are pull-driven from
each call site. C# adds FileSystemWatcher with 200 ms debounce
(per Stage 7.4 brief STEP 3.1).

## 5. Key catalog (full inventory)

Categorised by section. Keys are top-level unless noted nested.

### 5.1 Welcome / first-run keys

| Key | Type | Default | Read at | Write at | 7.4 enforcement |
|---|---|---|---|---|:---:|
| `welcome_seen` | bool | absent (treated as false) | `tray.ps1:1632` | (legacy; unused on writes; preserved on round-trip) | preserved |
| `welcome_seen_version` | string | absent | `tray.ps1:1633-1634` | (PS writes via Save-ConfigField when user clicks Continue on Welcome dialog; line ~2150 area) | enforced (the one behaviorally-active flag in 7.4) |

Welcome-seen logic (`tray.ps1:1623-1636`):
- Returns `true` only if `welcome_seen_version == APP_VERSION`.
- Legacy bool `welcome_seen=true` without version: returns `false`
  (force re-show on first version bump after upgrade).

C# `IConfigService.GetWelcomeSeen()` MUST replicate this: read
`welcome_seen_version`, compare to current app version, return bool.
For 7.4, the "current app version" is the `InformationalVersion`
attribute of the WPF skeleton.

### 5.2 Platform toggles (preserved in schema; 7.5 wires enforcement)

Nested under `platforms.{KEY}`, all bool, default `true` per
`tray.ps1:1517-1544`:

| Key path | Default | Notes |
|---|---|---|
| `platforms.Spotify` | true | Desktop app + Spotify Web Player |
| `platforms.SoundCloud` | true | Desktop RPC app + soundcloud.com |
| `platforms.YouTube` | true | youtube.com |
| `platforms.YouTubeMusic` | true | music.youtube.com |
| `platforms.AppleMusic` | true | Desktop app + music.apple.com |
| `platforms.TIDAL` | true | Desktop app + listen.tidal.com |
| `platforms.Deezer` | true | Desktop app + deezer.com |
| `platforms.AmazonMusic` | true | Desktop app + music.amazon.* |
| `platforms.Pandora` | true | pandora.com |
| `platforms.Bandcamp` | true | bandcamp.com |
| `platforms.Mixcloud` | true | mixcloud.com |
| `platforms.osu` | true | Rhythm game window title |
| `platforms.VLC` | true | VLC media player |
| `platforms.WMP` | true | Legacy WMP + Windows 11 Media Player |
| `platforms.Browser` | true | Master switch for all browser detection |

15 platforms total. Schema preserved in 7.4; enforcement in 7.5.

### 5.3 Discord RPC keys (server.exe writes/reads; tray reads-only)

Nested under `discord_rpc.*`. Documented but not exhaustively
catalogued here; see `src/server.js` (now `src/server_dotnet/`) for
write sites. PS tray reads via `Get-DiscordEnabled` at `tray.ps1:4391`.

| Key path | Type | Notes |
|---|---|---|
| `discord_rpc.enabled` | bool | Discord RPC toggle |

7.4 preserves these via JsonNode pass-through; no enforcement.

### 5.4 Audio device keys

Audio device selection lives at `audioDevice` or similar. Per the
audio device dialog at `tray.ps1:2192+`. 7.4 preserves on round-trip.

### 5.5 Live Audio Visualizer toggle

`liveAudioVisualizer` bool. Read by `Get-LiveAudioEnabled` at
`tray.ps1:4454`. 7.4 preserves on round-trip.

### 5.6 Auto-Start / installer-related keys

| Key | Type | Notes |
|---|---|---|
| `autostart_defaulted_on_v199` | bool | Tracks one-time default-on flip during v1.9.9 install. Preserved. |

### 5.7 Underscore-prefixed metadata keys

The PS tray and server.exe write metadata keys with leading
underscores (e.g., `_setAt` timestamps per Stage 4.5 lesson). These
must NOT be lost during round-trip. JsonNode pass-through preserves
them automatically (the rule that motivated the JsonNode pattern in
the first place; per absolute rule 4 of the brief).

Examples:

| Key | Notes |
|---|---|
| `_setAt` | Server.exe writes with last-modified timestamp |
| `_v` | Schema version marker (if present) |

7.4 MUST preserve these intact. Smoke test in STEP 5 verifies via a
PS-written config containing underscore keys, read by C#, written
back by C#, re-read by PS.

### 5.8 Overlay customisation keys

Overlay preset / theme / color picker / font settings live under
`overlay.*` nested objects. Written by `customize.exe` (the WebView2
host); read by `server.exe`; the tray neither writes nor reads these
directly. 7.4 preserves on round-trip via JsonNode pass-through.

### 5.9 Last.fm keys (legacy; preserved)

`lastfm_username` and similar legacy keys. Last.fm integration was
removed; keys preserved on round-trip but not consumed by the C# tray.

---

## 6. Read patterns in PS

`Get-PlatformsConfig` (`tray.ps1:1517`):
- Builds defaults map (all platforms = true).
- Reads file via `Get-Content -Raw | ConvertFrom-Json`.
- Walks `cfg.platforms.{Key}` for each platform.
- Caches result for 5 s.

`Get-WelcomeSeen` (`tray.ps1:1623`):
- File-existence check.
- Reads via `Get-Content -Raw | ConvertFrom-Json`.
- Compares `welcome_seen_version` to `$script:APP_VERSION`.
- No caching; called only at startup.

`Get-DiscordEnabled` (`tray.ps1:4391`): similar pattern.

`Get-LiveAudioEnabled` (`tray.ps1:4454`): similar pattern.

## 7. Write patterns in PS

`Save-PlatformsConfig($map)` (`tray.ps1:1564`):
- Read existing JSON (with BOM strip).
- Convert to ordered hashtable preserving all top-level keys.
- Replace `platforms` with new ordered platforms object.
- Write via `WriteAllText` + `UTF8Encoding(false)`.
- Invalidate cache.
- Log success.

`Save-ConfigField($field, $value)` (`tray.ps1:1594`):
- Read existing JSON (with BOM strip).
- Convert to ordered hashtable.
- Mutate one top-level field.
- Write via `WriteAllText` + `UTF8Encoding(false)`.
- Log success.

Neither pattern is atomic (no temp-then-rename). C# 7.4 ConfigService
ADDS atomic-write discipline (write-temp-then-rename via `File.Move`
with `overwrite:true`).

## 8. C# implementation contract

The C# ConfigService:

1. Uses ROAMING `%APPDATA%\MastersFM\config.json` (matches PS).
2. Reads via `File.ReadAllText` -> `JsonNode.Parse` (BOM-stripping
   not required since System.Text.Json handles BOM transparently;
   unlike PS which needs explicit strip).
3. Writes via atomic write-temp-then-rename pattern with
   `new UTF8Encoding(false)` and `JsonSerializerOptions { WriteIndented = true }`.
4. Caches the parsed root for 1 s (per brief).
5. FileSystemWatcher with 200 ms debounce; ignores echo from own writes.
6. Welcome-seen accessors handle the dual-flag pattern
   (`welcome_seen_version` is the authoritative flag; legacy
   `welcome_seen` bool returns false unless version matches).
7. Platform toggles preserved in schema; not enforced (7.5 wires
   enforcement).
8. Underscore-prefixed fields preserved automatically via JsonNode
   pass-through.

Schema is therefore documented; the implementation just needs to
honour the contract and not strongly-type anything.
