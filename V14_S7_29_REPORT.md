==
== V14_S7_29_REPORT.md  --  Stage 7.29 closure report
== customize.html rebuild FEATURES PORT (4 of 5)
==

# Outcome

PASS via operator gate (STEP 7). 9 STEPs executed. 1 SE5 cycle consumed
(strike 1/3) -- badge first-paint diagnostic + fix. 4 of 5 customize.html
rebuild cycles complete. Stage 7.30 is the final SWAP.

# Headline numbers

| Metric | Value |
|---|---:|
| Themes ported | 22 (verbatim from current customize.html) |
| Theme dropdown options | 22 |
| Master-following badge IDs | 8 (verbatim from Stage 7.21 STEP 4) |
| JARGON_MAP entries | 39 (verbatim from Stage 7.20) |
| Supercats with collapse | 7 (Start here default-open per Stage 7.24) |
| SETTINGS_CONFIG total | 120 (was 119 + new c-theme entry) |
| DOM IDs accounted for | 142 (120 + 22 colorPair pickers) |
| Smoke: search filter `color` -> visible rows | 23 across 5 supercats |
| Smoke: badges present (default state) | 8 / 8 on correct IDs |
| Smoke: console error / warn count | 0 / 0 |
| Stage 7.29 commits | 9 (incl. STEP 6 SE5 fix + log entries) |
| Lines net delta (`src/customize_v2.html`) | +713 (2160 -> 2873) |
| Files touched | 2 (`src/customize_v2.html`, `V14_S7_29_LOG.md`) |
| Protected files touched | 0 |
| SE5 cycles | 1 (badge first-paint; PASS on re-test) |
| Strikes consumed | 1 / 3 |

# Commit chain (Stage 7.28 closure -> Stage 7.29 closure)

```
9b8b3cb  STEP 0  -- features design locked
c7094bb  STEP 1  -- THEMES const (22 themes) + c-theme entry + applyTheme + bindThemeControl
a22b94b  STEP 2  -- master-following badges (CSS + 8 IDs + reactive on apply/change/reset)
b8b664f  STEP 3  -- JARGON_MAP (39 entries) + filterControls + initSearchFilter + Ctrl+F + Esc
2a26bfb  STEP 4  -- supercat collapse CSS + initSupercatCollapse + localStorage + Start here default open
4814d22  STEP 5  -- polish pass (Preset alert v14.1.0 + stale Stage 7.29 comments refreshed)
3a6b3e8  STEP 6  SE5 fix (strike 1/3) -- updateMasterBadge falls back to SETTINGS_CONFIG default
3d81e9d  log     -- STEPs 1-6 entries (themes / badges / search / collapse / polish / smoke incl SE5 fix)
```

Stage 7.28 closure: `e023425`. Stage 7.29 closure: `3d81e9d`.

# Closure SHA256

| File | SHA256 | Status |
|---|---|---|
| `src/tray.ps1` | `19011F0BD093CEA51CB34D053209F33FB3A37DE673777BAB34B5F8F26609533F` | MATCH 7.28 |
| `src/tray_native/tray_native.cs` | `6B9804A1AB70000652A2754E886BE3F05167F40EC136EB2CC6CDD62D8EFA9148` | MATCH 7.28 |
| `src/launcher.cs` | `291ED4C92B9BEA391BA9204323EA41BA60AD7903AF6E6D7BA9404E1056E0BD9D` | MATCH 7.28 |
| `src/server.js` | `C15ED9310CB33044A090878918DC2B89B3FB843901BA0F199D3092EF502A16AF` | MATCH 7.28 |
| `src/customize.html` | `7E98377DC97F83B31DEE96E805479D70CB4DF008D444C8108242FB1AE942C9B0` | UNCHANGED (Stage 7.24 closure SHA carried verbatim) |
| `src/customize_v2.html` | `FFAB016B59A5CF8DA032A155117BDAB06C9DFDAAFAFD9AD1FFC2F669E7249AB6` | new closure (2873 lines, +713 net) |

`version.json` still at `14.0.0` -- no bump (interim rebuild stage).

# What shipped

1. **`THEMES` const (22 themes)** at JS section 1a. Verbatim from current
   customize.html L2302-2567 -- same keys (each key serves as both option
   value AND label), same nested shape (font / masters / card / border /
   glow / nowPlaying / bars / title / artist / spectrum / progressBar /
   timestamps), same sentinel pattern ('var(--accent-master)' for
   accent-following keys), same hex values for theme-specific keys.

2. **`c-theme` SETTINGS_CONFIG entry** at supercat 'look', between the
   masters block and c-font. `options` array computed at
   SETTINGS_CONFIG-build time via `Object.keys(THEMES).map(...)` so it
   always reflects the THEMES const. Default: `'Rainbow (Default)'`.

3. **`applyTheme(themeKey) / THEME_KEY_MAP / getNestedValue`** at JS
   section 8b. Walks 20 mapped nested theme paths (font, masters.accentColor,
   card.{top, bot, angle}, border.colors, glow.{c1, c2}, nowPlaying.{color,
   spacing}, bars.color, title.{color, weight, glow color/size}, artist.{
   color, glow color/size}, spectrum.color, timestamps.color) to flat
   `State.config[c-*-id]`. Unmapped leaves (`glow.intensity`,
   `progressBar.fillColors`, `spectrum.colorMode`, etc.) intentionally
   skipped -- no v2 control yet; revisited at Stage 7.30 swap.

4. **`bindThemeControl` special-case** in `bindControl` dispatcher.
   Restores from State.config (or default), runs applyTheme on init if a
   non-default theme is stored, invokes applyTheme(value) on change.

5. **8 master-following badges**. `MASTER_FOLLOWING_IDS` const lists the
   8 c-* IDs verbatim from Stage 7.21 STEP 4 (c-np-color, c-bars-color,
   c-platform-color, c-platform-dot, c-title-color, c-artist-color,
   c-spec-color, c-ts-color). `.master-badge` CSS pill with background
   bound to `var(--accent-master)` for live colour tracking.
   updateMasterBadge / unfollowMaster / refreshAllMasterBadges helpers
   wired into bindColorPair (per-element change), applyTheme (master
   change cascade), onResetDefaults (reset cascade), and `init()`
   bootstrap (first paint).

6. **`JARGON_MAP` const (39 entries)** at JS section 8d. Verbatim from
   current customize.html L3860-3909.

7. **`filterControls(query) + initSearchFilter()`**. Lowercased substring
   match against control label / id / JARGON_MAP[label] synonyms with
   80ms debounce. Hides non-matching rows; hides supercats with zero
   visible rows. Ctrl+F focuses input; Esc clears + blurs.

8. **Supercat collapse** -- CSS `.supercat.collapsed .supercat-body
   { display: none }` + chevron `transform: rotate(-90deg)`.
   `initSupercatCollapse()` reads `localStorage['supercat_<id>_collapsed']`
   per supercat, wires click toggle + persist. First-load default keeps
   Start here open (Stage 7.24 lock) along with all others.

9. **Polish pass** -- Preset Manager alert text updated `"Stage 7.29"`
   -> `"v14.1.0"`. Stale Stage 7.29-as-future-work comments refreshed
   (search bar, supercats, bindButton). Audit confirms pure-white labels
   (`--c-text-primary: #ffffff`), deep red Reset button (`#dc2626` token
   via `.btn-danger-ghost`), supercat header colour tokens intact
   (`#c0c0c0` -> `#ffffff` on hover), 150ms ease-out animation tokens
   intact. No dead code / TODO / FIXME found.

# Smoke test results (STEP 6)

Per V14_S7_29_LOG.md S6.1-S6.8. Highlights via preview MCP at
`http://localhost:8765/customize_v2.html`:

- 22 theme dropdown options (Rainbow (Default) -> Dreamcore)
- 120 control rows total
- 8 master-following badges on correct IDs (after SE5 fix)
- Search "color" -> 23 visible rows across 5 supercats; clear restores 120
- Supercat 'look' collapse toggle + localStorage persist '1'/'0'
- Theme Neon Blue: `--accent-master` -> `#00c8ff`; c-card-top, c-font
  applied; badges preserved
- Reset: factory defaults restored on font / accent / card; badges
  re-appear on all 8 IDs
- Preset Manager alert: "Preset Manager: coming in v14.1.0"
- Console: 0 errors, 0 warnings

# SE5 cycle log (strike 1/3)

**Diagnosis (S6.2):** initial smoke ran `refreshAllMasterBadges` after
`refreshControlValues + applyConfig + initBindings`, but in standalone
mode `loadConfig` short-circuits via Stage 7.27 origin gate, leaving
`State.config = {}`. `updateMasterBadge` checked
`State.config[id] === 'var(--accent-master)'` -- with `undefined !==
'var(--accent-master)'` no badge mounted.

**Fix:** `updateMasterBadge` now falls back to
`SETTINGS_CONFIG.find(c => c.id === id).default` when `State.config[id]`
is `undefined`. Sentinel detection then matches the factory default and
the badge mounts correctly on first paint.

Re-test: 8/8 badges present on the correct 8 IDs.

# Out-of-scope verification

| Item | Touched? |
|---|---|
| Tooltips replacing inline help | NO (DEFERRED v14.1.0) |
| Preset Manager UI / save / load / delete | NO (DEFERRED v14.1.0; alert only) |
| `src/customize.html` | NO (SHA carried verbatim from Stage 7.24) |
| `src/overlay.html` | NO |
| WPF | NO |
| `src/server.js` | NO |
| Other protected files | NO |
| New CSS variables in Apply-to-OBS contract | NO |
| New SETTINGS_CONFIG entries beyond c-theme | NO |

# Known deferred (v14.1.0 + Stage 7.30 considerations)

- **Tooltips** (v14.1.0): replace tooltip-less labels with hover info
  icons for jargon-heavy controls. JARGON_MAP entries can drive the
  tooltip text.
- **Preset Manager UI** (v14.1.0): full save / load / delete / list
  modal. Server endpoints (`/save-preset`, `/list-presets`, etc.) already
  exist in customize.html Stage 7.18 -- only the UI side needs porting.
- **Sensitivity reset action** (v14.1.0): `c-spec-sensitivity-reset`
  button is currently a logging no-op; needs wiring to reset the
  corresponding sensitivity slider to its default.
- **Unmapped theme leaves** (Stage 7.30 evaluate): `glow.intensity`,
  `glow.pulseDuration`, `glow.enabled`, `title.glowEnabled`,
  `artist.glowEnabled`, `spectrum.colorMode`, `spectrum.barCount`,
  `spectrum.heightMult`, `spectrum.barRadius`,
  `progressBar.fillColors`, `card.borderThickness`, `border.enabled`,
  `border.spinDuration`. Decide which need v2 controls before the swap.
- **8 layout vis/lock dynamic rows** (Stage 7.30 evaluate): only
  spectrum's static pair is in SETTINGS_CONFIG; the 8 dynamic rows
  generated by current customize.html's `rebuildLayoutEditor()` are
  not yet ported.

# Next stage

After PASS, operator approves Stage 7.30 (THE SWAP -- final stage of
rebuild cycle). Replaces production `src/customize.html` with the v2
file; renames or archives the old; updates any references in
`src/server.js` (the canonical `/customize` route) or build scripts.

== END OF V14_S7_29_REPORT.md ==
