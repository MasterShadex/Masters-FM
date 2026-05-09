# V14_S7_8C_DIAGNOSIS.md

Stage 7.8C STEP 1: file-edit semantics confirmation + ObsSceneFileEditor reuse plan
Date: 2026-05-09
Brief: CLAUDE_CODE_INSTRUCTIONS.md (Stage 7.8C STEP 1)

---

## 1. Audit C Section 3 Re-read Summary

**Scene-collection path:** `%APPDATA%\obs-studio\basic\scenes\*.json`

**v12 behavior (tray.ps1 Direct path):**
- `Add-OBSBrowserSourceDirect` (tray.ps1:3805-4027) runs regardless of OBS state (no enforcement)
- OBS open: edits JSON + starts `Start-OBSExitWatcher` (polls 2s, re-applies 1.5s after exit)
- OBS closed: edit takes effect on next OBS open
- Idempotent: `Test-OBSBrowserSourceExists` checks `sources[]` + `scene_items[]` before write
- UUID: `[Guid]::NewGuid().ToString()` (lowercase, no braces) -- tray.ps1:3919

**Stage 7.8C architectural decision:** Always file-edit, regardless of OBS running state. Source appears next OBS launch. Matches v12 Direct path behavior per Audit C Section 3.

---

## 2. ObsSceneFileEditor.cs Confirmation

File: `src/tray_csharp/Services/ObsSceneFileEditor.cs` (288 lines, Stage 7.8B STEP 4)

### 2.1 AddBrowserSource() idempotency

**Lines 119-128** -- check for existing source before write:
```csharp
// Idempotent check: skip if Master's FM source already exists
var sources = root["sources"]?.AsArray();
if (sources != null)
{
    foreach (var src in sources)
    {
        if (src?["name"]?.GetValue<string>() == "Master's FM")
            return false; // <-- no-op
    }
}
```
**Confirmed idempotent at source level** -- scans `sources[]` for "Master's FM" by name; returns false (no-op) if found. Prevents duplicate sources.

### 2.2 RemoveBrowserSource() completeness

**Lines 238-285** -- removes source + cascades to scene_items:
```csharp
// Remove source from sources[] (lines 252-260)
for (int i = sources.Count - 1; i >= 0; i--)
{
    if (sources[i]?["name"]?.GetValue<string>() == "Master's FM")
    {
        sourceUuid = sources[i]?["uuid"]?.GetValue<string>();
        sources.RemoveAt(i);
        modified = true;
    }
}

// Remove scene_items referencing the source by UUID (lines 264-278)
if (sourceUuid != null)
{
    foreach (var src in sources)
    {
        if (src?["id"]?.GetValue<string>() != "scene") continue;
        var items = src["settings"]?["items"]?.AsArray();
        ...
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i]?["source_uuid"]?.GetValue<string>() == sourceUuid)
            {
                items.RemoveAt(i);
                modified = true;
            }
        }
    }
}
```
**Confirmed complete** -- removes from `sources[]` AND cascades to `scene_items[]` by UUID. Handles all scenes in the collection. Called for every `*.json` in scenes dir (via `GetSceneCollectionPaths`).

### 2.3 UTF-8 no-BOM write

**Line 18:**
```csharp
private static readonly UTF8Encoding NoBomUtf8 = new(false);
```
**Confirmed** -- `new UTF8Encoding(false)` = no BOM. Matches OBS native format and tray.ps1 write behavior.

---

## 3. Gaps That Stage 7.8C Must Fill

### GAP 1: URL-update-on-mismatch NOT implemented

**Brief requirement (Architectural Decisions section):**
> Idempotency: before write, scan `sources[]` for existing entry with `name == "Master's FM"`. If found, no-op (**UPDATE the URL field if it differs from the canonical `http://localhost:4242/?renderer=webgl`**, but never duplicate).

**Current behavior (lines 125-127):**
```csharp
if (src?["name"]?.GetValue<string>() == "Master's FM")
    return false; // <-- returns WITHOUT checking URL
```
The current code returns `false` immediately without inspecting the existing source's URL. A user who installed a prior version with a different URL (e.g., from a dev build that used port 4040) would keep the wrong URL until they manually toggle OFF + ON.

**Fix required in STEP 2:** When existing source found, compare `settings.url` to the canonical URL. If mismatched, update the URL in-place and write back. Preserve the source's existing UUID (critical for prior-install compatibility).

### GAP 2: Write-back parse validation NOT implemented

**Brief Safety Floor S5:**
> Validate output by re-parsing before writing. Halt on any parse failure during write-back.

**Current behavior (line 234):**
```csharp
File.WriteAllText(path, root.ToJsonString(WriteOpts), NoBomUtf8);
// No parse-back validation
```
Same gap in `RemoveFromCollection` (line 283).

**Fix required in STEP 2:** Add `_ = JsonNode.Parse(output) ?? throw new InvalidOperationException(...)` between `ToJsonString` and `WriteAllText`. This catches any unexpected serialization failure before committing to disk.

### GAP 3: No `obs.pending_restart` state persistence

**Brief requirement (STEP 3):** `ObsToggleState` enum (NotAdded/Added/PendingRestart) must persist to config field `obs.pending_restart` across tray restarts.

**Current state:** ObsSceneFileEditor has no config interaction. State management is in TrayMenuViewModel (Stage 7.8B), which does not have the pending-restart concept.

**Fix required in STEP 2+3:** New `ObsToggleState` enum + `IConfigService` persistence of `obs.pending_restart`.

---

## 4. Reuse Plan

| Stage 7.8C action | Implementation |
|---|---|
| Startup auto-add | REUSE `ObsSceneFileEditor.AddBrowserSource()` directly (no changes needed) |
| Tray toggle ON | REUSE `ObsSceneFileEditor.AddBrowserSource()` + fix GAP 1 (URL-update-on-mismatch) |
| Tray toggle OFF | REUSE `ObsSceneFileEditor.RemoveBrowserSource()` + add toast + PendingRestart state |
| MSI uninstall cleanup binary | Port `RemoveFromCollection` logic to `MastersFM_ObsCleanup/Program.cs` (standalone, no tray_csharp dependency) |
| Write-back safety | Fix GAP 2 in both `AddToCollection` and `RemoveFromCollection` |

ObsSceneFileEditor.cs is the canonical add/remove implementation per brief. All Stage 7.8C code routes through it (except the standalone cleanup binary which must be self-contained).

---

## 5. File-Edit Semantics Summary (v12 parity confirmed)

| Behavior | v12 (tray.ps1) | Stage 7.8C (C#) |
|---|---|---|
| Add trigger | 5s startup timer + tray toggle | 5s startup Task.Delay + tray toggle |
| OBS running at add time | Edit JSON + start exit watcher (re-write after OBS exit) | Edit JSON + show toast + set PendingRestart state |
| OBS closed at add time | Edit JSON; effective on next OBS open | Edit JSON; effective on next OBS open |
| Idempotent | Test-OBSBrowserSourceExists before write | Source name scan in AddToCollection |
| URL-update-on-mismatch | Not confirmed in Audit C | GAP 1 -- to be fixed in STEP 2 |
| Write validation | Not confirmed (PS ConvertTo-Json) | GAP 2 -- to be fixed in STEP 2 |
| Remove trigger | Tray toggle + MSI uninstall | Tray toggle + cleanup binary |
| Remove scope | All collections (sources[] + scene_items[]) | All collections (sources[] + scene_items[]) |
| UTF-8 write | No BOM (tray.ps1:3815) | No BOM (NoBomUtf8) |
