# Master's FM v14.0.0-rc.3

Release date: 2026-05-10

## What's new since v12.0.1

### Functional fixes
- Track skip detection now works (heartbeat with position-drift detection, ~1s latency)
- Pause/resume detection works (was silent in rc.2)
- Audio Source dialog now includes WASAPI / MME / KS / ASIO tabs (rc.2 was WASAPI-only)
- Check for Updates now opens a progress window (was silently no-op in rc.2)
- Dialogs render on the monitor where your cursor is (was always primary monitor)
- All dialogs now draggable by title bar (rc.2 dialogs were stuck where they spawned)
- Tray left-click opens menu (was no-op in rc.2)
- OBS browser source integration works without requiring OBS WebSocket plugin or any user setup
- OBS overlay toggle ON/OFF in tray correctly persists across tray restart (rc.2-era bug where toggle silently failed)
- MSI uninstall while OBS is open completes cleanly (cleanup binary handles deferred removal after OBS exit)

### Visual rebuild
- Brand-new design system: dark theme, brand-purple accents, custom rounded chrome, deep shadows
- Animated brand-purple accent bar at top of every dialog (respects reduced-motion)
- First-run Welcome window with animated waveform and clear feature copy
- Audio Source dialog: tabs + brand-purple selection accent + auto-persist + Reset
- Platforms dialog: two-column layout + live now-playing card + brand-tinted icons
- Setup Wizard: 4-step breadcrumbed flow + slide-fade transitions
- Error Dialog: calm brand-purple "i" icon (never red), optional details collapse
- Update Progress: 7 styled states
- Tray context menu: rounded corners, brand wordmark header, now-playing subline, icons left, check icons right for toggleables
- Full reduced-motion compliance via Windows accessibility setting

### Latency reductions
- Track-change detection: ~2-3s in rc.2 -> ~1s in rc.3
- Heartbeat cadence 2s -> 1s
- SMTC drain 250ms -> 100ms
- Parallel album-art prefetch on track-change

### Internal architecture
- Migration from PowerShell+WinForms tray to WPF+WPF-UI+CommunityToolkit.Mvvm tray (.NET 8)
- Process tree management via Job Objects with KILL_ON_JOB_CLOSE
- Code-signed binaries throughout
- IObsService extended with file-edit-only OBS integration

## Memory footprint

Master's FM v14 at steady state typically uses:
- **Server (`server.exe`):** ~80-130 MB
- **Tray (`MastersFM_Tray.exe`):** ~180-300 MB
- **Spectrum analyzer (`audio_spectrum.exe`):** ~45-65 MB

Total: ~300-500 MB across all processes. Most of this is the WPF tray (which is
larger than the v12 tray due to the design system rebuild and WPF's baseline cost).
The server is significantly lighter than v12's Node.js server in steady state.

Memory will spike briefly during JIT warmup on first launch and during album-art
fetches; the figures above are post-warmup steady-state.

## Known issues

- ASIO devices in Audio Source dialog show informational message; ASIO is not enumerable via Windows APIs (configure ASIO devices directly in your DAW)
- KS (Kernel Streaming) tab may show empty list on some systems (KS enumeration deferred)
- Dialog cursor-following placement only works on multi-monitor setups; single-monitor falls back to screen center
- The cleanup binary's self-delete may fail on rare Windows configurations (leaves a 1MB harmless exe at %PROGRAMDATA%\MastersFM\Cleanup\; safe to delete manually)
- Customize Overlay (server-side) and overlay.html have not received the visual rebuild treatment in rc.3; planned for rc.4 or v14.1.0

## Tester focus areas

Please test and report:
1. **Installation flow:** does the MSI install cleanly? Does the Welcome window appear once on first launch?
2. **Music detection:** does Master's FM detect what you're playing on SoundCloud / Spotify / YouTube Music / VLC / Apple Music / Tidal? How quickly does it react to skip / pause / resume?
3. **OBS integration:** does the overlay appear in your scene? Can you toggle it off cleanly?
4. **Visual quality:** do the dialogs look right on your display? Anything feel off (spacing, colors, animations)?
5. **Reduced-motion:** if you have Windows accessibility motion-reduction enabled, does Master's FM respect it?
6. **Latency:** how does music detection feel compared to before?

Report bugs at: https://github.com/MasterShadex/Masters-FM/issues

## Upgrade from rc.2

If you tested rc.2 (the rolled-back release): just install rc.3 over the top. Settings + OBS configuration migrate automatically. The first-run Welcome will not re-appear (your prior `first_run_shown` setting is preserved).

## Upgrade from v12.0.1

The Master's FM tray rewrote itself from PowerShell+WinForms to a native WPF app. Your existing v12.0.1 install can be uninstalled before installing rc.3, or rc.3 will install over the top (the MSI handles upgrade).
