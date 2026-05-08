# V14_S7_S7_6_ACRYLIC_DECISION.md

Stage 7.6 STEP 6 — ContextMenu backdrop investigation.
Locked answer: **Q3=C** (WPF-UI native if supported, else solid 0.85-alpha).

---

## Findings

### WPF-UI 4.3.0 ContextMenu backdrop surface

`Wpf.Ui.Extensions.ContextMenuExtensions.ApplyMica(ContextMenu)` exists in the
net8.0-windows7.0 target. This is the only dedicated ContextMenu backdrop API in
WPF-UI 4.3.0. It applies `DWMSBT_MAINWINDOW` (Mica) to the popup hwnd via DWM.

`Wpf.Ui.Controls.WindowBackdrop.ApplyBackdrop(IntPtr hwnd, WindowBackdropType)`
is also available and accepts any hwnd — including the ContextMenu popup hwnd
extractable via `PresentationSource.FromVisual`. With `WindowBackdropType.Acrylic`
this would apply `DWMSBT_TRANSIENTWINDOW` (frosted Acrylic). More invasive to hook
(requires `ContextMenu.Opened` → `HwndSource.CompositionTarget`).

`WindowBackdrop.IsSupported(WindowBackdropType.Acrylic)` → true only on
Windows 11 22H2+ (build 22621+); false on Windows 10 and earlier Windows 11.

---

## Decision

**Q3=C resolution:** Use `ContextMenuExtensions.ApplyMica()` as the native branch.

Rationale:
1. It is the API WPF-UI explicitly ships for ContextMenus — no manual hwnd extraction.
2. Mica on a ContextMenu popup is visually appropriate (desktop wallpaper blur through
   the menu background) and matches Windows 11 Start/Shell design language.
3. `WindowBackdrop.IsSupported(WindowBackdropType.Mica)` covers the same platform gate
   as Acrylic (both require Windows 11 22H2+).
4. No additional PresentationSource hooking or hwnd extraction needed.

**Branch A (Windows 11 22H2+):**
- ContextMenu `Background = Transparent` (let Mica show through)
- Apply `ContextMenuExtensions.ApplyMica(contextMenu)` in code-behind after
  `TrayMenuViewModel` is wired (STEP 11 implementation)
- Platform gate: `WindowBackdrop.IsSupported(WindowBackdropType.Mica)` (checked once
  at app startup; stored as `_micaSupported` bool)

**Branch B (Windows 10 / Windows 11 21H2):**
- ContextMenu `Background = {StaticResource TrayMenuBackgroundBrush}` (dark solid)
- `TrayMenuBackgroundBrush` = `SolidColorBrush Color="#D91A1A1A"` (0x D9 ≈ 0.85 alpha)
  added to App.xaml in STEP 11

**XAML approach:** The Background binding will be set in code-behind after checking
`_micaSupported`. The ContextMenu XAML sets `Background="{StaticResource TrayMenuBackgroundBrush}"`
as the default; code-behind overrides to Transparent + ApplyMica if branch A.

---

## Resource keys added in STEP 11

| Key | Type | Value |
|---|---|---|
| `TrayMenuBackgroundBrush` | SolidColorBrush | `#D91A1A1A` (≈85% alpha dark) |
| `TrayMenuSeparatorBrush` | SolidColorBrush | `#FF3A3A3A` |
| `TrayMenuHeaderBrush` | SolidColorBrush | `#FF9333EA` (brand purple) |
| `TrayMenuItemHoverBrush` | SolidColorBrush | `#33FFFFFF` (20% white overlay) |

---

## Implementation note for STEP 11

`ContextMenuExtensions.ApplyMica` must be called AFTER the ContextMenu popup is
open (its hwnd exists). Safe call site: `ContextMenu.Opened` event handler wired
once in MainWindow code-behind `OnLoaded`. The handler calls `ApplyMica` only if
`_micaSupported` is true.

```csharp
// In MainWindow.OnLoaded (STEP 11):
_micaSupported = WindowBackdrop.IsSupported(WindowBackdropType.Mica);
if (_micaSupported)
{
    var cm = NotifyIcon.ContextMenu;
    if (cm != null)
        cm.Opened += (_, _) => ContextMenuExtensions.ApplyMica(cm);
}
```
