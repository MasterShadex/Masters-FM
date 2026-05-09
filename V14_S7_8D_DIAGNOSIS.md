# V14 Stage 7.8D — Bug Diagnosis
Date: 2026-05-09

## Operator-Reported Symptom

> "Toggled OFF in tray. Menu still showed '(added)'. Source did NOT get removed from
> scene-collection.json. Persisted across tray restart."

---

## Pre-Diagnosis State (captured at STEP 0 backup)

**config.json (`%APPDATA%\MastersFM\config.json`) before Stage 7.8D:**
```json
"obs": {
    "enabled": true,
    "pending_restart": false
}
```
No `obs.intent` field. No `obs.tray_added_uuid` field.

**OBS scene file (Untitled.json) before Stage 7.8D:**
- "Master's FM" source PRESENT
- UUID: `adb8bfa8-8570-48ce-ace4-d1bb68f090c1`
- URL: `http://localhost:4242/?renderer=webgl`

---

## Code Trace — Toggle OFF Path (Stage 7.8C)

### Step 1: TrayMenuViewModel constructor (startup)

```csharp
var initEditor = new ObsSceneFileEditor(_logger);
_obsToggleState = initEditor.BrowserSourceExists()
    ? ObsToggleState.Added
    : ObsToggleState.NotAdded;
```

Source IS in file → `_obsToggleState = Added`. Menu shows "(added)". ✓

### Step 2: ToggleObsAsync — remove branch executes

```csharp
if (_obsToggleState == ObsToggleState.Added)   // ← true; branch executes
{
    var removed = editor.RemoveBrowserSource(); // ← removes source by name
    if (removed)
    {
        SetObsToggleState(ObsToggleState.NotAdded); // ← state + menu updated
    }
    // BUG SECONDARY: if removed == false, state never updates (mode B potential)
}
```

Assuming `removed == true`: state → NotAdded, menu → "OBS overlay" (no checkmark). ✓

**CRITICAL: What is NOT written at this point:**
- `obs.enabled` — **never modified by ToggleObsAsync or SetObsToggleState**
- `obs.enabled` stays `true` in config.json after the toggle off

### Step 3: SetObsToggleState — what it writes

```csharp
private void SetObsToggleState(ObsToggleState state)
{
    _obsToggleState = state;
    _configService.SetValue("obs.pending_restart", state == ObsToggleState.PendingRestart);
    UpdateObsMenuFromToggleState();
}
```

Writes `obs.pending_restart = false`. Does NOT write `obs.enabled`. ✓ config now has:
```json
"obs": { "enabled": true, "pending_restart": false }
```

### Step 4: 5 seconds later — App.xaml.cs auto-add timer fires

```csharp
var obsEnabledForAdd = _configService?.GetValue<bool>("obs.enabled", false) ?? false;
// ↑ reads obs.enabled → TRUE (never changed by toggle)
var autoAddEnabled = _configService?.GetValue<bool>("obs.auto_add", true) ?? true;
// ↑ obs.auto_add not set → defaults TRUE

if (obsEnabledForAdd && autoAddEnabled)   // ← ALWAYS TRUE
{
    _ = Task.Delay(5000).ContinueWith(_ =>
    {
        var editor = new ObsSceneFileEditor(logForAutoAdd);
        editor.AddBrowserSource(url, 1000, 200, 60, css);  // ← RE-ADDS the source
    });
}
```

Source is re-added to the scene file 5 seconds after every tray startup.
UI is NOT updated (no reconciliation runs; 60s poll timer no-ops because state = NotAdded).

### Step 5: Next tray restart — constructor re-reads file

```csharp
_obsToggleState = initEditor.BrowserSourceExists() // ← source is back → TRUE
    ? ObsToggleState.Added
    : ObsToggleState.NotAdded;
```

Menu shows "(added)" — looks as though the toggle never happened.

---

## Failure Mode Analysis

| Mode | Description | Status |
|------|-------------|--------|
| **A** | `_obsToggleState` was not `Added` when toggle clicked | **NOT the issue.** Constructor correctly initializes to Added when source exists in file. |
| **B** | `RemoveBrowserSource()` returned false silently | **POSSIBLE but secondary.** Name match "Master's FM" should work; Stage 7.8C name-only search has no UUID guard but should find the source by name reliably. |
| **C** | UI bindings (`IsChecked={Binding IsObsEnabled, Mode=OneWay}`) didn't update | **NOT the issue.** `[ObservableProperty] private bool _isObsEnabled` generates `OnPropertyChanged` correctly. `UpdateObsMenuFromToggleState()` sets `IsObsEnabled = false` for NotAdded. |
| **D** | 5s startup auto-add re-added the source on tray restart | **CONFIRMED PRIMARY.** `obs.enabled` is never written to false by ToggleObsAsync; auto-add fires on every startup. |

---

## Root Cause Summary

**Three overlapping defects, all closed by Stage 7.8D design:**

### RCA-1: `obs.enabled` config field conflates intent with reality (PRIMARY)
`obs.enabled` was written `true` by Stage 7.8B's `ObsService.ConnectAsync()`. Stage 7.8C's
`ToggleObsAsync()` removed the `ConnectAsync` call but did NOT add a write of `obs.enabled`.
Result: `obs.enabled` is permanently `true` once set and never reflects toggle-OFF.

**Stage 7.8D fix:** Replace `obs.enabled` with `obs.intent` ("on"|"off"). Every toggle writes
`obs.intent`. Migration: if `obs.enabled == true` → write `obs.intent = "on"`.

### RCA-2: 5s startup auto-add bypasses toggle intent
`App.xaml.cs` reads `obs.enabled` (always `true`) and fires `AddBrowserSource()` 5s after
every tray startup regardless of whether the user toggled off.

**Stage 7.8D fix:** Remove the 5s direct-add from App.xaml.cs. Replace with reconciliation
timer that reads `obs.intent` and auto-adds ONLY when intent == "on" and source is absent.

### RCA-3: No ground-truth reconciliation
After auto-add fires (Step 4), the ViewModel's `_obsToggleState` stays `NotAdded` — there is
no mechanism to detect that the scene file changed. The 60s poll timer no-ops on NotAdded.
On restart, the constructor reads the file and picks up the stale state.

**Stage 7.8D fix:** `ReconcileStateAsync()` runs every 60s and reads both `obs.intent` AND
the current scene file state to derive the correct `ObsToggleState`. Single source of truth.

---

## How Stage 7.8D Closes Each Mode

| Mode | Closure |
|------|---------|
| A | Not needed (wasn't broken), but ReconcileStateAsync ensures init state is always correct |
| B | `RemoveBrowserSourceByUuid(uuid)` — UUID-targeted remove eliminates name-only ambiguity |
| C | `OnPropertyChanged(nameof(IsObsEnabled))` explicitly fired in `SetObsToggleState` |
| D (PRIMARY) | `obs.intent` written on every toggle; 5s auto-add replaced by reconcile timer that reads intent |
