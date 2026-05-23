==
== V14_S7_21_REPORT.md  --  Stage 7.21 closure report
== Onboarding + sidebar restructure + final polish
== FINAL STAGE of the customize redesign cycle
==

# 1. Summary

Stage 7.21 -- the final stage of the customize redesign cycle -- is complete
and operator-PASSed at first attempt (zero SE5 cycles, one VBCSCompiler
infrastructure retry on STEP 6).

Scope landed:
- **Sidebar restructure** via 6 super-category collapsible groups
  (Start here / Look / Text / Effects / Audio / Layout (advanced)) wrapping
  17 sections at runtime, with localStorage persistence + search integration
- **Welcome banner** at top of preview pane on first ever open, with
  dismiss button + localStorage gate + footer "Show welcome message" re-show
- **Following-master badges** on the 8 per-element accent pickers showing
  reactive visibility + live color tracking when value is the
  `var(--accent-master)` sentinel
- **Polish pass**: 2 sec-help inline-style cleanups + 4 inline-hint span
  cleanups + sub-header consistency verification

Outcome: PASS. **Strikes consumed: 0 / 3.** No SE5 cycles. One non-source
infrastructure retry on STEP 6 (VBCSCompiler hang per Stage 7.19.5 lesson;
killed + retried; built clean).

**CUSTOMIZE REDESIGN CYCLE COMPLETE.** Build is shippable to friends.

---

# 2. Commits landed

8 commits over `0be1059` (Stage 7.20.6 closure):

| STEP | SHA | Subject |
|---:|---|---|
| 0 | `66ec074` | Stage 7.21: STEP 0 -- checkpoint + restructure plan + onboarding spec + polish list |
| 1 | `0aa603d` | Stage 7.21: STEP 1 -- supercat CSS scaffolding |
| 2 | `42585e0` | Stage 7.21: STEP 2 -- HTML wrap sections in supercats + JS toggle + localStorage + search integration |
| 3 | `98d040b` | Stage 7.21: STEP 3 -- welcome banner HTML + CSS + JS localStorage gate + footer reshow link |
| 4 | `7cddfa0` | Stage 7.21: STEP 4 -- following-master badges for 8 accent pickers |
| 5 | `d091812` | Stage 7.21: STEP 5 -- polish pass (inline-style cleanup via sec-help-emphasized + sec-help-tip + inline-hint) |
| 6 | (no commit; rebuild + SE2 documented in log) | -- |
| 8 (closure) | (this commit) | Stage 7.21: STEP 8 -- memory APPEND + cycle-complete closure |

---

# 3. Files touched

- `src/customize.html` (substantial additions; structural reorganization + 3 new feature blocks + polish)
- `V14_S7_21_LOG.md` (NEW; force-added past `V*_LOG.md` gitignore)
- `V14_S7_21_REPORT.md` (NEW; this file)
- `md/memory.md` (APPEND only at S8.4)
- `_BACKUPS_2026-05-23_23-50_S7_21_PRE/` (disk-only snapshot)

---

# 4. Files NOT touched

- All 4 protected source files (SHA256 UNCHANGED across S0.2 + S7.1 + S8.1)
- `src/overlay.html` (absolute rule 2; `git diff 0be1059..HEAD -- src/overlay.html` EMPTY)
- `src/server.js` (UNCHANGED; pass-through)
- All `src/tray_csharp/**`
- `version.json` (no bump; stays at 14.0.0)
- All 141 c-* setting IDs (no IDs added/removed; structural-only change)
- All CSS variable names (no renames)
- All themes (no theme changes; Stage 7.20.6 sentinels intact)

---

# 5. Super-category mapping (locked at S0.5, applied at STEP 2)

| Super-category | Sections inside (in display order) | Conceptual purpose |
|---|---|---|
| Start here         | Quick Settings, General                                         | Entry point: masters + global tweaks |
| Look               | Themes, Card appearance, Font, Album art, "Now Playing" label, Platform badge | Card chrome + identity + brand badge |
| Text               | Track and artist text, Progress bar and time                     | Text-bearing elements |
| Effects            | Outer glow, Text glow, Spinning border, Auto-color from album art | Visual effects |
| Audio              | Audio bars                                                       | Spectrum visualizer |
| Layout (advanced)  | Layout, Slide-in animation                                       | Advanced positioning + animation |

Implementation: `restructureSidebar()` at runtime (replaces prior `reorderSidebar()`).
- Builds 6 supercat wrapper divs with header buttons + bodies
- Moves sections into their supercat body in the locked order
- Wires click handlers + localStorage persistence

Sidebar header (search + advanced toggle) stays at top, unaffected.

---

# 6. Welcome banner

- HTML: at top of `.preview` main content area (above `.preview-header`)
- CSS: card-style with subtle accent-bar gradient along top edge, Stage 7.19 design tokens
- JS: `initWelcomeBanner()` reads `localStorage.customize_welcome_seen`; shows banner if absent/false, hides if true
- Dismiss button writes `true` + hides
- Footer "Show welcome message" link writes `false` + re-shows
- Defensive try/catch around localStorage

---

# 7. Following-master badges

- 8 `<span class="follow-master-badge" data-target="<id>">Following master</span>` elements inserted next to each per-element accent picker text input + before "↺ Use accent" link
- CSS: `background: var(--accent-master)` -> live color tracking (no JS needed for color sync); `.follow-master-active` toggles `display`
- JS `updateFollowMasterBadges()` walks all 8 badges, checks text input value, toggles active class
- JS `initFollowMasterBadges()` attaches input listeners + calls update once at startup
- Hooked into syncAll() so theme apply / reset / preset load refresh badges

---

# 8. Polish pass

| Item | File | Approach |
|---|---|---|
| `sec-help` inline-style at line 1056 (Layout) | customize.html | Replaced with `.sec-help-emphasized` CSS class |
| `sec-help` inline-style at line 1067 (Layout) | customize.html | Replaced with `.sec-help-tip` CSS class (uses `--fs-tiny` token) |
| 4 inline `<span style="font-size:11px;...">` at lines 1509/1729/1767/1982 | customize.html | Replaced with `<span class="inline-hint">` (tokenized via existing `--fs-tiny` + `--text-muted`) |
| Sub-header consistency check (7 .sec-subheader blocks from Stage 7.19) | customize.html | Verified: all consistent. No edits needed. |

`--text-xs` token NOT added (existing `--fs-tiny: 11px` already serves this purpose; using it instead of creating duplicate).

---

# 9. STEP 6 VBCSCompiler infrastructure hang (Stage 7.19.5 lesson reinforced)

First rebuild attempt at STEP 6 (background task `ba5s9wpuu`, started 00:07:49) hung at [1/5] `Building server.exe via dotnet publish`. Parent PowerShell PID 28100 accumulated only 0.34 CPU seconds in 7+ minutes; one VBCSCompiler PID 11400 alive but idle.

Same pattern as Stage 7.19.5 S7.2 first attempt: stale VBCSCompiler from a prior session-build cycle blocking the dotnet build pipeline.

Resolution per Stage 7.19.5 lesson:
- Killed PID 28100 (rebuild parent) + PID 11400 (VBCSCompiler)
- Re-dispatched rebuild (background task `bwsm08ctk`)
- Retry built clean: 00:15:29 -> 00:16:09 (40 sec warm cache), exit 0, SE2 PASS

NOT classified as an SE5 strike because:
- Source code was correct (no fix required)
- The hang was build-infrastructure (Roslyn VBCSCompiler daemon state)
- Stage 7.19.5 documented this exact pattern as a known recurrence
- The pre-emptive `Stop-Process -Name VBCSCompiler` mitigation that was used at STEPs 3 and 6 still didn't catch this instance because the stale VBCSCompiler was spawned BY the rebuild itself rather than being pre-existing

Permanent fix candidate (parked for v14.1.0): add `Stop-Process -Name VBCSCompiler -Force -ErrorAction SilentlyContinue` at the very end of `_full_rebuild.ps1` cleanup so the daemon doesn't linger across build cycles. This addresses the recurrence at source.

---

# 10. Operator verification

PASS at attempt 1. Operator gate test included:
- Welcome banner first-run + dismiss + reshow via footer link
- Super-category collapse/expand persistence
- Search auto-expand of matched supercats; hide of empty supercats
- Following-master badges appear on sentinel pickers + react to master accent change (live color tracking) + hide on per-element manual override + reappear on "Use accent" click
- Polish visual diff: no regression (inline-style cleanup transparent to user)
- Apply-to-OBS contract preserved (all 141 c-* IDs + per-element behavior intact)
- All prior stage surfaces preserved (7.19 / 7.20 / 7.20.5 / 7.20.6)

---

# 11. Strikes consumed

**0 / 3.** No SE5 diagnosis-fix pairs. No SE6 escalations. Clean execution.

One STEP 6 infrastructure-retry (VBCSCompiler hang) -- NOT a strike per Stage 7.19.5 precedent.

---

# 12. Scope-expansion temptations parked (SE7)

Documented in V14_S7_21_LOG.md S0.4 + this report:

1. **WPF parked items from Stage 7.19.5** (WizardDeviceItemStyle vs DeviceListItemStyle divergence, DeviceRowTemplate duplication, SystemColors guard cargo-culting) -- WPF surface, not customize.html. Stay parked for a future WPF cleanup stage.
2. **Stage 7.20.5 SE5 substitution removal in overlay.html** -- now redundant for post-Stage-7.20.6 configs but stays for backward compat. Parked for v14.1.0.
3. **`_full_rebuild.ps1` VBCSCompiler pre-kill at end-of-rebuild** -- candidate to add a `Stop-Process -Name VBCSCompiler` at the script's cleanup so the daemon doesn't linger. Parked for v14.1.0 (touches `_full_rebuild.ps1`, not customize.html).

---

# 13. v14 status

Still **v14.0.0** (no version bump). Stage 7.21 lands as fix-forward via commit SHA suffix.

**Cumulative fix-forward chain:**

```
7.17  718e3e1   -- local v14.0.0 cut
7.18  99c5f2d   -- Start-on-login + customize UX audit
7.19  02340e4   -- customize redesign foundation (renames + help + animations + sub-grouping)
7.19.5 b9e18aa  -- WPF Setup Wizard binding fix
7.20  cad6fd5   -- customize redesign Stage 2 (masters + search + advanced)
7.20.5 ba79b66  -- overlay.html master wiring
7.20.6 0be1059  -- non-default themes accent propagation
7.21  <new>     -- onboarding + sidebar restructure + final polish (THIS STAGE)
```

---

# 14. Customize redesign cycle complete

This stage closes the customize redesign cycle that started with Stage 7.18 Task B (audit).

**Cycle deliverables (across 7 stages):**
- ~110 friendly-voice control labels (Stage 7.19 renames)
- ~50 inline help text paragraphs (Stage 7.19)
- 6 v2 animation tokens for smoother UI feel (Stage 7.19 STEPs 8-9)
- 13 sub-headers in 7 dense sections (Stage 7.19 STEP 10)
- 5 Master controls in "Quick Settings" section (Stage 7.20)
- "↺ Use accent" affordance on 8 per-element accent pickers (Stage 7.20 STEP 2)
- Search bar + Ctrl+F shortcut + live filter (Stage 7.20 STEPs 7-9)
- "Show advanced settings" toggle with Basic/Advanced split + discovery links (Stage 7.20 STEPs 10-11)
- WPF Setup Wizard binding fix (Stage 7.19.5)
- Overlay.html master variable wiring (Stage 7.20.5)
- 22 themes converted to `var(--accent-master)` sentinel (Stage 7.20.6)
- 6 super-category sidebar groups with persistence + search integration (Stage 7.21 STEPs 1-2)
- Welcome banner + footer reshow link (Stage 7.21 STEP 3)
- 8 reactive following-master badges with live color tracking (Stage 7.21 STEP 4)
- Polish pass: inline-style cleanup + token consistency (Stage 7.21 STEP 5)

**What this means for OBS users:**
- Out-of-box first run: welcome banner explains the flow
- Quick Settings at top with 5 master controls that VISIBLY drive the overlay
- Themes work as one-click presets that follow the master accent
- Search finds renamed controls by old jargon (loudness, marquee, etc.)
- Advanced toggle keeps the sidebar approachable in Basic mode
- All Stage 7.15 fixes preserved (clock keeps ticking)
- All prior protected files SHA256 unchanged across the entire 8-stage cycle

**Operator's next step:** ship to friends, gather feedback, plan v14.1.0
from real usage signal.

---

# 15. v14.1.0 candidate backlog

Items deferred from this cycle for a future minor version:
- Server log rotation policy (parked since Stage 7.15)
- Overlay.html Stage 7.20.5 SE5 substitution removal (no-op now)
- WPF Setup Wizard cleanups (WizardDeviceItemStyle, DeviceRowTemplate dedup, SystemColors guard) -- parked since Stage 7.19.5
- `_full_rebuild.ps1` VBCSCompiler pre-kill at script-end cleanup
- Version-string consolidation (still scattered hardcoded fallbacks in some files)
- Theme glow color follow-master (parked from Stage 7.20.6)

== END OF FILE ==
