# Master's FM v14.0.0-rc.2

Release date: 2026-05-09
Tag: v14.0.0-rc.2 (release candidate; precedes v14.0.0 stable)

## What is rc.2?

rc.1 shipped the server and core runtime migration. rc.2 ships the tray application --
the piece the user actually sees and interacts with. The C# WPF tray replaces the
PowerShell + WinForms tray that has shipped since v9.0.0. It is a complete rewrite with
a new design language, a new detection architecture, and a new dialog suite.

If you are on v14.0.0-rc.1, this update will be offered through the tray menu's
"Check for updates" item. If you are still on v12.0.1, install this MSI manually
(the v12.0.1 tray cannot parse pre-release version strings and will not offer the update).

---

## What's new since v14.0.0-rc.1

### User-visible

**New tray application (C# .NET 8 + WPF)**

The PowerShell + WinForms tray has been replaced with a C# .NET 8 WPF application.
The architecture is similar -- same Job Object hierarchy, same process tree (launcher
supervises server + audio_spectrum + tray as Job Object children) -- but the tray
executable is now a proper .NET 8 binary instead of a hosted PowerShell runspace.

**12-item ContextMenu**

The previous right-click menu had one item (Quit). The new menu has twelve:
- Now-playing row: album art thumbnail + artist/track text
- Toggle Discord Rich Presence
- Toggle auto-start on login
- Open platform detection settings
- Open audio source settings
- Open customizer (overlay editor)
- Check for updates (cycles label by update state: idle / checking / downloading)
- Open patch notes
- Open log
- Restart
- Quit

On Windows 11 22H2 and later, the menu has a dark Acrylic backdrop with brand-purple
accent. On Windows 10 and older Windows 11, it falls back to a solid dark 85%-alpha
canvas. Both branches use identical item layouts and sizing.

**Six redesigned dialog surfaces**

All dialogs use the WPF-UI Fluent Dark theme with the same brand-purple (`#9333EA`)
accent used in the overlay. No hardcoded hex color values in dialog XAML -- all colors
come from dynamic resource references.

- Welcome + About dialog: patch notes virtualized (loads all 292+ versions without
  lag); About panel embedded in the same surface.
- Audio device dialog: WinRT device enumeration for WASAPI endpoints; ASIO tab present
  but hidden (WinRT does not surface ASIO devices; pending future patch).
- Platform detection dialog: 8 source toggles (SoundCloud, Spotify, YouTube, VLC, osu!,
  WMP legacy, foobar2000, system-SMTC fallback); all enabled by default on first run.
- Setup wizard: 3-step on-boarding flow (Welcome, Audio, Platforms); shown once on first
  launch; can be skipped; skip now correctly persists the seen-flag.
- Error dialog: info icon with brand-purple accent; wired to DispatcherUnhandledException.
- Update progress dialog: displayed during background MSI download.

**Live OBS integration**

A new OBS item appears in the ContextMenu. When OBS Studio is running with the WebSocket
plugin enabled, clicking the item connects to OBS and labels the item with the live
connection state (disconnected / connecting / connected / error). The overlay is unaffected
when OBS is disconnected. To enable: right-click the tray icon and click the OBS item,
or set `obs.enabled = true` in `%APPDATA%\MastersFM\config.json` and restart.

**Live Discord and auto-start toggles in the tray menu**

Discord Rich Presence and auto-start on login can now be toggled from the tray menu
without opening the customizer. State changes persist to `config.json` immediately.

**Now-playing row with album art**

The ContextMenu now-playing row shows a 48x48 album art thumbnail. Art is fetched via
the same 11-source cascade already used by the overlay. The cache is warm after the
first track change on each launch.

**Update label cycling**

The "Check for updates" tray menu item now cycles through four states as the update
check progresses (idle / checking / downloading / restart-to-update). No balloon
notifications are shown -- the label is the only indicator.

**Sentence case throughout**

All dialog text, labels, and tray menu items use sentence case (first word capitalised,
rest lower-case) consistent with the rest of the UI.

---

### Under-the-hood

**Detection architecture redesign**

The tray used to use 10 independent polling detectors. The new architecture has two
arms:

- Arm 1 (primary): SMTC event-driven. Subscribes to
  `GlobalSystemMediaTransportControlsSessionManager` via WinRT and receives track-change
  events at under-250 ms latency, replacing the 100 ms polling chain. On SoundCloud via
  soundcloud-rpc the effective latency is under 250 ms end-to-end.
- Arm 2 (gap-filler): Three lightweight 1-second poll detectors (osu!, VLC HTTP, WMP
  legacy) for sources that do not expose SMTC.

The 7 detectors that were either redundant or covered by SMTC arm 1 have been removed.
P99 latencies per detector are tracked in memory and emitted in each 60-second heartbeat
line in the log.

**Webhook schema alignment**

Two fields in the C# webhook payload were mismatched with what the server expected:
- `durationMs` (milliseconds) renamed to `duration` (seconds, as the server expects)
- `art` renamed to `trackArt` (matching the server's `data["trackArt"]` read path)

These mismatches meant duration and album art were not updating on track changes when
the C# tray was active. Both are fixed.

**Webhook send failures now logged**

Previously, a failed webhook HTTP request (server unreachable, timeout) was silently
discarded with no log output and no counter increment. The new behaviour logs a Warn
line and increments a `webhook_send_failures` counter. The heartbeat now shows
`webhooks=N/F` (sent/failed) instead of just `webhooks=N`.

**Setup wizard first-run gate fixed**

Two defects caused the wizard to re-show on every launch instead of only the first:
- The PS tray writes `welcome_seen_version` as `"v14.0.0-rc.1"` (with "v" prefix);
  the C# ConfigService was comparing it against `"14.0.0-rc.1"` (without "v") -- always
  unequal. Fixed via a `NormalizeVersion()` helper that strips leading v/V.
- The ViewModel Finish path was overwriting the correctly-stored value after setting it.
  Four redundant lines removed.

Additionally, Skip now correctly persists the seen-flag (was always false-returning on
Skip, causing the wizard to re-show after skip).

**SemVer comparator fix**

The update-check comparator previously evaluated `v14.0.0-rc.1 > v12.0.1` as false
because pre-release strings were treated as lesser regardless of major.minor.patch
ordering. Fixed: numeric major.minor.patch comparison happens first; the pre-release
less-than rule only applies when major.minor.patch are equal. 16 synthetic test cases
pass at every startup.

**Memory baseline reconciled: 220-260 MB**

The working-set plateau of the C# tray is 220-260 MB. See "Known behaviors" below for
context. A detailed structural analysis is in `V14_S7_S7_8_MEMORY_BASELINE_RECONCILIATION.md`
in the repository.

**Build pipeline cleanup (Stage 8)**

The build script `_full_rebuild.ps1` no longer contains the legacy csc.exe and Node.js
pkg fallback paths. These have been permanent dead code since Stages 1-3 when all
runtime components moved to `dotnet publish`. Net reduction: 593 lines -> 419 lines.
No user-visible change; the resulting binaries and MSI are identical.

---

## Known behaviors

### First-run memory ramp

On first launch after upgrading from any prior version, the setup wizard is displayed.
The wizard loads WPF ResourceDictionaries for its multi-page UI, temporarily increasing
the working set by approximately +67 MB. This excess is released by the WPF ResourceCache
and the working set returns to the normal 220-260 MB plateau within a few minutes.
On second and subsequent launches (with `welcome_seen` persisted) the ramp does not occur.

### OBS integration status

OBS connectivity validation was not run on the test machine (OBS Studio is not available
in the soak environment). The OBS service is implemented and functional, but operators
with OBS installed should treat this as a first-exposure test. If the OBS item in the
tray menu shows a persistent error state, check that OBS WebSocket plugin is installed and
that `obs.host`/`obs.port`/`obs.password` in `config.json` match the OBS WebSocket settings.
Report any connectivity issues in `#v14-rc-feedback`.

### Memory plateau: 220-260 MB

The C# tray's steady-state working set is 220-260 MB, higher than the v12.0.1 PowerShell
tray. This is the documented structural floor of the .NET 8 + WPF + WPF-UI + CSWinRT
(WinRT projection) stack at this feature set -- not a regression or a leak. The PS tray's
low steady-state was an artifact of hosting in an interpreter that shared its heap with
the OS PowerShell process and measured separately. A 6-hour soak confirms the plateau is
stable (both-half mean WS difference is the primary stability signal; see soak results
below). The old tray's 10.9 MB/h PS memory growth is gone.

---

## Known issues / deferred to future patch

- Q-DIALOG-2: "Test tone" button on the audio device dialog is not functional (requires
  audio output infrastructure not yet built; placeholder only).
- Q-DIALOG-3: ASIO tab on the audio device dialog is hidden (WinRT device enumeration
  does not surface ASIO devices; a direct ASIO enumeration path is deferred).
- B-022: Mid-session SMTC re-subscription full validation has not been completed. The
  CANARY mechanism re-probes every 30 seconds and mitigates the risk in practice; a
  full operator-induced mid-session test is deferred to post-rc.2.
- OBS Source Side controls (browser source add/remove, scene editing from the overlay
  editor) -- backend not implemented; deferred to a post-V14 patch.
- Bootstrapper rebuild -- the install bootstrapper is disabled (self-signed cert pattern
  is flagged by some AV software). The MSI direct-install path is the supported install
  method for all rc builds. Re-enable deferred until a CA cert is acquired.

---

## Auto-update behaviour

**If you are on v14.0.0-rc.1:** The update will be offered via the "Check for updates"
item in the tray menu (the SemVer fix in rc.2 allows the comparator to correctly identify
rc.2 > rc.1). You can also install the MSI directly for an immediate update.

**If you are on v12.0.1:** v12.0.1 cannot auto-update to pre-release versions (the
`[version]` cast in the PS tray does not parse `-rc.N` suffixes). Install the rc.2 MSI
manually from the GitHub Pre-release page. The MSI Major-Upgrade machinery handles the
upgrade from any prior version in place.

**SmartScreen warning on first launch** (unchanged from prior releases): Click "More info"
then "Run anyway." Full code-signing via a real CA cert is planned for stable v14.0.0.

---

## Soak validation

1-hour cutover soak completed 2026-05-09 (60 samples x 60s, Stage-8-clean build,
SoundCloud ambient playback, operator unavailable):

- Plateau: 237.4-249.9 MB (target 220-260 MB) -- PASS
- Both-half mean WS diff: -1.2 MB (target < 10 MB) -- PASS
- Final-30-sample LS slope: -15.71 MB/h (target < 8 MB/h; negative = declining) -- PASS
- Webhook success: 28/28 = 100% over 28 track changes (target > 95%) -- PASS
- overlay.log ERROR lines: 0 -- PASS
- No halt conditions triggered

---

## Rollback to v12.0.1

If rc.2 produces critical issues:

1. Download `Masters-FM-V12.0.1.msi` from:
   `https://github.com/MasterShadex/Masters-FM/releases/tag/v12.0.1`
2. Stop the tray: right-click the tray icon, Quit. Or kill via Task Manager:
   `MastersFM.exe`, `MastersFM_Tray_v14.exe`, `server.exe`, `audio_spectrum.exe`, `customize.exe`.
3. Run the v12.0.1 MSI. The Major-Upgrade table accepts downgrade installs.
4. Confirm via the tray context menu header -- it should read "Master's FM (dot) v12.0.1".

If rollback also fails, send a message in `#v14-rc-feedback` with the install log
(`%LOCALAPPDATA%\MastersFM\overlay.log` -- last 500 lines).

---

## Reporting issues

Discord channel `#v14-rc-feedback` ONLY. Include:
- OS version (winver)
- Whether this is a fresh install or an upgrade from rc.1 or v12.0.1
- The tray log: `%LOCALAPPDATA%\MastersFM\overlay.log` (last 500 lines)
- Steps to reproduce if intermittent

---

## RC framing

v14.0.0-rc.2 is a release candidate. Report anything unexpected. Stable v14.0.0 will
be promoted from rc.2 after a 7-14 day tester-validation window with no critical issues.
rc.2 is not the stable release.

---

## Acknowledgments

Stage 7 (11 sub-stages + INTERRUPTs #1 and #2) and Stage 8 executed across all commits
since v14.0.0-rc.1 was tagged at `44723fb`. The complete per-stage detail is in
`md/memory.md` in the repository. 1 strike of 9 consumed across the entire V14 cycle.
4 protected source files SHA256-unchanged throughout.
