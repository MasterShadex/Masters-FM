# Stage 7.12 Batch A -- STEP 0: Checkpoint + Issue 6 preliminary read

## Backup checkpoint
- `_BACKUPS_2026-05-11_13-26_S7_12_BATCH_A_PRE\` created (787 files)
- HEAD at backup: `4e093cc49a6793f6d535b4a149b9e6b05ece6ad4`

## Protected-file SHA256 (all MATCH baseline)
| File | SHA256 (first 8 chars) |
|---|---|
| src\tray.ps1 | 19011F0B... |
| src\tray_native\tray_native.cs | 6B9804A1... |
| src\launcher.cs | 291ED4C9... |
| src\server.js | C15ED931... |

## Issue 6 approach decision: Approach (b)

### Why approach (a) does not work
`DialogService.ShowWelcomeAsync(bool showAboutTab = false)` already exists and sets
`vm.ShowAboutTab = showAboutTab`. However, `WelcomeWindow.xaml` has no binding to
`ShowAboutTab` and `WelcomeWindow.xaml.cs` does not reference the property. The property
is dead code -- calling `ShowWelcomeAsync(showAboutTab: true)` currently shows the
identical first-run animated welcome screen.

### Approach (b) implementation plan

**STEP 1 will change 3 files:**

1. `src/tray_csharp/Dialogs/WelcomeWindow.xaml`
   - Add `x:Name="WelcomeContentScroller"` to the existing ScrollViewer (welcome copy)
   - Add `x:Name="WelcomeActionButtons"` to the existing action buttons Border
   - Add `<Grid x:Name="PatchNotesPanel" Grid.Row="0" Visibility="Collapsed">` with
     a ListBox bound to `PatchNotes` collection

2. `src/tray_csharp/Dialogs/WelcomeWindow.xaml.cs`
   - In `OnLoaded`: if `DataContext is WelcomeViewModel vm && vm.ShowAboutTab`,
     collapse `WelcomeContentScroller` + `WelcomeActionButtons`,
     show `PatchNotesPanel`

3. `src/tray_csharp/ViewModels/TrayMenuViewModel.cs`
   - Line 446: `ShowWelcomeAsync()` -- `ShowWelcomeAsync(showAboutTab: true)`

No new files. No new converter. No new ViewModel properties.
`PatchNotes` collection already populated from embedded `patch_notes.json` (292 versions).
