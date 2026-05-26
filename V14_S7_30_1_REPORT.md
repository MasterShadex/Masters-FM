==
== V14_S7_30_1_REPORT.md  --  Stage 7.30.1 closure report
== FLAT-to-NESTED config translator + round-trip smoke (the Stage 7.30 SE5 strike 2/3 fix)
==

# Outcome

**PASS** via operator-light gate (STEP 5). Builds the FLAT-to-NESTED
config translator that Stage 7.30 needed and didn't have, plus the
round-trip smoke pattern that closes the Stage 7.28 / 7.29 blind spot.

`src/customize.html` (legacy Stage 7.24 polish) stays in production
unchanged. v2 work lives in `src/customize_v2_archive.html`. Stage 7.30.2
will re-attempt the swap with the translator in place.

Strikes consumed: 0 / 3 (clean execution).

# Headline numbers

| Metric | Value |
|---|---|
| FLAT_TO_NESTED_MAP entries | 118 (sourced verbatim from legacy `initBindings()`) |
| SETTINGS_CONFIG round-trip coverage | 118 / 118 (100%) |
| Intentionally unmapped UI-only entries | 2 (c-theme, c-spec-sensitivity-reset) |
| Coverage orphans (either direction) | 0 |
| Live-control round-trip smoke | 12 / 12 PASS across 8 input types |
| Translator self-test | PASS (`window.__roundTripSelfTest`) |
| Console state during smoke | 0 errors / 0 warnings |
| Bind functions audited | 8 (slider/color/colorPair/toggle/select/text/number/colorList + dispatcher) |
| Bind functions with previewConfig call | 8 / 8 |
| bindButton legacy parity | actionResetTarget wired (c-spec-sensitivity-reset) |
| previewConfig coalescing pattern | in-flight + pending (legacy lines 4450-4479) |
| Protected files SHA | UNCHANGED end-to-end (4/4) |
| `src/customize.html` SHA | UNCHANGED (`7E98377DC97F...`) -- LEGACY production untouched |
| `src/overlay.html` SHA | UNCHANGED (`9A7CC817515F...`) |
| `version.json` | `14.0.0` (no bump) |
| Stage 7.30.1 commits | 5 (1 per STEP 0-4) |
| Files touched | 4 (`src/customize_v2_archive.html`, `V14_S7_30_1_LOG.md`, 2 evidence JSONs) |

# Commit chain (Stage 7.30 close-as-reverted -> Stage 7.30.1 close-as-PASS)

```
58ea4dc  STEP 0  -- research-first nested-path mapping extracted from legacy customize.html (118/118 coverage; 0 orphans)
750e016  STEP 1  -- FLAT_TO_NESTED_MAP (118 entries) + flatToNested + nestedToFlat + __roundTripSelfTest
79ef2e8  STEP 2  -- saveConfig + previewConfig POST nested via flatToNested; loadConfig uses /overlay-config + nestedToFlat
9eecd7f  STEP 3  -- previewConfig in-flight coalescing (legacy pattern) + bindButton wires actionResetTarget for c-spec-sensitivity-reset
213a049  STEP 4  -- round-trip smoke (12/12 PASS) + coverage (118/118; 0 orphans) + evidence persisted
```

Stage 7.30 close-as-reverted: `4fba343`. Stage 7.30.1 close-as-PASS:
`213a049` (this report + log + memory commits to follow).

# What shipped

## 1. Research-first nested-path discovery (STEP 0)

Read legacy `src/customize.html` exhaustively:
- DEFAULTS const at line 2236 (the canonical nested shape; 20 top-level keys)
- `initBindings()` at lines 3494-3847 (the canonical c-* ID -> S.path.to.value
  WRITE side; one per `bindRange / bindColor / bindToggle / bindSelect /
  bindText` call)
- `syncAll()` at lines 4242-4435 (the READ side; inverse of initBindings)
- `init()` at line 5156 with `fetch('/overlay-config')` (the canonical GET
  endpoint)
- `sendPreview()` at lines 4450-4479 (the in-flight coalescing pattern for
  rapid drags)

Coverage table built in V14_S7_30_1_LOG.md S0.4 -- every v2 SETTINGS_CONFIG
entry that needs a nested path has a row pointing to a specific legacy
source line. No paths were invented.

## 2. `FLAT_TO_NESTED_MAP` const (STEP 1)

Section 8b0 in `src/customize_v2_archive.html`. 118 entries. Path values
are ARRAYS (not dot-strings), with number segments for array indices
(`['progressBar', 'fillColors', 0]`). 8 logical groups by element:
General / DynamicColors / Layout / Masters / Card / Border / Glow / Art /
NowPlaying / Bars / PauseBehavior / PlatformBadge / Title / Artist /
Spectrum / ProgressBar / Timestamps / ShowAnimation.

## 3. `flatToNested(flatConfig)` (STEP 1)

Walks `FLAT_TO_NESTED_MAP` and constructs nested objects/arrays.
Special case for `c-border-colors`: comma-string -> array of hex.

## 4. `nestedToFlat(nestedConfig)` (STEP 1)

Inverse direction for `loadConfig` -- iterates FLAT_TO_NESTED_MAP and
pulls each path out of the nested config via `getNestedAtPath`. Missing
paths skip silently (overlay may carry keys v2 doesnt expose). Special
case for `c-border-colors`: array -> comma-string.

## 5. `window.__roundTripSelfTest` (STEP 1)

Identity self-test exposed for preview MCP. Default probe covers 10
representative controls; whitespace-tolerant for the colorList case.
Re-callable from console for any debug ad-hoc verification.

## 6. `loadConfig` endpoint fix + transform (STEP 2)

Was `GET /get-overlay-config` (which doesn't exist on server.js; 404s
silently). Now `GET /overlay-config` (canonical legacy endpoint at
server.js line 1143). On success, `State.config = nestedToFlat(nested)`
gives SETTINGS_CONFIG binds their flat keyspace.

## 7. `saveConfig` + `previewConfig` nested wire payload (STEP 2)

Both now POST `JSON.stringify(flatToNested(State.config))`. This is the
literal Stage 7.30 SE5 strike 2/3 root-cause fix. overlay.html's
`applyConfig` can now dereference `cfg.card.backgroundTop` etc.

## 8. `previewConfig` in-flight coalescing (STEP 3)

Ported from legacy customize.html lines 4450-4479. `_previewInFlight` +
`_previewPending` flags + recursive flush-trailing-state-once. First
event always fires immediately; subsequent events during a fetch in-flight
flip pending; on resolution the pending state flushes once. Bounds server
load during rapid slider drags without lagging the first event.

## 9. `bindButton` legacy parity (STEP 3)

`c-spec-sensitivity-reset` previously logged-only. Now reads
`config.actionResetTarget` + `config.resetTo` (already on the
SETTINGS_CONFIG entry per Stage 7.28) and dispatches the legacy `input`
event on the slider. The slider's bind handler then runs the normal
pipeline (State.config update + applyControlValue + previewConfig).

## 10. Round-trip smoke (STEP 4)

Two-tier verification via preview MCP at
`http://localhost:8765/customize_v2_archive.html`:

- **Translator self-test** (`window.__roundTripSelfTest`): 10 controls,
  identity round-trip PASS.
- **Live-control smoke**: 12 controls covering 8 input types
  (slider, toggle, colorPair, select, text, colorList; number behaves
  identically to slider; button is no-state). Each test fires a DOM
  event, then verifies flat State.config[id] AND
  flatToNested(State.config) nested path. 12 / 12 PASS.
- **Wire-payload inspection**: standalone short-circuits the real fetch,
  but the payload that WOULD be POSTed is `JSON.stringify(flatToNested
  (State.config))`. Captured shape verified nested:
  `{"masters":{"overallSize":80,"glowEnabled":false},"card":{"backgroundTop":"#abcdef"...}`.
- **Coverage**: 118 / 118 mapped; 0 orphans either direction.
- **Console**: 0 errors / 0 warnings.

# Closure SHA256

| File | SHA256 | Status |
|---|---|---|
| `src/tray.ps1` | `19011F0BD093CEA5...` | MATCH 7.30 |
| `src/tray_native/tray_native.cs` | `6B9804A1AB700006...` | MATCH 7.30 |
| `src/launcher.cs` | `291ED4C92B9BEA39...` | MATCH 7.30 |
| `src/server.js` | `C15ED9310CB33044...` | MATCH 7.30 |
| `src/overlay.html` | `9A7CC817515FFCC0...` | MATCH 7.30 |
| `src/customize.html` (LEGACY production) | `7E98377DC97F83B3...` | UNCHANGED (Stage 7.24 closure SHA carried) |
| `src/customize_v2_archive.html` (work target) | `AD7DABFC97AAB47E...` | new closure (Stage 7.30 SE5-strike-1 base + Stage 7.30.1 translator) |

# Evidence files (`evidence/s7_30_1/`)

1. `round_trip_smoke.json` -- translator self-test results + 12-test
   live-control smoke + wire-payload inspection + console state.
2. `coverage_verification.json` -- 118 / 118 coverage + 0 orphans
   + intentionally-unmapped entries documented.

# Standing rule introduced this stage

**Round-trip smoke pattern.** Any future stage that changes the data shape
between customize.html and overlay.html MUST include a round-trip smoke
test (DOM event -> State.config update -> wire-payload-shape verification)
before the operator gate. Stage 7.28 / 7.29 only verified DOM counts; the
gap there hid the Stage 7.30 SE5 strike 2/3 bug all the way to PyWebView.

# Out-of-scope verification

| Item | Touched? |
|---|---|
| `src/customize.html` (LEGACY production) | NO (SHA carried verbatim) |
| `src/overlay.html` | NO |
| `src/server.js` | NO |
| WPF | NO |
| Other protected files | NO |
| Re-swap to v2 | NO (Stage 7.30.2 scope) |
| New features | NO |
| Stage 7.25 research re-do | NO |
| PyWebView origin gate | NO (Stage 7.30 SE5 strike 1 preserved in v2_archive) |

# Stage 7.30.2 handoff

Operator-approved scope: **re-attempt the swap with the translator in place**.

- `src/customize_v2_archive.html` is now the candidate file. It has:
  - Stage 7.29 v2 content (4 features port + 22 themes + 8 badges + search + collapse + polish)
  - Stage 7.30 SE5 strike 1/3 fix (origin gate accepts both localhost + 127.0.0.1)
  - Stage 7.30.1 FLAT_TO_NESTED translator + nested wire payloads + bindButton legacy parity + previewConfig coalescing
- Stage 7.30.2 STEPs (operator can author the brief; minimal expected):
  1. Pre-swap baseline (counts + SHA)
  2. Atomic git mv (customize.html -> customize_legacy.html; customize_v2_archive.html -> customize.html)
  3. Cold rebuild + SE2 + install verification
  4. Round-trip smoke at the new production path (this should now PASS in PyWebView at localhost:4242)
  5. Operator-light gate (same evidence pattern as Stage 7.30 but with the translator-verified payload)
  6. Closure -- REBUILD CYCLE COMPLETE

# Constraints honored (SE1-SE8)

- **SE1** per-STEP internal verification: yes (translator self-test + DevTools after each step)
- **SE2** mandatory rebuild log inspection: N/A (HTML-only stage; no `_full_rebuild.ps1` invocation needed)
- **SE3** mandatory diff review after every commit: yes (5 commits, all scope-matched)
- **SE4** no "continue" shortcut at gate: yes (operator gave explicit `PASS`)
- **SE5** mistake handling: 0 cycles (clean execution)
- **SE6** three-strike escalation: not triggered (0 / 3)
- **SE7** no autonomous scope expansion: stayed within scope; re-swap parked for Stage 7.30.2
- **SE8** protected files SHA256 verified at STEP 0 + STEP 5: all UNCHANGED end-to-end

# Lessons / standing rules

1. **Round-trip smoke is mandatory** for cross-document data-shape changes (see Standing rule above).
2. **Research-first sourcing**: every mapping path traces to a specific legacy code line. 0 invented paths.
3. **Coverage table format** (c-* ID | nested path | source line) makes review fast and orphan detection trivial.
4. **In-flight coalescing > debounce** for live preview during rapid input events. Patterns:
   - debounce: every event resets a timer; the LAST event fires after N ms. Lags the first input visible to user.
   - in-flight coalescing (legacy + Stage 7.30.1): first event always immediate; subsequent events while a fetch is in-flight flip a "pending" flag; resolution flushes pending state once. Crisp first response + bounded server load.

== END OF V14_S7_30_1_REPORT.md ==
