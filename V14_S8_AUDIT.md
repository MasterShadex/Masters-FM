# V14_S8_AUDIT.md

Stage 8 -- Build Pipeline Cleanup. Post-execution audit.
Date: 2026-05-09.

---

## 1. Scope vs Delivered

| Scope item | Target | Delivered | Status |
|---|---|---|---|
| Delete `src/tray_launcher.cs` | STEP 3 Phase 1 | `git rm src/tray_launcher.cs` (commit `81874a5`) | DONE |
| Delete `build_tools/ps2exe/_build_tray.ps1` | STEP 3 Phase 1 | `git rm` (commit `81874a5`) | DONE |
| Delete `build_tools/ps2exe/_build_spectrum.ps1` | STEP 3 Phase 1 | `git rm` (commit `81874a5`) | DONE |
| Delete `build_tools/ps2exe/ps2exe.ps1` | STEP 3 Phase 1 | `git rm` (commit `81874a5`) | DONE |
| Delete stale `dist/` subdirs (9 dirs + `dist/server.exe`) | STEP 3 Phase 1 | Removed (untracked) | DONE |
| Remove csc.exe detection block (lines 99-104) | STEP 3 Phase 1 | Removed (-5 lines) | DONE |
| Remove `[1d/5]` csc.exe compile block (lines 186-227) | STEP 3 Phase 1 | Removed (-41 lines) | DONE |
| Collapse `$UseDotnet8Server` if/else + resedit block | STEP 4 Phase 2 | Collapsed (-67 lines) | DONE |
| Collapse `$UseDotnet8Launcher` if/elseif/else | STEP 4 Phase 2 | Collapsed (-25 lines) | DONE |
| Collapse `$UseDotnet8Customize` if/else | STEP 4 Phase 2 | Collapsed (-30 lines) | DONE |
| Collapse `$UseDotnetTrayNative` if (no else) | STEP 4 Phase 2 | Wrapper removed | DONE |
| Collapse `$UseDotnet8TrayCs` if (no else) | STEP 4 Phase 2 | Wrapper removed | DONE |
| Collapse `$UseDotnet8AudioSpectrum` if/else | STEP 4 Phase 2 | Collapsed (-38 lines) | DONE |
| Keep `$UseDotnet8Bootstrapper = $false` | STEP 4 Phase 2 | KEPT (bootstrapper still disabled) | DONE |
| Fix MSB1008 on dotnet publish calls | Discovered in Phase 2 | `& dotnet` + no-spaces temp dirs (+32 lines) | DONE |

---

## 2. Line Count Progression

| Checkpoint | Lines | Delta | Commit |
|---|---:|---:|---|
| Pre-Stage-8 (end of Stage 7.10) | 593 | -- | `39e24ee` |
| Post-Phase-1 (deletions + csc.exe removal) | 547 | -46 | `81874a5` |
| Post-Phase-2 (flag flattening + MSB1008 fix) | 419 | -128 | `353b3e8` |
| **Net Stage 8** | **419** | **-174** | |

---

## 3. Remaining `$Use*` References

| Location | Content | Expected |
|---|---|---|
| Line 11 (comment) | "collapsed in Stage 8. Bootstrapper remains disabled ($UseDotnet8Bootstrapper" | YES -- header comment |
| Line 19 | `$UseDotnet8Bootstrapper = $false` | YES -- intentionally kept |
| Line 135 (comment) | "tray_launcher.cs csc.exe build + tray_native.dll csc.exe fallback removed in Stage 8 ($UseDotnet8TrayCs and $UseDotnetTrayNative permanently true)" | YES -- historical note |
| Line 354 (comment) | "install_bootstrapper.exe build DISABLED ($UseDotnet8Bootstrapper = $false at top)" | YES -- bootstrapper note |
| Lines 358, 364 (comments) | Re-enable instructions + disabled if block | YES -- bootstrapper instructions |

`$csc` references: **0** (all removed in Phase 1).

---

## 4. Protected Source File Integrity

All four protected source files unchanged throughout Stage 8.

| File | Pre-Stage-8 SHA256 | Post-Stage-8 SHA256 | Status |
|---|---|---|---|
| `src/tray.ps1` | `19011F0B...` | `19011F0BD093CEA51CB34D053209F33FB3A37DE673777BAB34B5F8F26609533F` | UNCHANGED |
| `src/tray_native/tray_native.cs` | `6B9804A1...` | `6B9804A1AB70000652A2754E886BE3F05167F40EC136EB2CC6CDD62D8EFA9148` | UNCHANGED |
| `src/launcher.cs` | `291ED4C9...` | `291ED4C92B9BEA391BA9204323EA41BA60AD7903AF6E6D7BA9404E1056E0BD9D` | UNCHANGED |
| `src/server.js` | `C15ED931...` | `C15ED9310CB33044A090878918DC2B89B3FB843901BA0F199D3092EF502A16AF` | UNCHANGED |

No protected file was touched in any Stage 8 edit.

---

## 5. Build Gate Results

### Phase 1 build gate (commit `81874a5`)
Full rebuild: **exit 0**. MSI built, signed, installed. Smoke: overlay.log 0 ERROR, heartbeat 60s, ws=155.1 MB. Protected SHA256: UNCHANGED.

### Phase 2 build gate (commit `353b3e8`)
Full rebuild: **exit 0**.

| Step | Result |
|---|---|
| server.exe | exit=0, signed |
| MastersFM.exe (launcher) | exit=0 |
| customize.exe | exit=0, signed |
| tray_native.dll | exit=0, signed |
| WPF tray | built (160 KB exe, 36845.8 KB total dist) |
| audio_spectrum.exe | exit=0, signed |
| build_msi.py | MSI (59 files, 11.7 MB compressed), signed |
| version.json | written |
| Uninstall prior | exit=0 |
| Install MSI | exit=0 |
| Launch | process launched |

MSI ProductVersion: 14.0.0 / v14.0.0-rc.1. ProductCode: {81EBEED0-5F39-486A-8DA8-D764DF9EFAC2}.

---

## 6. MSB1008 Root Cause (Stage 8 Investigation Finding)

**Bug:** All four `dotnet publish` calls (launcher, customize, tray, audio_spectrum) used `Start-Process -ArgumentList "...-o \"$path\"..."`. When the dotnet CLI invokes MSBuild, it writes all args to a temporary response file (@tempfile). The literal `"` embedded by Start-Process in the argument blob is written to the @tempfile. The MSBuild server (still running from the prior server_dotnet build) reads the @tempfile and interprets `"G:\path"` as a relative path (first char is `"`, not a drive letter), prepends CWD `G:\Project Folder\Master FM\`, producing `G:\Project Folder\Master FM\"G:\path"`. MSBuild then sees two positional project arguments and raises MSB1008.

**Fix:** Use PowerShell call operator `& dotnet publish ... -o $tempPath` with no-spaces temp output directories (`G:\lnch_pub_tmp` etc.). The call operator passes each argument as a native CLR string to the dotnet process, bypassing the single-string-with-embedded-quotes mechanism. A no-spaces path eliminates the path-splitting vulnerability entirely. Artifacts are copied from temp to the actual output directory after publish succeeds.

**Impact:** Previously `_full_rebuild.ps1` would fail if the MSBuild server was still running from the server_dotnet build (which is the normal case). The bug was masked pre-Stage-8 because the `$UseDotnet8Launcher` if/else meant the launcher publish ran in a fresh process. After flag flattening, the server build and launcher build are adjacent in the same script execution, exposing the bug.

---

## 7. Open Items After Stage 8

| Item | Source | Status |
|---|---|---|
| `$UseDotnet8Bootstrapper = $false` -- bootstrapper disabled (AV) | open_issues.md | OPEN (unchanged) |
| OBS Source Side (unfinished) | open_issues.md | OPEN (out of Stage 8 scope) |
| SIMD deferred | open_issues.md | OPEN (out of scope) |
| Memory leak (periodic GC) | open_issues.md | OPEN (out of scope) |
| Stage 9 (next): server.js port to ASP.NET Core | V14_S8_SCOPING.md / plan | PENDING |

No new open items created by Stage 8.

---

## 8. Commit Summary

| Commit | Message | Changes |
|---|---|---|
| `58f95e5` | Stage 8: STEP 2 -- deletion grep pass | V14_S8_DELETION_GREP_RESULTS.md |
| `1f88425` | Stage 8: STEP 1 -- MSI component verification (OQ-2 closed) | V14_S8_OQ2_VERIFICATION.md |
| `81874a5` | Stage 8: Phase 1 -- deletions + _full_rebuild.ps1 cleanup | 4 files deleted, _full_rebuild.ps1 -46 lines |
| `353b3e8` | Stage 8: Phase 2 -- flag flattening + fix dotnet publish MSB1008 | _full_rebuild.ps1 -128 lines |

**Stage 8 complete.**
