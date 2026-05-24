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
