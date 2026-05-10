# V14 Stage 7.7B-FIX -- STEP 1 Exhaustive Theme Audit

Generated: 2026-05-10  
Scope: All tray_csharp XAML files -- 18 files, 7 defect classes (A-G)

---

## Summary

| Class | Description | Found | Critical | Action |
|---|---|---|---|---|
| A | Missing StaticResource/DynamicResource | 7 "missing" in grep | 0 crash-causing | None |
| B | Setter.Property non-DP | 2 (both pre-fixed) | 0 remaining | None |
| C | BasedOn type mismatch | 0 | 0 | None |
| D | Storyboard.TargetName orphan | 0 | 0 | None |
| E | MergedDictionaries order violation | 0 | 0 | None |
| F | Trigger Property non-DP + binding issue | 1 binding issue | 0 crash | Fix in STEP 2 |
| G | x:Key duplicates across Theme files | 0 | 0 | None |

**Net crash-causing defects remaining: 0**  
**Previously fixed defects (committed pre-audit): 2**  
**Minor functional bug found (not crash): 1** -- SetupWizardWindow breadcrumb binding

---

## Class A -- Resource Reference Audit

### Total references scanned
- All `{StaticResource X}` and `{DynamicResource X}` across 18 XAML files
- 291 reference occurrences, 64 unique keys referenced
- 74 unique keys defined in Theme/*.xaml

### "Missing" resources breakdown

7 keys appeared in references but not in Theme/*.xaml. All explained:

**1. AccentBarDuration**
- Status: DEFINED in Theme/Animations.xaml:14 (`<Duration x:Key="AccentBarDuration">0:0:8</Duration>`)
- Brief incorrectly listed as missing; was defined from STEP 1 onward.

**2. SystemAccentColorPrimaryBrush, TrayMenuBackgroundBrush, BoolToVis, TrayMenuSeparatorBrush, TrayMenuHeaderBrush, TrayMenuItemHoverBrush**
- Status: DEFINED in App.xaml Application.Resources
- Not in Theme/*.xaml but always available in merged chain.

**3. ActivePillStyle, PlatformListItemStyle**
- Status: DEFINED in PlatformsWindow.xaml Window.Resources
- Dialog-local, correct pattern for styles used only in one dialog.

**4. AudioTabControlStyle, AudioTabItemStyle, DeviceListBoxStyle, DeviceListItemStyle, DeviceRowTemplate**
- Status: DEFINED in AudioDeviceWindow.xaml Window.Resources

**5. StepBarStyle, StepBarActiveStyle, WizardActivePillStyle, WizardDeviceItemStyle**
- Status: DEFINED in SetupWizardWindow.xaml Window.Resources

**6. WPF-UI DynamicResource tokens (7 keys)**
- ApplicationBackgroundBrush, ControlFillColorDefaultBrush, ControlFillColorSecondaryBrush,
  ControlStrokeColorDefaultBrush, SolidBackgroundFillColorTertiaryBrush,
  TextFillColorPrimaryBrush, TextFillColorSecondaryBrush
- Status: PROVIDED by ui:ThemesDictionary (WPF-UI Fluent Dark)
- Used ONLY in old ErrorDialogWindow.xaml and UpdateProgressWindow.xaml
- Type: DynamicResource -- no crash, renders transparent if not found
- Both files being fully rebuilt in STEP 4 with our design tokens

**Action: None required for Class A.**

---

## Class B -- Setter.Property Non-DP Audit

### Already fixed (pre-audit)

| File | Line | TargetType | Property | Why Bad | Fix Commit |
|---|---|---|---|---|---|
| Typography.xaml | (removed) | TextBlock | TextOptions.TextFormattingMode | Attached property; DependencyPropertyConverter returned null at BAML load | 43e0796 |
| AppDialogStyle.xaml | (removed) | Window | WindowStartupLocation | CLR-only property; no Window.WindowStartupLocationProperty | 727e309 |

### Remaining in Theme files

All 31 unique Setter.Property values in Theme/*.xaml confirmed as valid DependencyProperties:
WindowStyle, AllowsTransparency, Background, ResizeMode, ShowInTaskbar, Foreground,
FontFamily, FontSize, Template (AppDialogStyle); Background, Foreground, FontFamily,
FontSize, FontWeight, Padding, MinWidth, Cursor, BorderThickness, BorderBrush
(Buttons); CaretBrush, SelectionBrush, ItemContainerStyle (Inputs); CornerRadius,
Effect, Height, Width, HorizontalAlignment, VerticalAlignment, Margin (Cards/Inputs);
LineHeight, LineStackingStrategy (Typography). All confirmed DPs.

### Dialog-local (not startup crash risk)

- `AudioDeviceWindow.xaml:141`: `ScrollViewer.HorizontalScrollBarVisibility` in
  ListBox style. Valid WPF attached property Setter pattern; ScrollViewer.HorizontalScrollBarVisibilityProperty
  IS a registered DP. Loaded only when AudioDeviceWindow opens, not at startup. KEEP.

**Action: None required for Class B.**

---

## Class C -- BasedOn Chain Audit

8 BasedOn references checked. All type-correct (same TargetType on parent and child).

**Action: None required for Class C.**

---

## Class D -- Storyboard Target Audit

1 Storyboard.TargetName reference found: `AccentShimmer` in AppDialogStyle.xaml.
`<Border x:Name="AccentShimmer">` exists in the same ControlTemplate scope.

**Action: None required for Class D.**

---

## Class E -- MergedDictionaries Order Audit

App.xaml: WPF-UI ThemesDictionary -> WPF-UI ControlsDictionary -> Theme/Index.xaml

Theme/Index.xaml order:
Colors -> Typography -> Animations -> Buttons -> Inputs -> Cards -> Icons -> BrandIcons -> AppDialogStyle

All consumers load after their dependencies. AccentBarDuration (Animations.xaml)
loads before AppDialogStyle.xaml which uses it. No order violations.

**Action: None required for Class E.**

---

## Class F -- Trigger Property + DataTrigger Binding Audit

### Trigger.Property values (all valid DPs)
IsChecked, IsEnabled, IsKeyboardFocused, IsKeyboardFocusWithin, IsMouseOver,
IsPressed, IsSelected -- all confirmed DependencyProperties on their respective types.

### DataTrigger binding issue (non-crash, visual bug)

**SetupWizardWindow.xaml -- breadcrumb bar DataTriggers**

Current (broken):
```xml
<DataTrigger Binding="{Binding CurrentStep, RelativeSource={RelativeSource AncestorType=Window}}"
             Value="Audio">
```

Problem: `RelativeSource AncestorType=Window` resolves against the Window element's
own CLR/DP properties, NOT against its DataContext. SetupWizardWindow does not have
a `CurrentStep` DependencyProperty on the Window class itself. Binding will fail
silently (DataTrigger never fires, breadcrumb bars never highlight step 2 or 3).

Not a crash. WPF binding errors are non-fatal. But breadcrumb visual state is broken.

**Also affected: Body StackPanel Visibility DataTriggers (same pattern)**

Fix: Change to `{Binding DataContext.CurrentStep, RelativeSource={RelativeSource AncestorType=Window}}`
to explicitly access Window.DataContext (SetupWizardViewModel).CurrentStep.

**Action: Fix in STEP 2 -- update all CurrentStep DataTrigger bindings in SetupWizardWindow.xaml**

---

## Class G -- x:Key Duplicate Audit

No duplicate x:Key values found across Theme/*.xaml files.

**Action: None required for Class G.**

---

## Fix Plan for STEP 2

| # | File | Change | Class |
|---|---|---|---|
| 1 | SetupWizardWindow.xaml | All `Binding CurrentStep, RelativeSource=...` -> `Binding DataContext.CurrentStep, RelativeSource=...` | F |

Only 1 fix required. All other defect classes are clean or pre-fixed.

After fix: `dotnet build -c Release` -- expect 0E/0W.

---

## Confidence Assessment

The two pre-audit fixes (TextOptions.TextFormattingMode + WindowStartupLocation) were
the crash-causing defects. The app should now launch cleanly and render styled dialogs.

The "all grey" operator report was caused by: the old MSI was still installed when
the operator tested, AND the crash prevented styled BAML from loading, so WPF fell
back to default grey controls. After the fixes + fresh MSI install (done in last
session), the app should render correctly.

STEP 3 verification will confirm this with screenshots.
