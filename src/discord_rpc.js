// ─────────────────────────────────────────────────────────────────────────────
// discord_rpc.js — Zero-dependency Discord Rich Presence client.
//
// Connects to the local Discord desktop client via its named pipe IPC
// (\\?\pipe\discord-ipc-0 .. discord-ipc-9), performs the HANDSHAKE, and
// sends SET_ACTIVITY commands whenever the current track changes.
//
// Protocol frames are 8-byte header + UTF-8 JSON payload:
//   [4 bytes opcode LE] [4 bytes payload length LE] [payload]
// Opcodes: 0=HANDSHAKE  1=FRAME  2=CLOSE  3=PING  4=PONG
//
// Public API:
//   init(clientId)                — open pipe + handshake (idempotent)
//   update({artist, track, source, startedAt, duration, isPaused, trackArt})
//   clear()                       — remove activity
//   destroy()                     — close pipe
// All methods are safe to call before the pipe is ready; pending updates
// are coalesced to the latest state and flushed on READY.
// ─────────────────────────────────────────────────────────────────────────────
const net  = require('net');
const { randomBytes } = require('crypto');

const OP_HANDSHAKE = 0;
const OP_FRAME     = 1;
const OP_CLOSE     = 2;
const OP_PING      = 3;
const OP_PONG      = 4;

let socket        = null;
let connected     = false;        // pipe open AND handshake READY received
let connecting    = false;        // mid-connect (pipe open in progress OR handshake not yet READY)
let clientId      = '';
let pendingState  = null;         // latest update() payload; flushed on READY
let reconnectTimer = null;
let recvBuffer    = Buffer.alloc(0);
let pid           = process.pid;
let logFn         = () => {};     // caller wires this up (server.js log())
let onReadyFn     = null;         // fired on each (re)connect so server can force-push

// ── Write-side throttle + coalescer (v6.0.4) ─────────────────────────────────
// Discord RPC enforces ~5 SET_ACTIVITY per 20 s per client. Exceeding it
// makes the pipe hang / drop / silently stop updating — exactly the "RPC
// hangs when I spam tracks" bug users report. Track changes typically
// produce 2+ writes each (initial push with placeholder art, then a
// second push when real art resolves asynchronously), so even moderate
// skipping can push 10+ writes in a few seconds.
//
// Fix: enforce a minimum gap between SET_ACTIVITY writes. If sendActivity
// is called during the throttle window, the incoming state REPLACES any
// previous pending state (latest-wins coalescing — Discord only cares
// about the final state, not intermediate ones) and a timer fires the
// coalesced write at the window boundary.
//
// MIN_SEND_INTERVAL_MS = 2000 gives 10 writes per 20 s — comfortably over
// the 5/20s hard limit headroom-wise but still feels instantaneous for
// normal track changes (which occur once per ~30 s - 5 min of music).
// Rapid skip-spamming now collapses to ONE write per 2 s window with the
// final state, instead of flooding the pipe.
const MIN_SEND_INTERVAL_MS = 2000;
let lastSentAt      = 0;          // wall-clock of last successful writeFrame(SET_ACTIVITY)
let throttleTimer   = null;       // pending coalesced write
let rateLimitedUntil = 0;         // if Discord complained, back off extra until this ms

function log(msg) { try { logFn(`[discord] ${msg}`); } catch {} }

function pipePath(n) {
  // On Windows Discord uses \\?\pipe\discord-ipc-N
  // On *nix it's $XDG_RUNTIME_DIR/discord-ipc-N (not relevant here, but kept for portability)
  if (process.platform === 'win32') return `\\\\?\\pipe\\discord-ipc-${n}`;
  const base = process.env.XDG_RUNTIME_DIR || process.env.TMPDIR || '/tmp';
  return `${base}/discord-ipc-${n}`;
}

function encodeFrame(op, payload) {
  const json = Buffer.from(JSON.stringify(payload), 'utf8');
  const hdr  = Buffer.alloc(8);
  hdr.writeUInt32LE(op, 0);
  hdr.writeUInt32LE(json.length, 4);
  return Buffer.concat([hdr, json]);
}

function writeFrame(op, payload) {
  if (!socket || socket.destroyed) return false;
  try {
    socket.write(encodeFrame(op, payload));
    return true;
  } catch (e) {
    log(`write error: ${e.message}`);
    return false;
  }
}

function handleFrame(op, payload) {
  if (op === OP_PING) { writeFrame(OP_PONG, payload); return; }
  if (op === OP_CLOSE) { log(`pipe CLOSE from Discord: ${JSON.stringify(payload)}`); scheduleReconnect(); return; }
  if (op !== OP_FRAME) return;

  if (payload && payload.cmd === 'DISPATCH' && payload.evt === 'READY') {
    connected  = true;
    connecting = false;
    // Fresh connection = fresh Discord-side rate-limit window, reset our
    // throttle clock so the first post-READY update fires immediately.
    lastSentAt      = 0;
    rateLimitedUntil = 0;
    const user = payload.data && payload.data.user;
    log(`READY (user=${user ? user.username || user.id : 'unknown'})`);
    // Flush any activity the caller queued while we were still connecting
    if (pendingState) {
      const s = pendingState; pendingState = null;
      sendActivity(s);
    }
    // Signal the caller so it can force-push current state + reset its own
    // dedup cache — otherwise activity stays blank in Discord after a
    // reconnect if no state changes happened during the disconnect window.
    if (onReadyFn) { try { onReadyFn(); } catch (e) { log(`onReady cb threw: ${e.message}`); } }
  } else if (payload && payload.evt === 'ERROR') {
    const errMsg = JSON.stringify(payload.data || payload);
    log(`Discord ERROR: ${errMsg}`);
    // If Discord complained about rate limiting, back off an extra 10 s on
    // top of the normal 2 s window so we definitely fall under the limit.
    // Discord's RPC error text contains "rate" / "too many requests" /
    // code 4006 in the few known rate-limit response variants.
    if (/rate|too many|4006/i.test(errMsg)) {
      rateLimitedUntil = Date.now() + 10000;
      log(`rate-limited by Discord — backing off 10 s`);
    }
  }
}

function onData(chunk) {
  recvBuffer = Buffer.concat([recvBuffer, chunk]);
  while (recvBuffer.length >= 8) {
    const op  = recvBuffer.readUInt32LE(0);
    const len = recvBuffer.readUInt32LE(4);
    if (recvBuffer.length < 8 + len) break;
    const body = recvBuffer.slice(8, 8 + len).toString('utf8');
    recvBuffer = recvBuffer.slice(8 + len);
    let json = null;
    try { json = JSON.parse(body); } catch (e) { log(`frame JSON.parse failed (body ${body.length} chars, op=${op}): ${e.message}`); json = null; }
    try { handleFrame(op, json); } catch (e) { log(`handleFrame threw: ${e.message}`); }
  }
}

function tryConnectPipe(n, done) {
  // Walk pipes 0..9 — multiple Discord clients (stable/canary/ptb) can coexist
  if (n > 9) { done(new Error('no discord-ipc pipe found (is Discord running?)')); return; }
  const path = pipePath(n);
  const sock = net.createConnection(path);
  let settled = false;
  const onErr = () => {
    if (settled) return; settled = true;
    sock.removeAllListeners();
    sock.destroy();
    tryConnectPipe(n + 1, done);
  };
  sock.once('error', onErr);
  sock.once('connect', () => {
    if (settled) return; settled = true;
    sock.removeListener('error', onErr);
    done(null, sock, n);
  });
}

function scheduleReconnect(delayMs = 5000) {
  if (reconnectTimer) return;
  connected  = false;
  connecting = false;
  if (socket) {
    try { socket.destroy(); } catch {}
    socket = null;
  }
  recvBuffer = Buffer.alloc(0);
  // Cancel any pending throttled write — the pipe's dead, firing would
  // just log a write error and fall through. READY on reconnect will
  // replay pendingState cleanly.
  if (throttleTimer) { clearTimeout(throttleTimer); throttleTimer = null; }
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, delayMs);
}

function connect() {
  if (!clientId) return;           // no-op until a client_id is configured
  if (connected || connecting) return;
  connecting = true;
  tryConnectPipe(0, (err, sock, n) => {
    if (err) {
      log(`connect failed: ${err.message} — retry in 10s`);
      connecting = false;
      // Discord may not be running yet; retry on a modest cadence. v6.0.4:
      // was 30 s (too slow — opening Discord after Master's FM meant 30 s
      // of no presence). 10 s is still quiet when Discord is genuinely
      // not installed, but recovers fast in the common case.
      if (!reconnectTimer) reconnectTimer = setTimeout(() => { reconnectTimer = null; connect(); }, 10000);
      return;
    }
    socket = sock;
    log(`connected to discord-ipc-${n}`);
    socket.on('data',  onData);
    socket.on('error', (e) => { log(`socket error: ${e.message}`); scheduleReconnect(); });
    socket.on('close', ()  => { log(`socket closed`); scheduleReconnect(); });
    // HANDSHAKE — must be the very first frame
    writeFrame(OP_HANDSHAKE, { v: 1, client_id: clientId });
    // READY will arrive via onData → handleFrame
  });
}

function buildActivity(state) {
  if (!state || !state.track) return null;

  const act = {};

  // "LISTENING TO [name]" header — always "Master's FM" so friends see the
  // app name rather than the platform. Source still shown in the state line.
  const srcName = (state.source || 'Master\'s FM').trim();
  act.name = 'Master\'s FM';

  // Two visible lines in the Discord presence card. Discord enforces ≥2 chars per line.
  act.details = (state.track || '').slice(0, 128) || 'Unknown track';
  const by    = state.artist ? `by ${state.artist}` : srcName;
  act.state   = by.slice(0, 128);
  if (act.state.length < 2) act.state = 'Master\'s FM';

  // Timestamps — only while playing. Paused tracks just show no timer.
  if (!state.isPaused && state.startedAt) {
    // startedAt is epoch ms; Discord wants seconds.
    const startSec = Math.floor(state.startedAt / 1000);
    act.timestamps = { start: startSec };
    if (state.duration && state.duration > 0) {
      const endSec = Math.floor((state.startedAt + state.duration) / 1000);
      if (endSec > startSec) act.timestamps.end = endSec;
    }
  }

  // Images — Discord accepts registered asset keys OR arbitrary HTTPS URLs.
  // data: URIs are rejected. small_image uses the registered 'mastersfm_logo'
  // asset (upload it once in the Discord app's Rich Presence assets panel).
  const art = state.trackArt || '';
  const isHttpsArt = /^https?:\/\//i.test(art);
  act.assets = {
    large_image: isHttpsArt ? art : 'mastersfm_logo',
    large_text:  srcName.slice(0, 128),
    small_image: 'mastersfm_logo',
    small_text:  state.isPaused ? '⏸ Paused' : 'Master\'s FM',
  };

  // Button — "Listen on [Source]" links to the track page when a URL is known.
  // Discord allows max 2 buttons, labels ≤ 32 chars. Only add when URL is HTTPS.
  const url = (state.originUrl || '').trim();
  if (/^https?:\/\//i.test(url)) {
    const btnLabel = `Listen on ${srcName}`.slice(0, 32);
    act.buttons = [{ label: btnLabel, url }];
  }

  act.type = 2; // ActivityType.Listening → "LISTENING TO [name]"
  return act;
}

// Actually write a SET_ACTIVITY frame to the pipe. Separated from
// sendActivity() so the throttle-timer path can call this without
// re-entering the throttle logic.
function _writeActivity(state) {
  const activity = buildActivity(state);
  const nonce = randomBytes(8).toString('hex');
  const payload = activity
    ? { cmd: 'SET_ACTIVITY', args: { pid, activity }, nonce }
    : { cmd: 'SET_ACTIVITY', args: { pid }, nonce };   // null activity → clear
  const ok = writeFrame(OP_FRAME, payload);
  if (ok) lastSentAt = Date.now();
  return ok;
}

// Public send — always routes through the throttle/coalescer so rapid
// track-spamming doesn't blow past Discord's 5-per-20s rate limit.
function sendActivity(state) {
  if (!connected) { pendingState = state; return; }

  const now   = Date.now();
  const floor = Math.max(lastSentAt + MIN_SEND_INTERVAL_MS, rateLimitedUntil);

  if (now >= floor) {
    // Inside the allowed window — write immediately.
    // Cancel any stale coalesce timer since we're sending now anyway.
    if (throttleTimer) { clearTimeout(throttleTimer); throttleTimer = null; }
    pendingState = null;
    _writeActivity(state);
    return;
  }

  // Throttled: keep LATEST state (collapse intermediate skips) and ensure
  // a timer is armed to fire at the window boundary.
  pendingState = state;
  if (!throttleTimer) {
    const wait = floor - now;
    throttleTimer = setTimeout(() => {
      throttleTimer = null;
      if (!connected) return;            // disconnected while waiting — handleFrame READY will re-flush
      const s = pendingState; pendingState = null;
      if (s !== null) _writeActivity(s);
    }, wait);
  }
}

function init(id, logger, onReady) {
  if (typeof logger  === 'function') logFn     = logger;
  if (typeof onReady === 'function') onReadyFn = onReady;
  const trimmed = (id || '').trim();
  if (!trimmed) {
    log('client_id not set — Discord RPC disabled. Add discord_rpc.client_id to config.json.');
    return;
  }
  // Reconnect on client_id change
  if (trimmed !== clientId) {
    clientId = trimmed;
    scheduleReconnect(0);
  } else if (!connected && !connecting) {
    connect();
  }
}

function update(state) { sendActivity(state); }

function clear() {
  pendingState = null;
  if (connected) sendActivity(null);
}

function destroy() {
  if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
  if (throttleTimer)  { clearTimeout(throttleTimer);  throttleTimer  = null; }
  if (socket) { try { socket.destroy(); } catch {} socket = null; }
  connected = false; connecting = false; recvBuffer = Buffer.alloc(0);
  pendingState = null; lastSentAt = 0; rateLimitedUntil = 0;
}

module.exports = { init, update, clear, destroy };
