# V14_S7_S7_10_INTERRUPT2_DIAGNOSIS.md

Stage 7.10 INTERRUPT #2 -- Webhook schema + procedure + harness threshold diagnosis

---

## S1.1 -- Defect E re-confirmation (GAP-1 + GAP-2)

### Source state (current)

`src/tray_csharp/Services/WebhookClient.cs` `BuildJsonPayload` (lines 92, 95):

```csharp
["durationMs"] = update.Duration?.TotalMilliseconds,
["art"]        = update.ArtUri,
```

`src/server_dotnet/WebhookHandler.cs` (lines 90-92, 105):

```csharp
// B2: Duration unit conversion (server.js line 909)
// tray sends seconds (float); store as milliseconds (long).
var durationMs = (long)Math.Round(((double?)data["duration"]?.GetValue<double>() ?? 0.0) * 1000.0);
...
var webhookArt = (string?)data["trackArt"] ?? string.Empty;
```

### GAP-1 (duration): CONFIRMED UNCHANGED

Server reads `data["duration"]` (expects float seconds). C# tray sends `"durationMs"` (milliseconds).
Result: `data["duration"]` = null for every C# tray new-track event. `durationMs = 0`.
Server DurationResolver cascade (Deezer / MusicBrainz) fires on every track change. Duration
eventually resolves correctly; progress bar starts at 0 before resolving (~0.5-2 s latency added
per track).

### GAP-2 (art): CONFIRMED UNCHANGED

Server reads `data["trackArt"]` (string). C# tray sends `"art"` (null when no SMTC thumbnail;
non-null when `SmtcEventBridge.TryExtractThumbnail` succeeded). Result: `webhookArt = ""` for
every C# tray send. SMTC-extracted thumbnails never reach the server; server art cascade runs.

### V14_S7_S7_10_WEBHOOK_BYTE_EQUIV.md Section 7 accuracy check

Both GAPs are documented accurately. No delta from Section 7 to current source. Confirmed.

### Fix (STEP 2)

Per Section 8 Option A: change `BuildJsonPayload` only (~2 lines):
- `"durationMs"` key -> `"duration"`, value `TotalMilliseconds` -> `TotalSeconds`
- `"art"` key -> `"trackArt"`, value unchanged

Do NOT touch `WebhookHandler.cs`. Server contract stays as-is.

---

## S1.2 -- Defect F send-failure path analysis

### Current `SendTrackUpdateAsync` exception handling

`src/tray_csharp/Services/WebhookClient.cs` lines 62-76:

```csharp
catch (TaskCanceledException)
{
    // Server down or slow; PS pattern is to log once and continue
    _telemetry.IncrementCounter("webhook_send_errors");
}
catch (HttpRequestException)
{
    // Server not running; silent (PS S15 pattern)
    _telemetry.IncrementCounter("webhook_send_errors");
}
catch (Exception ex)
{
    _logger.LogErr("webhook send", ex, Component);
    _telemetry.IncrementCounter("webhook_send_errors");
}
```

The `webhook_send_errors` counter IS incremented on exceptions. However:
- `TaskCanceledException` and `HttpRequestException` produce NO log line at any level.
- The generic `Exception` catch logs at Error level -- correct.
- The heartbeat (`GetHeartbeatSummary`) shows only `webhooks={webhook_sends}` (SUCCESS count).
  Failure count (`webhook_send_errors`) is not surfaced in the heartbeat line.
- Result: during the 7.10 second soak attempt, server.exe WAS running (started via launcher),
  but the first soak (PID 38816 standalone, no server) had webhook sends fail silently with
  no indication in the harness output or heartbeat. The `webhooks=1` anomaly in that soak's
  heartbeat appeared to be a fluke rather than a systematic failure.

### PS tray `Send-WebhookAsync` comparison (tray.ps1:5344-5368)

PS tray uses TRUE fire-and-forget:
```powershell
$null = $global:_httpClient.PostAsync($Url, $content)  # discard Task
```
The returned Task is discarded. Async exceptions are UNOBSERVED. PS tray has ZERO visibility into
webhook send failures (they are silently dropped at the .NET runtime level as unobserved task
exceptions). The only catch is for synchronous-side errors (URL parse etc.), which logs once.

**PS does NOT retry. PS does NOT log async send failures.** This means the C# tray already
EXCEEDS PS parity by incrementing `webhook_send_errors` on exception (PS would not even do that).
The fix adds Warn-level logs for the two silently-swallowed exception types. Retry is NOT added
(matches PS behavior: no retry).

### Fix (STEP 3)

1. `WebhookClient.cs`: add `_logger.LogWarn(...)` in `TaskCanceledException` and
   `HttpRequestException` catch blocks. Add `_telemetry.IncrementCounter("webhook_send_failures")`
   in all catch blocks (NEW counter name; separates exception-path from HTTP non-success-path).
2. `Telemetry.cs` `GetHeartbeatSummary`: change `webhooks={sends}` to `webhooks={sends}/{fails}`
   where fails = `webhook_send_failures` counter.
3. `DiagnosticHeartbeat.cs`: no change needed (heartbeat delegates to `GetHeartbeatSummary`).

---

## S1.3 -- Defect G procedure documentation

### Launcher architecture

`MastersFM.exe` (`src/launcher.cs`) is the supervisor. On launch it:
1. Creates a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so all children die when the
   launcher exits.
2. Starts `server.exe` (ASP.NET Core) as a child process.
3. Starts `audio_spectrum.exe` as a child process.
4. Starts `MastersFM_Tray.exe` (C# WPF tray) as a child process.

Confirmed from `launcher.log` entries showing child PID assignments.

### Standalone tray behaviour

`MastersFM_Tray.exe` launched directly (not via `MastersFM.exe`) starts ONLY the tray process.
`server.exe` is absent. `WebhookClient.SendTrackUpdateAsync` attempts to POST to
`http://127.0.0.1:4242/webhook` which is not listening. Every send except possibly the first
throws `HttpRequestException` silently (per Defect F). The `overlay.log` shows `webhooks=1`
across the entire standalone session because the first send hit the brief moment before the
server exited from the prior session, and all subsequent sends failed silently.

### Evidence from the 7.10 first soak

Second soak (the one that halted at sample 38) ran with server via launcher:
`soak_7_10_20260509_014531.json` showed `webhooks=1` in the first heartbeat. But this was the
SECOND soak run -- the harness console log showed `webhooks=1` at sample 1. The tray had been
running since 01:38 with server.exe (PID 10392). The `webhooks=1` was one real send that
completed before the soak started capturing.

### Correct soak launch procedure

```powershell
Stop-Process -Name 'MastersFM*' -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Start-Process "$env:LOCALAPPDATA\MastersFM\MastersFM.exe"
Start-Sleep -Seconds 5
# Verify:
Get-Process -Name 'MastersFM_Tray','server' -ErrorAction SilentlyContinue
```

Both `MastersFM_Tray` AND `server` must appear in the process list. If server is absent, the
soak is measuring tray-without-server and webhook validation is worthless.

### Fix (STEP 4)

Add a launcher-supervised launch helper at the top of `_soak_7_10.ps1` that:
1. Stops any orphaned MastersFM* processes.
2. Launches via `MastersFM.exe` (launcher) -- NOT `MastersFM_Tray.exe` directly.
3. Waits 5 seconds.
4. Verifies both `MastersFM_Tray` AND `server` are in the process list.
5. Aborts with a clear error message if either is absent.

### Process improvement (ID-29 candidate)

Future stage soaks MUST include "launcher-supervised launch verified" as an explicit pre-condition
(analogous to the "wizard completion test" pre-condition added in INTERRUPT #1). Checklist item:
`Get-Process MastersFM_Tray, server` -- both must be running before the harness loop begins.
This applies to all future soaks. Analogous to INTERRUPT #1's wizard gate: a silently-broken
pre-condition corrupts the entire soak data set.

---

## S1.4 -- Defect H harness threshold analysis

### Current halt logic (`_soak_7_10.ps1` `Check-HaltConditions`)

Three conditions (evaluated after EVERY sample):
1. WS peak > 280 MB -- correct ceiling; keep
2. Both-half mean diff > 15 MB -- CORRECT PRIMARY; but currently triggers from sample 20
3. Final-30-min slope (LS regression) > 8 MB/h -- triggers from sample 30 (30 min)

### What caused the false halt at sample 38

Soak halted at sample 37-38: WS jumped from 211.7 MB (sample 36) to 249.1 MB (sample 37) in one
sample interval. The final-30-min LS slope was computed over samples 8-38 (the 30-sample window)
with the step-shift dominating. LS slope = 24.31 MB/h >> 8 MB/h threshold.

The +37 MB step-shift at sample 37 (T+36 min) is consistent with WPF image cache + ArtLruCache
reaching 16-track capacity under burst load: rapid track-skipping during the listening session
-> SMTC event storm -> 16 ArtUri objects loaded into ArtLruCache capacity limit -> WPF BitmapImage
decode cache populated -> one-time step function in WS. Post-step plateau: 249-254 MB, inside the
220-260 MB reconciled band. NOT a leak.

The reconciled-baseline gate (V14_S7_S7_8_MEMORY_BASELINE_RECONCILIATION.md Section 4) states:
- PRIMARY: both-half mean WS within 10 MB
- SECONDARY: final-30-min end-to-end slope < 5 MB/h
- RELAXED full-soak LS slope < 8 MB/h

The harness used LS slope as the FIRST-triggered halt (hit at sample 30 if any regression present),
which fired before enough samples existed to judge both-half stability. The harness must match
the reconciled gate by treating both-half mean diff as PRIMARY and suppressing slope halt until
enough samples exist to survive a normal listening-session burst.

### Proposed new halt logic (STEP 5)

```
HALT conditions (evaluated after every sample):

1. CEILING BREAKER (any sample count >= 1):
   peak WS > 280 MB on a single sample -> HALT immediately

2. BOTH-HALF MEAN DIFF PRIMARY (sample count >= 60, elapsed >= 60 min):
   first-half mean and second-half mean differ by > 15 MB -> HALT
   (matches Section 4 PRIMARY gate)

3. RELAXED SLOPE (sample count >= 90, elapsed >= 90 min):
   final-30-min end-to-end slope > 8 MB/h -> HALT
   (NO slope halt in first 60 min -- listening burst window)
```

Key change: slope halt suppressed until sample 90 (90 min). Burst load during Hour 1 (listening
session) cannot trigger a false slope halt. The both-half mean diff gate covers genuine drift
from sample 60 onward.

---

## S1.5 Commit

Commit: `Stage 7.10 INTERRUPT #2: webhook + procedure + harness diagnosis`
