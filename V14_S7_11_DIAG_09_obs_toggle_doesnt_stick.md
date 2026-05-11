# Diagnosis: Issue 9 -- OBS Overlay Add/Remove Doesn't Stick After Restart

## Reproduction (operator-confirmed)
Clicking the OBS overlay toggle in the tray menu shows "added" state change and logs success.
But after restarting OBS, the browser source is NOT present in the OBS scene.
Stage 7.8D (UUID-tracked reconcile) was designed to fix exactly this.

---

## Source-of-truth analysis

### Live log evidence (from `%LOCALAPPDATA%\MastersFM\overlay.log`, 2026-05-11)

The log captures the exact failure sequence during operator's real-world test:

**Step 1: Toggle ON (08:38:50):**
```
[08:38:50] set obs.intent = on; persisted
[08:38:50] [ObsState] toggle: intent off → on
[08:38:50] [ObsState] reconcile: intent=on ours=False foreign=False obs=running fileNewer=False → NotAdded
[08:38:50] [ObsState] transition: NotAdded → NotAdded
[08:38:50] [WARN] [ObsSceneFileEditor] OBS is running -- file-edit changes take effect after OBS restart
[08:38:50] Added 'Master's FM' uuid=fa103f55-95d4-4463-b9a0-21daa0ef1947 to Untitled.json
[08:38:50] set obs.tray_added_uuid = fa103f55-...; persisted
[08:38:50] [ObsState] AutoAdd succeeded: uuid=fa103f55-...
[08:38:50] reconcile: intent=on ours=True foreign=False obs=running fileNewer=True → PendingRestart
[08:38:50] transition: NotAdded → PendingRestart
```

File written successfully. State correctly transitions to PendingRestart. This is the expected behavior.

**Step 2: OBS overwrites the file (~54 seconds later, 08:39:44):**
```
[08:39:44] [ObsState] reconcile: intent=on ours=False foreign=False obs=running fileNewer=False → NotAdded
[08:39:44] transition: PendingRestart → NotAdded
[08:39:44] [WARN] OBS is running -- file-edit changes take effect after OBS restart
[08:39:44] Added 'Master's FM' uuid=cbf771d5-752c-4aeb-a36d-ffdfae41ecaa to Untitled.json
[08:39:44] set obs.tray_added_uuid = cbf771d5-...; persisted
[08:39:44] AutoAdd succeeded: uuid=cbf771d5-...
[08:39:44] reconcile: intent=on ours=True foreign=False obs=running fileNewer=True → PendingRestart
[08:39:44] transition: NotAdded → PendingRestart
```

**Critical observation:** Between 08:38:50 and 08:39:44:
- `ours` went from `True` → `False`
- `fileNewer` went from `True` → `False`

This means the scene JSON file was OVERWRITTEN between those two reconcile ticks. The reconcile re-reads the file and no longer finds uuid=fa103f55. The tray re-adds with a NEW uuid (cbf771d5).

**This cycle repeats every ~60 seconds:**
- 08:53:44: same pattern -- PendingRestart → NotAdded (OBS overwrote again) → re-added uuid=cb019bf0
- Pattern continues indefinitely while OBS is running

**No OBS restart was observed in the log.** The operator never restarted OBS during this session. So the "after restart" test (operator's issue description) wasn't captured in this log.

### Why the source is lost permanently

1. Tray writes to `Untitled.json` (adds browser source entry)
2. OBS (already running) has its in-memory scene state -- loaded BEFORE the tray's write
3. OBS auto-saves its in-memory state back to `Untitled.json` (OBS's file save interval is ~30-60s)
4. OBS's saved state does not include the browser source (it wasn't in memory)
5. The file now reflects OBS's in-memory state -- browser source gone
6. Reconcile detects: `ours=False` (uuid not found in file) → transitions back to NotAdded
7. Tray re-adds with a new UUID -- but OBS will overwrite this one too

If the operator then restarts OBS after this cycle:
- The last state of `Untitled.json` is whatever OBS last saved (which clobbered the browser source)
- OBS loads `Untitled.json` on restart -- no browser source present
- Result: the source is NOT in OBS after restart

### State machine analysis (src/tray_csharp/ViewModels/TrayMenuViewModel.cs)

The `ObsToggleState` has states: NotAdded, PendingRestart, Added.

**`PendingRestart` state intention (from Stage 7.8D):** The tray wrote to the file; waiting for OBS to restart to pick up the change. In `PendingRestart`, the reconcile is supposed to DETECT when OBS has restarted (file becomes no longer "running" state or OBS process gone) and transition to `Added`.

**Bug in PendingRestart → NotAdded transition:** The reconcile INCORRECTLY falls back to NotAdded when it sees `ours=False` + `fileNewer=False` -- even though `intent=on`. The intended behavior should be: if intent=on and OBS is running and the file doesn't have our source, maintain PendingRestart and don't re-write the file (since OBS will just overwrite it again). Instead, wait for OBS to close.

**The re-add loop creates a new UUID every time:** Each time the reconcile transitions NotAdded → re-adds, it generates a fresh UUID. This means `obs.tray_added_uuid` in config.json also changes. When OBS finally closes and reopens, the reconcile might not correctly match the right UUID since the UUID changes with every re-add attempt.

### Files read

- `%LOCALAPPDATA%\MastersFM\overlay.log` (live evidence)
- `src/tray_csharp/Services/ObsSceneFileEditor.cs` (file write implementation)
- `src/tray_csharp/ViewModels/TrayMenuViewModel.cs` (state machine)
- `src/tray_csharp/App.xaml.cs` (startup reconcile timer wiring)

---

## Root cause (confirmed from live log + source)

**OBS overwrites the scene file while running.** OBS's in-memory scene state (which predates the tray's file-edit) gets periodically saved back to the JSON file, clobbering the tray's addition. The reconcile loop detects the overwrite and re-adds, but OBS overwrites again. This creates an infinite re-add cycle.

When the operator eventually restarts OBS, OBS reads the scene file in whatever state it last saved it -- which does not include the browser source.

The fundamental design constraint is correct and already documented in the warning: "OBS is running -- file-edit changes take effect after OBS restart." But the reconcile loop doesn't honor this: it sees `ours=False` and re-adds, when it should instead WAIT for OBS to close (not re-add while OBS is running).

**Secondary bug:** Each re-add generates a new UUID. This UUID churn means `obs.tray_added_uuid` in config gets overwritten repeatedly. If the operator toggles off and then off-again after this cycle, the remove step targets the LAST uuid (not the one OBS may have been showing if somehow one was added correctly).

---

## Why Stage 7.8C/7.8D missed this

Stage 7.8C/7.8D were developed and tested with the reconcile working correctly in isolation. The PendingRestart state was designed to hold the "file written, waiting for OBS restart" state. But the state machine has a flaw: when the reconcile re-runs and finds `ours=False` (OBS overwrote), it transitions back to NotAdded and re-adds instead of staying in PendingRestart. The code falls back to AutoAdd on every reconcile when intent=on and ours=False, without checking whether OBS is currently running.

The intent was: if `obs=running` and `ours=False` and we just wrote the file → it's OK, OBS will pick it up on restart. But the transition logic gates on the file state, not on whether OBS was the one that overwrote.

---

## Fix complexity

**Medium** (~3-5 hours Ruflo):

In the reconcile state machine (`ReconcileStateAsync` in TrayMenuViewModel.cs):
1. When transitioning PendingRestart → NotAdded, check: is OBS currently running (`obs=running`)? If yes, stay in PendingRestart -- do NOT re-add. OBS is going to overwrite any new write anyway.
2. Only re-add when: `intent=on`, `ours=False`, AND `obs=NOT running` (OBS closed -- so the file-edit will survive).
3. Fix UUID churn: if we're in PendingRestart and OBS is running and our uuid is not in the file, stay in PendingRestart with the EXISTING uuid (don't generate a new one).
4. Detect OBS restart: when transitioning from obs=running to obs=stopped, trigger immediate reconcile + add. When OBS starts fresh, it loads from disk -- we need the file to be correct before OBS starts.

**The core fix in one sentence:** In ReconcileStateAsync, if `intent=on` and `obs=running` and `ours=False`, set state to PendingRestart (do not re-add, do not change UUID). Only re-add when `obs=not running`.

---

## Recommended fix shape (NOT implemented)

In `ReconcileStateAsync` (TrayMenuViewModel.cs):
```
if (intent == on AND obs == running AND ours == False):
    // OBS will overwrite our file edit; don't re-add, just wait
    state = PendingRestart  // hold in waiting state
    // do NOT call AutoAdd
else if (intent == on AND obs == NOT running AND ours == False):
    // OBS is closed -- safe to write, edit will persist when OBS next opens
    AutoAdd()
    state = Added  // no restart needed since OBS will read on next open
```

---

## Verification after fix

1. OBS is running
2. Toggle OBS ON in tray menu
3. Confirm tray shows "OBS: pending restart" state
4. Wait 2+ minutes -- confirm tray does NOT repeatedly re-add (UUID stays stable in log)
5. Close OBS
6. Wait ~10s -- confirm tray detects OBS closed and either (a) adds before next OBS open, or (b) adds correctly on OBS open
7. Restart OBS
8. Confirm the browser source "Master's FM" is visible in OBS scene collection
9. Toggle OBS OFF in tray, restart OBS, confirm source is gone
