==
== V14_S7_25_CUSTOMIZE_REBUILD_RESEARCH.md
== customize.html rebuild research deliverable
== Stage 7.25 (research stage; NO source changes)
== HEAD baseline: 0335724 (Stage 7.24 closure)
==

# Executive summary

`src/customize.html` is **5,473 lines** (~265 KB) of organically-grown HTML/CSS/JS spanning 11 sequential stages (Stage 7.17 v14.0.0 cut through Stage 7.24 targeted polish). The file works correctly and has been operator-PASSed at every stage gate, but the structure shows the natural debt of 11 stacked feature additions:

- **Two `<style>` blocks** (lines 10-1075 + 5260-5470) instead of one
- **24 CSS chapter dividers** organized around HTML elements rather than functional concerns
- **84 CSS variables** at `:root`, with a `--dur-fast` / `--dur-fast-v2` split documenting that Stage 7.19 added new tokens without retiring the old ones
- **141 unique setting IDs** (`id="c-*"`) wired into a single ~370-line `initBindings()` function
- **22 themes** in a single 270-line const, mixing accent-following sentinels (`var(--accent-master)`) with theme-specific hex values per Stage 7.20.6
- **11 layout templates** (separate from themes) at lines 3138-3327
- **6 supercats** added by Stage 7.21 wrapping the 17 sections at runtime
- A search system (Stage 7.20), advanced toggle (Stage 7.20), welcome banner (Stage 7.21), following-master badges (Stage 7.21), supercat active-state via `:has()` (Stage 7.24), and START HERE default-expand (Stage 7.24) all layered on top

The Apply-to-OBS contract is narrower than expected — only **the 5 master CSS variables** (`--accent-master`, `--overall-scale`, `--text-scale`, `--glow-master-enabled`, `--animations-master-enabled`) plus **the 141 setting IDs as config keys** need to survive a rebuild. Everything else (the 70+ customize-only design tokens, the CSS chapter organization, the JS file structure) can be refactored freely.

This document catalogs everything across 7 sections and proposes a 5-stage migration plan (7.26-7.30) for the rebuild. The research doc itself is a v14.1.0 refactor roadmap regardless of whether the rebuild proceeds.

---

# Section 1: Setting IDs inventory

## 1.1 Total counts

| Metric | Count | Notes |
|---|---:|---|
| Unique `id="c-*"` attributes in HTML | **141** | confirmed via `grep -oE 'id="c-[a-z0-9_-]+"' src/customize.html \| sort -u \| wc -l` |
| Total occurrences (incl. duplicates) | 142 | one ID appears twice (paired color picker + hex text input pattern) |
| Brief estimate from prior stages | 149-154 | overcount in early estimates; **141 is the truth** |
| First ID line | 1139 (`c-master-accent-color-p`) | inside Quick Settings section |
| Last ID line | 2180 (`c-anim-crossfade`) | inside Slide-in animation section |
| HTML span | ~1042 lines | from L1139 to L2180 inclusive |

## 1.2 Section distribution

17 sections wrap the 141 controls. Sections are identified by `<div class="sec-header" data-sec="X">` markers. Counts derived from grep + manual range mapping:

| Section ID | Label (Stage 7.19 friendly) | Line range | Approx control count | Added in |
|---|---|---|---:|---|
| `quick-settings` | Quick Settings | 1126-1185 | 5 (the masters) | Stage 7.20 |
| `themes` | Themes | 1186-1198 | 0 (theme grid only; no c-* IDs) | Stage 7.20.6 (sentinel) |
| `general` | General | 1199-1218 | 1 (`c-overlay-opacity`) | original |
| `layout` | Layout | 1219-1267 | 2 (+18 dynamic visibility/lock pairs for c-layout-vis-* / c-layout-lock-*) | Stage 7.13 |
| `font` | Font | 1268-1295 | 1 (`c-font`) | original |
| `card` | Card appearance | 1296-1366 | 7 (radius, thickness, angle, top, bot, blur, opacity) | original |
| `dyncolors` | Auto-color from album art | 1367-1461 | 14 toggles | original |
| `border` | Spinning border | 1462-1490 | 3 + color list | original |
| `glow` | Outer glow | 1491-1536 | 5 | original |
| `textglow` | Text glow | 1537-1584 | ~16 (4 element groups × 3-4 controls) | original |
| `art` | Album art | 1585-1627 | 4 | original |
| `np` | "Now Playing" label | 1628-1709 | ~8 | original |
| `platform` | Platform badge | 1710-1776 | ~10 | original |
| `text` | Track and artist text | 1777-1902 | ~14 | original |
| `spectrum` | Audio bars (Spectrum) | 1903-2049 | ~16 | original |
| `progress` | Progress bar and time | 2050-2130 | ~10 | original |
| `anim` | Slide-in animation | 2131-2230 | 5 | original |

**Section organization observation:** `quick-settings` (Stage 7.20) appears FIRST followed by `themes` (Stage 7.20.6), then `general` (original), then `layout`. The early sections are recent additions; the original section order starts at `general`/`layout`. Stage 7.21 supercats re-grouped these into 6 super-categories at runtime via `restructureSidebar()`.

## 1.3 Control-type distribution (representative; not exhaustive transcription)

Derived from sampling the HTML pattern between each `<div class="sec-header">`:

| Type | Approx count | Notes |
|---|---:|---|
| `<input type="checkbox">` (toggle) | ~50 | wrapped in `<label class="toggle">` |
| `<input type="range">` (slider) | ~50 | usually with `<span class="range-val">` value display |
| Color pair: `<input type="color">` + `<input type="text" class="hex-in">` | ~25 pairs (50 IDs total) | swatch picker + hex string input |
| `<select>` (dropdown) | ~8 | font, layout grid, weight, mode, easing, dir |
| `<input type="text">` (text input) | ~4 | np label, platform label, custom text |
| Special: `c-spec-sensitivity-reset` button | 1 | inline reset button next to slider |
| `c-border-colors` color list | 1 | dynamic `.color-list` container |

## 1.4 The 5 masters (Quick Settings, Stage 7.20)

| ID | Type | Label | CSS Variable | Default | Notes |
|---|---|---|---|---|---|
| `c-master-accent-color` + `-p` pair | color | Master Accent Color | `--accent-master` | `#c060ff` | Paired picker + hex text; followed by 8 "Following accent" badges (Stage 7.21) on per-element accent pickers |
| `c-master-overall-size` | range 50-150 step 5 | Master Overall Size | `--overall-scale` | `100` (1.0) | applied to overlay `.card` transform |
| `c-master-text-size` | range 50-150 step 5 | Master Text Size | `--text-scale` | `100` (1.0) | applied via `calc(* var(--text-scale))` |
| `c-master-glow` | toggle | Master Glow | `--glow-master-enabled` + body `.no-glow` class | on | binary; class toggles override per-element |
| `c-master-animations` | toggle | Master Animations | `--animations-master-enabled` + body `.no-animations` class | on | binary; clock kept ticking per Stage 7.15 |

## 1.5 Advanced-only controls (Stage 7.20)

`.advanced-only` is applied at runtime by `markAdvancedElements()` (line 4060) to specific section rows and sub-sections. The CSS rule (line 755) hides them by default; `body.show-advanced .advanced-only { display: revert; }` shows them when the Advanced toggle is on.

Operator's earlier "Item 2 = A" decision (per the rebuild brief) is to **consolidate** all advanced-only controls into a single dedicated "Advanced" section in the rebuild rather than scatter them across other sections behind a global toggle.

Marked-advanced sections (sampled from `markAdvancedElements()` calls L4060-4115): card sub-sections (blur/opacity beyond the core radius/thickness/colors), most of spectrum's response/smooth/fps controls, dyncolors' less-common toggles (timestamps glow, progressbar, border), platform badge sub-options, font weight/spacing, anim easing details.

**Exact rebuild migration of "marked advanced" → "Advanced section" is a Stage 7.28 question.**

## 1.6 Complete ID list grouped by section

For verification (this is the inventory deliverable). Order matches HTML reading order.

**Quick Settings (5):** `c-master-accent-color`, `c-master-accent-color-p`, `c-master-overall-size`, `c-master-text-size`, `c-master-glow`, `c-master-animations` (6 IDs total since color pair counts as 2)

**General (1):** `c-overlay-opacity`

**Layout (20):** `c-layout-on`, `c-layout-grid`, plus 18 dynamic IDs of the form `c-layout-vis-<node>` and `c-layout-lock-<node>` for each of 9 layout nodes (spectrum, art, title, artist, np, platform, progressbar, timestamps, etc.)

**Font (1):** `c-font`

**Card (7):** `c-card-radius`, `c-card-thick`, `c-card-angle`, `c-card-top-p`/`c-card-top`, `c-card-bot-p`/`c-card-bot`, `c-card-blur`, `c-card-opacity` (9 IDs with color pairs)

**Auto-color (14 toggles):** `c-dynamic-colors`, `c-dyn-bg`, `c-dyn-glow`, `c-dyn-title`, `c-dyn-titleglow`, `c-dyn-artist`, `c-dyn-artistglow`, `c-dyn-np`, `c-dyn-platform`, `c-dyn-npglow`, `c-dyn-platformglow`, `c-dyn-spectrum`, `c-dyn-timestamps`, `c-dyn-tsglow`, `c-dyn-progressbar`, `c-dyn-border`

**Spinning border (3 + color list):** `c-border-on`, `c-border-spd`, `c-border-colors` (the container)

**Outer glow (5):** `c-glow-on`, `c-glow-c1-p`/`c-glow-c1`, `c-glow-c2-p`/`c-glow-c2`, `c-glow-int`, `c-glow-pulse`

**Text glow (~16, 4 element groups):**
- "Now Playing" glow: `c-np-glow-on`, `c-np-glow-color-p`/`c-np-glow-color`, `c-np-glow-size`
- Platform glow: `c-platform-glow-on`, `c-platform-glow-color-p`/`c-platform-glow-color`, `c-platform-glow-size`
- Title glow: `c-title-glow-on`, `c-title-glow-color-p`/`c-title-glow-color`, `c-title-glow-size`
- Artist glow: `c-artist-glow-on`, `c-artist-glow-color-p`/`c-artist-glow-color`, `c-artist-glow-size`
- Timestamps glow: `c-ts-glow-on`, `c-ts-glow-color-p`/`c-ts-glow-color`, `c-ts-glow-size`

**Album art (4):** `c-art-on`, `c-art-position`, `c-art-w`, `c-art-fade`

**"Now Playing" label (~8):** `c-np-text`, `c-np-size`, `c-np-color-p`/`c-np-color`, `c-np-spacing`, plus existing `c-np-glow-*` family from text glow

**Audio bars (Spectrum) (~16):** `c-bars-on`, `c-bars-color-p`/`c-bars-color`, `c-bars-count`, `c-bars-speed`, `c-keep-on-pause`, `c-spec-on`, `c-spec-autogain`, `c-spec-sensitivity`, `c-spec-sensitivity-reset` (button), `c-spec-mode`, `c-spec-color-p`/`c-spec-color`, `c-spec-mirror`, `c-spec-opacity`, `c-spec-bars`, `c-spec-gap`, `c-spec-radius`, `c-spec-min`, `c-spec-height`, `c-spec-response`, `c-spec-smooth`, `c-spec-fps`

**Platform badge (~10):** `c-platform-on`, `c-platform-sc-label`, `c-platform-color-p`/`c-platform-color`, `c-platform-dot-p`/`c-platform-dot`, `c-platform-size`, `c-platform-weight`, `c-platform-spacing`

**Track and artist text (~14):**
- Title: `c-title-size`, `c-title-weight`, `c-title-color-p`/`c-title-color`, `c-title-mspd`, `c-title-mpause`, `c-title-spacing`
- Artist: `c-artist-size`, `c-artist-weight`, `c-artist-color-p`/`c-artist-color`, `c-artist-spacing`, `c-artist-mspd`, `c-artist-mpause`

**Progress + Timestamps (~10):** `c-prog-on`, `c-prog-h`, `c-prog-radius`, `c-prog-track-p`/`c-prog-track`, `c-prog-f0-p`/`c-prog-f0`, `c-prog-f1-p`/`c-prog-f1`, `c-prog-f2-p`/`c-prog-f2`, `c-ts-size`, `c-ts-color-p`/`c-ts-color`

**Slide-in animation (5):** `c-anim-dur`, `c-anim-slide`, `c-anim-dir`, `c-anim-easing`, `c-anim-crossfade`

== END OF SECTION 1 ==

---

# Section 2: CSS Variables + Apply-to-OBS contract

## 2.1 Total counts

| Metric | Count | Source |
|---|---:|---|
| Unique `--*` variable definitions in customize.html `:root` | **84** | grep `^\s*--[a-z]` (lines 14-135 + 5 masters at L44-48) |
| Unique `var(--*)` usages in customize.html | 70 | grep `var\(--`; ~14 vars defined but unused, ~28 vars used multiple times |
| Unique `var(--*)` usages in overlay.html | **14** | overlay-side consumers (the actual Apply-to-OBS surface) |
| Unique `--*` variable definitions in overlay.html `:root` | 30+ | overlay defines its OWN :root with locally-scoped copies of the vars it uses; the names happen to match customize's by convention |

## 2.2 The TRUE Apply-to-OBS contract

After tracing the data flow, the Apply-to-OBS contract is NOT the CSS variable names themselves -- both files define their own `:root` and use `var(--name)` resolved against their own scope. The contract is actually:

**(A) The 5 master config keys** (`config.masters.*`), which customize.html writes and overlay.html reads:

| Config key | Customize sets CSS var on its :root | Overlay reads from config and applies | Default |
|---|---|---|---|
| `masters.accentColor` (hex string) | `--accent-master` (via setProperty at L3590, L4266) | `--accent-master` (overlay's own :root + per-element `var(--accent-master)`) | `#c060ff` |
| `masters.overallSize` (number 50-150) | `--overall-scale` (value/100; L3599, L4267) | `--overall-scale` on overlay's :root, used in `transform: scale()` on `.card` | `100` |
| `masters.textSize` (number 50-150) | `--text-scale` (value/100; L3610, L4268) | `--text-scale` on overlay's :root, used in `calc(* var(--text-scale))` on text font-sizes | `100` |
| `masters.glowEnabled` (bool) | `--glow-master-enabled` (1/0; L3621, L4269) | `--glow-master-enabled` + `.no-glow` body class (Stage 7.20.5 STEP 5) | `true` |
| `masters.animationsEnabled` (bool) | `--animations-master-enabled` (1/0; L3633, L4270) | `--animations-master-enabled` + `.no-animations` body class (Stage 7.20.5 STEP 6) | `true` |

**(B) The 141 setting IDs as config keys** (per Section 1). Each `c-X` maps to a `config.<section>.<setting>` path via the bindings established in `initBindings()` at L3494. Overlay.html reads the saved config and applies these to specific element styles.

## 2.3 What this means for the rebuild

The literal CSS variable NAMES in customize.html can be renamed/refactored freely as long as:
1. The 5 setProperty calls (L3590-3633, L4266-4270) still produce the right CSS variable names that overlay's :root and selectors reference (`--accent-master`, `--overall-scale`, `--text-scale`, `--glow-master-enabled`, `--animations-master-enabled`)
2. The saved config object preserves its `masters.{accentColor,overallSize,textSize,glowEnabled,animationsEnabled}` keys plus the 141 c-* mapped paths

**Conclusion:** Treat the **5 master CSS variable names** as the IMMUTABLE contract (renaming them requires synchronized changes in BOTH customize.html and overlay.html — out of scope for a customize-only rebuild). The **other 79 CSS variables** in customize.html `:root` are 100% customize-only (UI styling, fonts, spacing, shadows, durations) and can be renamed/consolidated freely.

## 2.4 IMMUTABLE -- the 5 master CSS variables

These names MUST appear unchanged in the rebuild:
- `--accent-master`
- `--overall-scale`
- `--text-scale`
- `--glow-master-enabled`
- `--animations-master-enabled`

## 2.5 customize-only CSS variables (free to refactor)

Grouped by purpose for the rebuild reorganization:

### Surfaces / backgrounds
- `--bg`, `--surface-1`, `--surface-2`, `--surface-3`
- legacy aliases: `--sidebar-bg`, `--panel`, `--panel-hover`, `--hover`, `--input-bg`

### Text colors
- `--text-primary`, `--text-secondary`, `--text-tertiary`
- legacy aliases: `--text`, `--text-muted`

### Brand / accent
- `--brand-base`, `--brand-deep`, `--brand-glow`
- `--accent-bar` (linear gradient)
- legacy aliases: `--accent`, `--accent-hover`, `--accent-bright`, `--accent-glow`

### Borders
- `--border-subtle`, `--border-focus`
- legacy aliases: `--border`, `--border-light`, `--input-border`

### Semantic tags
- `--success`, `--error`, `--warning`, `--info`

### Typography
- `--font-ui`, `--font-mono`
- `--fs-display`, `--fs-heading`, `--fs-subheading`, `--fs-body-strong`, `--fs-body`, `--fs-caption`, `--fs-tiny`, `--fs-mono`
- `--fw-regular`, `--fw-medium`, `--fw-semibold`
- `--lh-body`, `--lh-caption`

### Radius scale
- `--r-card`, `--r-button`, `--r-input`, `--r-pill`, `--r-tiny`

### Spacing rhythm (4px grid)
- `--sp-1` (4px), `--sp-2` (8px), `--sp-3` (12px), `--sp-4` (16px), `--sp-5` (24px), `--sp-6` (32px)

### Shadows
- `--shadow-dialog`, `--shadow-card-rest`, `--shadow-card-hover`, `--shadow-button-hover`, `--shadow-input-focus`

### Animation durations + easings (DUPLICATED -- code smell)
**Original Stage 7.13/7.17 tokens:**
- `--dur-press` (80ms), `--dur-fast` (150ms), `--dur-standard` (220ms), `--dur-slow` (280ms), `--dur-accent-bar` (8000ms)
- `--ease-standard`, `--ease-out`, `--ease-spring`, `--ease-in`

**Stage 7.19 "v2" tokens (added without retiring the originals):**
- `--dur-fast-v2` (200ms), `--dur-standard-v2` (300ms), `--dur-slow-v2` (450ms)
- `--ease-windows`, `--ease-macos`, `--ease-emphasized`

**Code smell:** Stage 7.19 added the v2 tokens for the Windows 11 / macOS feel but kept the v1 set. The rebuild should consolidate -- e.g. retire v1 in favor of v2 with a single rename pass, OR pick a single canonical token set per "what the project actually uses".

### Layout constants
- `--sidebar-w` (370px), `--topbar-h` (52px)

## 2.6 Stage 7.20 master CSS variables (IMMUTABLE, listed for clarity)

Already covered in 2.4 + the live data flow in 2.2. These are the only contract variables.

== END OF SECTION 2 ==

---

# Section 3: Themes inventory

## 3.1 Source location and structure

`const THEMES = { ... }` at lines **2302-2567** (~265 lines, the largest constant in the JS).

Followed by `const THEME_SWATCH_COLORS = { ... }` at line **2570** (~28 lines) which derives the first 4 border colours per theme for swatch previews.

Theme grid HTML lives in the Themes section header at line 1186; the grid is built at runtime by `function buildThemeGrid()` (L2598). Theme application happens via `function applyTheme(name)` (L2615).

## 3.2 Theme count

| Source | Count | Notes |
|---|---:|---|
| Theme keys in THEMES object | 22 | "Rainbow (Default)" + 21 themed presets |
| Default theme | 1 | "Rainbow (Default)" -- empty object `{}` falls through to DEFAULTS |
| Themed presets (non-default) | 21 | each has explicit `masters.accentColor` and per-element overrides |

## 3.3 Theme name list

In file order (lines 2303-2554):

| # | Name | Line | masters.accentColor | Primary character |
|---:|---|---:|---|---|
| 1 | Rainbow (Default) | 2303 | (none; falls to default) | colorful neutral fallback |
| 2 | Neon Blue | 2304 | `#00c8ff` | electric blue |
| 3 | Hot Pink | 2316 | `#ff4499` | pink |
| 4 | Retro Orange | 2328 | `#ff9900` | orange |
| 5 | Synthwave | 2340 | `#ff00cc` | magenta/purple |
| 6 | Forest Green | 2353 | (per-theme hex) | green |
| 7 | Crimson | 2365 | (per-theme hex) | crimson |
| 8 | Midnight | 2377 | (per-theme hex) | dark blue |
| 9 | Cherry Blossom | 2389 | (per-theme hex) | soft pink |
| 10 | Minimal White | 2401 | (per-theme hex) | neutral white |
| 11 | Vaporwave | 2414 | (per-theme hex) | cyan/magenta |
| 12 | Aurora | 2427 | (per-theme hex) | green/teal |
| 13 | Royal Purple | 2440 | (per-theme hex) | purple |
| 14 | Coffee | 2453 | (per-theme hex) | brown |
| 15 | Volcano | 2465 | (per-theme hex) | red/orange |
| 16 | Ice Crystal | 2478 | (per-theme hex) | ice blue |
| 17 | Galaxy | 2491 | (per-theme hex) | deep purple |
| 18 | Sunset | 2504 | (per-theme hex) | sunset orange/red |
| 19 | Lime | 2517 | (per-theme hex) | yellow-green |
| 20 | Vintage Sepia | 2529 | (per-theme hex) | sepia brown |
| 21 | Cyber Matrix | 2541 | `#00ff41` | matrix green |
| 22 | Dreamcore | 2554 | `#d9b3ff` | pastel purple |

## 3.4 Sentinel vs hex keys (Stage 7.20.6 pattern)

Each non-default theme follows the pattern established in Stage 7.20.6:

**Accent-following keys (hold the sentinel string `var(--accent-master)`):** values that the operator wants to "follow" the active master accent. When the user changes the Master Accent Color via Quick Settings, these elements update live. The following-master badge in the customize UI (Stage 7.21) makes this visible.

Per-theme accent-following keys (consistent across all 21 themed presets):
- `nowPlaying.color`
- `bars.color`
- `title.color`
- `artist.color`
- `spectrum.color` (nested inside spectrum object)
- `timestamps.color`

(8 per-element accent pickers per Stage 7.21 badge count; the same ~6 keys above sometimes paired with sentinel uses inside nested objects.)

**Theme-specific hex keys (hardcoded; do NOT follow master accent):**
- `card.backgroundTop`, `card.backgroundBottom`, `card.backgroundAngle`
- `border.colors` (array of 5 hex colors)
- `glow.color1`, `glow.color2`, optional `glow.intensity`, `glow.pulseDuration`
- `progressBar.fillColors` (array of 3 hex colors)
- Per-element decorative glow: `title.glowEnabled/glowColor/glowSize`, `artist.glowEnabled/glowColor/glowSize`

## 3.5 Pattern analysis

**Common shape per theme** (all 21 non-default themes share this shape; only the values differ):

- `font` -- one of ~8 google-font families
- `masters.accentColor` -- one of 21 unique hex values
- `card` -- 3 keys (top, bottom, angle gradient)
- `border.colors` -- 5-hex palette
- `glow` -- 2-4 keys (color1, color2, optional intensity, optional pulseDuration)
- `nowPlaying`, `bars`, `title`, `artist`, `spectrum`, `timestamps` -- all accent-following via sentinel
- `progressBar.fillColors` -- 3-hex palette

**Refactor candidate (v14.1.0 not Stage 7.26):** Since all 21 themes share the same shape with only value variations, the THEMES const could be refactored:
- Theme-base + variants pattern (DRY)
- Or external `themes.json` loaded at init
- Either approach is out of scope for the rebuild scaffold

## 3.6 The `applyTheme(name)` function (L2615)

Deep-merges `THEMES[name]` over `DEFAULTS`, then re-applies bindings + setProperty + per-element style refresh. The sentinel string flows through as a literal CSS value that, at render time, resolves against the active `--accent-master`. This is Stage 7.20.6 sentinel substitution behavior.

`buildThemeGrid()` at L2598 renders one button per theme. `THEME_SWATCH_COLORS` at L2570 derives the swatch preview (first 4 border colors per theme).

## 3.7 Implications for rebuild

- **The 22 theme names + per-theme accentColor + per-theme hex keys are config-level data, NOT structural.** Rebuild scaffold (Stage 7.26) can extract them to a data constant or external file without changing user-visible behavior.
- **The sentinel `'var(--accent-master)'` string is essential and MUST be preserved.** Removing it would break the "accent follows master" UX from Stage 7.20.5/7.20.6.
- **`applyTheme` + `buildThemeGrid` + `deepMerge` are the minimum required theme JS.** Other current theme-related code (e.g. `THEME_SWATCH_COLORS` derivation) is display optimization.

== END OF SECTION 3 ==

---

# Section 4: JavaScript inventory

## 4.1 Source location

Single `<script>` block at **lines 2232-5211** (~2,979 lines, the largest single block in the file).

No external JS files; everything inline. No imports, no modules; classic script-tag global-scope code.

## 4.2 Top-level structure (constants + main objects)

| Line | Symbol | Purpose |
|---:|---|---|
| 2236 | `const DEFAULTS = { ... }` | The default config object (~63 lines). All 141 settings get their default values here. |
| 2299 | `const DEFAULT_LAYOUT` | Deep-clone of DEFAULTS.layout for reset operations |
| 2302 | `const THEMES = { ... }` | 22-theme object (Section 3) |
| 2570 | `const THEME_SWATCH_COLORS = { ... }` | Swatch-preview color derivation |
| 2674 | `let S = clampLayoutCanvas(deepMerge(DEFAULTS, {}))` | **The global state variable** -- all live settings live here |
| 2721 | `const LAYOUT_NODE_LABELS = { ... }` | Friendly labels for the 9 layout nodes |
| 3138 | `const LAYOUT_TEMPLATES = [ ... ]` | 11 layout templates (separate from THEMES) |
| 3860 | `const JARGON_MAP = { ... }` | Stage 7.20 search synonyms (e.g. "color" -> ["colour", "hue", "tint"]) |

## 4.3 Function inventory (line + brief purpose)

Sorted by line; representative not exhaustive.

| Line | Function | Purpose | Origin stage |
|---:|---|---|---|
| 2598 | `buildThemeGrid()` | Renders theme buttons from THEMES const | original |
| 2615 | `applyTheme(name)` | Deep-merges theme over DEFAULTS, refreshes UI | original + Stage 7.20.6 sentinel handling |
| 2646 | `deepMerge(tgt, src)` | Recursive object merge for theme/config layering | original |
| 2663 | `clampLayoutCanvas(s)` | Sanitizes layout coords to canvas bounds | Stage 7.13 |
| 2682 | `scaleIframe()` | Computes preview-iframe transform:scale() to fit container | original |
| 2734 | `_nodeRectCanvas(n, cw, ch)` | Layout-node rect in canvas pixels | Stage 7.13 |
| 2751 | `_rectToCanvasXY(rect, anchor, cw, ch)` | Canvas-relative coord helper | Stage 7.13 |
| 2762 | `rebuildLayoutEditor()` | Re-renders the visual layout-editor overlay | Stage 7.13 |
| 2833 | `_layoutEditMouseDown(e)` | Layout-editor drag-handle mousedown | Stage 7.13 |
| 2989 | `toHex(c)` | Color string normalization (rgba -> #hex) | original |
| 3007 | `markDirty()` | Marks config dirty (clears preset selection, triggers save) | original |
| 3017-3088 | `bindRange/Toggle/Select/Text/Color + sync*` | The 5 bind* helpers + 5 sync* helpers (10 functions) used by initBindings | original |
| 3101 | `buildBorderColors()` | Renders the spinning-border color picker list | original |
| 3328 | `applyLayoutTemplate(t)` | Applies a layout template to S.layout | Stage 7.13 |
| 3355 | `renderMiniLayoutThumb(t)` | Tiny preview rendering for layout-template buttons | Stage 7.13 |
| 3400 | `openLayoutTemplatesModal()` | Opens layout-templates modal | Stage 7.13 |
| 3494 | `initBindings()` | **THE BIG ONE** -- wires all 141 c-* IDs to bind functions. ~370 lines (3494-3859). Includes the 5 masters' setProperty calls (L3590-3633). |
| 3911 | `buildSearchIndex()` | Builds search index from data-search attributes + JARGON_MAP | Stage 7.20 |
| 3950 | `runSearchFilter(query)` | Filters control visibility by query string | Stage 7.20 |
| 4007 | `initSearchBar()` | Wires the search input, Ctrl+F keyboard binding, clear button | Stage 7.20 |
| 4060 | `markAdvancedElements()` | Adds `.advanced-only` class to specific rows/sections | Stage 7.20 |
| 4115 | `addDiscoveryLinks()` | Adds "Want more control?" links inside sections with advanced content | Stage 7.20 |
| ~4250-4500 | misc apply/save/preset helpers | preset save/load, Apply to OBS button, status indicator | original + various stages |
| ~4500 | `Reset to Defaults` handler | The destructive reset (Stage 7.24 palette-shifted) | original |
| 4907 | `restructureSidebar()` | **THE SUPERCAT WRAPPER** -- runtime DOM rewrap of the 17 sections into 6 supercats | Stage 7.21 |
| 4955 | `readStoredCollapseState(id)` | Per-supercat localStorage helper (Stage 7.24 refactor from readCollapsed) | Stage 7.24 |
| 4976 | `wirePresetManager()` | Wires Preset Manager modal open/close/save/delete buttons | original Preset Manager era |
| ~5050 | Welcome banner init/close handlers | Stage 7.21 welcome banner show/dismiss flow | Stage 7.21 |
| ~5172 | DOM-ready bootstrap | Calls restructureSidebar, markAdvancedElements, initBindings, etc. | Stage 7.21 |

## 4.4 Event handler inventory

**Total `addEventListener` calls: 55** across the script block.

Categories (by sampling):

| Category | Examples | Approx count |
|---|---|---:|
| Per-control input bindings | wired inside the bind* helpers in initBindings | varies (140+ via initBindings) |
| Modal open/close | btn-preset-manager / pm-close / layout-templates-modal | ~8 |
| Search bar | search-input keyup/focus, Ctrl+F document keydown | 3 |
| Advanced toggle | sidebar-advanced-toggle change | 1 |
| Supercat headers | runtime per-header click in restructureSidebar | 6 (one per supercat) |
| Welcome banner | dismiss button + footer reshow link | 2 |
| Btn-reset, btn-apply | global top-bar action buttons | 2 |
| Layout editor mouse events | mousedown/mousemove/mouseup on canvas overlay | 3+ |
| Window resize / load | scaleIframe re-fit on resize, initial DOM-ready bootstrap | 2+ |

Note: the bind* helpers internally call `addEventListener('input', ...)` on each wired control. So initBindings produces 130+ listeners at runtime; the 55 in-source count is the LITERAL `.addEventListener(` count, which is lower because most listener wires are inside helper-function bodies that get called from initBindings.

## 4.5 localStorage keys inventory

**3 unique key patterns** (read and written by customize.html):

| Key pattern | Set by | Read by | Purpose | Added in |
|---|---|---|---|---|
| `customize_show_advanced` | Stage 7.20 advanced toggle change handler | initBindings + on body class init | persists Basic / Advanced mode preference | Stage 7.20 STEP 11 |
| `customize_welcome_seen` | Welcome banner dismiss button | Bootstrap init (skip banner if seen) | hides banner after first dismiss | Stage 7.21 STEP 3 |
| `supercat_<id>_collapsed` | Supercat header click handler | `readStoredCollapseState()` in restructureSidebar | per-supercat collapse persistence (6 supercats: start, look, text, effects, audio, layout) | Stage 7.21 STEP 2 |

**Total localStorage operations: 7** (3 read + 3 write calls, plus 1 nuance with the supercat key pattern read in two places: the helper and the click handler).

**No use of:** `sessionStorage`, `IndexedDB`, cookies, or any other persistence layer.

**Notes:**
- The `customize_welcome_seen` key is also explicitly removed (or set to non-true) by the "Show welcome message" footer link, which triggers the banner to reappear on the next reload. (Per Stage 7.21 STEP 3.)
- No localStorage cleanup / migration logic exists; if the key schema ever needs to evolve, old keys persist silently.

## 4.6 Global state surface

The global state surface is the single `let S = ...` at L2674. Everything is mutated through S then re-applied through:
- `markDirty()` (clears preset selection)
- `applyTheme(name)` (deep-merges theme into S, calls bind syncs)
- The big initBindings wire-up where each control's input event sets a path inside S and triggers an apply

Other module-scope variables (not exhaustively listed):
- `_origScaleIframe` at L2957 (cached function reference for resize observer)
- `_layoutEdit*` series (layout-editor mouse state)
- `DEFAULTS`, `DEFAULT_LAYOUT`, `THEMES`, `THEME_SWATCH_COLORS`, `LAYOUT_NODE_LABELS`, `LAYOUT_TEMPLATES`, `JARGON_MAP` consts

## 4.7 Duplicated / redundant JS patterns

**1. Repeated `document.getElementById(...)` lookups inside event handlers** -- many handlers do `document.getElementById('c-x')` instead of caching the element at module init. Sampling: ~50-100 redundant `getElementById` calls inside handler bodies that could be cached once.

**2. The 10 bind/sync helper functions** -- bindRange/bindToggle/bindSelect/bindText/bindColor + syncRange/syncToggle/syncSelect/syncText/syncColor are essentially mirror pairs. Could be unified into a single `bindControl(id, type, getter, setter, format?)` helper that handles all 5 types via dispatch. Would shrink ~70 lines to ~30.

**3. The big initBindings function** -- ~370 lines that mostly just call the bind helpers for each of 141 controls. Each control has roughly 1-2 lines of code. Pattern is uniform; could be driven from a config array instead of hand-written for each control. Would shrink to ~50 lines + a 141-entry data array.

**4. Apply Master CSS variables in two places** -- L3590-3633 (per-master input handlers) AND L4266-4270 (on initial load / theme apply). Same 5 setProperty calls duplicated. Could be unified into a single `applyMastersToCss(S.masters)` function called from both sites.

**5. Welcome banner + supercat collapse + advanced-toggle all read/write localStorage individually** -- no unified `Prefs` layer. Each surface has its own try/catch around the localStorage call. Could be unified into 5-10 lines of helper code.

**6. `THEME_SWATCH_COLORS` derives from THEMES** -- but it's a SECOND constant maintained separately. The derivation could happen once at runtime instead of duplicating data.

## 4.8 Implications for rebuild

- **Replace 5 bind* + 5 sync* helpers with a unified `bindControl()`** dispatcher (potential ~40-line shrink).
- **Refactor initBindings from 370 lines of hand-wiring to a ~50-line config-driven loop + a 141-entry data table.** The data table can live in the same file or be split into per-section files.
- **Cache DOM lookups at init.** Replace `document.getElementById(...)` inside handlers with closures over pre-cached references.
- **Unify the dual master-apply call sites** into a single `applyMastersToCss()`.
- **Introduce a `Prefs` wrapper** around the 3 localStorage keys (and any future ones). 5-line API: `Prefs.get(key, default)`, `Prefs.set(key, value)`, `Prefs.remove(key)`.

== END OF SECTION 4 ==

---

# Section 5: Features per stage

This section maps each stage's contribution to the current `customize.html` to understand "what does this stage have to deliver in the rebuild" -- and which features are essential vs nice-to-have.

## 5.1 Stage 7.19 -- customize redesign foundation (2026-05-22)

**Contributions to customize.html:**
- ~110 friendly-voice label rewrites across sections (jargon-to-human renames)
- ~50 inline help paragraphs added (`.sec-help` and `.control-help` classes)
- 6 v2 animation tokens added (`--dur-fast-v2`, `--dur-standard-v2`, `--dur-slow-v2`, `--ease-windows`, `--ease-macos`, `--ease-emphasized`)
- 13 sub-headers across 7 sections via `.sec-subheader` class (mid-section visual grouping)
- Section expand/collapse animations refined with the new v2 duration tokens
- Section help text styling (`.sec-help-emphasized`, `.sec-help-tip`, `.inline-hint` polish helpers added in 7.21 STEP 5)

**Essential?** YES (the labels and help text are the user-facing "voice" of customize). The rebuild MUST preserve all 110+ labels and the 50 help paragraphs verbatim or with operator-approved revisions.

**Nice-to-have?** The 13 sub-headers (`.sec-subheader`) -- they helped break up dense sections, but the rebuild's section organization might restructure such that sub-headers are unneeded.

## 5.2 Stage 7.19.5 -- WPF Setup Wizard binding fix (out of customize scope)

NOT in customize.html. WPF-only. **Skip in rebuild.**

## 5.3 Stage 7.20 -- master controls + search + advanced (2026-05-23)

**Contributions to customize.html:**
- **Quick Settings section** added as the FIRST section in the sidebar (HTML structure)
- **5 master controls** (Master Accent Color, Master Overall Size, Master Text Size, Master Glow, Master Animations) -- Section 1.4
- **"Use accent" links** (~8 instances) next to per-element accent color pickers; clicking sets the per-element value to the sentinel `var(--accent-master)`
- **Search bar** (HTML + CSS + `buildSearchIndex` + `runSearchFilter` + `initSearchBar` + JARGON_MAP synonyms)
- **Ctrl+F keyboard binding** for focusing the search bar
- **Advanced toggle** (`#sidebar-advanced-toggle` checkbox + `body.show-advanced` class + `markAdvancedElements()` JS + `customize_show_advanced` localStorage)
- **Discovery links** ("Want more control?") inside sections that contain advanced-only content (rendered by `addDiscoveryLinks()`)
- **Per-control `data-search` attributes** (~141 controls each get one) feeding the search index

**Essential?** ALL ESSENTIAL. The masters are the primary UX entry point, search is critical for a 141-control panel, advanced mode is a stated design principle.

## 5.4 Stage 7.20.5 -- overlay master wiring (out of customize scope)

NOT in customize.html. Stage 7.20.5 modified overlay.html to consume the master CSS variables that customize.html sets. **Skip in customize rebuild** but note the contract: customize must continue to set `--accent-master`, `--overall-scale`, `--text-scale`, `--glow-master-enabled`, `--animations-master-enabled` on its own :root from the user's master inputs.

## 5.5 Stage 7.20.6 -- theme accent propagation (2026-05-23)

**Contributions to customize.html:**
- Themes converted to use `var(--accent-master)` sentinel string for accent-following keys (`nowPlaying.color`, `bars.color`, `title.color`, `artist.color`, `spectrum.color`, `timestamps.color`)
- `applyTheme` flow handles the sentinel correctly (deep-merge preserves the literal string; sentinel resolves at CSS render against the live `--accent-master`)

**Essential?** YES. The 21 non-default themes are this stage's deliverable shape; sentinel substitution is the live "accent follows master" UX.

## 5.6 Stage 7.21 -- super-categories + welcome banner + final polish (2026-05-24)

**Contributions to customize.html:**
- **6 supercats** (`{ id, label, sections[] }` array in `restructureSidebar()` L4912-4926): Start here, Look, Text, Effects, Audio, Layout (advanced)
- Supercat mapping (which sections under each supercat):
  - **Start here:** quick-settings, general
  - **Look:** themes, card, font, art, np, platform
  - **Text:** text, progress
  - **Effects:** glow, textglow, border, dyncolors
  - **Audio:** spectrum
  - **Layout (advanced):** layout, anim
- **Welcome banner** (HTML + CSS + `customize_welcome_seen` localStorage gate + footer reshow link)
- **Following-master badges** (8 instances; reactive visibility when per-element accent picker is the sentinel)
- **Stage 7.21 STEP 5 polish helpers:** `.sec-help-emphasized`, `.sec-help-tip`, `.inline-hint` classes replacing prior inline-style attributes

**Essential?** YES. Supercats are critical sidebar organization for a 17-section panel. Welcome banner is first-time-user onboarding. Following-master badges are the visual feedback for "this picker is following the master accent."

## 5.7 Stage 7.22 + 7.23 -- WPF tray menu (out of customize scope)

NOT in customize.html. These stages touched the tray menu only. **Skip in customize rebuild.**

## 5.8 Stage 7.24 -- customize.html targeted polish (2026-05-24)

**Contributions to customize.html:**
- `.supercat-header` color: `var(--text-tertiary)` #5C5C66 -> #c0c0c0 (readable contrast)
- `.supercat-header:hover` color: `var(--text-secondary)` #9999A1 -> #e0e0e0
- NEW `:has()` active-state rule: `.supercat:has(.sec-header.open) > .supercat-header { color: var(--accent) }`
- `.sec-help` color: `var(--text-muted)` -> `#c0c0c0`
- `.control-help` color: `var(--text-secondary)` -> `#c0c0c0`
- `.btn-danger` palette shift red-400 family -> red-600 (`#dc2626` + `rgba(220,38,38,*)`)
- `restructureSidebar()` refactored: `readCollapsed()` -> `readStoredCollapseState()` returning raw string; explicit `else if (stored === null && sc.id === 'start')` branch for first-load expand documentation
- STEP 5 SKIPPED (top bar accent line already 3px)

**Essential?** The color brightening MUST carry over (operator-PASSed visual choice). The :has() active-state rule should carry over (operator-PASSed). The `readStoredCollapseState` rename is documentation-as-code; the rebuild can name this whatever.

## 5.9 Essential vs nice-to-have rollup

### MUST-HAVE in rebuild (cannot ship without):

1. All 141 c-* setting IDs preserved as config keys (Section 1)
2. The 5 master CSS variables preserved (Section 2)
3. All 22 themes preserved with sentinel pattern (Section 3)
4. The 11 layout templates preserved (Section 4, L3138-3327)
5. Quick Settings + masters UX (Stage 7.20)
6. Search bar + Ctrl+F + JARGON_MAP + per-control data-search (Stage 7.20)
7. Advanced toggle + advanced-only marking (Stage 7.20)
8. "Use accent" links (Stage 7.20)
9. Discovery links inside sections with advanced content (Stage 7.20)
10. 22 themes with sentinel substitution behavior (Stage 7.20.6)
11. 6 supercats + collapse persistence (Stage 7.21)
12. Welcome banner + reshow link (Stage 7.21)
13. Following-master badges on 8 accent pickers (Stage 7.21)
14. Stage 7.24 high-contrast palette (supercat headers, help text, Reset button)
15. Stage 7.24 :has() active-state for supercats
16. Stage 7.24 explicit START HERE first-load expand
17. All ~110 friendly labels + ~50 help paragraphs (Stage 7.19) -- verbatim or with operator-approved revisions
18. All animation token usages (Stage 7.19 v2 set; consolidation in rebuild is OK)
19. Layout editor (Stage 7.13) with the 9 layout nodes + drag handles + 11 templates
20. Apply to OBS button + status indicator (original)
21. Preset Manager (original)
22. Reset to Defaults (original; Stage 7.24 palette-shifted)

### NICE-TO-HAVE (rebuild could revise or replace):

1. Stage 7.19 sub-headers (`.sec-subheader`) -- if rebuild's section organization restructures densely, these may be redundant
2. Stage 7.19 inline help paragraphs -- operator's last-conversation decision was "replace help text with tooltips on info icons" so this becomes a redesign, not a port
3. THEME_SWATCH_COLORS as a separate const -- derive at runtime instead
4. Stage 7.21 STEP 5 polish helpers (`.sec-help-emphasized`, etc.) -- if help text becomes tooltips, these classes go away
5. `--dur-fast` / `--dur-fast-v2` dual token sets -- consolidate to one canonical set in rebuild

### COULD-CUT (rebuild could deprecate):

1. Layout editor canvas drag-and-drop visual mode (Stage 7.13) -- this is heavy code (~250 lines L2734-2989). If usage tracking shows few users use the visual editor and prefer text-based template selection, this could be retired. But default position: KEEP (existing feature, operator-approved).
2. THEMES font property -- if all themes ended up using the same default font, the font key could be removed. (Not actually the case; ~8 distinct fonts; KEEP.)

== END OF SECTION 5 ==
