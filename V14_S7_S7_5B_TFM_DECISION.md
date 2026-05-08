# V14_S7_S7_5B_TFM_DECISION.md

Stage 7.5B -- STEP 1 deliverable. csproj TargetFramework upgrade
decision + supporting changes.

---

## Decision

**TFM upgraded from `net8.0-windows` -> `net8.0-windows10.0.19041.0`**.

One-line change in `src/tray_csharp/MastersFM_Tray_v14.csproj`. No
other csproj adjustments required.

## Rationale

- `net8.0-windows` (no version) does NOT expose WinRT projections.
  `Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, ...")`
  returns null, which 7.5's SmtcEventBridge handles gracefully but
  leaves the SMTC arm inactive.
- `net8.0-windows10.0.19041.0` (Windows 10 2004) exposes WinRT
  projections for the entire `Windows.Media.Control` namespace
  including `GlobalSystemMediaTransportControlsSessionManager`,
  `GlobalSystemMediaTransportControlsSession`, etc.
- `Windows 10 2004` was released in May 2020; broadly available on
  any current testing machine.

## Alternatives considered

| TFM | Min Windows | Verdict |
|---|---|---|
| `net8.0-windows` (current) | n/a (no WinRT) | KEEP IF SMTC ARM IS ACCEPTABLY DORMANT -- not for production |
| `net8.0-windows10.0.17763.0` | Windows 10 1809 | viable but older; SMTC API dates from 1809 |
| `net8.0-windows10.0.19041.0` (chosen) | Windows 10 2004 | sweet spot: broad compatibility + reliable WinRT projection |
| `net8.0-windows10.0.22000.0` | Windows 11 | unnecessary; would break Windows 10 testers |

## Compatibility risk

Minimum supported Windows version after this upgrade: **Windows 10
2004 (build 19041)**. Per Microsoft's Windows 10 servicing model,
1809 reached end-of-service for Home/Pro in May 2021 and Enterprise
in May 2024. Consumer testers running Home/Pro on 1809 are expected
to be EOL; Microsoft itself has stopped supporting them. 19041 is
the current realistic minimum for any actively-used Windows 10
machine.

If Orken's testers include any Windows 10 1809 or earlier machines:
revert to `net8.0-windows10.0.17763.0` (the SMTC API minimum).
Documented as the rollback option per Q4 default.

## Rollback

If Stage 7.10 cutover validation surfaces compatibility issues on
older Windows 10:
1. Revert this single line: `<TargetFramework>net8.0-windows10.0.17763.0</TargetFramework>`
   (Windows 10 1809 minimum)
2. Re-build and re-publish
3. SMTC arm should still activate; the API surface is identical at
   17763 (1809 introduced SMTC; 19041 just made it more reliable)

## Tester-OS confirmation deferred

This brief's execution does not include polling Orken's tester
audience. The default 19041 is applied. If 7.10 cutover finds
compatibility issues, rollback per above paragraph.
