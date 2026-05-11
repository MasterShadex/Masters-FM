# Diagnosis: Issue 6 -- Patch Notes menu item opens the wrong window

## Reproduction (operator-confirmed)
Clicking "Patch notes" in tray menu opens the Welcome/Setup Wizard window instead of a dedicated patch notes view.

## Source-of-truth analysis

**MainWindow.xaml lines 135-145** -- Patch notes MenuItem wiring:
```xml
<MenuItem Header="Patch notes"
          Style="{StaticResource AppMenuItemStyle}"
          Command="{Binding OpenPatchNotesCommand}">
    <MenuItem.Icon>
        <Path Data="{StaticResource IconInfo}" .../>
    </MenuItem.Icon>
</MenuItem>
```
Command binding: `OpenPatchNotesCommand`.

**TrayMenuViewModel.cs lines 443-448** -- command implementation:
```csharp
[RelayCommand]
private async Task OpenPatchNotesAsync()
{
    _logger.Log("TrayMenu: Patch notes", "Tray");
    try { await _dialogService.ShowWelcomeAsync(); }
    catch (Exception ex) { _logger.LogErr("ShowWelcomeAsync", ex, "Tray"); }
}
```
`OpenPatchNotesAsync` calls `ShowWelcomeAsync()` -- this opens `WelcomeWindow`.

**DialogService.cs lines 37-51** -- ShowWelcomeAsync:
```csharp
public Task ShowWelcomeAsync(bool showAboutTab = false)
{
    return ShowOnDispatcherAsync(() =>
    {
        var window = _services.GetRequiredService<WelcomeWindow>();
        var vm = _services.GetRequiredService<WelcomeViewModel>();
        vm.ShowAboutTab = showAboutTab;
        window.DataContext = vm;
        window.Owner = Application.Current.MainWindow;
        PositionDialogOnCursorMonitor(window);
        window.ShowDialog();
    });
}
```

`WelcomeWindow` is the first-run onboarding screen (animated waveform, "Welcome to Master's FM" heading). Stage 7.7B describes it as a "4-step breadcrumbed flow" -- this is why the operator identifies it as "Setup Wizard". The WelcomeWindow is visually indistinguishable from a setup wizard.

There is no separate `ShowPatchNotesAsync` method in DialogService or IDialogService. A dedicated patch-notes-only presentation does not exist. `WelcomeWindow` was designed to double as both first-run onboarding AND the "what's new" destination, but the window's presentation (animated waveform hero, wizard-like step flow) does not match what a user expects when clicking "Patch notes".

`ShowWelcomeAsync` accepts `showAboutTab = false` by default. WelcomeViewModel exposes a `ShowAboutTab` property -- this hints that the Welcome window has an About tab that could serve as a more appropriate "patch notes" destination, but this is not surfaced via the current "Patch notes" command.

## Root cause
`OpenPatchNotesAsync` (TrayMenuViewModel.cs line 446) calls `ShowWelcomeAsync()` which opens the WelcomeWindow. The WelcomeWindow is a first-run wizard (animated hero screen + step flow) that the operator correctly identifies as visually indistinguishable from the Setup Wizard. There is no dedicated patch notes dialog. The intent was for the WelcomeWindow to serve dual purpose, but the presentation is wrong for a returning user accessing "Patch notes".

## Why prior work missed it
Stage 7.7B designed WelcomeWindow as dual-purpose. The briefs did not include a separate "Patch Notes" dialog as it was assumed the Welcome window would serve this need. The visual mismatch (onboarding wizard vs. patch notes) was not flagged until operator real-world testing.

## Fix complexity
Small -- two possible approaches:
(a) Add an `isPatchNotesMode` flag to WelcomeViewModel that suppresses the animated hero and step flow, showing only the change list directly. ~20-30 lines.
(b) Pass `showAboutTab: true` to ShowWelcomeAsync (1-line fix in TrayMenuViewModel) if the About tab contains the patch notes content -- this requires verifying WelcomeViewModel.ShowAboutTab behavior.
(c) Create a dedicated patch notes dialog (new Window + ViewModel). Large, out of scope for a trivial fix.

Approach (b) is the minimal 1-line fix if WelcomeViewModel.ShowAboutTab = true renders the patch notes content without the wizard flow. Read WelcomeViewModel.cs to confirm before implementing.

## Recommended fix shape (NOT implemented)
1. Read WelcomeViewModel.cs to confirm what ShowAboutTab=true renders.
2. If ShowAboutTab=true shows a changelog/patch-notes view (not the wizard hero): change TrayMenuViewModel.cs line 446 from `ShowWelcomeAsync()` to `ShowWelcomeAsync(showAboutTab: true)`.
3. If ShowAboutTab=true is not a patch notes view: implement approach (a) -- a flag that shows the WelcomeWindow in "patch notes" mode (skips hero, shows change list).

## Verification after fix
1. Open tray menu.
2. Click "Patch notes".
3. Confirm a change-notes / what's new view opens (NOT the animated welcome hero + wizard steps).
4. Confirm it positions on the cursor's monitor.