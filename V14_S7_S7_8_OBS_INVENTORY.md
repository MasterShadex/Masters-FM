# V14_S7_S7_8_OBS_INVENTORY.md

Stage 7.8 STEP 3 — OBS port inventory.
Date: 2026-05-08.

Source reads: tray.ps1 lines 3533-5176 (S8 region); server.js (full grep); ICustomizerLauncher.cs (service interface template).

---

## S3.1 PS S8 OBS surface

### Function inventory (tray.ps1)

| Function | Line | Role |
|---|---|---|
| `Show-OverlayCustomizer` | 3533 | Launches `customize.exe`. **NOT OBS**; co-located in S8 region only. |
| `Add-OBSBrowserSource` | 3561 | WS v5 path — connects to `ws://localhost:4455`; authenticates; adds browser source via WebSocket. **Legacy; superseded by Direct path** but kept for reference. |
| `Get-OBSSceneCollectionPaths` | 3737 | Finds `%APPDATA%\obs-studio\basic\scenes\*.json`. Returns path array. |
| `Test-OBSBrowserSourceExists` | 3757 | Checks both `sources[]` global list AND per-scene `scene_items[]`. URL canonicality: `http://localhost:4242/?renderer=webgl`. Returns bool. |
| `Add-OBSBrowserSourceDirect` | 3805 | **Primary add path.** Edits scene collection JSON directly (no OBS connection required). Manages UUID, dimensions (1000×200), URL. Writes UTF-8 no BOM. |
| `Remove-OBSBrowserSourceDirect` | 4029 | Removes browser source from all scenes. Direct JSON edit. |
| `Start-OBSExitWatcher` | 4082 | WinForms timer; polls for OBS process exit every 2 s; re-applies JSON 1.5 s after OBS exits. |
| `Try-AddToOBS` | 4128 | **Primary call site.** Calls `Add-OBSBrowserSourceDirect`. Handles result codes: `OK` / `EXISTS` / `NO_SCENES` (60 s retry timer) / default error. Posts balloon tips. |
| `Test-ObsSourceExists` | 4412 | 30 s cached wrapper around `Test-OBSBrowserSourceExists`. |
| Menu item | 4750 | Label: `"OBS Overlay Added"` (source present) or `"Add to OBS — Not Set Up"` (absent). `IsChecked` = not definitely missing. |
| Auto-add flag | 5174 | `$script:_obsAutoAddAttempted = $true` set after auto-add timer fires on startup. |

### Connection lifecycle (Add-OBSBrowserSource — WS v5)

```
TCP connect → ws://localhost:4455
← op=0  Hello   { d.rpcVersion=1, d.authentication.challenge, d.authentication.salt }
→ op=1  Identify { d.rpcVersion=1, d.authentication: authStr }
← op=2  Identified
→ op=6  Request  { d.requestType="CreateInput", ... }
← op=7  RequestResponse
close
```

- Timeout: 3 s connect; WsRecv skips op=5 Event frames (eventSubscriptions=0 in Identify)
- Errors returning string codes: `"NO_OBS"` (TCP refused), `"AUTH_FAIL"` (op=7 status ≠ 100), `"TIMEOUT"`, `"ALREADY_EXISTS"`, `"OK"`

### Auth algorithm

**PowerShell (Add-OBSBrowserSource, line ~3600):**
```powershell
$sha    = [System.Security.Cryptography.SHA256]::Create()
$secret = [Convert]::ToBase64String($sha.ComputeHash(
              [System.Text.Encoding]::UTF8.GetBytes($Password + $hello.d.authentication.salt)))
$authStr = [Convert]::ToBase64String($sha.ComputeHash(
              [System.Text.Encoding]::UTF8.GetBytes($secret + $hello.d.authentication.challenge)))
```

**C# equivalent (to be used in ObsService.cs):**
```csharp
using var sha = SHA256.Create();
var secret  = Convert.ToBase64String(
                  sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
var authStr = Convert.ToBase64String(
                  sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
```

Round 1 hash input: `password + salt`.  
Round 2 hash input: `base64(round1) + challenge`.

### Config keys

| Key | PS tray | C# IObsService |
|---|---|---|
| Host | Hardcoded `localhost` | `obs.host` (new; default `"localhost"`) |
| Port | Hardcoded `4455` | `obs.port` (new; default `4455`) |
| Password | Hardcoded `""` (no auth by default) | `obs.password` (new; default `""`) |
| Enabled flag | No key; `$obsFlagFile` (`obs_configured.flag`) controls setup state | `obs.enabled` (new; default `false`) |
| Auto-connect | No key; auto-add timer on startup | `obs.auto_connect` (new; default `false`) |

None of the five new config keys exist in PS tray. All are additions for the C# service.

### Error / return codes (PS direct path)

| Code | Meaning |
|---|---|
| `OK` | Source added |
| `EXISTS` | Source already present |
| `NO_SCENES` | No scene collection files found; 60 s retry |
| `NO_OBS` | WS connect refused (WS path only) |
| `AUTH_FAIL` | Wrong password (WS path only) |
| `TIMEOUT` | WS recv timeout (WS path only) |
| (default) | Other error; balloon tip shown |

---

## S3.2 Server.js OBS contract

**None.** Grep of `server.js` for `obs`, `4455`, `browser_source`, `scene` yields zero matches. The PS tray talks directly to OBS (WebSocket or file system). The Node server has no OBS-related endpoints, middleware, or configuration. The C# `IObsService` also connects directly to OBS — no server.js involvement.

---

## S3.3 ID-28 non-port candidates

The following PS S8 features are intentionally **not** ported to the C# `IObsService`. They are ID-28 candidates (deferred post-V14 or replaced by the state-driven tray menu).

| Feature | PS function | Disposition |
|---|---|---|
| Browser source add | `Add-OBSBrowserSourceDirect`, `Add-OBSBrowserSource` | **Deferred post-V14.** C# service monitors OBS connection state; it does not modify scene collections. |
| Browser source remove | `Remove-OBSBrowserSourceDirect` | **Deferred post-V14.** Same as above. |
| Direct JSON scene editing | `Add-OBSBrowserSourceDirect` / `Remove-OBSBrowserSourceDirect` | **Deferred post-V14.** Requires OBS not running; complex UUID/dim management; out of scope for monitoring service. |
| OBS exit watcher | `Start-OBSExitWatcher` | **Deferred post-V14.** The C# ObsService reconnect loop already handles OBS exit via `ClientWebSocket` close; a separate process-polling timer adds no value. |
| 60 s NO_SCENES retry | `Try-AddToOBS` 60 s timer | **Not applicable.** C# service doesn't add sources; retry timer only meaningful for source-add flow. |
| Balloon tips (OBS status) | `Try-AddToOBS` notify calls | **Replaced.** C# uses state-driven `TrayMenuViewModel.ObsLabel` + `ObsTooltip` bindings (STEP 6). |
| `obs_configured.flag` | `$obsFlagFile` | **Not applicable.** C# has no browser source setup workflow; flag has no C# equivalent. |
| Test-ObsSourceExists (30 s cache) | `Test-ObsSourceExists` | **Not applicable.** C# service monitors connection, not source presence. |
| Scene collection path finder | `Get-OBSSceneCollectionPaths` | **Deferred post-V14.** Part of direct JSON path. |

---

## S3.4 Cut decision for Stage 7.8 IObsService

The C# `IObsService` is a **connection monitoring service**, not a browser-source-setup utility. Architecturally it is novel — the PS tray has no persistent OBS connection.

### IN scope (Stage 7.8)

| Capability | Notes |
|---|---|
| Connect to OBS-WS v5 | `ClientWebSocket`; `ws://{host}:{port}`; BCL only — no new NuGets |
| Authenticate (SHA256 double-hash) | Exact algorithm from PS `Add-OBSBrowserSource`; op=0 → op=1 → op=2 handshake |
| Connection state machine | 6 states: `Disabled`, `Disconnected`, `Connecting`, `Authenticating`, `Connected`, `Error` |
| Reconnect with exponential backoff | 5 s / 10 s / 20 s / 40 s / 60 s cap; resets on clean connect |
| Heartbeat (current-scene ping) | 30 s `GetCurrentProgramScene` request when `Connected`; keeps WS alive |
| Clean disconnect | On `obs.enabled` toggle off; on app shutdown |
| Config read | `obs.enabled`, `obs.host`, `obs.port`, `obs.password`, `obs.auto_connect` from `IConfigService` |
| Telemetry counters | `obs_connect_attempts`, `obs_connect_successes`, `obs_auth_failures`, `obs_disconnects`, `obs_reconnect_attempts` via `ITelemetry` |
| Logging | `[OBS]` prefix; connection events, auth result, state transitions, errors |
| `ConnectionStateChanged` event | Raised on every state transition; consumed by `TrayMenuViewModel` (STEP 6) |

### OUT of scope (Stage 7.8)

| Capability | Reason |
|---|---|
| Browser source add/remove | Deferred post-V14; ID-28 candidate |
| Direct JSON scene editing | Deferred post-V14; ID-28 candidate |
| Scene collection management | Deferred post-V14 |
| Recording / streaming control | Not part of V14 feature set |
| OBS exit watcher (process poll) | Handled by WS close event; separate timer unnecessary |
| Source presence detection | Out of scope for monitoring service |
| Balloon tips | Replaced by state-driven tray menu bindings |

### Interface template alignment

`ICustomizerLauncher` (Stage 7.9 reference) shows the project pattern:
- Namespace `MastersFM.Tray.Services`
- Minimal surface (3 members: `Launch()`, `Close()`, `IsRunning`)
- XML doc on each member
- No base class; no generic constraints

`IObsService` will follow same pattern: lean interface, `ConnectionState` property + `ConnectionStateChanged` event + `ConnectAsync` / `DisconnectAsync` + `IsEnabled`.

---

## Cross-references

| Document | Role |
|---|---|
| V14_S7_S7_8_LOG.md | Stage 7.8 run log |
| tray.ps1 lines 3533-5176 | PS S8 region (OBS + customizer) |
| src/tray_csharp/Services/ICustomizerLauncher.cs | Service interface template |
| src/tray_csharp/Services/IObsService.cs | Created in STEP 4 |
| src/tray_csharp/Services/ObsService.cs | Created in STEP 4 |
