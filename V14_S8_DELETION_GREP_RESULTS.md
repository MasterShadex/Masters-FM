# V14_S8_DELETION_GREP_RESULTS.md

Stage 8 STEP 2 -- Deletion candidate grep pass (read-only diligence).
Date: 2026-05-09.
No deletions in this step. This is the pre-deletion safety check.

---

## Methodology

For each candidate, ran:
1. `Select-String -Path "_full_rebuild.ps1" -Pattern "<candidate>" -SimpleMatch`
2. `Select-String -Path "build_tools\build_msi.py" -Pattern "<candidate>" -SimpleMatch`
3. `Get-ChildItem "build_tools" -Recurse -File | Select-String -Pattern "<candidate>" -SimpleMatch`

---

## Results

### Candidate 1: `tray_launcher.cs`

| Source | Hits | Line(s) |
|---|---|---|
| `_full_rebuild.ps1` | 1 | Line 198: `/reference:$sma src\tray_launcher.cs` |
| `build_tools\build_msi.py` | 0 | -- |
| `build_tools\*` (recursive) | 0 | -- |

**Classification: SAFE.** The single hit in `_full_rebuild.ps1` is at line 198, inside the `if ($csc)` block (lines 186-227), specifically in the `[1d/5]` csc.exe compilation call for `tray_launcher.cs`. This entire block is a Phase 1 deletion target. After Phase 1, the reference will no longer exist.

---

### Candidate 2: `tray_launcher` (no .cs extension)

| Source | Hits | Line(s) |
|---|---|---|
| `_full_rebuild.ps1` | 1 | Line 198 (same hit as above) |
| `build_tools\build_msi.py` | 0 | -- |
| `build_tools\*` (recursive) | 0 | -- |

**Classification: SAFE.** Same single hit as Candidate 1. Inside the [1d/5] block being deleted.

---

### Candidate 3: `_build_tray.ps1`

| Source | Hits | Line(s) |
|---|---|---|
| `_full_rebuild.ps1` | 0 | -- |
| `build_tools\build_msi.py` | 0 | -- |
| `build_tools\*` (recursive) | 0 | -- |

**Classification: SAFE.** Zero hits. No incoming references from any build script.

---

### Candidate 4: `_build_spectrum.ps1`

| Source | Hits | Line(s) |
|---|---|---|
| `_full_rebuild.ps1` | 2 | Line 21 (comment), Line 321 (path join), Line 328 (WARN message) |
| `build_tools\build_msi.py` | 0 | -- |
| `build_tools\*` (recursive) | 0 | -- |

**Hit detail from `_full_rebuild.ps1`:**
- Line 21: `# RC1 rollback path: set $false to revert to legacy csc.exe via build_tools\ps2exe\_build_spectrum.ps1.`
- Line 321: `$spec = Join-Path $root 'build_tools\ps2exe\_build_spectrum.ps1'`
- Line 328: `L "  WARN: _build_spectrum.ps1 not found - audio_spectrum.exe NOT built"`

These three lines are all inside the `else { # Legacy csc.exe rollback }` block under `if ($UseDotnet8AudioSpectrum)` (lines 319-330). Since `$UseDotnet8AudioSpectrum = $true` is permanent, this else-branch is NEVER executed.

**Classification: SAFE.** All hits are inside the dead `$UseDotnet8AudioSpectrum` false-branch. Phase 2 will collapse this block entirely. Deleting `_build_spectrum.ps1` in Phase 1 leaves a dangling reference in dead code, but since the dead code is never executed AND Phase 2 removes it, this is safe.

---

### Candidate 5: `ps2exe.ps1`

| Source | Hits | Line(s) |
|---|---|---|
| `_full_rebuild.ps1` | 2 | Lines 21, 321, 328 (via `_build_spectrum.ps1` references -- same dead block) |
| `build_tools\build_msi.py` | 0 | -- |
| `build_tools\ps2exe\ps2exe.ps1` | many | Self-references (file being deleted) |
| `build_tools\ps2exe\_build_tray.ps1` | 3 | Lines 1, 7, 11 (internal references, file being deleted) |

**Classification: SAFE.** The `_full_rebuild.ps1` hits are the same dead false-branch references as Candidate 4. All other hits are self-referential within the ps2exe directory files that are all being deleted. Zero hits in build_msi.py.

---

### Candidate 6: `ps2exe` (any reference, outside build_tools\ps2exe\)

| Source | Hits | Line(s) |
|---|---|---|
| `_full_rebuild.ps1` | 3 | Lines 21, 321, 328 (dead false-branch, same as Candidate 4-5) |
| `build_tools\build_msi.py` | 0 | -- |
| `build_tools\*` (recursive, outside ps2exe dir) | 0 | -- |

**Classification: SAFE.** All hits outside `build_tools\ps2exe\` itself are in the dead false-branch of `_full_rebuild.ps1`. Zero hits in build_msi.py.

---

### Candidate 7: `dist\old_releases`

| Source | Hits |
|---|---|
| `_full_rebuild.ps1` | 0 |
| `build_tools\build_msi.py` | 0 |
| `build_tools\*` (recursive) | 0 |

Directory contains 4 old MSIs:
- `Masters-FM-V10.2.1.msi` (12,959,744 bytes, 05/02)
- `Masters-FM-V10.1.9.msi` (12,955,648 bytes, 05/02)
- `Masters-FM-V10.1.7.msi` (12,955,648 bytes, 05/02)
- `Masters-FM-V10.1.5.msi` (12,906,496 bytes, 05/02)

**Classification: SAFE.** Zero hits. No build script references. Old MSI archive with no production role.

---

### Candidate 8: `dist\server_dotnet_test` (and all numbered variants)

| Source | Hits |
|---|---|
| `_full_rebuild.ps1` | 0 |
| `build_tools\build_msi.py` | 0 |
| `build_tools\*` (recursive) | 0 |

Directories targeted:
- `dist\server_dotnet_test\`
- `dist\server_dotnet_test_42\`
- `dist\server_dotnet_test_43\`
- `dist\server_dotnet_test_44\`
- `dist\server_dotnet_test_45\`
- `dist\server_dotnet_test_47\`
- `dist\server_dotnet_test_48\`
- `dist\server_dotnet_test_49a\`

**Classification: SAFE.** Zero hits. These are staging/test build directories from .NET server development iterations. The active server build outputs to `dist\server_dotnet_release\`.

---

### Candidate 9: `dist\bootstrapper_test`

| Source | Hits |
|---|---|
| `_full_rebuild.ps1` | 0 |
| `build_tools\build_msi.py` | 0 |
| `build_tools\*` (recursive) | 0 |

**Classification: SAFE.** Zero hits. Bootstrapper development test directory; bootstrapper disabled (`$UseDotnet8Bootstrapper = $false`).

---

### Candidate 10: `dist\test_49a_helpers`

| Source | Hits |
|---|---|
| `_full_rebuild.ps1` | 0 |
| `build_tools\build_msi.py` | 0 |
| `build_tools\*` (recursive) | 0 |

**Classification: SAFE.** Zero hits. Test staging directory from Stage 4.9a work.

---

### Candidate 11: `dist\customize_test`

| Source | Hits |
|---|---|
| `_full_rebuild.ps1` | 0 |
| `build_tools\build_msi.py` | 0 |
| `build_tools\*` (recursive) | 0 |

**Classification: SAFE.** Zero hits. Test staging directory from customize component work.

---

### Candidate 12: `dist\tray_csharp\` (without `_release`)

| Source | Hits |
|---|---|
| `_full_rebuild.ps1` | 0 (all references are to `dist\tray_csharp_release`) |
| `build_tools\build_msi.py` | 0 (Stage 7.10 block references `dist/tray_csharp_release/...`) |
| `build_tools\*` (recursive) | 0 |

**Observation:** `dist\tray_csharp\` contains 20 files identical to `dist\tray_csharp_release\` EXCEPT `MastersFM_Tray_v14.dll` is 905,216 bytes (post-hotfix) vs 901,120 bytes in `tray_csharp_release`. This directory appears to be the output of an earlier build step (before `_release` suffix was standardized) and is not referenced by any current build script.

**Classification: SAFE.** Zero hits as MSI source or build target. The active WPF tray build output is `dist\tray_csharp_release\`.

---

### Candidate 13: `dist\server.exe` (root lone file)

| Source | Hits |
|---|---|
| `_full_rebuild.ps1` | 0 (references `server.exe` from project root, NOT `dist\server.exe`) |
| `build_tools\build_msi.py` | 0 (SRC = project root; `"server.exe"` from root) |
| `build_tools\*` (recursive) | 0 |

**Details:** `dist\server.exe` is 42,805,563 bytes (05/07) -- the legacy Node.js + pkg server binary from the pre-.NET-migration era. The active server at project root is 158,744 bytes (post-Stage-4 .NET server, signed). The active build flow: `dotnet publish` → `dist\server_dotnet_release\server.exe` → copy to `$root\server.exe` (project root).

**Classification: SAFE.** Zero hits. The 42.8 MB Node server binary is a stale artifact from Stage 3/earlier. No build script references it.

---

## Summary table

| Candidate | Hits in active code | Hits in dead code | Classification |
|---|---|---|---|
| `tray_launcher.cs` | 0 | 1 (inside [1d/5] block, Phase 1 target) | SAFE |
| `tray_launcher` | 0 | 1 (same) | SAFE |
| `_build_tray.ps1` | 0 | 0 | SAFE |
| `_build_spectrum.ps1` | 0 | 3 (inside dead `$UseDotnet8AudioSpectrum` false-branch) | SAFE |
| `ps2exe.ps1` | 0 | 3 + self-refs (same dead branch + files being deleted) | SAFE |
| `ps2exe` (any) | 0 | 3 + self-refs | SAFE |
| `dist\old_releases` | 0 | 0 | SAFE |
| `dist\server_dotnet_test*` (8 dirs) | 0 | 0 | SAFE |
| `dist\bootstrapper_test` | 0 | 0 | SAFE |
| `dist\test_49a_helpers` | 0 | 0 | SAFE |
| `dist\customize_test` | 0 | 0 | SAFE |
| `dist\tray_csharp\` (no `_release`) | 0 | 0 | SAFE |
| `dist\server.exe` (root, 42.8 MB) | 0 | 0 | SAFE |

**All 13 candidates cleared. No DEFERRED items.** Phase 1 may proceed in STEP 3.

---

## Note on `_build_spectrum.ps1` and Phase 2 ordering

The dead false-branch references to `_build_spectrum.ps1` in `_full_rebuild.ps1` (lines 321, 328) will exist in the script after Phase 1 deletes the file. This is acceptable because:
1. The dead branch is never executed (`$UseDotnet8AudioSpectrum = $true` permanently).
2. Phase 2 will collapse this block and remove the references.
3. If Phase 2 were somehow skipped, the dead branch would still never execute.

Phase 1 and Phase 2 are ordered correctly; this ordering dependency is by design.
