# Task Handoff — "OBS Source Side" Placement Feature

**Target version:** v6.0.6
**Source folder:** `F:\Claude AI\Master FM\`
**Date handed off:** 2026-04-22

## Goal

Add a user-facing preference that controls where Master's FM anchors its
Browser Source on the OBS canvas when the tray auto-adds the source to a
scene. Three choices: **Left edge**, **Right edge**, **Center bottom**.
Persists as `_cfg.obs.sourceSide` (string `"left" | "right" | "center"`),
saved under `cfg.overlay.obs.sourceSide` in `%APPDATA%\MastersFM\config.json`
(because `customize.html` saves the whole `S` blob into `cfg.overlay` via
`/save-overlay-config`).

Typical OBS canvas: `1920 × 1080`. Default Browser Source size:
`1920 × 200` (see `Add-OBSBrowserSourceDirect` in `tray.ps1`, around
lines 2851-2859).

---

## Requirements (verbatim from user request)

1. In `customize.html`, add a select or segmented control under a
   "Placement" section: options "Left edge", "Right edge", "Center
   bottom". Persist as `_cfg.obs.sourceSide` (`"left" | "right" |
   "center"`).
2. In `tray.ps1`'s scene-write path, read that preference and when
   **ADDING a new source item**, compute position:
   - `left`  → `pos.x = 0`, `pos.y = canvasH - sourceH - 40`
   - `right` → `pos.x = canvasW - sourceW`, `pos.y = canvasH - sourceH - 40`
     *(user's spec said just `canvasW - sourceW` for right; keep y at
     bottom-offset too for a clean "flush right" look — match left's y)*
   - `center` → `pos.x = (canvasW - sourceW) / 2`, `pos.y = canvasH - sourceH - 40`

   Canvas width/height should come from the scene collection's video
   settings. **Note**: OBS scene collection JSON files do **not** contain
   base canvas resolution — that lives in the per-profile
   `%APPDATA%\obs-studio\basic\profiles\*\basic.ini` under `[Video]`
   keys `BaseCX` / `BaseCY`. If `basic.ini` can't be read, fall back to
   `1920 × 1080`.

3. **Preserve the user's manual position** if they've already moved the
   item in OBS — only apply the side anchor for FIRST-TIME adds, or
   when the explicit "Reset position" button is pressed. Existing scene
   items with an existing `pos` should NOT be rewritten during
   normal auto-add.

4. Add a "Reset position to [side]" button in the Placement section
   that rewrites just the `pos` fields on the existing scene item in
   every scene collection.

5. Ship as **v6.0.6**. Add a patch-note entry (see section below).

---

## Verify path

1. Start OBS (so at least one scene collection JSON exists).
2. Uninstall Master's FM.
3. Reinstall with `obs.sourceSide = "right"` preference saved.
4. On OBS launch, the scene should now have the Master's FM source
   auto-added and anchored to the right edge.

---

## Current state of the work — WHAT'S ALREADY DONE

### ✅ `customize.html`

The following is **already implemented and present in the source file**:

1. **DEFAULTS** — added `obs: { sourceSide: 'left' }` (line ~1288).
2. **Placement section HTML** — new `<div class="section">` with
   `data-sec="obsplace"` inserted after the Animation section (line
   ~1243 area). Contains:
   - `<select id="c-obs-side">` with three options (left / right /
     center), labelled with arrow glyphs `←`, `→`, `═`.
   - `<button class="btn-anim-preview" id="btn-obs-reset">` labelled
     "⟲ Reset Position to [side]" with a `<span id="v-obs-side-label">`
     that updates as the user changes the select.
3. **JS wiring** — in `initBindings()`:
   - `bindSelect('c-obs-side', ...)` writes `S.obs.sourceSide` and
     calls `updateObsSideLabel(v)`.
   - Click handler on `#btn-obs-reset` calls `resetObsPosition()` which:
     - POSTs `S` to `/save-overlay-config` first (so the preference
       is persisted before the tray reads it).
     - POSTs `{ side }` JSON to `/obs-reset-position`.
     - Surfaces success/error in the existing `#status` topbar span.
4. **syncAll()** — new block syncs `c-obs-side` from
   `S.obs?.sourceSide ?? 'left'` and calls `updateObsSideLabel()` to
   initialise the button label.

**Nothing else in `customize.html` needs to change** for this feature.
Do not touch the existing `<select id="c-anim-dir">` — that's the
animation direction select and is unrelated. The new select uses the
same `<select>` + `<option>` pattern as `c-anim-dir`, so keep the
anchor behaviour identical.

### ❌ STILL TO DO

#### 1. `server.js` — add `/obs-reset-position` POST endpoint

Add after the existing `/save-overlay-config` handler (around line
1363, before the `/customize` GET handler). The endpoint:

- Reads optional JSON body `{ side: "left"|"right"|"center" }`
  (for telemetry/logging — the authoritative side is whatever is in
  `config.overlay.obs.sourceSide` at the time tray picks up the flag).
- Writes a small text file (just a timestamp is fine) to
  `%APPDATA%\MastersFM\obs_reset_request.flag` as UTF-8 no BOM.
- Responds `200 OK` on success, `500` with the error message on
  failure. Log via existing `log()`/`flog()` helpers.

Use `os.homedir()` + path or `process.env.APPDATA` to resolve the
path (server.js already uses `findConfigPath()` / `process.env.APPDATA`
— mirror whatever's there).

#### 2. `tray.ps1` — implement the position logic

**A) Canvas-size helper.** New function near the existing
`Get-OBSSceneCollectionPaths` (around line 2709):

```powershell
function Get-OBSCanvasSize {
    # Reads BaseCX/BaseCY from the most recently-modified basic.ini
    # under %APPDATA%\obs-studio\basic\profiles\*. Falls back to
    # 1920x1080 if no profile is found.
    $defaults = @{ Width = 1920; Height = 1080 }
    try {
        $profilesDir = [System.IO.Path]::Combine($env:APPDATA, "obs-studio", "basic", "profiles")
        if (-not (Test-Path $profilesDir)) { return $defaults }
        $ini = Get-ChildItem $profilesDir -Recurse -Filter "basic.ini" -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending |
               Select-Object -First 1
        if (-not $ini) { return $defaults }
        $text = [System.IO.File]::ReadAllText($ini.FullName)
        $w = if ($text -match '(?m)^BaseCX=(\d+)') { [int]$Matches[1] } else { 1920 }
        $h = if ($text -match '(?m)^BaseCY=(\d+)') { [int]$Matches[1] } else { 1080 }
        return @{ Width = $w; Height = $h }
    } catch {
        Log "Get-OBSCanvasSize failed: $_"
        return $defaults
    }
}
```

**B) Side-preference reader.** New function near the top of the config
helpers (after `Save-ConfigField`, around line 751):

```powershell
function Get-OBSSourceSide {
    try {
        $cfgPath = Get-UserCfgPath
        if (-not (Test-Path $cfgPath)) { return 'left' }
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        $side = $cfg.overlay.obs.sourceSide
        if ($side -in @('left','right','center')) { return $side }
    } catch {}
    return 'left'
}
```

**C) Position-computer.**

```powershell
function Compute-OBSPosition($side, $canvasW, $canvasH, $sourceW, $sourceH) {
    $marginBottom = 40
    $y = [Math]::Max(0, $canvasH - $sourceH - $marginBottom)
    switch ($side) {
        'right'  { return @{ x = [double]([Math]::Max(0, $canvasW - $sourceW)); y = [double]$y } }
        'center' { return @{ x = [double]([Math]::Max(0, ($canvasW - $sourceW) / 2)); y = [double]$y } }
        default  { return @{ x = 0.0; y = [double]$y } }   # left
    }
}
```

**D) Modify `Add-OBSBrowserSourceDirect`** (around line 2910) so the
`$newItem` construction uses the computed position instead of
`pos = [PSCustomObject]@{ x = 0.0; y = 0.0 }`. Only applies to the
NEW-item branch (when a scene is missing the item). Existing items
are untouched — their `pos` stays whatever the user set.

Pull the canvas size and source dimensions once per-collection:

```powershell
$canvas   = Get-OBSCanvasSize
$sideKey  = Get-OBSSourceSide
$srcW     = if ($existingSrc -and $existingSrc.settings.width)  { [int]$existingSrc.settings.width }  else { 1920 }
$srcH     = if ($existingSrc -and $existingSrc.settings.height) { [int]$existingSrc.settings.height } else { 200 }
$anchor   = Compute-OBSPosition $sideKey $canvas.Width $canvas.Height $srcW $srcH
```

Then in the scene-item add block:

```powershell
pos             = [PSCustomObject]@{ x = $anchor.x; y = $anchor.y }
```

Log the anchor decision: `Log "OBS direct: anchoring new item to '$sideKey' at ($($anchor.x), $($anchor.y)) on ${($canvas.Width)}x${($canvas.Height)} canvas"`.

**E) `Reset-OBSSourcePosition`** — new function after
`Remove-OBSBrowserSourceDirect` (around line 2995):

```powershell
function Reset-OBSSourcePosition {
    $paths = Get-OBSSceneCollectionPaths
    if (-not $paths -or $paths.Count -eq 0) {
        Log "OBS reset position: no scene collections found"
        return "NO_SCENES"
    }
    $canvas  = Get-OBSCanvasSize
    $sideKey = Get-OBSSourceSide
    $updated = @()
    $noBom   = [System.Text.UTF8Encoding]::new($false)

    foreach ($path in $paths) {
        $colName = [System.IO.Path]::GetFileNameWithoutExtension($path)
        try {
            $raw  = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
            $json = $raw | ConvertFrom-Json
            $sourceDef = @($json.sources) | Where-Object { $_.name -eq "Master's FM" } | Select-Object -First 1
            $srcW = if ($sourceDef -and $sourceDef.settings.width)  { [int]$sourceDef.settings.width }  else { 1920 }
            $srcH = if ($sourceDef -and $sourceDef.settings.height) { [int]$sourceDef.settings.height } else { 200 }
            $anchor = Compute-OBSPosition $sideKey $canvas.Width $canvas.Height $srcW $srcH

            $changed = $false
            foreach ($src in @($json.sources)) {
                $isScene = ($src.id -eq "scene" -or $src.versioned_id -eq "scene")
                if (-not $isScene) { continue }
                if (-not ($src.settings -and $src.settings.PSObject.Properties['items'])) { continue }
                foreach ($item in @($src.settings.items)) {
                    if ($item.name -ne "Master's FM") { continue }
                    $item.pos = [PSCustomObject]@{ x = $anchor.x; y = $anchor.y }
                    $changed = $true
                    Log "OBS reset: '$colName' / scene '$($src.name)' → ($($anchor.x), $($anchor.y))"
                }
            }
            if ($changed) {
                [System.IO.File]::WriteAllText($path, ($json | ConvertTo-Json -Depth 20), $noBom)
                $updated += $colName
            }
        } catch {
            Log "OBS reset position: ERROR in '$colName': $_"
        }
    }
    if ($updated.Count -gt 0) { return "OK:$($updated -join ', ')" }
    return "NONE"
}
```

**F) Flag-file watcher timer.** tray.ps1 has no HTTP server of its own
and server.js can't call PowerShell directly, so the Reset button's
`/obs-reset-position` POST just writes
`%APPDATA%\MastersFM\obs_reset_request.flag`. Tray watches the flag on
a WinForms timer.

Add after the existing OBS watcher timers (near line 3049) and start
it inside the same block that calls `Try-AddToOBS` on startup:

```powershell
$script:_obsResetFlagPath = [System.IO.Path]::Combine(
    [System.Environment]::GetFolderPath('ApplicationData'),
    'MastersFM', 'obs_reset_request.flag')

if (-not $global:_obsResetTimer) {
    $global:_obsResetTimer = New-Object System.Windows.Forms.Timer
    $global:_obsResetTimer.Interval = 1500
    $global:_obsResetTimer.add_Tick({
        if (-not (Test-Path $script:_obsResetFlagPath)) { return }
        try {
            Remove-Item $script:_obsResetFlagPath -Force -ErrorAction SilentlyContinue
            Log "OBS reset: flag detected — running Reset-OBSSourcePosition"
            $r = Reset-OBSSourcePosition
            Log "OBS reset result: $r"
        } catch { Log "OBS reset flag handler failed: $_" }
    })
    $global:_obsResetTimer.Start()
}
```

**Important**: if OBS is currently running, the user will need to
restart OBS for the pos changes to take effect (OBS keeps scene state
in memory and overwrites the JSON on exit). Mirror the "restart OBS"
balloon-tip pattern from `Try-AddToOBS` if you want to warn them — or
just let the existing OBS exit-watcher (`Start-OBSExitWatcher`)
re-apply on the next OBS restart. Simpler: show a tray balloon after
reset telling the user "If OBS is open, restart it for the change to
take effect."

#### 3. `$script:APP_VERSION` bump + patch note

Line 212 of `tray.ps1` — bump from `"v6.0.4"` to `"v6.0.6"`.

Add a new entry at the TOP of `$script:PATCH_HISTORY` (just after the
array open on line 220), matching the existing shape:

```powershell
@{ Version = "v6.0.6"; Date = "2026-04-22"; Notes = @(
    @{ Tag = "NEW";      Text = "OBS Source Side preference. New 'Placement' section in the customizer (select: Left edge / Right edge / Center bottom) controls where the Browser Source anchors itself on the OBS canvas when the tray auto-adds it to a scene. Canvas width/height is read from your most recent OBS profile's basic.ini (falls back to 1920x1080). Existing scene items you've already positioned by hand are left alone — the anchor only applies to first-time auto-adds." },
    @{ Tag = "NEW";      Text = "'Reset Position to [side]' button in the Placement section. Rewrites just the pos fields on the existing Master's FM scene item in every scene collection, so you can re-snap it to the chosen edge without removing and re-adding the source. If OBS is currently open, restart OBS to see the change — OBS holds scene state in memory and overwrites the JSON on exit." }
) },
```

---

## Files the other AI will need to read / edit

| File | Lines of interest |
|------|-------------------|
| `F:\Claude AI\Master FM\customize.html` | **Already done.** Do not edit. |
| `F:\Claude AI\Master FM\server.js` | `/save-overlay-config` handler ends around 1362; add `/obs-reset-position` immediately after. |
| `F:\Claude AI\Master FM\tray.ps1` | `$script:APP_VERSION` line 212, `$script:PATCH_HISTORY` line 220+, `Get-OBSSceneCollectionPaths` line 2709, `Add-OBSBrowserSourceDirect` line 2766, `Remove-OBSBrowserSourceDirect` line 2955, `Save-ConfigField` line 729, timer init near 3049. |
| `F:\Claude AI\Master FM\HANDOFF.md` | Optional — update session history + version table after the rebuild lands. |

## Rebuild command

```
powershell.exe -ExecutionPolicy Bypass -File "F:\Claude AI\Master FM\_full_rebuild.ps1"
```

Do NOT run `REBUILD.bat`. The `_full_rebuild.ps1` script pkg-bundles
server.js → server.exe, csc-compiles the C# launcher + tray launcher,
builds the signed MSI, uninstalls old, installs new, and relaunches.
See the memory file at
`C:\Users\Master\.claude\projects\F--Claude-AI-Master-FM\memory\MEMORY.md`
for the current distribution / signing setup.

---

## IMPORTANT — don't break the unrelated `<select>` anchor behaviour

An earlier AI session apparently changed something around the overlay
`<select>` / `<option>` anchoring such that an artist text was
replaced with the literal word "selected." on the overlay. The
Placement feature here uses a **plain standard HTML `<select>` with
three `<option>` children** — identical in structure to the existing
`c-anim-dir` animation-direction select. Do NOT introduce any custom
anchor, segmented-control, or JS that rewrites the select's internals.
The element is:

```html
<select id="c-obs-side">
  <option value="left">← Left edge</option>
  <option value="right">→ Right edge</option>
  <option value="center">═ Center bottom</option>
</select>
```

That's it. Keep it that way.

---

## Verification checklist (from user)

- [ ] Start OBS once so a scene collection JSON exists under
      `%APPDATA%\obs-studio\basic\scenes\`.
- [ ] Uninstall Master's FM.
- [ ] Reinstall — open the customizer, set Placement → Right edge,
      click Apply to OBS.
- [ ] Relaunch OBS.
- [ ] The source should auto-add on the right edge.
- [ ] Drag the source manually inside OBS to verify the next auto-add
      does NOT overwrite the manual position.
- [ ] Click Reset Position; restart OBS; the source should re-snap to
      the chosen side.

---

*Handoff written mid-task by the original session. customize.html
changes are already present in the source file. Pick up from the
server.js endpoint and proceed through the tray.ps1 changes, version
bump, and rebuild.*
