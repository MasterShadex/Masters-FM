# V14 Stage 7.30.4 -- REPORT (CLOSED -- operator PASS)

customize MEGA stage -- 5 items bundled. Operator re-gate verdict: PASS
(after 1 SE5 cycle). Strikes consumed: 1 / 3.

## Items delivered (all in src/customize.html; committed)

- Item 1 -- Preset Manager: modal UI (list / save / load / delete) wired to the
  existing server endpoints; replaces the v14.1.0 alert stub. Overwrite-on-
  existing-name confirm folded in. Export / Import DEFERRED (operator instruction).
- Item 2 -- V1 organization restored: Advanced supercat removed; 6 legacy
  supercats (start / look / text / effects / audio / layout); 56 advanced rows
  gated by `data-advanced` + `body.show-advanced`; "Show advanced" toggle +
  localStorage `customize_show_advanced` (default off).
- Item 3 -- Section separation: `.supercat` margin-bottom 8 -> 12px (var(--s-3));
  Stage 7.30.3 row density preserved.
- Item 4 -- Label renames (4 of 12 proposed): c-spec-response -> "Reaction time",
  c-spec-fps -> "Animation frame rate", c-border-spd -> "Border spin speed",
  c-border-colors -> "Border colors"; JARGON_MAP synced. Config keys (c-* IDs) unchanged.
- Item 5 -- Supercat polish: border-left 3 -> 2px (pad 9 -> 10px compensation);
  collapsed accent stripe opacity 0.6; box-shadow only when expanded.

## SE5 strike 1/3 (gate-1 FAIL: Preset Load + Delete returned HTTP 405)

Root cause: client guessed POST for load / delete. Fix (research-first, mirrors
`customize_legacy.html` + `server.js` verbatim): load -> `GET /load-preset?name=`,
delete -> `DELETE /delete-preset?name=`, save POST unchanged. +OVERWRITE-on-
existing-name confirm folded in (legacy pmOverwrite). Commit `f2133fa`.

## Verification

- Round-trip smoke: 10 / 10 control types passed; console 0 warn / 0 error (smoke.json).
- Server-side endpoint probe (read-only, nonexistent name): `GET /list-presets`
  200; `GET /load-preset` 404 (not 405); `DELETE /delete-preset` 200 (not 405).
  405 symptom confirmed gone at protocol level.
- Cold rebuild: 5 / 5 green, "REBUILD DONE OK" exit 0, log 0 error / warning.
- Install customize.html SHA == source `5E59A262AC485537...` (MATCH).
- Operator GUI re-test PASS: Save / Load / Delete / overwrite confirmed working
  in fresh WebView2.

## IMMUTABLE / scope preserved

- SETTINGS_CONFIG entries: 120 (no new). FLAT_TO_NESTED_MAP: 118 (immutable).
  THEMES: 22. self-test ok.
- `customize_legacy.html` UNCHANGED (`7E98377D...`). `overlay.html` UNCHANGED
  (`9A7CC817...`). No protected / WPF / server.js changes. customize.html is the
  only source file touched.
- `version.json` restored to HEAD (msi_sha256 build-artifact churn discarded;
  version stays 14.0.0 -- no bump).

## Closure SHA256

- `src/customize.html`: `5E59A262AC485537...` (Stage 7.30.4 closure)
- `src/customize_legacy.html`: `7E98377DC97F83B3...` UNCHANGED (LEGACY_SHA)
- `src/overlay.html`: `9A7CC817515F...` UNCHANGED
- `version.json`: 14.0.0 (no bump; restored)

## Out of scope (flagged, awaiting operator OK)

- Preset Export / Import (legacy had both: .json download line 5068, upload
  5099). Folded in OVERWRITE only this cycle per operator. Candidate for a
  follow-up stage -- DECISION PENDING (operator asked at gate, answered "pass"
  without ruling on Export/Import).
- ~194 untracked entries in `git status`, many malformed-command debris (names
  like `,`, `{`, `return`, `JsonNode.Parse`). NOT from this stage (only
  customize.html touched, committed). Pending operator approval to clean --
  nothing deleted.

## Strikes

1 / 3 consumed (SE5 strike 1 -- preset HTTP method). 2 remaining.
