---
name: codebase details — sizes, endpoints, config schema
description: File sizes, server endpoints, audio backends, config keys, sidebar order
type: project
---

# Codebase Details

## File sizes (from 2026-04-28 audit)
- `tray.ps1`: ~7546 lines, 212 catches
- `audio_spectrum.cs`: ~2604 lines, 65 catches
- `server.js`: ~1434 lines, 63 catches
- `overlay.html`: ~3500+ lines
- `customize.html`: ~3700+ lines
- `launcher.cs`: small (~100 lines)
- `customize.cs`: small
- `MastersFM_Tray.cs`: small

## Server endpoints (server.js → server.exe :4242)
- `GET /` → overlay.html
- `GET /customize` → customize.html
- `GET /events` → SSE stream (track, overlay-config, preview-config, capture events)
- `GET /spectrum` → SSE stream (audio bands data)
- `GET /screenshot` → triggers capture round-trip, returns PNG (2.5s timeout)
- `POST /screenshot-response` → overlay posts captured PNG back here
- `GET /list-presets` → array of preset names
- `GET /load-preset?name=X` → config object
- `POST /save-preset` body: `{name, config}`
- `DELETE /delete-preset?name=X`
- `POST /save-overlay-config` → saves config to Roaming
- `POST /set-device` body: `{backend, id}` — switches audio capture backend
- `GET /health` → `{status, backend, ...}`
- `POST /client-log` → returns 204 for anything (by design)
- `/set-sensitivity`, `/set-hop` → audio_spectrum tuning

## Audio backends (audio_spectrum.cs)
- `wasapi_loopback` — default, captures render endpoint output mix
- `wasapi_input` — shared-mode WASAPI capture from input endpoint
- `wasapi_exclusive` — exclusive-mode WASAPI (WDM-KS equivalent)
- `mme` — NAudio WaveInEvent
- `asio` — NAudio AsioOut (requires ASIO drivers; `asio_none` placeholder if none installed)
- WDM-KS: 0 devices on user's current Windows install

## Spectrum renderer
- WebGL renderer (default since v9.3.0): `?renderer=webgl` in OBS URL
- canvas2d fallback: automatic if WebGL fails or opt-out
- RFFT since v9.0.0 (22% CPU reduction over prior CFFT)
- Capture thread at `ThreadPriority.AboveNormal` since v9.1.0
- Self-test at startup: <0.2% diff vs CFFT on 440Hz sine

## Sidebar section order (customize.html, v8.0.2+)
1. Visual Themes (default-collapsed)
2. Dynamic Colors
3. General (opacity, pause behaviour)
4. Layout (editor + templates)
5. Font
6. Slide-In Animation
7. Card Shape
8. Spinning Border
9. Outer Glow
10. Text Glow (NP / Platform / Title / Artist / TS glow — v8.1.5)
11. Album Art
12. Now Playing Label
13. Platform Badge
14. Track & Artist
15. Spectrum Visualizer (4 sub-groups)
16. Progress & Timestamps

## Layout templates (11 total, v8.1.2)
Horizontal, Right-Aligned, Centered Art, Spectrum Top, Spectrum Bottom, Spectrum Hero, No Album Art, Minimal Text-Only, Bookend, Title-On-Art, DJ Booth, Square Art-Forward, Compact Card
All coords fit within 935×135 visible card-inner.

## Visual themes (22 total, v8.0.2)
Originals (10): Rainbow (Default), Neon Blue, Hot Pink, Retro Orange, Synthwave, Forest Green, Crimson, Midnight, Cherry Blossom, Minimal White
v8.0.2 additions (12): Vaporwave, Aurora, Royal Purple, Coffee, Volcano, Ice Crystal, Galaxy, Sunset, Lime, Vintage Sepia, Cyber Matrix, Dreamcore

## Preset manager endpoints pattern
- Rename = client-side: load + save-as-new + delete-old
- Duplicate = client-side: load + save with auto-suffixed name
- `_lastPresetName` persisted as `S.lastPresetName`

## Media sources polled by tray.ps1
- SMTC (Windows system media transport controls) — covers Spotify, YouTube, browsers, osu
- osu process existence cached at 5s TTL
- Spotify process existence cached at 5s TTL
- Last.fm scrobbling
- Discord RPC

## Claude Preview debugging setup
- `.claude/launch.json` configured (port 4242)
- `mcp__Claude_Preview__preview_start name="customize"` → attaches
- **MUST resize first:** `preview_resize width=1280 height=800` — default viewport is 8px wide
- `preview_eval`, `preview_screenshot` for inspection
