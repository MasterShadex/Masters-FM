# V14_S7_8B_E2E_SMOKE.md

Stage 7.8B — End-to-End Functional Smoke Test Matrix
Date: 2026-05-09
Brief: CLAUDE_CODE_INSTRUCTIONS.md (Stage 7.8B STEP 11)

---

## Test Matrix (14 items)

| # | Test item | Category | Result | Notes |
|---|---|---|---|---|
| 1 | Tray icon visible | Regression | ANALYTICAL PASS | Tray icon wiring unchanged; no modifications to MainWindow, TrayIconViewModel, or App.xaml.cs startup path |
| 2 | Right-click + left-click both open menu | Regression | ANALYTICAL PASS | TrayMenuViewModel + H.NotifyIcon wiring unchanged since INTERRUPT #3 fix |
| 3 | Each of 5 dialogs opens on cursor's monitor | Issue 4 | PENDING: runtime | PositionDialogOnCursorMonitor() wired in all 5 ShowXxx methods; requires operator to move cursor to non-primary monitor then open each dialog |
| 4 | Each of 5 dialogs draggable by title bar | Regression | ANALYTICAL PASS | XAML dialog windows unchanged; no WindowStyle or IsHitTestVisible modifications |
| 5 | Audio Source has WASAPI / MME / ASIO tabs | Regression | ANALYTICAL PASS | AudioDeviceWindow + AudioDeviceViewModel unchanged; only ShowAudioDeviceAsync wiring updated (added PositionDialogOnCursorMonitor before ShowDialog) |
| 6 | Check for Updates opens UpdateProgressWindow | Regression | ANALYTICAL PASS | ShowUpdateProgressAsync unchanged; UpdateProgressWindow + IUpdateCheckService unmodified |
| 7 | Toggle OBS ON via tray menu → browser source appears in OBS within 2-3s | Issue 9 PRIMARY | PENDING: runtime | WebSocket primary path implemented (STEP 3); file-edit fallback (STEP 4); ConnectAsync + WaitForObsConnectedAsync + AddBrowserSourceAsync wired (STEP 5); requires OBS with WebSocket server enabled (Mode A) |
| 8 | Toggle OBS OFF via tray menu → browser source disappears from OBS | Issue 9 PRIMARY | PENDING: runtime | RemoveBrowserSourceAsync + DisconnectAsync wired in ToggleObsAsync (STEP 5); requires OBS with WebSocket server enabled |
| 9 | Skip a track → overlay updates within ≤1s | Latency | ANALYTICAL PASS | SmtcEventBridge DrainCadenceMs 250ms→100ms; worst-case detection 100ms, avg 50ms — confirmed in V14_S7_8B_LATENCY_POSTFIX.md; runtime spot-check recommended |
| 10 | Pause a track → overlay reflects within ≤1s | Latency | ANALYTICAL PASS | HeartbeatService IntervalSeconds 2.0→1.0; worst-case 1000ms, avg 500ms — confirmed in V14_S7_8B_LATENCY_POSTFIX.md; runtime spot-check recommended |
| 11 | Scrub forward 30s → overlay reflects within ≤1s | Latency | ANALYTICAL PASS | Seek detection via HeartbeatService drift check (SeekThresholdMs=3000ms); heartbeat cadence 1.0s; worst-case 1000ms — confirmed in V14_S7_8B_LATENCY_POSTFIX.md |
| 12 | Restart tray → OBS browser source auto-added 5s after startup | Auto-add | PENDING: runtime | 5s Task.Delay timer wired in App.xaml.cs OnStartup (STEP 5); gated on obsService.IsEnabled + obs.auto_add config key; requires Mode A OBS |
| 13 | Restart tray with OBS source already added → no duplicate created | Idempotent | ANALYTICAL PASS | ObsSceneFileEditor.AddBrowserSource() checks for existing source by name "Master's FM" before inserting; OBS WebSocket path uses GetSceneItemId to detect existence |
| 14 | Quit tray cleanly → process tree teardown OK | Regression | ANALYTICAL PASS | App.OnExit unchanged; HeartbeatService.Stop() + ObsService.DisconnectAsync path unchanged; no new long-lived tasks that could block shutdown |

---

## Summary

| Category | Items | PASS | PENDING runtime |
|---|---|---|---|
| Regression | 1, 2, 4, 5, 6, 13, 14 | 7 ANALYTICAL PASS | 0 |
| Issue 9 (OBS) | 7, 8, 12 | 0 | 3 PENDING |
| Issue 4 (cursor-following) | 3 | 0 | 1 PENDING |
| Latency | 9, 10, 11 | 3 ANALYTICAL PASS | 0 |
| **Total** | **14** | **10 ANALYTICAL PASS** | **4 PENDING runtime** |

---

## Analytical basis

- Build: 0 warnings, 0 errors (Release + Debug, STEP 10)
- Protected file SHA256: 4 source files UNCHANGED (STEP 10)
- Latency targets analytically met per V14_S7_8B_LATENCY_POSTFIX.md:
  - Track-change worst-case: 250ms → 100ms (−150ms)
  - Pause/seek worst-case: 2000ms → 1000ms (−1000ms)
  - Art-after-text lag: 200-500ms → ~0ms

## Pending runtime items

Items 3, 7, 8, 12 require operator verification:

- **Item 3**: Move cursor to secondary monitor; right-click tray → open Welcome, Audio Device, Platforms, SetupWizard, ErrorDialog. Each should open centred on the monitor containing the cursor.
- **Items 7+8**: Ensure OBS is running with WebSocket server enabled (Tools → obs-websocket Settings → Enable). Right-click tray → OBS → Toggle ON. Verify "Master's FM" browser source (1000×200) appears within 2-3s. Toggle OFF, verify removal.
- **Item 12**: Quit tray. Relaunch. Confirm browser source auto-added in OBS after ~5s.

No strikes consumed. 0 failures detected (runtime items PENDING, not FAIL).

---

## Verdict

**PASS** (10 items analytically verified; 4 items PENDING operator runtime verification before rc.3 tag).
