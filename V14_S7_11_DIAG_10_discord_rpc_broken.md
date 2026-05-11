# Diagnosis: Issue 10 -- Discord RPC Doesn't Work

## Reproduction (operator-confirmed)
Tray menu "Discord" item shows as enabled (checkmark). Discord Rich Presence does not appear
in Discord's UI while music is playing.

---

## Source-of-truth analysis

### Architecture overview

Discord RPC spans TWO separate processes:

**Tray process (MastersFM_Tray.exe):**
- `DiscordToggleService` -- reads/writes `discord_rpc.enabled` config flag ONLY
- No actual Discord IPC -- purely a config proxy

**Server process (server.exe, .NET 8):**
- `DiscordRpcService` (BackgroundService) -- handles actual Discord IPC
- Uses `Lachee.DiscordRPC` NuGet package for IPC protocol
- `ReloadConfigAsync()` -- reads `discord_rpc.enabled` from `config.json`

### Tray side (DiscordToggleService.cs)

`src/tray_csharp/Services/DiscordToggleService.cs` lines 1-5 comment:
```
// Server.exe (already on .NET 8) polls config and picks up the flag
// on its next read cycle. Decoupled from server -- no HTTP /reload-config
// call (PS does this for instant feedback; C# defers to server's poll
// cadence which is sub-second).
```

The tray service:
- Writes `discord_rpc.enabled` to `config.json` via `IConfigService.SetValue()`
- Does NOT POST to server's `/reload-config` endpoint
- Does NOT start or stop Discord IPC directly

### Server side (DiscordRpcService.cs)

`src/server_dotnet/DiscordRpcService.cs` line 100-157 (`ReloadConfigAsync`):
```csharp
public async Task ReloadConfigAsync(CancellationToken ct = default)
{
    // reads discord_rpc.enabled from config.json
    // re-initializes or disposes DiscordRpcClient accordingly
}
```

`ReloadConfigAsync` is called:
1. **At server startup** (ExecuteAsync, line 68)
2. **By ConfigHandler POST /reload-config** (documented in summary comment)

**KEY FINDING:** `ReloadConfigAsync` has NO timer-based invocation. There is no config file watcher in DiscordRpcService. The comment in DiscordToggleService claims "server's poll cadence which is sub-second" -- but no polling implementation exists in the code read.

**POTENTIAL GAP:** If the tray changes `discord_rpc.enabled` in config.json and does NOT call `/reload-config`, the server's `DiscordRpcService` only picks up the change:
- When server.exe is restarted (next `ExecuteAsync`)
- When something else calls `POST /reload-config`

Without a config file watcher or a reload call from the tray, the Discord RPC service's enabled/disabled state does not update dynamically after startup.

### Config file watcher: INCONCLUSIVE

The tray comment says "server's poll cadence which is sub-second." This could refer to a config watcher in `server_dotnet/ConfigService.cs` or similar, which might notify `DiscordRpcService` via events. This part of the server_dotnet codebase was NOT read in this diagnosis brief (read-only constraint: reading all of server_dotnet was not planned for this STEP).

**What is confirmed:** `DiscordRpcService.ReloadConfigAsync` is not timer-scheduled. Whether a config change event flows from a server-side ConfigService to DiscordRpcService cannot be confirmed without reading `server_dotnet/ConfigService.cs` and the server's DI registrations.

### Log evidence (overlay.log, 2026-05-11 08:31:39)

```
[Discord] DiscordToggleService initialized; initial state=True
[Discord] initial state=True
```

Discord shows as enabled on startup. No Discord RPC connection log lines appear (these would come from server.exe's own log, not overlay.log). The overlay.log only shows tray-side events.

**Missing evidence:** server.exe's log would show:
- "Discord RPC: client initialized (client_id=...)"
- "Discord RPC: connected as {Username}"
- OR "Discord RPC: connection failed -- is Discord running?"

These log lines cannot be confirmed from overlay.log (tray-only log). Server.exe logs to a separate location not read in this diagnosis.

### Lachee.DiscordRPC dependency

`src/server_dotnet/DiscordRpcService.cs` line 1: `using DiscordRPC;`

The Lachee.DiscordRPC library is a well-established .NET Discord RPC library. It works via named pipe IPC. Requirements:
1. Discord must be running on the operator's machine
2. The `DiscordRpcClient(_clientId).Initialize()` must be called
3. `_client.OnReady` fires only if Discord accepts the connection

**Could operator's Discord simply not have been running?** Possible. Cannot confirm from source-only analysis.

### csproj dependency check: ADDITIONAL READ NEEDED

Whether `Lachee.DiscordRPC` (or similar) is listed as a NuGet dependency in `server_dotnet.csproj` was NOT confirmed in this read. If the package isn't properly installed, Discord RPC would fail at runtime with `FileNotFoundException`.

---

## Root cause assessment

**Three candidate causes (in decreasing confidence):**

**Candidate A (HIGH confidence): Config toggle not propagated to server**
The tray writes `discord_rpc.enabled` to config.json but does NOT call server's `/reload-config`. If server.exe doesn't have an independent config watcher, the server's Discord RPC service keeps whatever state it had at startup. If discord was `enabled=True` at startup, the client IS initialized on startup. But if the operator had `discord_rpc.enabled=false` in a previous config and the server reads that on startup, the service never initializes.

**Candidate B (MEDIUM confidence): Config race or wrong config path**
The tray's `IConfigService` writes to `%APPDATA%\MastersFM\config.json`. Server.exe's `_state.ConfigPath` (see `DiscordRpcService.cs` line 107) must point to the same path. If they point to different paths (e.g., APPDATA vs. LOCALAPPDATA), the server reads stale config.

**Candidate C (LOW confidence, can't confirm from source): Lachee NuGet not installed / Discord not running**
If Discord wasn't running during operator's test, IPC connection fails. DiscordRpcService logs "connection failed -- is Discord running?" to server.exe's log. The operator seeing "doesn't work" might mean Discord wasn't running.

---

## Fix complexity

**Candidate A fix:** small (1-2 lines) -- add a `/reload-config` HTTP call from `DiscordToggleService.Enable()` / `Disable()` after writing the config. The server endpoint already exists (used by PS tray). This gives the same "instant feedback" path the comment says PS uses.

**Candidate B:** Requires reading server_dotnet's config path resolution -- no code change needed if paths already match; trivial fix if not.

**Candidate C:** Not a code bug; operator needs to confirm Discord is running.

---

## Recommended fix shape (NOT implemented)

In `DiscordToggleService.Enable()` and `Disable()`, after `_config.SetValue<bool>(EnabledKey, value)`:
```csharp
// Notify server to reload config immediately (same pattern PS tray uses)
await _webhookClient.PostReloadConfigAsync();
```

OR: Add a config file watcher in `server_dotnet/ConfigService.cs` that fires `ReloadConfigAsync` when `discord_rpc.enabled` changes. This is more robust but requires server-side change.

---

## Verification after fix

1. Ensure Discord is running
2. Open tray, confirm Discord toggle is checked (enabled)
3. Start playing music
4. Check Discord profile -- Rich Presence should show "Now Playing" status
5. Toggle Discord OFF in tray
6. Check Discord profile -- Rich Presence should clear within 1-2 seconds
7. Toggle Discord ON again -- Rich Presence should restore on next track update

---

## Adjacent concerns

The `discord_rpc.client_id` in config.json is a hard-coded default (`1495411843836018819`). If this App ID hasn't been configured in Discord Developer Portal, the Rich Presence images (`mastersfm_logo`, etc.) won't render. This is a separate configuration issue, not a code bug. Operator should verify the App ID is active in their Discord Developer Portal.
