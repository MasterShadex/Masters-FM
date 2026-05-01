---
name: open issues and deferred items
description: Unfinished features, known bugs, and deferred work with status
type: project
---

# Open Issues & Deferred Items

## ACTIONABLE / UNFINISHED FEATURES

### 1. OBS Source Side placement (OBS_SOURCE_SIDE_TASK.md)
- `customize.html` UI already wired: `#c-obs-side`, `#btn-obs-reset` elements exist
- **Backend NOT implemented** — needs:
  - `server.js`: `POST /obs-reset-position` endpoint
  - `tray.ps1`: `Get-OBSCanvasSize`, `Get-OBSSourceSide`, `Compute-OBSPosition`, `Reset-OBSSourcePosition` functions; modified `Add-OBSBrowserSourceDirect`; 1500ms flag-watcher timer
- Targeted at v6.0.6 originally — needs rebasing to v9.6.x before shipping

### 2. Texture / material library (V7_IDEAS.md)
- Concept: glass, vinyl grain, CRT scanline, holographic, neon tube visual effects
- Approach: backdrop-filter + canvas overlay + blend-mode
- Never started. Backlog item only.

## DEFERRED — KNOWN ARCHITECTURAL

### 3. Tray PowerShell SMA runspace memory leak
- ~25 MB/hour slow leak in tray.ps1 PowerShell runspace
- Root fix: migrate tray to native C# (major effort)
- Current mitigation: Restart menu
- Documented in AUDIT_FUZZ_LOG.md PHASE B finding 9.1

### 4. SIMD magnitude calculation (audio_spectrum.cs)
- Would further reduce FFT CPU but blocked on System.Numerics.Vectors deployment
- Three strikes hit in v9.1.0 run
- To unblock: one of —
  a. Modify `build_msi.py` to include `audio_spectrum.exe.config` binding redirect
  b. Use `unsafe float*` pointer SSE intrinsics
  c. Embed app config as assembly string resource

### 5. WebGL visual parity verification
- User-driven only — append `?renderer=webgl` to OBS Browser Source URL and visually compare
- No automated browser tooling available per procedure constraints

### 6. Zero-wait burst transient in audio_spectrum HTTP server
- Observed 88% failure in one run, 0% in 4 isolated repros — non-reproducible
- If it recurs: capture `audio_spectrum.log` at moment of failure; instrument HandleSetDevice timing; check Windows event log for COM/audio driver events

## DISK SPACE

### 7. F: drive full
- `F:\Claude AI\_BACKUPS_v9*\` etc. consuming ~17 GB on F: (was 0 GB free as of 2026-04-29)
- All v9.6.0+ backups going to C: (727 GB free)
- User should decide which old checkpoints to prune
