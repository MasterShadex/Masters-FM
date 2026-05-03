# V112X_WEBHOOK_TEST_RESULT.md

**Date:** 2026-05-03  
**Test:** Disabled all 5 `Send-WebhookAsync` call sites in tray.ps1 — no webhooks sent to server.exe on any event (new-track, heartbeat, source-closed, art updates)

---

## User's Observation

**"FPS drop is the same"**

---

## Interpretation

**Hypothesis REFUTED.** `Send-WebhookAsync` is not the cause of the track-change FPS drop.

This eliminates the entire tray→server HTTP path from suspicion. Even with zero network activity on track change, the lag persists unchanged.

---

## What Has Now Been Ruled Out

Combining this test with the prior instrumentation run (V112X_LAG_DIAGNOSIS.md):

| Candidate | Status | Evidence |
|-----------|--------|---------|
| Tick handler work (any op >200ms) | **ELIMINATED** | avg tick=1-3ms, max=18ms, SLOW TICK=0 |
| GetPlaybackInfo ALPC block | **ELIMINATED** | Always cached, never logged at 1ms threshold |
| GetSessions ALPC block | **ELIMINATED** | 3ms on cache miss |
| GetTimelineProperties ALPC block | **ELIMINATED** | Always cached |
| GetAllVisibleTitles (EnumWindows) | **ELIMINATED** | 2ms |
| Send-WebhookAsync / HTTP to server | **ELIMINATED** | This test — same lag with webhooks disabled |
| Disk I/O / Defender | **ELIMINATED** | DiskQ≈0 during track changes |
| CPU contention | **ELIMINATED** | ProcQ=0, tray CPU flat |
| Invoke-DeferredThumbExtraction | **ELIMINATED** | Non-blocking state machine confirmed |
| Discord RPC / server.exe | **PREVIOUSLY ELIMINATED** | User tested: closing both → lag still happens |
| OBS | **PREVIOUSLY ELIMINATED** | User tested: closing OBS → lag still happens |

---

## Remaining Suspects

Everything measurable inside the tray process has been eliminated. The lag is caused by something **outside tray.ps1's own execution** that is triggered by the tray's presence.

**Likely culprit: `MastersFM.exe` (the launcher / Electron/WebGL process)**

When a track changes, the tray sends a webhook to server.exe — but the TRAY is now confirmed not to be the cause. However, the user test of "only MastersFM_Tray.exe running" may have included `MastersFM.exe` still running (the companion overlay process). If `MastersFM.exe` receives the server event (via SSE) and does something GPU/CPU-intensive (WebGL re-render, canvas update, animation kick), that would cause exactly the observed FPS drop.

**The test to run next:** Fully exit `MastersFM.exe` (the overlay window) while keeping `MastersFM_Tray.exe` running. If the tray runs WITHOUT the overlay, does the FPS drop disappear? This distinguishes "tray causes it" from "overlay causes it."

Alternatively: if closing all of Master's FM (tray + overlay + server) eliminates lag, but closing just server and overlay while keeping only the tray still lags — then it really is the tray. If closing the overlay alone eliminates it, the overlay is the cause.

**Second suspect: `soundcloud-rpc` itself**

The tray only detects the track change — it doesn't initiate it. soundcloud-rpc's SMTC session update (the actual event that causes the track change notification) could be causing the lag independently of anything the tray does. Testing: close only the tray while keeping soundcloud-rpc running and skip a track. If no lag → tray is the cause. If lag persists → soundcloud-rpc is the cause.

---

## State at End of Run

- Source: clean v11.2.3 (`git diff src/tray.ps1` → empty)
- Installed: clean v11.2.3 (zero TEST_DISABLED_WEBHOOK matches)
- Tray: running at PID=20232, v11.2.3, normal operation restored
- OBS overlay: back to receiving track change updates normally

---

## Sworn Statement

- Only 5 temporary `# TEST_DISABLED_WEBHOOK` comment changes were made to src/tray.ps1
- All reverted; git diff confirms zero net change
- No version bump, no commit, no push
- No memory.md edits during run
- All five protected files untouched

---

**WEBHOOK HYPOTHESIS TEST COMPLETE — result: refuted. Send-WebhookAsync is not the cause. Remaining suspect is MastersFM.exe overlay process or soundcloud-rpc itself. User to decide on next diagnostic step.**
