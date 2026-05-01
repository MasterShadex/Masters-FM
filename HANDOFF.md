# Master's FM — HANDOFF (read this FIRST every new session)

**Current version:** `v8.1.9` (2026-04-27) — next bump rolls to v8.2.0
**Source root:** `F:\Claude AI\Master FM\`
**Runtime install:** `C:\Users\Master\AppData\Local\MastersFM\` (READ-ONLY for editing — only the rebuild pipeline writes here)
**User config:** `C:\Users\Master\AppData\Roaming\MastersFM\config.json` (survives MSI reinstalls)
**Logs:** `C:\Users\Master\AppData\Local\MastersFM\*.log` (server.log, startup.log, host.log, overlay.log…)

---

## ⚡ ALWAYS-USE COMMAND
```
powershell.exe -ExecutionPolicy Bypass -File "F:\Claude AI\Master FM\_full_rebuild.ps1"
```
Don't use `REBUILD.bat`. The PS1 does: pkg → csc 4 exes → resedit → MSI build → sign → stop running tray+server → uninstall old MSI → install new MSI → launch. Bundle output: `C:\Users\Master\Desktop\MastersFM_Installer\` (.msi + .cer + INSTALL.bat).

---

## 🔢 VERSION-NUMBER RULE (HARD)
Single-digit segments only. **NEVER** v8.0.10 — roll over: v8.0.9 → v8.1.0. The major (x.0.0) only rolls on explicit user signal. Bump on every rebuild that ships a logical change — the user has called this out twice. Each version gets its own `PATCH_HISTORY` entry in `tray.ps1`.

---

## 🧠 ARCHITECTURE QUICK MAP

```
[OBS Browser Source 1000×200] ──HTTP──> server.exe :4242
                                            │
                          serves overlay.html (the live overlay)
                          serves customize.html (the editor GUI)
                                            │
   audio_spectrum.exe (NAudio WASAPI loopback) ──FFT──> SSE /spectrum
                                            │
   server.exe broadcasts via SSE:  ◄────────┘
     event: track          (now-playing data)
     event: overlay-config (after Apply to OBS)
     event: preview-config (live customize edits — only iframe with ?preview=1 listens)
```

### Core source files (all in `F:\Claude AI\Master FM\`)
| File | Lines | Purpose |
|---|---|---|
| `overlay.html` | ~3500 | The actual overlay rendered in OBS Browser Source |
| `customize.html` | ~3700 | The editor GUI (sidebar + iframe live-preview) |
| `tray.ps1` | ~3900 | Win tray app + welcome dialog + patch-notes window + APP_VERSION + PATCH_HISTORY |
| `server.js` | ~1700 | Node HTTP+SSE server (wrapped via pkg into server.exe) |
| `audio_spectrum.cs` | ~1500 | C# WASAPI loopback → FFT → SSE bands stream |
| `MastersFM.cs` | small | Launcher (job object holds tray + server + audio_spectrum) |
| `MastersFM_Tray.cs` | small | C# host that runs tray.ps1 in a real PowerShell runspace |
| `customize.cs` | small | Native window wrapper that loads customize.html via WebView2 |
| `_full_rebuild.ps1` | ~250 | The one rebuild script. Handles signing, MSI bundle, install. |

---

## 📦 LIVE OVERLAY DOM (overlay.html)

```
<div id="scale-wrapper">          <!-- transform: scale(0.5) on 200%×200% wrap → logical px = 2× visible -->
  <div id="widget">                  <!-- inset:40 (glow padding) — fades out drop-shadow -->
    <div id="glow-wrap">              <!-- has the drop-shadow filter for outer glow -->
      <div class="card-outer">         <!-- v8.1.1: width grows OUTWARD with --border-thickness -->
        <div class="card-outer::before"> <!-- spinning conic-gradient border -->
        <div class="card-inner">        <!-- inset:var(--border-thickness) — STAYS 935×135 visible -->
          <div class="art-wrap" data-layout-node="albumArt">
          <div class="info">             <!-- display:contents in layout-mode -->
            <div class="now-playing-label">
              <div class="bars" data-layout-node="animatedBars">
              <span class="now-playing-text" data-layout-node="nowPlayingText">
              <span class="platform-badge" data-layout-node="platformBadge">
            <div class="title-wrap" data-layout-node="trackTitle">
              <div class="track-title">
            <div class="artist-wrap" data-layout-node="artistName">
              <div class="artist">
          <div class="right-col">         <!-- display:contents in layout-mode -->
            <canvas id="spectrum-canvas" data-layout-node="spectrum">
            <div class="time-bar" data-layout-node="progressBar">
```

### Layout-mode geometry (CRITICAL)
- Iframe natural: 1000×200 (OBS source size)
- After scale-wrapper (0.5x): logical 2000×400, visible 1000×200
- After widget inset:40 + card-outer calc(100%-40 + ...) + card-inner inset:5 logical:
  - **Visible card-inner = 935×135** at default border-thickness
  - **V6_PAD_VIS = 32.5** (visible inset of card-inner from iframe edge)
- Layout-template coordinates are in CARD-INNER-RELATIVE space (935×135), not raw canvas (1000×200)
- `applyLayoutMode()` does `cw = canvas.width - V6_PAD_VIS*2` to get usable width
- **v8.1.1 fix:** `card-outer.width = calc(100% - 40px + 2 * (var(--border-thickness, 5px) - 5px))` so border GROWS OUTWARD into glow padding instead of shrinking card-inner inward. card-inner stays 935×135 regardless of slider.

---

## ⚠️ HARD-WON GOTCHAS (don't repeat these)

### 1. Closing-script-tag inside a JS COMMENT kills the page
```
// the closing-script tag, so binding...    ← HTML parser sees this and ENDS the script tag
```
Cost us v8.0.0 — the entire customize page was broken because init() never ran. **Never write the literal closing-script string inside any script block, even in a comment.** Use "closing script tag" or split it.

### 2. PowerShell escape rules (tray.ps1)
- Backtick is the PS escape, NOT backslash
- Inside double-quoted strings, escape-double-quote ends the string early — patch notes broke twice doing this
- Use plain text in patch-note `Text = "..."` — no escapes, no special chars that confuse PS

### 3. Init order in customize.html
- Inline `<script>` runs synchronously DURING parser
- DOM elements that come AFTER the `<script>` tag (e.g. modals at the bottom of body) DON'T exist yet at top-level script execution
- Bind those INSIDE `init()` (the async IIFE at end of script) — its `await fetch(...)` yields to the parser, by which time the rest of the body has been parsed
- Pattern: `wirePresetManager()` is called from init() AFTER await
- If you bind at top-level, `getElementById` returns null and `addEventListener` throws TypeError that aborts the rest of the script

### 4. SSE preview-config gating (v8.0.8 fix)
- `preview-config` SSE events broadcast to ALL connected overlays
- Previously the OBS overlay subscribed too — every customize tweak hit the live stream + flipped back 5s later via polling
- **Fix:** wrap the listener in `if (_IS_PREVIEW)` (the `?preview=1` flag)
- OBS overlay only updates on `overlay-config` event, which fires from Apply to OBS

### 5. simBands backfill hides spectrum silence (v8.0.9 fix)
- `Math.max(energy, simBands[srcIdx] * 0.15)` on the WASAPI path forces a 15% simulator floor
- Made silent audio show a phantom thin bar at the bottom no slider could kill
- **Fix:** removed the Math.max on WASAPI path. Kept it in analyser fallback for browsers without audio_spectrum.exe access

### 6. Layout templates' coords are CARD-INNER-relative
- 935×135 max, NOT 1000×200
- albumArt at 200×200 OVERFLOWS the visible card. Max h = 135.
- Templates that use 1000-wide elements like progress bars get clipped at the card-inner border-radius corners
- All 11 templates (v8.1.2) sized within 935×135

### 7. Mouseleave timer + click commit race (v8.1.0 fix)
- Hover-preview's `mouseleave` had a 60ms `setTimeout(restoreSnapshot)` debounce
- When user CLICKED a card, the modal closed → mouseleave fired → 60ms later restoreSnapshot ran → UNDID the just-applied template
- **Fix:** mouseleave timer also checks `_committed` flag

### 8. applyTheme stripped Dynamic Colors (v8.0.0 fix)
- `applyTheme` deepMerged theme onto DEFAULTS, then preserved ONLY overlay/art/nowPlaying from S
- Lost S.dynamicColors → flipped Dynamic Colors OFF on every theme switch
- **Fix:** added dynamicColors AND layout to the preserve list

### 9. WebView2 / OBS Browser Source = Chromium 108+
- Use modern CSS like `:has()`, `text-wrap: balance`, `aspect-ratio` — they all work
- Houdini `@property` works for color interpolation in linear-gradient

### 10. SMTC stale-timeline carry-over on YouTube/browser sources (v8.1.4 → v8.1.9)
- When YouTube auto-advances to the next video (or pre-roll ad ends), Chrome updates SMTC `MediaProperties` (title/artist) but doesn't always push fresh `TimelineProperties` — previous video's `Position` AND `EndTime` stay pinned for several heartbeats
- Symptom progression across versions:
  - v8.1.3 and earlier: bar shows previous video's pos AND dur ("1:31 / 5:52" carry-over)
  - v8.1.4: dur-only check; bar hidden when carry-over detected, but false-positives when two videos share same duration
  - v8.1.7: tried `tlFresh` trust signal — wrong because tlFresh just means "Chrome touched timeline" not "data is fresh"
  - v8.1.8: tried comparing extrapolated posMs against cached posMs — extrapolation in `Get-SMTCPosition` adds wall-clock age to rawPosMs, so the same stale data compared differently across polls
  - **v8.1.9 (current):** `Get-TrustedTimelineMs` helper in tray.ps1 (~line 5003) takes `rawPosMs` (un-extrapolated, what SMTC physically has stored) for cache comparison. Detection: "title changed AND `rawPosMs > 5 s` AND `durMs` unchanged from previous". Fresh YouTube videos always start at position 0, so any new title with high rawPos is almost certainly carry-over
- **Action when detected:** emit `posMs=0 AND durMs=0` (overlay hides time bar via `setDuration(0)`; server resets startedAt to now). Returns hashtable `@{ posMs; durMs }` to caller.
- **Clear conditions:** `durMs` changes from cached stale value OR `rawPosMs` drops below 5 s (new video actually started) OR give-up timer (60 ticks ~15 s)
- Called from BOTH `Find-SMTCSession` (covers `Get-BrowserMediaNowPlaying` → YouTube/SoundCloud/Deezer browser path AND `Get-SpotifyNowPlaying`) AND `Get-SMTCNowPlaying` (general SMTC scanner). Both must read `$_tl.rawPosMs` from `Get-SMTCPosition` and pass it as the third argument

### 11. SMTC broken-resume on YouTube/browser sources (v8.1.6 fix)
- When the user pauses a YouTube video and walks away for several minutes, Chrome eventually re-emits the SMTC session in a busted state — `PlaybackStatus = Playing` AND `positionMs == EndTime` (a contradiction — a video at duration is finished, not playing)
- Symptom: overlay shows "17:43 / 17:43" while user was actually paused at 15:39. Re-pausing snaps back to 15:39 (correct), but unpausing flips back to 17:43 because the next webhook still has positionMs ≈ duration
- **Fix:** `Get-TrustedPlaybackState` helper in tray.ps1 (~line 5060, sits next to `Get-TrustedDurMs`) caches the most recent mid-video paused position per SMTC session via `$global:_smtcResumeGuard`. When SMTC reports playing-AND-at-end while a mid-video pausedPos is cached, tray overrides the playback state back to Paused at the cached position. Once SMTC reports a sane positionMs (clearly under duration), the guard clears and tray trusts SMTC again
- Called from BOTH `Find-SMTCSession` (covers `Get-BrowserMediaNowPlaying` → YouTube/SoundCloud/Deezer browser path AND `Get-SpotifyNowPlaying`) AND `Get-SMTCNowPlaying` (general SMTC scanner). Same wiring pattern as `Get-TrustedDurMs` from v8.1.4
- Only fires when cached pausedPos < 90% of duration AND current pos within 1.5 s of duration — narrow guard so legitimate end-of-track playback still works

### 12. Patch notes auto-dismiss after 10 seconds
- `Show-WelcomeDialog` has `$AUTO_CLOSE_SECONDS = 10`
- Each version bump triggers it ONCE on first launch of that version
- If you don't press a key within 10s it auto-dismisses and saves `welcome_seen_version` so it never re-shows for that version
- User missed this through 8 rebuilds because they were in OBS/customize while it popped behind

---

## 🎨 DEFAULTS / CONFIG SCHEMA (v8.1.3)

`config.json` lives in Roaming. Top-level keys: `lastfm_username`, `discord_rpc`, `platforms`, `liveAudioVisualizer`, `overlay`. The `overlay` block is what's saved by Apply to OBS and what overlay.html consumes.

```js
// overlay.html DEFAULTS — mirror in customize.html DEFAULTS or saved presets break
{
  font: 'Inter',
  liveAudioVisualizer: true,   // tray-driven flag
  overlay: { opacity: 1.0 },
  lastPresetName: '',          // v8.0.1: which preset is active
  card: {
    borderRadius: 56, borderThickness: 5,
    backgroundTop: 'rgba(18,6,36,0.99)', backgroundBottom: 'rgba(12,4,24,0.99)',
    backgroundAngle: 148,
    backgroundBlur: 0,         // v7.1.3: backdrop-filter blur
    backgroundOpacity: 0.99    // v7.1.3: card transparency for blur to show through
  },
  border: { enabled: true, spinDuration: 4, colors: ['#ff1085','#ff60c8','#d040ff','#8020e0','#a040d8'] },
  glow:   { enabled: true, color1: '#ff60c8', color2: '#a060d0', intensity: 1.0, pulseDuration: 4 },
  art:    { enabled: true, position: 'left' /* | 'right' | 'center' (v8.0.0 dual-fade) */, width: 310, fadeWidth: 130 },
  nowPlaying:    { text:'Now Playing', fontSize:44, color:'#c060ff', letterSpacing:5,
                   glowEnabled:false, glowColor:'#c060ff', glowSize:12 },   // v8.1.3
  bars:          { enabled:true, color:'#c060ff', count:4, speed:0.85 },
  platformBadge: { enabled:true, soundcloudLabel:'SoundCloud',
                   color:'rgba(200,160,255,0.38)', dotColor:'rgba(200,100,255,0.28)',
                   fontSize:30, fontWeight:600, letterSpacing:2,
                   glowEnabled:false, glowColor:'#c8a0ff', glowSize:10 },   // v8.1.3
  title:  { fontSize:68, fontWeight:800, color:'#ffffff', marqueeSpeed:68, marqueePause:2,
            letterSpacing:0, glowEnabled:false, glowColor:'#ff80ff', glowSize:20 },
  artist: { fontSize:44, fontWeight:500, color:'rgba(220,185,255,0.52)', letterSpacing:0,
            marqueeSpeed:68, marqueePause:2,                              // v8.0.0
            glowEnabled:false, glowColor:'#80c0ff', glowSize:14 },        // v8.0.0
  spectrum: {
    enabled:true, barCount:50, gap:3, barRadius:4,
    colorMode:'rainbow' /* | 'solid' | 'gradient' */, color:'#c060ff',
    fps:1000, responseMs:10.7,        // /set-hop on audio_spectrum.exe
    sensitivity:1.0,                  // /set-sensitivity
    smoothing:0.60, heightMult:1.0, minHeight:2, mirrorMode:false,
    autoGain:false, opacity:1.0
  },
  progressBar: { enabled:true, height:9, borderRadius:6,
                 trackColor:'rgba(160,70,255,0.10)',
                 fillColors:['#8020c0','#ff60c8','#c040ff'] },
  timestamps: { fontSize:46, color:'#e090ff',
                glowEnabled:false, glowColor:'#e090ff', glowSize:12 },     // v8.1.3
  showAnimation: { duration:0.5, slideDistance:28, direction:'up', easing:'spring', crossfadeOnSkip:false },
  pauseBehavior: { keepVisibleOnPause:false },
  dynamicColors: { enabled:false, bg:true, glow:true, titleText:true, titleGlow:true,
                   artistText:true, artistGlow:true,                      // v8.0.0
                   nowPlayingText:true, npGlow:true,                       // v8.1.3
                   platformBadge:true, platformGlow:true,                  // v8.1.3
                   spectrum:true, timestamps:true, tsGlow:true,            // v8.1.3
                   progressBar:true, border:true },
  layout: {
    enabled: false,
    canvas: { width: 1000, height: 200 },   // HARD-LOCKED to 1000×200 in v8.0.1+
    gridSize: 8,
    nodes: {
      albumArt:        { x, y, w, h, z:1, visible, locked, anchor:'topleft' },
      animatedBars:    { ..., z:2 },
      nowPlayingText:  { ..., z:2 },
      platformBadge:   { ..., z:2 },
      trackTitle:      { ..., z:3 },
      artistName:      { ..., z:4 },
      spectrum:        { ..., z:5 },
      progressBar:     { ..., z:6, anchor:'bottomleft' }
    }
  }
}
```

**deepMerge(DEFAULTS, S)** runs on every save path (Apply to OBS, Save Preset, sendPreview) so any new key added to DEFAULTS auto-persists into all saved configs. Adding a new field = add it to BOTH overlay.html and customize.html DEFAULTS, and you're done — no save-logic changes needed.

---

## 🗂 PRESET MANAGER (v8.0.0+)

Topbar layout: `🗂 Preset Manager | ↺ Reset to Defaults | ✓ Apply to OBS`
- Old `<select>` dropdown + ✕ button + 💾 Save Preset all REMOVED
- All preset operations live in the modal now (custom GUI, not browser prompt/confirm)
- Each row: 📂 Load (auto-applies to OBS) | 💾 Overwrite | ✏ Rename (inline) | 📋 Duplicate (auto-suffixes) | 🗑 Delete (in-row Yes/No confirm)
- Save form pre-fills with the loaded preset name
- Modal markup is at the bottom of customize.html — bindings live in `wirePresetManager()` called from init()
- `_lastPresetName` persisted as `S.lastPresetName` so it survives Master's FM restart
- `markDirty()` clears the active-preset highlight when user edits anything

Endpoints (server.js):
- `GET  /list-presets` → array of names
- `GET  /load-preset?name=X` → config object
- `POST /save-preset` body: `{name, config}` (overwrites by name)
- `DELETE /delete-preset?name=X`
- Rename = client-side: load + save-as-new + delete-old
- Duplicate = client-side: load + save with auto-suffixed name

---

## 🎚 LAYOUT EDITOR (v8.0.0+)

- Canvas hard-locked to **1000×200**. Slider/preset/swap-WH all removed.
- 11 templates (Horizontal, Right-Aligned, Centered Art, Spectrum Top, Spectrum Bottom, Spectrum Hero, No Album Art, Minimal Text-Only, Bookend, Title-On-Art, DJ Booth, Square Art-Forward, Compact Card)
- All template coords fit within 935×135 visible card-inner (v8.1.2 rewrite)
- Templates modal: SVG mini-previews per card, hover-preview pushes to live iframe, click commits, auto-restore on close-without-commit, no confirm dialog
- Editor outlines (purple dashed boxes + labels + handles) **invisible by default** in the live preview — fade in only when you hover OR a node is selected (v8.0.7)
- Hovered node = 100% opacity. Other nodes during hover = 50% opacity (CSS `:has()` rule)
- Locked nodes: faint amber outline always shown + `pointer-events:none` so they don't block clicks on unlocked siblings
- Show / Lock Elements rows have BOTH visibility + lock toggles (8 elements × 2 toggles)

---

## 🎨 VISUAL THEMES (22 total)

Originals (10): Rainbow (Default), Neon Blue, Hot Pink, Retro Orange, Synthwave, Forest Green, Crimson, Midnight, Cherry Blossom, Minimal White
v8.0.2 additions (12): Vaporwave, Aurora, Royal Purple, Coffee, Volcano, Ice Crystal, Galaxy, Sunset, Lime, Vintage Sepia, Cyber Matrix, Dreamcore

Visual Themes section is **default-collapsed** in the sidebar (v8.0.3) so the customize page opens tidy.

---

## 🧭 SIDEBAR ORDER (v8.0.2 — top-down logical flow)

1. Visual Themes (auto/preset)
2. Dynamic Colors (auto from album)
3. General (overlay opacity, pause behaviour)
4. Layout (custom positioning + templates)
5. Font
6. Slide-In Animation
7. Card Shape
8. Spinning Border
9. Outer Glow
10. **Text Glow (v8.1.5 — NP / Platform / Title / Artist / TS glow controls all here)**
11. Album Art
12. Now Playing Label (incl. Animated Bars; NP Glow MOVED to Text Glow)
13. Platform Badge (Platform Glow MOVED to Text Glow)
14. Track & Artist (Title Glow + Artist Glow MOVED to Text Glow)
15. Spectrum Visualizer (4 sub-groups: Smart auto-tune / Look & feel / Bar shape / Animation feel)
16. Progress & Timestamps (TS Glow MOVED to Text Glow)

Reorder is a runtime DOM swap (`appendChild` moves existing nodes) — no HTML order changes.

---

## 📋 v8.x VERSION HISTORY (in tray.ps1 PATCH_HISTORY)

```
v8.0.0  Preset Manager GUI + artist marquee/glow + canvas restriction + center-art double-fade
v8.0.1  pm-close fix + auto-load last preset + intro text balance + topbar dropdown removal
v8.0.2  11 layout templates + 12 themes + sidebar reorder
v8.0.3  Spectrum reorg + themes default-collapsed
v8.0.4  closing-script-in-comment recovery (page was broken since v8.0.0)
v8.0.5  Layout templates SVG mini-previews + coord fixes for card-inner
v8.0.6  Hover-preview + remove confirm dialog
v8.0.7  Hidden-by-default editor outlines + locked-node pointer-events:none + hover-dim
v8.0.8  SSE preview-config gated to ?preview=1 only (OBS no longer sees customize tweaks live)
v8.0.9  Removed simBands backfill on WASAPI path (no more phantom silence baseline)
v8.1.0  Mouseleave-after-click race fix + applyLayoutTemplate calls markDirty
v8.1.1  Border Thickness grows OUTWARD (card-outer expands, card-inner stays 935×135)
v8.1.2  All 11 layout templates rewritten — non-overlapping coords within 935×135
v8.1.3  Glow on Now Playing label / Platform Badge / Timestamps + Dynamic-glow variants for each
v8.1.4  YouTube stale-duration carry-over fix (Get-TrustedDurMs guard in Find-SMTCSession + Get-SMTCNowPlaying)
v8.1.5  New "Text Glow" sidebar section consolidates NP / Platform / Title / Artist / TS glow controls; default glowSize unified to 6 px
v8.1.6  YouTube long-pause carry-over fix (Get-TrustedPlaybackState in Find-SMTCSession + Get-SMTCNowPlaying — overlay no longer claims a paused video finished after walking away)
v8.1.7  Get-TrustedDurMs now uses tlFresh signal + give-up timer so timestamps don't disappear forever when a new YouTube video shares duration with the previous (fixes regression introduced by v8.1.4 over-aggressive guard)
v8.1.8  Replaced Get-TrustedDurMs with Get-TrustedTimelineMs — checks BOTH posMs AND durMs against previous track to detect carry-over; on carry-over emits BOTH 0 (so server-side startedAt is correct when bar reappears)
v8.1.9  Get-TrustedTimelineMs now uses RAW posMs (not extrapolated) for cache compare; detection uses "rawPosMs > 5s on a new title" + "durMs unchanged"; give-up timer 20→60 ticks
```

---

## 🔑 SHIPPING / DISTRIBUTION

- Self-signed cert (CN=MasterShadex). Bundle on Desktop = `.msi` + `.cer` + `INSTALL.bat`
- Friend installs: extract folder → double-click `.cer` → Trusted Publishers → double-click `.msi`. ~20 seconds.
- The bootstrapper `.exe` path is DISABLED (Bitdefender flagged a friend's copy). Don't re-enable without a real CA cert (Certum/SSL.com etc.).
- Smart App Control users still need SAC off or a CA cert — only that clears it.

---

## 🛠 USEFUL HELPER SCRIPTS (in source root)

- `_smoke.ps1` — 9-point automated smoke test (processes, HTTP, Discord, art-proxy, errors)
- `_probe_customize.ps1` — 12-endpoint customize-pipeline probe
- `_check_versions.ps1` — prints Product/Company/Version of all four exes
- `_log_errors.ps1` — greps every log for errors, filters known-benign lines
- `_full_rebuild.ps1` — THE rebuild script. Always use this.

---

## 🧪 LIVE PREVIEW DEBUGGING

The Claude Code MCP `Claude_Preview` tools work great here:
1. `.claude/launch.json` already configured (port 4242, dummy long-running PowerShell command since the real server is started by the tray)
2. `mcp__Claude_Preview__preview_start name="customize"` → attaches
3. `preview_eval` to inspect DOM, computed styles, network requests
4. `preview_screenshot` for visual checks
5. **Resize first** (`preview_resize width=1280 height=800`) — default viewport is 8px wide and confused me for 30 minutes once

---

## 🧹 SESSION-END NOTES (this 2026-04-27 session)

- Started at v6.8.8, ended at v8.1.3 — 30+ rebuilds across one massive session
- Major user feedback patterns to remember:
  - "Bump version per change" — they called this out twice
  - "Make sure nothing breaks" / "remember nothing is bigger than 1000×200" — recurring constraint
  - User shipped many "screenshot says it looks wrong" feedbacks — visual verification matters more than my tooling can show; trust the user's eye
  - "Patch notes don't show on rebuild" was actually 10s auto-dismiss timing, not a bug
- The session got laggy near the end purely from accumulated context (long transcript + many big-file reads). Starting fresh is cheap.

If you're picking up cold: `Master's FM V8.1.3.msi` is on the Desktop, the customize page is fully working, layout templates have proper coordinates, glow works on title/artist/NP/platform/timestamps. The next likely user requests will probably be UX polish, more themes/templates, or new effects on existing elements — DEFAULTS+bind+sync is the established pattern.

Good luck.
