# V14 Stage 7.7B FINAL -- Before/After Visual Documentation
**Date:** 2026-05-10  
**Before source:** `_BACKUPS_2026-05-09_23-36_S7_7B_PRE/screenshots_pre/`  
**After dir:** `_BACKUPS_2026-05-10_S7_7B_FINAL_PRE/screenshots_after/`  
**After screenshots:** PENDING_OPERATOR -- run `build_tools/_take_screenshots.ps1 -Name <name>` with each dialog active.

---

## Summary of Visual Transformations (Stage 7.7B)

Stage 7.7B replaced all legacy WPF-UI surfaces with the design system. The BEFORE state was rc.2
prior to any 7.7B work. The AFTER state is the full Stage 7.7B + 7.7B-FIX + 7.7B FINAL result.

---

## Surface 1: Welcome Window

**BEFORE:** `screenshots_pre/01_WelcomeWindow.png`  
**AFTER:** `screenshots_after/01_WelcomeWindow.png` (PENDING_OPERATOR)

### Transformation
- rc.2 had no WelcomeWindow (first-run dialog was a plain WPF-UI fallback).
- Now: 720x480 hero layout; left column Surface0 with brand wordmark "Master's FM"
  in BrandPurpleBase + 32-bar waveform visualization (static in ReducedMotion=true mode);
  right column with "WELCOME TO" overline, DisplayTextStyle heading, three feature bullets
  with brand icons, Get Started / Skip Setup buttons.
- Spacing audit pass (2026-05-10): overline margin corrected 6->8px; heading margin 14->16px;
  bullet icon-to-text gap corrected 10->12px.
- Escape-to-close added.

---

## Surface 2: Setup Wizard Window

**BEFORE:** `screenshots_pre/04_SetupWizardWindow.png`  
**AFTER:** `screenshots_after/02_SetupWizardWindow_step1.png` (PENDING_OPERATOR)

### Transformation
- rc.2: plain unstyled dialog.
- Now: AppDialogStyle chrome (3px BrandPurpleBase accent bar, drag-region title, close button).
  4-step wizard with animated pill breadcrumb (active = BrandPurpleBase, inactive = Surface2).
  Slide-fade transitions between steps (zeroed in ReducedMotion mode).
  Step content: Welcome intro + platform choices + audio source + confirmation.
- Escape-to-close added.

---

## Surface 3: Audio Source Window

**BEFORE:** `screenshots_pre/02_AudioDeviceWindow.png`  
**AFTER:** `screenshots_after/03_AudioDeviceWindow_wasapi.png` (PENDING_OPERATOR)

### Transformation
- rc.2: default WPF-UI device list, no tabs.
- Now: 3-tab layout (WASAPI / MME / ASIO). Each row has brand-purple left accent when
  selected (ColumnDefinition Width=3 BrandPurpleBase). Default row shows AppMenuItemStyle
  highlight. Auto-persist toast: 3s fade-in/out on selection change.
  Reset button (TertiaryButtonStyle) reverts to original device.
- Previously had hardcoded #FFFFFF on Default pill and toast text (documented as future).
- Escape-to-close added.

---

## Surface 4: Platforms (Platform Detection) Window

**BEFORE:** `screenshots_pre/03_PlatformsWindow.png`  
**AFTER:** `screenshots_after/04_PlatformsWindow_live.png` (PENDING_OPERATOR)

### Transformation
- rc.2: plain list of platforms.
- Now: two-column layout. Left: platform cards with toggle switch + detection status badge.
  Right: Now Playing card with album art (BitmapImage), artist/track TextBlocks, gradient
  overlay (BrandPurpleBase #8B5CF6 / #4C1D95 -- no token for deep purple, documented future).
  ActivePillStyle on active platforms uses #FFFFFF foreground (documented future).
- Escape-to-close added.

---

## Surface 5: Error Dialog

**BEFORE:** `screenshots_pre/05_ErrorDialogWindow.png`  
**AFTER:** `screenshots_after/05_ErrorDialogWindow.png` (PENDING_OPERATOR)

### Transformation
- rc.2: WPF-UI MessageBox look-alike.
- Now: AppDialogStyle chrome. Brand-purple "i" icon (IconInfo, BrandPurpleGlow, 40x40).
  HeadingTextStyle "Error" header. BodyTextStyle error message with TextWrapping.
  IsReadOnly TextBox for error details (MonoTextStyle inline -- documented future).
  SecondaryButtonStyle "OK" button. OnClosing guard absent (no blocking state).
- Escape-to-close added.

---

## Surface 6: Update Progress Window

**BEFORE:** `screenshots_pre/05_UpdateProgressWindow.png`  
**AFTER:** `screenshots_after/06_UpdateProgressWindow_idle.png` (PENDING_OPERATOR)

### Transformation
- rc.2: WPF-UI UpdateProgress dialog.
- Now: AppDialogStyle chrome. State-driven sections via DataTrigger on CurrentState
  (Idle/Checking/Available/Downloading/Ready/Installing/Error).
  Check icon (BrandPurpleBase, 40x40) for Idle/Ready; Info icon for Error.
  ProgressBar Height corrected 6->4px (spacing audit 2026-05-10).
  Download section Margin 0,0,0,6->0,0,0,8 (spacing audit).
  Authenticode badge for Ready state. OnClosing guard blocks Downloading/Installing.
- Escape-to-close added (respects OnClosing guard -- no-op during active download/install).

---

## Surface 7: Tray Context Menu

**BEFORE:** `screenshots_pre/` (no pre-screenshot for ContextMenu; pre-state was system default)  
**AFTER:** `screenshots_after/07_TrayContextMenu.png` (PENDING_OPERATOR)

### Transformation
- rc.2: system-default WPF ContextMenu (flat, no rounding, no brand).
- Now: AppContextMenuStyle (CornerRadius=12, Padding=4, MinWidth=220, Acrylic backdrop
  on Win11 22H2+). AppMenuItemStyle (36px rows, 3-column icon/label/check layout).
  Header item: "Master's FM" bold BrandPurpleBase + NowPlayingHeaderText subtitle
  (live track "PAO - CORE FLIP - PAO x SCULLION" confirmed in log).
  Icons: Settings/Speaker/Sparkle/Camera/Info/Reset/Close (14x14, TextSecondary, Uniform).
  Check column: BrandPurpleBase checkmark visible for toggled-ON items.
  AppMenuSeparatorStyle: 1px BorderSubtle dividers.

---

## Focus Visual Style (New -- STEPs 1-3, 2026-05-10)

**Added:** `AppFocusVisualStyle` in Theme/Inputs.xaml  
**Applied to:** PrimaryButtonStyle, SecondaryButtonStyle, TertiaryButtonStyle, IconButtonStyle,
  AppTextBoxStyle, AppComboBoxStyle, AppCheckBoxStyle, AppRadioButtonStyle

### Description
2px dashed Rectangle with BorderFocus stroke color, RadiusX/Y=8, renders in WPF adorner layer.
Appears on Tab-key navigation. Not visible in ReducedMotion screenshots (same appearance).
To observe: Tab through Audio Source dialog -- focus ring visible on each focusable element.

---

## Before/After Image Capture Instructions (PENDING_OPERATOR)

To capture AFTER screenshots, for each dialog:
1. Open the dialog via the tray context menu.
2. With the dialog as the foreground window, run:
   ```
   powershell -File "build_tools\_take_screenshots.ps1" -Name "01_WelcomeWindow"
   ```
3. Repeat for each surface with the appropriate -Name value.

Required names: 01_WelcomeWindow, 02_SetupWizardWindow_step1, 02_SetupWizardWindow_step2,
  02_SetupWizardWindow_step3, 02_SetupWizardWindow_step4, 03_AudioDeviceWindow_wasapi,
  03_AudioDeviceWindow_mme, 03_AudioDeviceWindow_asio, 03_AudioDeviceWindow_reset,
  04_PlatformsWindow_live, 04_PlatformsWindow_idle, 05_ErrorDialogWindow,
  06_UpdateProgressWindow_idle, 06_UpdateProgressWindow_checking,
  06_UpdateProgressWindow_available, 06_UpdateProgressWindow_downloading,
  06_UpdateProgressWindow_ready, 06_UpdateProgressWindow_installing,
  06_UpdateProgressWindow_error, 07_TrayContextMenu_collapsed, 07_TrayContextMenu_playing

---

## Confirmed Changes Summary (no screenshots required)

| Surface | Before | After | STEPs |
|---------|--------|-------|-------|
| All dialogs | No Escape key | Escape closes | 7.7B FINAL |
| All dialogs | No focus ring on Tab | 2px dashed BorderFocus ring | 7.7B FINAL |
| All inputs | 50% disabled opacity | 40% disabled opacity | 7.7B FINAL |
| WelcomeWindow | 6px/14px/10px margins | 8px/16px/12px margins | 7.7B FINAL |
| UpdateProgressWindow | 6px ProgressBar height | 4px ProgressBar height | 7.7B FINAL |
| ContextMenu | System default | Rounded, Acrylic, branded | 7.7B |
| WelcomeWindow | No dialog | Hero two-column | 7.7B |
| SetupWizardWindow | Plain | Pill breadcrumb + AppDialogStyle | 7.7B |
| AudioDeviceWindow | Unstyled list | Tabs + accent + toast | 7.7B |
| PlatformsWindow | Plain list | Two-column + Now Playing card | 7.7B |
| ErrorDialogWindow | MessageBox | AppDialogStyle + brand icon | 7.7B-FIX |
| UpdateProgressWindow | WPF-UI dialog | AppDialogStyle + state machine | 7.7B-FIX |
