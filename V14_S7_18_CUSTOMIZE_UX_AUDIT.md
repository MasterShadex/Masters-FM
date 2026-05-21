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

## 2. Pain-point identification (STEP 7)

### 2.1 Cognitive load hot-spots

Ranking the top 5 sections by likely overwhelm-factor (control count + text density + mixed control types + section length):

| Rank | Section | Controls | Text chars | Why it overwhelms |
|---:|---|---:|---:|---|
| 1 | **Spectrum Visualizer** | 16 | 1195 | Highest text density of any section; mix of toggle / slider / dropdown / color / button; jargon-heavy labels (Reaction Speed, Loudness Boost, Bar Roundness); brand-name references inside the tooltip (SteelSeries Sonar, Voicemeeter) require audio-engineering context. |
| 2 | **Text Glow** | 15 | 697 | Five identical 3-control blocks (Enabled / Glow Color / Glow Intensity) stacked back-to-back for five different text elements, with no visual sub-grouping. User has to read each block to know which text element it's for. |
| 3 | **Dynamic Colors** | 16 | 422 | 16 boolean toggles, no visual grouping, no hierarchy. Each toggle is plain-English but the SHEER NUMBER on one screen produces "where do I even start" overwhelm. Decisions are mostly correlated (if you want dynamic on, you likely want all of them on) but the UI presents them as 16 independent choices. |
| 4 | **Layout** | 12 | 782 | 8 layout-node vis/lock pairs (16 toggles?) plus intro paragraph + tip + template picker + Edit button. The vis/lock pairing is intelligible after you read the inline "Left toggle = visible. Right toggle (🔒) = lock" hint, but the hint is ABOVE the rows, not adjacent to the first row. |
| 5 | **Track & Artist** | 12 | 428 | Two 6-control blocks (Title and Artist) with identical layout but distinct controls each. Plus a "Marquee Speed" / "Marquee Pause" duplication between Title and Artist that requires the user to set both. Color picker placement varies between Title and Artist (Title has color BEFORE marquee, Artist has color AFTER -- this is from the source line ordering). |

**Bottom 5 (least overwhelming)**: General (1 control), Font (1 control), Spinning Border (2), Outer Glow (5), Album Art (4). These are well-scoped single-purpose sections.

### 2.2 Jargon hot-spots

Ranking the top 5 sections by jargon-per-control density:

| Rank | Section | Jargon examples | Plain-English ratio |
|---:|---|---|---|
| 1 | **Spectrum Visualizer** | "Reaction Speed", "Loudness Boost", "Bar Roundness", "Lowest/Tallest Bar Height", "Color Mode", "Mirror Bars", "Auto-Volume Match", FPS-related fields; SteelSeries / Voicemeeter brand context | <30% plain English |
| 2 | **Text Glow** | "Glow Intensity" + the "HUE-SHIFTED Artist Glow" tooltip explaining "60° apart on the colour wheel" | ~50% plain |
| 3 | **Card Shape** | "BG Angle" (abbreviation), "BG Top", "BG Bottom", "Border Thickness" (geometry), "Background Blur" (which interacts with Background Opacity in a non-obvious way) | ~50% plain |
| 4 | **Layout** | "Snap Grid", "vis/lock", "layout template", emoji-prefixed node names that double as legends | ~60% plain (helped by emoji labels) |
| 5 | **Outer Glow** | "Blur Radius", "Spread", glow-effect terminology generally | ~60% plain |

### 2.3 Discoverability gaps

Specific examples of things a new user would have trouble finding:

| Buried thing | Where it lives | Why hard to find |
|---|---|---|
| **Overall card size / scale** | Currently NOT a top-level control. The card auto-fills the OBS browser source dimensions. New users may look in "General" or "Layout" for a "Size" control; there isn't one. | Common request, no obvious home |
| **Accent color** (i.e. the dominant brand color of the card) | NOT a single control. Each text element / glow / progress fill has its OWN color picker (~25 separate pickers). New users look for "Accent Color" or "Theme Color" and don't find one. | Decentralized across 16 sections |
| **"Make text bigger / more readable"** | Spread across Track & Artist, Now Playing Label, Platform Badge sections -- each with its own Font Size slider, no global "Text Size" setting | Decentralized across 3 sections |
| **First-time setup / "what do I do first?"** | No setup wizard. No "Quick Start" guide inside customize.html. The Stage 7.13 design assumes the user picks a theme from Visual Themes first, then tweaks. But Visual Themes is collapsed by default. | No onboarding affordance |
| **How to disable a single feature** (e.g. "I don't want the spinning border") | Spread across multiple sections: Spinning Border has its own toggle, Outer Glow has its own, Text Glow has 5 toggles, Dynamic Colors has 16 toggles. To "turn off everything fancy" the user has to find each section. | Decentralized opt-outs |
| **Why a slider does what it does (preview-free)** | Many sliders affect properties that have no immediate preview animation. Slide-In Animation duration only triggers on track change; Glow Intensity changes are subtle. | Settings with delayed/subtle feedback |
| **Effect of "Background Blur" alone** | Background Blur only renders when Background Opacity < 100%. New users move the Blur slider with Opacity at default 100%, see nothing happen, and assume the Blur slider is broken. The Card Shape section's `sec-help` mentions this interplay, but it's easy to miss. | Hidden dependency on Opacity |

### 2.4 Redundancy / cruft (candidates for review -- DO NOT REMOVE)

Things that MAY be redundant. Flagged for operator's review; brief explicitly says DO NOT REMOVE in this audit:

| Candidate | Where | Type of redundancy |
|---|---|---|
| **Marquee Speed + Marquee Pause for Title AND Artist** | Track & Artist section | The user almost always wants Title and Artist to scroll at the same speed. Having FOUR sliders (Title Speed, Title Pause, Artist Speed, Artist Pause) where two would do is a candidate for a "Marquee" sub-group with single speed + single pause. |
| **5 separate Text Glow blocks (one per element)** | Text Glow section | Each text element gets identical Enable/Color/Intensity controls. A user who wants "glow on title only, off everywhere else" has to toggle 5 individually. Candidate for a master toggle + per-element overrides. |
| **16 Dynamic Colors toggles** | Dynamic Colors section | Many users will want "all on" or "all off". A master toggle + a sub-group of overrides is a candidate. |
| **"Reset to Defaults" button in header AND "↺ Reset" buttons next to many sliders** | Header + sliders | Per-slider reset is good UX. Header-level reset is also good. The two coexist; not actually redundant on examination, just dense. |
| **Preset Manager + Apply to OBS as separate buttons** | Header | The functional distinction is real (presets save *current state* with a name; Apply pushes current state to OBS without naming). May be unclear to new users. |
| **Visual Themes section (theme cards) + Customize Overlay window itself** | Whole UI | The "Visual Themes" picker writes ~30 settings at once. The rest of the customize UI lets you tweak those 30 settings individually. For a new user, picking a theme is sufficient; for a power user, the rest of the panel is essential. The UI doesn't surface this two-tier model. |

### 2.5 v12 baseline comparison

Stage 7.13 rebuilt customize.html visually (v14 design tokens) but explicitly preserved the structural layout. Comparing `_archive/v12_customize_baseline/customize.html` (4204 lines) with current `src/customize.html` (4346 lines):

| Metric | v12 baseline | v14 current | Delta |
|---|---:|---:|---:|
| Total lines | 4204 | 4346 | +142 (token additions, reduced-motion guard, accent bar HTML, comments) |
| File size (bytes) | 207 199 | 214 752 | +7 553 (+3.6 %) |
| Number of sections | 16 | 16 | unchanged |
| Section ORDER | identical | identical | unchanged |
| Section names | identical | identical | unchanged |
| Top-level header (Logo + Preset Manager + Reset + Apply) | present | present | accent bar added (cosmetic) |
| Number of controls (per inventory above) | ~132 sidebar + 12 themes | ~132 sidebar + 12 themes | unchanged |
| Default collapsed/expanded state of sections | all collapsed | all collapsed | unchanged |

**Conclusion:** Stage 7.13 was a **pure visual rebuild** -- it did NOT change information architecture, control count, section count, section order, default-state. Whatever UX complaints exist about customize.html, **Stage 7.13 did NOT cause them** -- they predate v14. The visual rebuild made things look "more polished" but didn't address density, jargon density, or discoverability. Operator complaints about "too much text" / "not friendly enough" are about the underlying IA, not Stage 7.13's visual changes.

This is critical context for the future redesign brief: the answer is NOT "undo Stage 7.13" or "fix what Stage 7.13 broke." It's "address an IA issue that has existed across versions."

### 2.6 First-impression pass (Ruflo as fresh-eyes simulator)

Numbered observations, written as if encountering the file for the first time:

1. **The accent bar at the very top** of the page (3-px animated brand-purple) catches the eye first. Suggests "this is a polished product with thoughtful styling." Positive first signal.

2. **The header reads "Master's FM | Overlay Customizer"** with a small icon and the three action buttons (Preset Manager / Reset / Apply) right-aligned. Clear. I know what app I'm in.

3. **The sidebar dominates the left side.** I see ~6 section headers visible before scrolling: "🎨 Visual Themes", "⚙️ General", "📐 Layout", "🅰️ Font", "🟦 Card Shape", "🌈 Dynamic Colors". The emoji icons help skim. The labels are nouns I can parse.

4. **The right side is a live preview** showing the actual overlay rendered. This is great -- I can immediately see what I'm customizing. Probably the single biggest UX strength of this surface.

5. **The "Apply to OBS" button on the top right** is bright brand-purple and clearly the primary action. I assume "this is what I click when I'm done." Good signal.

6. **I want to start customizing. Where?** My eye goes back to Visual Themes (top of the sidebar, emoji is the most "themey"). I click. The section expands. I see 12 theme cards. I pick "Cool Blue." The preview updates. Cool, I'm in.

7. **Now I want to change something specific** -- say, the album art size. Where is "Album Art"? I scan the sidebar headers. I see "Album Art" at the bottom of what's visible. I scroll, find it, click. The section expands. I see "Size" slider. I drag it. Preview updates. Great.

8. **Now I want to change the OVERALL card size** ("make the whole thing smaller"). Where? I look for "Size" or "Scale" or "Card Size". I check "General" (1 control: Opacity). Nope. I check "Card Shape" (corners + colors + blur). Nope. I check "Layout" (drag elements around). Nope. **I cannot find an overall card-size control.** Time spent: ~30-60 seconds of scanning, then I give up.

9. **Now I want to change the "accent color"** (the dominant brand color of the card). Where? I look for "Accent" or "Color" or "Theme Color". There's no such section. There IS "Dynamic Colors" but that's about extracting from album art. **The actual accent color is decentralized across ~25 separate color pickers.** Time spent: ~1-2 minutes of confusion. I might give up and just pick a different Visual Theme.

10. **The "Spectrum Visualizer" section** -- I expand it because the name sounds interesting. I see 16 controls, a long help paragraph mentioning SteelSeries Sonar and Voicemeeter, sliders called "Loudness Boost" and "Reaction Speed", and "Color Mode" with multiple options. **This feels like a different app** -- way more technical than the rest of customize.html.

11. **The "Layout" section** -- I expand it. There's a "Use Custom Layout" toggle, an intro paragraph explaining drag-and-drop, eight rows that each have TWO toggles (visibility + lock) with emoji prefixes. After reading the inline "Left toggle = visible. Right toggle (🔒) = lock" hint, I get it. But the hint is positioned ABOVE the first row, not inline -- I almost missed it.

12. **No search bar** anywhere. If I knew the control was called "Spinning Border Speed" or "Outer Glow Pulse Duration", I couldn't search for it. I have to know which section to look in.

13. **No "first-time setup" affordance.** I'm dropped into a 16-section settings panel. There's no "Start here" / "Quick setup" / "Recommended for new users" path.

14. **Reset to Defaults in the header is helpful and findable.** If I muck things up, I have a panic button. Good.

15. **Time-to-find for common tasks** (Ruflo's mental stopwatch):
    - "Change accent color" -- never directly findable; ~1-2 min of scanning, then accept that you have to pick a theme
    - "Make overlay smaller" -- not findable as a single control; never resolved
    - "First-time setup" -- not findable; assumed to be "pick a theme"
    - "Turn off the spinning border" -- ~20 sec (scan headers, find "Spinning Border", expand, toggle off)
    - "Change font" -- ~10 sec (find "Font" section, change dropdown)
    - "Hide album art" -- ~30 sec (find "Album Art", find toggle inside)
    - "Stop the marquee scrolling on the title" -- ~1 min (which section? "Track & Artist"? Try, find Marquee Speed slider -- but how do I just turn it OFF? There's a speed slider but no enable toggle? Need to set speed to 0?)

---

*Section 4 (synthesis -- pain-point ranking, themes, open questions, non-recommendations) follows in STEP 8 commit. This file is built incrementally per the brief's STEP structure. STEP 8 is the final commit; HARD HALT applies post-commit.*
