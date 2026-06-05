# V14 Stage 7.32 -- REPORT (PASS)

**PATH A: V2 look + V1 categories/settings + V1-lossless import/export + tooltips-only help.**
Closed PASS (operator re-test) after one SE5 fix (strike 1/3). Only `src/customize.html` changed.
No version bump (shipping stays 14.0.0). Real GitHub never touched.

## Outcome
The shipping customize panel now keeps V2's visual design while restoring V1's full structure:
- **Categories:** V1's **17** collapsible categories (Quick Settings / Themes / General / Layout /
  Font / Card appearance / Auto-color from album art / Spinning border / Outer glow / Text glow /
  Album art / "Now Playing" label / Platform badge / Track and artist text / Audio bars / Progress
  bar and time / Slide-in animation) -- was 6 supercats. Names/order/icons sourced verbatim from
  `customize_legacy.html`. V2's `.supercat` card + collapse styling kept (V2 look). First-load
  default: Quick Settings open, the rest collapsed (V1 tidy-sidebar behavior).
- **Settings:** full V1 set present. GAP analysis (1C) = **NONE** -- V2's 120 controls were already a
  strict superset of V1's 119 c-* controls (+ the c-theme dropdown that replaces V1's theme grid).
  Item 3 (restore dropped) SKIPPED. Counts unchanged: SETTINGS_CONFIG 120 / FLAT_TO_NESTED_MAP 118 /
  THEMES 22.
- **Import/export (the crux):** now V1-compatible + **lossless including LAYOUT**. Root cause was a
  flat-only pipeline (`State.config` is c-* keyed) that dropped `layout.canvas` + node geometry (and
  every other unmapped key) on load/save/export/import. Fix = a full-nested passthrough
  (`State.nested`) + a `deepMerge()` helper; export/save/preview now send
  `deepMerge(State.nested, flatToNested(State.config))` and import folds the full imported config into
  `State.nested` first. Decisive round-trips (evidence/s7_32/import_export_compat.json): new->new
  lossless incl layout (export1===export2); a reconstructed V1-format (friend v12) envelope imports +
  applies; malformed/wrong-shape rejected. Envelope/forgiving-import unchanged (already V1-shaped).
- **Big text:** the welcome banner (the only big text in V2 -- V2 had no per-section paragraphs) +
  its CSS/JS + the footer reshow link removed. Tooltips (95/120 controls, the 7.30.6 help-icon engine)
  are now the only help surface.
- **Preview pane (SE5 fix, strike 1):** operator FAIL -- the Customize preview stretched/ballooned
  with the window. The V2 rebuild had dropped V1's `scaleIframe`; V2's iframe was width/height:100%.
  Restored V1's behavior: a FIXED 1000x200 preview iframe scaled via `transform:scale` to fit the pane,
  capped at 1.0 (never balloons), re-run on resize + after load. The preview is now a faithful 1000x200
  representation. overlay.html + the OBS render were never the cause (overlay fills its OBS Browser
  Source by design; unchanged).

## Scope / no regression (closure SHA256)
- ONLY `src/customize.html` changed: 33D09CDF -> **7E1ECEF3**.
- UNCHANGED: tray.ps1 19011F0B / tray_native.cs 6B9804A1 / launcher.cs 291ED4C9 / server.js C15ED931 /
  customize_legacy.html **7E98377D** / overlay.html **9A7CC817**.
- version.json **14.0.0** (msi_sha256 7dcd4e34; no bump). Real `MasterShadex/Masters-FM` remote
  UNTOUCHED (no push this stage). Overlay nested-config contract preserved (saveConfig/previewConfig
  still post nested -- now the complete config incl layout).

## Verification
- DOM-free harnesses (build_tools/_s732_verify.js, _s732_roundtrip.js): script compiles; 17 categories
  with all 120 controls bucketed per the 1B inventory; import/export round-trips all PASS; bindings
  category-independent.
- Cold rebuild SE2 CLEAN (server.exe OK -> WPF tray built -> REBUILD DONE OK; zero error/warn); install
  sanity (installed customize.html == src; tray+server healthy; overlay live). version.json restored.
- Operator gate: FAIL (preview balloon) -> SE5 fix -> re-rebuild SE2 clean -> operator **PASS**.

## Commits
1fc8804 STEP 0 (research+plan) / 751265e STEP 1 (17 categories) / [SKIP] STEP 2 / b481d30 STEP 3
(import/export lossless) / d43fe51 STEP 4 (big text removed) / 6f79784 STEP 5 (rebuild+install) /
808344c STEP 6 pre-gate / 7c2f5be FAIL+SE5 diagnosis / 6b61525 SE5 preview fix / 0ed4813 re-gate /
+ STEP 7 closure.

## Notes / still pending
- 7.31.2 (auto-update end-to-end test vs test-fm) remains PARKED at its STEP 3 operator-publish HALT
  (superseded by 7.32 per the brief; not closed).
- Strikes used: 1/3 (the preview-pane SE5 fix). Real release stays operator-only.
