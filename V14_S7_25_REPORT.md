==
== V14_S7_25_REPORT.md  --  Stage 7.25 closure report
== customize.html rebuild RESEARCH ONLY
==

# 1. Summary

Stage 7.25 -- a pure research stage in preparation for a customize.html rebuild -- is complete and operator-PASSed. Outcome: PASS at attempt 1; strikes consumed 0 / 3.

The deliverable is one comprehensive markdown document: `V14_S7_25_CUSTOMIZE_REBUILD_RESEARCH.md` (1,238 lines across 7 sections + Executive Summary + Conclusion). NO source code changes were performed at any point.

This stage cataloged the current `src/customize.html` (5,473 lines after 11 stacked stages) and proposed a 5-stage rebuild plan (Stage 7.26 -> 7.30, ~15-20h Ruflo + 5 operator gates).

---

# 2. Commits landed

9 commits on `0335724` (Stage 7.24 closure):

| STEP | SHA | Subject |
|---:|---|---|
| 0 | `24c70e3` | Stage 7.25: STEP 0 -- research stage checkpoint + SHA256 baseline |
| 1 | `e17e595` | Stage 7.25: STEP 1 -- setting IDs inventory in research doc |
| 2 | `be36b58` | Stage 7.25: STEP 2 -- CSS variables inventory + Apply-to-OBS contract isolated |
| 3 | `c83bd02` | Stage 7.25: STEP 3 -- themes inventory + pattern analysis |
| 4 | `9abc5fa` | Stage 7.25: STEP 4 -- JS inventory (handlers + state + functions + stage additions) |
| 5 | `16e5cdf` | Stage 7.25: STEP 5 -- features inventory per stage + essential/nice-to-have categorization |
| 6 | `9dfa32a` | Stage 7.25: STEP 6 -- code quality issues + messy patterns identified |
| 7 | `b4845db` | Stage 7.25: STEP 7 -- rebuild skeleton + design language + multi-stage plan + risks |
| 9 closure | (this commit) | Stage 7.25: STEP 9 -- memory APPEND + research stage closure |

8 doc-content commits (STEPs 0-7) + 1 closure commit (STEP 9). STEP 8 is the gate (no commit; review + PASS only).

---

# 3. Files touched

| File | Net diff vs `0335724` | Role |
|---|---:|---|
| `V14_S7_25_LOG.md` (NEW) | force-added past `V*_LOG.md` gitignore | running log |
| `V14_S7_25_CUSTOMIZE_REBUILD_RESEARCH.md` (NEW) | tracked; the deliverable | research document |
| `V14_S7_25_REPORT.md` (NEW, this file) | tracked | closure report |
| `md/memory.md` | APPEND only | closure entry in S9.4 |

---

# 4. Files NOT touched

- All 4 protected source files (`tray.ps1`, `tray_native.cs`, `launcher.cs`, `server.js`) -- SHA256 UNCHANGED end-to-end (S0.1 + S9.1)
- `src/customize.html` -- 0-line `git diff 0335724..HEAD --`
- `src/overlay.html` -- 0-line diff
- `src/tray_csharp/**` -- 0-line diff
- `build_tools/build_msi.py` -- 0-line diff
- `_full_rebuild.ps1` -- 0-line diff (not invoked)
- `version.json` -- 0-line diff (14.0.0)
- Setup Wizard UNCHANGED
- All other source files UNCHANGED

---

# 5. Research deliverable highlights

### Section 1 -- Setting IDs (141 unique c-* attributes)

- 141 confirmed unique IDs (was estimated 149-154 in prior stages; 141 is the truth)
- Distributed across 17 sections; line range L1139-2180
- ~50 toggles + ~50 sliders + ~25 color pairs + ~8 dropdowns + ~4 text inputs + 1 inline reset button + 1 color list container
- The 5 masters (Quick Settings, Stage 7.20) mapped to their CSS variables
- Advanced-only marking pattern via runtime `markAdvancedElements()` at L4060
- Complete ID list grouped by section (rebuild migration checklist)

### Section 2 -- CSS Variables + Apply-to-OBS contract

- 84 customize-side variable definitions; 14 overlay-side consumers
- **True contract analysis:** the contract is NOT the CSS variable names per se; it's (A) the 5 master config-key shape and (B) the 141 c-* setting IDs as config keys
- 5 master CSS var names treated as IMMUTABLE by convention: `--accent-master`, `--overall-scale`, `--text-scale`, `--glow-master-enabled`, `--animations-master-enabled`
- 79 customize-only CSS variables grouped by purpose; all free to refactor

### Section 3 -- Themes (22 themes)

- 22 themes (Rainbow Default + 21 themed presets) at L2302-2567
- Sentinel-vs-hex categorization: 6 accent-following keys (sentinel `var(--accent-master)`) + theme-specific hex keys (card bg, border colors, glow gradient, progressBar fills)
- Common shape across all 21 non-default themes -> DRY refactor candidate for v14.1.0

### Section 4 -- JavaScript

- Single `<script>` block L2232-5211 (~2,979 lines, 54% of file)
- 37+ functions cataloged with line numbers + origin stages
- 3 localStorage keys (customize_show_advanced / customize_welcome_seen / supercat_*_collapsed)
- Single global state variable `let S` at L2674
- 6 duplicated patterns flagged: redundant DOM lookups, bind/sync helper duplication, ~370-line `initBindings` hand-wiring (the standout), dual master-apply call sites, scattered localStorage access, separately-maintained `THEME_SWATCH_COLORS`

### Section 5 -- Features per stage

- Each of Stage 7.19, 7.20, 7.20.6, 7.21, 7.24 contributions mapped to specific elements in current customize.html
- Stage 7.19.5 / 7.20.5 / 7.22 / 7.23 noted as out-of-scope (WPF or overlay)
- 22 must-have + 5 nice-to-have + 2 could-cut rollup

### Section 6 -- Code quality

- 12 inline-style HTML locations with line numbers + suggested utility class fixes
- 9 !important uses (all judged legitimate)
- 2-style-block code smell (the late-added Preset Manager CSS at L5260-5470)
- ~145 stage-tagged comments (~75 CSS + ~70 JS) as organic-growth breadcrumbs
- Duplicated tokens (`--dur-fast` vs `--dur-fast-v2`)
- 5 button variants without unified base
- ~370-line `initBindings` function as the standout rework target

### Section 7 -- Rebuild skeleton + 5-stage migration plan + risks

- Proposed file layout: single-file HTML+CSS+JS organized by FUNCTIONAL concern (not by HTML element type)
- Option A (recommended): consolidated Advanced supercat per operator's "Item 2 = A" decision
- Design language: carry tray menu PRINCIPLES (high contrast, accent restraint, :has() active state) NOT literal styling (no 16px font, no big padding)
- 5-stage migration plan with hours + gates: 7.26 scaffold (3-4h) -> 7.27 Apply-to-OBS port (3-4h) -> 7.28 sections+controls port (5-6h) -> 7.29 features port (3-4h) -> 7.30 swap (1-2h). Total ~15-20h Ruflo + 5 operator gates.
- 15-item risk register with mitigations
- 7 operator decision points for Stage 7.26 brief

---

# 6. Operator gate result

Pre-gate Ruflo-side checks (S8.1) ALL PASS:
- Protected files SHA256 all MATCH baseline (trivially -- no source touched)
- `git diff --name-only` shows only the 2 doc files
- customize.html / overlay.html / src/tray_csharp/** all 0-line diff
- Research doc has all 7 section markers
- Research doc total: 1,238 lines

Operator response: **PASS** (literal, via AskUserQuestion gate-result selection; accepted per SE4 first unambiguous PASS).

---

# 7. SE rules status (SE1-SE8)

- **SE1** per-STEP internal verification: yes (each section section-check before commit; grep verification)
- **SE2** N/A this stage (no rebuild by design)
- **SE3** mandatory `git diff --stat HEAD~1 HEAD` after every commit: yes (9 commits)
- **SE4** literal PASS/FAIL at gate, no "continue" shortcut: HONORED. Halt sustained across Stop hook firings; PASS received via AskUserQuestion.
- **SE5** mistake handling: 0 cycles
- **SE6** three-strike escalation: NOT TRIGGERED
- **SE7** no autonomous scope expansion: HONORED. The research doc proposes the rebuild BUT does not actually start it. Stage 7.26 brief writing is the explicit next step pending operator approval.
- **SE8** protected files SHA256 verified at S0.1 + S9.1: all UNCHANGED

---

# 8. v14 status

Still **v14.0.0** (no version bump). Stage 7.25 lands as research-only fix-forward via commit SHA suffix.

Cumulative fix-forward chain:
- Stage 7.17 `718e3e1` (v14.0.0 cut)
- Stage 7.18 -> Stage 7.21 `9b27a82` (customize redesign cycle CLOSED)
- Stage 7.22 `2605c75` (tray polish round 1 + autostart force-ON)
- Stage 7.23 `82d4aa8` (tray polish round 2 high-contrast)
- Stage 7.24 `0335724` (customize targeted polish)
- Stage 7.25 (closure SHA assigned by this commit; research only, no source changes)

Installed binaries from Stage 7.24 closure remain in place; tray DLL ProductVersion `14.0.0+b3420bb...` (no rebuild this stage).

---

# 9. Next operator action

The operator has TWO paths after Stage 7.25 PASS:

**Path A -- proceed to Stage 7.26 (rebuild scaffold)**

Operator confirms acceptance of:
1. 5-stage rebuild plan (~15-20h Ruflo, 5 operator gates)
2. Risk of subtle behavior regression somewhere
3. Old `customize.html` stays as `customize_legacy.html` in archive after Stage 7.30 swap (instant revert)
4. Specific decisions on operator's 7 decision points (Section 7.6 of research doc):
   - Option A vs Option B section organization (recommendation: Option A)
   - Tooltip vs inline help (recommendation: tooltip, per operator's earlier conversation)
   - Pure-white labels (recommendation: yes, if hierarchy holds)
   - Section icons (recommendation: yes)
   - 5-stage breakdown (recommendation: as proposed)
   - Risk register mitigations (recommendation: as proposed)
   - Pre-rebuild `_full_rebuild.ps1` fix for HTML-only incremental support (recommendation: do BEFORE Stage 7.26 to avoid 5 × 10-min cold rebuilds)

Then write the Stage 7.26 `CLAUDE_CODE_INSTRUCTIONS.md` (rebuild scaffold).

**Path B -- pause; keep research doc as v14.1.0 refactor roadmap**

The research doc is valuable regardless of whether the rebuild proceeds:
- Sections 1-6 catalog current state for ANY future refactor (in-place cleanup, partial fixes, or full rebuild later)
- Section 7 + risk register can inform v14.1.0 planning
- No commitment to the 15-20h rebuild required

== END OF REPORT ==
