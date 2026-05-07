# V14 Researcher 1 — Component Inventory & Dependency Map

## Component Table

| File | Language | Lines | Role | Dependencies (calls / imports) | Dependents (called by) |
|------|----------|-------|------|-------------------------------|------------------------|
| `src/tray.ps1` | PowerShell | 9,424 | Core application engine: SMTC polling, tray icon & menu, OBS integration, scrobbling, update logic, balloon notifications, spawning server/audio/customize processes | `tray_native.dll` (Add-Type -Path); WinRT via reflection (SMTC); `http://localhost:4242` (POST /current, GET /overlay-config, GET /version); WinForms/.NET (System.Windows.Forms); shell32/dwmapi/user32 P/Invoke (via tray_native); opens browser to localhost:4242/customize, /update | `tray_launcher.cs` (dot-sources it); `launcher.cs` (spawns it via MastersFM_Tray.exe); `build_msi.py` (packages it) |
| `src/tray_native.cs` | C# | 835 | Pre-compiled P/Invoke + COM types DLL for tray.ps1. Provides: MFM_Shell (AUMID), MFM_MenuNative (rounded-corner menu), NativeMethods.GuiRes (GDI handle monitoring), MasterFM.Win32Windows (EnumWindows), MasterFM.AudioPeak (Core Audio peak), MasterFM.SMTC.SMTCWatcher (event-driven WinRT SMTC watcher) | shell32.dll, dwmapi.dll, gdi32.dll, user32.dll, kernel32.dll (P/Invoke); COM (Core Audio APIs); WinRT (SMTC) via reflection | `tray.ps1` (loads via Add-Type -Path); `_full_rebuild.ps1` (compiles it) |
| `src/tray_launcher.cs` | C# | 206 | PowerShell-in-process host exe (MastersFM_Tray.exe). Dot-sources tray.ps1 inside a global-scope PS runspace so WinForms click handlers keep function access. Sets AUMID. | System.Management.Automation (PS SDK); shell32.dll (AUMID); reads tray.ps1 from disk | `launcher.cs` (spawns MastersFM_Tray.exe); `_full_rebuild.ps1` (compiles it) |
| `src/launcher.cs` | C# | 518 | Root process host (MastersFM.exe). Creates Windows Job Object, spawns server.exe + audio_spectrum.exe + MastersFM_Tray.exe as children. Provides hidden HWND for Task Manager app-grouping. Sets AUMID. Single-instance mutex. | kernel32.dll (Job Object), shell32.dll (AUMID + SHChangeNotify + SHGetPropertyStoreForWindow), user32.dll; System.Windows.Forms (hidden form); spawns `server.exe`, `audio_spectrum.exe`, `MastersFM_Tray.exe` | Entry point — called by user / MSI shortcut; `_full_rebuild.ps1` (compiles it) |
| `src/customize.cs` | C# | 204 | WebView2-hosted native window (customize.exe). Renders customize.html at localhost:4242/customize as a frameless desktop app. Launched from tray menu. | Microsoft.Web.WebView2.Core + WinForms DLLs; dwmapi.dll (rounded corners); shell32.dll (AUMID); navigates to `http://localhost:4242/customize` | `tray.ps1` (Start-Process customize.exe from menu); `_full_rebuild.ps1` (compiles it) |
| `src/install_bootstrapper.cs` | C# | 241 | One-click installer bootstrapper ("Install Master's FM.exe"). Extracts embedded MSI + cert, imports cert to TrustedPublisher, runs msiexec, launches app. Fully standalone — no runtime dependencies on the app itself. | System.Security.Cryptography.X509Certificates; msiexec.exe; shell32.dll (ShellExecute for UAC self-elevation) | None at runtime — user double-clicks it; `build_msi.py` / release pipeline embeds MSI into it |
| `src/audio_spectrum.cs` | C# | 3,307 | WASAPI/MME/KS/ASIO audio capture + FFT + SSE server (audio_spectrum.exe). Captures system audio, runs 2048-point FFT, serves 480 log-spaced bands as SSE on port 4243. | NAudio.Core, NAudio.Wasapi, NAudio.WinMM, NAudio.Asio (NuGet); System.Net (HttpListener); `http://127.0.0.1:4243/spectrum` (self-hosted) | `launcher.cs` (spawns it); `overlay.html` (EventSource to port 4243); `customize.html` (preview reads port 4243); `tray.ps1` (tray menu toggles it, POSTs config to port 4243) |
| `src/server.js` | JavaScript (Node) | 1,610 | HTTP/SSE server on port 4242. Serves overlay.html, customize.html, update.html, config API (/overlay-config, /current, /version, /obs-reset-position, presets, manifest). Manages config.json. Calls Deezer/SoundCloud APIs for art. Integrates discord_rpc.js. | `./discord_rpc` (require); `config_default.json` (reads at startup and on reload); `src/overlay.html` (serves at GET /); `src/customize.html` (serves at GET /customize); `src/update.html` (serves at GET /update); Deezer API (HTTPS); SoundCloud API (HTTPS) | `launcher.cs` (spawns server.exe, the pkg-bundled form); `tray.ps1` (POSTs track data to /current); `overlay.html` (SSE from /current, GET /overlay-config); `customize.html` (GET /overlay-config, POST /overlay-config, GET /version); `update.html` (polls /version) |
| `src/discord_rpc.js` | JavaScript (Node) | 337 | Zero-dependency Discord Rich Presence IPC client. Connects to Discord's named pipe, sends SET_ACTIVITY frames with track data. Throttles to 1 write/2s to stay under Discord's rate limit. | Node built-ins only: `net`, `crypto`; Discord IPC pipe (\\?\pipe\discord-ipc-N) | `server.js` (require('./discord_rpc'); called on every track update) |
| `src/overlay.html` | HTML/CSS/JS | 3,479 | WebGL/Canvas OBS Browser Source overlay. Renders now-playing card with art, track/artist, spectrum visualizer, progress bar, animated border. Polls server for track updates via SSE. | `http://localhost:4242/current` (SSE EventSource); `http://localhost:4242/overlay-config` (GET config); `http://localhost:4243/spectrum` (SSE EventSource for spectrum); Google Fonts CDN (Inter + Noto families) | `server.js` (serves it at GET /); OBS Browser Source (loads it directly as URL) |
| `src/customize.html` | HTML/CSS/JS | 4,186 | Full overlay customizer UI. Live preview of overlay, controls for all config properties (colors, fonts, layout, spectrum, etc.), preset save/load. | `http://localhost:4242/overlay-config` (GET + POST); `http://localhost:4242/version` (polls for reload); `http://localhost:4243/spectrum` (spectrum preview); Google Fonts CDN | `server.js` (serves at GET /customize); `customize.cs` (loads it in WebView2 window) |
| `src/update.html` | HTML/CSS/JS | 261 | Update progress UI. Shows download bar and status messages while the app self-updates. Polls /version to detect server restart after install. | `http://localhost:4242/version` (polls); `http://localhost:4242/update-status` (polls progress) | `server.js` (serves at GET /update); `tray.ps1` (opens browser tab to localhost:4242/update) |
| `src/config_default.json` | JSON | 99 | Default configuration schema. Ships with every release; server deep-merges user config on top of it so new keys are filled in on upgrade. | None | `server.js` (reads at startup for defaults + migration); `build_msi.py` (packages it) |
| `_full_rebuild.ps1` | PowerShell | 346 | Build orchestrator. Runs pkg (server.js → server.exe), csc.exe (launcher.cs, customize.cs, tray_launcher.cs, tray_native.dll), audio_spectrum build helper, resedit (rebrand server.exe icons), then build_msi.py. | `npx pkg` (Node bundler); `csc.exe` (.NET Framework compiler); `build_tools/ps2exe/_build_spectrum.ps1`; `build_tools/resedit`; `build_tools/signing/_sign_msi.ps1`; `python build_tools/build_msi.py` | Developer / CI — called manually or by release scripts |
| `build_tools/build_msi.py` | Python | 653 | MSI packager. Uses msi.dll (ctypes) directly — no WiX. Reads version from tray.ps1 (`$script:APP_VERSION`), creates MSI with all dist files, sets up shortcuts, upgrade table, LZMA compression. | `msi.dll` (ctypes); reads `src/tray.ps1` for version; packages all files listed in FILES array (server.exe, tray.ps1, overlay.html, customize.html, discord_rpc.js, update.html, tray_native.dll, audio_spectrum.exe, NAudio DLLs, WebView2 DLLs, etc.) | `_full_rebuild.ps1` (calls `python build_tools/build_msi.py`); release scripts |
| `version.json` | JSON | 1 | Published release metadata. Consumed by tray.ps1 update checker to compare current vs. available version and download the MSI. Fields: version, msi_url, msi_sha256, autoInstall. | None | `tray.ps1` (Invoke-WebRequest to GitHub raw URL to check version); fetched from GitHub Releases URL |

---

## Dependency Graph (text)

```
[User / MSI Shortcut]
        │
        ▼
  launcher.cs (MastersFM.exe)          ← entry point, process root
  │  sets AUMID, creates Job Object, single-instance mutex
  │
  ├──► server.js (server.exe)          ← pkg-bundled Node process
  │         │  requires
  │         ├──► discord_rpc.js        ← IPC to Discord pipe
  │         ├──► config_default.json   ← default schema on startup
  │         └──► [HTTP GET /]          serves ──► overlay.html
  │              [HTTP GET /customize] serves ──► customize.html
  │              [HTTP GET /update]    serves ──► update.html
  │              [HTTPS] Deezer / SoundCloud art APIs
  │
  ├──► audio_spectrum.cs (audio_spectrum.exe)  ← WASAPI/NAudio/SSE
  │         └──► port 4243/spectrum           ← consumed by overlay + customize
  │
  └──► tray_launcher.cs (MastersFM_Tray.exe)
            └── dot-sources ──► tray.ps1      ← application engine (~9400 lines)
                                   │
                                   ├── Add-Type -Path tray_native.dll
                                   │          └── tray_native.cs (compiled DLL)
                                   │              ├── P/Invoke: shell32, dwmapi,
                                   │              │   gdi32, user32
                                   │              ├── COM: Core Audio (AudioPeak)
                                   │              └── WinRT reflection: SMTCWatcher
                                   │
                                   ├── HTTP POST → server.exe:4242/current
                                   │                (pushes track metadata each tick)
                                   ├── HTTP GET  → server.exe:4242/overlay-config
                                   ├── HTTP GET  → server.exe:4242/version
                                   │                (update checker)
                                   ├── HTTP GET  → version.json (GitHub)
                                   └── Start-Process → customize.exe (on menu item)
                                                          └── navigate → localhost:4242/customize

[OBS Browser Source] → http://localhost:4242/ → overlay.html
  overlay.html
    ├── EventSource → :4242/current   (track SSE)
    ├── GET         → :4242/overlay-config
    └── EventSource → :4243/spectrum  (audio spectrum SSE)

[install_bootstrapper.cs ("Install Master's FM.exe")]  ← standalone, no runtime deps on app
    ├── embeds payload MSI + cert as resources
    ├── imports cert → TrustedPublisher
    └── runs msiexec → installs app

[Developer]
  _full_rebuild.ps1
    ├── npx pkg src/server.js → server.exe
    ├── csc.exe src/launcher.cs → MastersFM.exe
    ├── csc.exe src/customize.cs → customize.exe
    ├── csc.exe src/tray_launcher.cs → MastersFM_Tray.exe
    ├── csc.exe src/tray_native.cs → tray_native.dll
    ├── build_tools/ps2exe/_build_spectrum.ps1 → audio_spectrum.exe
    ├── build_tools/resedit → rebrand server.exe icons
    └── python build_tools/build_msi.py → MastersFM_Setup.msi
          └── reads src/tray.ps1 for $script:APP_VERSION
```

---

## Replaceability Ranking

Ordered from easiest (pure leaf / no dependents inside the app) to hardest (everything depends on it).

### 1. `version.json` — Trivially replaceable
Pure data file. No code logic. A .NET 8 equivalent is identical JSON, possibly served from a GitHub Actions artifact or a new endpoint. Zero dependencies inside the app at build time.

### 2. `src/install_bootstrapper.cs` — Fully independent
No runtime dependency on any other app component. Self-contained installer bootstrapper. Can be rewritten in .NET 8 as a standalone console app or replaced with a WiX bootstrapper, MSIX, or ClickOnce without touching anything else.

### 3. `src/config_default.json` — Pure data, format-stable
Read only by server.js at startup. Can be migrated unchanged. Any .NET 8 server would read the same JSON. No code changes needed in this file itself.

### 4. `src/update.html` — UI leaf, minimal logic
Polls two HTTP endpoints (/version, /update-status). Self-contained HTML/JS with no server-side rendering. Can be kept as-is or trivially ported since those endpoints need to exist anyway.

### 5. `src/discord_rpc.js` — Logic leaf, single consumer
Required only by server.js. Zero external dependencies (Node built-ins only). Logic is well-encapsulated (module.exports = {init, update, clear, destroy}). Can be rewritten in C# (named pipe IPC is straightforward) without touching any other file. Alternatively, keep Node bundling and leave this unchanged.

### 6. `src/customize.cs` — Thin UI shim, single purpose
Only calls localhost:4242/customize via WebView2. Replace with a .NET 8 WinForms/WPF window hosting WebView2 (same API, same NuGet package, no behavior change). No shared state with tray.ps1 at runtime beyond the HTTP endpoint.

### 7. `src/tray_launcher.cs` — Thin PowerShell host
Its only job is: open a runspace, dot-source tray.ps1, block. In a .NET 8 migration this component dissolves — if tray.ps1 is replaced by a C# application, this shim is simply removed.

### 8. `src/audio_spectrum.cs` — Self-contained but depended on by two frontends
Standalone exe with well-defined HTTP/SSE interface (port 4243). Can be migrated to .NET 8 independently (NAudio is fully .NET 8 compatible). overlay.html and customize.html just need the same SSE endpoint URL. The main coupling is the JSON frame shape `{f, b}` (b = 480-byte array), which must be preserved.

### 9. `build_tools/build_msi.py` — Build-time only, but reads tray.ps1 for version
Build dependency, not runtime. Can be replaced with a WiX 4 / MSIX / GitHub Actions workflow in a .NET 8 world. The version-from-tray.ps1 regex parse must be updated to read from wherever the new version source lives.

### 10. `_full_rebuild.ps1` — Build orchestrator
Calls every compiler step and bundles outputs. In a .NET 8 migration this becomes a dotnet publish pipeline. Must be updated after every other component migrates. No runtime coupling.

### 11. `src/overlay.html` — Frontend leaf, but complex internal logic
No .NET dependencies. Pure HTML/CSS/JS with Canvas rendering, SSE client, config application logic (~3,500 lines). Depends on server.js for two SSE streams and config GET. Can be migrated independently — the HTTP protocol contract is the only coupling. The challenge is complexity, not dependency depth.

### 12. `src/customize.html` — Frontend leaf, highest internal complexity
4,186 lines of HTML/CSS/JS. Depends on server.js for /overlay-config GET+POST and /version polling. Like overlay.html, the HTTP contract is the only coupling. Internal complexity (full config editor, live preview, preset system) makes this one of the larger standalone migration efforts.

### 13. `src/server.js` — Central HTTP hub, many consumers
All frontends and tray.ps1 talk through this. Migrating to .NET 8 (e.g. ASP.NET Core minimal API) means reimplementing: SSE push (/current), config CRUD, art fetching (Deezer/SoundCloud HTTPS), preset management, discord_rpc integration, /update progress streaming, manifest/PWA endpoint. Every other component talks to this — must be migrated before or in lockstep with the frontends.

### 14. `src/tray_native.cs` — Critical runtime dependency of tray.ps1
tray.ps1 calls Add-Type -Path tray_native.dll at startup and uses its types in every tick (SMTC, P/Invoke, AudioPeak). In a .NET 8 migration the SMTCWatcher and P/Invoke types would move into the new app's own assembly. The component dissolves rather than migrates — its functionality must exist before tray.ps1 can be removed.

### 15. `src/tray.ps1` — Hardest: core engine, deepest dependency fan-in
9,424 lines. Everything depends on it at runtime: tray_native.dll was purpose-built for it; tray_launcher.cs exists only to host it; launcher.cs spawns the host; server.js /current endpoint exists to receive its pushes; update.html/overlay.html exist to serve its users. The SMTC polling loop, OBS integration, scrobbling, balloon UI, config hot-reload, and all business logic live here. Must be migrated last, after server.js, tray_native, and the process-management layer are all available in .NET 8.

---

## Migration Order Implication

### Independent (can migrate in any order, no cross-blocking)

| Component | Why independent |
|-----------|----------------|
| `install_bootstrapper.cs` | Standalone installer, no runtime coupling |
| `version.json` | Pure data |
| `config_default.json` | Pure data |
| `audio_spectrum.cs` | Self-contained exe, HTTP/SSE interface is stable |
| `discord_rpc.js` | Consumed only by server.js via require() |
| `update.html` | Polls two endpoints; endpoints need to exist first but HTML itself is trivially portable |

### Sequential dependencies (must wait for prerequisites)

```
Phase 1 — Infrastructure (no runtime deps):
  install_bootstrapper.cs → .NET 8 console app (or MSIX bootstrapper)
  tray_native.cs → fold into new app assembly (prerequisite for Phase 3)
  audio_spectrum.cs → .NET 8 exe, NAudio unchanged

Phase 2 — Server layer (must precede frontends):
  discord_rpc.js → C# named-pipe client OR keep in bundled Node; wire into new server
  server.js → ASP.NET Core minimal API (SSE, config, art, presets)
    ↳ unblocks: overlay.html, customize.html, update.html (can be verified end-to-end)

Phase 3 — Process management + tray (must follow Phase 1 + 2):
  launcher.cs → .NET 8, Job Object + AUMID logic is identical
  tray_launcher.cs → dissolves; PS host not needed if tray.ps1 is replaced
  customize.cs → .NET 8 WinForms + WebView2 (same NuGet, trivial port)
  tray.ps1 → .NET 8 application (BackgroundService + WinForms tray icon + SMTC)
    ↳ requires: tray_native types already in assembly (Phase 1)
    ↳ requires: server.js replaced (Phase 2) — tray POSTs to /current

Phase 4 — Build pipeline:
  _full_rebuild.ps1 → dotnet publish + GitHub Actions
  build_tools/build_msi.py → WiX 4 / MSIX packaging
```

### The single critical-path gate

`tray.ps1` cannot be migrated until `server.js` (Phase 2) is complete, because the tray's tick loop posts to `/current` on every track change — that contract must be validated against the new server before the tray logic can be cut over. Everything else has no inter-component blocking at the source level.
