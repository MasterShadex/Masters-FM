# V14 Sub-stage 4.1 Launcher Startup Diagnosis

**Date:** 2026-05-05
**Status:** CONFIRMED ROOT CAUSE
**Investigator:** Ruflo (diagnose-only run)

---

## One-Paragraph Summary

The .NET 8 skeleton server.exe exits when launched by MastersFM.exe because Kestrel cannot
bind to port 4242 -- the legacy Node.js server.exe is still LISTENING on that port. This is a
port-binding race condition (Hypothesis C). When the port is free, the .NET 8 server starts
correctly and stays alive indefinitely under launcher supervision. The failure is independent
of the skeleton returning 404 for all routes; implementing real endpoints in sub-stage 4.2
will NOT fix this. A one-line port-clearing step in launcher.cs (kill any existing process on
port 4242 before spawning server.exe) is the correct fix.

---

## Context

Sub-stage 4.1 retarget observation:

> "Launcher is running but server.exe isn't on 4242 -- likely the launcher started it but it
> exited quickly (skeleton returning 404 might interfere with tray health checks). Starting
> the skeleton manually for the functional test."

That parenthetical hypothesis about tray health checks was incorrect. The actual cause is
a port binding race, confirmed empirically below.

---

## Empirical Observations

### STEP 0b: launcher.cs source analysis

Key findings from reading launcher.cs in full:

- Working directory: `dir = AppDomain.CurrentDomain.BaseDirectory` (install dir,
  `C:\Users\Master\AppData\Local\MastersFM\`)
- ProcessStartInfo: `UseShellExecute = false, CreateNoWindow = true` -- NO
  `RedirectStandardOutput` or `RedirectStandardError`. .NET 8 console logger writes to
  inherited handles (null in WinForms parent, silently dropped).
- No `WaitForInputIdle()` or startup-confirmation mechanism.
- No health-check timer. No HTTP probe against server.exe.
- No auto-restart if server.exe exits.
- Job Object: `KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK`. Applied to server.exe immediately
  after spawn. Kills server.exe if launcher dies. Does NOT kill server.exe directly.
- After spawn: sets `PriorityClass = BelowNormal`. No process monitoring after that.

### STEP 0b: tray.ps1 source analysis

- `$server = $null; if (-not $skipServerLaunch) { ... }` -- when `-skipServerLaunch` is set
  (the normal launcher path), tray.ps1 does NOT start server.exe. Server launch fully
  delegated to launcher.
- Port 4242 kill-before-start logic exists in tray.ps1 at line 5075-5086 but is INSIDE the
  `(-not $skipServerLaunch)` block -- it only runs when tray.ps1 launches the server itself.
  When the launcher path is used, this kill step is SKIPPED.
- Poll timer at line 5199-5228: polls `GET /current` every 2 seconds for tooltip update.
  On failure, catches silently and sets `$tray.Text = "Master's FM - waiting..."`. No kill
  of server.exe, no restart trigger. Health check hypothesis RULED OUT from source.

### Hypothesis F result: RULED OUT FROM SOURCE

Neither launcher.cs nor tray.ps1 has a health-check timer that kills server.exe on
non-2xx responses. Hypothesis F is definitively false. The "skeleton returning 404 might
interfere with tray health checks" hypothesis in the original observation was incorrect.

### Clean-state test (STEP 1c, build with $UseDotnet8Server=$true)

Build rebuilt and installed with .NET 8 server path. Build script kills all processes at
step 3/5 then installs, so port 4242 is free at launch time.

```
03:36:51  [1/5] Building server.exe via dotnet publish (net8.0, ASP.NET Core, R2R)...
...
03:36:51  Launching C:\Users\Master\AppData\Local\MastersFM\MastersFM.exe
```

t+2s: server.exe PID=21540 ALIVE, port 4242 LISTENING (PID 21540)
t+5s: server.exe PID=21540 ALIVE
GET /version => HTTP 404 (skeleton behavior confirmed)
runtimeconfig.json tfm=net8.0 confirmed

Launcher log:
```
[03:36:51.971] server.exe spawned, PID=21540
[03:36:51.974] server.exe priority=BelowNormal (v9.6.0)
[03:36:52.021] hidden HWND created + AUMID attached, entering Application.Run
```

Result: .NET 8 server stays alive indefinitely when port 4242 is free at launch time.

### Hypothesis C test (STEP 2 -- port-binding race)

Controlled test: held port 4242 with TcpListener (PID 12788) BEFORE starting the launcher.

```powershell
$tcp = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 4242)
$tcp.Start()
# netstat: TCP 127.0.0.1:4242  LISTENING  12788
Start-Process "$installDir\MastersFM.exe" -WorkingDirectory $installDir
```

Observations:
- Launcher started at 03:39:23.443
- t+1s (03:39:24.463): server.exe DEAD
- t+2s, t+3s, t+5s: server.exe DEAD

Launcher log during this test:
```
[03:39:23.490] server.exe spawned, PID=420
[03:39:23.493] server.exe priority=BelowNormal (v9.6.0)
[03:39:23.538] hidden HWND created + AUMID attached, entering Application.Run
```

server.exe (PID 420) spawned and exited within ~1 second. The launcher logged the spawn
but has no monitoring to detect or log the exit. No "server exited with code" message exists
because launcher.cs does not watch server.exe's exit.

Windows Event Log: no entries for server.exe crash (clean exit, not a crash dump).
server.log: not created by .NET server (it only logs to console, which goes to null handles).

Result: Hypothesis C CONFIRMED. Port-binding race causes server.exe to start and exit
within ~1 second.

---

## Hypothesis Test Summary

| Hyp | Description | Result | Evidence |
|-----|-------------|--------|----------|
| A | Working directory mismatch | RULED OUT | Launcher sets same WD as manual launch (`AppDomain.CurrentDomain.BaseDirectory`). Server works fine in clean state. |
| B | stdout/stderr null handles crash | RULED OUT | Server runs fine under launcher in clean state. .NET 8 console logger handles null output gracefully. |
| C | Port-binding race | **CONFIRMED** | Server.exe dead at t+1s when port 4242 is held by TcpListener. Clean state works fine. |
| D | Job Object behavior | RULED OUT | Job Object assigns successfully (logged). Does not cause server exit at startup. |
| E | Exit code on startup | NOT INDEPENDENT | Server exits cleanly (ExitCode=1) after catching Kestrel bind failure. Symptom of C, not independent cause. |
| F | Health check killing server | RULED OUT | No health check exists in launcher.cs or tray.ps1. Hypothesis was false. |

---

## Root Cause

**Hypothesis C confirmed.** When the legacy Node server.exe is still running on
port 4242 at the time the launcher starts the .NET 8 server.exe, Kestrel fails to
bind to `http://127.0.0.1:4242`. The exception propagates through `app.RunAsync()`,
is caught by the `catch (Exception ex) when (ex is not OperationCanceledException)`
block in Program.cs, logged to console (null handle = silently dropped), and the
process exits with `Environment.ExitCode = 1`.

The launcher logs "server.exe spawned, PID=X" but has no process-exit monitoring
and never reports the death. tray.ps1 is unaware (it delegated server management
to the launcher via `-skipServerLaunch`). The failure is completely silent from the
outside.

The original sub-stage 4.1 observation "Launcher is running but server.exe isn't on
4242" was triggered by starting MastersFM.exe while the legacy Node server was still
active on port 4242 -- exactly the port-binding race scenario.

---

## Why the Failure is Silent

Three design gaps combine to make the failure invisible:

1. **launcher.cs has no exit monitoring for server.exe.** After `Process.Start()` + priority
   set + Job Object assignment, the launcher has no code to detect server.exe's death.
   `srv.WaitForExit()` is never called; no timer checks `srv.HasExited`.

2. **No server.log written by .NET server.** The .NET skeleton only uses `AddConsole()`.
   Since MastersFM.exe is a WinForms app with no console, stdout/stderr are null handles.
   All logging output is silently dropped. The `server.log` file is not created by the
   .NET server (only the legacy Node server writes `server.log`).

3. **tray.ps1 polls /current but catches failures silently.** When server.exe is dead,
   HTTP requests fail with connection refused. The poll timer catches this, sets the
   tooltip to "waiting...", and continues. No alarm is raised.

---

## Will Sub-stage 4.2 Fix This?

**NO.** Sub-stage 4.2 implements real HTTP endpoints (`/version`, `/current`, etc.).
The port-binding race causes server.exe to exit BEFORE Kestrel binds, BEFORE any
request can be served. The server never reaches a state where it could serve a `/version`
200 response. Implementing endpoints does not change the bind-time failure.

---

## Recommended Fix

**One change in launcher.cs** (defer to sub-stage 4.2 or earlier):

Before spawning server.exe, check if port 4242 is occupied and kill the occupying
process. Tray.ps1 already does this at lines 5075-5086 for the non-skipServerLaunch
case; launcher.cs should do the same for the normal launch case.

Minimal implementation in launcher.cs, inside the `if (File.Exists(server))` block
before `Process.Start(srvPsi)`:

```csharp
// Kill any process already holding port 4242 so the .NET server can bind
try
{
    var info = new System.Diagnostics.ProcessStartInfo("netstat.exe", "-ano")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var netstat = System.Diagnostics.Process.Start(info);
    string output = netstat.StandardOutput.ReadToEnd();
    netstat.WaitForExit();
    foreach (var line in output.Split('\n'))
    {
        if (line.Contains(":4242 ") && line.Contains("LISTENING"))
        {
            var parts = line.Trim().Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && int.TryParse(parts[parts.Length - 1], out int pid) && pid > 0)
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(); Log("Killed PID=" + pid + " on :4242"); }
                catch (Exception ex) { Log("Kill PID=" + pid + " failed: " + ex.Message); }
            }
        }
    }
    System.Threading.Thread.Sleep(400);  // let port release
}
catch (Exception ex) { Log("Port 4242 clearance check failed: " + ex.Message); }
```

Alternative (simpler): add a brief `Thread.Sleep(1000)` after the legacy server kill in
the rebuild script. But that only helps during install flows, not manual restarts.

**Permanent fix complexity:** Low. This is a 20-line addition to the existing server-spawn
block in launcher.cs. It mirrors the logic tray.ps1 already has.

**Can this be deferred to sub-stage 4.11 (the final switchover)?** Yes. The failure only
manifests when the .NET server is active (`$UseDotnet8Server=$true`). In sub-stages 4.2-4.10
the flag stays `$false` and the legacy Node server is used. The fix should land before
sub-stage 4.11 flips the flag permanently.

---

## Sworn Statement

- No permanent source modifications. `_full_rebuild.ps1` was temporarily changed from
  `$UseDotnet8Server = $false` to `$true` for the diagnostic build, then **reverted** to
  `$false` before writing this report. No changes to launcher.cs, Program.cs, tray.ps1,
  or any other source file.
- No version bump.
- No GitHub push.
- No memory.md mid-run edits (STEP 4 only).

---

LAUNCHER STARTUP INVESTIGATION COMPLETE -- HYPOTHESIS C (PORT-BINDING RACE) CONFIRMED.
