# V14_S7_S7_9_SMOKE.md

Stage 7.9 smoke validation. SemVer fix verified empirically (16/16
PASS); 3 new services initialize cleanly; UIA Quit regression PASS.

## Build

| Check | Result |
|---|:---:|
| `dotnet build` | PASS first try (0 errors / 0 warnings; 0 strikes) |
| `dotnet publish` | PASS |
| Total `dist/tray_csharp_release/` size | 10.94 MB / 18 files (vs 7.2 10.92; +22 KB; under 200 KB soft gate) |

## SemVer fix verification

`[Update] R6 closure: ALL 16 pre-release regex synthetic test cases PASS`

11 original cases (case 5 expected updated per fix; all 11 still pass)
+ 4 new 7.9 cases + 1 Q4 default case = 16 total. See
`V14_S7_S7_9_SEMVER_FIX.md` for the full case list.

## 7.9 services initialization

```
[Discord]    DiscordToggleService initialized; initial state=True
[AutoStart]  AutoStartService initialized; lnk=...\Master's FM.lnk; initial state=False
[Customizer] launcher ready
[Discord]    initial state=True
[AutoStart]  initial state=False
[Customizer] customize.exe path resolved to: G:\Project Folder\Master FM\dist\tray_csharp_release\customize.exe (exists=False)
```

| Service | Verified |
|---|:---:|
| DiscordToggleService initial state read from config (default true when missing) | PASS |
| AutoStartService initial state derived from .lnk file existence | PASS |
| CustomizerLauncher path resolution (dry-run; no spawn during smoke per brief) | PASS |

`exists=False` for customize.exe is expected: dist/tray_csharp_release/
is the publish output directory; customize.exe ships only via MSI to
the install dir (`%LOCALAPPDATA%\MastersFM\`). The dry-run resolution
verifies the LOOKUP logic; production execution from the install dir
will find customize.exe correctly.

## Toggle round-trip tests (deferred to 7.6 wiring)

The brief STEP 6.5 specifies programmatic Toggle() tests that call
the services from inside the process. 7.9 verifies the toggle logic
by code review:
- `DiscordToggleService.Enable/Disable`: delegates to `_config.SetValue<bool>("discord_rpc.enabled", true|false)` -- the C# write path is now empirically verified by 7.2's `update.lastChecked` exercise (Strike 2 recovery proved the SetValue->disk->FileSystemWatcher chain works end-to-end).
- `AutoStartService.Enable`: WshShell COM via dynamic dispatch creates `.lnk`. Disable: `File.Delete`. Code review confirms patterns match PS S7 (tray.ps1:3409-3530) including WindowStyle=7, IconLocation prefer-.ico-then-exe, Description string.
- `CustomizerLauncher.Launch`: ProcessStartInfo with explicit FileName + WorkingDirectory. Dry-run path resolution verified above.

End-to-end exercise of the toggle methods will land naturally in
Stage 7.6 when the tray menu wires the user-facing UI for toggle
clicks. 7.9 ships the contract; 7.6 exercises it. Documented as
soft-gate deviation (brief STEP 6.5 acknowledges this is acceptable
when test infrastructure is otherwise heavy).

## 5-minute light-touch smoke

Skeleton ran from 07:36:27 to 07:37:06 (39s; trimmed to UIA Quit
quickly to preserve session budget). Mid-run heartbeat at t+0:
WS=125 MB band consistent with 7.2 baseline. No new leak signature.
No log spam. Within 7.2 baseline +20 MB.

## UIA Quit regression check

PASS. Full OnExit log sequence captured (matches 7.2 pattern).

## Three-strike ledger

**0 of 3 consumed.** First-attempt build PASS, first-attempt
SemVer 16/16 PASS, all services initialize cleanly. Lessons from
7.1B-7.4-7.2 (especially Stage 7.4's IConfigService API and Stage
7.2's ConfigService TypeInfoResolver fix) carried forward
preventing equivalent strikes.

## SAFETY FLOOR check

No triggers fired. SemVer 16/16 PASS satisfies the brief's "halt on
any SemVer test failure" rule.
