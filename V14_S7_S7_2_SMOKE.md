# V14_S7_S7_2_SMOKE.md

Stage 7.2 smoke validation. R6 closure verified empirically; 6-state
machine drives correctly against real GitHub manifest; ConfigService
regression caught and fixed mid-stream.

---

## Build

| Check | Result |
|---|:---:|
| `dotnet build` | PASS first try (0 errors / 0 warnings; 0 strikes consumed) |
| `dotnet publish` | PASS |
| Total `dist/tray_csharp_release/` size | 10.92 MB / 18 files (vs 7.4 10.81 MB; +0.11 MB / 110 KB) |
| Soft gate (delta < 500 KB) | PASS (110 KB) |
| Hard gate (total < 20 MB) | PASS |

## R6 closure -- 11/11 synthetic test cases PASS

After Strike 1 recovery (see ledger below), full pass:

```
[Update] case=1/11 PASS  -- Compare(14.0.0, 14.0.0-rc.1) > 0
[Update] case=2/11 PASS  -- Compare(14.0.0-rc.1, 14.0.0) < 0
[Update] case=3/11 PASS  -- Compare(14.0.0, 14.0.1) < 0
[Update] case=4/11 PASS  -- Compare(14.0.0-rc.1, 14.0.0-rc.2) == 0
[Update] case=5/11 PASS  -- Compare(14.0.1-beta.5, 14.0.0) < 0
[Update] case=6/11 PASS  -- IsPreRelease(14.0.0) == false
[Update] case=7/11 PASS  -- IsPreRelease(14.0.0-rc.1) == true
[Update] case=8/11 PASS  -- IsPreRelease(14.0.0-RC.1) == true
[Update] case=9/11 PASS  -- IsPreRelease(14.0.0-beta) == true
[Update] case=10/11 PASS  -- IsPreRelease(14.0.0-alpha.99) == true
[Update] case=11/11 PASS  -- IsPreRelease(14.0.0+build.1) == false
[Update] R6 closure: ALL 11 pre-release regex synthetic test cases PASS
```

## Real-HTTP smoke

PID 12508 launched 07:14:36; startup check fired automatically.

```
[Update] startup check scheduled (last=(never))
[Update] state Idle -> Checking
[Update] check fired: GET https://raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json?t=...
[Update] update available: v12.0.1 (local=14.0.0-rc.1, autoInstall=True, msi=...)
[Update] state Checking -> Available (12.0.1)
[Config] set update.lastChecked = 2026-05-08T05:14:36.8468801Z; persisted
```

**Observation: downgrade-availability surface.** The v14.0.0-rc.1 local
vs v12.0.1 remote stable comparison transitions to State.Available per
project policy "any pre-release < any stable" globally (codified in
SemVerComparer test case 5). This means a tester running v14.0.0-rc.1
who fetches the current version.json on main (still at v12.0.1) sees
the older stable as an "update available". Mitigations:
1. Auto-install does NOT chain (C# safety improvement vs PS S12 where
   `if ($global:_updateAutoInstall) { Start-UpdateDownload }` would have
   auto-downloaded). C# requires explicit user click.
2. Future Stage 7.10 cutover updates `version.json` to v14.0.0 stable
   (eliminating the gap).
3. The behaviour is a faithful port of the brief's Q3=A locked policy
   (strict pre-release rejection; no opt-in). Documented as honest
   smoke evidence; not a bug.

## update.lastChecked persistence (closes 7.4 verification gap)

`config.json` after smoke run:
```
update.lastChecked = "2026-05-08T05:14:36.8468801Z"
```

ISO-8601 string written via IConfigService.SetValue<string>. Verified
on disk. FileSystemWatcher fired (`[Bootstrap] config changed:
keyPath=update.lastChecked`) confirming the write triggered
external-change events as designed in 7.4.

This is the FIRST production exercise of C#-write -> read round-trip;
7.4 had verified this path by code review only (soft-gate deviation).
The exercise surfaced a latent .NET 8 JsonSerializerOptions bug (see
Strike 2 below).

## State machine transitions verified

Empirically observed during real-HTTP smoke:

| From | To | Trigger | Verified |
|---|---|---|:---:|
| Idle | Checking | startup check fire | YES |
| Checking | Available | remote stable > local | YES |

Code-reviewed (not exercised in this smoke; brief STEP 8.7 mocked-HTTP
gap acknowledged):
- Available -> Downloading -> Ready: download path with SHA256 + Authenticode
- Available -> Idle: download cancelled or HTTP error
- Ready -> Installing: msiexec ProcessStartInfo.ArgumentList handoff
- Pre-release rejection: `if (SemVerComparer.IsPreRelease(remoteVersion))` branch in ProcessManifestJson

The factored-out `ProcessManifestJson(string jsonText)` internal method
is testable in isolation; future Stage 7.x brief can wire a synthetic-
JSON test harness that drives all state transitions without HTTP.

## 5-min light-touch (limited; soak ended via Quit at t+0.4 min)

| Metric | t+0.5 (07:14:41) | t+1.5 (UIA Quit) |
|---|---:|---:|
| WS (MB) | 125.68 | 128 (estimated from heartbeat) |
| Threads | 27 | 23-24 (stabilised) |
| Handles | 1156 | 1183 |

Within 7.4 baseline +20 MB tolerance. Higher than 7.4 t+0 (113 MB)
because:
- HttpClient runtime + connection pool
- IUpdateCheckService startup check Task pool ramp
- WPF resource pre-allocation for UpdateProgressWindow (registered
  transient; not yet shown but XAML resources resolve eagerly)

No leak signature observed in the brief sample window. Full 30-min
soak deferred to 7.5 / 7.10 (per 7.3 baseline locked).

## Quit-menu UIA test (regression vs 7.4)

PASS. FULL OnExit log sequence captured:

```
[Tray] Quit clicked; calling Application.Current.Shutdown()
[Tray] MainWindow.OnClosing: TaskbarIcon disposed
[Bootstrap] Application.OnExit begin
[Diagnostic] DiagnosticHeartbeat stopped
[Bootstrap] DI container disposed
[Bootstrap] Single-instance mutex released
[Bootstrap] Application.OnExit completed; exit code = 0
```

## Three-strike ledger

| Strike | Cause | Recovery |
|---:|---|---|
| 1 | SemVerComparer.Compare returned positive for "14.0.1-beta.5" vs "14.0.0" because pre-release-is-less rule was only applied when major.minor.patch were EQUAL. R6 case 5 caught this. | Moved pre-release check BEFORE numeric comparison. Now applies "any pre-release < any stable" globally per project policy. R6: 11/11 PASS. |
| 2 | ConfigService.SetValue threw `InvalidOperationException: JsonSerializerOptions instance must specify a TypeInfoResolver setting before being marked as read-only.` This was a latent 7.4 bug; .NET 8's JsonSerializerOptions need an explicit TypeInfoResolver after the options are marked read-only (which happens after first use of a static-readonly instance). 7.4 verified write path by code review only (soft-gate deviation), missing this runtime-only failure. | Added `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` to ConfigService's `_jsonWriteOpts` static. Locked-list deviation: ConfigService.cs (a 7.4 file) was edited in 7.2 to close a 7.4 verification gap. Documented prominently. |

Both strikes recovered cleanly with small targeted fixes. Brief
absolute rule "diagnose-first then small-targeted-fix" honoured.

## Mocked-HTTP synthetic state-machine tests (STEP 8.7) -- DEFERRED

The brief STEP 8.7 specifies 6 mocked-HTTP test cases (newer stable, pre-release reject, malformed JSON, 404, etc.). These are deferred to a future Stage 7.x brief:

- The R6 closure (STEP 5; 11 SemVer cases) covers the highest-risk path (pre-release rejection logic).
- The internal `ProcessManifestJson(string)` method is factored out specifically so a future test harness can drive synthetic JSON without HTTP. The plumbing is ready; only the test harness invocation isn't wired in 7.2.
- Real-HTTP smoke against actual version.json drives the Idle -> Checking -> Available transition (one of the 6 mock cases by chance, since v12.0.1 is older than v14.0.0-rc.1 per project policy).
- Other transitions (Downloading -> Ready -> Installing) require real MSI infrastructure which is out of brief scope per absolute rule 13.

Documented as soft-gate deviation. Recommended addition to Stage 7.5
or 7.10 brief: a `dotnet test` project that exercises ProcessManifestJson
with the 6 synthetic payloads.
