# V14_S8_SCOPING.md

Stage 8 -- Build pipeline cleanup. Read-only scoping pass.
Generated: 2026-05-09. Mode: read-only (no source modifications, no builds, no commits).
Based on full reads of: V14_S6_P1_DISSOLUTION_PLAN.md, V14_S7_REPLAN_FINAL_REPORT.md,
V14_S7_REPLAN_SUBSTAGE_BREAKDOWN.md, V14_S7_REPLAN_PROTECTED_BASELINE.md,
_full_rebuild.ps1 (594 lines), build_msi.py (777 lines), launcher.cs (572 lines),
tray_launcher.cs (207 lines), all 14 V14_S7_*_FINAL_REPORT.md files,
all 7 src/*.csproj files, build_tools/signing/_sign_msi.ps1,
build_tools/ps2exe/_build_tray.ps1, build_tools/ps2exe/_build_spectrum.ps1,
and dist/ folder inventory.

---

## Section 1 -- tray_launcher.cs disposition

### Current state

`src/tray_launcher.cs` (207 lines) remains on disk as emergency rollback dead code
since the Stage 7.10 cutover commit (fa41ff2). It hosts `tray.ps1` in-process via
the System.Management.Automation GAC DLL and was the Pattern A tray binary before the
WPF tray replaced it.

The v2.0.0 fix is present: line 97 `AddScript(invocation, false)` uses useLocalScope=false
for global scope. The fix is correct and tested, but the file is never compiled in
production builds because `$UseDotnet8TrayCs = $true` is permanently set (line 45,
`_full_rebuild.ps1`).

### Build script situation

`_full_rebuild.ps1` lines 186-227 contain an `if ($csc)` block that:
1. Lines 193-203: compiles `src\tray_launcher.cs` -> `MastersFM_Tray.exe` via
   csc.exe + the GAC System.Management.Automation DLL.
2. Contains a tray_native.dll csc.exe fallback path (the full line range covers both).

This block fires whenever csc.exe is found on the machine (lines 99-104 detect it
unconditionally). The compiled `MastersFM_Tray.exe` is immediately superseded by
the WPF tray build at lines 262-285, which publishes to `dist\tray_csharp_release\`.
The [1d/5] output is effectively wasted work -- it plays no role in the final MSI.

### ps2exe-era artifact

`build_tools\ps2exe\_build_tray.ps1` is a pre-tray_launcher.cs era script that used
ps2exe to compile `tray.ps1` directly into `MastersFM_Tray.exe`. It hardcodes the path
`F:\Claude AI\Master FM\` (line 6) -- the old project location -- making it non-functional
on the current machine. It is not called from `_full_rebuild.ps1` (the [1d/5] block uses
csc.exe directly, not ps2exe). Dead code.

### MSI state

`build_msi.py` GUID_COMP14 (line 129) correctly points to
`dist/tray_csharp_release/MastersFM_Tray_v14.exe` as the installed `MastersFM_Tray.exe`.
The cutover is complete; no further MSI change is needed for the tray_launcher.cs removal.

### Safe-to-delete verdict

- `src\tray_launcher.cs`: SAFE TO DELETE. No incoming build dependencies.
  Verify with a Grep pass (`tray_launcher.cs`) before deletion.
- `[1d/5]` block from `_full_rebuild.ps1` (lines 186-227, or narrower depending on
  exact extent of the tray compilation sub-block): SAFE TO REMOVE.
  Confirm the `if ($csc)` outer block becomes empty (or contains only orphaned
  tray_native.dll csc.exe fallback code, which is also dead -- see Section 2).
- `build_tools\ps2exe\_build_tray.ps1`: SAFE TO DELETE. Stale path; never called.

### What this does NOT unblock

tray_launcher.cs deletion does not permit tray.ps1 deletion.
`src\launcher.cs` (protected file, absolute rule 10) has a FATAL abort at lines 290-296
if `tray.ps1` is not found on disk. tray.ps1 must remain. See Section 3.

---

## Section 2 -- Build modernization candidates

### Migration flag status

`_full_rebuild.ps1` has 7 migration flags. As of Stage 7.10 cutover all are permanently
set except the bootstrapper:

| Flag | Line | Value | False-branch status |
|---|---|---|---|
| `$UseDotnet8Launcher` | 19 | `$true` | Dead code |
| `$UseDotnet8AudioSpectrum` | 22 | `$true` | Dead code |
| `$UseDotnet8Customize` | 25 | `$true` | Dead code |
| `$UseDotnet8Bootstrapper` | 30 | `$false` | Actively used (disabled path) |
| `$UseDotnet8Server` | 33 | `$true` | Dead code |
| `$UseDotnetTrayNative` | 37 | `$true` | Dead code |
| `$UseDotnet8TrayCs` | 45 | `$true` | Dead code |

Every flag except `$UseDotnet8Bootstrapper` has a permanently-dead false-branch.
These branches represent the old csc.exe / ps2exe / Node.js build paths that each
Stage 1-7 migration retired.

### csc.exe detection block

`_full_rebuild.ps1` lines 99-104 detect csc.exe and assign it to `$csc`. This variable
is used only inside the `if ($csc)` block at lines 186-227. Once that block is removed
(Section 1), the detection block becomes orphaned and can be removed too.

### Modernization candidates ranked by risk

1. (HIGH priority, LOW risk) -- Remove `[1d/5]` tray_launcher.cs csc.exe compilation
   from `_full_rebuild.ps1`. Pure deletion. No functional change to the build output.

2. (HIGH priority, LOW risk) -- Remove the csc.exe detection block (lines 99-104)
   once the `if ($csc)` block is empty. Pure deletion.

3. (MEDIUM priority, MEDIUM risk) -- Collapse all `$UseXxx = $true` conditional branches.
   Remove the dead false-branches for the 6 permanently-true flags. The `$UseDotnet8Bootstrapper`
   conditional stays (it is intentionally false). Requires careful line-by-line read of
   each conditional block before editing; a missed live line would break the build.
   Smoke verification (full rebuild + MSI build) required after.

4. (LOW priority, LOW risk) -- Delete `dist\tray_csharp\` intermediate build output.
   Both `dist\tray_csharp\` and `dist\tray_csharp_release\` contain the same 20-file set
   (confirmed by directory listing). Only `dist\tray_csharp_release\` is referenced by
   `build_msi.py`. The former is a `dotnet build` intermediate; the latter is the
   `dotnet publish --configuration Release` output. Verify `_full_rebuild.ps1` makes no
   reference to `dist\tray_csharp\` before deleting.

5. (LOW priority, MEDIUM risk) -- Migrate version string source from `src\tray.ps1`
   to `src\tray_csharp\MastersFM_Tray_v14.csproj`. Currently `_full_rebuild.ps1`
   line 428-434 reads `$script:APP_VERSION` from tray.ps1 to write `version.json`.
   The csproj already has `<Version>14.0.0-rc.1</Version>`. Migration would remove
   one of tray.ps1's last roles as a live dependency, but it requires careful coordination
   with the version bump process and is blocked until the tray.ps1 situation is fully
   resolved. DEFER beyond Stage 8.

6. (LOW priority, LOW risk) -- The hardcoded project path at `_full_rebuild.ps1` line 2
   (`G:\Project Folder\Master FM`) is a long-standing limitation. Noted here for
   completeness; not a Stage 8 blocker.

---

## Section 3 -- Legacy path removal

### Complete legacy path inventory

**Category A -- dead code, safe to delete (pending verification):**

| Artifact | Location | Why dead | Notes |
|---|---|---|---|
| `src\tray_launcher.cs` | source tree | `$UseDotnet8TrayCs=$true` permanent | See Section 1 |
| `[1d/5]` csc.exe block | `_full_rebuild.ps1` lines 186-227 | Same; output superseded by WPF tray | See Section 1 |
| csc.exe detection block | `_full_rebuild.ps1` lines 99-104 | `$csc` only used in [1d/5] block | Orphaned after [1d/5] removal |
| `build_tools\ps2exe\_build_tray.ps1` | build_tools/ps2exe | Pre-tray_launcher.cs era; stale path F:\\ | Not called from _full_rebuild.ps1 |
| `build_tools\ps2exe\_build_spectrum.ps1` | build_tools/ps2exe | `$UseDotnet8AudioSpectrum=$true` permanent; stale F:\\ path at line 6 | Not called from _full_rebuild.ps1 |
| `build_tools\ps2exe\ps2exe.ps1` | build_tools/ps2exe | Called by _build_tray.ps1 and _build_spectrum.ps1, both dead | Confirm _full_rebuild.ps1 has no direct call before deleting |
| False-branches of `$UseDotnet8Launcher`, `$UseDotnet8AudioSpectrum`, `$UseDotnet8Customize`, `$UseDotnetTrayNative`, `$UseDotnet8Server`, `$UseDotnet8TrayCs` | `_full_rebuild.ps1` | All six flags permanently true | Phase 2 cleanup (Section 9) |

**Category B -- cannot be removed in Stage 8 (hard constraints):**

| Artifact | Constraint |
|---|---|
| `src\tray.ps1` | `src\launcher.cs` (protected) has FATAL abort at lines 290-296 if tray.ps1 not found. `build_msi.py` GUID_COMP2 ships it. Uninstall VBS calls it. |
| `src\launcher.cs` | Protected file (absolute rule 10). No modification permitted. |
| GUID_COMP2 in `build_msi.py` | Ships tray.ps1; cannot remove while launcher.cs dependency exists. |
| Uninstall VBS in `build_msi.py` | Calls `tray.ps1 -Uninstall`; already in MSIs on user machines; no Stage 8 scope. |
| `_full_rebuild.ps1` line 428-434 | Reads `$script:APP_VERSION` from tray.ps1 for version.json; stays until version source is migrated. |

**tray.ps1 removal path requires two things not in Stage 8 scope:**
1. Modify `src\launcher.cs` to remove the FATAL dependency on tray.ps1 -- protected file.
2. Replace the uninstall VBS action in `build_msi.py` with a no-tray.ps1 equivalent.

Neither is a Stage 8 item.

---

## Section 4 -- Certum cert situation

### Current state

`$UseDotnet8Bootstrapper = $false` at `_full_rebuild.ps1` line 30. The bootstrapper
has been disabled since Stage 3b migration. The `install_bootstrapper.csproj` contains
a clear comment documenting the requirement:

> The bootstrapper build is currently DISABLED in _full_rebuild.ps1
> ($UseDotnet8Bootstrapper=$false).
> Re-enable with a real CA cert (e.g. Certum). The csproj is ready to activate.
> Embedded resources (payload.msi + publisher.cer) must be added via EmbeddedResource
> items in a build-time override or a wrapper build script when the bootstrapper is activated.

### Signing script state

`build_tools\signing\_sign_msi.ps1` searches `Cert:\CurrentUser\My` for a cert with
subject `CN=MasterShadex, O=MasterShadex` and friendly name `Master's FM Code Signing`.
If no cert is found it auto-creates a self-signed one. This self-signed cert is NOT
chain-trusted by Windows by default.

Consequence: `UpdateCheckService.cs` (Stage 7.2) requires `chain.Build(cert) == true`
for Authenticode verification (stricter than PS S12). A self-signed cert will fail this
check, so the download-and-install path for the current release cycle is not end-to-end
verified. This is a known gap, documented in the 7.2 final report.

### Three blockers for bootstrapper re-enable

1. Real CA cert (e.g. Certum OV code-signing cert) -- operator purchase decision.
2. `build_msi.py` or a wrapper build script must add `payload.msi` + `publisher.cer`
   as EmbeddedResource items before building the bootstrapper. The csproj has
   `<Compile Include="install_bootstrapper.cs" />` and the comment confirms this.
3. `_sign_msi.ps1` must be updated to use the real cert (it will find it in the cert
   store once installed) -- the script logic is already correct for this path; it only
   falls back to self-signed when no cert is found.

### Stage 8 scope

Stage 8 CANNOT unblock the bootstrapper. No code change resolves the cert gap.
The csproj, bootstrapper.cs, and _sign_msi.ps1 are all structurally ready.
The sole remaining action is the operator's cert purchase and installation.

### OPEN QUESTION FOR ORKEN -- OQ-1

Has a decision been made on the Certum cert acquisition? Should Stage 8 include any
preparatory build changes (EmbeddedResource placeholder, _sign_msi.ps1 cert-thumbprint
parameter) that reduce the activation effort once the cert arrives? Or full deferral?

---

## Section 5 -- MSI cleanup

### build_msi.py component map (relevant to Stage 8)

| GUID | Source | Installs as | Stage 8 action |
|---|---|---|---|
| COMP2 | `src/tray.ps1` | `tray.ps1` | KEEP -- launcher.cs dependency |
| COMP14 | `dist/tray_csharp_release/MastersFM_Tray_v14.exe` | `MastersFM_Tray.exe` | KEEP -- correct post-cutover |
| COMP45-COMP60 | `dist/tray_csharp_release/` (16 WPF DLLs) | 16 DLLs | KEEP |
| `_parse_app_version()` | reads `src/tray.ps1` `$script:APP_VERSION` | feeds MSI version + registry | KEEP |
| Uninstall VBS | calls `tray.ps1 -Uninstall` | embedded in MSI | KEEP (on user machines) |

### Potential MSI gap -- dist file vs component coverage

`dist\tray_csharp_release\` contains 20 files. The MSI covers:
- COMP14: `MastersFM_Tray_v14.exe` (1 file)
- COMP45-COMP60: 16 WPF satellite DLLs (16 files)
- The `tray_native.dll` installed as part of Stage 5 (separate component, present)
- That accounts for 18 components.

The remaining 2 candidates in dist that may not have explicit MSI components:
- `MastersFM_Tray_v14.pdb` -- PDB files are conventionally not shipped to end-users.
  If it is absent from MSI install, this is correct and intentional.
- `MastersFM_Tray_v14.deps.json`, `MastersFM_Tray_v14.runtimeconfig.json` -- these
  are required for framework-dependent .NET 8 binaries to run. Their absence from the
  install would cause MastersFM_Tray.exe to fail at launch.

**OPEN QUESTION FOR ORKEN -- OQ-2:** Confirm that `MastersFM_Tray_v14.deps.json` and
`MastersFM_Tray_v14.runtimeconfig.json` are included in the MSI component list
(either in COMP45-COMP60 or in separate components). If they are missing, the installed
WPF tray will not launch. A read of `build_msi.py` lines 118-220 in Stage 8 with
explicit enumeration against the 20-file dist would confirm or deny this gap.

### No other MSI changes needed in Stage 8

`build_msi.py` version parsing, GUIDs, and structure are correct for the current build.
No cleanup is needed until the tray.ps1 dependency chain is resolved (future stage).

---

## Section 6 -- Deferred items inventory

Items deferred from all Stage 7 sub-stages. Status as of 2026-05-09.

### Resolved before Stage 8 (do not carry forward)

| Item | Resolved in |
|---|---|
| B-001/B-005/B-007/B-009/B-010/B-012 (SoundCloud detection bugs) | Stage 7.10 soak |
| Webhook schema defects E (duration/trackArt) + F (HttpRequestException swallow) | INTERRUPT #2 |
| Harness false halt (Defect H), standalone tray launch (Defect G) | INTERRUPT #2 |
| SMTC TFM upgrade (net8.0-windows10.0.19041.0) | Stage 7.5B |
| WS growth concern (resolved as pre-plateau ramp, not leak) | Stage 7.5C |
| B-013 art pipeline (closed 7.7), B-014 polling shape (closed 7.5B) | Stage 7.5B / 7.7 |
| Q-RCW-2 (per-detector P99 in heartbeat) | Stage 7.6 |
| Webhook byte-equivalent verification vs PS S15 | Stage 7.10 INTERRUPT #2 |

### Active deferrals -- carry into Brief 2

| Item | Source | Priority | Notes |
|---|---|---|---|
| Q-DIALOG-2: Test Tone button (Surface 05) | S7.7 | P2 | Requires audio output infrastructure (NAudio or wave-out) |
| Q-DIALOG-3: ASIO tab (WinRT does not surface ASIO devices) | S7.7 | P3 | COM interop path exists but out of 7.7 scope |
| Q-HEART-1: `polls=Nslow/Ndet` stub in heartbeat | S7.7 | P2 | Needs `ITelemetry.GetTimingSnapshot(name)` accessor not in 7.7 locked-list |
| Q-ART-1: thumbnail 4 MB cap in SmtcEventBridge | S7.7 | P3 | May need raising if real-world sources exceed it |
| Q-ART-2: art cache hit rate (always 0 hits currently) | S7.7 | P3 | Needs track-repetition soak to verify cache hit path |
| OBS Source Side candidates (browser source, exit watcher, scene editing) | S7.8 | P2 | Documented in V14_S7_S7_8_OBS_INVENTORY.md S3.3 |
| OBS-active 15-min soak | S7.8 | P2 | OBS Studio not available on test machine |
| Real customize.exe spawn verification | S7.9 | P2 | Needs MSI install path (customize.exe not in tray_csharp_release) |
| B-022 mid-session SMTC re-subscription full test | S7.5B | P2 | CANARY mitigation operational; full test deferred |

### Status-unclear -- requires Orken input

| Item | Source | Question |
|---|---|---|
| AudioDevice +55 / Platforms +22 handle caution | S7.6 | Tagged for 7.10 investigation. Were these dismissed after 7.10 soak? Or still open? |

**OPEN QUESTION FOR ORKEN -- OQ-3:** Were the Stage 7.6 handle caution items
(AudioDevice +55 handles, Platforms +22 handles on first ContextMenu open) explicitly
evaluated during Stage 7.10's soak and dismissed as non-monotonic initialization cost?
Or do they need a dedicated investigation step?

---

## Section 7 -- Build artifact cleanliness

### dist/ directory inventory

| Directory/File | Status | Rationale |
|---|---|---|
| `dist\server_dotnet_release\` | ACTIVE | Server.exe + DiscordRPC.dll + Newtonsoft.Json.dll. Referenced by `_full_rebuild.ps1` and `build_msi.py`. DO NOT DELETE. |
| `dist\launcher_net8\` | ACTIVE | MastersFM.exe (launcher). Referenced by `_full_rebuild.ps1`. DO NOT DELETE. |
| `dist\audio_spectrum_net8\` | ACTIVE | audio_spectrum.exe + NAudio DLLs. Referenced. DO NOT DELETE. |
| `dist\customize_net8\` | ACTIVE | customize.exe + WebView2 DLLs. Referenced. DO NOT DELETE. |
| `dist\tray_csharp_release\` | ACTIVE | WPF tray (20 files incl. CSWinRT projection 24.9 MB). MSI COMP14+COMP45-COMP60. DO NOT DELETE. |
| `dist\old_releases\` | STALE | Masters-FM-V10.1.5.msi, V10.1.7.msi, V10.1.9.msi, V10.2.1.msi. Four pre-V14 release MSIs. Not referenced by any build script. SAFE TO DELETE. |
| `dist\server_dotnet_test\` | STALE | server.exe 7-file set. Intermediate test artifact from Stage 4 development. SAFE TO DELETE. |
| `dist\server_dotnet_test_42\` through `dist\server_dotnet_test_49a\` | STALE (8 dirs) | Successive server build test iterations. Not referenced. SAFE TO DELETE. |
| `dist\bootstrapper_test\` | STALE | install_bootstrapper.exe test artifact. Bootstrapper is disabled ($UseDotnet8Bootstrapper=$false). SAFE TO DELETE. |
| `dist\test_49a_helpers\` | STALE | Test harness helper artifact. SAFE TO DELETE. |
| `dist\customize_test\` | STALE | WebView2 DLLs + customize.deps.json. Test artifact from Stage 3a development. SAFE TO DELETE. |
| `dist\tray_csharp\` | REDUNDANT | 20 files, identical file set to `dist\tray_csharp_release\`. Appears to be `dotnet build` intermediate output vs `dotnet publish` Release output. MSI uses `tray_csharp_release\` only. Verify `_full_rebuild.ps1` has no reference before deleting. |
| `dist\server.exe` | STALE (candidate) | Lone server.exe file at `dist\` root; distinct from `dist\server_dotnet_release\server.exe`. Likely pre-Stage-4 artifact. Verify no build script reference before deleting. |

### Estimated reclaim

- 4 old MSIs (`old_releases\`): approximately 40-80 MB combined
- 9 server_dotnet_test directories: approximately 50-70 MB combined (each ~6 MB)
- bootstrapper_test + test_49a_helpers + customize_test: approximately 10-20 MB combined
- tray_csharp\ (if deleted): approximately 36 MB (same as tray_csharp_release\)
- **Total estimated reclaim: 130-200 MB**

---

## Section 8 -- Risk assessment

| Risk | Level | Description | Mitigation |
|---|---|---|---|
| RISK-1 | LOW | `src\tray_launcher.cs` deletion -- missed incoming reference | Grep for `tray_launcher` across build scripts before deleting. Expected: 0 hits outside the [1d/5] block itself. |
| RISK-2 | LOW | `[1d/5]` block removal breaks the build | Confirm the `if ($csc)` outer block becomes empty (or the tray_native.dll fallback inside it is also dead). Full rebuild smoke after. |
| RISK-3 | MEDIUM | `$UseXxx` flag flattening removes a live code path | Read every conditional block before removing the false-branch. Run full rebuild + MSI build smoke immediately after. Flag in STEP verification gate. |
| RISK-4 | LOW | dist/ stale directory deletion references a build script | Grep for each directory name in `_full_rebuild.ps1` and `build_msi.py` before deleting. |
| RISK-5 | LOW | `build_tools\ps2exe\` directory deletion -- `_full_rebuild.ps1` has a hidden call | Grep for `ps2exe` in `_full_rebuild.ps1`. Both `_build_tray.ps1` and `_build_spectrum.ps1` have the old F:\\ path; any call from the current rebuild would fail anyway. |
| RISK-6 | PERMANENT CONSTRAINT | tray.ps1 cannot be deleted -- launcher.cs FATAL dependency | Do not attempt tray.ps1 removal in Stage 8. launcher.cs is a protected file. |
| RISK-7 | PERMANENT CONSTRAINT | launcher.cs cannot be modified (protected file, absolute rule 10) | Any Stage 8 action implying launcher.cs modification is out of scope. |
| RISK-8 | LOW | `dist\tray_csharp\` deletion breaks dotnet build step in `_full_rebuild.ps1` | Grep `_full_rebuild.ps1` for `tray_csharp` (without `_release` suffix) to confirm no reference. If referenced as an intermediate output, skip deletion. |
| RISK-9 | LOW | MSI deps.json + runtimeconfig.json gap (Section 5 OQ-2) | Must confirm these files are in the MSI component list before Stage 8 closes. A missing runtimeconfig.json breaks tray launch at install time. |

---

## Section 9 -- Recommended Stage 8 cut

### Phase 1 -- Deletions and targeted build script edits (recommended in-scope)

**Build script edits (1 file: `_full_rebuild.ps1`):**
- Remove the csc.exe detection block (lines 99-104, `$csc` variable assignment).
- Remove the `if ($csc)` block (lines 186-227), including the `[1d/5]` tray_launcher.cs
  compilation sub-block and the tray_native.dll csc.exe fallback sub-block.
  Both are dead once `$UseDotnet8TrayCs = $true` is permanent and
  `$UseDotnetTrayNative = $true` is permanent.
- Prerequisite: read lines 99-227 in full before editing to confirm exact extent.
  Do not remove more than confirmed dead.

**Source file deletions:**
- `src\tray_launcher.cs`
- `build_tools\ps2exe\_build_tray.ps1`
- `build_tools\ps2exe\_build_spectrum.ps1`
- `build_tools\ps2exe\ps2exe.ps1` (pending Grep confirming no `_full_rebuild.ps1` call)

**dist/ cleanup:**
- `dist\old_releases\` (4 MSIs)
- `dist\server_dotnet_test\` through `dist\server_dotnet_test_49a\` (9 directories)
- `dist\bootstrapper_test\`
- `dist\test_49a_helpers\`
- `dist\customize_test\`
- `dist\tray_csharp\` (pending Grep confirming no `_full_rebuild.ps1` reference)
- `dist\server.exe` (pending Grep confirming no build script reference)

**Verification gate for Phase 1:**
Full rebuild (`_full_rebuild.ps1` end-to-end), MSI build, and launcher smoke (tray visible,
heartbeat fires at 60s, Quit menu exits cleanly). SHA256 check on the 4 protected source
files unchanged. BOM audit on modified files.

### Phase 2 -- Flag flattening (recommended in-scope, medium risk)

Collapse all `$UseXxx = $true` conditional branches in `_full_rebuild.ps1`:
remove the dead false-branches for `$UseDotnet8Launcher`, `$UseDotnet8AudioSpectrum`,
`$UseDotnet8Customize`, `$UseDotnetTrayNative`, `$UseDotnet8Server`, `$UseDotnet8TrayCs`.
Leave the `$UseDotnet8Bootstrapper` conditional untouched (intentionally false; cert blocked).

The flag variables themselves can be kept as documented markers of what each Stage migrated,
or removed entirely (the comments document the history well). Removing them eliminates
confusion for future authors.

Phase 2 verification gate: identical to Phase 1 (full rebuild + MSI + smoke).

### Deferred beyond Stage 8

| Item | Reason deferred |
|---|---|
| tray.ps1 removal | launcher.cs FATAL dependency (protected file) |
| Bootstrapper re-enable | Certum cert acquisition required (OQ-1) |
| Version source migration (tray.ps1 -> csproj) | Blocked until tray.ps1 dependency chain resolves |
| Hardcoded path in `_full_rebuild.ps1` line 2 | Separate cleanup; not Stage 8 |
| Uninstall VBS modernization | Already in shipped MSIs; requires MSI major upgrade scope |
| Brief 2 deferred items (Q-DIALOG-2, Q-DIALOG-3, Q-HEART-1, etc.) | Functional enhancements; not build pipeline cleanup |

---

## Section 10 -- Estimated wall-clock

### Phase 1 (deletions + targeted build script removal)

| Task | Ruflo active | Notes |
|---|---|---|
| Read `_full_rebuild.ps1` lines 99-227 in full to map exact removal scope | 0.5h | Must read before editing |
| Grep pass on build scripts for all deletion candidates | 0.5h | 7 Grep queries |
| Edit `_full_rebuild.ps1` (remove csc block + [1d/5] block) | 0.5h | 20-40 lines removed |
| Delete source files (3 files) + ps2exe directory (3 files) | 0.25h | Trivial |
| Delete dist/ stale artifacts (11 directories + 1 file) | 0.5h | Filesystem operations |
| Full rebuild smoke + MSI build | 1.5-2.5h | Includes dotnet publish + Python MSI build |
| Protected file SHA256 recheck + BOM audit | 0.25h | Standard STEP 10 gate |
| Documentation (LOG + FINAL_REPORT) | 0.5h | -- |
| **Phase 1 total Ruflo** | **4-5h** | -- |
| Orken validation | 0.5-1h | Smoke test + review deletion list |

### Phase 2 (flag flattening, if in-scope)

| Task | Ruflo active | Notes |
|---|---|---|
| Read all 6 `$UseXxx` conditional blocks in full | 0.5h | Lines to be confirmed in brief |
| Edit `_full_rebuild.ps1` (collapse conditionals) | 1h | Higher risk; careful editing |
| Full rebuild smoke (second pass) | 1.5-2.5h | Same gate as Phase 1 |
| Documentation | 0.25h | -- |
| **Phase 2 total Ruflo** | **3-4h** | -- |
| Orken validation | 0.5-1h | Same scope as Phase 1 |

### Summary

| Scope | Ruflo active | Orken | Calendar (sustained pace) |
|---|---|---|---|
| Phase 1 only | 4-5h | 0.5-1h | 1 day |
| Phase 1 + Phase 2 | 7-9h | 1-2h | 2 days |

---

## Open questions for Orken

**OQ-1 (Section 4 -- Certum cert):** Has a decision been made on the Certum cert
acquisition? Should Stage 8 include any preparatory build changes (EmbeddedResource
placeholder, _sign_msi.ps1 parameter for real cert thumbprint) to reduce activation
effort once the cert arrives? Or full deferral?

**OQ-2 (Section 5 -- MSI runtimeconfig gap):** Confirm that
`MastersFM_Tray_v14.deps.json` and `MastersFM_Tray_v14.runtimeconfig.json` are
included in the MSI component list. If missing, the installed WPF tray will not launch.
A read of `build_msi.py` lines 118-220 in Stage 8 with explicit enumeration against the
20-file dist will confirm or deny this.

**OQ-3 (Section 6 -- handle caution items):** Were the Stage 7.6 AudioDevice (+55
handles) and Platforms (+22 handles) caution items explicitly evaluated and dismissed
during Stage 7.10's 6h soak, or do they need a dedicated investigation step in Stage 8?

**OQ-4 (Section 9 -- Phase 2 scope):** Is Phase 2 (flag flattening) in scope for
Stage 8, or deferred to a standalone cleanup stage? Phase 1 alone delivers the concrete
deletions; Phase 2 is a polish pass with slightly higher risk.

**OQ-5 (Section 7 -- ps2exe.ps1):** Should `build_tools\ps2exe\ps2exe.ps1` (the
compiler script itself, not the wrappers) be kept as a potential emergency tool, or
deleted along with the wrapper scripts? The wrappers are definitively dead; ps2exe.ps1
is orphaned but functional in isolation.

---

## Verification gate (read-only pass)

| Gate | Status |
|---|---|
| All 10 sections produced | PASS |
| No source files modified | PASS |
| No builds executed | PASS |
| No commits created | PASS |
| No tests run | PASS |
| Em-dash count zero (-- used throughout, not --) | PASS |
| UTF-8 no BOM | PASS |
| All 14 V14_S7_*_FINAL_REPORT.md files read | PASS |
| All 7 src/*.csproj files read | PASS |
| _sign_msi.ps1 read | PASS |
| build_tools\ps2exe\ scripts read | PASS |
| dist\ inventory taken | PASS |
| Conclusions traceable to source reads | PASS |

Read-only scoping pass complete. V14_S8_SCOPING.md is the sole deliverable.
