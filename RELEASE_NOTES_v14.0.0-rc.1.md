# Master's FM v14.0.0-rc.1 (Release Candidate)

## The pause

Testers have been on v12.0.1 for an extended period while the V14 .NET 8 migration was in
flight. The pause was deliberate: v14 is a major architectural migration, not a feature
release, and shipping it half-finished would have been worse than shipping nothing. This is
the first build that bundles all the migration work into a single shippable cumulative
release.

## What changed (high-level)

This is a cumulative ship of Stages 1-3 + Stage 4 + Stage 5 MINIMAL. Stages 6-8 are deferred.

- **Server runtime: Node.js -> ASP.NET Core (.NET 8).**
  `server.exe` is now an ASP.NET Core minimal-API binary built via `dotnet publish` with R2R.
  All 26 routes ported with byte-equivalent HTTP behaviour modulo documented intentional
  differences. Process tree differs (no embedded Node), memory profile differs (lower steady
  state), startup is faster (~30-50% R2R pre-JIT).
- **Discord RPC: custom JS -> Lachee.DiscordRPC (C#).**
  New `DiscordRpcService` + `DiscordRpcThrottle` (2000ms throttle, latest-wins coalescer)
  replaces the JS implementation. Behaviour mirrors v12.0.1 from the Discord-user perspective
  (paused/playing states, large/small images, Listen button). Built on Lachee.DiscordRPC
  1.2.1.24 NuGet -- the only new runtime dependency in V14.
- **Album art cascade: entirely rewritten in C#.**
  Full 11-source cascade ported with per-source rate limits and concurrency bounds preserved.
  enrichByTitle Deezer/iTunes search now runs before lock; art and duration resolve
  concurrently via Task.WhenAll. May find art for tracks the legacy server could not.
- **Launcher, audio_spectrum, customize, tray_native: all migrated to .NET 8.**
  Launcher and audio_spectrum target `net8.0-windows` (R2R, framework-dependent x64).
  Customize uses `net8.0-windows` with the existing WebView2 NuGet 1.0.2420.47 dependency.
  tray_native targets `netstandard2.0` (loadable by Windows PowerShell 5.1, future-PS7-ready).
- **Hybrid build: install_bootstrapper remains on csc.exe.**
  RC1 ships hybrid -- .NET 8 for the runtime stack (launcher/server/audio_spectrum/customize/
  tray_native); legacy csc.exe path preserved for install_bootstrapper. Re-enable of the
  .NET 8 bootstrapper requires (a) a real CA cert (Bitdefender flags the self-signed pattern)
  and (b) EmbeddedResource items for payload.msi + publisher.cer in the csproj. Neither is in
  place for RC1; deferred to a future stable release.
- **tray.ps1 unchanged.**
  Only the version string and a new patch-note entry were edited. The tray host (csc.exe
  built MastersFM_Tray.exe from tray_launcher.cs) is unchanged. Stage 6 (tray_launcher
  dissolution) is folded into Stage 7's cutover per the V14 plan -- no work happens in this
  release.

## Intentional differences from legacy (consolidated ID-1 through ID-35)

These are documented behaviour deltas vs the v12.0.1 Node.js server. None are user-visible
unless you are reading HTTP traces.

- **ID-1:** Content-Length (Kestrel) vs Transfer-Encoding: chunked (Node) on most response shapes.
- **ID-2:** No `Keep-Alive: timeout=5` header (Kestrel handles keep-alive implicitly).
- **ID-3:** /current returns null until first webhook (was already the v12.0.1 behaviour).
- **ID-4:** bootId differs per restart (by design).
- **ID-5:** /update-status `ts` field reflects live data.
- **ID-6:** SSE frames use CRLF line endings (Kestrel default; valid per SSE spec).
- **ID-7:** Art cascade stubs were filled in (4.9b-e); webhook now invokes the real cascade.
- **ID-8:** Discord RPC port complete in 4.10 (was a stub in 4.4).
- **ID-9:** Artist enrichment via enrichByTitle (Deezer/iTunes search) now runs on every webhook
  with placeholder artist BEFORE lock acquisition (previously folded into setTrack).
- **ID-10:** /webhook response body "OK" text/plain (raw bytes, no Content-Type).
- **ID-11:** /save-overlay-config is straight replacement of cfg.overlay (matches v12.0.1).
- **ID-12:** /reload-config Discord RPC stub removed; now invokes real Discord reload.
- **ID-13:** No config_default.json merge at GET /overlay-config time.
- **ID-14:** /art streams via ResponseHeadersRead + CopyToAsync (was Buffer in Node).
- **ID-15:** Concurrent /screenshot returns 409 instead of silent replace (resource leak fix).
- **ID-16:** HttpsGetAsync absorbs errors into string.Empty (server.js threw; call sites catch).
- **ID-17:** musicbrainz HttpClient uses MastersFM/1.7 UA (was Mozilla/5.0).
- **ID-18:** Cascade passes pre-cleaned artist/track to API sources.
- **ID-19:** SC/osu placeholders in 4.9b+c are CDN passthrough (full search in 4.9d).
- **ID-20:** LruCache(200) added (not in server.js).
- **ID-21:** DurationResolver is standalone (not tied to currentTrack state).
- **ID-22:** SmtcFallbackSource added in 4.9d (covers non-browser data: URI fallback).
- **ID-23:** SoundCloudOembedSource uses public oEmbed (no client_id needed).
- **ID-24:** OsuScraperSource replaces the OsuDirectSource placeholder.
- **ID-25:** Bing 1.2s deadline via CancellationTokenSource (vs Promise.race in Node).
- **ID-26:** YouTubeSource reuses "bing" named client UA (already-existing browser UA).
- **ID-27:** SC client_id regex uses [a-zA-Z0-9] (server.js authoritative).
- **ID-28-31:** enrichByTitle wiring details (4.9e).
- **ID-32:** Discord ActivityType not set (was always ignored by Discord).
- **ID-33:** 30s reconnect loop (was 5s/10s escalation).
- **ID-34:** _lastSig clears only on READY+reload (30s age-refresh self-heal otherwise).
- **ID-35:** pendingPresence flushed through throttle (was synchronous).

## Known issues (not regressions; pre-existing)

- **Tray memory grows ~10.9 MB/h** during long sessions. Documented in `open_issues.md` with
  the noted root fix in Stage 7 (tray.ps1 -> C# port). Not a regression vs v12.0.1.
- **install_bootstrapper.exe self-signed pattern** is flagged by some AV (Bitdefender). The
  RC1 install path uses the .msi directly, not the bootstrapper, so this does not affect RC1
  testers. Re-enable of the bootstrapper is deferred until a CA cert is acquired.
- **Auto-update from RC1 to stable v14.0.0:** RC1 testers will need to download stable v14.0.0
  manually from the Releases page; the current `tray.ps1` `[version]` cast cannot parse
  pre-release suffixes, so the auto-update path silently no-ops on RC1 -> RC2 / RC1 -> stable
  transitions. This is patched in stable v14.0.0 ship (a Stage-7-class fix). For RC1 itself,
  this is incidentally the very mechanism that prevents v12.0.1 testers from auto-updating to
  the RC -- it is RC framing's protective property.
- **First-launch memory after upgrade from v12.x (C# tray):** On first launch after upgrading from v12.x, the setup wizard temporarily increases memory by ~70 MB. This settles back to ~250 MB on the next launch and is normal.

## RC framing

This is a release candidate. Please report anything unexpected, no matter how minor. A stable
v14.0.0 will follow once feedback is incorporated. RC1 captures the cumulative state of all
.NET 8 migration work to date except Stage 6 (tray_launcher dissolution, folded into Stage 7
cutover) and Stage 7 (tray.ps1 to C# rewrite, the largest remaining stage at ~600h estimated).

**v12.0.1 will NOT prompt you to update.** This is intentional. RC1 is **manual install only**
to keep tester opt-in explicit. The auto-update channel for v12.0.1 stays pointed at v12.0.1
itself; no auto-update notification will appear in the tray, and the install will not happen
silently. To install RC1 you have to download the installer from the GitHub Pre-release page
linked below and run it; it will replace v12.0.1 in place via the MSI Major-Upgrade machinery.

**SmartScreen warning on first launch.** Windows may show a SmartScreen warning when first
launching MastersFM. Click "More info" then "Run anyway". This is unchanged from v12.0.1;
full code-signing (a real CA cert) is planned for stable v14.0.0.

## How to report issues

Discord channel `#v14-rc-feedback` ONLY. Do NOT use GitHub Issues. Do NOT use the general
Discord chat. One channel, one place. Orken is creating the channel before posting the
announcement.

## Rollback to v12.0.1

If RC1 breaks for you, the legacy v12.0.1 install can be re-applied:

1. Download `Masters-FM-V12.0.1.msi` from the v12.0.1 GitHub release page:
   `https://github.com/MasterShadex/Masters-FM/releases/tag/v12.0.1`
2. Stop tray and server: right-click the tray icon, choose Quit. Or kill via Task Manager:
   `MastersFM.exe`, `MastersFM_Tray.exe`, `server.exe`, `audio_spectrum.exe`, `customize.exe`.
3. Run the v12.0.1 .msi. It performs an in-place downgrade because the MSI Major-Upgrade table
   accepts older `ProductVersion` values too. After install, the tray icon should reappear at
   v12.0.1 within ~5 seconds.
4. Confirm via the tray context menu header text -- it should read "Master's FM (dot) v12.0.1".

If the rollback also fails, send a message in `#v14-rc-feedback` with the install log
(`%LOCALAPPDATA%\MastersFM\transcript.log` -- attach the last ~500 lines).

## Deferred to v14.0.0 stable / future

- **Stage 6 (tray_launcher dissolution):** folded into Stage 7's cutover commit. Per V14 plan,
  0 hours of independent effort. Phase 1 finding documented in `V14_S6_P1_FINAL_REPORT.md`.
- **Stage 7 (tray.ps1 -> C# .NET 8 application):** the largest remaining stage. Estimated
  400-700h. Pending RC1 tester feedback before scheduling.
- **Stage 8 (build pipeline cleanup):** end-of-V14 polish. Includes csproj AssemblyInfo
  embedding for cleaner version metadata in binary properties, deletion of dead build scripts
  (`build_tools/ps2exe/_build_tray.ps1`, etc.), and re-pathing of dev scripts that hard-code
  `C:\Users\Master\` paths.
- **install_bootstrapper.exe to .NET 8:** pending real CA cert acquisition (Certum or
  equivalent) and EmbeddedResource items for payload.msi + publisher.cer in the csproj.
- **Auto-update path support for SemVer pre-release versions:** patch in stable v14.0.0 to
  handle `-rc.N` / `-beta.N` suffixes in the `[version]` cast or via a custom comparator.
