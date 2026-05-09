# V14_S7_8B_OBS_DIAGNOSIS.md

Stage 7.8B STEP 1: OBS "Connecting -> Error" diagnosis
Date: 2026-05-09
Brief: CLAUDE_CODE_INSTRUCTIONS.md (Stage 7.8B)

---

## 1. Failure Classification

**Mode B confirmed: OBS WebSocket server disabled (plugin installed, server_enabled: false).**

---

## 2. Live Diagnostic Results

### 2.1 Port check

```
netstat -an | findstr 4455
(no output)
```

Port 4455 is NOT listening. OBS process (`obs64`, PID 41060) is running but the
WebSocket server is not accepting connections on any interface.

### 2.2 OBS WebSocket plugin config

File: `%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json`

```json
{
  "alerts_enabled": false,
  "auth_required": false,
  "first_load": false,
  "server_enabled": false,
  "server_password": "wS6Yvij07Ly6n44p",
  "server_port": 4455
}
```

Key findings:
- `server_enabled: false` -- WebSocket server disabled; this is the sole root cause
- `server_port: 4455` -- port is correctly configured
- `auth_required: false` -- no password challenge; empty password also accepted
- `server_password: "wS6Yvij07Ly6n44p"` -- password known for when auth is enabled

### 2.3 Tray overlay.log evidence

```
[21:08:12] [OBS] connecting to ws://localhost:4455/ (attempt #24)
[21:08:12] [OBS] connect to ws://localhost:4455/ failed: WebSocketException: Unable to connect to the remote server
[21:09:12] [OBS] connecting to ws://localhost:4455/ (attempt #25)
[21:09:12] [OBS] connect to ws://localhost:4455/ failed: WebSocketException: Unable to connect to the remote server
```

Interval: 60s (backoff cap). Exception: `WebSocketException: Unable to connect to the
remote server` -- TCP connection refused, not an authentication error. The tray is
connecting to the correct host/port; the server is simply not listening.

### 2.4 App config state

File: `%APPDATA%\MastersFM\config.json`

```json
{
  "obs": { "enabled": true }
}
```

`obs.host`, `obs.port`, `obs.password` keys are absent. `ObsService.cs` reads these
with defaults: host=localhost, port=4455, password="" (DefaultPwd). The defaults are
correct; the missing keys are non-blocking.

---

## 3. Failure Origin in ObsService.cs

File: `src/tray_csharp/Services/ObsService.cs`

**Line 223** (in `ConnectOnceAsync`):

```csharp
try { await ws.ConnectAsync(uri, ct); }
catch (Exception ex)
{
    _log.LogErr($"connect to {uri} failed", ex, Cmp);
    return false;
}
```

The `ClientWebSocket.ConnectAsync` call throws `WebSocketException` because OBS is not
listening on port 4455. The exception is caught, logged as `[OBS] connect to
ws://localhost:4455/ failed`, and `ConnectOnceAsync` returns `false`. `ConnectLoopAsync`
then advances the backoff index, logs `reconnect in Xs`, sets state to `Error`, and
waits before retrying. This produces the observed "Connecting -> Error" cycle.

No code path beyond line 223 is reached when `server_enabled: false`. Lines 231-274
(handshake, authentication, RunConnectedLoopAsync) are unreachable in this mode.

---

## 4. Fix Scope

**Mode B fix = config edit only. No code change required.**

### 4.1 Operator action (required before STEP 3 can be live-tested)

1. In OBS: **Tools > obs-websocket Settings**
2. Check **"Enable WebSocket server"**
3. Confirm port: 4455
4. Click OK (OBS restarts the WebSocket listener immediately; no OBS restart needed)

After this, port 4455 will be listening and `ObsService.ConnectOnceAsync` will
succeed at line 223.

### 4.2 Config propagation (STEP 5, not STEP 1)

`%APPDATA%\MastersFM\config.json` should receive `obs.password` when the tray OBS
settings UI is wired in STEP 5. With `auth_required: false`, the current empty-string
default in `ObsService.DefaultPwd` is sufficient for the WebSocket handshake until
then.

### 4.3 Non-fix items

- `obs.host` / `obs.port` missing from config.json: non-blocking; defaults
  (localhost:4455) are correct.
- Backoff reaching 60s cap: expected behavior; the cap is in the
  `private static readonly int[] Backoff = { 5, 10, 20, 40, 60 }` array at line 46.
  After the fix, the next 60s cycle will succeed.

---

## 5. Mode Classification Reference

| Mode | Condition | This Session |
|---|---|---|
| A | obs-websocket plugin not installed | NOT this mode (config.json exists) |
| **B** | **server_enabled: false** | **CONFIRMED (this mode)** |
| C | Wrong port in config.json | NOT this mode (port 4455 matches) |
| D | Wrong password | NOT this mode (auth_required: false) |
| E | OBS not running | NOT this mode (obs64 PID 41060 running) |
| F | Code bug in ConnectOnceAsync | NOT this mode (exception is TCP refusal, not logic error) |

---

## 6. Verdict

Mode B. Fix is a one-time operator config edit (OBS > Tools > obs-websocket Settings >
Enable WebSocket server). No code change needed in STEP 1. Implementation STEPs 2-5
can proceed in parallel; live testing of the WebSocket path requires the fix to be
applied before STEP 3 smoke.
