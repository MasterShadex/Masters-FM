# V14 Stage 5 Phase 1 -- Risk Analysis

## 1. Risk inventory

### R1 -- SMTC event timing differences

**Description**: SMTCWatcher's event handlers fire on WinRT background threads.
Moving from csc.exe (.NET Framework) to dotnet SDK (.NET 8) changes the CLR's
thread pool and COM apartment behavior. Event delivery timing could differ --
in practice the 250ms rate limit and 800ms burst suppression absorb most variance,
but edge cases during rapid session switching are possible.

**Applies to**: Sub-stages 5.2, 5.3

---

### R2 -- Thumbnail format compatibility

**Description**: Current thumbnail extraction reads raw bytes from
IRandomAccessStreamWithContentType and forwards them as-is (base64 data URI).
MIME is detected from stream.ContentType + magic bytes. No resize or recompress.

Moving the extraction into tray_native.dll (5.2) must replicate this exactly --
particularly the MIME detection logic (image/png default, image/jpeg if FF D8 FF).
If the new code changes byte order, MIME assignment, or adds any processing,
the overlay's image display could break.

**Applies to**: Sub-stage 5.2

---

### R3 -- Session-switching edge cases

**Description**: soundcloud-rpc creates a new SMTC session object (new RCW) on
every track change while keeping the same SAUMID. SMTCWatcher already handles
this via a reference-equality check in EnumerateAndSubscribeSessions().

Any CsWinRT migration must preserve this behavior. CsWinRT wraps each WinRT
object in a new RCW wrapper -- ReferenceEquals behavior MUST be tested.

**Applies to**: Sub-stage 5.3 (CsWinRT conversion), 5.5 (validation)

---

### R4 -- Build pipeline regression

**Description**: _full_rebuild.ps1 currently uses csc.exe for tray_native.
Adding dotnet build for tray_native requires csc.exe removal or co-existence.
Risk: other build steps that assume csc.exe availability still work (csc.exe
is also used for MastersFM_Tray.exe -- check dependency graph carefully).

The csc.exe block in _full_rebuild.ps1 is guarded by `if ($csc)`. If dotnet
build replaces it, the `if ($csc)` block must NOT also run for tray_native
(duplicate output would overwrite the dotnet-built DLL).

**Applies to**: Sub-stage 5.4

---

### R5 -- tray.ps1 binding fragility

**Description**: tray.ps1 loads tray_native.dll via:
  `Add-Type -Path $__nativeDll`

Then accesses types by full name:
  `[MasterFM.SMTC.SMTCWatcher]::new()`
  `[MasterFM.Win32Windows]::GetAllVisibleTitles()`

If any public type name, namespace, method signature, or field name changes,
tray.ps1 breaks at the call site (PS throws a cast or method-not-found error).

ALL existing public API must be preserved verbatim in tray_native.cs.
Sub-stage 5.3 "cleanup" must NOT rename any public members.

**Applies to**: All sub-stages

---

### R6 -- WinRT activation requirements from non-packaged process

**Description**: Some WinRT APIs require the calling process to be packaged
(MSIX) or to have a specific app manifest. SMTC APIs (GlobalSystemMedia
TransportControlsSessionManager) are available from unpackaged processes -- this
is verified by the current implementation which works from PowerShell.

Risk: A future Windows update could restrict SMTC access from unpackaged
PowerShell processes. Low likelihood but worth noting for Stage 5 docs.

**Applies to**: All sub-stages (ongoing concern, not migration-specific)

---

### R7 -- .NET version / assembly loading incompatibility

**Description**: This is the most critical risk for Stage 5.

PowerShell 5.1 (.NET Framework 4.x) CANNOT load .NET 8 assemblies.
If tray_native.dll is compiled to target net8.0-windows10.0.19041.0 (full CsWinRT),
tray.ps1 will fail at `Add-Type -Path tray_native.dll` with an assembly load error.

**Resolution paths**:
a. Target netstandard2.0 -- loadable by PS5.1 AND PS7, but no CsWinRT Windows APIs
b. Migrate tray.ps1 host to PowerShell 7 (Stage 7 prerequisite question)
c. Ship two DLLs (net462 + net8.0), load correct one by detecting PS version

Option (c) adds significant complexity. Option (b) is gated on Q1 (see QUESTIONS.md).

**Applies to**: Sub-stages 5.1 (TFM choice), 5.3 (CsWinRT scope)

---

### R8 -- WinRT reflection vs CsWinRT projection: IReadOnlyList semantics

**Description**: SMTCWatcher iterates sessions via `System.Collections.IEnumerable`
cast. CsWinRT projections return `IReadOnlyList<T>` which implements
`IEnumerable<T>`. The cast from `IReadOnlyList<GlobalSystemMedia...Session>` to
`System.Collections.IEnumerable` must still work in CsWinRT.

In practice this should work (IReadOnlyList<T> -> IEnumerable<T> -> IEnumerable),
but must be tested. Failure would manifest as empty session list after migration.

**Applies to**: Sub-stage 5.3 (CsWinRT conversion only)

---

### R9 -- Add-Type namespace collision

**Description**: tray.ps1 checks `'MasterFM.Win32Windows' -as [type]` before
loading the DLL (line 19 fallback guard). If the DLL was previously loaded from
the old csc.exe-built version AND the process is still running, the new dotnet
SDK version cannot be loaded into the same AppDomain (CLR prevents duplicate
assembly loads by name).

This is only a risk during development (running tray.ps1 twice in the same
PS session after switching DLL builds). Fresh PS launch -- no issue.

**Applies to**: Development / testing only

---

## 2. Risk matrix

| Risk | Likelihood | Severity | Priority | Sub-stages |
|------|-----------|---------|---------|-----------|
| R7 (.NET version incompatibility) | HIGH | HIGH | CRITICAL | 5.1, 5.3 |
| R5 (tray.ps1 binding fragility) | MEDIUM | HIGH | HIGH | All |
| R4 (build pipeline) | MEDIUM | MEDIUM | HIGH | 5.4 |
| R3 (session switching edge cases) | MEDIUM | MEDIUM | MEDIUM | 5.3, 5.5 |
| R1 (event timing) | LOW | MEDIUM | MEDIUM | 5.2, 5.3 |
| R2 (thumbnail format) | LOW | MEDIUM | MEDIUM | 5.2 |
| R8 (IReadOnlyList semantics) | LOW | HIGH | MEDIUM | 5.3 only |
| R9 (Add-Type collision) | LOW | LOW | LOW | Dev only |
| R6 (WinRT activation) | VERY LOW | HIGH | LOW | Ongoing |

---

## 3. Per-risk mitigation strategies

### R7 mitigation (CRITICAL)

- Default to netstandard2.0 for sub-stage 5.1 -- safe for PS5.1 loading
- Do NOT move to net8.0-windows10.0.x until Q1 (PS7 migration) is resolved
- Add PowerShell version check in tray.ps1 startup logging:
  `EarlyLog "PSVersion=$($PSVersionTable.PSVersion)"`
  (already present at startup log)
- Keep csc.exe rollback path in _full_rebuild.ps1 for emergency revert

### R5 mitigation

- Lock all public type names, namespaces, method signatures in a contract list
  before any sub-stage editing begins
- Run the Type-Availability smoke test before and after every sub-stage:
  ```powershell
  $dll = Add-Type -Path tray_native.dll -PassThru
  $dll | Select-Object FullName | Format-Table
  ```
- Do NOT rename ANY public member during cleanup (sub-stage 5.3)

### R4 mitigation

- Add $UseDotnetTrayNative flag to _full_rebuild.ps1 (default $true)
- Keep the csc.exe block unchanged but guarded: only runs if $UseDotnetTrayNative=$false
- Test both paths: dotnet path produces correct DLL, csc.exe path still available
- Verify MastersFM_Tray.exe csc.exe step is NOT affected (different build block)

### R3 mitigation

- Add a specific soundcloud-rpc session-recycling test to sub-stage 5.5:
  1. Start soundcloud-rpc, play track, confirm SAUMID logged
  2. Skip to next track (session object recycled), confirm new track detected
  3. Verify DrainEvents() shows SessionRemoved + SessionAdded (not just MediaPropsChanged)

### R1 mitigation

- Do not change SMTCWatcher's rate-limit values (250ms, 800ms) during cleanup
- Run 15-min music session with event count logging before and after each sub-stage
- Accept up to +/- 10% event count variance as within normal range

### R2 mitigation

- Extract a thumb from Spotify before 5.2 -- save raw bytes to file
- After 5.2 -- extract same track again, compare bytes
- bytes.Length must be identical; MIME must match; data URI prefix must match

### R8 mitigation

- In sub-stage 5.3 (CsWinRT), explicitly test:
  `var sessions = mgr.GetSessions(); // IReadOnlyList<...>`
  `foreach (var s in sessions) { ... } // IEnumerable`
- If cast fails, add explicit `sessions.ToList()` to force enumeration

### R6 mitigation

- No mitigation available (OS policy)
- Document current working state as baseline
- Note: SMTC access from unpackaged Win32 processes has been stable since Windows 10

---

## 4. Open questions for user resolution

See V14_S5_P1_QUESTIONS.md (created separately).

Key question summary:

Q1: Will tray.ps1 migrate to PowerShell 7 during Stage 7 or earlier?
    YES -> tray_native can target net8.0-windows10.0.19041.0 with full CsWinRT
    NO  -> tray_native targets netstandard2.0, reflection approach retained

Q2: Should thumbnail extraction move into tray_native (sub-stage 5.2)?
    YES -> tray.ps1 simplifies significantly (remove ~60-line state machine)
    NO  -> 5.2 is skipped; thumbnail stays in tray.ps1

Q3: Is the existing SMTCWatcher reflection approach acceptable long-term (if
    PS7 migration is not planned), or should Stage 5 be limited to dotnet
    build migration only (skeleton + build pipeline)?
    MINIMAL -> only 5.1 + 5.4 + 5.5 (netstandard2.0, no reflection change)
    FULL    -> all 5 sub-stages with CsWinRT conversion (requires PS7)
