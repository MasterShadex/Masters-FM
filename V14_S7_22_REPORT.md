==
== V14_S7_22_REPORT.md  --  Stage 7.22 closure report
== WPF tray menu polish + autostart force-ON
==

# 1. Summary

Stage 7.22 -- two operator-feedback tweaks after shipping v14.0.0 -- is
complete and operator-PASSed.

Tweak 1 landed (BIG effort: WPF tray context menu polish):
- Translucent accent-tinted hover background (TrayMenuHoverBrush, 10% accent)
- IsPressed state with stronger tint (TrayMenuPressedBrush, 20%)
- Win11-style accent checkmark Path "M 0 7 L 5 12 L 14 0" stroked with
  BrandPurpleDeep (#7C3AED) on toggleable IsChecked items
- Refined header: album art CornerRadius 6 + drop shadow; "Master's FM"
  15px SemiBold accent; marquee track 12px; 16,12 header padding
- 1px translucent white separators (was opaque BorderSubtle), 12,4 margin
- All 11 menu item icons consistently 16x16 (was mix of 14 and 12)
- ContextMenu container CornerRadius 8 (was 12), Padding 6, MinWidth 240
- Win11 22H2+ Acrylic backdrop wiring preserved (existing code-behind path)

Tweak 2 landed (autostart force-ON):
- Removed `autostart_defaulted_v14_0_0` flag gate from App.xaml.cs
- Unconditional `_autoStartService.Enable()` on every bootstrap
- New log line `[AutoStart] AutoStart forced ON (every install)`

Outcome: PASS. **Strikes consumed: 1 / 3** on STEP 8 (SE5 cycle for XML
comment `--` violation -- documented + fixed cleanly).

---

# 2. Commits landed

11 commits over `9b27a82` (Stage 7.21 closure):

| STEP | SHA | Subject |
|---:|---|---|
| 0 | `0736d18` | Stage 7.22: STEP 0 -- checkpoint + tray menu inventory + autostart code path + design plan |
| 1 | `6a49826` | Stage 7.22: STEP 1 -- autostart force-ON every install (remove flag gate) |
| 2 | `b791e19` | Stage 7.22: STEP 2 -- tray menu design tokens (hover/pressed/separator + art drop shadow) |
| 3 | `4d39068` | Stage 7.22: STEP 3 -- MenuItem template refresh (rounded hover + accent Win11 checkmark + IsPressed) |
| 4 | `b915bd6` | Stage 7.22: STEP 4 -- header polish (album art CornerRadius 6 + drop shadow + accent SemiBold + 12px track + 16,12 padding) |
| 5 | `75253e5` | Stage 7.22: STEP 5 -- Separator style (translucent TrayMenuSeparatorBrush + 12,4 margin) |
| 6 | `6e93e3e` | Stage 7.22: STEP 6 -- icon consistency (all menu item icons bumped to 16x16) |
| 7 | `84cacc5` | Stage 7.22: STEP 7 -- ContextMenu container polish (CornerRadius 8, Padding 6, MinWidth 240) |
| SE5 diag | `963cb61` | Stage 7.22: SE5 DIAGNOSIS -- XML comment '--' violation broke WPF tray build (strike 1/3) |
| SE5 fix | `a7d53dd` | Stage 7.22: SE5 FIX -- replace '--' inside XML comments (XML spec forbids '--' in comments) |
| 10 closure | (this commit) | Stage 7.22: STEP 10 -- memory APPEND + WPF tray polish + autostart force-ON closure |

---

# 3. Files touched

- `src/tray_csharp/App.xaml.cs` (autostart Tweak 2; +12 / -21 net)
- `src/tray_csharp/Theme/Colors.xaml` (new design tokens; +17 lines)
- `src/tray_csharp/Theme/ContextMenu.xaml` (MenuItem template + Separator + container refresh; +43 / -23 net across STEPs 3+5+7+SE5)
- `src/tray_csharp/MainWindow.xaml` (header polish + icon bump; +28 / -16 net across STEPs 4+6+SE5)
- `V14_S7_22_LOG.md` (NEW; force-added past `V*_LOG.md` gitignore)
- `V14_S7_22_REPORT.md` (NEW; this file)
- `md/memory.md` (APPEND only at S10.4)
- `_BACKUPS_2026-05-24_S7_22_PRE/` (disk-only snapshot)

---

# 4. Files NOT touched

- All 4 protected source files (`tray.ps1`, `tray_native.cs`, `launcher.cs`, `server.js`) -- SHA256 UNCHANGED across the entire stage (S0.2 + S9.1 + S10.1)
- `src/customize.html` (`git diff 9b27a82..HEAD -- src/customize.html` EMPTY)
- `src/overlay.html` (`git diff 9b27a82..HEAD -- src/overlay.html` EMPTY)
- All other `src/tray_csharp/**` files (Setup Wizard XAML untouched per absolute rule 4)
- `version.json` (no bump; stays at 14.0.0)
- All menu item text, order, structure unchanged

---

# 5. Tweak 1 detailed deliverable

### Design tokens added (Colors.xaml)
| Token | Value | Use |
|---|---|---|
| `TrayMenuHoverBrush` | `#1A7C3AED` (10% BrandPurpleDeep) | MenuItem IsHighlighted background |
| `TrayMenuPressedBrush` | `#337C3AED` (20% BrandPurpleDeep) | MenuItem IsPressed background |
| `TrayMenuSeparatorBrush` | `#33FFFFFF` (20% white translucent) | Separator background |
| `TrayMenuArtShadow` | DropShadowEffect (Blur 6, Depth 2, Opacity 0.45) | Header album art elevation |

### MenuItem template (AppMenuItemStyle) refresh
- Padding: 12,0 (was 8,0)
- IsHighlighted: `TrayMenuHoverBrush` (was Surface2)
- IsPressed: new trigger with `TrayMenuPressedBrush`
- IsChecked: Path `Data="M 0 7 L 5 12 L 14 0"` stroked with `BrandPurpleDeep` (Win11 checkmark)
- Icon column: 16-wide reserved (unchanged), gap 10 (was 8)

### Header polish (MainWindow.xaml)
- Album art Border: CornerRadius 6 (was 4), margin 0,0,12,0 (was 0,0,10,0), Effect=TrayMenuArtShadow
- "Master's FM" TextBlock: 15px SemiBold (was 14px Bold), BrandPurpleDeep (was BrandPurpleBase)
- Marquee viewport: Height 17 (was 16), TextBlock 12px (was 11px)
- Header MenuItem Padding: 16,12 (new attribute)

### Separator style
- Background: `TrayMenuSeparatorBrush` (was BorderSubtle)
- Margin: 12,4 (was 8,4) -- matches new 12,0 item padding

### Icon consistency (MainWindow.xaml)
- All 11 menu item icons: 16x16 (was 14x14 for 10 items + 12x12 for Quit)

### ContextMenu container (AppContextMenuStyle)
- CornerRadius: 8 (was 12) -- tighter Win11 menu shape
- Padding: 6 (was 4)
- MinWidth: 240 (was 220) -- accommodates wider 16px icons
- Acrylic + CardHoverShadow paths preserved unchanged

---

# 6. Tweak 2 detailed deliverable

App.xaml.cs lines 288-301 (Stage 7.18 Task A's flag-gated block) REMOVED.

New code (12 lines, +12 / -21 net):
```csharp
// Stage 7.22 Tweak 2: autostart force-ON every install. No flag-gating.
try
{
    _autoStartService.Enable();
    _logger.Log("AutoStart forced ON (every install)", "AutoStart");
}
catch (Exception ex) { _logger.LogErr("AutoStart force-on", ex, "AutoStart"); }
```

The prior `Reconcile()` call at App.xaml.cs line 276 stays in place (it handles the existing-shortcut-points-at-wrong-target case).

Operator-locked tradeoff: users who disable autostart from the tray menu will see it re-enabled on the next install/update. Operator accepts this.

---

# 7. SE5 cycle (XML comment '--' violation)

## Original FAIL (silent WARN, not gate)

First STEP 8 rebuild completed with exit 0 and `=== REBUILD DONE OK ===` but
silently emitted:
```
WARN: WPF tray dotnet publish failed (exit 1) -- continuing
```

The script continued past the WARN by design ("continuing" branch). Install
shipped the PRIOR Stage 7.21 STEP 5 DLL because the new build failed to
produce a fresh DLL. Caught by SE2 inspection comparing the installed DLL's
ProductVersion against the current HEAD SHA.

## Diagnosis (commit `963cb61`)

Manual `dotnet publish` revealed:
```
MainWindow.xaml(62,59): error MC3000: 'An XML comment cannot contain '--',
ContextMenu.xaml(51,28): error MC3000: 'An XML comment cannot contain '--',
```

**Root cause:** W3C XML 1.0 spec section 2.5 forbids `--` inside `<!-- ... -->`
comments. Stage 7.22 STEPs 3+4 introduced descriptive comments using ` -- `
(em-dash style per the project's em-dash hard constraint) inside XML comments,
breaking the XAML compile.

## Fix (commit `a7d53dd`)

Replace ` -- ` inside XML comments with ` : ` (for key:value style lines) or
` * ` (for bullet-style lists) or restructure prose to avoid the dash. The
project's em-dash hard constraint targets em-dash CHARACTER replacement; XML
comments are the one source-environment where neither em-dash nor `--` is
valid, so a different delimiter is required there.

**Files affected:** 2 XAML files, 6 line edits across 4 comment blocks.

`dotnet build` clean after fix (no MC3000 errors).

Retry rebuild PASS: 00:59:42 -> 01:10:24 (~10:42 cold cache), exit 0,
`=== WPF tray built ===` line confirms WPF DLL was actually rebuilt,
installed DLL ProductVersion now matches HEAD SHA, autostart log line
present.

## Strike accounting

Strike 1 of 3 consumed on STEP 8. SE6 not triggered. SE5 strict
diagnosis-then-fix protocol followed (no patch-and-pray): DIAGNOSIS commit
captured root cause + fix shape, FIX commit landed minimum-scope correction.

---

# 8. Build infrastructure observation (parked for v14.1.0)

`_full_rebuild.ps1` silently swallows WPF tray build failures via its
"WARN: ... -- continuing" branch. The script then proceeds to package + install
the OLD DLL from a prior successful build, shipping a broken-or-stale binary
without raising an error.

This is the second time the cycle has bitten a stage (Stage 7.19.5 hit the
same pattern with VBCSCompiler hangs that the script papered over). Both
times the symptom was identical: rebuild reports DONE OK but installed DLL
is stale.

Permanent fix for v14.1.0 (touches `_full_rebuild.ps1` which is not in the
4-file protected list but operator preference may park it): change WPF tray
publish failure from WARN to HARD ERROR + abort. Plus VBCSCompiler pre-kill
at script END to prevent the hang pattern at script-START.

Parked per SE7.

---

# 9. Operator verification

PASS at attempt 1 (post-SE5-fix). Verified:
- Autostart check: after fresh install, "Start on login" should be checkmarked
  (also "AutoStart forced ON (every install)" log line confirms code path)
- Tray menu polish (subjective): operator confirmed visual upgrade
- No regression: all click behaviors, tray icon, album art updates, Setup
  Wizard untouched

---

# 10. Strikes consumed + execution rules

| Rule | Status |
|---|---|
| SE1 per-STEP verification | YES (verified each STEP before next) |
| SE2 mandatory log inspection after rebuild | YES (caught the silent WARN at STEP 8, triggered SE5) |
| SE3 mandatory diff review every commit | YES (11 commits, all scope-matched) |
| SE4 strict PASS/FAIL gate | YES (operator gave explicit PASS) |
| SE5 diagnosis-then-fix pairs | YES (1 pair on STEP 8: `963cb61` diag, `a7d53dd` fix) |
| SE6 three-strike halt | not triggered (1 of 3 consumed) |
| SE7 no scope expansion | YES (parked: `_full_rebuild.ps1` WARN→ERROR, VBCSCompiler script-end kill, Setup Wizard polish, About dialog polish) |
| SE8 protected files SHA256 | YES (verified S0.2 + S9.1 + S10.1, all UNCHANGED) |

**Strikes consumed: 1 / 3.**

---

# 11. v14 status

Still **v14.0.0** (no version bump). Stage 7.22 lands as fix-forward via
commit SHA suffix.

Cumulative fix-forward chain:
```
7.17 718e3e1 -> 7.18 99c5f2d -> 7.19 02340e4 -> 7.19.5 b9e18aa
            -> 7.20 cad6fd5 -> 7.20.5 ba79b66 -> 7.20.6 0be1059
            -> 7.21 9b27a82 -> 7.22 (closure SHA)
```

Installed `MastersFM_Tray_v14.dll` `ProductVersion` after S8 retry rebuild:
`14.0.0+a7d53dd569f1f1355f7de724c4eaf7df107a0eeb` (SE5 FIX commit).

---

# 12. Scope-expansion temptations parked (SE7)

1. **`_full_rebuild.ps1` WARN -> HARD ERROR on WPF tray publish failure**
   -- script currently silent-fails. Parked for v14.1.0.
2. **VBCSCompiler script-end cleanup** -- add `Stop-Process -Name VBCSCompiler`
   at end of `_full_rebuild.ps1` to prevent hang pattern. Parked for v14.1.0.
3. **Setup Wizard XAML Win11 polish parity** -- the same polish could be
   applied to the Setup Wizard. Operator absolute rule 4 excluded it. Parked.
4. **About dialog visual polish** -- another WPF surface that could match the
   new tray menu look. Out of scope. Parked.
5. **TrayMenuViewModel.NowPlayingHeaderText refactor** -- minor binding hop
   improvement. Cosmetic. Defer.

---

# 13. Next operator action

After shipping the updated build to friends, gather feedback on:
- The Win11 polish feel (does the hover/check/header read well in real use)
- Autostart force-ON behavior (any user complaints about re-enable)

Both signals feed v14.1.0 planning.

== END OF FILE ==
