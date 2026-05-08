# V14_S7_S7_2_STATE_MACHINE_SPEC.md

Stage 7.2 -- STEP 1 deliverable. Source-of-truth contract for the C#
update-check state machine, distilled from `src/tray.ps1:5187-5798`
(S12). Every claim cited with `tray.ps1:LINE` for traceability.

---

## 1. State machine

States (per `tray.ps1:5191`):

```
idle | checking | available | downloading | ready | installing
```

C# adds a 7th state `Error` (per Stage 7.2 brief STEP 2.1) for
transient errors that hold visible state before reverting to Idle on
the next tick.

## 2. State variables (PS globals)

| PS variable | Type | Purpose | Line |
|---|---|---|---|
| `$global:_updateState` | string | Current state | 5191 |
| `$global:_updateVersion` | string | e.g. "10.1.0" | 5192 |
| `$global:_updateMsiUrl` | string | URL of MSI to download | 5193 |
| `$global:_updateMsiSha256` | string | Expected SHA256 of MSI | 5194 |
| `$global:_updateAutoInstall` | bool | Auto-install after download | 5195 |
| `$global:_updateLastCheckMs` | long | Epoch ms of last check | 5196 |
| `$global:_updateMsiPath` | string | Path to downloaded MSI | 5197 |
| `$global:_updateUserCheck` | bool | Manual-check trigger (vs cadence) | 5198 |
| `$global:_updateDownloadBytes` | long | Bytes received | 5202 |
| `$global:_updateDownloadTotal` | long | Total bytes | 5203 |

C# equivalents are encapsulated within `UpdateCheckService` private
fields (no global state).

## 3. Cadence

`tray.ps1:5730`: `$intervalMs = 1 * 60 * 60 * 1000` (1 hour).

Comment notes "v10.2.3: was 6 hours" -- prior cadence was 6 hours.

C# port follows the **brief STEP 4.9 specification: 6 hours**. The
brief explicitly says `if older than 6 hours, schedule a check on
app startup`. This is a deliberate revert to the pre-v10.2.3 cadence.

## 4. Manifest URL

`tray.ps1:5188`:

```
https://raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json
```

Cache-busting suffix appended at fetch time (`tray.ps1:5591-5592`):

```
$cb = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
"$($global:_updateManifestUrl)?t=$cb"
```

C# port preserves the cache-bust pattern.

## 5. Manifest schema

The remote `version.json` payload (verified against current root-level
`version.json`):

| Field | Type | Required | Notes |
|---|---|---|---|
| `version` | string | yes | SemVer-like; e.g. `14.0.0-rc.1` or `12.0.1` |
| `msi_url` | string | yes | Direct download URL |
| `msi_sha256` | string | yes | Expected SHA256 (lowercase hex) for integrity |
| `autoInstall` | bool | yes | Whether to skip the user "Install" click |

## 6. State transitions

### 6.1 `idle -> checking`

Trigger A: cadence elapsed (`tray.ps1:5733-5736`).
Trigger B: user-initiated check (`tray.ps1:4794` `Invoke-UpdateCheck` called from tray menu).
Trigger C: app startup with stale `update.lastChecked` (Stage 7.2 addition; not in PS S12).

Action (`tray.ps1:5586-5598`):
- Fire HttpClient.GetStringAsync against manifest URL
- Set state = `checking`
- Update `_updateLastCheckMs`

### 6.2 `checking -> available`

Trigger: HTTP fetch task completed with RanToCompletion AND remote SemVer is newer than local.

Action (`tray.ps1:5740-5757`):
- Parse JSON
- Compare versions (the FRAGILE `[version]` cast at line 5745 -- see section 7)
- If `remote > local`:
  - Store remote version, msi_url, msi_sha256, autoInstall flag
  - Set state = `available`
  - Show balloon notification (PS only; C# 7.2 brief Q2=A: NO balloon, tray label only)
  - If `autoInstall == true`: chain to `Start-UpdateDownload` immediately

### 6.3 `checking -> idle` (no update available)

Trigger: HTTP fetch completed AND remote SemVer <= local SemVer.

Action (`tray.ps1:5759-5779`):
- Set state = `idle`
- If user-initiated check: show "up to date" balloon + flash tooltip (PS only; C# 7.2 NO balloon)

### 6.4 `checking -> idle` (error)

Trigger A: HTTP fetch task NOT RanToCompletion (`tray.ps1:5780-5782`).
Trigger B: JSON parse failure (`tray.ps1:5783-5785`).
Trigger C: `[version]` cast failure (the accidental pre-release rejection -- see section 7).

Action: state -> `idle`, log error. C# port adds explicit pre-release
rejection BEFORE the comparison (R6 closure; STEP 4.4 of brief).

### 6.5 `available -> downloading`

Trigger: User clicks "Download" (or auto-install path from 6.2).

Action (`tray.ps1:5600-5656`):
- Construct WebClient (PS) / use HttpClient streaming (C#)
- Fire DownloadDataAsync
- Set state = `downloading`
- Per-progress events fire `DownloadProgressChanged` -> updates `_updateDownloadBytes` / `_updateDownloadTotal`

### 6.6 `downloading -> ready` (success)

Trigger: DownloadDataCompleted event with success.

Action (`tray.ps1:5615-5647`):
- Compute SHA256 of downloaded bytes
- Compare to expected `msi_sha256` -- mismatch reverts to state `available`, deletes file, logs error
- On match: write to temp path, store path, set state = `ready`
- If `autoInstall == true`: chain to `Install-Update`

C# 7.2 ADDS X509 Authenticode verification BEFORE transition to
Ready (per brief absolute rule 11). PS does Authenticode in
`Install-Update` instead (the install path, not the ready
transition). C# is stricter: verify before the user sees Ready, so
the badge displayed in Surface 07 mockup is meaningful.

### 6.7 `downloading -> available` (error)

Trigger: download cancelled or HTTP error.

Action: state -> `available`, log error.

### 6.8 `ready -> installing`

Trigger: User clicks "Install" (or auto-install from 6.6).

Action (`tray.ps1:5659-5725`):
- Authenticode verification (PS does it here; C# already did at 6.6)
- Set state = `installing`
- PS: write temp PS1 helper script, spawn `powershell.exe -File <helper>` (the helper does uninstall-then-install + relaunch). Helper uses single-string `-ArgumentList "/i `"path`" /quiet /norestart"` form to handle spaces in user path (B-003 quoting bug).
- PS: `Application.Exit()` so the MSI can replace files

C# port (per brief STEP 4.8): SIMPLER. Uses `ProcessStartInfo` with
`ArgumentList` (modern API; closes B-003 by construction):
- `["/i", msiPath, "/quiet", "/norestart"]`
- `Process.Start(...)` to spawn msiexec directly
- `Application.Current.Shutdown(0)`

PS's helper-script-with-uninstall-first approach handles the v10.1.7
"Major Upgrade SecureRepair failure" scenario. C# brief explicitly
chooses simpler ProcessStartInfo path; if SecureRepair regression
surfaces in Stage 7.10 cutover validation, the helper-script
approach can be reintroduced.

### 6.9 `installing -> (process exits)`

PS calls `Application.Exit()` after spawning installer. C# calls
`Application.Current.Shutdown(0)`. The installer (msiexec) handles
the rest; the tray exits so the MSI can replace files.

## 7. The fragile `[version]` cast

`tray.ps1:5745`:

```
$remote = [version]($json.version)
```

If `$json.version` is `"14.0.0-rc.1"`, the `[version]` cast THROWS:
`"Cannot convert value '14.0.0-rc.1' to type 'System.Version'"`.

The catch handler at `tray.ps1:5783-5785`:

```
} catch {
    $global:_updateState = 'idle'
    LogErr 'Poll-UpdateCheck manifest' $_
}
```

This ACCIDENTALLY rejects all pre-release remote versions by sending
state to idle. R6 documented this as fragile because:
1. The `[version]` cast can fail on OTHER inputs too (whitespace,
   non-numeric segments, etc.) -- the catch is a sledgehammer.
2. There's no log message saying "this was a pre-release"; just a
   generic LogErr.
3. The behaviour is not documented or tested.

C# port (R6 closure): explicit regex check BEFORE comparison:

```
if (SemVerComparer.IsPreRelease(remoteVersion))
{
    _logger.Log($"remote version {remoteVersion} is pre-release; auto-update disabled by policy", "Update");
    TransitionTo(UpdateState.Idle);
    return;
}
```

The 11 synthetic test cases in `SemVerComparer` SelfTest verify the
regex behaviour:
1. `IsPreRelease("14.0.0")` == false
2. `IsPreRelease("14.0.0-rc.1")` == true
3. `IsPreRelease("14.0.0-RC.1")` == true (case-insensitive)
4. `IsPreRelease("14.0.0-beta")` == true (no digits after)
5. `IsPreRelease("14.0.0-alpha.99")` == true
6. `IsPreRelease("14.0.0+build.1")` == false (build metadata != pre-release)
7. `Compare("14.0.0", "14.0.0-rc.1")` > 0 (stable > rc)
8. `Compare("14.0.0-rc.1", "14.0.0")` < 0
9. `Compare("14.0.0", "14.0.1")` < 0 (older < newer)
10. `Compare("14.0.0-rc.1", "14.0.0-rc.2")` == 0 (we treat all rc as equivalent and reject)
11. `Compare("14.0.1-beta.5", "14.0.0")` < 0 (any pre-release < any stable)

## 8. Authenticode verification

PS approach (`tray.ps1:5664-5678`):
- `Get-AuthenticodeSignature` cmdlet
- Accept `Status -in @('Valid', 'UnknownError')`
- Subject `-like '*MasterShadex*'`

C# approach (per brief absolute rule 11):
- `X509Certificate.CreateFromSignedFile(msiPath)` -> `X509Certificate2`
- `X509Chain.Build()` for chain validation
- `cert.Subject` parsed; expect `CN=MasterShadex`
- Verification BEFORE transitioning to Ready (stricter than PS)
- On any failure: log error, delete file, transition Idle (NOT Ready)

The C# approach is stricter:
- Chain validation REQUIRED (PS accepted UnknownError = self-signed
  cert with broken chain)
- CN exact match (PS accepted any subject containing "MasterShadex")
- Failure deletes the partial download; PS leaves it on disk

## 9. Install handoff

PS approach (`tray.ps1:5683-5724`): writes a temp PS1 helper to do:
1. Sleep 3s
2. Look up ProductCode in registry
3. msiexec /x ProductCode (uninstall old)
4. msiexec /i NewMsi (install new)
5. Relaunch app via Start-Process

C# approach (brief STEP 4.8, simpler):
- `ProcessStartInfo`:
  - `FileName = "msiexec.exe"`
  - `ArgumentList`: `"/i", msiPath, "/quiet", "/norestart"`
  - `UseShellExecute = false`
  - `CreateNoWindow = true`
- `Process.Start(psi)`
- `Application.Current.Shutdown(0)`

Closes B-003 (quoting-spaces bug) by construction: ArgumentList
escapes per-arg, no shell interpretation.

The simpler approach SKIPS the explicit uninstall step. If 7.10
cutover validation shows MSI Major Upgrade SecureRepair failure
(the v10.1.7 reason for the helper script approach), then 7.10
brief would re-introduce a similar helper. For 7.2, simpler path
ships.

## 10. C# implementation summary

Class: `UpdateCheckService` implements `IUpdateCheckService`.
Dependencies: `ILogger`, `IConfigService`, `ITelemetry`, `HttpClient`.
Internal state: `_currentState`, `_availableVersion`, `_msiUrl`,
`_msiSha256`, `_autoInstall`, `_msiPath`, `_lastErrorMessage`,
`_currentTask`, `_cts`, `_lock`.

Public events:
- `StateChanged` (carries old state, new state, optional progress, optional detail)

Public methods:
- `CheckNowAsync(CancellationToken ct = default)`
- `DownloadAsync(CancellationToken ct = default)`
- `Cancel()`
- `InstallAsync(CancellationToken ct = default)`

Public properties:
- `CurrentState`
- `AvailableVersion`
- `LastCheckedUtc`
- `LastErrorMessage`

The internal helper `ProcessManifestAsync(string jsonText)` is
factored out of CheckNowAsync so the test suite (STEP 5 R6 closure;
STEP 8.7 mocked HTTP synthetic cases) can drive state transitions
without making real HTTP calls.

Last-checked timestamp: ISO-8601 string at config key
`update.lastChecked` (preserved across restarts via IConfigService).
Read on startup; written on every successful check (success or
no-update; NOT on transient errors).

---

End of state machine spec.
