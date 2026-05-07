# V14_RC1_FLAGS_FINALIZED.md

STEP 3 complete: 2026-05-07 ~17:15.
Target: `_full_rebuild.ps1` flag block (lines 6-37 after edit).

## Final flag state (RC1)

| Line | Flag | RC1 value | Rollback ($false) behaviour |
|---|---|---|---|
| 19 | `$UseDotnet8Launcher` | $true | csc.exe + .NET Framework 4.x launcher build |
| 22 | `$UseDotnet8AudioSpectrum` | $true | csc.exe via `build_tools\ps2exe\_build_spectrum.ps1` |
| 25 | `$UseDotnet8Customize` | $true | csc.exe customize build (note: legacy csc.exe customize WebView2 DLL parity broken since v12.3.0; full rollback requires CHECKPOINT_v12_2_0 per memory.md) |
| 30 | `$UseDotnet8Bootstrapper` | **$false** | (only the csc.exe path is active for RC1; .NET 8 path requires CA cert + EmbeddedResource items per directive 3=b) |
| 33 | `$UseDotnet8Server` | $true | legacy Node.js + @yao-pkg/pkg server build |
| 37 | `$UseDotnetTrayNative` | $true | csc.exe + .NET Framework 4.x tray_native build |

## Header comment block (lines 7-14, NEW)

```
# ============================================================================
# v14.0.0-rc.1 build flags (RC1 ship state)
# ----------------------------------------------------------------------------
# All five .NET-8-ready flags are $true. csc.exe rollback paths are preserved
# behind the $false branch of each flag: emergency revert path only, do not
# delete the csc.exe-side build steps. install_bootstrapper remains on csc.exe
# ($UseDotnet8Bootstrapper = $false) until a real CA cert + EmbeddedResource
# items land; see open_issues.md for re-enable steps.
# ============================================================================
```

## Per-flag inline comments

Each flag now has either "RC1 rollback path" or equivalent rollback wording in its inline comment. Bootstrapper flag has the multi-line comment retained explaining why it stays $false for RC1.

## Em-dash audit
- U+2014 (em-dash): 0 occurrences in `_full_rebuild.ps1`
- U+2013 (en-dash): 0 occurrences

Absolute Rule 2 satisfied. Script parses clean (PowerShell `Parser.ParseFile` returned no errors).

## Deviation from brief STEP 3.1

Brief STEP 3.1 instructs `$UseDotnet8Bootstrapper = $true`. Per directive 3=b: kept at `$false` because:
1. `open_issues.md` lines 77-80 document the disable: Bitdefender flags the self-signed bootstrapper pattern (embedded MSI + self-elevate + cert store mod).
2. Re-enable requires: (a) real CA cert (Certum ~EUR 25/yr, not yet acquired), (b) `EmbeddedResource` items for `payload.msi` + `publisher.cer` (not yet added to `install_bootstrapper.csproj`).
3. Setting $true without those preconditions either fails the build (missing resources) or produces an AV-flagged binary that scares testers.

Mitigation: RC1 ships hybrid -- .NET 8 for the runtime stack (launcher, server, audio_spectrum, customize, tray_native) and csc.exe for the install bootstrapper. The MSI install path does NOT require the bootstrapper; testers install via the signed .msi directly.

This deviation is disclosed in:
- Release notes STEP 6.3 (the "what changed" section)
- Release notes STEP 6.9 (the "deferred to future" section)
- Final report `V14_RC1_FINAL_REPORT.md`

End of flags-finalized doc.
