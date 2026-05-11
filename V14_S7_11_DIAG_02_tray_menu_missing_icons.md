# Diagnosis: Issue 2 -- Tray menu missing icons in front of several items

## Reproduction (operator-confirmed)
Several tray menu items have no icon in the left icon column.

## Source-of-truth analysis

**MainWindow.xaml** -- full MenuItem icon inventory:

| # | Item | Has Icon? | Resource key | Notes |
|---|---|---|---|---|
| 1 | Header (Master's FM / now-playing) | No | -- | Correct -- decorative header, IsEnabled=False |
| 2 | Platform detection | YES | IconSettings | gear icon |
| 3 | Audio source | YES | IconSpeaker | speaker cone |
| 4 | Customize overlay | YES | IconSparkle | 4-pointed star |
| 5 | Discord | NO | -- | IsCheckable=True, no MenuItem.Icon block |
| 6 | Start on login | NO | -- | IsCheckable=True, no MenuItem.Icon block |
| 7 | OBS overlay | YES | IconCamera | camera body with lens |
| 8 | Patch notes | YES | IconInfo | filled circle with i |
| 9 | View log | NO | -- | no MenuItem.Icon block |
| 10 | Check for updates | YES | IconReset | circular refresh arrow |
| 11 | Restart Master's FM | NO | -- | no MenuItem.Icon block |
| 12 | Quit Master's FM | YES | IconClose | X mark |

4 items lack icons: Discord (5), Start on login (6), View log (9), Restart Master's FM (11).

**Theme/Icons.xaml** -- available PathGeometry resources:
IconClose, IconMinimize, IconChevronRight, IconChevronDown, IconCheck, IconArrowLeft,
IconArrowRight, IconReset, IconSpeaker, IconMic, IconInfo, IconWarning, IconError,
IconSettings, IconCamera, IconSparkle.

None of these are appropriate reuses for the 4 missing items. All 4 need NEW icon
definitions added to Icons.xaml.

**Recommended new icons for Icons.xaml:**
- IconDiscord: Discord Clyde logo approximation or generic headset/headphone -- Fluent UI System: ic_fluent_headset_24_regular
- IconStartup: computer power / startup arrow -- Fluent UI System: ic_fluent_desktop_arrow_right_24_regular or ic_fluent_rocket_24_regular
- IconDocument / IconLog: document/file shape -- Fluent UI System: ic_fluent_document_24_regular
- IconRestart: circular arrow similar to IconReset but clockwise, or reuse IconReset -- could reuse IconReset (already used by Check for updates) but this is visually ambiguous; a distinct restart icon is cleaner

**Note on IsCheckable items:**
For Discord (5) and Start on login (6) which are IsCheckable=True: in WPF, the MenuItem.Icon slot is replaced by the checkmark glyph when IsChecked=True. Adding an icon here will show the icon when unchecked and the checkmark when checked -- this is standard WPF behaviour for checkable items with icons and is visually acceptable.

## Root cause
Stage 7.7B-FIX STEP 5 added icons to 7 of 11 items (Platform detection, Audio source, Customize overlay, OBS overlay, Patch notes, Check for updates, Quit). Four items were not given icons: Discord, Start on login, View log, Restart Master's FM. This was acknowledged at the time as a partial completion. No follow-up was scheduled to complete the remaining 4.

## Why prior work missed it
Stage 7.7B-FIX commit log explicitly noted "6 items with icons" as the state at that point. The gap was known but deferred without a tracking issue. Subsequent briefs did not include icon completion in their scope.

## Fix complexity
Small -- 4 new PathGeometry definitions in Icons.xaml + 4 new MenuItem.Icon blocks in MainWindow.xaml. Estimated 30-40 lines total.

## Recommended fix shape (NOT implemented)
1. Add 4 PathGeometry resources to Theme/Icons.xaml: IconDiscord (or IconHeadset), IconStartup, IconDocument (or IconLog), IconRestart.
2. Add <MenuItem.Icon> block to each of the 4 missing items in MainWindow.xaml, following the existing pattern:
   ```xml
   <MenuItem.Icon>
       <Path Data="{StaticResource IconDocument}"
             Fill="{StaticResource TextSecondary}"
             Width="14" Height="14" Stretch="Uniform"/>
   </MenuItem.Icon>
   ```

## Verification after fix
Launch tray. Right-click or left-click to open menu. All 11 non-header items should have an icon in the left column. Discord and Start on login show icons when unchecked; checkmarks when checked.