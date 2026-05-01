---
name: hard constraints and gotchas
description: Things that must never change, known failure modes, and hard-won lessons
type: project
---

# Hard Constraints

## Paths

- **Source root:** `G:\Project Folder\Master FM\` (confirmed 2026-04-30)
- **Runtime install:** `C:\Users\Master\AppData\Local\MastersFM\` — READ-ONLY, never edit directly
- **User config:** `C:\Users\Master\AppData\Roaming\MastersFM\config.json` — survives MSI reinstalls

## Build rules

- Always use `_full_rebuild.ps1`. Never `REBUILD.bat`.
- **Sacred files — never edit:** `_full_rebuild.ps1`, `build_msi.py`, `_sign_msi.ps1`, `_build_bootstrapper.ps1`, `REBUILD.bat`, `INSTALL.bat`, `build_tools/`
- **Version rule (HARD):** single-digit segments only. v8.0.9 → v8.1.0, never v8.0.10. Bump on every logical-change rebuild. Major (x.0.0) only on explicit user signal.
- Every shipped rebuild: bump `APP_VERSION` in `tray.ps1` AND prepend a `PATCH_HISTORY` entry.

## Layout / geometry

- Canvas hard-locked to **1000×200**. No sliders, no swap-WH.
- Visible card-inner = **935×135** at default border-thickness. Layout template coords are card-inner-relative, NOT 1000×200.
- albumArt at 200×200 overflows visible card (max h=135). All 11 templates fit within 935×135 (rewritten v8.1.2).

## Code gotchas (hard-won)

### 1. Closing `</script>` tag in JS comment kills the page
Never write the literal string `</script>` inside any script block, even in a comment.
Cost: entire customize page broken in v8.0.0 (init() never ran). Write "closing script tag" or split it.

### 2. PowerShell escape rules (tray.ps1)
- Backtick is the PS escape, NOT backslash
- Double-quoted string with `\"` in patch note text breaks PS parser — use `'` single quotes in patch note strings
- `Invoke-RestMethod` misdecodes UTF-8 multi-byte chars using system codepage; fetch() in browser is correct. Only affects PS test harnesses.
- Validate PS syntax with `[Parser]::ParseFile` before rebuild when editing tray.ps1 patch notes.

### 3. Init order in customize.html
- Inline `<script>` runs synchronously during parser
- DOM elements AFTER the `<script>` tag don't exist yet at top-level execution
- Bind those inside `init()` (the async IIFE) after `await fetch(...)` — fetch yields to the parser
- Pattern: `wirePresetManager()` called from init() after await

### 4. SSE preview-config gating
- `preview-config` SSE must be gated to `if (_IS_PREVIEW)` (the `?preview=1` flag)
- OBS overlay only updates on `overlay-config` event (fired by Apply to OBS)

### 5. SMTC stale-timeline (YouTube carry-over)
- When YouTube auto-advances, Chrome may not push fresh `TimelineProperties`
- `Get-TrustedTimelineMs` in tray.ps1 (~line 5003) uses RAW posMs (not extrapolated) for cache compare
- Detection: "title changed AND rawPosMs > 5s AND durMs unchanged from previous"
- On carry-over: emit posMs=0 AND durMs=0; clear when durMs changes OR rawPosMs < 5s OR give-up (60 ticks)

### 6. SMTC broken-resume (YouTube long pause)
- Chrome re-emits SMTC in busted state after long pause: `PlaybackStatus=Playing AND positionMs==EndTime`
- `Get-TrustedPlaybackState` in tray.ps1 (~line 5060) caches mid-video pausedPos; overrides back to Paused
- Guard only fires when pausedPos < 90% duration AND pos within 1.5s of end

### 7. applyTheme must preserve dynamicColors + layout
- `applyTheme` deepMerges onto DEFAULTS — must preserve `dynamicColors` AND `layout` from current state S
- Otherwise Dynamic Colors gets flipped off on every theme switch

### 8. audio_spectrum.exe does NOT auto-respawn after kill
- Job Object architecture; by design
- Recovery: Restart menu or full relaunch
- Not a bug

### 9. tray.ps1 registry probe uses SilentlyContinue
- Line ~2033: `-ErrorAction Stop` was changed to `SilentlyContinue` on registry probe
- If reverting old code, verify this fix is still in place

### 10. Webhook must be async (fire-and-forget)
- `Invoke-RestMethod` on UI thread blocks 200-900ms; fixed in v8.2.5 with `Send-WebhookAsync` (HttpClient.PostAsync)
- Never put synchronous HTTP calls on the tray polling thread

## Config / DEFAULTS

- Adding a new config field: add to BOTH `overlay.html` DEFAULTS and `customize.html` DEFAULTS
- `deepMerge(DEFAULTS, S)` runs on every save path — auto-persists any new key. No save-logic changes needed.

## Process priorities (v9.6.0)
- `server.exe` → BelowNormal
- `MastersFM_Tray.exe` (tray host) → BelowNormal
- `audio_spectrum.exe` → Normal (explicit; real-time capture deadlines)
- Audio capture thread inside audio_spectrum → AboveNormal (v9.1.0 boost; KEEP)
