---
name: version history and run log
description: What shipped in each version from v9.0.0 onward, and key facts from each run
type: project
---

# Version History (v9.x — from HANDOFF + run reports)

## v9.6.0 (2026-04-29) — Background-app lag fix
- `launcher.cs`: sets tray + server to `ProcessPriorityClass.BelowNormal` post-spawn
- `audio_spectrum.exe` stays at Normal (real-time audio deadlines)
- Root cause: obs-browser-page at AboveNormal + dwm/parsecd at High starved background Normal apps
- **NOTE:** F: drive was 0 GB free during this run — backups went to C:\. ~17 GB of old `F:\Claude AI\_BACKUPS_v9*\` folders need pruning.

## v9.5.0 (2026-04-29) — Track-change CPU spike fix
- Three layered caches in `tray.ps1`:
  1. `Get-SMTCSessionsCached` / `Get-SMTCMediaPropsCached` — per-tick SMTC session cache
  2. Process-existence cache for Spotify + osu (5s TTL) — eliminates `Get-Process` per-tick scans
  3. Dump-diagnostic frequency 200-tick → 600-tick
- Result: steady-state tick avg 6-9ms → 2ms; track-change 226ms → no SLOW TICK
- Instrumentation kept: `$global:_detectorMs` + `detectors=...` in SLOW TICK log

## v9.4.0 — WebGL on by default (config-key; rolled back)
- Attempt to enable WebGL via config; blank overlay in OBS on some setups
- Rolled back; v9.3.0 approach (URL-param `?renderer=webgl`) was correct

## v9.3.0 (2026-04-29) — WebGL promoted to default via URL param
- WebGL is now the default (`?renderer=webgl` in OBS Browser Source URL)
- OBS Auto-Add URL updated: `http://localhost:4242/?renderer=webgl`
- Renderer toggle in customize panel (Animation Feel → Performance dropdown)
- Config migration: `[MIGRATE v9.3.0]` log on upgrade
- WebGL `_glInit` safety net: hides canvas + nulls handles on failure
- **PowerShell lesson:** patch note text with `\"` inside double-quoted PS string breaks parse; use `'` single quotes in patch notes

## v9.2.0 (2026-04-28) — Screenshot endpoint + WebGL opt-in
- `GET /screenshot` → broadcasts `event: capture` SSE → overlay POSTs canvas PNG back → 2.5s timeout
- WebGL renderer as URL-param opt-in (`?renderer=webgl`)
- `/screenshot` is a permanent diagnostic tool for visual regression testing

## v9.1.0 (2026-04-28) — Capture thread priority boost
- `audio_spectrum.cs` capture thread set to `ThreadPriority.AboveNormal`
- 37% additional CPU reduction (16.21% → 10.27% on max settings)
- SIMD failed (3 strikes — System.Numerics.Vectors version mismatch)
- WebGL initially shipped but blank OBS overlay → rolled back (fixed properly in v9.2.0-9.3.0)

## v9.0.0 (2026-04-28) — Real-FFT (RFFT)
- RFFT replaces legacy complex FFT in `audio_spectrum.cs`
- 22% CPU reduction (17.10% → 13.30%)
- Self-test at startup: <0.2% diff vs CFFT on 440Hz sine
- CFFT fallback path kept in source
- Pre-run version was v8.3.8

## v8.x history (from HANDOFF.md)
See HANDOFF.md §"📋 v8.x VERSION HISTORY" for full list (v8.0.0 through v8.1.9). Key milestones:
- v8.0.0: Preset Manager GUI + artist marquee/glow + canvas restriction + center-art double-fade
- v8.1.0: Mouseleave-after-click race fix
- v8.1.1: Border grows OUTWARD (card-inner stays 935×135)
- v8.1.2: All 11 layout templates rewritten within 935×135
- v8.1.3: Glow on NP / Platform Badge / Timestamps
- v8.1.4-v8.1.9: YouTube SMTC stale-timeline carry-over fixes (see HANDOFF gotcha #10)

## Intermediate versions (from triage run)
- v8.2.0: Bundled 4 fixes from audits (WaveFormat null-check, /save-preset 500→400, /save-overlay-config 500→400, registry -EA Stop→SilentlyContinue)
- v8.2.1: 405 Method Not Allowed on wrong-method requests
- v8.2.2: /set-device input validation in audio_spectrum.cs
- v8.2.3: WdmKsCaptureAdapter constructor refactor (pre-creates `_inner`)
- v8.2.4-v8.2.5: Perf investigation; v8.2.5 fixed webhook fan-out (Invoke-RestMethod → HttpClient.PostAsync fire-and-forget, 468ms → 19ms)
- v8.3.x: Various, pre-v9.0.0 cleanup
