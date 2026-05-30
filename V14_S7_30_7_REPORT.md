# V14 Stage 7.30.7 -- REPORT (CLOSED -- operator PASS) -- CUSTOMIZE CYCLE COMPLETE

Install + build hygiene, the FINAL stage of the customize.html rebuild cycle. Operator gate
verdict: **PASS, delete the zero-byte files**. Strikes 0 / 3 (clean run). NO source changes --
customize.html / customize_legacy.html / overlay.html SHA-unchanged end-to-end.

## What shipped (4 items)

- **Item 1 -- stale customize_v2.html:** confirmed it exists ONLY as an install-dir orphan
  (`%LOCALAPPDATA%\MastersFM\customize_v2.html`, 65904 B); NOT in src/, NOT in build_msi.py,
  NOT a server route (the 2 grep hits are stale comments inside customize.html). The build does
  NOT carry it (proven: the cold-rebuild MSI did not reinstall it). The non-empty install orphan
  was NOT deleted -- it is non-empty and the operator approved only the zero-byte set, and the
  ABSOLUTE rule forbids deleting non-empty files. Flagged for a separate one-line approval (see
  Deferred). Build side of Item 1 = DONE.
- **Item 2 -- legacy bundling:** `build_tools/build_msi.py` now bundles `src/customize_legacy.html`
  into the MSI (new `GUID_COMP65`, verified unique vs all 67 component GUIDs). Proven: the cold
  rebuild installed customize_legacy.html (was ABSENT pre-rebuild -> PRESENT 7E98377D). The
  instant-revert safety net now survives clean installs.
- **Item 3 -- _full_rebuild.ps1 hardening:** VBCSCompiler pre-kill added in preflight (after
  REBUILD START, so the HTML-only fast path is unaffected); WPF tray publish failure is now a
  HARD ERROR (exit 11, clear FAIL message) on BOTH the exit!=0 and the exit=0-but-no-exe branches.
  Success path byte-identical -- proven by a clean cold rebuild where neither hard-error fired.
- **Item 4 -- junk cleanup (GATED):** 462 untracked -> 72 zero-byte / 390 non-empty. Operator
  approved deletion of the zero-byte set: **71 zero-byte debris files deleted** (tokenized command
  fragments; re-verified 0 bytes immediately before each delete; shell-hostile names handled via
  -LiteralPath enumeration). `src/InstallFiles` (0 B, under src/) HELD per the no-touching-src
  rule. `.gitignore` extended for the .NET publish artifacts (*.deps.json, *.runtimeconfig.json,
  *.pdb, *.gcdump, web.config, staticwebassets endpoints, obs_cleanup publish output, ruvector.db,
  .swarm/). Stage reports left untracked (operator chose plain delete, not +commit-reports).

## Bonus (separate workstream): album-art live-refresh fix (Stage 7.30.7.1)

Operator reported mid-stage: YouTube overlay album art stuck on the Chrome app-icon while the
tray showed the real thumbnail ("fetches too fast"). Diagnosed to the .NET now-playing pipeline
(`server_dotnet/WebhookHandler.cs`): the same-track path froze `trackArt` -- the early transient
SMTC thumbnail (app icon) was stored at track-start (ArtResolved=true), and the B11 art-retry only
fires when art is empty, so the later real SMTC thumbnail (which the tray re-extracts and the 50 ms
heartbeat re-sends) was ignored. Fix: on same-track heartbeats, adopt a newer valid SMTC data-URI
when it differs from the stored art and the stored art is itself a data-URI/empty (never clobber a
resolved HTTPS cover); the existing BroadcastIfChanged pushes it to the overlay. Committed
`1f24d45`; deployed via cold rebuild; **operator-verified live ("art bug is fixed")**. This is NOT
part of the hygiene scope -- it is a parallel, operator-sanctioned functional fix.

## Verification

- Cold rebuild with hardened scripts: SE2 CLEAN (REBUILD DONE OK, VBCSCompiler pre-killed, server
  + WPF publish OK, 0 ERROR/WARN/FAIL). Install: customize.html SHA == source 33D09CDF;
  customize_legacy.html bundled present; version.json restored to 14.0.0 (no bump).
- STEP 5: 71 deleted == approved 71; src/ untouched; build artifacts confirmed ignored
  (git check-ignore); untracked 462 -> 367.
- 4 protected + 3 source HTML SHA256: ALL MATCH baseline, end-to-end.

## Final integrity (SHA256 -- unchanged end-to-end)

- `src/customize.html`        : `33D09CDFF76547D4...`
- `src/customize_legacy.html` : `7E98377DC97F83B3...` (LEGACY_SHA)
- `src/overlay.html`          : `9A7CC817515FFCC0...`
- `src/tray.ps1`              : `19011F0BD093CEA5...`
- `src/tray_native/tray_native.cs` : `6B9804A1AB700006...`
- `src/launcher.cs`           : `291ED4C92B9BEA39...`
- `src/server.js`             : `C15ED9310CB33044...`
- `version.json`              : 14.0.0 (msi_sha256 7dcd4e34, no bump)

## The customize-cycle arc (CLOSED)

7.25 research -> 7.26 scaffold -> 7.27 apply-to-OBS port -> 7.28 SETTINGS_CONFIG (141 controls)
-> 7.29 themes/badges/search/collapse -> 7.30 + 7.30.2 atomic swap (REBUILD CYCLE COMPLETE) ->
7.30.1 FLAT-to-NESTED translator -> 7.30.3 density/contrast -> 7.30.4 MEGA (Preset Manager + V1
org + renames) -> 7.30.5 preset Export/Import (full legacy parity) -> 7.30.6 tooltip system (41)
-> 7.30.6.1 blanket tooltips (95/120) -> **7.30.7 install/build hygiene (THIS -- cycle complete)**.

## Deferred / backlog (optional polish, v14.1.0)

- Stale install-dir customize_v2.html (65904 B orphan) -- removal pending explicit approval
  (non-empty; harmless; nothing references it).
- 13 unmapped theme leaves; 8 dynamic layout vis/lock pairs; Mica/Acrylic review; version-string
  consolidation; server log rotation; real friend feedback.
- Album-art follow-up (optional): the YouTube-search HTTPS source is unreliable for long DJ mixes;
  the SMTC live-refresh fix covers the reported symptom, but a "fetch a real web cover when SMTC
  has none" improvement could be its own stage.

## Commits (7 since 8fc091e)

`d99d54e` STEP 0 / `8e8c72c` STEP 1 / `0d28ae5` STEP 2 / `df8f942` STEP 3 / `14a67cb` interim
checkpoint / `1f24d45` STEP 7.30.7.1 art fix / `0972bd7` STEP 5. STEP 4 = gate (no commit).
v14.0.0 stable.
