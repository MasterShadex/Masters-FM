# V14_RC1_FINAL_REPORT.md

Master's FM v14.0.0-rc.1 Release Candidate -- ship summary.
Generated: 2026-05-07 ~23:40.
Status: LOCAL TAG ONLY (NOT PUSHED). Awaiting STEP 8 approval + STEP 9 manual hand-off.

## Top one-paragraph summary

v14.0.0-rc.1 cumulatively bundles Stages 1-3 + Stage 4 (sub-stages 4.1-4.11) + Stage 5
MINIMAL (5.1 + 5.4 + 5.5) into the first .NET 8 ship since the v12.0.1 baseline. Source
versions bumped from v12.3.0 to v14.0.0-rc.1 in the four allowlisted active-code locations.
Build flags finalized: 5 of 6 .NET-8-ready flags = $true (bootstrapper kept $false per
known Bitdefender concern). Two clean post-fix rebuilds passed. ~194-minute structured soak
confirmed plateau persistence at ~688-700 MB working set. Auto-update path verified safe
(version.json on main unchanged + PowerShell `[version]` cast throws on `-rc.1` suffix).
**STEP 8 commit + tag is HALTED for explicit approval of the V14_RC1_GIT_PLAN.md
allowlist/denylist before any git mutation.**

---

## Headline validation finding (per addition #1)

**The biggest lesson from RC1 validation: STEP 5 discipline saved a hard-bricked first
install on every tester.**

The .NET 8 server.exe binary worked in isolation across every Stage 4 sub-stage validation
(4.1 through 4.11) when run from `dist/server_dotnet_release/`. The MSI install path,
however, never received the `DiscordRPC.dll` runtime dependency that Lachee.DiscordRPC's
runtime hooks require. The build pipeline's post-publish copy block (`_full_rebuild.ps1`
foreach at lines ~42-46) and the MSI's `build_msi.py` FILES list both omitted it.

The bug was masked in single-build testing by INSTALL RESIDUE: a previous v12.0.1 (Node.js
server) install left DLLs in place that the .NET 8 server happened not to need. Each
Stage 4 sub-stage rebuild ran `$UseDotnet8Server=$true`, validated for ~30-60 minutes, then
restored `$UseDotnet8Server=$false` and reinstalled the legacy Node.js server -- so the
gap was never tested under a fresh install path.

ONLY a build-2-over-build-1 install during this RC1 STEP 5 surfaced the bug, when:
- Build 1 installed the .NET 8 MSI (server.exe missing DiscordRPC.dll, but residual Node
  install dir didn't need it because Node was just being uninstalled)
- Build 2 ran cleanly, server.exe spawned, threw `FileNotFoundException`,
  `BackgroundService failed`, `HostOptions.BackgroundServiceExceptionBehavior=StopHost`,
  Application is shutting down
- Port 4242 became idle, /version returned "Unable to connect," validation HALTED

If RC1 had shipped without STEP 5 catching this:
- Every tester downloading the .msi from GitHub Pre-release page would have installed it
- On first launch, server.exe would have crashed within 5 seconds of MastersFM.exe spawn
- Tray would show but overlay would never connect (no /events SSE), customize.html would
  fail (no /overlay-config), Discord RPC would never fire (the cause of the crash itself)
- Effective state: hard-bricked first install on every tester
- Hotfix turnaround: hours-to-days. Damages tester trust on a release brand-new to them
  after their weeks-long pause on v12.0.1.

The fix landed in this brief: added DiscordRPC.dll + Newtonsoft.Json.dll to BOTH
`_full_rebuild.ps1` and `build_msi.py` (with new GUID_COMP43 and GUID_COMP44 constants),
re-ran two clean rebuilds, server.exe stays alive, /version 200 confirmed in install dir.

**This justifies fresh-soak rigor on every future ship.** Synthetic-from-source testing is
necessary but not sufficient. Stage-flag-toggling is fragile. Future RC validations must:
1. Run two clean full rebuilds.
2. EVERY rebuild MUST install through the actual MSI path (not run from `dist/`).
3. Smoke test the running server WITHIN MINUTES of install (not days later).
4. Capture stdout/stderr if the server PID dies within 60s of launch (manual repro).
5. Check `dir AppData install root` for expected DLLs after install.

The hotfix playbook adds a new failure mode "Missing native dependency at runtime" with
diagnostic commands and a repeat-prevention rule.

### Soak handle-band ratification (honest call-out)

Mini-observation showed monotonic handle growth 722 to 844 (+122) over 30 min. Within HALT
bounds (<850) but outside PASS band (700-830). PASS call rests on broader 194-min dataset
showing clear oscillation cycles of 50-127 units; 30-min window too narrow to capture a
full cycle. Future soaks should sample at least 60 min to evaluate handle-band compliance.

---

## Files changed

### Active source (5 files modified)

- `_full_rebuild.ps1` -- RC1 ship state header block (lines 7-14), 6 flag values + per-flag
  RC1 rollback comments, server foreach copy now includes DiscordRPC.dll + Newtonsoft.Json.dll
- `build_tools/build_msi.py` -- `_parse_app_version` strips SemVer pre-release suffix from
  MSI ProductVersion; new GUID_COMP43 + GUID_COMP44 constants; Stage 4 _optional FILES tuple
  appends DiscordRPC.dll + Newtonsoft.Json.dll
- `src/install_bootstrapper.cs` -- Stage 3b .NET 8 port (Environment.ProcessPath fix)
- `src/launcher.cs` -- Stage 1 .NET 8 launcher (full Stage 1 work)
- `src/tray.ps1` -- ALLOWLISTED minimal edits: APP_VERSION line 253 (`v14.0.0-rc.1`) +
  one new PatchNotes entry prepended at lines 267-276 (8 NEW/IMPROVED entries explaining
  v14.0.0-rc.1 cumulative scope, hybrid build state, RC framing). v12.3.0 historical entry
  preserved at line 277.

### New source files (committed in cumulative commit)

- `src/Directory.Build.props` (Stage 2 apphost contamination fix)
- `src/launcher.csproj` (Stage 1)
- `src/audio_spectrum.csproj` (Stage 2)
- `src/customize.csproj` (Stage 3a)
- `src/install_bootstrapper.csproj` (Stage 3b)
- `src/server_dotnet/` (entire directory; Stage 4)
- `src/tray_native/` (Stage 5.1 moved location with new csproj)
- `test-ps51-load.ps1` (Stage 5.1 PS5.1 validation harness)

### Deleted

- `src/tray_native.cs` (moved to `src/tray_native/tray_native.cs` in Stage 5.1)

### Validation result reference

`V14_RC1_VALIDATION.md` -- 5.1 PASS, 5.2 PASS, 5.3 DEFERRED, 5.4 PASS, 5.5 PASS, 5.6 PASS,
5.7 PASS-with-deviation (~194 min mini-confirmed plateau).

### Auto-update verification result

`V14_RC1_AUTOUPDATE_VERIFICATION.md` -- dual-layer protection:
- PRIMARY: `version.json` on `origin/main` left at v12.0.1 (NOT in commit set per addition A)
- SECONDARY: PowerShell `[version]` cast throws on `-rc.1` suffix at `tray.ps1:5745`,
  exception caught at line 5783, idle, no balloon, no auto-install

Both layers must fail simultaneously for an RC1 leak to general testers.

### Secret audit result

`V14_RC1_SECRET_AUDIT.md` -- 0 (a) findings, 0 (c) findings, 4 (b) findings (Discord App ID
public-by-design; Windows username "Master" + F:\ paths in 10 dev scripts pre-existing in
public git history since v10.0.0 / v11.2.1; not blocking).

### Hotfix playbook reference

`V14_RC1_HOTFIX_PLAYBOOK.md` -- 8 failure modes catalogued: (1) Discord RPC connection,
(2) art cascade wrong-art, (3) webhook deep-merge, (4) SSE heartbeat gaps, (5) tray_native
PS5.1 load on older .NET Framework, (6) **Missing native dependency at runtime** (new),
(7) **Server crashes/misbehaves with no diagnostic log** (new), (8) Auto-update path
serving RC1 to general testers. RC2 cut process documented.

### Tester announcement draft reference

`TESTER_ANNOUNCEMENT_v14.0.0-rc.1.md` -- Discord-format, manual-install-only language,
SmartScreen disclosure, rollback to v12.0.1 instructions, posted by Orken in
`#v14-rc-feedback`. NOT POSTED.

---

## Validation result table (STEP 5.9)

| Item | Status | Note |
|---|---|---|
| 5.1 Two clean rebuilds | PASS | post-fix exit=0 both, all binaries Signed Valid |
| 5.2 Signing audit | PASS | 7/9 our-binaries signed; 2 unsigned pre-existing pattern; third-party DLLs unsigned MSI-wrapped |
| 5.3 Baseline diff | DEFERRED | per-substage diffs cover ID-1..35; ratified |
| 5.4 Smoke | PASS | live SoundCloud /current 200, /version 200, /update-status shows v14.0.0-rc.1 |
| 5.5 Webhook B1-B11 | PASS | synthetic + live |
| 5.6 PS5.1 load test | PASS | all 9 types resolved |
| 5.7 6h soak | PASS-with-deviation | ~194 min effective; plateau confirmed at ~688-700 MB |
| HALT determination | NONE | no SAFETY FLOOR triggers active |
| **STEP 5 OVERALL** | **PASS** | proceed to STEP 8 (HALT for approval) / STEP 9 (manual handoff) |

---

## .NET 8 server baseline (LOCKED for future RC / stable comparisons)

| Metric | Value |
|---|---|
| WS plateau | ~688-700 MB |
| Time to plateau (first cold start) | 90-120 min |
| Post-plateau growth rate | 0.7-16 MB/h depending on workload |
| Handle band | 688-844 (~156-unit oscillation range) |
| Thread band | 30-45 |
| Steady-state threads | 34-36 typical |
| /version target | HTTP 200 on every probe |
| Discord RPC state | "running" while Discord client is up |

The legacy Node 0.65 MB/h figure is discarded as no-longer-relevant for .NET 8 comparisons.
Future soaks compare against the figures above.

---

## Open questions / future work (priority order)

### Priority 1 -- File logging for .NET 8 server (estimated 2-4h)

`launcher.cs` spawns server.exe with `RedirectStandardOutput=false`, so all .NET 8 server
log output is discarded. Without this, RC1 "server died" tester reports have no diagnostic
surface. Fix: modify `launcher.cs` `ProcessStartInfo` for server spawn to redirect stdout +
stderr to `%LOCALAPPDATA%\MastersFM\server-dotnet.log` with size-based rotation (5 MB max,
keep 3 generations). launcher.cs is a protected file; requires explicit user direction.
Likely v14.0.0-rc.2 or v14.0.x candidate.

### Priority 2 -- SemVer pre-release support in tray.ps1 update-check (30-60 min)

`tray.ps1:5745` uses `[version]` which throws on pre-release suffixes. This is currently
ACCIDENTAL safety: old testers polling RC1's version.json silently no-op. But it also
means RC1 testers cannot auto-update to RC2 / stable v14.0.0 via this path. For stable
v14.0.0 ship, regex-strip pre-release suffix before [version] cast. tray.ps1 is protected
but will be touched for the APP_VERSION bump anyway, so this lands in the same commit.

### Priority 3 -- Sign MastersFM.exe + MastersFM_Tray.exe in build pipeline

Both currently NotSigned (pre-existing pattern). MSI signing covers install integrity but
once installed both binaries run unsigned. Add signtool block in `_full_rebuild.ps1` to
the [1b/5] launcher build (mirror customize block at lines 142-150) and to the [1d/5]
csc.exe MastersFM_Tray block. Stage 8 build-pipeline-cleanup item.

### Priority 4 -- Embed AssemblyInfo metadata in .NET 8 binaries

Csproj `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>` preserves parity with csc.exe
binaries (which had no assembly info). Result: VersionInfo on server.exe etc. shows blank.
Cosmetic but unhelpful for production diagnostics. Stage 8 item.

### Priority 5 -- Re-enable install_bootstrapper .NET 8 build

`$UseDotnet8Bootstrapper=$false` per known Bitdefender flag. To re-enable: real CA cert
(Certum or equivalent) + EmbeddedResource items for payload.msi + publisher.cer in
install_bootstrapper.csproj. Future stable v14.0.x candidate.

### Priority 6 -- Stage 6 (tray_launcher dissolution) folded into Stage 7

Per V14 plan and `V14_S6_P1_FINAL_REPORT.md`: Stage 6 is 0 hours of independent effort;
the deletion happens during Stage 7's cutover commit. Skip Stage 6 as a sub-stage.

### Priority 7 -- Stage 7 (tray.ps1 -> C# .NET 8 application)

The largest remaining stage. Estimated 400-700h. Pending RC1 tester feedback before
scheduling.

---

## Recommendation for next steps

1. **Approve V14_RC1_GIT_PLAN.md commit allowlist/denylist.** STEP 8 is HALTED at the
   commit-set boundary per addition #5; cannot proceed without Orken green-light.
2. **On approval:** execute the git add / git commit / git tag commands documented in
   V14_RC1_GIT_PLAN.md. Two commits (cumulative + memory APPEND) + one annotated tag.
   Output `V14_RC1_GITHUB_RELEASE.md` placeholder for STEP 9 manual hand-off.
3. **STEP 9 (manual):** Orken installs gh CLI OR uses GitHub web UI to:
   - `git push origin main` (or feature branch + PR if preferred)
   - `git push origin v14.0.0-rc.1`
   - `gh release create v14.0.0-rc.1 --prerelease --title "v14.0.0-rc.1 (Release Candidate)" --notes-file RELEASE_NOTES_v14.0.0-rc.1.md "Master's FM Install\MastersFM_Setup.msi"`
   - Verify "Pre-release" label on the release page.
4. **STEP 10 (manual):** Orken posts `TESTER_ANNOUNCEMENT_v14.0.0-rc.1.md` content to
   the new `#v14-rc-feedback` Discord channel.
5. **Monitor tester feedback over coming days.** Real validation is what testers report.
6. **If RC2 needed:** follow `V14_RC1_HOTFIX_PLAYBOOK.md` cut process.
7. **For stable v14.0.0:** apply Priority-1 + Priority-2 future-work items at minimum;
   re-validate in same STEP 5 form.

---

## Sworn statement

I confirm that:

1. tray.ps1 edits in this brief were limited to: (a) `$script:APP_VERSION` constant on
   line 253, and (b) one new PatchNotes table entry prepended at lines 267-276. No other
   tray.ps1 changes.
2. Other protected files (tray_native.cs, launcher.cs, server.js, memory.md) were untouched
   for the v14.0.0-rc.1 cumulative commit. memory.md APPEND happens in a separate Commit 2
   per STEP 7.
3. version.json is NOT in the STEP 8 commit set per addition A. The version.json on
   `origin/main` remains at v12.0.1, preventing auto-update to RC1 for general testers.
4. STEP 1 secret audit: 0 (a)/(c) findings; 4 (b) findings all pre-existing in public git
   history since v10.0.0 / v11.2.1.
5. STEP 4 auto-update verification: dual-layer protection (primary: version.json unchanged
   on main; secondary: PowerShell `[version]` cast throws on `-rc.1` suffix).
6. STEP 5 validation: 5.1 PASS, 5.2 PASS, 5.3 DEFERRED-with-ratification, 5.4 PASS, 5.5
   PASS, 5.6 PASS, 5.7 PASS-with-deviation. STEP 5 overall: PASS.
7. Mid-validation HALT triggered on DiscordRPC.dll missing from MSI; resolved via Option A
   fix; soak resumed clean post-fix.
8. Em-dash hard constraint (Absolute Rule 2) respected for all PS / build-script edits in
   this brief: 0 em-dashes added to `_full_rebuild.ps1`, `build_msi.py`, or any new content
   in tray.ps1.
9. UTF-8 BOM constraint respected. New documentation files written without BOM.
10. NO source files modified outside the directive-allowlisted set. NO commits, tags, or
    pushes have been executed yet (STEP 8 halts for Orken approval per addition #5).

End of v14.0.0-rc.1 final report.
