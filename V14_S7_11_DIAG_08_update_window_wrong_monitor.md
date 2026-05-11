# Diagnosis: Issue 8 -- Check for Updates window opens on wrong monitor

## Reproduction (operator-confirmed)
Clicking "Check for updates" in tray menu opens UpdateProgressWindow on wrong monitor. All other dialogs (Platform detection, Audio source, Platforms, Error, Welcome) open on the monitor containing the cursor.

## Source-of-truth analysis

**TrayMenuViewModel.cs lines 465-488** -- CheckUpdatesAsync:
```csharp
[RelayCommand]
private async Task CheckUpdatesAsync()
{
    ...
    try
    {
        await _dialogService.ShowUpdateProgressAsync();
        ...
    }
}
```
Goes through DialogService.ShowUpdateProgressAsync -- correct path.

**DialogService.cs lines 128-148** -- ShowUpdateProgressAsync:
```csharp
public Task ShowUpdateProgressAsync()
{
    return ShowOnDispatcherAsync(() =>
    {
        if (_updateWindow != null && _updateWindow.IsVisible)
        {
            _updateWindow.Activate();
            return;
        }
        _updateWindow = _services.GetRequiredService<UpdateProgressWindow>();
        _updateWindow.Owner = Application.Current.MainWindow;
        _updateWindow.Closed += (_, _) => { _updateWindow = null; };
        _updateWindow.Show();   // <-- no PositionDialogOnCursorMonitor call
    });
}
```

This is the defect. `PositionDialogOnCursorMonitor` is NOT called before `_updateWindow.Show()`.

**Compare with all other dialogs in DialogService.cs:**
- `ShowWelcomeAsync` (line 47): calls `PositionDialogOnCursorMonitor(window)` before `ShowDialog()` -- CORRECT
- `ShowAudioDeviceAsync` (line 64): calls `PositionDialogOnCursorMonitor(window)` before `ShowDialog()` -- CORRECT
- `ShowPlatformsAsync` (line 82): calls `PositionDialogOnCursorMonitor(window)` before `ShowDialog()` -- CORRECT
- `ShowSetupWizardAsync` (line 98): calls `PositionDialogOnCursorMonitor(window)` before `ShowDialog()` -- CORRECT
- `ShowErrorAsync` (line 119): subscribes `ContentRendered` to call `PositionDialogOnCursorMonitor` (for SizeToContent windows) -- CORRECT
- `ShowUpdateProgressAsync` (line 146): MISSING `PositionDialogOnCursorMonitor` call -- DEFECT

**PositionDialogOnCursorMonitor implementation (DialogService.cs lines 156-168):**
Uses `System.Windows.Forms.Cursor.Position` to find the monitor containing the cursor, then sets `WindowStartupLocation=Manual` and `Left/Top` to center the window on that monitor's working area. Works correctly for all other dialogs.

**Additional factor:** `ShowUpdateProgressAsync` uses `Show()` (non-modal) while all other dialogs use `ShowDialog()` (modal). The `WindowStartupLocation` in UpdateProgressWindow.xaml applies at `Show()` time. Without `PositionDialogOnCursorMonitor`, the window uses whatever `WindowStartupLocation` is set in XAML (likely `CenterScreen` or `CenterOwner`). `CenterOwner` would center on the MainWindow (which is hidden at -1000,-1000) -- this could place the window off-screen or on the primary monitor. `CenterScreen` centers on the primary monitor regardless of cursor position.

## Root cause
`DialogService.ShowUpdateProgressAsync` (lines 128-148) does not call `PositionDialogOnCursorMonitor(_updateWindow)` before `_updateWindow.Show()`. Every other dialog calls this method. Stage 7.8B added `PositionDialogOnCursorMonitor` to all modal dialogs but the non-modal UpdateProgressWindow was not updated at the same time.

## Why prior work missed it
Stage 7.8B STEP 9 explicitly added cursor-following to all modal dialogs. `ShowUpdateProgressAsync` uses `Show()` (non-modal) and was either not included in the Stage 7.8B sweep, or the non-modal pattern was treated as a separate case that was not completed. The INTERRUPT #3 commit that introduced `ShowUpdateProgressAsync` predates Stage 7.8B's cursor-following work.

## Fix complexity
Trivial -- 1 line added to DialogService.cs ShowUpdateProgressAsync before `_updateWindow.Show()`.

## Recommended fix shape (NOT implemented)
In `ShowUpdateProgressAsync` (DialogService.cs ~line 145), add the positioning call:
```csharp
_updateWindow.Owner = Application.Current.MainWindow;
_updateWindow.Closed += (_, _) => { _updateWindow = null; };
PositionDialogOnCursorMonitor(_updateWindow);  // ADD THIS LINE
_updateWindow.Show();
```

Note: `PositionDialogOnCursorMonitor` sets `WindowStartupLocation=Manual` and `Left/Top` before `Show()`. Since UpdateProgressWindow has a fixed XAML Width/Height (not SizeToContent), this pattern works without ContentRendered, same as the modal dialogs.

## Verification after fix
1. Move cursor to Monitor 2 (non-primary).
2. Click "Check for updates" in tray menu.
3. Confirm UpdateProgressWindow opens on Monitor 2, centered.
4. Close window. Move cursor to Monitor 1. Repeat.
5. Confirm window follows cursor's monitor.