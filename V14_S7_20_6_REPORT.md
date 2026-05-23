==
== V14_S7_20_6_REPORT.md  --  Stage 7.20.6 closure report
== Non-default themes accent propagation (Master Accent works on every theme)
== Local-only deliverable. Tracked.
==

# 1. Summary

Stage 7.20.6 -- the theme-side companion to Stage 7.20.5 -- is complete and
operator-PASSed at first attempt (zero SE5 cycles).

Scope landed:
- Updated DEFAULTS in customize.html so 6 accent-following keys + 2 platform
  badge keys hold the literal `var(--accent-master)` sentinel instead of
  factory hex
- Updated all 21 non-default themes to use the same sentinel for the 6
  accent-following keys in each theme block
- Added a `masters: { accentColor: '<theme accent>' }` block to each of the
  21 non-default themes so applying a theme also sets the master picker to
  the theme's intended accent color
- No overlay.html changes (Stage 7.20.5 closed that surface). The
  existing Stage 7.20.5 STEP 7 master block + SE5 fix already handle the
  sentinel correctly; the SE5 fix becomes a harmless no-op for
  post-Stage-7.20.6 configs (no factory hex remains to match) and stays
  in place for backward compat with pre-Stage-7.20.6 saved configs.
- No server.js changes (pass-through confirmed read-only at S0.5).

Outcome: PASS. Strikes consumed: **0 / 3**. No SE5 cycles. All scope
requirements met.

---

# 2. Commits landed

5 commits over `ba79b66` (Stage 7.20.5 closure):

| STEP | SHA | Subject |
|---:|---|---|
| 0 | `8c1ea09` | Stage 7.20.6: STEP 0 -- checkpoint + theme inventory + conversion plan |
| 1 | `83473f3` | Stage 7.20.6: STEP 1 -- theme definitions use var(--accent-master) sentinel for accent-following keys |
| 2 | `00688c2` | Stage 7.20.6: STEP 2 -- theme-apply flow verification (no code changes needed; existing pipeline handles sentinel) |
| 3 | (no commit; rebuild + SE2 documented in log) | -- |
| 5 (closure) | (this commit) | Stage 7.20.6: STEP 5 -- memory APPEND + non-default theme accent propagation closure |

Stage delta vs. `ba79b66`:

```
V14_S7_20_6_LOG.md     | +N (running log)
V14_S7_20_6_REPORT.md  | (this file)
src/customize.html     | +118 / -97 net (215 lines touched)
md/memory.md           | +N (APPEND)
```

---

# 3. Files touched

- `src/customize.html` (+118 / -97 net; 22 theme blocks edited including DEFAULTS)
- `V14_S7_20_6_LOG.md` (NEW; force-added past `V*_LOG.md` gitignore)
- `V14_S7_20_6_REPORT.md` (NEW; this file)
- `md/memory.md` (APPEND only at S5.4)
- `_BACKUPS_2026-05-23_23-00_S7_20_6_PRE/` (disk-only snapshot)

---

# 4. Files NOT touched

- `src/tray.ps1`               (protected; SHA256 UNCHANGED S0.2 + S4.1 + S5.1)
- `src/tray_native/tray_native.cs` (protected; SHA256 UNCHANGED)
- `src/launcher.cs`            (protected; SHA256 UNCHANGED)
- `src/server.js`              (protected; SHA256 UNCHANGED; pass-through verified at S0.5)
- `src/overlay.html`           (absolute rule 2; `git diff ba79b66..HEAD -- src/overlay.html` EMPTY)
- All `src/tray_csharp/**`
- `version.json`               (no bump; stays at 14.0.0 fix-forward via SHA suffix)

---

# 5. Theme conversion summary

22 themes total (1 Default + 21 non-default):

| Theme | masters.accentColor | 6 accent keys -> sentinel |
|---|---|---|
| Rainbow (Default) | `#c060ff` (from DEFAULTS) | DEFAULTS object converted (8 keys including 2 platform-badge) |
| Neon Blue         | `#00c8ff` | YES |
| Hot Pink          | `#ff4499` | YES |
| Retro Orange      | `#ff9900` | YES |
| Synthwave         | `#ff00cc` | YES |
| Forest Green      | `#00ff88` | YES |
| Crimson           | `#ff4422` | YES |
| Midnight          | `#8899cc` | YES |
| Cherry Blossom    | `#ff88bb` | YES |
| Minimal White     | `#aaaacc` | YES |
| Vaporwave         | `#ff71ce` | YES |
| Aurora            | `#22ddff` | YES |
| Royal Purple      | `#9b51e0` | YES |
| Coffee            | `#d2a679` | YES |
| Volcano           | `#ff6600` | YES |
| Ice Crystal       | `#a0e6ff` | YES |
| Galaxy            | `#b388ff` | YES |
| Sunset            | `#ff6e7f` | YES |
| Lime              | `#aaff00` | YES |
| Vintage Sepia     | `#c9a96e` | YES |
| Cyber Matrix      | `#00ff41` | YES |
| Dreamcore         | `#d9b3ff` | YES |

Sentinel count post-conversion: **104** `var(--accent-master)` occurrences in customize.html.
Masters block count: **21** (one per non-default theme; Default uses DEFAULTS.masters).

---

# 6. Per-theme intentional non-accent values preserved as hex

The following theme-specific values stay as hex / theme-original RGBA and are NOT converted to sentinel:

- `card.backgroundTop` / `card.backgroundBottom` / `card.backgroundAngle` (card chrome)
- `border.colors[]` (spinning border gradient stops)
- `glow.color1` / `glow.color2` (outer glow gradient stops -- own color domain)
- `title.glowColor` / `artist.glowColor` / `nowPlaying.glowColor` etc. (text glow colors -- own domain)
- `progressBar.fillColors[]` (progress bar gradient stops)
- `nowPlaying.letterSpacing`, `title.fontWeight`, `artist.glowSize`, etc. (non-color per-element styling)
- `spectrum.colorMode`, `spectrum.barCount`, `spectrum.heightMult` (non-color spectrum settings)

This preserves the visual identity of each theme (cards, borders, gradient glows stay theme-specific)
while making the accent color (text + bars + spectrum + timestamps) master-driven across all themes.

---

# 7. Trade-off accepted (operator-locked at gate)

On themes that previously had subtle accent-shade gradients between elements (e.g., Neon Blue had
`#00d4ff` for nowPlaying and `#60d8ff` for timestamps, slightly different blues), all
accent-following elements now share ONE master accent color when the theme is applied. Users
wanting subtle intra-theme variation can:
- Manually pick per-element colors after applying the theme (per-element wins; master doesn't
  affect them)
- Use the "↺ Use accent" link (Stage 7.20 affordance) to restore follow-master state

This trade-off aligns with operator-locked decision #1 (Master = "set all" shortcut; per-element wins).
Stage 7.20.5 already shipped this trade-off for Default theme; Stage 7.20.6 extends it consistently.

---

# 8. Theme switch overwrites per-element overrides (verified at gate)

`applyTheme()` at line 2383 uses `S = deepMerge(base, { preserve-list })`. The preserve list is:
`overlay`, `art.position`, `nowPlaying.text`, `dynamicColors`, `layout`.

Per-element accent values (`title.color`, `artist.color`, etc.) are NOT in the preserve list --
they get the new theme's values (the sentinel string after this stage's conversion). So switching
themes effectively RESETS per-element overrides back to follow-master. Operator-verified at gate
test step "Theme switch overwrites per-element overrides".

---

# 9. Apply-to-OBS pipeline (unchanged from Stage 7.20.5)

The save/preview flow handles the sentinel correctly because Stage 7.20.5 already wired it:
- customize.html POSTs `S` (with sentinel values) to `/preview-config` or `/save-overlay-config`
- server.js pass-through to overlay.html via SSE-broadcast or fresh GET on overlay reload
- overlay.html `applyConfig()` at line 1764 (Stage 7.20.5 STEP 7) sets `--accent-master` from
  `cfg.masters.accentColor`
- Downstream per-element `R.setProperty('--<element>-color', cfg.<element>.color)` writes the
  literal `'var(--accent-master)'` string when the value is a sentinel
- CSS resolves `color: var(--<element>-color, var(--accent-master))` -> per-element variable
  holds `var(--accent-master)` -> resolves to master accent
- Spectrum WebGL path (line 3630, Stage 7.20.5 STEP 7 patch): detects literal
  `var(--accent-master)` string and substitutes current master hex before RGB

Stage 7.20.5 SE5 fix in overlay.html (factory-default substitution) becomes a no-op for
post-Stage-7.20.6 configs (no factory hex remains to substitute). It stays in place for
backward compat with pre-Stage-7.20.6 saved configs that still hold factory hex values.

---

# 10. Operator verification

PASS at attempt 1. Operator gate test included:
- Apply Neon Blue: master accent picker shows theme accent + accent-following elements turn theme accent in OBS
- Change master accent to red on Neon Blue: OBS accents turn red while card background / border stay theme's dark blue
- Repeat on other themes
- Theme switch overwrites per-element overrides (Title green manually -> Hot Pink applies -> Title pink)
- Default theme regression check: still works
- Preset Manager + Reset to Defaults: still work

---

# 11. Strikes consumed

**0 / 3.** No SE5 diagnosis-fix pairs. No SE6 escalations. Clean execution.

---

# 12. Scope-expansion temptations parked (SE7)

Documented in V14_S7_20_6_LOG.md S0.7:

1. **"Following master" UI badge** instead of `var(--accent-master)` literal text in the per-element
   hex input. Same UX as Stage 7.20 "Use accent" link -- operator-accepted; could be polished later.
2. **Theme-side "deliberately divergent" accent option** (per-key opt-out flags in themes). Stage
   7.20.6 takes the simpler all-or-nothing approach. Defer if needed.
3. **Removal of Stage 7.20.5 SE5 substitution fix** in overlay.html (now redundant for
   post-Stage-7.20.6 configs). Kept for backward compat with pre-existing configs.
4. **Theme glow color follow-master**: outer glow + per-element text glow colors stay theme-specific
   hex. Could add a "Master Glow Color" but that's new master scope. Defer.

---

# 13. v14 status

Still **v14.0.0** (no version bump). Stage 7.20.6 lands as fix-forward via commit SHA suffix.

Cumulative fix-forward chain:

```
7.17  718e3e1   -- local v14.0.0 cut
7.18  99c5f2d   -- Start-on-login + customize UX audit
7.19  02340e4   -- customize redesign foundation
7.19.5 b9e18aa  -- WPF Setup Wizard binding fix
7.20  cad6fd5   -- customize redesign (masters + search + advanced)
7.20.5 ba79b66  -- overlay.html master wiring
7.20.6 <new>    -- non-default themes accent propagation (THIS STAGE)
```

---

# 14. Remaining customize-redesign work

| Stage | Scope | Estimate |
|---|---|---|
| **7.21** | first-time-setup banner + sidebar structural revision + final polish | ~6-10 h |

After Stage 7.21 (or operator's decision to pause): customize redesign cycle complete.

== END OF FILE ==
