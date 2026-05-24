==
== V14_S7_23_REPORT.md  --  Stage 7.23 closure report
== WPF tray menu polish ROUND 2 (contrast, readability, deeper polish)
==

# 1. Summary

Stage 7.23 -- Round 2 of WPF tray context menu polish after operator
feedback that Stage 7.22 still felt grey-on-grey, weird-edged, and hard
to read -- is complete and operator-PASSed.

The brief's "go harder" mandate landed in 9 source commits across 4
tray_csharp files, plus this report + memory APPEND in the closure commit.

Deliverables (operator-locked decisions from S0.6 + S0.7 + S0.8):
- Pure white text on near-black solid background (max contrast)
- Bigger menu items: 16px font, 14,10 padding, 18px icons
- Solid #1A1A1A background, NO acrylic (crisp edges instead of haze)
- Crisp 1px #33FFFFFF border + bigger drop shadow (blur 20, depth 4)
- Section sub-headers: ACTIONS / TOGGLES / ABOUT (visual grouping)
- Thinner more breathing-room separators (opacity 0.6, 14,6 margin)
- Bigger header: 52px album art + 17px SemiBold accent app name + 13px
  track + 18,14 padding
- Inline descriptions under 6 non-obvious items via MenuItem.Tag
- Neutral white hover (#22FFFFFF), accent reserved for app name + checkmark

Outcome: PASS. **Strikes consumed: 0 / 3** (no SE5 cycles).

---

# 2. Commits landed

10 commits on `2605c75` (Stage 7.22 closure):

| STEP | SHA | Subject |
|---:|---|---|
| 0  | `5b78f2a` | Stage 7.23: STEP 0 -- checkpoint + categorization + design tokens locked + acrylic removal plan |
| 1  | `3592551` | Stage 7.23: STEP 1 -- design tokens (high-contrast colors + larger sizes + bigger shadow) |
| 2  | `47b3db7` | Stage 7.23: STEP 2 -- remove acrylic, use solid dark background (crisp edges) |
| 3  | `978d65d` | Stage 7.23: STEP 3 -- bigger MenuItem style + inline description support |
| 4  | `9bcdda2` | Stage 7.23: STEP 4 -- inline descriptions on 6 non-obvious menu items |
| 5  | `075289d` | Stage 7.23: STEP 5 -- section sub-headers (ACTIONS / TOGGLES / ABOUT) |
| 6  | `4e5420b` | Stage 7.23: STEP 6 -- separators thinner + more breathing room |
| 7  | `2a888c9` | Stage 7.23: STEP 7 -- bigger header (52px art + 17px app name + 13px track + 18,14 padding) |
| 8  | `b3420bb` | Stage 7.23: STEP 8 -- container polish (crisper border + bigger shadow + larger radius) |
| 11 closure | (this commit) | Stage 7.23: STEP 11 -- memory APPEND + tray polish round 2 closure |

No SE5 cycles. No diagnosis-fix pairs. No rebuilds repeated.

---

# 3. Files touched

| File | Net diff vs `2605c75` | Role |
|---|---:|---|
| `src/tray_csharp/Theme/Colors.xaml` | +84 / -11 | New high-contrast palette + size + effect tokens; updated Stage 7.22 hover/pressed brushes to neutral white |
| `src/tray_csharp/Theme/ContextMenu.xaml` | +149 / -65 (cumulative) | AppMenuItemStyle template refresh (bigger + description support), new AppMenuSubHeaderStyle, Separator polish, AppContextMenuStyle container polish |
| `src/tray_csharp/MainWindow.xaml` | +89 / -42 (cumulative) | 6 Tag descriptions added, 3 sub-header MenuItems + 1 Separator inserted, header polish (52px art / 17px name / 13px track / 18,14 padding), all 11 icon Path Width/Height 16 -> 18 |
| `src/tray_csharp/MainWindow.xaml.cs` | +14 / -32 (cumulative; net shrink) | Acrylic gate block deleted; using Wpf.Ui.Controls + using System.Windows.Interop removed |
| `V14_S7_23_LOG.md` (NEW) | force-added past `V*_LOG.md` gitignore | running log |
| `V14_S7_23_REPORT.md` (NEW, this file) | tracked | closure deliverable |
| `md/memory.md` | APPEND only | closure entry in S11.4 |
| `_BACKUPS_2026-05-24_S7_23_PRE/` | disk-only | pre-stage snapshot of 5 tray_csharp files |

---

# 4. Files NOT touched

- All 4 protected source files (`src/tray.ps1`, `src/tray_native/tray_native.cs`, `src/launcher.cs`, `src/server.js`) -- SHA256 UNCHANGED end-to-end (S0.2 / S10.1 / S11.1).
- `src/customize.html` -- `git diff 2605c75..HEAD --` returns 0 lines.
- `src/overlay.html` -- 0-line diff.
- `build_tools/build_msi.py` -- 0-line diff.
- `src/tray_csharp/App.xaml.cs` -- UNCHANGED (Stage 7.22 autostart force-ON intact at lines 287-292).
- All other `src/tray_csharp/**` (Setup Wizard XAML, ViewModels, Services, NowPlayingViewModel etc.) -- UNCHANGED.
- `version.json` -- 14.0.0 (no bump; fix-forward via SHA suffix).
- `_full_rebuild.ps1` -- UNCHANGED (silent WARN behavior STILL parked for v14.1.0 per Stage 7.22 lesson).
- Setup Wizard surface -- UNCHANGED.

---

# 5. Tweak 1 detailed deliverable (Stage 7.22's "Tweak 1" continued at Round 2)

### Design tokens added (`Theme/Colors.xaml`)

13 new tokens + 2 in-place updates + `xmlns:sys` namespace addition:

| Token | Value | Role |
|---|---|---|
| `TrayMenuBackgroundBrush` | `#1A1A1A` | container solid bg |
| `TrayMenuBorderBrush` | `#33FFFFFF` | crisp 1px border |
| `TrayMenuTextPrimaryBrush` | `#FFFFFF` | pure white primary text |
| `TrayMenuTextSecondaryBrush` | `#B0B0B0` | inline descriptions + track name |
| `TrayMenuTextTertiaryBrush` | `#808080` | sub-headers ACTIONS/TOGGLES/ABOUT |
| `TrayMenuAccentBrush` | `#7C3AED` | local alias for BrandPurpleDeep |
| `TrayMenuHoverBrush` (UPDATED) | `#22FFFFFF` (was `#1A7C3AED`) | neutral white hover |
| `TrayMenuPressedBrush` (UPDATED) | `#33FFFFFF` (was `#337C3AED`) | neutral white pressed |
| `TrayMenuItemPadding` | `14,10` | per-item padding |
| `TrayMenuHeaderPadding` | `18,14` | header padding |
| `TrayMenuSubHeaderPadding` | `16,12,16,4` | sub-header text padding |
| `TrayMenuSeparatorMargin` | `14,6` | separator margin |
| `TrayMenuItemRadius` | `6` | item rounded background |
| `TrayMenuContainerRadius` | `10` | container outer corners |
| `TrayMenuIconSize` | `18` | icon Path Width/Height |
| `TrayMenuItemFontSize` | `16` | menu item label size |
| `TrayMenuDescriptionFontSize` | `12` | inline description size |
| `TrayMenuSubHeaderFontSize` | `11` | sub-header size |
| `TrayMenuHeaderAppNameFontSize` | `17` | "Master's FM" wordmark size |
| `TrayMenuHeaderTrackFontSize` | `13` | track marquee size |
| `TrayMenuAlbumArtSize` | `52` | header album art size |
| `TrayMenuDropShadow` | Blur 20, Depth 4, Opacity 0.5 | container drop shadow |

### MenuItem template refresh (`Theme/ContextMenu.xaml` `AppMenuItemStyle`)

Auto-height row (was fixed 36px) so two-line items grow naturally:

```xml
<Grid>  <!-- no Height; auto-sizes -->
    <ColumnDefinitions> 18 | 10 | * | 8 | 18 </ColumnDefinitions>

    <ContentPresenter Content="{TemplateBinding Icon}" ... />   <!-- col 0 -->

    <StackPanel Grid.Column="2" Orientation="Vertical">
        <ContentPresenter Content="{TemplateBinding Header}" ... />   <!-- primary -->
        <TextBlock x:Name="DescriptionLine"
                   Text="{TemplateBinding Tag}"
                   FontSize="{DynamicResource TrayMenuDescriptionFontSize}"
                   Foreground="{DynamicResource TrayMenuTextSecondaryBrush}"
                   Margin="0,2,0,0" />   <!-- secondary, collapsed if Tag is null -->
    </StackPanel>

    <Path x:Name="CheckMark" Grid.Column="4" ... />   <!-- Win11 accent check -->
</Grid>
```

`ControlTemplate.Triggers` includes a `Tag={x:Null}` rule that collapses
the description TextBlock. No new C# converter needed; pure XAML.

### Sub-header style (`Theme/ContextMenu.xaml` `AppMenuSubHeaderStyle`, NEW)

Non-interactive MenuItem wrapper (IsHitTestVisible=False, Focusable=False)
whose ControlTemplate is just a TextBlock styled per the Stage 7.23
TrayMenuSubHeader* tokens. Hover and keyboard navigation skip cleanly.

### Items with inline descriptions (`MainWindow.xaml`)

| Item | Tag (description) | Group |
|---|---|---|
| Platform detection | "Pick where to read now playing from" | ACTIONS |
| Audio source | "Pick which audio device to visualize" | ACTIONS |
| Customize overlay | "Change colors, fonts, layout" | ACTIONS |
| Patch notes | "See what's new in this version" | ABOUT |
| View log | "Open the diagnostic log" | ABOUT |
| Check for updates | "Look for a newer version" | ABOUT |

5 items keep null Tag and render single-line: Discord, Start on login,
OBS overlay (ObsLabel parenthetical does the equivalent), Restart, Quit.

### Header refresh (`MainWindow.xaml`)

- Album art: `Width/Height` 44 -> `TrayMenuAlbumArtSize` (52); `CornerRadius` 6 -> 8; right-margin 12 -> 14
- "Master's FM" wordmark: 15px -> 17px (`TrayMenuHeaderAppNameFontSize`); `Foreground` `BrandPurpleDeep` -> `TrayMenuAccentBrush`
- Marquee track: 12px -> 13px (`TrayMenuHeaderTrackFontSize`); viewport height 17 -> 19; top-margin 3 -> 4
- Track Foreground: `TextSecondary` (#9999A1) -> `TrayMenuTextSecondaryBrush` (#B0B0B0)
- Header MenuItem Padding: `16,12` -> `TrayMenuHeaderPadding` (18,14)

### Separator refresh

`AppMenuSeparatorStyle`: Margin `12,4` -> `TrayMenuSeparatorMargin` (14,6),
added `Opacity="0.6"`. Brush stays `TrayMenuSeparatorBrush` (#33FFFFFF).

### Container refresh

`AppContextMenuStyle`: BorderBrush `BorderSubtle` -> `TrayMenuBorderBrush`
(crisp #33FFFFFF), Padding `6` -> `4`, Border template CornerRadius `8`
-> `TrayMenuContainerRadius` (10), Effect `CardHoverShadow` -> `TrayMenuDropShadow`
(bigger shadow). BorderThickness `1` unchanged. MinWidth `240` unchanged.

---

# 6. Acrylic removal (`MainWindow.xaml.cs`)

The Stage 7.22 Acrylic backdrop gate that applied Wpf.Ui's `WindowBackdrop`
to the ContextMenu on Win11 22H2+ was identified by operator feedback as
the "weird edges" symptom (acrylic haze against busy desktop backgrounds).

Removed in `47b3db7`:
- 21-line `OnLoaded()` block that gated `WindowBackdrop.IsSupported(Acrylic)`,
  wired `cm.Opened += ApplyBackdrop` + `cm.Background = Brushes.Transparent`
- `using System.Windows.Interop;` (only used for `HwndSource`)
- `using Wpf.Ui.Controls;` (only used for `WindowBackdrop`)
- `Acrylic supported=` diagnostic log line (was diagnostic-only)

`Wpf.Ui` NuGet package reference STAYS (referenced by Setup Wizard XAML).
Only the local code-behind use of `WindowBackdrop` went.

Solid `TrayMenuBackgroundBrush` (#1A1A1A) is now applied unconditionally
through `AppContextMenuStyle` `Background` setter; no runtime override.

Restoration path documented in MainWindow.xaml.cs comment: if a future
stage wants acrylic back, the original wiring is at Stage 7.22 SE5 FIX
HEAD `a7d53dd`.

---

# 7. Operator gate result

Pre-gate Ruflo-side checks (S10.1) ALL PASS:
- HEAD = `b3420bb` STEP 8 closure
- Working tree clean modulo standing churn
- 4 protected files SHA256: ALL MATCH S0.2 baseline (UNCHANGED)
- `git diff 2605c75..HEAD --` on 4 protected files: 0 lines
- `git diff 2605c75..HEAD -- src/customize.html src/overlay.html`: 0 lines
- `git diff 2605c75..HEAD -- build_tools/build_msi.py`: 0 lines
- `version.json` `version`: 14.0.0 (no bump)
- Installed tray DLL ProductVersion: `14.0.0+b3420bb8eb41df5cd93fa7d9b8062b9b506bcfc3` (matches HEAD)
- Autostart log line PRESENT in fresh overlay.log: `[2026-05-24 02:24:51.603] [INFO ] [TRAY-CS] [AutoStart] AutoStart forced ON (every install)`
- `[ERROR ]`/`[WARN ]` lines in overlay.log: 0
- Build script output: `=== REBUILD DONE OK ===` at 02:24:50

Gate text printed per brief S10.2. Operator response: **PASS** (literal,
via AskUserQuestion gate-result selection after halt; recorded in transcript).

---

# 8. SE rules status (SE1-SE8)

- **SE1** per-STEP internal verification (`dotnet build` after every source-touching STEP): PASS each time (0 errors, 0 warnings; ~2 sec)
- **SE2** mandatory log inspection after STEP 9 rebuild: PASS (0 new ERROR/WARN; autostart force-ON line PRESENT; DLL ProductVersion MATCH; no WPF tray WARN line)
- **SE3** mandatory `git diff --stat HEAD~1 HEAD` after every commit: yes (10 commits, all scope-matched to tray_csharp/Theme/{Colors,ContextMenu}.xaml + tray_csharp/MainWindow.{xaml,xaml.cs} + log)
- **SE4** literal PASS/FAIL at gate, no "continue" shortcut: HONORED (gate halted in transcript; operator gave explicit PASS)
- **SE5** mistake handling: 0 cycles, 0 / 3 strikes consumed
- **SE6** three-strike escalation: NOT TRIGGERED
- **SE7** no autonomous scope expansion: HONORED. Items parked instead of expanded:
  - `_full_rebuild.ps1` HARD ERROR on WPF publish failure (carried from Stage 7.22 v14.1.0 backlog; STILL parked)
  - `_full_rebuild.ps1` VBCSCompiler pre-kill in preflight (carried)
  - Setup Wizard rounded-Win11 audit (whether other context menus exist in app for similar refresh)
  - Mica vs Acrylic backdrop infrastructure review (Wpf.Ui still references)
- **SE8** protected files SHA256 verified at S0.2 + S10.1 + S11.1: all UNCHANGED end-to-end

---

# 9. v14 status

Still **v14.0.0** (no version bump). Stage 7.23 lands as fix-forward via
commit SHA suffix.

Cumulative fix-forward chain:
- Stage 7.17 `718e3e1` (v14.0.0 cut)
- Stage 7.18 -> Stage 7.21 `9b27a82` (customize redesign cycle CLOSED)
- Stage 7.22 `2605c75` (tray polish round 1 + autostart force-ON)
- Stage 7.23 (closure SHA assigned by S11.5 commit)

Installed `MastersFM_Tray_v14.dll` `ProductVersion` after S11.2 final
warm rebuild: will reflect S11.5 closure SHA (currently `b3420bb` from
S9.2 cold rebuild).

---

# 10. Build infrastructure observation (carried from Stage 7.22)

`_full_rebuild.ps1` STILL has the silent `WARN: WPF tray dotnet publish
failed (exit 1) -- continuing` branch that shipped a stale DLL during
Stage 7.22 STEP 8. Stage 7.23 did NOT touch this script (per absolute
rule "no infrastructure changes"). Maintenance candidate STILL parked
for v14.1.0 alongside other infrastructure items.

Stage 7.23 STEP 9 rebuild succeeded on first try (no SE5 cycle, no
silent stale-DLL incident). The cold rebuild took ~10m42s (server.exe
took ~10 minutes alone; the WPF tray publish itself took ~3 seconds).

---

# 11. v14.1.0 candidate backlog (cumulative)

Inherited from prior stages + Stage 7.23 additions:

- Server log rotation policy (parked since Stage 7.15)
- Overlay.html Stage 7.20.5 SE5 substitution removal (no-op for post-7.20.6 configs)
- WPF parked items (Stage 7.19.5 WizardDeviceItemStyle dedup; DeviceRowTemplate consolidation; SystemColors guard cleanup)
- `_full_rebuild.ps1` VBCSCompiler pre-kill at end-of-script cleanup
- `_full_rebuild.ps1` `WARN: WPF tray dotnet publish failed (exit 1) -- continuing` -> HARD ERROR + abort (Stage 7.22 SE5 lesson)
- Version-string consolidation
- Theme glow color follow-master (parked from Stage 7.20.6)
- Real user feedback from shipping to friends
- Stage 7.22 parks: tray menu reuse audit; em-dash audit
- **Stage 7.23 NEW parks:**
  - Audit other context menus / popups in the app for the same high-contrast solid-bg refresh (currently only the tray ContextMenu was touched; Setup Wizard menus / dialog popovers might benefit from consistency)
  - Mica vs Acrylic infrastructure review: Wpf.Ui's `WindowBackdrop` is still referenced via NuGet but no longer used at the call site. If no other call site materializes, the package could be a candidate for elimination (currently kept because Setup Wizard XAML uses Wpf.Ui controls extensively)
  - `Colors.xaml` tray-specific token namespace consolidation: 18 new `TrayMenu*` tokens were added in Stage 7.23; total tray-menu surface is now ~25 tokens across hover/pressed/separator/art (7.22) + bg/border/text/sizes/effects (7.23). Worth a future cleanup pass for naming consistency

---

# 12. Operator verification (checklist 1-23 from S10.2 gate)

The operator PASS-ed against the 23-item gate checklist:

| # | Item | Result |
|---|---|---|
| 1-5 | Contrast + readability (pure white text, accent app name, light grey track, muted readable descriptions) | PASS |
| 6-8 | Edge quality (crisp not hazy; solid bg; defined 1px border + pronounced shadow) | PASS |
| 9-11 | Layout + grouping (sub-headers visible; items grouped; subtle separators) | PASS |
| 12-19 | Inline descriptions (6 items get description; 5 stay single-line; OBS keeps existing ObsLabel parenthetical) | PASS |
| 20-22 | Hover + interaction (subtle white tint not accent; accent check-mark on toggle ON; all clicks work) | PASS |
| 23 | Autostart still works | PASS (verified via overlay.log + `.lnk` file presence) |

No regression observed.

---

# 13. Next operator action

Ship the latest build to friends with both Stage 7.22 tweaks (autostart
force-ON, round-1 polish) PLUS Stage 7.23's round-2 high-contrast pass.

The MSI lives at:
`G:\Project Folder\Master FM\Master's FM Install\MastersFM_Setup.msi`

Friends-distribution bundle at:
`C:\Users\Master\Desktop\MastersFM_Installer\` (3 files: MSI + INSTALL.bat + cert)

After feedback comes in: plan v14.1.0 from real signal, including the
parked items above and any new requests.

== END OF REPORT ==
