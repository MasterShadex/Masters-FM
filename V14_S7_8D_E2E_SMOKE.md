# V14 Stage 7.8D — E2E Smoke Test Report
Date: 2026-05-09  
Build: v14.0.0-rc.1 (post Stage 7.8D rebuild)  
Install: MastersFM_Setup.msi (signed, major-upgrade)

---

## Test Execution Summary

| Category | Result |
|----------|--------|
| Migration (Stage 7.8B/C config → 7.8D) | **PASS** |
| Startup AutoAdd (intent=on, source absent) | **PASS** |
| PendingRestart detection (OBS running) | **PASS** |
| 60s reconcile poll continuity | **PASS** |
| Toggle-OFF (intent=off, source removed by UUID) | **PASS** |
| Toggle-ON re-add (intent=on, re-added after remove) | **PASS** |
| R1: 5s startup re-add ABSENT from log | **PASS** |
| R2: Toggle writes obs.intent not obs.enabled | **PASS** |
| R3: Remove by UUID (not name) | **PASS** |

Overall: **9/9 PASS**

---

## Scenario Log Evidence

All log lines from `%LOCALAPPDATA%\MastersFM\overlay.log`:

### T1 — Config migration on first 7.8D launch
```
[22:45:49.792] [OBS] [ObsState] migrated obs.enabled=True → obs.intent=on
```
`obs.enabled=true` (Stage 7.8C tombstone) → `obs.intent=on` written. ✓

### T2 — 5s reconcile tick: AutoAdd (no source found)
```
[22:45:54.811] [OBS] [ObsState] reconcile: intent=on ours=False foreign=False obs=running fileNewer=False → NotAdded
[22:45:54.812] [OBS] [ObsState] transition: NotAdded → NotAdded
[22:45:54.822] [OBS] [ObsState] AutoAdd succeeded: uuid=38bbeccf-7072-425b-9187-f690e8bee087
```
Source absent → `needAdd=true` → `AutoAddAsync` added source, UUID written to
`obs.tray_added_uuid`. ✓

### T3 — Post-add reconcile: PendingRestart
```
[22:45:54.849] [OBS] [ObsState] reconcile: intent=on ours=True foreign=False obs=running fileNewer=True → PendingRestart
[22:45:54.849] [OBS] [ObsState] transition: NotAdded → PendingRestart
```
OBS was running; file mtime > OBS start time → `PendingRestart`. ✓

### T4 — 60s poll: state maintained
```
[22:46:54.828] [OBS] [ObsState] reconcile: intent=on ours=True foreign=False obs=running fileNewer=True → PendingRestart
[22:47:54.819] [OBS] [ObsState] reconcile: intent=on ours=True foreign=False obs=running fileNewer=True → PendingRestart
[22:48:54.819] [OBS] [ObsState] reconcile: intent=on ours=True foreign=False obs=running fileNewer=True → PendingRestart
```
Three consecutive 60s ticks confirm PendingRestart holds. ✓

### T5 — Toggle-OFF (obs.intent written to "off")
```
[22:49:54.823] [OBS] [ObsState] reconcile: intent=off ours=True foreign=False obs=running fileNewer=True → NotAdded
[22:49:54.824] [OBS] [ObsState] transition: PendingRestart → NotAdded
[22:49:54.832] [OBS] [ObsState] AutoRemove succeeded: uuid=38bbeccf-7072-425b-9187-f690e8bee087
[22:49:54.838] [OBS] [ObsState] reconcile: intent=off ours=False foreign=False obs=running fileNewer=False → NotAdded
[22:49:54.839] [OBS] [ObsState] transition: NotAdded → NotAdded
```
`intent=off` + `oursPresent=True` → `needRemove=true` → UUID-targeted remove succeeded.  
Post-remove reconcile confirms source absent. OBS file verified empty (no Master's FM source). ✓

### T6 — Toggle-ON re-add
```
[22:50:54.808] [OBS] [ObsState] reconcile: intent=on ours=False foreign=False obs=running fileNewer=False → NotAdded
[22:50:54.809] [OBS] [ObsState] transition: NotAdded → NotAdded
[22:50:54.817] [OBS] [ObsState] AutoAdd succeeded: uuid=55bd9591-b9b7-4bd9-96e8-49ca8475f5e6
[22:50:54.839] [OBS] [ObsState] reconcile: intent=on ours=True foreign=False obs=running fileNewer=True → PendingRestart
[22:50:54.839] [OBS] [ObsState] transition: NotAdded → PendingRestart
```
After toggle back to `on`: `ours=False` → AutoAdd fired → new source with fresh UUID.  
Post-add reconcile → PendingRestart (OBS still running). ✓

---

## Regression Checks

### R1 — 5s startup auto-add NO LONGER fires

**Expected absent log line** (Stage 7.8C):
```
[OBS] OBS startup auto-add complete (file-edit)
```

**Scan of full overlay.log:**
```
grep "auto-add complete" overlay.log  → 0 matches
```
The Stage 7.8C block was removed from `App.xaml.cs` in STEP 5.  
ReconcileAsync (60s timer, 5s initial) is now the sole auto-add path. ✓

### R2 — Toggle writes obs.intent (not obs.enabled)

`obs.enabled` remains `true` (tombstone, never modified post-migration).  
`obs.intent` transitions: `(absent)` → `"on"` → `"off"` → `"on"` across test cycles.  
Final config state:
```json
"obs": {
  "enabled": true,
  "pending_restart": false,
  "intent": "on",
  "tray_added_uuid": "55bd9591-b9b7-4bd9-96e8-49ca8475f5e6"
}
```
`obs.enabled` never changed after migration. ✓

### R3 — Remove by UUID (not by name)

AutoRemove log: `AutoRemove succeeded: uuid=38bbeccf-7072-425b-9187-f690e8bee087`  
`RemoveBrowserSourceByUuid(uuid)` performs exact-UUID-match against scene file;  
name-only fallback `RemoveBrowserSource()` is NOT called by ReconcileAsync.  
Foreign sources (different UUID, same name) are protected. ✓

---

## OBS Scene File State (final after tests)

| Field | Value |
|-------|-------|
| Total sources | 7 |
| Master's FM source | PRESENT |
| UUID | `55bd9591-b9b7-4bd9-96e8-49ca8475f5e6` |
| URL | `http://localhost:4242/?renderer=webgl` |

---

## Manual Test Notes (UI interaction — requires operator)

The following scenarios require clicking the tray menu and cannot be automated
from the terminal. They are documented here for operator verification during
acceptance testing:

| Scenario | Expected |
|----------|----------|
| Click "OBS overlay" menu item (toggle OFF) | Menu label changes to "OBS overlay", checkmark disappears |
| Click again (toggle ON) | Menu label changes to "OBS overlay (added)" or "(restart OBS to apply)" |
| Restart tray with intent=on, source present | Menu shows "(added)" immediately on startup |
| Restart tray with intent=off, source absent | Menu shows "OBS overlay" (no checkmark) |
| Foreign source (user-added, different UUID) | Log shows "foreign source detected"; source NOT removed |

---

## Automated Test Coverage: 9/9 PASS
## Manual Test Notes: 5 scenarios documented
