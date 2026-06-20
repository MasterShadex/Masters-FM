# Master's FM

A polished now-playing overlay for **OBS** and a rich **Discord Rich Presence** for whatever
you're listening to on Windows — Spotify, YouTube Music, Apple Music, Tidal, browser players,
local files, anything that talks to Windows' built-in media controls.

It runs from your system tray, watches what's playing, and ships the track + cover art to:
- A **transparent overlay** you can drop straight into OBS as a Browser Source — comes with a
  built-in spectrum visualizer that reacts to your system audio in real time
- **Discord Rich Presence** so your friends see exactly what's playing, with album art

Built for streamers, content creators, and people who just want their setup to look good.

---

## What you get

**Now-playing overlay for OBS**
- Drag-and-drop the URL into a Browser Source — done
- Live track title, artist, album, cover art, and elapsed/total time
- Spectrum bars driven by real WASAPI loopback (your actual system audio, not a fake animation)
- 22 built-in themes, ~150 fine-grained controls in the customize panel, full preset import/export
- Smooth crossfade animations on track changes — choose what fades (everything, just info, or just art)

**Discord Rich Presence**
- Sets your status to "Listening to {track} by {artist}" with album art
- Updates in real time as your music changes
- Works with anything that talks to Windows' SMTC (Spotify, Tidal, YouTube Music, browsers, local players)

**System tray app**
- Lives quietly in your tray; click for the menu
- Customize Overlay button opens the full settings panel in a window
- One-click "Copy Overlay URL" for OBS
- Auto-update with signed installer (verified against a pinned Authenticode cert)

---

## Quick start

1. **Download** the latest signed installer from the [Releases page](../../releases/latest) —
   look for `Masters-FM-Vx.x.x.msi`.
2. **Run it.** Windows will SmartScreen-warn the first time (no EV cert yet); click
   *More info → Run anyway*. The MSI installs Master's FM and a tray launcher; nothing
   else is touched.
3. **Open the tray menu** → *Copy Overlay URL* → paste that URL as a **Browser Source** in OBS.
4. **Open Discord** — your status will start updating as soon as music plays.
5. **Tray menu → Customize Overlay** → tweak themes, sliders, animations to taste.

Updates happen automatically: the tray checks for new releases on launch and hourly. When
one's available you get a notification and a one-click install. Friends with v11 or v12
get a major-upgrade-clean install (the new version uninstalls the old one cleanly).

---

## Requirements

- **Windows 10/11** — uses Windows Media Transport Controls (SMTC), so any source that
  registers with Windows' media keys works automatically
- **Default audio device set in Windows** — the spectrum bars sample from your system's
  default render endpoint (WASAPI loopback); the overlay also supports input devices and
  virtual mixers (Sonar, Voicemeeter, VB-Cable) when chosen explicitly in the tray
- **No .NET install required** — the installer bundles a self-contained .NET 8 runtime

---

## How it's built

| Component | Tech |
|---|---|
| System tray + customize window | C# / WPF on .NET 8 (CommunityToolkit.Mvvm) |
| Now-playing server (port 4242) | C# / ASP.NET Core (.NET 8), Server-Sent Events |
| Audio spectrum engine (port 4243) | C# / NAudio (WASAPI/MME/ASIO/WDM-KS), FFT band-mapping |
| Overlay + customize UI | HTML/CSS/JS (vanilla, runs in OBS's CEF / WebView2) |
| Auto-updater | C# in-tray, GitHub Releases API + pinned Authenticode verification |
| Installer | Python build orchestrator → MakeCab → MSI database (Wix-free) |

Architecture: tray and customize window own settings + lifecycle; server is a transport
between SMTC events and the overlay; audio engine is a separate process so audio glitches
can never block the UI thread; updater is pinned to a specific code-signing cert so a
compromised repo can't ship a malicious update.

---

## Privacy

Master's FM does not collect telemetry. The tray talks to GitHub for the auto-updater
manifest and downloads MSIs only when *you* click update. The overlay and Rich Presence
data never leave your machine. There's no account, no signup, nothing phones home.

---

## Building from source

You'll only want to do this if you're hacking on it. The end-user path is the signed MSI
on the Releases page.

```powershell
# Requires .NET 8 SDK, Python 3.10+, and Windows 10 SDK signtool
.\_full_rebuild.ps1
```

This builds `audio_spectrum.exe`, the WPF tray + customize window, and the server, then
packages everything into a self-contained MSI in `Master's FM Install\`. Signing requires
the code-signing certificate (operator-only).

---

## Licence

Currently source-available. Use it, fork it for your own setup. A formal OSS licence will
be added once the v14 line stabilizes.

---

*Made by [MasterShadex](https://github.com/MasterShadex).*
