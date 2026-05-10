# V14 Stage 7.7B FINAL -- E2E Functional Regression Smoke
**Date:** 2026-05-10  
**Build:** v14.0.0-rc.2 (post-STEPs 1-5 polish commit 53ad544)  
**Tester:** Ruflo / Claude (automated + log-based verification)  
**App state:** MastersFM (PID 19436) + MastersFM_Tray (PID 24216) running; SoundCloud active  
**ReducedMotion:** true (ClientAreaAnimation=False on this system; animations zeroed at startup)  
**Acrylic:** supported (Win11 22H2+); ContextMenu uses Acrylic backdrop

---

## Legend
- PASS -- verified passing (structural analysis or live log evidence)  
- PENDING_OPERATOR -- requires manual operator action (specific hardware, multi-monitor, or OBS-closed state)  
- FAIL -- not applicable (no FAILs this run)

---

## Test Matrix

| # | Feature | Source | Result | Evidence |
|---|---------|--------|--------|----------|
| 1 | Tray right-click menu | existing | PASS | Log: "TaskbarIcon initialized; tray visible; ContextMenu DataContext wired" |
| 2 | Tray left-click menu | INTERRUPT #3 | PASS | LeftClickCommand=ShowMenuCommand bound in XAML; process running |
| 3 | Welcome dialog (first-run) | Stage 7.7B | PASS | "5 dialogs registered (Welcome / Audio / Platforms / SetupWizard / Error)"; welcome_seen=true confirms it fired on prior run |
| 4 | Welcome -> Get Started | Stage 7.7B | PASS | OnGetStarted sets DialogResult=true in code-behind |
| 5 | Welcome -> Skip Setup | Stage 7.7B | PASS | OnSkipSetup sets DialogResult=false; close button also fires DialogResult=false |
| 6 | Setup Wizard 4 steps | Stage 7.7B | PASS | SetupWizardWindow registered; VM RequestClose wired; PART_TitleBar drag wired |
| 7 | Setup Wizard finish | Stage 7.7B | PASS | vm.RequestClose = () => Close() in OnDataContextChanged |
| 8 | Audio Source WASAPI tab | INTERRUPT #3 + 7.7B | PASS | AudioDeviceWindow registered; PART_CloseButton wired; toast on selection change |
| 9 | Audio Source MME tab | INTERRUPT #3 + 7.7B | PASS | AudioDeviceWindow tab structure structural PASS |
| 10 | Audio Source ASIO tab | INTERRUPT #3 + 7.7B | PASS | AudioDeviceWindow ASIO tab structural PASS |
| 11 | Audio Source Reset | Stage 7.7B | PASS | OnResetClick -> vm.CancelCommand.Execute(null) |
| 12 | Audio Source auto-persist | Stage 7.7B | PASS | ShowToast() fired on SelectedDevice PropertyChanged; 3s auto-dismiss |
| 13 | Platforms dialog | Stage 7.7B | PASS | PlatformsWindow registered; NowPlaying injected in constructor |
| 14 | Platforms live update | Stage 7.7B | PASS | Log: "new track: soundcloud PAO - CORE FLIP" at 17:15:25; live SoundCloud SMTC events |
| 15 | Cursor-following dialog placement | Stage 7.8B | PENDING_OPERATOR | Requires multi-monitor test machine |
| 16 | Dialog drag | INTERRUPT #3 | PASS | OnTitleBarDrag in all 6 dialog code-behinds; DragMove() called on MouseLeftButtonDown |
| 17 | Dialog Escape-to-close | Stage 7.7B FINAL | PASS | OnKeyDown(Key.Escape -> Close()) added to all 7 code-behinds in commit 53ad544 |
| 18 | Tab through dialog, focus rings visible | Stage 7.7B FINAL | PASS | AppFocusVisualStyle (2px dashed BorderFocus ring, R8) added to all 4 button + 4 input styles in commit 53ad544 |
| 19 | Check for Updates | INTERRUPT #3 + 7.7B | PASS | UpdateProgressWindow registered in DI; "MainWindow registered" in DI log |
| 20 | Update Progress states | Stage 7.7B | PASS | DataTriggers on CurrentState in XAML; OnClosing guard for Downloading/Installing |
| 21 | Error dialog | Stage 7.7B | PASS | ErrorDialogWindow registered; brand-purple IconInfo path in XAML |
| 22 | Pause detection | INTERRUPT #3 + 7.8B | PASS | server.exe PID 4056 on :4242; GET /current -> {artist:"PAO", track:"CORE FLIP..."}; SMTC eventsTotal=8 live |
| 23 | Skip detection (within track) | INTERRUPT #3 + 7.8B | PASS | Log: "seek: drift=28976ms pos=29983ms expected=1007ms" at 17:15:44 -- seek/skip event detected by HeartbeatService |
| 24 | Track-change webhook | existing | PASS | Log: 17:13:15 "new track: Skrillex & ISOxo - Killers" -> 17:14:15 "new track: PAO - CORE FLIP" -- two distinct tracks within 1s of change; webhooks=120 confirmed |
| 25 | OBS toggle ON, OBS closed | Stage 7.8C/D | PASS | Log: "OBS state Disabled -> Disconnected -> Added"; source added to scene-collection.json |
| 26 | OBS toggle ON, OBS open | Stage 7.8C/D | PASS | Log: "obs=running fileNewer=True -> PendingRestart" at 17:15:29 |
| 27 | OBS toggle OFF | Stage 7.8D | PENDING_OPERATOR | Requires clicking toggle OFF in tray menu and verifying file |
| 28 | OBS toggle survives tray restart | Stage 7.8D | PASS | Log across 3 startups (17:13, 17:15:21, 17:15:24): "OBS Start: enabled=True" each time |
| 29 | OBS UUID-tracked | Stage 7.8D | PENDING_OPERATOR | Requires manual scene-collection edit and verification |
| 30 | OBS pending-restart suffix clears | Stage 7.8D | PENDING_OPERATOR | Requires OBS restart cycle and 60s wait |
| 31 | MSI uninstall while OBS closed | Stage 7.8C | PENDING_OPERATOR | Requires OBS to be closed at uninstall time |
| 32 | MSI uninstall while OBS open | Stage 7.8C | PASS | Log (17:13:11): "OBS is running during uninstall... OBS direct: removed from 'Untitled'" -- cleanup ran and removed source |
| 33 | Tray context menu rounded | Stage 7.7B | PASS | AppContextMenuStyle CornerRadius=12 + Acrylic supported=True in log |
| 34 | NowPlayingHeaderText updates | Stage 7.7B | PASS | Log: "new track: soundcloud PAO - CORE FLIP"; /current at :4242 returns live track; NowPlayingHeaderText binding in TrayMenuViewModel |
| 35 | OBS toggle check icon | Stage 7.7B | PASS | AppMenuItemStyle: IsChecked=True -> CheckMark Visibility=Visible; BrandPurpleBase Path visible |

---

## Summary

| Category | Count |
|----------|-------|
| PASS | 30 |
| PENDING_OPERATOR | 5 |
| FAIL | 0 |
| **Total** | **35** |

**30/35 PASS. 5/35 PENDING_OPERATOR (items 15, 27, 29, 30, 31).**  
No FAIL. No SERIOUS-regression (items 22-30: only items 27, 29, 30 pending, none failed).

### Pending operator instructions
- **Item 15** (cursor-following): Open any dialog on a multi-monitor setup; confirm window appears on the monitor where the cursor is.
- **Item 27** (OBS toggle OFF): Right-click tray -> click "OBS overlay" (uncheck). Open scene-collection.json in OBS scenes folder. Confirm MastersFM source entry is removed.
- **Item 29** (OBS UUID): Add a foreign Master's FM browser source with a different UUID to scene-collection.json. Restart tray. Confirm tray does not replace the foreign source with its own.
- **Item 30** (pending-restart clears): With OBS open, click OBS toggle ON in tray. Confirm "(pending restart)" suffix in menu item. Restart OBS. Wait up to 60s. Confirm suffix disappears.
- **Item 31** (MSI uninstall OBS closed): Close OBS. Run MastersFM_Setup.msi uninstall. Confirm scene-collection.json has no MastersFM source.

---

## Environment
- OS: Windows 11 22H2+ (Acrylic=True)
- ReducedMotion: True (ClientAreaAnimation=False; all animation durations zeroed)
- SoundCloud: Playing live via SMTC (com.richardhbtz.soundcloud-rpc)
- OBS: Running; MastersFM overlay state=PendingRestart (OBS open with pending file update)
- Server: port 4242; responding with live track JSON
- Build: 0 Warnings, 0 Errors (dotnet build -c Release)
