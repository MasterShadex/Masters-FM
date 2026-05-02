const http = require('http');
const https = require('https');
const fs = require('fs');
const path = require('path');

const PORT = 4242;

// ─── DIRECT FILE LOGGER (bypasses stdout buffering entirely) ──────────
// Writes straight to disk so logs are always visible even in pkg binaries.
const LOG_DIR  = process.cwd();          // tray.ps1 sets WorkingDirectory = install dir
const LOG_FILE = path.join(LOG_DIR, 'server.log');
function flog(msg) {
  const line = `[${new Date().toISOString()}] ${msg}\n`;
  try { fs.appendFileSync(LOG_FILE, line); } catch {}
  process.stdout.write(line);            // also to stdout (best-effort)
}
// Truncate log on each startup so it doesn't grow forever
try { fs.writeFileSync(LOG_FILE, `=== server started ${new Date().toISOString()} ===\n`); } catch {}

// Server boot timestamp — used by the overlay's version check to auto-reload
// after Master's FM restarts. CEF (OBS Browser Source) caches overlay.html
// aggressively; the overlay polls /version and when it sees a new SERVER_BOOT_ID
// it calls location.reload(true) to force a full re-fetch. This is how the
// overlay picks up new code and fresh art after the app is restarted.
// v6.1.2 — let-binding so /obs-reset-position can bump it on demand,
// forcing connected overlays (OBS Browser Source, customize preview)
// to reload and pick up the latest config cleanly. Useful when the user
// has changed things and wants a guaranteed-fresh render in OBS.
let SERVER_BOOT_ID = Date.now();

// ─── GLOBAL ERROR NETS (last line of defense) ─────────────────────────
// Without these the Node VM would print to stderr and exit. The tray would
// happily keep ticking and posting to a dead :4242, and the user would just
// see "overlay stopped working" with no explanation. Log EVERYTHING.
process.on('uncaughtException', (err, origin) => {
  try {
    flog(`!! uncaughtException origin=${origin} msg=${err && err.message}`);
    if (err && err.stack) flog(`   stack:\n${err.stack}`);
  } catch {}
  // Intentionally do NOT exit — the overlay/SSE loop is self-healing and
  // exiting here would drop every connected client. The stack tells us
  // where the bug is so we can fix it in the next patch.
});
process.on('unhandledRejection', (reason, promise) => {
  try {
    const msg = reason && reason.message ? reason.message : String(reason);
    flog(`!! unhandledRejection: ${msg}`);
    if (reason && reason.stack) flog(`   stack:\n${reason.stack}`);
  } catch {}
});
process.on('warning', w => { try { flog(`Node warning [${w.name}]: ${w.message}`); } catch {} });
// ──────────────────────────────────────────────────────────────────────

// ─── CONFIG ────────────────────────────────────────────

flog(`process.cwd()      = ${process.cwd()}`);
flog(`process.execPath   = ${process.execPath}`);
flog(`__dirname          = ${__dirname}`);

// ── Parent-process (launcher) liveness guard ───────────────────────────────
// Primary kill mechanism: Windows Job Object (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)
// in MastersFM.exe ensures we die when the launcher is killed. This is a
// belt-and-suspenders backup: if the Job Object ever fails (e.g. already-in-job
// race on first spawn) we detect the parent's death ourselves and self-exit.
{
  const _launcherPid = process.ppid;
  if (_launcherPid && _launcherPid > 1) {
    flog(`Launcher guard: monitoring parent PID=${_launcherPid}`);
    setInterval(() => {
      try {
        process.kill(_launcherPid, 0);  // signal 0 = existence check (works on Windows too)
      } catch (e) {
        if (e.code === 'ESRCH') {
          flog(`Launcher PID ${_launcherPid} has exited — server self-terminating`);
          process.exit(0);
        }
        // EPERM: process exists but we can't signal it — still alive, ignore
      }
    }, 2000);
  }
}

// ── User data directory: %APPDATA%\MastersFM\
// This is Roaming AppData — completely separate from the install folder
// (%LOCALAPPDATA%\MastersFM) so it is NEVER deleted by the uninstaller.
// Config and presets live here permanently across all installs/reinstalls.
function getUserDataDir() {
  const base = process.env.APPDATA || process.env.LOCALAPPDATA || process.cwd();
  const dir  = path.join(base, 'MastersFM');
  try { fs.mkdirSync(dir, { recursive: true }); } catch {}
  return dir;
}

function getPresetsDir() {
  const dir = path.join(getUserDataDir(), 'presets');
  try { fs.mkdirSync(dir, { recursive: true }); } catch {}
  return dir;
}

// Config lives in Roaming AppData — survives uninstall/reinstall forever.
function findConfigPath() {
  return path.join(getUserDataDir(), 'config.json');
}

// config_default.json ships with each release in the install folder.
function findDefaultConfigPath() {
  const candidates = [
    path.join(process.cwd(), 'config_default.json'),
    process.execPath ? path.join(path.dirname(process.execPath), 'config_default.json') : null,
    path.join(__dirname, 'config_default.json'),
  ].filter(Boolean);
  for (const p of candidates) {
    try { if (fs.statSync(p).isFile()) return p; } catch {}
  }
  return candidates[0];
}

// Deep-merge: user (target) values always win.
// Source (defaults) only fills in keys missing from target.
// Arrays are kept wholesale — never merged element-by-element.
function deepMergeConfig(target, source) {
  const out = Object.assign({}, target);
  for (const k of Object.keys(source)) {
    if (!(k in out)) {
      out[k] = source[k];  // new key in this release — use default
    } else if (
      source[k] !== null && typeof source[k] === 'object' && !Array.isArray(source[k]) &&
      target[k] !== null && typeof target[k] === 'object' && !Array.isArray(target[k])
    ) {
      out[k] = deepMergeConfig(target[k], source[k]);
    }
    // else: user value wins
  }
  return out;
}

// Runs on every server start.
// Ensures config.json exists in Roaming AppData and has all current keys.
// Migration priority: Roaming config → old LocalAppData config → old backup → fresh defaults.
function migrateConfig() {
  try {
    const configPath  = findConfigPath();   // Roaming\MastersFM\config.json
    const defaultPath = findDefaultConfigPath();

    let defaults = {};
    try {
      defaults = JSON.parse(fs.readFileSync(defaultPath, 'utf8').replace(/^\uFEFF/, ''));
    } catch {
      flog('migrateConfig: config_default.json not found — skipping');
      return;
    }

    let userCfg = {}, source = 'defaults';
    // 1. Try Roaming config (normal case after first run)
    try {
      userCfg = JSON.parse(fs.readFileSync(configPath, 'utf8').replace(/^\uFEFF/, ''));
      source  = 'existing roaming config';
    } catch {
      // 2. Try legacy LocalAppData config (user upgrading from old version)
      try {
        const legacy = path.join(process.env.LOCALAPPDATA || '', 'MastersFM', 'config.json');
        userCfg = JSON.parse(fs.readFileSync(legacy, 'utf8').replace(/^\uFEFF/, ''));
        source  = `legacy install-dir config (${legacy})`;
      } catch {
        // 3. Try the old VBS uninstall backup (belt-and-suspenders)
        try {
          const bak = path.join(process.env.APPDATA || '', 'MastersFM_config_save.json');
          userCfg = JSON.parse(fs.readFileSync(bak, 'utf8').replace(/^\uFEFF/, ''));
          try { fs.unlinkSync(bak); } catch {}
          source  = 'uninstall backup';
        } catch { /* genuinely fresh install */ }
      }
    }

    const merged = deepMergeConfig(userCfg, defaults);

    // (v9.3.0 renderer-migration log block removed in v9.4.0 — canvas2d is
    // gone, so the renderer field is irrelevant. Existing config files keep
    // their renderer key on disk; the overlay just ignores it.)

    fs.writeFileSync(configPath, JSON.stringify(merged, null, 2), 'utf8');
    flog(`migrateConfig: loaded from ${source} → saved to ${configPath}`);
  } catch (e) {
    flog(`migrateConfig error: ${e.message}`);
  }
}

// Finds an HTML asset whether running as pkg bundle or plain node.
// Checks cwd, exe dir, and __dirname (snapshot) in that order.
function findHtmlPath(filename) {
  const candidates = [
    path.join(process.cwd(), filename),
    process.execPath ? path.join(path.dirname(process.execPath), filename) : null,
    path.join(__dirname, filename),
  ].filter(Boolean);
  for (const p of candidates) {
    try { if (fs.statSync(p).isFile()) return p; } catch {}
  }
  return candidates[0]; // fall back to cwd even if missing
}
// Ensure config.json exists and contains all keys from config_default.json.
// Preserves user settings on reinstall; adds new keys for future releases.
migrateConfig();

// Built-in Discord application for Master's FM. All installs share this client_id
// so rich presence works out-of-the-box — no per-user Discord app setup needed.
// Users can override via discord_rpc.client_id in config.json (advanced use).
const DEFAULT_DISCORD_CLIENT_ID = '1495411843836018819';

let DISCORD_RPC_ENABLED  = true;
let DISCORD_RPC_CLIENTID = DEFAULT_DISCORD_CLIENT_ID;
try {
  const cfgPath = findConfigPath();
  const raw = fs.readFileSync(cfgPath, 'utf8').replace(/^\uFEFF/, '');  // strip UTF-8 BOM if present
  flog(`config.json raw: ${raw.slice(0, 200)}`);
  const cfg = JSON.parse(raw);
  if (cfg.discord_rpc) {
    DISCORD_RPC_ENABLED  = cfg.discord_rpc.enabled !== false;
    // Use config override if provided, otherwise fall back to built-in app ID
    DISCORD_RPC_CLIENTID = (cfg.discord_rpc.client_id || '').trim() || DEFAULT_DISCORD_CLIENT_ID;
    flog(`discord_rpc: enabled=${DISCORD_RPC_ENABLED} client_id=${DISCORD_RPC_CLIENTID === DEFAULT_DISCORD_CLIENT_ID ? 'built-in' : 'custom'}`);
  }
} catch (e) { flog(`Config load error: ${e.message}\n${e.stack}`); }
// ───────────────────────────────────────────────────────

let currentTrack  = null;
let _tsDiagCount  = 0;
let artResolved   = false;
let artResolving  = false;
let previewConfig = null;   // in-memory preview set by /customize; cleared on save

// ─── Server-Sent Events: push currentTrack to overlay instantly ─────────
// Eliminates polling latency — webhook arrives, broadcast fires, overlay
// receives within milliseconds over its open EventSource connection.
const sseClients = new Set();
// v9.2.0: shared state for the /screenshot endpoint. The HTTP request handler
// is recreated per-request inside the http.createServer callback (a fresh
// closure each time), so the pending-screenshot slot must live OUTSIDE that
// closure or each request creates its own empty state.
const _ssScreenshot = { pending: null, counter: 1 };
function sseBroadcast() {
  // Refresh Discord presence on every state change. pushDiscord() is cheap
  // (one buffered write over the IPC pipe) and reading from currentTrack
  // every broadcast keeps the timer / pause badge / art in sync automatically.
  try { pushDiscord(); } catch (e) { flog(`sseBroadcast: pushDiscord threw: ${e && e.message}`); }
  if (sseClients.size === 0) return;
  const payload = `data: ${JSON.stringify(currentTrack)}\n\n`;
  for (const res of sseClients) {
    try { res.write(payload); } catch { sseClients.delete(res); }
  }
}
// Heartbeat every 15 s so idle connections don't get dropped by proxies/OBS
setInterval(() => {
  if (sseClients.size === 0) return;
  for (const res of sseClients) {
    try { res.write(`: ping\n\n`); } catch { sseClients.delete(res); }
  }
}, 15000);

// v11.0.0: periodic status log every 60s — sseClients.size visible in server.log for CANARY correlation
setInterval(() => {
  flog(`[STATUS] uptime=${Math.floor(process.uptime())}s sseClients=${sseClients.size}`);
}, 60000);

// ─── Helpers ───────────────────────────────────────────

function log(msg) {
  flog(msg);   // all log() calls now go to server.log on disk
  console.log(`[${new Date().toLocaleTimeString()}] ${msg}`);
}

// ─── Discord Rich Presence ──────────────────────────────
// Connects via local named pipe (\\?\pipe\discord-ipc-N). Zero external deps.
// Kicks off only if a client_id is configured; idle loops quietly otherwise.
const discord = require('./discord_rpc');
// onReady callback: Discord just (re)connected. Reset our dedup cache so the
// next pushDiscord goes through — otherwise the user would see a blank
// Discord activity whenever the Discord client is closed+reopened during
// a song, because the signature matches the pre-disconnect state.
function _onDiscordReady() {
  _lastDiscordSig = '';
  try { pushDiscord(); } catch (e) { flog(`onReady push error: ${e.message}`); }
}
if (DISCORD_RPC_ENABLED && DISCORD_RPC_CLIENTID) {
  try { discord.init(DISCORD_RPC_CLIENTID, flog, _onDiscordReady); }
  catch (e) { flog(`discord_rpc init error: ${e.message}`); }
}
// pushDiscord() turns the live currentTrack into a presence payload. Data URIs
// are filtered out here — Discord's image CDN rejects them; only HTTPS URLs
// resolve to a visible large_image thumbnail.
//
// Dedup + age-refresh: sseBroadcast fires on every webhook heartbeat (~2 s),
// which would spam Discord with identical SET_ACTIVITY frames. Discord
// tolerates it but re-fetches the large_image URL each time — visible as a
// flash + costs ~10 s per cycle on slow networks. We skip the call when the
// payload matches our last-sent one … but ONLY for up to 30 s. After that
// the next push goes through even if identical, so if Discord ever missed
// a frame it self-heals within half a minute.
let _lastDiscordSig    = '';
let _lastDiscordPushAt = 0;
function pushDiscord() {
  if (!DISCORD_RPC_ENABLED || !DISCORD_RPC_CLIENTID) return;
  if (!currentTrack) {
    if (_lastDiscordSig !== '__cleared__') {
      _lastDiscordSig = '__cleared__';
      _lastDiscordPushAt = Date.now();
      try { discord.clear(); } catch (e) { flog(`pushDiscord: clear() threw: ${e && e.message}`); }
    }
    return;
  }
  const art = currentTrack.trackArt || '';
  const safeArt = /^https?:\/\//i.test(art) ? art : '';
  // Signature captures every field that affects the rendered activity.
  // startedAt is included so seeks trigger a re-push (startedAt shifts).
  const sig = [
    currentTrack.artist, currentTrack.track, currentTrack.source,
    currentTrack.startedAt, currentTrack.duration,
    !!currentTrack.isPaused ? 1 : 0,
    safeArt, currentTrack.originUrl || '',
  ].join('|');
  const ageMs = Date.now() - _lastDiscordPushAt;
  if (sig === _lastDiscordSig && ageMs < 30000) return;
  _lastDiscordSig    = sig;
  _lastDiscordPushAt = Date.now();
  try {
    discord.update({
      artist:    currentTrack.artist,
      track:     currentTrack.track,
      source:    currentTrack.source,
      startedAt: currentTrack.startedAt,
      duration:  currentTrack.duration,
      isPaused:  !!currentTrack.isPaused,
      trackArt:  safeArt,
      originUrl: currentTrack.originUrl || '',
    });
  } catch (e) { flog(`discord update error: ${e.message}`); }
}

function trackKey(artist, track) {
  return `${artist.trim().toLowerCase()}|||${track.trim().toLowerCase()}`;
}

function isSameTrack(artist, track) {
  if (!currentTrack) return false;
  return trackKey(currentTrack.artist, currentTrack.track) === trackKey(artist, track);
}

// Detects placeholder / garbage artist strings that some SMTC sources emit when
// metadata is missing: all '?' (encoding-loss), all '-', all '_', empty, or
// the literal "unknown" variants. A real artist name always contains at least
// one letter or digit, so we also reject strings that have none.
function isPlaceholderArtist(s) {
  const v = (s || '').trim();
  if (!v) return true;
  const low = v.toLowerCase();
  if (low === 'unknown' || low === 'unknown artist' || low === 'various artists' || low === 'n/a') return true;
  // Strings made entirely of punctuation / placeholder chars (?, -, _, ., •, spaces)
  if (/^[\s\?\-_.·•*]+$/.test(v)) return true;
  // No letter and no digit anywhere → can't be a real name
  if (!/[\p{L}\p{N}]/u.test(v)) return true;
  return false;
}

// Strip (feat. ...), [feat. ...], ft., featuring, with, remix, edit, remaster suffixes
// so "Get You (feat. Kali Uchis)" → "Get You"
function cleanTrack(t) {
  return t
    .replace(/\s*[\(\[](?:feat|ft|featuring|with)\.?\s[^\)\]]*[\)\]]/gi, '')
    .replace(/\s*[\(\[](?:remix|edit|version|mix|remaster|remastered|acoustic|live|radio)[^\)\]]*[\)\]]/gi, '')
    .replace(/\s*-\s*(?:remix|edit|version|mix|remaster|remastered|acoustic|live|radio)\s*$/gi, '')
    .trim();
}

// For artist "Daniel Caesar & H.E.R." → "Daniel Caesar"
function cleanArtist(a) {
  return a.split(/\s*[,&]\s*/)[0].trim();
}

function httpsGet(url, extraHeaders) {
  return new Promise((resolve, reject) => {
    const headers = Object.assign({ 'User-Agent': 'Mozilla/5.0' }, extraHeaders || {});
    const req = https.get(url, { headers, rejectUnauthorized: false }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        return httpsGet(res.headers.location, extraHeaders).then(resolve).catch(reject);
      }
      let body = '';
      res.on('data', chunk => body += chunk);
      res.on('end', () => resolve(body));
    });
    req.on('error', reject);
    req.setTimeout(5000, () => { req.destroy(); reject(new Error('Timeout')); });
  });
}

function isUrlAccessible(url) {
  return new Promise((resolve) => {
    try {
      const u = new URL(url);
      const req = https.request(
        { method: 'HEAD', hostname: u.hostname, path: u.pathname + u.search,
          headers: { 'User-Agent': 'Mozilla/5.0' }, rejectUnauthorized: false },
        (res) => resolve(res.statusCode >= 200 && res.statusCode < 400)
      );
      req.on('error', () => resolve(false));
      req.setTimeout(4000, () => { req.destroy(); resolve(false); });
      req.end();
    } catch { resolve(false); }
  });
}

function isValidArt(url) {
  if (!url || typeof url !== 'string') return false;
  if (url.includes('2a96cbd8b46e442fc41c2b86b821562f')) return false;
  if (url.includes('default_avatar')) return false;
  // Accept http(s) URLs OR base64 data URIs (used for SMTC thumbnails sent by tray)
  return url.startsWith('http') || url.startsWith('data:image/');
}

// ─── Duration resolver (runs in parallel with artwork) ─
// Tries exact names first, then cleaned names (no feat./remix).
// This is why timestamps now work for songs like "Get You (feat. Kali Uchis)".
async function resolveDuration(artist, track) {
  // Already have a duration (e.g. from webhook)
  if (currentTrack?.duration > 0) return;

  const exact   = { artist, track };
  const cleaned = { artist: cleanArtist(artist), track: cleanTrack(track) };
  const hasCleaned = cleaned.artist !== artist || cleaned.track !== track;
  const variants  = hasCleaned ? [exact, cleaned] : [exact];

  const trySet = (ms, source) => {
    if (ms > 0 && currentTrack && isSameTrack(artist, track) && !(currentTrack.duration > 0)) {
      log(`⏱  Duration (${source}): ${Math.round(ms / 1000)}s`);
      currentTrack.duration = ms;
      return true;
    }
    return false;
  };

  log(`⏱  Resolving duration for: ${artist} - ${track}` +
      (hasCleaned ? ` → also trying "${cleaned.artist} - ${cleaned.track}"` : ''));

  // 1. Deezer
  for (const v of variants) {
    try {
      const body = await httpsGet(
        `https://api.deezer.com/search?q=artist:"${encodeURIComponent(v.artist)}"` +
        ` track:"${encodeURIComponent(v.track)}"&limit=1`
      );
      const json = JSON.parse(body);
      const dur  = (json.data?.[0]?.duration || 0) * 1000;
      if (trySet(dur, `Deezer${v === cleaned ? ' (cleaned)' : ''}`)) return;
    } catch (e) { log(`duration Deezer lookup threw (${v.artist} - ${v.track}): ${e.message}`); }
  }

  // 3. iTunes
  for (const v of variants) {
    try {
      const q    = encodeURIComponent(`${v.artist} ${v.track}`);
      const body = await httpsGet(`https://itunes.apple.com/search?term=${q}&media=music&limit=1`);
      const json = JSON.parse(body);
      const dur  = json.results?.[0]?.trackTimeMillis || 0;
      if (trySet(dur, `iTunes${v === cleaned ? ' (cleaned)' : ''}`)) return;
    } catch (e) { log(`duration iTunes lookup threw (${v.artist} - ${v.track}): ${e.message}`); }
  }

  // 4. MusicBrainz
  for (const v of variants) {
    try {
      const body = await httpsGet(
        `https://musicbrainz.org/ws/2/recording/?query=recording:${encodeURIComponent(v.track)}` +
        `+artist:${encodeURIComponent(v.artist)}&fmt=json&limit=1`
      );
      const json = JSON.parse(body);
      const dur  = json.recordings?.[0]?.length || 0;
      if (trySet(dur, `MusicBrainz${v === cleaned ? ' (cleaned)' : ''}`)) return;
    } catch (e) { log(`duration MusicBrainz lookup threw (${v.artist} - ${v.track}): ${e.message}`); }
  }

  log(`⏱  ❌ No duration found for: ${artist} - ${track} — giving up (webhook will provide if available)`);
  // Mark as gave-up so the poll loop stops retrying every 2s.
  // The webhook will still override this if SoundCloud sends a duration.
  if (currentTrack && isSameTrack(artist, track)) {
    currentTrack.durationGaveUp = true;
  }
}

// ─── SoundCloud client_id scraper ───────────────────────
// Scraped from the public homepage JS bundle so we can hit api-v2.soundcloud.com
// search without authentication. Cached for 6 h; refreshed on 401/403.
let _scClientId = null;
let _scClientIdAt = 0;
const SC_CLIENTID_TTL = 6 * 60 * 60 * 1000;

async function getSoundCloudClientId(force = false) {
  if (!force && _scClientId && (Date.now() - _scClientIdAt) < SC_CLIENTID_TTL) {
    return _scClientId;
  }
  try {
    const html = await httpsGet('https://soundcloud.com/');
    const scripts = [...html.matchAll(/src="(https:\/\/a-v2\.sndcdn\.com\/assets\/[0-9a-f\-]+\.js)"/g)].map(m => m[1]);
    // Last script in the bundle is usually the one containing client_id
    for (const url of scripts.reverse()) {
      try {
        const js = await httpsGet(url);
        const m = js.match(/client_id\s*[:=]\s*["']([a-zA-Z0-9]{32})["']/);
        if (m) {
          _scClientId   = m[1];
          _scClientIdAt = Date.now();
          log(`🔑 SoundCloud client_id acquired (len=${m[1].length})`);
          return _scClientId;
        }
      } catch (e) { log(`SoundCloud client_id scrape: fetch ${url} threw: ${e.message}`); }
    }
  } catch (e) {
    log(`⚠️  SoundCloud client_id scrape failed: ${e.message}`);
  }
  return null;
}

// Search SoundCloud directly for artist+track and return the track's artwork URL,
// upgraded to 500x500. Returns '' on any failure. Used for instant art on
// SoundCloud-exclusive tracks that Deezer/iTunes don't know about.
async function searchSoundCloudArt(artist, track) {
  const clientId = await getSoundCloudClientId();
  if (!clientId) return '';

  const variants = [
    `${artist} ${track}`,
    `${cleanArtist(artist)} ${cleanTrack(track)}`,
    track,
  ].filter((v, i, a) => v && v.trim() && a.indexOf(v) === i);

  for (const q of variants) {
    try {
      const url  = `https://api-v2.soundcloud.com/search/tracks?q=${encodeURIComponent(q)}&client_id=${clientId}&limit=5`;
      const body = await httpsGet(url);
      const json = JSON.parse(body);
      const hits = json.collection || [];
      const artistLow = (artist || '').toLowerCase().trim();
      const trackLow  = (track  || '').toLowerCase().trim();
      // Prefer a hit whose title contains the track name; fall back to first hit
      const pick = hits.find(h => {
        const tt = (h.title || '').toLowerCase();
        const ua = ((h.user && h.user.username) || '').toLowerCase();
        return tt.includes(trackLow) && (!artistLow || ua.includes(artistLow) || tt.includes(artistLow));
      }) || hits[0];
      if (pick && pick.artwork_url) {
        const art = pick.artwork_url.replace(/-large|-t300x300|-t200x200|-t120x120|-t67x67/, '-t500x500');
        if (await isUrlAccessible(art)) return art;
        if (await isUrlAccessible(pick.artwork_url)) return pick.artwork_url;
      }
      // If no artwork_url, use the user avatar as last resort
      if (pick && pick.user && pick.user.avatar_url) {
        const av = pick.user.avatar_url.replace(/-large|-t300x300|-t200x200/, '-t500x500');
        if (await isUrlAccessible(av)) return av;
      }
    } catch (e) {
      // 401/403 → stale client_id; force refresh once
      if (/401|403/.test(e.message || '')) {
        _scClientId = null;
        const fresh = await getSoundCloudClientId(true);
        if (!fresh) break;
      }
    }
  }
  return '';
}

// ─── YouTube art fallback ─────────────────────────────────
// Scrape YouTube's public search results for a matching video and return its
// hqdefault thumbnail. Used when SMTC didn't provide a thumbnail (e.g. ad-
// blocker interference) and Deezer/iTunes can't find the track — which is
// common for non-music YouTube uploads (podcasts, lofi mixes, game OSTs).
async function searchYouTubeArt(artist, track) {
  try {
    const q = encodeURIComponent(`${artist || ''} ${track || ''}`.trim());
    if (!q) return '';
    const body = await httpsGet(
      `https://www.youtube.com/results?search_query=${q}`,
      { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 MastersFM' }
    );
    // YouTube results page embeds JSON in ytInitialData; first "videoId" match
    // is the top hit. Scraping is fragile but requires no API key.
    const m = body.match(/"videoId":"([A-Za-z0-9_\-]{11})"/);
    if (!m) { flog(`🎬 YouTube search: no videoId match in ${body.length} bytes`); return ''; }
    const vid = m[1];
    // Prefer maxresdefault (1280x720) — falls back to hqdefault automatically on 404
    const hq = `https://img.youtube.com/vi/${vid}/hqdefault.jpg`;
    if (await isUrlAccessible(hq)) { flog(`🎬 YouTube search: videoId=${vid} → hqdefault`); return hq; }
    const mq = `https://img.youtube.com/vi/${vid}/mqdefault.jpg`;
    if (await isUrlAccessible(mq)) { flog(`🎬 YouTube search: videoId=${vid} → mqdefault`); return mq; }
    return '';
  } catch (e) {
    flog(`⚠️  YouTube search failed: ${e.message}`);
    return '';
  }
}

// ─── osu! beatmap art fallback ────────────────────────────
// Scrape osu.ppy.sh beatmapsets search for a matching beatmap and return its
// cover image. No API key required — the search page is public HTML with
// beatmapset IDs embedded in data-attributes and JSON.
async function searchOsuArt(artist, track) {
  try {
    const q = encodeURIComponent(`${artist || ''} ${track || ''}`.trim());
    if (!q) return '';
    // The public /beatmapsets search responds to the Accept: application/json
    // header with a JSON body containing beatmapsets[].id — much easier to
    // parse than the HTML page.
    const body = await httpsGet(
      `https://osu.ppy.sh/beatmapsets/search?q=${q}&s=any`,
      { 'Accept': 'application/json', 'User-Agent': 'MastersFM/1.7' }
    );
    let id = null;
    try {
      const json = JSON.parse(body);
      id = json?.beatmapsets?.[0]?.id || null;
    } catch {
      // HTML fallback: match any `beatmapsets/<id>` reference in the response
      const m = body.match(/\/beatmapsets\/(\d+)/);
      if (m) id = m[1];
    }
    if (!id) { flog(`🎵 osu! search: no beatmapset match`); return ''; }
    // Known CDN endpoints for beatmapset covers (no auth required)
    const candidates = [
      `https://assets.ppy.sh/beatmaps/${id}/covers/cover@2x.jpg`,
      `https://assets.ppy.sh/beatmaps/${id}/covers/cover.jpg`,
      `https://b.ppy.sh/thumb/${id}l.jpg`,
    ];
    for (const url of candidates) {
      if (await isUrlAccessible(url)) { flog(`🎵 osu! search: beatmapset=${id} → ${url.split('/').pop()}`); return url; }
    }
    return '';
  } catch (e) {
    flog(`⚠️  osu! search failed: ${e.message}`);
    return '';
  }
}

// ─── Generic web image search fallback ────────────────────
// Absolute last resort — when every dedicated music service whiffs, scrape
// Bing Images for "<artist> <track> album cover" and take the first result.
// Bing doesn't require a key and its HTML exposes direct image URLs in
// `murl` query params. Budget: we give it ~1200 ms; if Bing is slow we
// give up rather than delay the overlay's track-change animation.
async function searchWebImageArt(artist, track) {
  try {
    const q = encodeURIComponent(`${artist || ''} ${track || ''} album cover`.trim());
    if (!q) return '';
    const body = await Promise.race([
      httpsGet(`https://www.bing.com/images/search?q=${q}&form=HDRSC2&first=1`,
        { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 MastersFM' }),
      new Promise((_, reject) => setTimeout(() => reject(new Error('Bing timeout')), 1200)),
    ]);
    // Bing embeds image URLs in JSON blobs inside data-m or murl= query params.
    // Match the first murl=... that's a real image file.
    const m = body.match(/"murl":"(https?:\\?\/\\?\/[^"]+?\.(?:jpg|jpeg|png|webp))"/i);
    let imgUrl = m ? m[1].replace(/\\\//g, '/') : '';
    if (!imgUrl) {
      // Fallback pattern: "imgurl": param in anchor hrefs
      const m2 = body.match(/mediaurl=([^&"]+)/i);
      if (m2) imgUrl = decodeURIComponent(m2[1]);
    }
    if (!imgUrl) { flog(`🌐 Bing search: no match for "${artist} ${track}"`); return ''; }
    // Bing sometimes returns data: URIs or oddly-encoded URLs; skip anything
    // that isn't a clean http(s) URL so the overlay doesn't choke later.
    if (!/^https?:\/\//i.test(imgUrl)) return '';
    if (await isUrlAccessible(imgUrl)) { flog(`🌐 Bing search: ${imgUrl.slice(0,80)}…`); return imgUrl; }
    return '';
  } catch (e) {
    flog(`⚠️  Bing image search failed: ${e.message}`);
    return '';
  }
}

// ─── Artwork resolver ───────────────────────────────────
async function resolveArtwork(artist, track, webhookArt, originUrl, source) {

  // Data-URI thumbnails from the tray (SMTC) — these are the exact thumbnails
  // embedded in the browser's media player UI (YouTube video frame, SoundCloud
  // track cover, YouTube Music album art, etc.).  For browser-sourced tracks
  // they are the most direct and accurate art available, so we return them
  // immediately instead of wasting time on Deezer/iTunes lookups that
  // often fail for niche or user-uploaded content anyway.
  const smtcThumb = (webhookArt && webhookArt.startsWith('data:image/')) ? webhookArt : null;

  // Browser platforms — SMTC thumbnail is authoritative (it IS the art shown
  // in the browser tab's Now Playing widget for that exact track/video).
  const sourceLower = (source || '').toLowerCase();
  const browserPlatforms = ['soundcloud','youtube','deezer','tidal','apple music','bandcamp','mixcloud'];
  const isBrowserPlatform = browserPlatforms.some(p => sourceLower.includes(p));
  if (smtcThumb && isBrowserPlatform) {
    log('🖼  Art: SMTC thumbnail (browser source)');
    return smtcThumb;
  }

  // 0. SoundCloud direct search — tracks from SoundCloud RPC / SoundCloud web player
  //    rarely appear on Deezer/iTunes, so hit SoundCloud's own API first.
  if (sourceLower === 'soundcloud') {
    const scArt = await searchSoundCloudArt(artist, track);
    if (scArt) { log('🖼  Art: SoundCloud search'); return scArt; }
  }

  // 0b. osu! direct search — beatmap covers live on assets.ppy.sh and aren't
  //     indexed by Deezer/iTunes/MusicBrainz. Hit osu! first for osu! tracks.
  if (sourceLower.includes('osu')) {
    const osuArt = await searchOsuArt(artist, track);
    if (osuArt) { log('🖼  Art: osu! search'); return osuArt; }
  }

  // 1. Webhook art URL (e.g. from the SoundCloud RPC webhook). Skip for data: URIs.
  if (isValidArt(webhookArt) && !smtcThumb) {
    const upgraded = webhookArt.replace(/-large|-t300x300|-t200x200/, '-t500x500');
    if (await isUrlAccessible(upgraded)) { log('🖼  Art: webhook');         return upgraded;   }
    if (upgraded !== webhookArt && await isUrlAccessible(webhookArt))
                                         { log('🖼  Art: webhook (orig)');  return webhookArt; }
    log('🖼  ⚠️  Webhook art URL failed, trying other sources');
  }

  // 2. SoundCloud oEmbed
  if (originUrl && originUrl.includes('soundcloud.com')) {
    try {
      const body = await httpsGet(`https://soundcloud.com/oembed?format=json&url=${encodeURIComponent(originUrl)}`);
      const data = JSON.parse(body);
      if (isValidArt(data.thumbnail_url)) {
        const url = data.thumbnail_url.replace(/-large|-t300x300|-t200x200/, '-t500x500');
        if (await isUrlAccessible(url)) { log('🖼  Art: SoundCloud oEmbed'); return url; }
      }
    } catch (e) { log(`art SoundCloud oEmbed lookup threw: ${e.message}`); }
  }

  // 3. Deezer
  for (const [a, t] of [[artist, track], [cleanArtist(artist), cleanTrack(track)]]) {
    try {
      const body = await httpsGet(
        `https://api.deezer.com/search?q=artist:"${encodeURIComponent(a)}" track:"${encodeURIComponent(t)}"&limit=1`
      );
      const json = JSON.parse(body);
      const art  = json.data?.[0]?.album?.cover_xl || json.data?.[0]?.album?.cover_big;
      if (isValidArt(art)) { log(`🖼  Art: Deezer${a !== artist ? ' (cleaned)' : ''}`); return art; }
    } catch (e) { log(`art Deezer lookup threw (${a} - ${t}): ${e.message}`); }
  }

  // 5. iTunes
  for (const [a, t] of [[artist, track], [cleanArtist(artist), cleanTrack(track)]]) {
    try {
      const q    = encodeURIComponent(`${a} ${t}`);
      const body = await httpsGet(`https://itunes.apple.com/search?term=${q}&media=music&limit=1`);
      const json = JSON.parse(body);
      const art  = json.results?.[0]?.artworkUrl100;
      if (isValidArt(art)) {
        log(`🖼  Art: iTunes${a !== artist ? ' (cleaned)' : ''}`);
        return art.replace('100x100bb', '500x500bb');
      }
    } catch (e) { log(`art iTunes lookup threw (${a} - ${t}): ${e.message}`); }
  }

  // 6. MusicBrainz + Cover Art Archive
  for (const [a, t] of [[artist, track], [cleanArtist(artist), cleanTrack(track)]]) {
    try {
      const body = await httpsGet(
        `https://musicbrainz.org/ws/2/recording/?query=recording:${encodeURIComponent(t)}` +
        `+artist:${encodeURIComponent(a)}&fmt=json&limit=1`
      );
      const json = JSON.parse(body);
      const id   = json.recordings?.[0]?.releases?.[0]?.id;
      if (id) { log(`🖼  Art: Cover Art Archive${a !== artist ? ' (cleaned)' : ''}`); return `https://coverartarchive.org/release/${id}/front`; }
    } catch (e) { log(`art MusicBrainz lookup threw (${a} - ${t}): ${e.message}`); }
  }

  // 7. SMTC thumbnail (data URI, last-resort fallback)
  if (smtcThumb) {
    log('🖼  Art: SMTC thumbnail (fallback)');
    return smtcThumb;
  }

  // 8. YouTube video thumbnail — last-resort for YouTube sources when SMTC
  //    didn't provide one and the track isn't indexed by any music service.
  //    Non-music YouTube uploads (podcasts, DJ sets, game soundtracks) end up
  //    here and deserve SOME art — the video's own thumbnail is the obvious
  //    choice. This scrapes YouTube's public search page; works without API key.
  if (sourceLower.includes('youtube')) {
    const ytArt = await searchYouTubeArt(artist, track);
    if (ytArt) { log('🖼  Art: YouTube video thumbnail'); return ytArt; }
  }

  // 9. Web image search — absolute last resort. Covers obscure tracks not
  //    indexed by any music service (small-label releases, DJ mixes, podcast
  //    episodes, etc.). Runs with a 1.2 s deadline so a slow Bing doesn't
  //    delay the overlay's track-change animation. If Bing times out the
  //    overlay simply shows the music-note fallback and moves on — no hang.
  const webArt = await searchWebImageArt(artist, track);
  if (webArt) { log('🖼  Art: Bing web search'); return webArt; }

  log(`🖼  ❌ No artwork found for: ${artist} - ${track}`);
  return '';
}

// ─── Enrich title-only tracks ───────────────────────────
// Called when tray sends a track with no/unknown artist (VLC file play, WMP, etc.).
// Searches Deezer → iTunes to find the real artist + starter art URL.
async function enrichByTitle(title) {
  const q = encodeURIComponent(title);

  // 1. Deezer — no key, fast, best metadata coverage
  try {
    const body = await httpsGet(`https://api.deezer.com/search?q=${q}&limit=5`);
    const json  = JSON.parse(body);
    const hits  = json.data || [];
    const ttl   = title.toLowerCase().trim();
    for (const hit of hits) {
      if (!hit.title || !hit.artist?.name) continue;
      const htl = hit.title.toLowerCase().trim();
      if (htl === ttl || htl.includes(ttl) || ttl.includes(htl)) {
        log(`🔍 Enriched (Deezer exact): "${title}" → "${hit.artist.name} - ${hit.title}"`);
        return { artist: hit.artist.name, track: hit.title, art: hit.album?.cover_xl || hit.album?.cover_big || '' };
      }
    }
    if (hits[0]?.artist?.name) {
      log(`🔍 Enriched (Deezer first): "${title}" → "${hits[0].artist.name} - ${hits[0].title}"`);
      return { artist: hits[0].artist.name, track: hits[0].title, art: hits[0].album?.cover_xl || hits[0].album?.cover_big || '' };
    }
  } catch (e) { log(`enrichByTitle Deezer threw ("${title}"): ${e.message}`); }

  // 2. iTunes
  try {
    const body = await httpsGet(`https://itunes.apple.com/search?term=${q}&media=music&limit=1`);
    const json  = JSON.parse(body);
    const hit   = json.results?.[0];
    if (hit?.artistName) {
      log(`🔍 Enriched (iTunes): "${title}" → "${hit.artistName} - ${hit.trackName}"`);
      return { artist: hit.artistName, track: hit.trackName || title, art: hit.artworkUrl100?.replace('100x100bb', '500x500bb') || '' };
    }
  } catch (e) { log(`enrichByTitle iTunes threw ("${title}"): ${e.message}`); }

  log(`🔍 ❌ Could not enrich "${title}" — artist stays unknown`);
  return null;
}

// ─── Set track (art + duration run in parallel) ─────────
// startedAt: optional epoch ms — calculated from positionMs for seek-accurate progress.
async function setTrack(artist, track, webhookArt, originUrl, duration, source, startedAt, isPausedInit) {
  let resolvedArtist = (artist || '').trim();
  let resolvedTrack  = track;
  let resolvedArt    = webhookArt;

  // If artist is missing, generic, or a placeholder like "?????????", search by title
  // to get the real artist + starter art. This covers VLC/WMP playing local files, or
  // SoundCloud RPC reporting the literal "?????????" when metadata is unresolved.
  const artistUnknown = isPlaceholderArtist(resolvedArtist);
  if (artistUnknown) {
    log(`🔍 Artist missing/placeholder ("${resolvedArtist}") for "${resolvedTrack}" [${source}] — searching by title...`);
    const enriched = await enrichByTitle(resolvedTrack);
    if (enriched) {
      resolvedArtist = enriched.artist;
      resolvedTrack  = enriched.track;
      if (!isValidArt(resolvedArt) && isValidArt(enriched.art)) resolvedArt = enriched.art;
    }
  }

  const resolvedStartedAt = startedAt || Date.now();
  log(`✅ ${source}: ${resolvedArtist} - ${resolvedTrack}${duration > 0 ? ` (${Math.round(duration/1000)}s)` : ''}${startedAt ? ` pos=${Math.round((Date.now()-startedAt)/1000)}s` : ''}`);
  const startPaused = isPausedInit === true;
  currentTrack = { artist: resolvedArtist, track: resolvedTrack, trackArt: '', duration, originUrl, startedAt: resolvedStartedAt, source, isPaused: startPaused, pausedAt: startPaused ? Date.now() : 0, _setAt: Date.now() };
  artResolved  = false;
  artResolving = true;
  // Reset the Discord dedupe BEFORE the early push so every new track is
  // guaranteed to send a fresh SET_ACTIVITY — never relies on whatever the
  // previous sig was. Previously, if Discord missed a frame (temporarily
  // disconnected pipe, pipe flushed stale payload, etc.) the dedupe would
  // lock us out and Discord would keep showing the previous track forever.
  _lastDiscordSig = '';
  // Push to Discord IMMEDIATELY with the new track's start+duration+metadata
  // so it appears in the user's status within milliseconds instead of after
  // art resolution (which can take 2-15 s on slow paths like MusicBrainz /
  // YouTube / Bing). The placeholder image is the Master's FM logo; when real
  // art resolves below, sseBroadcast() re-pushes with the final URL.
  //
  // Bug previously fixed here: waiting for art meant that if a song changed
  // mid-art-resolution, Discord would keep showing the PREVIOUS track's end
  // timestamp (computed from the previous duration) because we hadn't yet
  // sent the new activity with the new duration. Pushing immediately with
  // the NEW startedAt + NEW duration overwrites Discord's old end-time
  // completely — no more "next song ending 5:00 ago" carryover.
  try { pushDiscord(); } catch (e) { flog(`early discord push error: ${e.message}`); }

  // Run artwork and duration resolution at the same time
  const [art] = await Promise.all([
    resolveArtwork(resolvedArtist, resolvedTrack, resolvedArt, originUrl, source),
    duration > 0 ? Promise.resolve() : resolveDuration(resolvedArtist, resolvedTrack),
  ]);

  if (currentTrack && isSameTrack(resolvedArtist, resolvedTrack)) {
    currentTrack.trackArt = art;
    artResolved = true;
    log(`📋 Track ready — art: ${art ? '✅' : '❌'}  duration: ${currentTrack.duration > 0 ? `✅ ${Math.round(currentTrack.duration/1000)}s` : '❌ not found'}`);
    sseBroadcast(); // pushDiscord() is called inside sseBroadcast — re-pushes with final art URL
  }
  artResolving = false;
}

// ─── Webhook handler ────────────────────────────────────
async function handleWebhook(body) {
  try {
    const data       = JSON.parse(body);
    const artist     = data.artist || '';
    const track      = data.track  || '';
    const duration   = Math.round((data.duration || 0) * 1000); // tray sends seconds → ms
    const positionMs = data.positionMs ? Math.round(data.positionMs) : 0;
    // Seek-aware startedAt: back-calculate track origin from reported position.
    // GUARD: for brand-new tracks (!same) SMTC often reports the OLD track's
    // position for the first 1-2 heartbeats. Ignore positionMs > 5 s on track
    // CHANGES — but NOT on the first webhook after server boot, where the
    // "new" track is just the user's ongoing playback that we need to sync to.
    // Without this distinction, restarting Master's FM mid-song would snap the
    // overlay back to 0:00 instead of reflecting the real playhead.
    const hadPrevTrack  = !!currentTrack;
    const isNewTrack    = !isSameTrack(data.artist || '', data.track || '');
    const isTrackChange = hadPrevTrack && isNewTrack;    // mid-session track change → apply stale guard
    const trustedPos    = (isTrackChange && positionMs > 5000) ? 0 : positionMs;
    const startedAt     = trustedPos > 0 ? (Date.now() - trustedPos) : Date.now();
    // Use real platform name from tray (Spotify, VLC, osu!, Chrome, …) or fall back to 'Webhook'
    const source     = data.source || 'Webhook';
    const isPaused   = data.isPaused === true;
    const isSeek     = data.seek === true;

    const same = isSameTrack(artist, track);
    // Timestamp diagnostic — logs source-reported pos vs server's computed elapsed every
    // ~10 s so drift/staleness is visible in server.log without webhook-line spam.
    // Skip when positionMs < 1 s — sources like SoundCloud RPC whose SMTC timeline is
    // frozen at near-zero would produce meaningless "drift=+Ns" lines every 10 s.
    if (same && currentTrack && positionMs >= 1000) {
      const expectedMs = Date.now() - currentTrack.startedAt;
      const drift = expectedMs - positionMs;
      _tsDiagCount = (_tsDiagCount || 0) + 1;
      if ((_tsDiagCount % 5) === 0) {
        log(`⏱  [${source}] reported=${Math.round(positionMs/1000)}s overlay=${Math.round(expectedMs/1000)}s drift=${drift>0?'+':''}${Math.round(drift/1000)}s paused=${isPaused}`);
      }
    }
    // Only log webhook arrival on meaningful events — new track, pause-flip, newly-known duration.
    // Pure 2-second heartbeats are silenced to keep server.log readable.
    const pauseFlipped = currentTrack && same && (currentTrack.isPaused !== isPaused);
    const durationNewlyKnown = currentTrack && same && duration > 0 && !(currentTrack.duration > 0);
    if (!same || pauseFlipped || durationNewlyKnown) {
      log(`🔔 Webhook [${source}]: "${artist} - ${track}" dur=${Math.round(duration/1000)}s pos=${Math.round(positionMs/1000)}s art=${data.trackArt ? '✅' : '❌'} paused=${isPaused} same=${same}`);
    }

    if (!same) {
      await setTrack(artist, track, data.trackArt || '', data.originUrl || '', duration, source, startedAt, isPaused);
    } else {
      // ── Unified pause / resume / seek handling ─────────────────────────────
      // Key insight: while paused, SMTC freezes positionMs at the pause point,
      // but wall-clock time keeps moving. If we let startedAt drift, the overlay's
      // elapsed (Date.now() - startedAt) grows even while paused → bad bounce.
      // Fix: every paused webhook re-pins startedAt = now - positionMs so
      // elapsed = positionMs exactly. pausedAt is refreshed each tick too, so
      // the client can freeze its progress bar at the latest known position.
      if (currentTrack) {
        const wasPaused = currentTrack.isPaused;
        if (isPaused) {
          // v6.6.4: RE-PIN startedAt to the source's reported positionMs on
          // every pause event. User explicitly asked: "every time when I pause
          // or unpause the media, it will instantly sync the correct timestamps".
          // Previous code (which intentionally AVOIDED re-pinning to prevent
          // snaps) was producing the sync drift they're complaining about.
          // Accepting the occasional visible snap for accurate timestamps.
          if (!wasPaused) {
            currentTrack.startedAt = Date.now() - positionMs;
            currentTrack.pausedAt = Date.now();
            currentTrack.isPaused = true;
            log(`⏸  Paused: ${artist} - ${track} @ ${Math.round(positionMs/1000)}s (re-synced to source)`);
          }
          // else: already paused — leave startedAt/pausedAt alone.
        } else {
          // v6.6.4: on RESUME, also re-pin startedAt to source position so
          // elapsed = positionMs the instant we unpause — then grows
          // naturally from there. Again, prioritising source-truth over
          // display continuity per user request.
          if (wasPaused) {
            currentTrack.startedAt = Date.now() - positionMs;
            log(`▶  Resumed: ${artist} - ${track} @ ${Math.round(positionMs/1000)}s (re-synced to source)`);
          }
          currentTrack.isPaused = false;
          currentTrack.pausedAt = 0;
        }
      }
      // v6.6.4: keep duration synced with every webhook (was only updating if
      // originally unset). User wants END time to stay accurate too — some
      // sources report slightly different duration as the track plays.
      if (duration > 0 && duration !== currentTrack.duration) {
        if (!currentTrack.duration) {
          log(`⏱  Duration set from webhook: ${Math.round(duration/1000)}s`);
        } else if (Math.abs(duration - currentTrack.duration) > 1000) {
          log(`⏱  Duration updated from webhook: ${Math.round(currentTrack.duration/1000)}s → ${Math.round(duration/1000)}s`);
        }
        currentTrack.duration = duration;
      }
      // Re-pin startedAt ONLY when tray explicitly flags a seek. SMTC positionMs is
      // routinely 3-5 s stale vs wall-clock (timeline snapshots freeze between SMTC
      // events), so drift-based detection was constantly misfiring and snapping the
      // overlay backwards. Tray's local seek detector (compare consecutive positionMs
      // against elapsed tick time) is authoritative.
      if (isSeek && currentTrack && !currentTrack.isPaused) {
        const expectedPos = Date.now() - currentTrack.startedAt;
        const drift = Math.abs(expectedPos - positionMs);
        currentTrack.startedAt = Date.now() - positionMs;
        log(`⏩  Seek (${Math.round(drift/1000)}s jump) — startedAt resynced to pos=${Math.round(positionMs/1000)}s`);
      }
      // First-heartbeat correction: SMTC positionMs from the background source is often
      // stale at the moment of source-switch (frozen since the source lost focus). By the
      // next heartbeat (~100ms later) SMTC has refreshed. If the new positionMs disagrees
      // with our computed elapsed by >5s within the first 2s of track acceptance, re-pin.
      if (!isSeek && !isPaused && positionMs > 0 && currentTrack && currentTrack._setAt) {
        const timeSinceSet = Date.now() - currentTrack._setAt;
        if (timeSinceSet < 2000) {
          const computedElapsed = Date.now() - currentTrack.startedAt;
          const drift = Math.abs(computedElapsed - positionMs);
          if (drift > 5000) {
            currentTrack.startedAt = Date.now() - positionMs;
            log(`⏱  First-heartbeat correction [${source}]: ${Math.round(drift/1000)}s drift → resynced to ${Math.round(positionMs/1000)}s`);
          }
        }
      }
      // Continuous drift correction: asymmetric to avoid the primary cause of
      // long-term cumulative timer drift. When SMTC freezes (browser tab
      // backgrounded, Windows SMTC event gap) positionMs stays stale for seconds
      // or minutes while the overlay's wall-clock keeps running correctly.
      // Snapping BACKWARD to a stale positionMs introduces a permanent offset that
      // compounds across freeze/unfreeze cycles into 30+ seconds of visible drift.
      //
      //  Forward correction  (SMTC > overlay):  snap at 4 s — SMTC is genuinely
      //    ahead, overlay fell behind somehow (bad initial startedAt, missed event).
      //  Backward correction (overlay > SMTC):  snap only at 30 s — extremely
      //    conservative to avoid false-snapping on SMTC lag/freeze. Small overlead
      //    (< 30 s) is almost always SMTC staleness, not a real bug.
      //  positionMs > 1000 guard skips sc-rpc which always reports near-zero.
      if (!isSeek && !isPaused && positionMs > 1000 && currentTrack) {
        const computedElapsed = Date.now() - currentTrack.startedAt;
        const signedDrift = positionMs - computedElapsed; // positive → SMTC ahead, overlay behind
        if (signedDrift > 4000) {
          // Overlay is genuinely behind SMTC — snap forward
          currentTrack.startedAt = Date.now() - positionMs;
          log(`⏱  Sync fwd [${source}]: overlay ${Math.round(signedDrift/1000)}s behind → resynced to ${Math.round(positionMs/1000)}s`);
        } else if (signedDrift < -30000) {
          // Overlay 30s+ ahead of SMTC — extreme stale SMTC or genuine error
          currentTrack.startedAt = Date.now() - positionMs;
          log(`⏱  Sync bwd [${source}]: overlay ${Math.round(-signedDrift/1000)}s ahead (extreme) → resynced to ${Math.round(positionMs/1000)}s`);
        }
      }
      // Retry art if still missing
      if (!artResolved && !artResolving && isValidArt(data.trackArt) && !currentTrack.trackArt) {
        artResolving = true;
        resolveArtwork(artist, track, data.trackArt, data.originUrl || '', source).then(art => {
          if (art && currentTrack && isSameTrack(artist, track)) {
            currentTrack.trackArt = art;
            artResolved = true;
            log(`🖼  Art updated from webhook retry`);
            sseBroadcast();
          }
          artResolving = false;
        });
      }
    }
    sseBroadcast();
    return true;
  } catch (e) {
    log(`❌ Webhook parse error: ${e.message}`);
    return false;
  }
}

// ─── HTTP server ────────────────────────────────────────
// v6.6.3: listen() moved to fire IMMEDIATELY after createServer() so
// OBS Browser Source can connect the instant server.exe's Node runtime
// finishes its pkg startup — previously listen() was the LAST line of
// the file (~350 lines after createServer), which didn't execute until
// all intervening async setup completed. User reported OBS takes 10-30 s
// to detect Master's FM after launch; much of that is pkg-packaged Node
// cold start (unavoidable), but listen-at-end added avoidable delay.
// All route handlers reference hoisted async-function declarations so
// they work immediately even before later helpers finish setting up.
const server = http.createServer((req, res) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');

  if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return; }

  if (req.method === 'POST' && req.url === '/webhook') {
    let body = '';
    req.on('data', chunk => body += chunk);
    req.on('end', async () => {
      const ok = await handleWebhook(body);
      res.writeHead(ok ? 200 : 400);
      res.end(ok ? 'OK' : 'Bad Request');
    });
    return;
  }

  // ── Client-side log relay: overlay.html POSTs diagnostic messages here ──
  if (req.method === 'POST' && req.url === '/client-log') {
    let body = '';
    req.on('data', d => { body += d; });
    req.on('end', () => {
      try {
        const { level, msg } = JSON.parse(body);
        flog(`[OVERLAY/${(level||'INFO').toUpperCase()}] ${msg}`);
      } catch { flog(`[OVERLAY/RAW] ${body}`); }
      res.writeHead(204); res.end();
    });
    return;
  }

  if (req.method === 'POST' && req.url === '/reload-config') {
    try {
      const cfgPath = findConfigPath();
      const cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8').replace(/^\uFEFF/, ''));  // strip BOM
      const prevEnabled  = DISCORD_RPC_ENABLED;
      const prevClientId = DISCORD_RPC_CLIENTID;
      if (cfg.discord_rpc) {
        DISCORD_RPC_ENABLED  = cfg.discord_rpc.enabled !== false;
        DISCORD_RPC_CLIENTID = (cfg.discord_rpc.client_id || '').trim() || DEFAULT_DISCORD_CLIENT_ID;
      }
      const discordChanged = (prevEnabled !== DISCORD_RPC_ENABLED) || (prevClientId !== DISCORD_RPC_CLIENTID);
      if (discordChanged) {
        if (DISCORD_RPC_ENABLED && DISCORD_RPC_CLIENTID) {
          try { discord.init(DISCORD_RPC_CLIENTID, flog, _onDiscordReady); _lastDiscordSig = ''; pushDiscord(); } catch (e) { flog(`discord init error: ${e.message}`); }
        } else {
          try { discord.destroy(); } catch (e) { flog(`reload-config: discord.destroy threw: ${e && e.message}`); }
        }
        log(`Discord RPC reconfigured — enabled=${DISCORD_RPC_ENABLED} client_id=${DISCORD_RPC_CLIENTID ? 'set' : 'empty'}`);
      }
      log(`Config reloaded`);
      res.writeHead(200); res.end('OK');
    } catch (e) {
      flog(`reload-config error: ${e.message}`);
      res.writeHead(500); res.end(`Config read error: ${e.message}`);
    }
    return;
  }

  if (req.method === 'GET' && req.url === '/overlay-config') {
    // Re-reads config.json fresh every time so the overlay hot-reloads when the user
    // edits config.json (the overlay polls this endpoint every 5 seconds).
    try {
      const cfgPath = findConfigPath();
      const raw = fs.readFileSync(cfgPath, 'utf8').replace(/^\uFEFF/, '');
      const cfg = JSON.parse(raw);
      const overlayCfg = cfg.overlay || {};
      // v2.1.0 — include select top-level flags the overlay also needs so
      // its single-endpoint config fetch covers everything. `liveAudioVisualizer`
      // is flipped from the tray's quick-toggle menu item; the overlay reads
      // it to decide whether to use WASAPI-loopback spectrum or fall back.
      if (cfg.liveAudioVisualizer !== undefined) {
        overlayCfg.liveAudioVisualizer = !!cfg.liveAudioVisualizer;
      }
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(overlayCfg));
    } catch (e) {
      flog(`overlay-config error: ${e.message}`);
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end('{}');
    }
    return;
  }

  // ── /art: proxy current track art via localhost (avoids CDN CORS restrictions) ──
  if (req.method === 'GET' && req.url.startsWith('/art')) {
    const art = currentTrack?.trackArt || '';
    if (!art) { res.writeHead(404); res.end(); return; }
    if (art.startsWith('data:')) {
      try {
        const comma = art.indexOf(',');
        const mime  = (art.slice(5, comma).split(';')[0]) || 'image/jpeg';
        const buf   = Buffer.from(art.slice(comma + 1), 'base64');
        res.writeHead(200, { 'Content-Type': mime, 'Cache-Control': 'no-store', 'Access-Control-Allow-Origin': '*' });
        res.end(buf);
      } catch (e) { flog(`/art data-uri error: ${e.message}`); res.writeHead(500); res.end(); }
    } else {
      fetch(art)
        .then(r => r.arrayBuffer().then(ab => ({ ct: r.headers.get('content-type') || 'image/jpeg', ab })))
        .then(({ ct, ab }) => {
          res.writeHead(200, { 'Content-Type': ct, 'Cache-Control': 'no-store', 'Access-Control-Allow-Origin': '*' });
          res.end(Buffer.from(ab));
        })
        .catch(e => { flog(`/art proxy error: ${e.message}`); res.writeHead(502); res.end(); });
    }
    return;
  }

  if (req.method === 'GET' && req.url === '/current') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(currentTrack));
    return;
  }

  // Overlay polls this on load and periodically; when bootId changes (server
  // restart) it calls location.reload() so OBS Browser Source picks up fresh
  // HTML/JS/art instead of CEF's stale cached copy.
  if (req.method === 'GET' && req.url === '/version') {
    res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    res.end(JSON.stringify({ bootId: SERVER_BOOT_ID }));
    return;
  }

  // ── v9.2.0 SCREENSHOT ENDPOINT ────────────────────────────────────────
  // (state declared at module scope — see _ssScreenshot near sseClients)
  if (req.method === 'GET' && req.url === '/screenshot') {
    if (sseClients.size === 0) {
      res.writeHead(503, { 'Content-Type': 'text/plain' });
      res.end('No overlay connected (sseClients.size === 0)');
      return;
    }
    const requestId = String(_ssScreenshot.counter++);
    let timer = null;
    const done = (status, contentType, body) => {
      if (timer) { clearTimeout(timer); timer = null; }
      _ssScreenshot.pending = null;
      try {
        res.writeHead(status, { 'Content-Type': contentType, 'Cache-Control': 'no-cache' });
        res.end(body);
      } catch {}
    };
    _ssScreenshot.pending = {
      requestId,
      resolve: (pngBuffer) => done(200, 'image/png', pngBuffer),
      fail:    (msg)       => done(502, 'text/plain', String(msg)),
    };
    timer = setTimeout(() => {
      if (_ssScreenshot.pending && _ssScreenshot.pending.requestId === requestId) {
        done(504, 'text/plain', 'Overlay did not respond within 2500 ms');
      }
    }, 2500);
    const payload = `event: capture\ndata: ${JSON.stringify({ requestId })}\n\n`;
    for (const r of sseClients) {
      try { r.write(payload); } catch { sseClients.delete(r); }
    }
    return;
  }

  if (req.method === 'POST' && req.url === '/screenshot-response') {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      try {
        const j = JSON.parse(body);
        if (!_ssScreenshot.pending) {
          res.writeHead(409, { 'Content-Type': 'text/plain' });
          res.end('No screenshot pending');
          return;
        }
        if (j.requestId !== _ssScreenshot.pending.requestId) {
          res.writeHead(409, { 'Content-Type': 'text/plain' });
          res.end('requestId mismatch (stale response)');
          return;
        }
        if (j.error) {
          _ssScreenshot.pending.fail(j.error);
          res.writeHead(200); res.end('ok (forwarded error)');
          return;
        }
        const m = String(j.png || '').match(/^data:image\/png;base64,(.+)$/);
        if (!m) {
          _ssScreenshot.pending.fail('Bad PNG payload (no data:image/png prefix)');
          res.writeHead(200); res.end('ok (forwarded bad-format error)');
          return;
        }
        const pngBuffer = Buffer.from(m[1], 'base64');
        _ssScreenshot.pending.resolve(pngBuffer);
        res.writeHead(200); res.end('ok');
      } catch (e) {
        if (_ssScreenshot.pending) _ssScreenshot.pending.fail('Server-side parse error: ' + e.message);
        res.writeHead(400); res.end('parse error: ' + e.message);
      }
    });
    return;
  }
  // ── end v9.2.0 screenshot endpoint ────────────────────────────────────

  // ── /events: Server-Sent Events stream for real-time overlay updates ──
  if (req.method === 'GET' && req.url === '/events') {
    res.writeHead(200, {
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache, no-transform',
      'Connection': 'keep-alive',
      'X-Accel-Buffering': 'no',
    });
    // Send current state immediately so a fresh connection isn't blank
    res.write(`data: ${JSON.stringify(currentTrack)}\n\n`);
    sseClients.add(res);
    req.on('close', () => sseClients.delete(res));
    return;
  }

  // ── Static assets — icon + web-app manifest (used by the customizer window) ──
  if (req.method === 'GET' && (req.url === '/MastersFM.ico' || req.url === '/favicon.ico')) {
    try {
      const icoPath = path.join(process.cwd(), 'MastersFM.ico');
      const data = fs.readFileSync(icoPath);
      res.writeHead(200, { 'Content-Type': 'image/x-icon', 'Cache-Control': 'public,max-age=86400' });
      res.end(data);
    } catch { res.writeHead(404); res.end('icon not found'); }
    return;
  }

  if (req.method === 'GET' && req.url === '/manifest.json') {
    const manifest = {
      name: "Master's FM",
      short_name: "Master's FM",
      description: "Now Playing Overlay for OBS",
      start_url: "/customize",
      display: "standalone",
      background_color: "#0d0420",
      theme_color: "#6d28d9",
      icons: [{ src: "/MastersFM.ico", sizes: "256x256", type: "image/x-icon" }]
    };
    res.writeHead(200, { 'Content-Type': 'application/manifest+json' });
    res.end(JSON.stringify(manifest));
    return;
  }

  // ── Update progress page + status endpoint (v10.0.6) ─────────────────────
  if (req.method === 'GET' && req.url === '/update-status') {
    res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    try {
      const os = require('os');
      const statusFile = require('path').join(os.tmpdir(), 'mastersfm_update_status.json');
      res.end(fs.readFileSync(statusFile, 'utf8'));
    } catch {
      res.end('{"state":"idle","version":null,"progress":0,"bytesDown":0,"bytesTotal":0,"current":null,"ts":0}');
    }
    return;
  }

  if (req.method === 'GET' && req.url === '/update') {
    try {
      const htmlPath = findHtmlPath('update.html');
      const html = fs.readFileSync(htmlPath, 'utf8');
      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end(html);
    } catch {
      res.writeHead(404); res.end('update.html not found');
    }
    return;
  }

  if (req.method === 'GET' && (req.url === '/' || req.url.startsWith('/?'))) {
    try {
      const htmlPath = findHtmlPath('overlay.html');
      const html = fs.readFileSync(htmlPath, 'utf8');
      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end(html);
    } catch {
      log('❌ overlay.html not found');
      res.writeHead(500); res.end('overlay.html not found');
    }
    return;
  }

  // ── /preview-config GET — returns in-memory preview or falls back to saved ──
  if (req.method === 'GET' && req.url === '/preview-config') {
    try {
      if (previewConfig !== null) {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(previewConfig));
      } else {
        const cfgPath = findConfigPath();
        const raw = fs.readFileSync(cfgPath, 'utf8').replace(/^\uFEFF/, '');
        const cfg = JSON.parse(raw);
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(cfg.overlay || {}));
      }
    } catch (e) {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end('{}');
    }
    return;
  }

  // ── /preview-config POST — customizer sends live updates here ───────────────
  // Broadcasts to SSE clients as `event: preview-config` so the overlay can
  // apply changes within a single frame — no waiting on the 500ms /preview-
  // config poll fallback. Customize page fires one POST per control-change
  // event (slider, color picker, toggle) so drags feel real-time.
  if (req.method === 'POST' && req.url === '/preview-config') {
    let body = '';
    req.on('data', chunk => body += chunk);
    req.on('end', () => {
      try {
        previewConfig = JSON.parse(body);
        res.writeHead(200); res.end('OK');
        // Push to every connected SSE client — no serialization cost when
        // nobody's listening.
        if (sseClients.size > 0) {
          const payload = `event: preview-config\ndata: ${JSON.stringify(previewConfig)}\n\n`;
          for (const r of sseClients) {
            try { r.write(payload); } catch { sseClients.delete(r); }
          }
        }
      } catch (e) { res.writeHead(400); res.end('Bad JSON'); }
    });
    return;
  }

  // ── /list-presets GET — returns array of saved preset names ─────────────────
  if (req.method === 'GET' && req.url === '/list-presets') {
    try {
      const dir   = getPresetsDir();
      const names = fs.readdirSync(dir)
        .filter(f => f.endsWith('.json'))
        .map(f => f.slice(0, -5))
        .sort((a, b) => a.localeCompare(b));
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(names));
    } catch { res.writeHead(200); res.end('[]'); }
    return;
  }

  // ── /save-preset POST — body: {name, config} ─────────────────────────────────
  if (req.method === 'POST' && req.url === '/save-preset') {
    let body = '';
    req.on('data', chunk => body += chunk);
    req.on('end', () => {
      try {
        // PHASE B fix (2026-04-28): split JSON.parse into its own try so we
        // return 400 for bad input (matching /preview-config's pattern), and
        // validate config presence — previously empty-body and missing-config
        // fell through to JSON.stringify(undefined)=undefined → fs.writeFileSync
        // throws → caught below as 500. Per the procedure's "Causes wrong HTTP
        // status codes (e.g. 500 for bad input)" definition.
        let parsed;
        try { parsed = JSON.parse(body); }
        catch { res.writeHead(400); res.end('Bad JSON'); return; }
        const { name, config } = parsed || {};
        if (!name) { res.writeHead(400); res.end('Name required'); return; }
        if (config === undefined || config === null) {
          res.writeHead(400); res.end('Config required'); return;
        }
        const safe = name.replace(/[^a-zA-Z0-9 _\-]/g, '').trim().slice(0, 64);
        if (!safe) { res.writeHead(400); res.end('Invalid name'); return; }
        const file = path.join(getPresetsDir(), safe + '.json');
        fs.writeFileSync(file, JSON.stringify(config, null, 2), 'utf8');
        log(`Preset saved: ${safe}`);
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ saved: safe }));
      } catch (e) { res.writeHead(500); res.end(e.message); }
    });
    return;
  }

  // ── /load-preset GET — ?name=... returns overlay config object ───────────────
  if (req.method === 'GET' && req.url.startsWith('/load-preset')) {
    try {
      const name = new URLSearchParams(req.url.split('?')[1] || '').get('name') || '';
      const safe = name.replace(/[^a-zA-Z0-9 _\-]/g, '').trim();
      const file = path.join(getPresetsDir(), safe + '.json');
      const data = fs.readFileSync(file, 'utf8');
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(data);
    } catch { res.writeHead(404); res.end('Not found'); }
    return;
  }

  // ── /delete-preset DELETE — ?name=... ────────────────────────────────────────
  if (req.method === 'DELETE' && req.url.startsWith('/delete-preset')) {
    try {
      const name = new URLSearchParams(req.url.split('?')[1] || '').get('name') || '';
      const safe = name.replace(/[^a-zA-Z0-9 _\-]/g, '').trim();
      fs.unlinkSync(path.join(getPresetsDir(), safe + '.json'));
      log(`Preset deleted: ${safe}`);
      res.writeHead(200); res.end('OK');
    } catch { res.writeHead(404); res.end('Not found'); }
    return;
  }

  // ── /save-overlay-config POST — writes overlay config to disk ───────────────
  if (req.method === 'POST' && req.url === '/save-overlay-config') {
    let body = '';
    req.on('data', chunk => body += chunk);
    req.on('end', () => {
      try {
        // PHASE B fix (2026-04-28): same pattern as /save-preset — split
        // JSON.parse out so bad input returns 400, not 500.
        let overlay;
        try { overlay = JSON.parse(body); }
        catch { res.writeHead(400); res.end('Bad JSON'); return; }
        if (overlay === null || overlay === undefined) {
          res.writeHead(400); res.end('Body required'); return;
        }
        const cfgPath = findConfigPath();
        const raw = fs.readFileSync(cfgPath, 'utf8').replace(/^\uFEFF/, '');
        const cfg = JSON.parse(raw);
        cfg.overlay = overlay;
        fs.writeFileSync(cfgPath, JSON.stringify(cfg, null, 2), 'utf8');
        previewConfig = null;   // saved config is now authoritative
        log('Overlay config saved to disk');
        res.writeHead(200); res.end('OK');
        // Broadcast the persisted config as event: overlay-config so OBS /
        // customize preview both flip to the authoritative state the moment
        // Apply is clicked — no more 5-second wait for the next config poll.
        if (sseClients.size > 0) {
          const payload = `event: overlay-config\ndata: ${JSON.stringify(overlay)}\n\n`;
          for (const r of sseClients) {
            try { r.write(payload); } catch { sseClients.delete(r); }
          }
        }
      } catch (e) {
        flog(`save-overlay-config error: ${e.message}`);
        res.writeHead(500); res.end(`Save error: ${e.message}`);
      }
    });
    return;
  }

  // (v6.2.3: /obs-reset-position endpoint removed with the rest of the
  // placement stack. SERVER_BOOT_ID stays a `let` binding because the
  // customize page still polls /version for auto-reload-on-rebuild.)

  // (v9.4.0: /switch-renderer endpoint removed along with the canvas2d wipe.
  // WebGL is now the only renderer so there's nothing to switch. The sentinel
  // file + tray balloon notification flow is gone too.)

  // ── /customize GET — serve the web customizer page ──────────────────────────
  if (req.method === 'GET' && req.url === '/customize') {
    try {
      const htmlPath = findHtmlPath('customize.html');
      const html = fs.readFileSync(htmlPath, 'utf8');
      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end(html);
    } catch {
      log('❌ customize.html not found');
      res.writeHead(500); res.end('customize.html not found');
    }
    return;
  }

  // v8.2.1 — 405 Method Not Allowed for known paths with wrong verb (was 404).
  // Prior fuzz audit finding: PUT to /current, POST to /version, etc. all
  // returned 404. RFC 7231 says use 405 + Allow header when path is known
  // but method isn't. Two tables: exact-match routes and prefix-match routes
  // (the latter for /art?t=..., /load-preset?name=..., /delete-preset?name=...).
  const routeTable = {
    '/':                      ['GET'],
    '/webhook':               ['POST'],
    '/client-log':            ['POST'],
    '/reload-config':         ['POST'],
    '/overlay-config':        ['GET'],
    '/current':               ['GET'],
    '/version':               ['GET'],
    '/events':                ['GET'],
    '/MastersFM.ico':         ['GET'],
    '/favicon.ico':           ['GET'],
    '/manifest.json':         ['GET'],
    '/preview-config':        ['GET', 'POST'],
    '/list-presets':          ['GET'],
    '/save-preset':           ['POST'],
    '/save-overlay-config':   ['POST'],
    '/customize':             ['GET'],
  };
  const prefixRouteTable = {
    '/art':            ['GET'],
    '/load-preset':    ['GET'],
    '/delete-preset':  ['DELETE'],
  };
  const reqPath = req.url.split('?')[0];
  let allowed = routeTable[reqPath];
  if (!allowed) {
    for (const prefix in prefixRouteTable) {
      if (reqPath === prefix) { allowed = prefixRouteTable[prefix]; break; }
    }
  }
  if (allowed) {
    res.writeHead(405, { 'Allow': allowed.join(', '), 'Content-Type': 'text/plain' });
    res.end('Method Not Allowed. Allowed: ' + allowed.join(', '));
    return;
  }
  res.writeHead(404); res.end('Not found');
});

// v6.6.3: error handler + listen() live RIGHT HERE, immediately after
// createServer returns. Previously this was ~350 lines later at the
// bottom of the file — nothing between executed synchronously, but
// the move makes the intent explicit: bind the port as fast as the
// script will allow. pkg-packaged Node cold-start is ~2-5 s and adds
// most of the user-visible 10-30 s "OBS detects Master's FM" delay;
// this removes any avoidable JS-side latency on top of that.
server.on('error', (e) => {
  if (e.code === 'EADDRINUSE') log(`❌ Port ${PORT} already in use — close the other instance first.`);
  else log(`❌ Server error: ${e.message}`);
  process.exit(1);
});

// Bind to loopback only — OBS Browser Source accesses localhost:PORT which
// resolves to 127.0.0.1. Binding to 0.0.0.0 (all interfaces) triggers the
// Windows Defender Firewall "allow access?" prompt naming it "Node.js Server".
// Loopback traffic bypasses the Windows firewall entirely, so no prompt ever
// appears and the app doesn't need a firewall exception at all.
server.listen(PORT, '127.0.0.1', () => {
  console.log(`
╔══════════════════════════════════════════════╗
║   Master's FM — Now Playing Overlay Server   ║
╠══════════════════════════════════════════════╣
║  Webhook     →  http://localhost:${PORT}/webhook
║  OBS Source  →  http://localhost:${PORT}/
╚══════════════════════════════════════════════╝

Waiting for tracks...
`);
});
