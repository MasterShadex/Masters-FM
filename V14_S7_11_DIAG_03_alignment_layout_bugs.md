# Diagnosis: Issue 3 -- Alignment/Layout Bugs

## Reproduction (operator-confirmed)
- Audio Source dialog: tab labels show "WAS" and "M" instead of "WASAPI" / "MME" (cut off)
- Audio Source dialog: "Audio source updated." success toast is oversized, dominates layout
- Audio Source dialog: footer status text feels cramped against the Reset button
- Platforms dialog: scrollbar position extends past the visible list area

---

## Source-of-truth analysis

### Defect A: Tab label truncation

**File:** `src/tray_csharp/Dialogs/AudioDeviceWindow.xaml` lines 19-56

AudioTabItemStyle ControlTemplate:
```xaml
<Grid Height="44" VerticalAlignment="Stretch">
    <TextBlock x:Name="Label"
               Text="{TemplateBinding Header}"
               Style="{StaticResource SubHeadingTextStyle}"
               Foreground="{StaticResource TextTertiary}"
               VerticalAlignment="Center"/>
    <!-- no HorizontalAlignment="Left", no TextTrimming -->
```

`src/tray_csharp/Theme/Typography.xaml` lines 25-29 (SubHeadingTextStyle):
```xaml
<Style x:Key="SubHeadingTextStyle" TargetType="TextBlock">
    <Setter Property="FontFamily" Value="Segoe UI"/>
    <Setter Property="FontSize"   Value="15"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
</Style>
```

**AudioTabControlStyle** (lines 60-95):
```xaml
<StackPanel Grid.Row="0"
            Orientation="Horizontal"
            IsItemsHost="False">
    <TabPanel IsItemsHost="True"
              Background="Transparent"/>
</StackPanel>
```

**What source analysis confirms:**
- SubHeadingTextStyle has no `TextTrimming`, no explicit `Width` or `MaxWidth`
- AudioTabItemStyle has no explicit `Width`, `MinWidth`, or `MaxWidth` on the TabItem or its ControlTemplate Grid
- TextBlock inside the Grid has no `TextTrimming` and no `MaxWidth`

**PARTIALLY INCONCLUSIVE from read-only analysis.** Neither the style nor the template contains an explicit width constraint that would truncate "WASAPI" to "WAS" or "MME" to "M". The most probable causes are:

1. **WPF TabPanel layout measurement issue in nested template.** The `TabPanel` (IsItemsHost="True") is wrapped inside a `StackPanel` (IsItemsHost="False"). During WPF's arrange pass, the TabPanel may receive a constrained width from the StackPanel that doesn't match the measured desiredSize, causing tab items to be arranged at a smaller-than-expected width and the TextBlock's content to be clipped by the Grid bounds.

2. **Missing `ClipToBounds` override.** If the Grid or TabItem is being arranged at a smaller width than the TextBlock's DesiredWidth, the text clips at the Grid boundary without needing TextTrimming.

3. **No left Padding/Margin on TextBlock.** The tab TextBlock has no `Margin` or `Padding` on its left side. If any upstream container contributes unexpected left offset, the tab text starts at x=0 relative to the tab item's content area and could be clipped at the left edge too.

**Diagnostic needed to confirm:** Live measurement -- log `TabItem.ActualWidth` and `TextBlock.ActualWidth` during rendering to see if the tab item is being arranged at a width smaller than the text content. This is a 5-minute check at fix time.

**Fix complexity:** small (likely 1-3 lines -- add `HorizontalAlignment="Left"` to the TextBlock in AudioTabItemStyle, and/or a `MinWidth` on the TabItem)

---

### Defect B: Toast banner always occupies layout space

**File:** `src/tray_csharp/Dialogs/AudioDeviceWindow.xaml` lines 198-209, 352-366

The main dialog Grid defines 5 rows:
```xaml
<RowDefinition Height="80"/>    <!-- Row 0: Header -->
<RowDefinition Height="Auto"/>  <!-- Row 1: Stereo Mix banner -->
<RowDefinition Height="*"/>     <!-- Row 2: Tab bar + content -->
<RowDefinition Height="Auto"/>  <!-- Row 3: Toast banner -->
<RowDefinition Height="64"/>    <!-- Row 4: Footer -->
```

The toast element:
```xaml
<Border x:Name="ToastBanner"
        Grid.Row="3"
        Margin="24,4,24,4"
        Padding="16,8"
        CornerRadius="8"
        Opacity="0"
        IsHitTestVisible="False">
    <TextBlock Text="Audio source updated."
               Style="{StaticResource BodyTextStyle}" .../>
</Border>
```

**Root cause (confirmed from source):** `Opacity="0"` makes the element invisible but does NOT remove it from the WPF layout pass. Row 3 has `Height="Auto"`, so it sizes to the ToastBanner's rendered height. Even at Opacity=0:
- ToastBanner vertical space = Margin top (4) + Padding top (8) + TextBlock line height (21, from BodyTextStyle LineHeight=21) + Padding bottom (8) + Margin bottom (4) = **45px always reserved**

This 45px row permanently steals from the `Height="*"` tab content row (Row 2), reducing the visible device list area when it should be invisible.

**Fix complexity:** trivial (2-3 lines)
- Option A: Set Row 3 height to `0` by default, animate to `Auto` via code-behind when toast fires
- Option B: Use `Visibility="Collapsed"` when toast is not showing and switch to `Visible` before animating opacity (requires code-behind coordination)
- Recommended: Option B -- set `Visibility="Collapsed"` as the default, set `Visibility="Visible"` before starting opacity animation, set `Visibility="Collapsed"` after fade-out animation completes

---

### Defect C: Footer status text cramping

**File:** `src/tray_csharp/Dialogs/AudioDeviceWindow.xaml` lines 369-391

Footer layout:
```xaml
<Grid.ColumnDefinitions>
    <ColumnDefinition Width="Auto"/>  <!-- Col 0: StatusText -->
    <ColumnDefinition Width="*"/>     <!-- Col 1: spacer -->
    <ColumnDefinition Width="Auto"/>  <!-- Col 2: Reset button -->
</Grid.ColumnDefinitions>
<TextBlock Grid.Column="0" Text="{Binding StatusText}" ... />
<Button Grid.Column="2" Content="Reset" ... />
```

**Root cause:** The StatusText TextBlock has `Width="Auto"` (sizes to its content). When `StatusText` is long -- e.g., "4 WASAPI output, 4 input, 4 MME device(s)" -- the Auto column expands to fit the full text. The `*` spacer column shrinks accordingly. At typical device counts, the spacer may collapse to near-zero, giving the impression that the text and button are "cramped" together.

**Contributing factor:** No `Margin` is set on the Reset button's left side (no padding between the spacer and the Reset button's visual label). The spacer column provides separation, but if status text is long, the gap is visually tight.

**Secondary finding:** No `TextTrimming` is set on the StatusText TextBlock. If the status text ever exceeds the full footer width, it overflows silently (no ellipsis).

**Fix complexity:** trivial (1-2 lines -- add `MaxWidth` on StatusText or add left Margin to Reset button, e.g., `Margin="12,0,0,0"`)

---

### Defect D: Platforms dialog scrollbar overflow

**File:** `src/tray_csharp/Dialogs/PlatformsWindow.xaml`

From source inspection of PlatformsWindow.xaml (Width=580, Height=460; two-column layout; ListBox with PlatformListItemStyle):

**INCONCLUSIVE from read-only analysis.** The full scrollbar template and ListView/ScrollViewer configuration for PlatformsWindow could not be fully traced from source inspection. Operator described "scrollbar position extends past the visible list area." This could be caused by:
1. A fixed-height container not properly constraining the ScrollViewer height
2. A custom ScrollViewer style that misaligns the scrollbar track relative to the viewport
3. Incorrect row sizing in the two-column layout

**Diagnostic needed:** Requires live visual inspection or reading the full PlatformsWindow.xaml scrollbar style to confirm.

---

## Summary of defects found

| # | Defect | Status | Fix complexity |
|---|--------|--------|----------------|
| A | Tab label truncation ("WAS"/"M") | Partially inconclusive -- no explicit clip found in source; requires live width measurement | small |
| B | Toast banner always 45px | **Confirmed from source** -- Opacity=0 does not collapse layout | trivial |
| C | Footer status text cramping | **Confirmed from source** -- no min gap between long status text and Reset button | trivial |
| D | Platforms scrollbar overflow | Inconclusive -- insufficient source data | needs live check |

---

## Why prior work missed it

Stage 7.7B FINAL STEP 3 ("layout audit") marked this category as PASS based on structural verification (file written, build succeeds, dialog smoke test passes). The smoke test only checks that dialogs open without crashing -- it does not measure per-element pixel layout. The Opacity=0 toast flaw is invisible during smoke test; the tab truncation only manifests with real text content in a running WPF layout pass.

---

## Fix complexity

See per-defect table above. Defects B and C are trivial one-liners. Defect A is small (requires live diagnosis to confirm root cause first). Defect D is inconclusive (needs reading PlatformsWindow.xaml fully).

---

## Recommended fix shape (NOT implemented)

**Defect B (toast, confirmed):**
In `AudioDeviceWindow.xaml.cs`, change `ShowToast()` method to:
1. Set `ToastBanner.Visibility = Visibility.Visible` before starting the opacity animation
2. After fade-out animation completes, set `ToastBanner.Visibility = Visibility.Collapsed`
In `AudioDeviceWindow.xaml`: change default `Opacity="0"` to also add `Visibility="Collapsed"` on the ToastBanner Border.

**Defect C (footer cramping, confirmed):**
Add `Margin="12,0,0,0"` to the Reset button in the footer Grid (Col 2), ensuring a minimum visual gap between status text and Reset button regardless of StatusText length.

**Defect A (tab truncation, pending live diagnostic):**
Once live diagnosis confirms the exact cause, likely fix: add `HorizontalAlignment="Left"` to the TextBlock in AudioTabItemStyle ControlTemplate, and/or add `MinWidth="70"` to the TabItem style to ensure tab items are never arranged narrower than their content.

---

## Verification after fix

1. Launch Audio Source dialog with 4+ WASAPI devices enumerated
2. Confirm all tab labels "WASAPI", "MME", "KS" render at full width
3. Select a device -- confirm "Audio source updated." toast appears (fades in), then disappears without leaving blank space
4. Confirm footer status text "N WASAPI output, N input, N MME device(s)" does not crowd the Reset button
