# V14 Stage 7.30.6 -- REPORT (CLOSED -- operator PASS)

Tooltip system. Operator gate verdict: PASS ("41 tooltips work, read well, no
clipping, no regression"). Strikes consumed: 0 / 3 (clean run).

## What shipped (all in src/customize.html)

Hover / focus / tap help tooltips (info ⓘ icons) next to the 41 controls that
warrant explanation, replacing legacy's always-visible inline help (operator
"less text everywhere" goal). Help text research-first from legacy
customize_legacy.html, condensed.

- Item 1 (help source): legacy mechanism = 51 positional `<p class="control-help">`
  paragraphs. Extracted all 51 -> coverage table -> locked 41 warranted (jargon /
  non-obvious); self-explanatory controls (master accent, plain colors/sizes,
  on/off toggles) excluded.
- Item 2 (help field): additive `help: '...'` string on 41 existing SETTINGS_CONFIG
  entries. No new entries, no key changes; FLAT_TO_NESTED_MAP + THEMES untouched.
- Item 3 (tooltip UI): renderControl adds an accessible `<button class="help-icon">`
  (inline SVG glyph, aria-label) next to the label only when `config.help`; CSS
  tooltip opens BELOW the row, width-capped (no right-edge clip); hover + keyboard
  focus + tap reveal; Esc closes; one-at-a-time on tap; click-away closes.
  Dependency-free.

## Verification (Ruflo, preview MCP)

- 41 icons render ONLY where help present; self-explanatory excluded (verified
  c-master-accent-color / c-spec-on / c-title-color / c-prog-on have no icon).
- No-clip: activated tooltip right edge 291 <= row right 299, on-screen (1280px).
- Keyboard: focusable button, :focus shows tooltip, Esc closes + blurs.
- Tap: toggle / one-at-a-time / Esc / click-away all PASS.
- Round-trip self-test ok; c-spec-response slider binding intact; console 0 / 0.
- Counts: SETTINGS_CONFIG 120 / FLAT_TO_NESTED_MAP 118 / THEMES 22 (unchanged).

## IMMUTABLE / scope

- customize.html is the only source file touched. customize_legacy.html UNCHANGED
  (7E98377D); overlay.html UNCHANGED (9A7CC817); protected (tray.ps1 /
  tray_native.cs / launcher.cs / server.js) UNCHANGED.
- version.json 14.0.0 (no bump; cold-rebuild msi_sha256 restored).

## Closure SHA256

- `src/customize.html`: `88F380F68D260254...` (Stage 7.30.6 closure)
- `src/customize_legacy.html`: `7E98377DC97F83B3...` UNCHANGED (LEGACY_SHA)
- `src/overlay.html`: `9A7CC817515FFCC0...` UNCHANGED

## Deployment

Cold rebuild (`_full_rebuild.ps1 -FullRebuild`, per the 7.30.4 WebView2 lesson);
SE2 log clean; install SHA == source. Operator tested tooltips live; PASS first try.

## Strikes

0 / 3. Clean run.

## Next

- Operator handoff: the NEXT stage EXPANDS tooltip coverage to more controls
  (beyond the 41 warranted this stage). The `help` field + render-time icon infra
  is in place -- expansion = add more `help` strings to SETTINGS_CONFIG entries.
- Per the brief, the remaining customize-cycle item is Stage 7.30.7 (install/build
  hygiene: legacy bundling decision, stale customize_v2.html cleanup,
  _full_rebuild.ps1 hardening, junk-file cleanup).

## Commits (6)

`fbb792d` STEP 0 / `246683f` STEP 1 / `7ee0643` STEP 2 / `672818c` STEP 3 /
`41abe66` STEP 4 / closure (STEP 6). STEP 5 = gate (no commit).
