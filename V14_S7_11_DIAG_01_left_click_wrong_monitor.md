# Diagnosis: Issue 1 -- Left-click tray opens menu on wrong monitor

## Reproduction (operator-confirmed)
Left-click on tray icon -> menu opens on wrong monitor.
Right-click on same icon -> menu opens correctly aligned to cursor's monitor.

## Source-of-truth analysis

**MainWindow.xaml lines 29-33** -- TaskbarIcon definition:
```xml
<tb:TaskbarIcon x:Name="NotifyIcon"
                LeftClickCommand="{Binding ShowMenuCommand}"
                NoLeftClickDelay="True">
```
Left-click fires ShowMenuCommand via WPF command binding.

**TrayMenuViewModel.cs lines 512-517** -- ShowMenu command:
```csharp
[RelayCommand]
private void ShowMenu()
{
    _logger.Log("TrayMenu: ShowMenu (left-click)", "Tray");
    OpenContextMenu?.Invoke();
}
```
Delegates to the OpenContextMenu action.

**MainWindow.xaml.cs lines 61-68** -- OpenContextMenu delegate wired in OnLoaded:
```csharp
_trayMenuViewModel.OpenContextMenu = () =>
{
    if (NotifyIcon.ContextMenu is { } cm)
    {
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        cm.IsOpen = true;
    }
};
```
This is the source of the defect. PlacementMode.Mouse is used to position the context menu.

**Right-click path (H.NotifyIcon native):**
H.NotifyIcon handles right-click entirely in its Win32 WM_RBUTTONDOWN/WM_RBUTTONUP handler. It uses the Win32 LPARAM from the NIN_CONTEXTMENU notification (which contains the exact cursor coordinates at the time of the click) to position the ContextMenu. This guarantees correct monitor placement.

**Left-click path (our custom code):**
PlacementMode.Mouse in WPF positions the popup using WPF's internal mouse input state -- specifically the last processed WM_MOUSEMOVE or similar mouse message in WPF's input queue. The host window (MainWindow) is Width=0, Height=0, at Left=-1000, Top=-1000, Visibility=Hidden -- it never receives WM_MOUSEMOVE messages from the taskbar notification area. When the tray icon is clicked on Monitor 2, WPF's internal mouse position tracking reflects wherever the cursor last was when a WPF window had focus (which may be Monitor 1 or an outdated position). PlacementMode.Mouse therefore places the menu at the wrong position.

By contrast, if PlacementMode.AbsolutePoint were used with coordinates from System.Windows.Forms.Cursor.Position (Win32 GetCursorPos()), the current physical cursor position would be used regardless of WPF input state, giving the same correct behavior as the right-click path.

## Root cause
PlacementMode.Mouse (MainWindow.xaml.cs line 65) relies on WPF's input message tracking, which is stale for a hidden zero-size host window that never receives WM_MOUSEMOVE from the taskbar. The right-click path works because H.NotifyIcon uses Win32 GetCursorPos() directly. The left-click path does not.

## Why prior work missed it
INTERRUPT #3 STEP 2 implemented LeftClickCommand and the OpenContextMenu delegate. The comment in the code ("Opens the ContextMenu at the current cursor position using WPF PlacementMode.Mouse") is the intended behaviour, but PlacementMode.Mouse silently fails for hidden notification-icon windows. The issue only manifests on multi-monitor setups and was not caught in single-monitor testing.

## Fix complexity
Small -- 3-4 line change in MainWindow.xaml.cs OnLoaded. No new dependencies.

## Recommended fix shape (NOT implemented)
In the OpenContextMenu delegate (MainWindow.xaml.cs ~line 61), replace PlacementMode.Mouse with PlacementMode.AbsolutePoint using the actual Win32 cursor position:

```csharp
_trayMenuViewModel.OpenContextMenu = () =>
{
    if (NotifyIcon.ContextMenu is { } cm)
    {
        var p = System.Windows.Forms.Cursor.Position;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        cm.HorizontalOffset = p.X;
        cm.VerticalOffset = p.Y;
        cm.IsOpen = true;
    }
};
```

System.Windows.Forms.Cursor.Position calls Win32 GetCursorPos() which always returns the actual current cursor position independent of WPF input state. This is the same data source H.NotifyIcon uses for right-click positioning.

## Verification after fix
1. Set up two monitors.
2. Move cursor to Monitor 2 (non-primary).
3. Left-click tray icon.
4. Confirm menu appears on Monitor 2 aligned to cursor position.
5. Right-click tray icon on Monitor 2.
6. Confirm both left and right-click now agree on monitor placement.