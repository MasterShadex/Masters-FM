==
== V14_S7_20_REPORT.md  --  Stage 7.20 closure report
== Customize Redesign Stage 2 (master controls + search + advanced toggle)
== Local-only deliverable. Tracked.
==

# 1. Summary

Stage 7.20 -- the second of 3 customize.html redesign stages -- is complete and
operator-PASSed (first attempt; the operator's initial mixed reply "PASS but
FAIL on Quick Settings" was correctly identified as the documented Stage 7.20
limitation per absolute rule 2 + S12.2 and re-prompted per SE4 strict, then
clarified to "fully PASS").

Scope landed:
- New **"Quick Settings"** section at the top of the sidebar with 5 master
  controls: Accent color, Overall size, Text size, Glow toggle, Animations
  toggle. Default-open. Each master writes both to `S.masters.<field>` (which
  persists + transmits to overlay.html via the existing save/preview pipeline)
  AND to a new `:root` CSS variable (`--accent-master`, `--overall-scale`,
  `--text-scale`, `--glow-master-enabled`, `--animations-master-enabled`).
- **"↺ Use accent" links** next to each of the 8 per-element accent pickers.
  Clicking sets the per-element JS value to the literal CSS function string
  `var(--accent-master)` so the element follows the master via CSS resolution
  (once overlay.html consumes the var in Stage 7.20.5).
- **Sticky sidebar header** at the very top of the sidebar containing:
  - **"Show advanced settings"** checkbox (default OFF, persisted to
    localStorage `customize_show_advanced`). Basic mode hides Layout / Spinning
    border / Slide-in animation / Platform badge entire sections + 11 individual
    advanced controls + per-element override sub-groups inside Text glow and
    Auto-color from album art.
  - **Search bar** with placeholder `Find a setting...`, live filter (80 ms
    debounce, lowercase contains-match against `data-search` attributes),
    Ctrl+F focus + select with `preventDefault()` overriding browser find, Esc
    clears + blurs, × clear button visible when input has content.
- **`data-search` attributes** on every `.row` element with `.row-label`,
  populated at init by `buildSearchIndex()`. Each attribute concatenates the
  new (Stage 7.19) friendly label + old jargon labels (via `JARGON_MAP` of
  ~30 high-jargon Stage 7.19 renames) + synonyms + section name.
- **"Want more control? Show advanced settings"** discovery links at the bottom
  of 6 sections that have advanced-hidden content. Clicking flips Advanced ON.
- **`prefers-reduced-motion`** polish: OS-level reduced-motion request
  collapses the new transitions to `transition: none`.

Scope deferred (NOT in this stage):
- All overlay.html consumption of the new masters (deferred to optional
  **Stage 7.20.5**, operator-commissioned, ~3-5 h estimate; documented
  honestly per absolute rule 2 + S12.2 in V14_S7_20_LOG.md).
- First-time-setup banner + sidebar structural revision + final polish
  (deferred to **Stage 7.21**, operator-commissioned).

Outcome: PASS. Strikes consumed: 0 / 24. No SE5 diagnosis-fix pairs. No SE6
escalation. All 30 DoD checklist items PASS.

---

# 2. Commits landed

16 commits over `b9e18aa` (Stage 7.19.5 closure):

| STEP | SHA | Subject |
|---:|---|---|
| 0   | `5f4e50b` | Stage 7.20: STEP 0 -- checkpoint + master/search/advanced specs locked |
| 1   | `15d5f3c` | Stage 7.20: STEP 1 -- Quick Settings section HTML skeleton |
| 2   | `1bea163` | Stage 7.20: STEP 2 -- master Accent Color picker functional |
| 3   | `91e778d` | Stage 7.20: STEP 3 -- master Overall Size slider functional |
| 4   | `31cf4e2` | Stage 7.20: STEP 4 -- master Text Size slider functional |
| 5   | `888f5ec` | Stage 7.20: STEP 5 -- master Glow toggle functional |
| 6   | `6045b63` | Stage 7.20: STEP 6 -- master Animations toggle functional |
| 7   | `662b777` | Stage 7.20: STEP 7 -- data-search attributes via buildSearchIndex |
| 8   | `d4125db` | Stage 7.20: STEP 8 -- search bar HTML + CSS scaffolding |
| 9   | `6fffecc` | Stage 7.20: STEP 9 -- search JS (live filter + Ctrl+F + Esc + clear) |
| 10  | `c20a659` | Stage 7.20: STEP 10 -- advanced toggle HTML + CSS scaffolding |
| 11  | `c106f37` | Stage 7.20: STEP 11 -- mark advanced-only + discovery links + localStorage |
| 12  | `e62456e` | Stage 7.20: STEP 12 -- master save/load verification + Apply-to-OBS reality documentation |
| 13  | `9e58b36` | Stage 7.20: STEP 13 -- search polish (prefers-reduced-motion + maxlength) |
| 14+S15.1 | `52e219f` | Stage 7.20: STEP 14 + S15.1 -- post-rebuild SE2 PASS + pre-gate checks PASS |
| 16  | (pending; this commit) | Stage 7.20: STEP 16 -- memory APPEND + master controls + search + advanced closure |

Stage delta vs. `b9e18aa`:

```
V14_S7_20_LOG.md     | + ~530 lines
V14_S7_20_REPORT.md  | (this file)
src/customize.html   | + 649 lines, no deletions
```

---

# 3. Files touched

- `src/customize.html` (+649 / -0 net; ~4438 -> ~5087 lines)
- `V14_S7_20_LOG.md` (NEW; force-added past `V*_LOG.md` gitignore)
- `V14_S7_20_REPORT.md` (NEW; this file; tracked)
- `md/memory.md` (APPEND only at S16.4)
- `_BACKUPS_2026-05-23_13-30_S7_20_PRE/` (disk-only snapshot; NOT tracked)

---

# 4. Files NOT touched

Per absolute rules from brief:
- `src/tray.ps1`              (protected; SHA256 verified UNCHANGED at S0.2 + S15.1 + S16.1)
- `src/tray_native/tray_native.cs` (protected; SHA256 verified UNCHANGED)
- `src/launcher.cs`           (protected; SHA256 verified UNCHANGED)
- `src/server.js`             (protected; SHA256 verified UNCHANGED)
- `src/overlay.html`          (absolute rule 2; `git diff b9e18aa..HEAD -- src/overlay.html` EMPTY)
- All `src/tray_csharp/**`    (no WPF changes)
- All `src/server/**`         (no server-side changes)
- `version.json`              (no bump; stays at 14.0.0 fix-forward via SHA suffix)
- All other `src/` (only customize.html edited)

---

# 5. Master control behaviors implemented (recap of S0.6)

| Master | JS path | CSS var | Default | Type | Range |
|---|---|---|---|---|---|
| Accent Color | `S.masters.accentColor` | `--accent-master` | `#c060ff` | color picker + hex text | full RGB |
| Overall Size | `S.masters.overallSize` | `--overall-scale` | `100` (= 1.0) | range slider | 50-150 step 5 |
| Text Size    | `S.masters.textSize`    | `--text-scale`   | `100` (= 1.0) | range slider | 50-150 step 5 |
| Glow         | `S.masters.glowEnabled` | `--glow-master-enabled` | `true` (= 1) | toggle | on/off |
| Animations   | `S.masters.animationsEnabled` | `--animations-master-enabled` | `true` (= 1) | toggle | on/off |

Each handler:
1. Writes the new value to `S.masters.<field>` (persisted via existing save flow)
2. Sets the corresponding CSS variable on `document.documentElement.style`
3. Calls the existing `markDirty()` + `preview()` flow via the standard
   `bindColor` / `bindRange` / `bindToggle` helpers
4. Restored on `syncAll()` via `syncColor` / `syncRange` / `syncToggle` with
   `S.masters?.<field> ?? <default>` fallbacks for forward compat

"Use accent" link delegated click handler walks `e.target.closest('.use-accent-link')`,
reads `data-target` attribute, looks up the corresponding `c-<target>-p` picker
+ `c-<target>` text input, writes `var(--accent-master)` to the text input,
dispatches an `input` event so the existing `bindColor` flow captures the change
+ propagates through `markDirty` + `preview`.

---

# 6. Apply-to-OBS limitation honestly documented (per S12.2 + S12.3)

**The masters are wired in customize.html. They do NOT visibly affect the
preview iframe or OBS output in Stage 7.20.**

Reason: the customize.html preview is an `<iframe>` loading `overlay.html` via
`/?preview=1`. The card lives inside the iframe. Absolute rule 2 forbade
touching `overlay.html`, which is where:
- `transform: scale(var(--overall-scale))` would apply to the card root
- `:root { --accent-master: ... }` would resolve `var(--accent-master)` color
  references on per-element pickers that follow the master
- `.no-glow` / `.no-animations` classes would apply with the Stage 7.15 clock
  guard preserved
- `calc(var(--per-element-size) * var(--text-scale))` would wrap font-sizes

Without those changes in overlay.html, the new CSS variables on customize.html's
`:root` are not consumed in the iframe's document, and per-element pickers
set to `var(--accent-master)` resolve to overlay.html's inherited defaults
rather than the master color.

This is the brief's documented expected behavior. The operator's initial mixed
reply at the gate ("PASS but FAIL on Quick Settings") was a forgotten-limitation
report, not a regression. After re-prompt per SE4 strict (referencing the
limitation documentation), the operator confirmed "fully PASS".

### What DOES work in Stage 7.20
- 5 master controls UI exists + is interactive
- Values persist across reload (localStorage for advanced toggle;
  `S.masters` in saved config for the master values)
- Saved presets include the masters block; round-trip preserves intent
- Reset to Defaults restores brand masters
- Apply to OBS transmits `S.masters` to overlay.html in every save payload
- CSS variables get updated on every change to customize.html's `:root`
- "Use accent" links rewrite per-element JS values to `var(--accent-master)`
- Stage 7.19 surface fully intact (friendly labels, help text, animation
  tokens, sub-headers all preserved)
- Stage 7.19.5 WPF binding fix preserved (no IOE in post-install logs)
- Apply-to-OBS contract on EXISTING controls unchanged

### Stage 7.20.5 deliverable (recommended, operator-commissioned)
Adds the following to `src/overlay.html` (~3-5 h estimate):
1. Receive `S.masters` from save / preview-config payload
2. Mirror the 5 master CSS variables in overlay.html's `:root`
3. On config receive, write `S.masters.*` -> `:root` setProperty
4. Apply `transform: scale(var(--overall-scale)); transform-origin: center center` on card root
5. Wrap per-element font-size CSS with `calc(* var(--text-scale))`
6. Add `.no-glow` rule (`filter: none !important; text-shadow: none !important`)
7. Add `.no-animations` rule (`animation: none !important; transition: none !important`)
   with explicit Stage 7.15 clock guard for `#time-current` + `#time-total`
8. Toggle `.no-glow` + `.no-animations` classes on card root from
   `S.masters.glowEnabled` + `S.masters.animationsEnabled`

---

# 7. Search index stats

- Total `.row` elements indexed: ~115 (everything with a `.row-label`)
- Total `c-*` setting IDs: 141 (pre-existing 135 + 5 new master + 1 picker variant)
- `JARGON_MAP` entries: ~30 (covers the highest-jargon Stage 7.19 renames:
  border radius, BG angle/top/bottom, marquee, loudness boost, reaction speed,
  letter spacing, etc.)
- Search query behavior:
  - Empty -> show all (no classes)
  - Non-empty -> 80 ms debounce -> per-section walk:
    - Each row's `data-search` contains-match against lowercased query
    - Matching rows: `.row-highlighted` (subtle purple background)
    - Non-matching rows in matched sections: `.row-dimmed` (35% opacity)
    - Sections with zero matches: `.section-hidden-by-search` (display: none on `.section`)
    - Matched sections auto-expand (`.sec-body.open` + `.sec-header.open`)
- Max query length: 200 chars (input `maxlength` attribute defends against
  pathological-length pastes)
- Ctrl+F (or Cmd+F): preventDefault + focus + select the search input
- Esc on input: clear + blur

---

# 8. Advanced / Basic split

### Entire sections hidden in Basic mode (4 sections)
- Layout (already "Layout (advanced)" per Stage 7.19)
- Spinning border (already "Spinning border (advanced)" per Stage 7.19)
- Slide-in animation
- Platform badge

### Individual rows hidden in Basic mode (11 rows, matched by `.row-label` text)
- Card appearance: Blur behind the card
- Track and artist text: Title letter spacing, Title scroll pause, Artist
  letter spacing, Artist scroll pause
- Audio bars: Make quiet sounds louder, How quickly bars react to music,
  Animation frame rate (advanced), Smoothness
- Outer glow: First color, Second color

### Sub-groups hidden in Basic mode (walked by `.sub-label` divider)
- Text glow: 5 per-element override blocks (Now Playing / Platform / Title /
  Artist / Timestamps glow controls). Master glow toggle in Outer glow stays
  visible in Basic.
- Auto-color from album art: per-element override toggles. Master "Auto-color"
  toggle stays visible in Basic.

### Discovery links (visible only when Advanced is OFF)
Appended to bottom of 6 section bodies:
- `s-card` (Card appearance)
- `s-text` (Track and artist text)
- `s-spectrum` (Audio bars)
- `s-glow` (Outer glow)
- `s-textglow` (Text glow)
- `s-dyncolors` (Auto-color from album art)

Text: "Want more control? Show advanced settings". Clicking flips the Advanced
checkbox ON via delegated click handler.

### Persistence
localStorage key `customize_show_advanced` (string `"true"` / `"false"`).
Defensive try/catch around all read/write so private-browsing or restricted
WebView configs gracefully degrade to ephemeral session state.

---

# 9. Operator verification

**PASS at attempt 1.**

Initial reply was mixed ("PASS but FAIL on all settings under Quick Settings"),
which triggered SE4 strict re-prompt per protocol. Re-prompt clarified the
known limitation per absolute rule 2 + S12.2 (masters not visibly affecting
preview is documented expected behavior, not regression). Operator's
subsequent reply: "Sorry, then fully PASS." Accepted.

This is recorded as PASS at attempt 1 because no SE5 diagnosis-fix pair was
triggered and the gate text already documented the expected limitation. The
re-prompt was a clarification, not a strike.

---

# 10. Strikes consumed

**0 / 24.**

No SE5 diagnosis-fix pairs. No SE6 three-strike escalation. No FAIL gate. The
brief executed end-to-end on first attempt.

---

# 11. Scope-expansion temptations parked (SE7)

Documented in V14_S7_20_LOG.md S0.6.5 + this report:

1. **Missing `V14_S7_19_CUSTOMIZE_REDESIGN_PROPOSAL.md`** referenced by brief
   S0.4. Documentation gap. The audit + Stage 7.19 brief + Stage 7.20 brief
   together cover the design intent. Defer to a documentation-cleanup brief.
2. **`server.log` size 5.6 GB** (no rotation per RULE 5 of standing rules).
   Parked in Stage 7.19's deferred-to-v14.1.0 backlog. Still parked.
3. **Duplicated `c-dyn-*` checkbox set** (7 toggles for "auto-color from album
   art" per-element overrides) could be consolidated. Cosmetic. Defer to v14.1.0.
4. **Animation control IDs not exhaustively grepped at S0.5.D** (slide easing
   variants, etc.). STEP 7's bulk `data-search` pass covers them via row-label
   matching, so search still finds them.
5. **"Use accent" affordance micro-interaction polish** (no animation on
   click, no "Following master" pill state badge). Minimum-viable in Stage 7.20;
   defer richer UX to a future polish brief.
6. **`prefers-reduced-motion` polish at STEP 13** only covers the new Stage 7.20
   transitions; pre-Stage-7.20 transitions (e.g., `.sec-body` itself) are
   already handled in the brief media-query block. No additional work needed.

These are PARKED. Do not implement during Stage 7.20.

---

# 12. Mistakes encountered + diagnosis-fix pairs (SE5)

**Zero SE5 diagnosis-fix commit pairs landed in Stage 7.20.**

Minor mid-STEP self-corrections caught by tool errors before any commit:
- STEP 2 DEFAULTS edit: initial `Edit` call's `old_string` used `--` (double-
  hyphen) where the existing comment had `—` (em-dash from pre-Stage-7.19 code).
  Caught by Edit failure; re-tried with smaller unique context that avoided the
  em-dash line. No commit fingerprint.
- STEP 7 buildSearchIndex placement: was inserted before `syncAll` declaration
  but called from INIT after `initBindings`. Verified the function-hoisting
  worked (JS function declarations hoist). No commit fingerprint.

None reached a commit. Stage 7.20 commit chain is clean (no `DIAGNOSIS` or
`FIX` SE5-pair commits).

---

# 13. Log inspection findings (SE2)

S14.2 post-rebuild inspection: `%LOCALAPPDATA%\MastersFM\overlay.log`
83 lines (fresh post-install).

| Metric | Result vs S0.3 baseline |
|---|---|
| `InvalidOperationException` | 0 (Stage 7.19.5 fix holds) |
| Actual ERROR-level entries | 0 (the 1 regex hit on "Error" is the same `[INFO ]` DialogService init false positive we identified in Stage 7.19.5 S7.2) |
| WARN entries | 0 |
| `[setup wizard show]` | absent (wizard correctly skipped via `welcome_seen=true` persisted flag from Stage 7.19.5 install) |
| New customize.html JS errors | none observed -- STEP 2/3/4/5/6/7/9/11 functions executed cleanly at customize.html load |

`server.log` tail-100: 0 ERROR/WARN.

**SE2 PASS.** No new ERROR/WARN introduced by Stage 7.20.

---

# 14. Recommended Stage 7.20.5 brief: overlay.html master variable wiring

**Scope:** Make the 5 masters visibly apply in the preview iframe + OBS output.

**Estimate:** ~3-5 hours Ruflo + ~10 min operator gate.

**Deliverable:**
1. Receive `S.masters` from save / preview-config payload in overlay.html's
   config-receive handler
2. Add to `overlay.html` `:root`:
   ```css
   --accent-master:             #c060ff;
   --overall-scale:             1.0;
   --text-scale:                1.0;
   --glow-master-enabled:       1;
   --animations-master-enabled: 1;
   ```
3. In config-receive, mirror `S.masters.*` -> overlay.html `:root` via
   `document.documentElement.style.setProperty(...)`
4. Apply `transform: scale(var(--overall-scale)); transform-origin: center center`
   on the card root element
5. Wrap each per-element font-size declaration as
   `calc(var(--<per-element-size>) * var(--text-scale))`
6. Add CSS rules:
   ```css
   .card.no-glow         { filter: none !important; text-shadow: none !important; }
   .card.no-animations   { animation: none !important; transition: none !important; }
   .card.no-animations #time-current,
   .card.no-animations #time-total { animation: none; transition: none; }
   ```
   (The clock rules explicitly preserve Stage 7.15 by avoiding any
   `content` / `visibility` transitions and by being idempotent with the
   broader rule.)
7. Toggle `.no-glow` + `.no-animations` classes on card root from
   `S.masters.glowEnabled` + `S.masters.animationsEnabled` in the
   config-receive handler

**Pre-conditions:** Stage 7.20 closed (this brief).

**Operator decision after Stage 7.20 PASS:** commission 7.20.5 (visual wiring),
OR skip to 7.21 (onboarding + sidebar restructure + final polish), OR pause +
ship the current state to friends.

---

# 15. Remaining customize-redesign work

| Stage | Scope | Estimate |
|---|---|---|
| **7.20.5** (optional, recommended after 7.20) | overlay.html master variable wiring | ~3-5 h |
| **7.21** | first-time-setup banner + sidebar structural revision (super-categories / tabs) + final polish | ~6-8 h |

After 7.21: customize redesign complete. v14 still at 14.0.0 fix-forward chain:

```
7.17 718e3e1 -> 7.18 99c5f2d -> 7.19 02340e4 -> 7.19.5 b9e18aa -> 7.20 <closure sha>
                                                                       |
                                                                       +-> (optional) 7.20.5
                                                                       |
                                                                       +-> 7.21
```

== END OF FILE ==
