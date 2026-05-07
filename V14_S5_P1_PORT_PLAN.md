# V14 Stage 5 Phase 1 -- Port Plan

## 1. Stage 5 overview

### Restated goal (after inventory reframe)

The original Stage 5 description assumed tray_native.dll was a C++/CLI mixed-mode
binary. It is not -- it is already pure C#. The actual Stage 5 goals are:

PRIMARY: Migrate tray_native.dll build from csc.exe (.NET Framework 4.x) to the
dotnet SDK, enabling proper project structure, SDK tooling, and a path toward
compile-time CsWinRT projections.

SECONDARY: Convert WinRT reflection-based bindings to CsWinRT projections, which
requires also resolving the PowerShell 5.1 / .NET 8 assembly loading incompatibility.

UNCHANGED: Move the thumbnail extraction async state machine from tray.ps1 into
tray_native (C#), simplifying the PowerShell side.

### Architecture decision required (see QUESTIONS.md)

Stage 5 cannot be fully planned without resolving one user decision:
  Q1: Will tray.ps1 migrate to PowerShell 7 at or before Stage 7?

If YES: tray_native can target net8.0-windows10.0.19041.0 with full CsWinRT.
If NO: tray_native must target netstandard2.0 (no compile-time CsWinRT Windows APIs).

The sub-stages below are structured to proceed with the netstandard2.0 path
(safe, compatible, no PS7 dependency) with CsWinRT upgrades gated behind Q1.

---

## 2. Sub-stage breakdown

### Sub-stage 5.1 -- Project skeleton (2-4h)

**Goal**: Create tray_native.csproj, build tray_native.dll via dotnet build, verify
tray.ps1 loads the new DLL without error.

**Deliverables**:
- src/tray_native/ directory
- src/tray_native/tray_native.csproj (netstandard2.0 target)
- src/tray_native/tray_native.cs (moved from src/tray_native.cs, no content changes)
- Updated _full_rebuild.ps1: add dotnet build step for tray_native, keep csc.exe
  as rollback (add $UseDotnetTrayNative flag at top)
- Updated Directory.Build.props if needed (BaseIntermediateOutputPath isolation)

**Validation criteria**:
- `dotnet build src\tray_native\tray_native.csproj` exits 0
- Resulting tray_native.dll loads in PowerShell 5.1 via Add-Type -Path
- All 5 public types accessible: MFM_Shell, MFM_MenuNative, NativeMethods.GuiRes,
  MasterFM.Win32Windows, MasterFM.AudioPeak, MasterFM.SMTC.SMTCWatcher
- tray.ps1 starts without error
- SMTC watcher initializes (sessions logged)

**Risk callouts**: R4 (build pipeline), R5 (tray.ps1 binding), R7 (assembly arch)

**Hour estimate**: best=2, realistic=3, worst=5

---

### Sub-stage 5.2 -- Thumbnail extraction migration (3-5h)

**Goal**: Move thumbnail extraction from the tray.ps1 async state machine into
tray_native.dll as a synchronous C# method, simplifying tray.ps1 significantly.

NOTE: This is viable even on netstandard2.0 because thumbnail bytes can be
extracted using existing WinRT reflection approach (same as current PS code)
or via a helper method that tray.ps1 calls synchronously on a background thread.

**Deliverables**:
- New public class in tray_native.cs: `MasterFM.SMTC.SMTCThumbnail`
  - Static method: `ExtractBytes(object mediaPropertiesRcw, int timeoutMs) -> byte[]`
  - Returns raw bytes (MIME detection included) or null on failure
  - Uses Task + CancellationToken internally, presents sync API to caller
- tray.ps1 changes:
  - Remove ~60 lines of async state machine (idle/opening/loading)
  - Replace with: `$bytes = [MasterFM.SMTC.SMTCThumbnail]::ExtractBytes($snap.MediaPropertiesRcw, 500)`
  - Encode in PS: `"data:image/xxx;base64," + [Convert]::ToBase64String($bytes)`

**Validation criteria**:
- Thumbnail loading from Spotify, soundcloud-rpc sources works
- MIME detection correct (PNG default, JPEG magic bytes override)
- 500ms timeout respected (no hung ticks)
- No regression in SMTC art pipeline for server (SmtcSource.cs is independent)

**Risk callouts**: R1 (async timing), R2 (thumbnail format)

**Hour estimate**: best=3, realistic=4, worst=6

---

### Sub-stage 5.3 -- SMTC watcher cleanup and hardening (3-5h)

**Goal**: With the project on dotnet SDK, use C# language features (.NET 8 / C# 12)
to clean up SMTCWatcher code (no behavioral changes):
- Replace `System.Linq.Expressions` lambda builder with direct delegate approach
  (simpler, same behavior)
- Add nullable reference annotations (`#nullable enable`)
- Add XML doc comments to public API
- Use `lock` statement properly (current `_initLock` object is fine)
- Verify no memory leaks in Dispose path (existing logic looks correct)

NOTE: If Q1 answer is YES (PS7), this sub-stage ALSO converts reflection calls
to CsWinRT projections. If Q1 answer is NO, reflection stays but the Expression
lambda approach is simplified.

**Deliverables**:
- Cleaned-up tray_native.cs (same API surface, improved internals)
- Zero behavioral changes for tray.ps1

**Validation criteria**:
- Build exits 0 with zero warnings
- All existing SMTC behaviors pass (session tracking, event delivery, burst suppression)
- Dispose does not leak WinRT subscriptions (verified via smtc_watcher.log)

**Risk callouts**: R1 (event timing)

**Hour estimate**: best=2, realistic=3, worst=5

---

### Sub-stage 5.4 -- Build pipeline update (2-3h)

**Goal**: Update _full_rebuild.ps1 to use dotnet build for tray_native, with
clean rollback support.

**Deliverables**:
- $UseDotnetTrayNative flag at top of _full_rebuild.ps1 (default: $true)
- dotnet build path:
  ```
  [1d3/5] Building tray_native.dll (dotnet build, net8.0-windows / netstandard2.0)...
  dotnet build src\tray_native\tray_native.csproj -c Release -o . --no-self-contained
  ```
- csc.exe fallback path ($UseDotnetTrayNative = $false) preserved as rollback
- Signing step unchanged (applies to output DLL regardless of compiler)

**Validation criteria**:
- Full rebuild succeeds: `.\_full_rebuild.ps1`
- tray_native.dll output in project root (same path as before)
- DLL size comparable to current 33KB
- Signing applied correctly

**Risk callouts**: R4 (build pipeline)

**Hour estimate**: best=1, realistic=2, worst=3

---

### Sub-stage 5.5 -- Validation and side-by-side (3-5h)

**Goal**: Verify the new tray_native.dll is behaviorally equivalent to the
current csc.exe-compiled one under real conditions.

**Deliverables**:
- Smoke test script: validates all 5 type namespaces load, SMTC watcher
  initializes, track changes deliver events
- Side-by-side comparison:
  - Run with new tray_native.dll: log SMTC event counts over 15-min listen
  - Confirm event counts match prior behavior (no events lost)
- Formal validation checklist in V14_S5_VALIDATION_CHECKLIST.md

**Validation criteria** (all must pass):
- All 5 type namespaces load: MFM_Shell, MFM_MenuNative, NativeMethods.GuiRes,
  MasterFM.Win32Windows, MasterFM.AudioPeak, MasterFM.SMTC.*
- SMTCWatcher.Initialize() succeeds with real manager
- Track change from Spotify: MediaPropertiesChanged event received within 1s
- soundcloud-rpc session recycling: no duplicate events, no missed tracks
- AudioPeak.GetPeakForProcessName() returns valid float
- Win32Windows.GetAllVisibleTitles() returns non-empty list
- MFM_Shell.SetCurrentProcessExplicitAppUserModelID() sets AUMID without throw
- Thumbnail extraction: valid base64 data URI for Spotify album art
- Memory: no WorkingSet growth over 15 minutes (PS-side, not server)
- Dispose: no crash, smtc_watcher.log shows clean shutdown

**Risk callouts**: R3 (session switching), R5 (binding)

**Hour estimate**: best=3, realistic=4, worst=6

---

## 3. Inter-sub-stage dependencies

```
5.1 (skeleton) --> 5.2 (thumbnail) --> 5.3 (cleanup)
5.1 (skeleton) --> 5.4 (build pipeline)
5.2 + 5.3 + 5.4 --> 5.5 (validation)
```

5.1 must complete before all others. 5.2 and 5.4 can run in parallel after 5.1.
5.3 follows 5.2 (shares codebase). 5.5 needs all of 5.2, 5.3, 5.4.

---

## 4. Per-sub-stage spec summary

| Sub-stage | Goal | Best | Realistic | Worst |
|-----------|------|------|-----------|-------|
| 5.1 | Project skeleton | 2h | 3h | 5h |
| 5.2 | Thumbnail extraction migration | 3h | 4h | 6h |
| 5.3 | SMTC watcher cleanup | 2h | 3h | 5h |
| 5.4 | Build pipeline update | 1h | 2h | 3h |
| 5.5 | Validation + side-by-side | 3h | 4h | 6h |
| **TOTAL** | | **11h** | **16h** | **25h** |

Realistic 16h is at the low end of the V14 plan's "30-50h" estimate for Stage 5.
The discrepancy is because:
- The "C++/CLI to CsWinRT" scenario would have been 30-50h (new build toolchain,
  new runtime, new language)
- The actual scenario (C# to C# + dotnet SDK migration) is simpler
- If Q1 = YES (PS7 + full CsWinRT), add 8-15h for the reflection-to-projection
  conversion (sub-stage 5.3 expands significantly)
- V14 plan's estimate included the assumption of C++/CLI removal

---

## 5. Total hour estimate

Realistic: 16h (without CsWinRT projection conversion)
Realistic: 24-31h (with CsWinRT projection conversion, requires PS7 migration path)

---

## 6. Recommended execution order

1. **Read Q1 answer from user** (see QUESTIONS.md) -- determines TFM choice
2. Execute 5.1 (unblocks everything)
3. Execute 5.2 and 5.4 in parallel (independent after 5.1)
4. Execute 5.3 after 5.2
5. Execute 5.5 after all others pass

---

## 7. Files created / modified by Stage 5

### New files
- src/tray_native/ (directory)
- src/tray_native/tray_native.csproj
- src/tray_native/tray_native.cs (moved from src/tray_native.cs)
- V14_S5_VALIDATION_CHECKLIST.md (sub-stage 5.5)

### Modified files
- _full_rebuild.ps1 (add dotnet build step, keep csc.exe rollback)
- src/tray.ps1 (sub-stage 5.2: remove thumbnail state machine ~60 lines)
- Directory.Build.props (possibly: add tray_native project to obj path isolation)

### Deleted files
- src/tray_native.cs (replaced by src/tray_native/tray_native.cs)

### Output (unchanged location)
- tray_native.dll at project root (same as today)

---

## 8. Rollback plan

At each sub-stage, a rollback flag in _full_rebuild.ps1 ($UseDotnetTrayNative)
reverts to the csc.exe build if dotnet build has issues. The csc.exe path is
kept until Stage 5 is fully validated and signed off.

tray.ps1 changes in 5.2 are the riskiest rollback point -- a git revert of the
thumbnail state machine removal restores the previous behavior fully.
