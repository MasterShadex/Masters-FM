# V14_S7_S7_4_PREFLIGHT.md

Stage 7.4 pre-flight (STEP 0 outputs).

## 0.1 Working directory

`G:\Project Folder\Master FM\` -- confirmed.

## 0.2 Process check

No conflicting processes running.

## 0.3 sha256 baseline

See `V14_S7_S7_4_PROTECTED_BASELINE.md`. All 5 protected files
baselined; `md\memory.md` at post-Stage-7.3-APPEND state.

## 0.4 Repo state

| Item | Status |
|---|---|
| HEAD | `1650543` (Stage 7.3 memory.md APPEND) |
| Stage 7.1 commits (`f7bb96e` + `5fd9c8a`) | preserved (DO NOT amend) |
| Stage 7.1B commits (`b7eca5b` + `75b38cf`) | preserved (DO NOT amend) |
| Stage 7.3 commits (`0930e26` + `1650543`) | the 7.4 foundation |
| Tag `v14.0.0-rc.1` | `44723fb` (intact, NOT pushed) |
| `version.json` | modified-unstaged (RC1 deferral state) |
| `src/tray_csharp/` | EXISTS with 7.3 logging services + WPF skeleton |

## 0.5 Reading status

| Required | File | Status |
|---|---|---|
| Yes | `memory.md` | Read (post-7.3 APPEND state) |
| Yes | `V14_S7_REPLAN_FINAL_REPORT.md` | Read |
| Yes | `V14_S7_REPLAN_SUBSTAGE_BREAKDOWN.md` 7.4 entry | Read |
| Yes | `V14_S7_S7_3_FINAL_REPORT.md` | Read (closes O-1/O-2/O-3 confirmed) |
| Yes | `V14_S7_S7_3_SKELETON_BASELINE.md` | Read (133 MB plateau; 4.2 MB/h drift) |
| Yes | `src/tray.ps1:1447-1640` | Read in full (S3 Config section) |
| Yes | `src/tray_csharp/Services/ILogger.cs` | Read |
| Yes | `src/tray_csharp/App.xaml.cs` | Read (DI insertion target) |

## 0.6 Critical brief discrepancy caught

Brief STEP 3.1 states `%LOCALAPPDATA%\MastersFM\config.json per PS`.
PS source at `tray.ps1:1456` shows `%APPDATA%` (Roaming), NOT
`%LOCALAPPDATA%`. PS source is canonical for round-trip parity. The
C# ConfigService MUST use ROAMING. Documented in
`V14_S7_S7_4_CONFIG_SCHEMA.md` section 1.

Also: the brief's welcome-seen API uses `welcome.seen` nested key,
but PS uses flat `welcome_seen` + `welcome_seen_version` keys. C#
implements brief API (`GetWelcomeSeen()`/`SetWelcomeSeen()`) but
INTERNALLY uses PS's flat keys to maintain round-trip parity.

## 0.7 Default decisions

Per brief OPEN QUESTIONS PRE-EXECUTION:

1. FileSystemWatcher debounce 200 ms (default).
2. SetValue<T> array indices: NO (default; keys-only access).
3. Save() batched method: NO (default; SetValue auto-write).
4. Schema inventory exhaustive: YES (15 platform keys + welcome flags + Discord + audio + auto-start + overlay + last.fm legacy + underscore-prefixed metadata catalogued in CONFIG_SCHEMA.md).

## 0.8 Budget posture

Active work budget: 6-10h realistic, 14h hard cap. Budget pressure
from earlier stages; will skip programmatic round-trip C# write test
(brief STEP 5.5 acknowledges three options: test method / Quit hook /
dotnet test); will rely on code review + empirical PS-write
round-trip test instead. Documented as soft-gate deviation.
