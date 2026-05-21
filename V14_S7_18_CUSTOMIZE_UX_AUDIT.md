# V14_S7_18_CUSTOMIZE_UX_AUDIT.md

**Stage:** 7.18 Task B (Customize panel UX research, READ-ONLY)
**Brief:** `CLAUDE_CODE_INSTRUCTIONS.md` (Stage 7.18 STEPs 5-9)
**Target file:** `src/customize.html` (4346 lines, 214 752 bytes; v14 visual rebuild from Stage 7.13)
**Baseline reference:** `_archive/v12_customize_baseline/customize.html` (4204 lines, 207 199 bytes; pre-Stage-7.13)
**Author:** Ruflo (Claude) -- READ-ONLY audit. No code changes. No fix proposals.
**Started:** 2026-05-21 (Stage 7.18 Task B)

---

## ABSOLUTE READ-ONLY CONSTRAINT

This document is the SOLE deliverable of Task B. The brief explicitly states:

- **NO code changes to `src/customize.html`** (verified at S9.2 -- `git diff --stat HEAD~3 HEAD -- src/customize.html` must show ZERO lines changed)
- **NO proposed fixes** anywhere in this document
- **NO mockups** of redesigns
- **NO "I would recommend..."** prescriptions

This audit DESCRIBES the current state and IDENTIFIES candidate pain points. It does NOT prescribe solutions. Solutions are the subject of a future operator-commissioned brief.

The synthesis section (STEP 8 / §4 below) contains an EXPLICIT non-recommendations list -- things the audit specifically does NOT recommend, to constrain the future redesign's scope.

---

## 1. Inventory (STEP 6)

### 1.1 Section inventory

`customize.html` exposes settings via a left sidebar of 16 collapsible sections, in render order. All sections default to **collapsed** (verified via CSS `.sec-body { display: none; }` at line 247; `.sec-body.open` enables display).

Control counts below exclude inline hex-text inputs (which always pair 1:1 with a color picker, so they're counted as part of the color-picker control). Counts include all direct interactive elements -- sliders, color pickers, toggles, dropdowns, buttons, number inputs.

| # | Section | Sliders | Colors | Toggles | Dropdowns | Buttons | Numbers | Total controls | Text content (chars) | Default state |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 1 | **Visual Themes** | 0 | 0 | 0 | 0 | ~12 dynamic theme cards | 0 | ~12 | 208 | Collapsed |
| 2 | **General** | 1 (Opacity) | 0 | 0 | 0 | 0 | 0 | 1 | 175 | Collapsed |
| 3 | **Layout** | 0 | 0 | 9 (vis + lock pairs) | 1 | 2 (template, edit) | 0 | 12 | 782 | Collapsed |
| 4 | **Font** | 0 | 0 | 0 | 1 (typeface picker) | 0 | 0 | 1 | 111 | Collapsed |
| 5 | **Card Shape** | 5 | 2 | 0 | 0 | 0 | 0 | 7 | 425 | Collapsed |
| 6 | **Dynamic Colors** | 0 | 0 | 16 | 0 | 0 | 0 | 16 | 422 | Collapsed |
| 7 | **Spinning Border** | 1 | 0 | 1 | 0 | 0 | 0 | 2 (+ 5-color palette picker UI inside) | 188 | Collapsed |
| 8 | **Outer Glow** | 2 | 2 | 1 | 0 | 0 | 0 | 5 | 206 | Collapsed |
| 9 | **Text Glow** | 5 | 5 | 5 | 0 | 0 | 0 | 15 | 697 | Collapsed |
| 10 | **Album Art** | 2 | 0 | 1 | 1 | 0 | 0 | 4 | 220 | Collapsed |
| 11 | **Now Playing Label** | 4 | 2 | 2 | 0 | 0 | 0 | 8 | 346 | Collapsed |
| 12 | **Platform Badge** | 2 | 2 | 1 | 1 | 0 | 0 | 6 | 328 | Collapsed |
| 13 | **Track & Artist** | 8 | 2 | 0 | 2 | 0 | 0 | 12 | 428 | Collapsed |
| 14 | **Spectrum Visualizer** | 10 | 1 | 3 | 1 | 1 | 0 | 16 | **1195** | Collapsed |
| 15 | **Progress & Timestamps** | 3 | 5 | 1 | 0 | 0 | 0 | 9 | 295 | Collapsed |
| 16 | **Slide-In Animation** | 2 | 0 | 1 | 2 | 1 | 0 | 6 | 416 | Collapsed |
| **TOTAL** | | **45** | **21** | **41** | **9** | **~16** | **0** | **~132 + 12 theme cards** | **6 642** | All 16 collapsed |

(Counts represent in-sidebar controls. The header has 3 additional buttons -- Preset Manager, Reset, Apply -- and the inline color pickers each have a paired hex text input not counted separately.)

### 1.2 Control inventory (whole file)

From `grep -cE 'type="..."'` on `src/customize.html`:

| Control type | Count | Notes |
|---|---:|---|
| `input[type="range"]` (sliders) | 51 | Includes any sliders not in the 16 sections (e.g. inside the layout-template modal) |
| `input[type="color"]` (color pickers) | 25 | Plus 25 paired hex text inputs |
| `input[type="checkbox"]` (toggles) | 41 | Includes 16 toggles from Layout vis/lock pairs (8 layout nodes x 2) |
| `input[type="text"]` | 30 | ~25 are hex text inputs paired with color pickers; ~5 are standalone text inputs (preset name editor, layout template name editor) |
| `input[type="number"]` | 4 | Inside the layout-template modal |
| `<select>` (dropdowns) | 9 | Font picker, layout template picker, art position dropdown, transition style, etc. |
| `<button>` | 11 | Preset Manager toolbar, Reset, Apply, Edit-layout, layout-template buttons, etc. |
| `input[type="file"]` | 1 | (Preset Manager Import button -- not user-facing styling) |
| **Total interactive elements** | ~172 | (Excluding paired hex texts to avoid double-count: 51 sliders + 25 colors + 41 toggles + 9 selects + 11 buttons + 4 numbers + 1 file = 142 distinct controls. Plus ~12 dynamic theme cards in Visual Themes built by `buildThemeGrid()` at line ~1893.) |

For a single-window UI rendered in a 370 px sidebar + scrolling preview pane, this is a dense control surface.

### 1.3 Label / tooltip / blurb inventory

Each section opens with a `.sec-help` paragraph explaining what the section does, followed by per-control rows. Some sections also have `.sec-tips` blocks below the controls for additional context. Inline `<span>` hints appear next to specific controls.

Categorization sampling (2-3 per section, deliberately picking the most visible labels):

| Category | Examples | Where it dominates |
|---|---|---|
| **Plain English** -- non-technical, immediately understandable | "Visual Themes", "Visual Themes" hint card text, "Enable", "Background", "Outer Glow", "Track Title", "Artist", "Mirror Bars", "Color Mode" intro | Dynamic Colors (per-element on/off toggles), Visual Themes (theme card names), Album Art (basic position dropdown) |
| **Mildly technical** -- uses common UI/design terms | "Border Radius", "Border Thickness", "Opacity", "Font Size", "Font Weight", "Letter Spacing", "Background Blur", "Background Opacity", "Marquee Speed" | Card Shape, Track & Artist, Now Playing Label, Platform Badge |
| **Jargon-heavy** -- requires domain knowledge | "Bar Roundness", "Reaction Speed", "Loudness Boost", "Auto-Volume Match", "HUE-SHIFTED" (capitals in source), "Snap Grid", "BG Angle" (abbreviation), "FPS", "minHeight", "heightMult" (these last two leak from JS preset names into UI?) | Spectrum Visualizer, Layout, Text Glow (the hue-shift artist-glow tip) |
| **Wall of text** -- explanatory paragraph > 50 words | Spectrum Visualizer "Loudness Boost" tooltip (60+ words, mentions SteelSeries Sonar / Voicemeeter / virtual-mixer by brand, version number `v9.6.5`, and audio-engineering concepts); Layout section intro paragraph + tip; Text Glow intro paragraph (synthwave reference); Card Shape "Background Opacity" tooltip (technical interplay between opacity and blur) | Spectrum Visualizer (single biggest wall-of-text contributor), Layout, Text Glow |

**Sections with highest jargon density** (top 3 by qualitative read):

1. **Spectrum Visualizer** -- "Loudness Boost", "Auto-Volume Match", "Reaction Speed", "Bar Roundness", "Lowest Bar Height", "Tallest Bar Height", color-mode terms; PLUS the multi-paragraph explanations of why those settings matter for specific virtual-audio-mixer products. This is a power-user section in the middle of a settings panel that doesn't otherwise advertise itself as power-user.
2. **Text Glow** -- "HUE-SHIFTED Artist Glow" (with explanation of "around 60° apart on the colour wheel"), five identical "Enabled / Glow Color / Glow Intensity" triplets for five different text elements, intro paragraph referencing "synthwave / retro looks".
3. **Card Shape** -- relatively few items but "BG Angle", "BG Top", "BG Bottom", "Background Opacity", "Background Blur" cluster of technical CSS-style labels.

**Sections with cleanest plain-English labels** (top 3):

1. **Dynamic Colors** -- 16 toggles, every label is a noun referring to a visible card element (Background, Outer Glow, Track Title, Artist, Now Playing Label, Platform Badge, Spectrum Bars, Timestamps, Progress Bar, Spinning Border). The intro paragraph is 2 sentences of plain English.
2. **Visual Themes** -- self-explanatory theme names (Vibrant Default, Cool Blue, etc.); single-click action.
3. **Album Art** -- "Show Album Art" toggle, "Position" dropdown (Left/Right/Center), "Size" slider, "Edge Fade Width" slider. Direct labels, single-paragraph intro.

### 1.4 Layout / hierarchy

- **Vertical sidebar**, 370 px fixed width. The 16 sections stack vertically in render order. To reach the 16th section (Slide-In Animation) the user scrolls past the previous 15 collapsed headers.
- **No visual grouping beyond section dividers.** Each section has a 8-px-padded header with an icon + title + chevron. Headers all look the same -- there is no visual cue that "Visual Themes" is a top-level shortcut, "Layout" is a power-user feature, or "Spectrum Visualizer" is the most-controls-heavy section.
- **No search / filter / category navigation.** A user looking for "where do I change the album art size" must guess that it's under "Album Art" (correct) and that "Visual Themes" doesn't already include album art settings (it actually does, indirectly, via the theme bundle).
- **All sections default-collapsed.** Stage 7.13 introduced this for visual cleanliness (comment in source: `v8.0.1: default-collapsed so the customize page opens with a tidy sidebar`). The trade-off: a new user opening the page sees 16 unfamiliar terms but zero controls.
- **Header order is informative but not learnable at first glance.** The order is: Themes -> General -> Layout -> Font -> Card Shape -> Dynamic Colors -> Spinning Border -> Outer Glow -> Text Glow -> Album Art -> Now Playing Label -> Platform Badge -> Track & Artist -> Spectrum Visualizer -> Progress & Timestamps -> Slide-In Animation. This is roughly "global -> structure -> color -> per-element -> motion" but the relationship isn't explicit.
- **Control labels are left-aligned, value/control inline-right.** Standard form layout. No issue with consistency itself; the issue is volume per row.
- **The live preview pane (right side of customize.html)** is always visible and renders the current settings via `overlay.html` in an iframe. This is a major UX win -- changes preview in real time without leaving the page.
- **The header bar contains** the brand logo + "Preset Manager" button + "Reset to Defaults" button + "Apply to OBS" button. The accent bar at top (`.accent-bar`) animates a 3-px brand-purple gradient (Stage 7.13).

---

*Sections 2-4 (pain-point identification, v12 baseline comparison, synthesis) follow in STEPs 7 and 8 commits. This file is built incrementally per the brief's STEP structure.*
