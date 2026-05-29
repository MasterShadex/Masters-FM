# V14 Stage 7.30.5 -- REPORT (CLOSED -- operator PASS)

Preset EXPORT + IMPORT restored from legacy. Operator gate verdict: PASS.
Strikes consumed: 0 / 3 (clean run, no SE5 cycles).

## Items delivered (all in src/customize.html; committed)

- Item 1 -- Export: "Export" button in the Preset Manager modal. Serializes the
  CURRENT live config (`State.config` -> `flatToNested`) into the legacy envelope
  `{format:"mastersfm.preset", version:1, name:"overlay-config", exportedAt,
  exportedFrom:"v14.0.0", config}` and downloads `MastersFM_overlay-config.json`
  via Blob + anchor (pure client-side; no server call). Mirrors legacy pmExport
  (line 5068) envelope + filename; v2 exports the CURRENT config (operator spec)
  vs legacy's saved-preset-by-name.
- Item 2 -- Import: "Import" button + hidden file input (accept .json). Reads the
  file, `JSON.parse` (catch), forgiving envelope-or-bare detection (mirrors legacy
  pmImport line 5099), MANDATORY `isValidPresetShape` validation, then applies
  DIRECTLY (`nestedToFlat` -> MERGE into State.config -> applyConfig +
  refreshControlValues + previewConfig). v2 applies directly (operator spec) vs
  legacy's save-to-server-then-load.

## Validation (the risky part -- all verified)

`isValidPresetShape`: non-null object, not array, >=1 top-level key in
`FLAT_TO_NESTED_MAP` values. Runs BEFORE any State.config mutation.

Import matrix 5/5 (real handleImportFile via preview MCP):
- Valid file        -> applied
- Malformed JSON    -> "file is not valid JSON", no state change
- Wrong-shape JSON  -> "does not look like a Master's FM preset", no state change
- Empty file        -> "file is not valid JSON", graceful
- Re-import same     -> works both times (input.value reset)

Export -> import round-trip: lossless (0 diffs; c-border-colors comma<->array
survives). Round-trip self-test ok (10-key probe). Console 0 warn / 0 error.

## FULL LEGACY PRESET PARITY ACHIEVED

v2 Preset Manager now matches legacy end-to-end: list / save / load / delete /
overwrite (Stage 7.30.4) + export / import (Stage 7.30.5). The customize.html
rebuild has complete legacy preset feature parity plus the v2 design improvements.

## Legacy vs v2 divergence (research-first finding)

Legacy export = a SAVED preset by name (server round-trip); legacy import = SAVE
to /save-preset then "click Load." v2 (operator /goal spec + gate) = export the
CURRENT config + import APPLIES directly. Legacy was source of truth for FORMAT
(envelope / filename / forgiving detect); operator spec for MECHANISM. Full
extraction in V14_S7_30_5_LOG.md S0.3-S0.5.

## IMMUTABLE / scope preserved

- src/customize.html is the only source file touched (+122 lines).
- customize_legacy.html UNCHANGED (`7E98377D...`). overlay.html UNCHANGED
  (`9A7CC817...`). server.js / tray.ps1 / tray_native.cs / launcher.cs: SHA UNCHANGED.
- SETTINGS_CONFIG / FLAT_TO_NESTED_MAP / THEMES: unchanged (round-trip self-test
  confirms the map intact). No new server endpoints (export/import are client-side).
- version.json 14.0.0 (no bump; cold-rebuild msi_sha256 churn restored).

## Closure SHA256

- `src/customize.html`: `8695B992A949B9FA...` (Stage 7.30.5 closure)
- `src/customize_legacy.html`: `7E98377DC97F83B3...` UNCHANGED (LEGACY_SHA)
- `src/overlay.html`: `9A7CC817515FFCC0...` UNCHANGED
- `version.json`: 14.0.0 (no bump)

## Deployment

Cold rebuild (`_full_rebuild.ps1 -FullRebuild`) -- NOT fast-path -- per the
Stage 7.30.4 WebView2-cache lesson, so the operator could hands-on test in a
fresh WebView2. SE2 log clean; install SHA == source. Passed first try.

## Commits (6)

`99d839d` STEP 0 (research) / `d29ee29` STEP 1 (export) / `78b1772` STEP 2
(import) / `2a9cd01` STEP 3 (evidence) / `a67e564` STEP 4 (rebuild) / closure
(STEP 6). STEP 5 was the gate (no commit).

## Strikes

0 / 3 consumed. Clean run.
