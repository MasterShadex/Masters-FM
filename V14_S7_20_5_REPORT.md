==
== V14_S7_20_5_REPORT.md  --  Stage 7.20.5 closure report
== Overlay.html master variable wiring (makes Stage 7.20 masters affect OBS)
== Local-only deliverable. Tracked.
==

# 1. Summary

Stage 7.20.5 -- the overlay.html wiring follow-up to Stage 7.20 -- is complete
and operator-PASSed at attempt 2 (1 SE5 diagnosis-then-fix pair consumed for
the master-accent-propagation root cause).

Scope landed:
- Added `--accent-master`, `--overall-scale`, `--text-scale` CSS variables to
  overlay.html `:root` (defaults match customize.html Stage 7.20)
- Changed 7 per-element accent CSS fallbacks from hex/brand-token to
  `var(--accent-master)` so the fallback chain resolves to master
- Applied `transform: scale(var(--overall-scale)); transform-origin: center;`
  on the `.card-outer` root element
- Wrapped 6 per-element font-size declarations with
  `calc(... * var(--text-scale))`
- Added `.no-glow` and `.no-animations` CSS class rules with broad descendant
  scope + `!important`, preserving the Stage 7.15 clock fix
- Extended `applyConfig()` to read `cfg.masters` and apply: setProperty for
  the 3 master CSS vars + body classList toggle for the 2 master toggles
- Spectrum WebGL color resolution patched to handle the literal
  `var(--accent-master)` string (substitutes master hex before WebGL)
- **SE5 FIX:** added applyConfig pre-processing that substitutes
  factory-default per-element accent values with `var(--accent-master)`
  literal so master accent propagates to default-themed elements out-of-box

Scope honored:
- `src/customize.html` UNCHANGED (Stage 7.20 surface preserved;
  `git diff cad6fd5..HEAD -- src/customize.html` empty)
- `src/server.js` UNCHANGED (round-trip verified READ-ONLY at S0.5)
- All 4 protected source files SHA256 UNCHANGED across the entire stage
  (S0.2 + S9.1 + S10.1)
- Stage 7.15 clock fix preserved (no transitions added to time elements;
  `tabular-nums` retained; clock content updates unaffected by
  `.no-animations`)
- Stage 7.19 surface preserved (no customize.html changes)
- Stage 7.20 surface preserved (no customize.html changes; masters block
  in customize.html DEFAULTS continues to flow through unchanged)
- Stage 7.19.5 WPF binding fix preserved (post-install logs continue to show
  0 InvalidOperationException)

Outcome: PASS. Strikes consumed: **1 / 3** on STEP 9 (one SE5 cycle: FAIL
"accent color on quick settings, fix this" -> diagnosis -> fix -> re-test ->
PASS!).

---

# 2. Commits landed

10 commits over `cad6fd5` (Stage 7.20 closure):

| STEP | SHA | Subject |
|---:|---|---|
| 0   | `62d072c` | Stage 7.20.5: STEP 0 -- checkpoint + overlay inventory + wiring plan locked |
| 1   | `781f6d4` | Stage 7.20.5: STEP 1 -- master CSS variables added to overlay.html :root |
| 2   | `6635b58` | Stage 7.20.5: STEP 2 -- per-element accent vars default to var(--accent-master) |
| 3   | `2476e5d` | Stage 7.20.5: STEP 3 -- Overall Size transform on card root |
| 4   | `0bbd409` | Stage 7.20.5: STEP 4 -- Text Size scaling on per-element font-sizes |
| 5+6 | `434596f` | Stage 7.20.5: STEP 5+6 -- .no-glow and .no-animations CSS class rules |
| 7   | `442b9e6` | Stage 7.20.5: STEP 7 -- JS config-apply for master controls |
| SE5 diagnosis | `0dcab30` | Stage 7.20.5: SE5 DIAGNOSIS -- Master Accent Color not propagating to non-customized elements |
| SE5 fix       | `30262c6` | Stage 7.20.5: SE5 FIX -- substitute factory-default per-element accents with var(--accent-master) |
| 10 (closure) | (this commit) | Stage 7.20.5: STEP 10 -- memory APPEND + overlay master wiring closure |

Stage delta vs. `cad6fd5`:

```
V14_S7_20_5_LOG.md     | +N (running log throughout the brief)
V14_S7_20_5_REPORT.md  | (this file)
src/overlay.html       | +136 / -14 net
```

---

# 3. Files touched

- `src/overlay.html` (+136 / -14 net; ~3636 -> ~3758 lines)
- `V14_S7_20_5_LOG.md` (NEW; force-added past `V*_LOG.md` gitignore)
- `V14_S7_20_5_REPORT.md` (NEW; this file; tracked)
- `md/memory.md` (APPEND only at S10.4)
- `_BACKUPS_2026-05-23_20-58_S7_20_5_PRE/` (disk-only snapshot; NOT tracked)

---

# 4. Files NOT touched

- `src/tray.ps1`               (protected; SHA256 UNCHANGED S0.2 + S9.1 + S10.1)
- `src/tray_native/tray_native.cs` (protected; SHA256 UNCHANGED)
- `src/launcher.cs`            (protected; SHA256 UNCHANGED)
- `src/server.js`              (protected; SHA256 UNCHANGED; round-trip verified READ-ONLY at S0.5)
- `src/customize.html`         (absolute rule 2; `git diff cad6fd5..HEAD -- src/customize.html` EMPTY)
- All `src/tray_csharp/**`     (no WPF changes)
- `version.json`               (no bump; stays at 14.0.0 fix-forward via SHA suffix)
- All other `src/`             (only overlay.html edited)

---

# 5. Per-element accent fallback verified

8 per-element accent CSS-driven sites in overlay.html now resolve to
`var(--accent-master)` when the per-element CSS variable is unset OR holds
the literal `var(--accent-master)` string:

| Site | Selector | Variable |
|---|---|---|
| line 466 | `.bar-b` background | `--bar-color` |
| line 478 | `.now-playing-label` color | `--np-color` |
| line 486 | `.platform-badge` color | `--platform-color` |
| line 491 | `.platform-badge::before` color | `--platform-dot-color` |
| line 500 | `.title` color | `--title-color` |
| line 514 | `.artist` color | `--artist-color` |
| line 569 + 576 | `#time-current` + `#time-total` color | `--ts-color` |

Plus JS-driven 8th accent: Spectrum WebGL color resolution at line ~3630
detects literal `var(--accent-master)` and substitutes the current master hex
before passing RGB to WebGL.

Per-element wins behavior verified at gate (test step 6: Title manually
blue stayed blue when master went red; clicking "Use accent" made it follow
master again).

---

# 6. SE5 cycle (FAIL -> diagnosis -> fix -> PASS)

## Original FAIL (attempt 1)

Operator reply at STEP 9.2 first gate:
> `FAIL accent color on quick settings, fix this. All other things passed!`

The 4 other masters (Overall Size, Text Size, Glow, Animations) all PASSED
on first attempt. Only Master Accent Color failed to propagate.

## Diagnosis (commit `0dcab30`)

**Root cause:** Stage 7.20.5 STEP 2's change of CSS fallback declarations from
hex to `var(--accent-master)` only resolves when a per-element CSS variable
is UNSET. In practice, customize.html ALWAYS sets per-element accent variables
via `R.setProperty('--<element>-color', <hex>)` during applyConfig, populated
from the user's saved config or factory theme defaults. The fallback chain
therefore never activated for default-themed elements.

The "Use accent" link in customize.html (Stage 7.20 STEP 2 affordance) does
work -- it writes the literal `'var(--accent-master)'` string to
`S.<element>.color`, which setProperty propagates and CSS resolves correctly.
But out-of-box, NO element starts in that "follow master" state; every element
holds a factory-default hex.

## Fix (commit `30262c6`)

Pre-process `cfg` at the start of applyConfig (immediately after `_cfg = cfg`):
when `cfg.masters?.accentColor` is set, walk a hardcoded list of 8
factory-default per-element accent values; for any per-element value that
EQUALS its factory default, substitute the literal string
`'var(--accent-master)'`. Downstream setProperty calls then write the CSS
reference and the chain resolves to master.

Per-element wins decision preserved: values that DIFFER from factory defaults
(i.e., user-customized OR non-default theme) pass through unchanged.

## Re-test PASS

Operator re-test reply: `PASS!`

Default-theme Master Accent Color now propagates to all 8 accent-following
elements out-of-box. Per-element customizations still win.

---

# 7. Known limitations (documented honestly)

## 7.1 Non-default themes won't auto-follow master accent

Non-default themes (Neon Blue, Hot Pink, Retro Orange, etc.) have their own
per-element accent values which DIFFER from the factory defaults the SE5
fix checks against. On those themes, master accent won't propagate to
elements automatically. Users still need to click "↺ Use accent" per-element
in customize.html (the Stage 7.20 affordance) to opt in.

**Could be fixed in a future stage** by:
- Adding `var(--accent-master)` sentinel to theme accent-following keys in
  customize.html THEMES (requires lifting absolute rule 2 NO touching
  customize.html, OR commissioning Stage 7.20.6 separately)

## 7.2 Master Overall Size at 150% may clip OBS browser source bounds

`.card-outer` `transform: scale(1.5)` makes the card 50% larger than the
OBS browser source dimensions (typically 1000x200). Content may clip at
the OBS bounds. **Accepted** per S0.6.A; user can resize the OBS source
to accommodate larger card.

## 7.3 Stage 7.15 clock guard preserved by design

Time elements (`#time-current`, `#time-total`) have NO `animation:` or
`transition:` declarations to disable, so the `.no-animations` `!important`
rule has nothing to override on them. Clock content updates happen via
JS `.textContent` mutations which are unaffected by CSS animation/transition
rules. Verified at gate (test step 4 / 5 within Master Animations): clock
still ticked while Master Animations was OFF.

---

# 8. Operator verification

| Attempt | Reply | Outcome |
|---:|---|---|
| 1 | `FAIL accent color on quick settings, fix this. All other things passed!` | SE5 triggered |
| 2 (re-test after SE5 fix) | `PASS!` | Accepted (literal PASS, case-insensitive) |

Strike count: **1 / 3** on STEP 9. Stage proceeds to STEP 10 closure.

---

# 9. v14 status

Still **v14.0.0** (no version bump). Stage 7.20.5 lands as fix-forward via
commit SHA suffix.

Installed `MastersFM_Tray_v14.dll` `ProductVersion` after S10.2 rebuild:
will be `14.0.0+<this closure commit's sha>` once committed.

Cumulative fix-forward chain:

```
7.17  718e3e1  -- local v14.0.0 cut
7.18  99c5f2d  -- Start-on-login + customize UX audit
7.19  02340e4  -- customize redesign foundation
7.19.5 b9e18aa -- WPF Setup Wizard binding fix
7.20  cad6fd5  -- customize redesign (masters + search + advanced)
7.20.5 <new>   -- overlay.html master wiring (THIS STAGE)
```

---

# 10. Remaining customize-redesign work

| Stage | Scope | Estimate |
|---|---|---|
| **7.21** | first-time-setup banner + sidebar structural revision + final polish | ~6-10 h |

Optional follow-up if non-default-theme follow-master is wanted:
- **Stage 7.20.6** -- theme rework to add `var(--accent-master)` sentinel to
  accent-following keys in customize.html THEMES (would require lifting
  absolute rule 2 for that brief)

After Stage 7.21 (or operator's decision to pause): customize redesign cycle
complete. v14 ready for further use OR a potential 14.1.x bump.

== END OF FILE ==
