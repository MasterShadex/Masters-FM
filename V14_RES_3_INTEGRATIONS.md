# V14 Researcher 3 — External Integration Surface

## Integration Migration Matrix

| Integration | Current impl | .NET 8 equivalent | Compatibility issues | Effort |
|---|---|---|---|---|
| WinRT / SMTC | Reflection-based via PS 5.1 WinRT projection + `SMTCWatcher` in `tray_native.cs` | First-class `Windows.Media.Control` WinRT APIs via .NET 8 CsWinRT | Eliminates all reflection hackery; TypedEventHandler generics work natively | Low — simplification |
| Windows Forms (tray, menus, balloons) | `System.Windows.Forms` on .NET Framework 4.8 via PS 5.1 / `tray_launcher.cs` | WinForms on .NET 8 (fully supported, same API surface) | A few behavior differences (DPI awareness, ContextMenuStrip rendering); `ShowBalloonTip` still works | Low |
| Win32 P/Invoke (shell32, dwmapi, user32, gdi32) | `tray_native.cs` — `MFM_Shell`, `MFM_MenuNative`, `NativeMethods.GuiRes`, `Win32Windows`, `AudioPeak` | Identical on .NET 8; P/Invoke syntax unchanged | None — P/Invoke is unchanged in .NET 8 | None |
| Core Audio (IMMDevice / IAudioSessionManager2 / IAudioMeterInformation) | Manual COM P/Invoke in `MasterFM.AudioPeak` inside `tray_native.cs` | Same COM interop works on .NET 8; alternatively use CsWinRT AudioGraph or NAudio 2.x | No breaking change; NAudio 2.2+ targets .NET 6/8 natively | Low |
| GitHub auto-update | `Invoke-RestMethod` to `api.github.com`; `WebClient.DownloadDataAsync` for MSI; SHA-256 + `Get-AuthenticodeSignature` | `HttpClient` with `HttpClientFactory`; `System.Security.Cryptography.SHA256`; `AuthenticodeSignatureHelper` or WinTrust P/Invoke | `WebClient` is obsolete on .NET 8 (still works, issues deprecation warning); direct replacement is `HttpClient` | Low |
| Discord RPC | Zero-dependency Node.js IPC client in `discord_rpc.js` over `\\.\pipe\discord-ipc-N` | Keep Node.js file as-is (server.exe stays Node), or port to .NET 8 using `PipeStream`; NuGet `Lachee.DiscordRPC` is the mainstream .NET wrapper | No change needed if server.js stays; if porting, `Lachee.DiscordRPC` 1.1+ targets .NET 6 | None (stays in Node) |
| OBS Browser Source via WebSocket | `System.Net.WebSockets.ClientWebSocket` in PowerShell; obs-websocket v5 protocol on `ws://localhost:4455` | Identical `ClientWebSocket` API on .NET 8 | obs-websocket protocol is stable; no .NET change needed | None |
| OBS Browser Source via direct JSON injection | PowerShell string manipulation of OBS scene-collection JSON | Same approach in C# using `System.Text.Json` | No external dependency | None |
| Audio capture (WASAPI / MME / ASIO) | NAudio 2.x in `audio_spectrum.cs` targeting .NET Framework 4.8 | NAudio 2.2+ official .NET 6/8 TFM; `WasapiLoopbackCapture`, `WaveInEvent`, `AsioOut` all present | NAudio 2.2 dropped `net46` but retains same API surface; recompile with `net8.0-windows` TFM | Low — recompile |
| Node.js HTTP server + SSE (server.js) | `pkg` bundles `server.js` + `discord_rpc.js` to `server.exe` against Node 18 | Keep as-is, or migrate to .NET 8 ASP.NET Core minimal API with SSE; `pkg` itself now uses `@yao-pkg/pkg` for Node 18+ | `vercel/pkg` is unmaintained; `@yao-pkg/pkg` is the community fork and supports Node 18/20 | Low (pkg fork) or Medium (ASP.NET port) |
| Google Fonts (overlay.html) | CDN `<link>` to `fonts.googleapis.com` at runtime inside OBS CEF | Unchanged — CEF in OBS fetches at runtime | OBS CEF must have internet access | None |
| Music metadata APIs (Deezer, iTunes, MusicBrainz, SoundCloud, osu!, YouTube, Bing) | HTTPS `GET` via Node `https` module in `server.js`; no auth keys except SoundCloud (client_id scraped) | Identical endpoints; if ported to ASP.NET use `HttpClient`; all APIs are plain HTTPS | SoundCloud client_id is scraped (fragile); all others stable | None (stays in Node) |
| MSI / Windows Installer | Custom Python `build_msi.py` using `msi.dll` via ctypes; no WiX | On .NET 8 can use WiX v4 (MSBuild SDK) or keep Python builder | Python builder works without WiX; WiX v4 adds upgrade/CA flexibility | Low (optional) |
| Code signing | `signtool.exe` (Windows SDK) + `Set-AuthenticodeSignature` fallback; SHA-256 + DigiCert timestamp | Same tools; `signtool` and `Set-AuthenticodeSignature` unchanged on Windows 11 | No change | None |
| PowerShell runtime host | `System.Management.Automation` SDK in `tray_launcher.cs` hosting PS 5.1 runspace | On .NET 8 must use `Microsoft.PowerShell.SDK` NuGet (targets .NET 8); `InitialSessionState.CreateDefault()` API unchanged | If v13 C# rewrite is fully in C#, the PowerShell host is eliminated entirely (tray.ps1 is already legacy-only for uninstall); otherwise SDK swap is straightforward | Low / irrelevant post-v13 |
| WebView2 (customize.exe) | Microsoft WebView2 fixed-version runtime; `Microsoft.Web.WebView2.WinForms` NuGet | WebView2 NuGet 1.0.2210+ supports .NET 6/8; API surface identical | Evergreen or fixed-version runtime unchanged; NuGet TFM bump needed | Low |
| Last.fm | Removed in v8.x; stub remains in tray.ps1 as comment | No migration needed | N/A | None |

---

## Per-integration detail

### 1. WinRT / SMTC (System Media Transport Controls)

- **Current**: `GlobalSystemMediaTransportControlsSessionManager` WinRT type bound entirely via reflection at runtime. PowerShell's `[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime]` projects the WinRT type into the CLR AppDomain; `MasterFM.SMTC.SMTCWatcher` (in `tray_native.cs`, lines 225–835) then introspects it with `Type.GetMethod` / `GetProperty` / `Expression.Lambda` to attach `TypedEventHandler<,>` delegates.
- **How it's used**: `SMTCWatcher.Initialize(manager)` subscribes to `SessionsChanged`, `CurrentSessionChanged`, `MediaPropertiesChanged`, `PlaybackInfoChanged`, `TimelinePropertiesChanged`. Coalesces events (750ms burst window) and drains a `ConcurrentQueue<SMTCChangeRecord>` from the PowerShell tick. `CapturePlaybackInfo`, `CaptureTimeline`, `FetchMediaPropsSync` extract title/artist/playback state via reflection into `SMTCSessionSnapshot` objects.
- **.NET 8 path**: .NET 8 ships first-class WinRT projections for Windows APIs via CsWinRT (`Microsoft.Windows.SDK.NET.Ref` NuGet). `GlobalSystemMediaTransportControlsSessionManager`, `TypedEventHandler<T,U>`, and all SMTC types are available as real strongly-typed .NET types — no reflection or `ContentType=WindowsRuntime` tricks needed. `TryGetMediaPropertiesAsync` can be `await`-ed natively. The entire `AttachEvent` / `DetachBinding` workaround (lines 760–808) is unnecessary on .NET 8 because `EventInfo.AddEventHandler` works normally for WinRT events via CsWinRT.
- **Issues/risks**: The v13.0.0 C# rewrite (`tray_detectors.cs` / `tray_app.cs`) already targets .NET Framework 4.8 with WinMD references. Migrating to .NET 8 + CsWinRT simplifies about 600 lines of reflection code to ~60 lines of idiomatic async C#. Risk is low; the behavioral contract (event-driven with coalescing/burst-suppression) stays the same — only the binding mechanism changes.

---

### 2. Windows Forms (tray icon, menus, balloon tips)

- **Current**: `System.Windows.Forms` loaded in PowerShell (`Add-Type -AssemblyName System.Windows.Forms`). `NotifyIcon` at `tray.ps1:4264`; `ShowBalloonTip` used throughout (lines 4081, 4085, 4126, 4134, 4146, 4152, 4175, 4693, 4702). Custom `TrayMenuForm` renders rounded-corner menus via `DwmSetWindowAttribute` / `CreateRoundRectRgn` P/Invoke from `MFM_MenuNative`. `Application.Run` message pump is kept alive by `tray_launcher.cs`.
- **How it's used**: `NotifyIcon` hosts the tray icon and double-click handler. Custom menu form replaces the stock `ContextMenuStrip` for rounded styling. Balloon tips provide user feedback for updates, OBS add, Discord RPC toggle, auto-start toggle. `Application.ThreadException` and `AppDomain.UnhandledException` hooks log crashes.
- **.NET 8 path**: WinForms on .NET 8 is fully supported (`net8.0-windows` TFM, `<UseWindowsForms>true</UseWindowsForms>`). Same `NotifyIcon`, `ShowBalloonTip`, `ContextMenuStrip` API surface. The v13.0.0 C# rewrite already targets WinForms natively; the only question for a future .NET 8 bump is the TFM change in the `.csproj`.
- **Issues/risks**: `NotifyIcon.ShowBalloonTip` in .NET 8 on Windows 11 may route through `ToastNotification` instead of the legacy balloon depending on system focus-assist state — same behavior as .NET Framework 4.8. `DWM_WINDOW_CORNER_PREFERENCE` attribute IDs are unchanged. High DPI: .NET 8 defaults to `PerMonitorV2` DPI awareness; verify tray menu sizing on high-DPI monitors.

---

### 3. Win32 P/Invoke (shell32, dwmapi, user32, gdi32)

- **Current**: Four static classes in `tray_native.cs`:
  - `MFM_Shell`: `shell32.dll` `SetCurrentProcessExplicitAppUserModelID` (AUMID for taskbar grouping)
  - `MFM_MenuNative`: `dwmapi.dll` `DwmSetWindowAttribute`, `gdi32.dll` `CreateRoundRectRgn`, `user32.dll` `SetWindowRgn` + `SetForegroundWindow`
  - `NativeMethods.GuiRes`: `user32.dll` `GetGuiResources` (GDI/User object leak canary)
  - `MasterFM.Win32Windows`: `user32.dll` `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `GetWindowTextW`, `GetClassNameW`
- **.NET 8 path**: P/Invoke is identical on .NET 8. `DllImport` attribute works exactly the same. No changes needed. For cleaner code, `LibraryImport` (source-generated P/Invoke, .NET 7+) is the new preferred attribute but is a style preference, not a requirement.
- **Issues/risks**: None. All these Win32 APIs are stable and unchanged across Windows 10/11.

---

### 4. Core Audio (WASAPI session/peak detection)

- **Current**: `MasterFM.AudioPeak` in `tray_native.cs` (lines 101–207) defines COM interfaces (`IMMDeviceEnumerator`, `IMMDeviceCollection`, `IMMDevice`, `IAudioSessionManager2`, `IAudioSessionEnumerator`, `IAudioSessionControl`, `IAudioSessionControl2`, `IAudioMeterInformation`) via `[ComImport]` + GUIDs. Used by the SoundCloud-RPC pause-detection path to measure audio peak for a named process.
- **How it's used**: `AudioPeak.GetPeakForProcessName("soundcloud-rpc")` walks all render endpoints, enumerates audio sessions, matches by process name, and returns `IAudioMeterInformation.GetPeakValue`. Called from the scrobble tick to detect pause state for sources that never update SMTC `PlaybackStatus`.
- **.NET 8 path**: COM interop is identical on .NET 8; the same `[ComImport]` interfaces work unchanged. Alternatively, NAudio 2.2+ wraps `MMDeviceEnumerator` and `AudioSessionManager2` with managed types that expose peak values — `MMDevice.AudioMeterInformation.MasterPeakValue` — which would replace the manual COM glue.
- **Issues/risks**: None blocking. COM interop on .NET 8 requires `<EnableComHosting>` only for COM-exposable classes, not for importing COM servers — not applicable here.

---

### 5. GitHub Auto-Update

- **Current** (`tray.ps1`, `version.json`, `build_tools/_do_release_v*.ps1`):
  - Manifest: `https://raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json` — polled every 6 hours via `Invoke-RestMethod` / `HttpClient.GetStringAsync` fire-and-poll.
  - Download: `WebClient.DownloadDataAsync` (switched from `HttpClient.GetByteArrayAsync` in v10.1.4 due to PS 5.1 + WinForms reliability issues).
  - Verification: SHA-256 hash compared against `msi_sha256` field in manifest; `Get-AuthenticodeSignature` Authenticode check (accepts `Valid` or `UnknownError` status for self-signed cert).
  - Install: helper `.ps1` written to `%TEMP%`, executes `msiexec /quiet /norestart`; `Application.Exit()` called first so msiexec finds files unlocked.
  - Release push: `_do_release_v*.ps1` calls `api.github.com/repos/MasterShadex/Masters-FM/releases` (POST) then `uploads.github.com` (PUT asset); token read from Windows Credential Manager.
- **.NET 8 path**: Replace `WebClient.DownloadDataAsync` with `HttpClient.GetAsync` / `CopyToAsync` streaming; `WebClient` is `[Obsolete]` in .NET 6+ (not removed, just warned). `SHA256.ComputeHash` is unchanged. `Get-AuthenticodeSignature` / `WinVerifyTrust` P/Invoke unchanged. GitHub API calls use the same REST endpoints.
- **Issues/risks**: `WebClient.DownloadDataAsync` emits a deprecation warning at compile time in .NET 8 (`CS0618`). Functional replacement is `HttpClient.GetStreamAsync` + `Stream.CopyToAsync`. The msiexec install pattern (write helper script, call `cmd.exe /c ping + msiexec`) is Windows-specific and unchanged.

---

### 6. Discord RPC

- **Current**: `src/discord_rpc.js` — zero-dependency Node.js module. Opens `\\?\pipe\discord-ipc-N` (pipes 0–9) using Node `net.createConnection`. Implements the Discord RPC wire protocol: 8-byte header (`[opcode:uint32LE][length:uint32LE]`) + UTF-8 JSON payload. Opcodes: HANDSHAKE(0), FRAME(1), CLOSE(2), PING(3), PONG(4). `SET_ACTIVITY` command with `cmd`, `args.activity`, `args.pid`, `nonce`. Rate-limit coalescer: minimum 2000ms between `SET_ACTIVITY` writes; latest-wins on coalescence. Hard-coded client_id `1495411843836018819` (`DEFAULT_DISCORD_CLIENT_ID` in `server.js:208`); overrideable via config.
- **How it's used**: `server.js` calls `discord.init(clientId, logFn, onReadyFn)` at boot; `pushDiscord()` is called from `sseBroadcast()` on every track-state change. Activity payload includes `details` (track), `state` (artist), `timestamps.start/end`, `assets.large_image` (HTTPS art URL or `mastersfm_logo` registered asset), `buttons[0]` (listen link), `type: 2` (Listening).
- **.NET 8 path**: Option A (recommended): leave `discord_rpc.js` + `server.exe` as the Discord RPC host — no change needed. Option B (if porting to pure .NET 8): `System.IO.Pipes.NamedPipeClientStream` replaces `net.createConnection`; the binary framing is trivial `BinaryReader`/`BinaryWriter` code; `System.Text.Json` serializes payloads. NuGet `Lachee.DiscordRPC` 1.1+ abstracts all of this and targets .NET 6/8.
- **Issues/risks**: Discord's named pipe IPC is stable and undocumented-but-widely-used. The pipe walk (0–9) handles Discord Stable/Canary/PTB coexistence. No auth/TLS — local pipe only. Rate-limit (5 SET_ACTIVITY / 20s) is a Discord enforcement, not a .NET concern.

---

### 7. OBS Browser Source / WebSocket (obs-websocket v5)

- **Current**: Two injection paths in `tray.ps1`:
  - `Add-OBSBrowserSourceWS` (lines 3532–~3700): connects to `ws://localhost:4455` via `System.Net.WebSockets.ClientWebSocket`. Implements obs-websocket v5 protocol: `op:0` Hello, `op:1` Identify (with optional SHA-256 HMAC auth), `op:2` Identified, `op:6` Request/Response. Calls `GetInputList`, `GetSceneList`, `CreateInput` (browser_source, `http://localhost:4242/?renderer=webgl`), `CreateSceneItem` for each scene.
  - `Add-OBSBrowserSourceDirect` (line 3776): no OBS running — reads OBS scene-collection JSON files directly from `%APPDATA%\obs-studio\`, string-injects a browser_source entry, writes back.
- **How it's used**: Triggered by user clicking "Add OBS Overlay" from the tray menu. Auth challenge uses `SHA-256(Base64(SHA-256(password + salt)) + challenge)` per obs-websocket v5 spec.
- **.NET 8 path**: `System.Net.WebSockets.ClientWebSocket` is identical on .NET 8. No change required. `System.Text.Json` for the JSON payloads is the modern replacement for `ConvertTo-Json` / `ConvertFrom-Json` PowerShell calls.
- **Issues/risks**: obs-websocket v5 is the current stable protocol (OBS 28+). The direct JSON injection path is fragile but unchanged. OBS CEF (Chromium Embedded Framework) in the Browser Source hosts `overlay.html` — its Chromium version determines WebGL2 support (OBS 29+ uses CEF based on Chromium 108+, which supports WebGL2 and `@property`).

---

### 8. Audio Capture (WASAPI / MME / ASIO — audio_spectrum.exe)

- **Current**: `src/audio_spectrum.cs` — standalone C# exe compiled to `audio_spectrum.exe`. Uses **NAudio 2.x** (`NAudio.CoreAudioApi`, `NAudio.Wave`):
  - `WasapiLoopbackCapture` — WASAPI loopback (default; captures system output mix)
  - `WaveInEvent` — MME WaveIn (legacy Stereo Mix)
  - `WasapiCapture` (exclusive mode) — WDM-KS
  - `AsioOut` — Steinberg ASIO (VB-Audio/Voicemeeter/FL Studio)
  - 2048-point FFT, 480 log-spaced output bands, 8ms SSE publish interval on `http://127.0.0.1:4243/spectrum`.
  - NAudio DLLs: `NAudio.Core.dll`, `NAudio.Wasapi.dll`, `NAudio.WinMM.dll`, `NAudio.Asio.dll` all bundled in MSI.
- **How it's used**: `overlay.html` opens `new EventSource('http://127.0.0.1:4243/spectrum?fps=2000')` (line 2719). Frames are `data: {f:N,b:BASE64_480_BYTES}`. `audio_spectrum.exe` is spawned by `MastersFM.exe` (launcher) and placed in the Windows Job Object so it dies when the launcher exits. Process priority: `AboveNormal` on outer capture thread (from v9.1.0); `Normal` on the exe itself.
- **.NET 8 path**: NAudio 2.2.1 officially targets `net6.0` and `net8.0` (as well as `net472`). Recompile `audio_spectrum.cs` with `-target:net8.0-windows` and reference NAudio 2.2+ NuGet packages. All four backend classes (`WasapiLoopbackCapture`, `WaveInEvent`, `WasapiCapture`, `AsioOut`) exist in NAudio 2.2 with the same API. The custom HTTP SSE server (plain `TcpListener` / `HttpListener`) is unchanged.
- **Issues/risks**: NAudio 2.2 drops `net46` TFM support — use `net8.0-windows`. ASIO requires STA COM apartment (`ApartmentState.STA`) — still applies on .NET 8. The ASIO driver probe spawns short-lived STA threads (line 330–343) — this pattern is unchanged. `AsioOut` requires Steinberg ASIO SDK components bundled as native DLLs — these are included in NAudio.Asio and are PE binaries; they load fine under .NET 8 via the same P/Invoke mechanism.

---

### 9. Last.fm

- **Current**: Removed entirely in v8.x (`tray.ps1:1307`: "Last.fm dependency removed entirely"). A stub comment survives at line 1610: "Last.fm removed — kept as stub so old references don't break during transition."
- **.NET 8 path**: N/A — no migration needed.
- **Issues/risks**: None.

---

### 10. MSI / Windows Installer (build_msi.py)

- **Current**: `build_tools/build_msi.py` — custom Python script using `msi.dll` via `ctypes` (Windows Installer API directly). No WiX. Generates a fresh `PRODUCT_CODE` GUID per build so MSI `Upgrade` table removes the prior install cleanly. MSI contains: `MastersFM_Tray.exe`, `server.exe` (pkg'd Node 18), `MastersFM.exe` (launcher), `customize.exe`, `tray.ps1`, `overlay.html`, `config_default.json`, `tray_native.dll`, `audio_spectrum.exe`, NAudio DLLs, WebView2 runtime DLLs. `INSTALL.bat` bundled for manual install with cert trust.
- **.NET 8 path**: Python `build_msi.py` is build-time only — .NET version of the app is irrelevant. Keep as-is, or migrate to WiX v4 (MSBuild SDK, `<PackageReference Include="WiX">`) which supports .NET 8 targets natively and has better upgrade/CA/bootstrapper support. WiX v4 replaced WiX v3's `candle.exe`/`light.exe` with a single `wix build` command.
- **Issues/risks**: The Python MSI builder works but is non-standard. Fixed GUIDs for components (`GUID_COMP1`–`GUID_COMP24`) mean adding new files requires manual GUID management. WiX v4 automates component GUIDs via `ComponentGuidGenerationSeed`. The `md\tools.md` notes "WiX Toolset (MSI build)" as a tool item, suggesting WiX was considered but the Python path was chosen for zero-dependency builds.

---

### 11. Code Signing

- **Current**: `build_tools/signing/_sign_msi.ps1` — searches for `signtool.exe` under `C:\Program Files (x86)\Windows Kits\10\bin` (Windows SDK); falls back to `Set-AuthenticodeSignature` PowerShell cmdlet. Signs with SHA-256, timestamps via DigiCert (`http://timestamp.digicert.com`). Certificate selected by thumbprint from the local Windows certificate store. Tray's update verifier calls `Get-AuthenticodeSignature` and accepts `Valid` or `UnknownError` status (v10.0.1 fix for self-signed certs not in system trust store). `INSTALL.bat` imports cert to `TrustedPublisher` + `Root` stores via `certutil -addstore`.
- **.NET 8 path**: Identical — `signtool.exe`, `Set-AuthenticodeSignature`, and DigiCert timestamp server are Windows SDK / PowerShell tools independent of .NET runtime version. On .NET 8 the signing step in the build pipeline is unchanged.
- **Issues/risks**: Self-signed cert requires manual trust installation (`INSTALL.bat`). If the app moves to a commercial EV code-signing cert, the `UnknownError` acceptance in the update verifier can be tightened to `Valid`-only. No .NET-specific risk.

---

### 12. Node.js HTTP Server (server.js / server.exe)

- **Current**: `src/server.js` bundled by `@vercel/pkg` targeting `node18-win-x64` into `server.exe`. Exposes:
  - `GET /events` — SSE stream for overlay (`currentTrack` state, ``: ping heartbeat every 15s)
  - `GET /` — serves `overlay.html`
  - `GET /spectrum` — proxies/forwards to `audio_spectrum.exe` on `:4243` (actually the overlay connects directly to `:4243`)
  - `POST /webhook` — receives track data from tray detectors
  - `GET/POST /config`, `GET /preview-config`, `POST /save-overlay-config` — config CRUD
  - `GET /screenshot`, `GET /update`, `POST /set-volume`, `GET /obs-reset-position` — misc
  - External HTTPS calls: Deezer API, iTunes Search API, MusicBrainz WS2, SoundCloud API v2 (client_id scraped), osu! beatmapsets, YouTube search (HTML scrape), Bing Images (HTML scrape)
  - Loads `discord_rpc.js` as a `require()` module.
- **.NET 8 path**: Option A (keep Node): switch from unmaintained `vercel/pkg` to `@yao-pkg/pkg` (community fork, supports Node 18/20). Low risk, minimal change. Option B (migrate to ASP.NET Core): replace `server.js` with a .NET 8 minimal API project; SSE via `Response.WriteAsync` with `text/event-stream`; all HTTPS lookups via `HttpClient`. `discord_rpc.js` would need a .NET port (see integration 6). Option B is significant scope (medium effort) but eliminates the Node.js dependency entirely.
- **Issues/risks**: `vercel/pkg` is officially archived/unmaintained as of 2023. `@yao-pkg/pkg` 5.x maintains Node 18/20 support and is a drop-in replacement (`npm i -D @yao-pkg/pkg`). The SoundCloud `client_id` scrape is fragile (depends on SoundCloud JS bundle structure). YouTube/Bing HTML scraping has no API key requirement but can break on page layout changes.

---

### 13. WebView2 (customize.exe)

- **Current**: `customize.exe` is a WinForms host for `Microsoft.Web.WebView2.WinForms` showing `customize.html`. WebView2 runtime DLLs (`WebView2.Core.dll`, `WebView2.WinForms.dll`, `WebView2Loader.dll`) are bundled in the MSI (fixed-version runtime, not evergreen). Mentioned in `md/memory.md:23` as a v13.0.1 fix ("launches `customize.exe` (native WebView2), no browser ever").
- **.NET 8 path**: `Microsoft.Web.WebView2` NuGet 1.0.2210+ officially supports `net6.0` and `net8.0-windows`. API surface is identical. Bump `<TargetFramework>` to `net8.0-windows`, update NuGet reference.
- **Issues/risks**: Fixed-version WebView2 runtime bundling works the same on .NET 8. Evergreen WebView2 (downloaded by installer) is an alternative but requires internet access on first run. The WebView2 versioning is independent of .NET version.

---

### 14. Google Fonts (overlay.html CDN)

- **Current**: `overlay.html:19` — a single `<link>` to `fonts.googleapis.com` loading Inter + 11 Noto families + Noto Emoji. Loaded at runtime by OBS CEF.
- **.NET 8 path**: N/A — this is a browser-side CDN fetch inside OBS's Chromium. Not affected by host app .NET version.
- **Issues/risks**: OBS Browser Source must have outbound internet access for the fonts to load. On air-gapped machines, fonts fall back to system fonts (system-ui, sans-serif). Consider bundling woff2 files locally for offline reliability.

---

## Summary: .NET 8 Migration Complexity Ranking

1. **Easiest** (recompile + TFM bump only): WinForms, Win32 P/Invoke, Core Audio COM, WebView2, audio_spectrum NAudio recompile
2. **Low effort** (targeted rewrites): SMTC reflection → CsWinRT (significant simplification, eliminates ~600 lines), GitHub updater `WebClient` → `HttpClient`, pkg → `@yao-pkg/pkg`
3. **Medium effort** (optional architectural choice): server.js Node → ASP.NET Core minimal API (eliminates Node dependency; discord_rpc.js must be ported or wrapped)
4. **No action needed**: Discord RPC (stays in Node), OBS websocket, MSI builder, code signing, Last.fm (removed), Google Fonts

The most impactful improvement from a .NET 8 migration is the **SMTC WinRT reflection elimination** — replacing ~600 lines of runtime reflection + expression-tree delegate wiring with idiomatic `await`-based async CsWinRT calls.
