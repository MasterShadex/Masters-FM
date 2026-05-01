---
name: Master's FM project overview
description: What the project is, architecture, source files, build pipeline
type: project
---

# Master's FM — Project Overview

**Type:** Windows desktop app — OBS now-playing overlay with spectrum visualizer
**Current version:** v9.6.0 (shipped 2026-04-29)
**Source root:** `G:\Project Folder\Master FM\`
**Runtime install:** `C:\Users\Master\AppData\Local\MastersFM\` (READ-ONLY — only rebuild pipeline writes here)
**User config:** `C:\Users\Master\AppData\Roaming\MastersFM\config.json` (survives MSI reinstalls)

## Architecture

```
[OBS Browser Source 1000×200] ──HTTP──> server.exe :4242
                                            │
                          serves overlay.html (live OBS overlay)
                          serves customize.html (editor GUI)
                                            │
   audio_spectrum.exe (NAudio WASAPI loopback/input/MME/ASIO) ──FFT──> SSE /spectrum
                                            │
   server.exe broadcasts via SSE:
     event: track          (now-playing data)
     event: overlay-config (after Apply to OBS)
     event: preview-config (live customize edits — only iframe with ?preview=1)
     event: capture        (screenshot round-trip trigger)
```

## Core source files

| File | Lines | Purpose |
|---|---|---|
| `overlay.html` | ~3500+ | OBS Browser Source content; WebGL spectrum renderer |
| `customize.html` | ~3700+ | Editor GUI; sidebar controls; layout editor; preset manager |
| `tray.ps1` | ~7500+ | Win tray app; SMTC polling; Discord RPC; Last.fm; APP_VERSION; PATCH_HISTORY |
| `server.js` | ~1400+ | Node HTTP+SSE server (pkg → server.exe); preset/config endpoints |
| `audio_spectrum.cs` | ~2600+ | C# WASAPI/MME/ASIO loopback → RFFT → SSE bands |
| `launcher.cs` | small | Job Object launcher; spawns tray+server+audio_spectrum; sets process priorities |
| `MastersFM_Tray.cs` | small | C# host that runs tray.ps1 in a PowerShell runspace |
| `customize.cs` | small | Native window wrapper loading customize.html via WebView2 |
| `_full_rebuild.ps1` | ~250 | THE build script: pkg → csc 4 exes → resedit → MSI → sign → install |

## Build command

```
powershell.exe -ExecutionPolicy Bypass -File "G:\Project Folder\Master FM\_full_rebuild.ps1"
```

Never use REBUILD.bat. The PS1 handles: pkg → csc → resedit → MSI build → sign → stop tray+server → uninstall old MSI → install new MSI → launch.
Bundle output: `C:\Users\Master\Desktop\MastersFM_Installer\` (.msi + .cer + INSTALL.bat)

## Helper scripts

- `_smoke.ps1` — 9-point automated smoke test (processes, HTTP, Discord, art-proxy, errors)
- `_probe_customize.ps1` — 12-endpoint customize-pipeline probe
- `_check_versions.ps1` — prints Product/Company/Version of all four exes
- `_log_errors.ps1` — greps every log for errors, filters known-benign lines
- `_full_rebuild.ps1` — the one rebuild script

## Build pipeline sacred files (never edit)

`_full_rebuild.ps1`, `build_msi.py`, `_sign_msi.ps1`, `_build_bootstrapper.ps1`, `REBUILD.bat`, `INSTALL.bat`, `build_tools/`

## Distribution

- Self-signed cert (CN=MasterShadex). Bundle = `.msi` + `.cer` + `INSTALL.bat`
- Bootstrapper `.exe` path DISABLED (Bitdefender flagged). Do not re-enable without real CA cert.
