# Tester announcement -- v14.0.0-rc.1

(Discord-formatted, Markdown supported. Posted in `#v14-rc-feedback`.)

---

Hey all -- thanks for hanging in there.

You've been on v12.0.1 for a while. Reason: the V14 .NET 8 migration is a big architectural
shift (the entire server backend went from Node.js to ASP.NET Core, plus four other binaries
moved to .NET 8). Shipping it incrementally would have meant testers handling broken in-flight
state every couple of weeks. Instead I bundled everything and held until it was cohesive.

This is **v14.0.0-rc.1 -- a release candidate**, not a stable release. It works on my machine
across a 49-minute listening session and a 6-hour overnight soak, but the real test is your
machines and your daily workflows. I expect rough edges. Please report anything that feels
different or off.

### What it actually changes

Almost nothing user-visible. The pieces under the hood are different (.NET 8 instead of
Node.js for the server; Lachee.DiscordRPC instead of custom JS for Discord; full art cascade
rewritten in C#) but the overlay, customize, tray menu, and Discord-side experience should be
identical to v12.0.1 modulo small things you might notice (faster startup, slightly different
process tree in Task Manager, art may resolve for some tracks the old server missed).

### Install (manual download only)

**v12.0.1 will NOT prompt you to update.** This is intentional -- RC1 is manual install only.
Download the installer from the GitHub link below and run it; it will replace v12.0.1 in
place via the MSI Major-Upgrade machinery (no manual uninstall needed).

**https://github.com/MasterShadex/Masters-FM/releases/tag/v14.0.0-rc.1**

The `.msi` is signed by `CN=MasterShadex` (same self-signed cert as v12.0.1). After install
the tray menu header will read `v14.0.0-rc.1`. If it still says `v12.0.1`, the install did
not take; close the tray and run the installer again.

**Windows may show a SmartScreen warning when first launching MastersFM. Click "More info"
then "Run anyway". This is unchanged from v12.0.1; full code-signing is planned for stable
v14.0.0.**

### What to test

Just use it normally. Listen to music for a while. Open the tray menu, fiddle with customize,
restart the app a couple of times. If something feels different, write it down. Specifically:

- Discord status updates (paused, playing, art, Listen button)
- Album art appearing for tracks (especially SoundCloud, osu!, YouTube)
- Tray balloon notifications when tracks change
- The overlay rendering (no broken layouts, no missing fonts, no console errors in F12)
- Customize window opening, presets saving, and persisting across restarts
- Memory growth over a long session (Task Manager: `MastersFM.exe` + `server.exe` +
  `MastersFM_Tray.exe` + `audio_spectrum.exe`)

### Reporting

`#v14-rc-feedback` channel here on Discord. ONLY here. Do not use GitHub Issues. Include:
- What you saw
- What you expected
- The version (right-click tray, look at the menu header -- it should say `v14.0.0-rc.1`)
- If it's a hang or crash, the last ~200 lines from `%LOCALAPPDATA%\MastersFM\transcript.log`

### Rollback if RC1 breaks

If RC1 doesn't work for you and you need to go back:

1. Download `Masters-FM-V12.0.1.msi` from the v12.0.1 release:
   https://github.com/MasterShadex/Masters-FM/releases/tag/v12.0.1
2. Quit the tray (right-click tray, Quit) or kill via Task Manager.
3. Run the v12.0.1 .msi. It downgrades in place.
4. Confirm the tray menu now reads `v12.0.1`.

If rollback also breaks: ping me in `#v14-rc-feedback` with your transcript.log.

### Heads up: I'm on vacation

Hotfix turnaround is hours, not minutes. If you see something urgent, post it in
`#v14-rc-feedback` and I'll see it when I'm online. If a tester needs RC1 immediately
reverted on their machine, the rollback steps above are reliable -- I have run them several
times during V14 development.

Thanks for testing. The fact that v12.0.1 has been stable enough to leave alone for weeks
while V14 came together is the highest compliment I can give the codebase right now.
