using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// IHostedService: manages Discord Rich Presence lifecycle.
/// Ports server.js lines 205-336 (discord_rpc init + pushDiscord).
///
/// Stage 7.12 Batch B Phase B: now uses our native DiscordIpcClient instead of
/// Lachee.DiscordRPC.  Benefits:
///   - ActivityType is a first-class field on DiscordIpcActivity (no JsonConverter
///     hack against JsonConvert.DefaultSettings).
///   - SetActivityAsync awaits the actual pipe write (latency visible & bounded).
///   - No external NuGet dependency, full byte-level control over the protocol.
///
/// This service adds:
///   - Config loading  (discord_rpc.enabled, discord_rpc.client_id)
///   - Connection lifecycle + 30 s reconnect loop on disconnect
///   - 250 ms throttle + latest-wins coalescer (via DiscordRpcThrottle)
///   - Dedup + 30 s age-refresh (mirrors server.js _lastDiscordSig logic, lines 299-322)
///   - Activity building from currentTrack JsonNode (mirrors discord_rpc.js buildActivity, lines 209-258)
///
/// IMPORTANT: Do NOT simplify the dedup logic or remove the age-refresh.
/// The 30 s age cap ensures Discord self-heals if it ever missed a frame.
/// </summary>
internal sealed class DiscordRpcService : BackgroundService
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const string DefaultClientId     = "1495411843836018819";
    // Stage 7.12 Batch B Phase C #2 (real-time sync): 250 ms → 50 ms.
    //
    // Discord's 5/20 s rate limit is an AVERAGE, not a per-write floor.  After
    // a single state flip (pause/seek) we want to push immediately; the next
    // unrelated heartbeat is suppressed by _lastSig (PushDiscord) and the 30 s
    // age cap (DedupMaxAgeMs).  In normal use we send <1 write/s, well under
    // the 5/20 s ceiling — but during rapid scrub bursts the 50 ms coalescer
    // lets each scrub tick be visible within ~50-80 ms instead of 250 ms.
    //
    // The Phase B native pipe (DiscordIpcClient) writes in <2 ms, so 50 ms is
    // genuinely the latency the user sees on a back-to-back state flip.
    private const int    ThrottleMs          = 50;
    private const int    ReconnectIntervalMs = 30_000;   // loop interval for reconnect attempts
    private const long   DedupMaxAgeMs       = 30_000;   // 30 s max dedup age -- self-heal window
    private const int    ActivityTypeListening = 2;      // Discord ActivityType.Listening
    // Stage 7.12 Batch B Phase J rev2: tightened from 10 s to 5 s drift.
    // Discord interpolates the progress bar client-side from (now - start)
    // between our pushes, so the bar visually advances; each refresh snaps
    // it back to the actual pause position.  5 s is the floor — at 4 s
    // we'd hit Discord's 5-per-20-s rate limit ceiling when other pauses,
    // resumes, or seeks share the window.  5 s = 4 pushes per 20 s,
    // leaving headroom for one state-change push without triggering Phase
    // E's rate-limit deferral.  The explicit "⏸ M:SS / M:SS" indicator
    // baked into the activity's State line (BuildActivity below) is the
    // authoritative source of the pause time, so even mid-drift the user
    // sees a frozen "Paused at 2:30 / 5:00" in plain text.
    private const long   PauseBucketMs       = 5_000;

    // ── DI ────────────────────────────────────────────────────────────────────
    private readonly ServerState             _state;
    private readonly ILogger<DiscordRpcService> _logger;

    // ── Config (guarded by _lock) ─────────────────────────────────────────────
    private bool   _enabled  = false;
    private string _clientId = DefaultClientId;

    // ── Client + throttle (all guarded by _lock unless noted) ─────────────────
    private readonly object       _lock = new();
    private DiscordIpcClient?     _client;
    private DiscordRpcThrottle?   _throttle;
    private bool                  _connected;        // READY received and not yet closed
    private DiscordIpcActivity?   _pendingActivity;  // queued while disconnected; flushed on READY

    // ── Dedup (guarded by _lock) ──────────────────────────────────────────────
    private string _lastSig    = string.Empty;
    private long   _lastPushAt = 0L;

    // ─────────────────────────────────────────────────────────────────────────

    public DiscordRpcService(ServerState state, ILogger<DiscordRpcService> logger)
    {
        _state  = state;
        _logger = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial config load + client init on startup
        await ReloadConfigAsync(stoppingToken);

        // Reconnect loop: every 30 s, try to init if enabled but not connected.
        // Covers: Discord started after Master's FM, Discord crashed mid-session.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(ReconnectIntervalMs, stoppingToken); }
            catch (OperationCanceledException) { return; }

            bool shouldInit;
            lock (_lock) { shouldInit = _enabled && !_connected; }
            if (shouldInit)
            {
                _logger.LogInformation("Discord RPC: reconnect attempt");
                await TryInitClientAsync(stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeClient();
        await base.StopAsync(cancellationToken);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reload discord_rpc settings from config.json and re-init/destroy client as needed.
    /// Called by ConfigHandler POST /reload-config.
    /// Mirrors server.js lines 1119-1133.
    /// </summary>
    public async Task ReloadConfigAsync(CancellationToken ct = default)
    {
        bool   newEnabled  = false;
        string newClientId = DefaultClientId;

        var cfgPath = _state.ConfigPath;
        if (File.Exists(cfgPath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(cfgPath, new UTF8Encoding(false), ct);
                // Strip BOM if present (matching ConfigHandler 4.5 pattern)
                if (raw.StartsWith('﻿')) raw = raw[1..];
                var cfg     = JsonNode.Parse(raw)?.AsObject();
                var discord = cfg?["discord_rpc"]?.AsObject();
                if (discord != null)
                {
                    newEnabled  = discord["enabled"]?.GetValue<bool>() ?? true;
                    var id      = (discord["client_id"]?.GetValue<string>() ?? string.Empty).Trim();
                    newClientId = string.IsNullOrEmpty(id) ? DefaultClientId : id;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discord RPC: config read failed, keeping current settings");
                return;
            }
        }

        bool   prevEnabled;
        string prevClientId;
        bool   clientIdChanged;
        bool   enabledChanged;
        lock (_lock)
        {
            prevEnabled  = _enabled;
            prevClientId = _clientId;
            _enabled     = newEnabled;
            _clientId    = newClientId;

            clientIdChanged = !string.Equals(prevClientId, newClientId, StringComparison.Ordinal);
            enabledChanged  = prevEnabled != newEnabled;
        }

        if (clientIdChanged)
        {
            // Different App ID — old client can't represent it, full reinit.
            _logger.LogInformation(
                "Discord RPC: client_id changed ({Old} -> {New}); reinitialising",
                prevClientId == DefaultClientId ? "built-in" : "custom",
                newClientId  == DefaultClientId ? "built-in" : "custom");
            DisposeClient();
            if (newEnabled)
            {
                lock (_lock) { _lastSig = string.Empty; }
                await TryInitClientAsync(ct);
            }
        }
        else if (enabledChanged)
        {
            // Stage 7.12 Batch B DIAG-10 rev2: only the enabled flag flipped.
            // Don't churn the client on every toggle — a Dispose/Initialize cycle
            // inside the same process leaves Discord in a state where the
            // re-connected client is acknowledged by IPC but not surfaced in
            // the user's profile activity.  Keep the pipe alive, just clear or
            // restore the displayed presence.
            if (newEnabled)
            {
                _logger.LogInformation("Discord RPC: re-enabled (client kept alive)");
                bool needInit;
                lock (_lock)
                {
                    _lastSig = string.Empty;   // force next PushDiscord through
                    needInit = _client == null;
                }
                if (needInit) await TryInitClientAsync(ct);
                // Caller (PushDiscord on next track update) will set presence.
            }
            else
            {
                _logger.LogInformation("Discord RPC: disabled (clearing presence, client kept alive)");
                DiscordIpcClient? clientToClear = null;
                lock (_lock)
                {
                    if (_connected) clientToClear = _client;
                    _lastSig    = "__cleared__";
                    _lastPushAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                if (clientToClear != null)
                {
                    try { await clientToClear.SetActivityAsync(null, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Discord RPC: clear presence on disable failed"); }
                }
            }
        }
        // (no change to either field — nothing to do)
    }

    /// <summary>
    /// Push current track state to Discord via dedup + throttle.
    /// Mirrors server.js pushDiscord() (lines 301-336).
    ///
    /// Call sites (mirrors sseBroadcast() which always calls pushDiscord):
    ///   1. After new-track is set, BEFORE cascade (early push -- shows track immediately)
    ///   2. After cascade resolves (push with final art URL)
    ///   3. Always-fires broadcast at end of HandleAsync (covers heartbeat pause/resume/seek)
    ///   4. After B11 art-retry resolves
    ///
    /// Thread-safe -- all accesses guarded by _lock.
    /// </summary>
    public void PushDiscord(JsonNode? currentTrack)
    {
        bool enabled;
        bool connected;
        lock (_lock) { enabled = _enabled; connected = _connected; }
        _logger.LogInformation(
            "Discord RPC: PushDiscord enter (enabled={Enabled} connected={Connected} hasTrack={HasTrack})",
            enabled, connected, currentTrack != null);
        if (!enabled) return;

        // ── No track: clear activity ──────────────────────────────────────────
        if (currentTrack == null)
        {
            DiscordIpcClient? clientToClear = null;
            lock (_lock)
            {
                if (_lastSig == "__cleared__") return;
                _lastSig    = "__cleared__";
                _lastPushAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_connected) clientToClear = _client;
            }
            if (clientToClear != null)
                _ = clientToClear.SetActivityAsync(null, CancellationToken.None);
            return;
        }

        // ── Extract track fields ──────────────────────────────────────────────
        var  artist    = (string?)currentTrack["artist"]    ?? string.Empty;
        var  track     = (string?)currentTrack["track"]     ?? string.Empty;
        var  source    = (string?)currentTrack["source"]    ?? string.Empty;
        long startedAt = (long?)currentTrack["startedAt"]?.GetValue<long>() ?? 0L;
        long duration  = (long?)currentTrack["duration"]?.GetValue<long>()  ?? 0L;
        bool isPaused  = currentTrack["isPaused"]?.GetValue<bool>() == true;
        // Stage 7.12 Batch B Phase J: pausedAt is the wall-clock ms when the
        // server's B5 pause handler ran.  Used to compute "progress at pause"
        // = pausedAt − startedAt, which we then bake into Discord's
        // timestamps.start so the progress bar shows the frozen position
        // instead of Discord falling back to "elapsed since app open".
        long pausedAt  = (long?)currentTrack["pausedAt"]?.GetValue<long>()  ?? 0L;
        var  artRaw      = (string?)currentTrack["trackArt"]      ?? string.Empty;
        var  artHttpsRaw = (string?)currentTrack["trackArtHttps"] ?? string.Empty;
        var  originUrl   = (string?)currentTrack["originUrl"]     ?? string.Empty;

        // safeArt prefers the HTTPS-only field (added by ArtCascade specifically
        // for Discord, since the primary trackArt is often a data: URI from
        // Windows SMTC which Discord's CDN rejects).  Falls back to trackArt if
        // it happens to already be an HTTPS URL.
        var safeArt = IsHttpUrl(artHttpsRaw)
            ? artHttpsRaw
            : (IsHttpUrl(artRaw) ? artRaw : string.Empty);

        // ── Dedup ─────────────────────────────────────────────────────────────
        // Includes startedAt so seeks re-push the activity (shifted end time).
        //
        // Phase J: when paused, add a 10-second "pause bucket" to the sig.
        // Discord's progress bar is rendered client-side from the timestamps
        // we send, so the bar visually advances during the interval between
        // our pushes — we periodically re-push fresh start/end values to
        // re-pin the displayed position at the pause point.  Bucket flips
        // every 10 s while paused → push fires every 10 s → ~10 s max drift.
        var  nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long pauseBucket = isPaused ? (nowMs / PauseBucketMs) : 0L;
        var  sig   = string.Join("|", artist, track, source, startedAt, duration,
                                      isPaused ? 1 : 0, safeArt, originUrl, pauseBucket);

        lock (_lock)
        {
            var ageMs = nowMs - _lastPushAt;
            if (sig == _lastSig && ageMs < DedupMaxAgeMs) return;
            _lastSig    = sig;
            _lastPushAt = nowMs;
        }

        // ── Build activity ────────────────────────────────────────────────────
        var activity = BuildActivity(artist, track, source, startedAt, duration, isPaused,
                                     pausedAt, safeArt, originUrl);
        if (activity == null) return;

        // ── Queue through throttle (or hold as pending if disconnected) ───────
        DiscordRpcThrottle? throttle;
        lock (_lock)
        {
            if (!_connected)
            {
                // Hold for flush on READY (mirrors discord_rpc.js pendingState, line 277)
                _pendingActivity = activity;
                return;
            }
            throttle = _throttle;
        }
        throttle?.Queue(activity);
    }

    // ── Client lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Initialize (or re-initialize) the Discord IPC client.
    /// Creates a fresh throttle (resets rate-limit state, mirrors discord_rpc.js line 103).
    /// Caller must NOT hold _lock (we await network I/O).
    /// </summary>
    private async Task TryInitClientAsync(CancellationToken ct)
    {
        DisposeClient();

        string clientId;
        lock (_lock) { clientId = _clientId; }

        var client = new DiscordIpcClient(clientId, _logger);
        client.Ready  += OnReady;
        client.Closed += OnClosed;
        client.Error  += OnError;

        lock (_lock)
        {
            _client = client;
            // Fresh throttle for each connection: resets lastSentAt to 0, so the
            // first post-READY update fires immediately (mirrors discord_rpc.js line 103).
            _throttle = new DiscordRpcThrottle(ThrottleMs, SendActivityAsync, _logger);
        }

        try
        {
            var ok = await client.ConnectAsync(ct);
            if (!ok)
            {
                _logger.LogInformation("Discord RPC: ConnectAsync returned false (is Discord running?)");
                DisposeClient();
                return;
            }
            _logger.LogInformation("Discord RPC: client initialized (client_id={ClientId})", clientId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord RPC: TryInitClientAsync failed");
            DisposeClient();
        }
    }

    /// <summary>Dispose client + throttle (idempotent, lock-safe).</summary>
    private void DisposeClient()
    {
        DiscordIpcClient?   client;
        DiscordRpcThrottle? throttle;
        lock (_lock)
        {
            _connected = false;
            client     = _client;
            throttle   = _throttle;
            _client    = null;
            _throttle  = null;
        }
        if (client != null)
        {
            client.Ready  -= OnReady;
            client.Closed -= OnClosed;
            client.Error  -= OnError;
            try { client.Dispose(); } catch { }
        }
        throttle?.Dispose();
    }

    /// <summary>
    /// Throttle callback -- awaits the actual SetActivity pipe write.
    /// Snapshots the client under the lock so a concurrent reconnect can't NRE us.
    /// </summary>
    private async Task SendActivityAsync(DiscordIpcActivity? activity)
    {
        DiscordIpcClient? client;
        lock (_lock)
        {
            if (!_connected) return;
            client = _client;
        }
        if (client == null) return;

        try
        {
            await client.SetActivityAsync(activity, CancellationToken.None);
            _logger.LogInformation(
                "Discord RPC: SetActivity sent (details='{Details}' state='{State}' largeImg='{Large}' tsStart={TsStart} tsEnd={TsEnd} largeImgIsUrl={IsUrl})",
                activity?.Details                ?? "(null)",
                activity?.State                  ?? "(null)",
                activity?.LargeImage             ?? "(null)",
                activity?.StartUnixMs?.ToString() ?? "(null)",
                activity?.EndUnixMs?.ToString()   ?? "(null)",
                IsHttpUrl(activity?.LargeImage));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Discord RPC: SetActivity error"); }
    }

    // ── Discord event handlers (fired from DiscordIpcClient read loop) ────────

    private void OnReady(string user)
    {
        _logger.LogInformation("Discord RPC: connected as {User}", user);
        DiscordIpcActivity?  pending;
        DiscordRpcThrottle? throttle;
        lock (_lock)
        {
            _connected = true;
            // Reset dedup so the next PushDiscord always fires a fresh SET_ACTIVITY.
            // Mirrors discord_rpc.js _onDiscordReady: _lastDiscordSig = '' (server.js line 281).
            _lastSig         = string.Empty;
            pending          = _pendingActivity;
            _pendingActivity = null;
            throttle         = _throttle;
        }
        // Flush any activity queued while we were disconnected.
        if (pending != null) throttle?.Queue(pending);
    }

    private void OnClosed(string? reason)
    {
        _logger.LogInformation("Discord RPC: disconnected (reason={Reason})", reason ?? "(none)");
        lock (_lock) { _connected = false; }
    }

    private void OnError(string message)
    {
        _logger.LogWarning("Discord RPC: error {Msg}", message);
    }

    // ── Activity building ─────────────────────────────────────────────────────

    /// <summary>
    /// Build a DiscordIpcActivity from track state.
    /// Mirrors discord_rpc.js buildActivity() (lines 209-258) field-by-field.
    ///
    /// Mapping:
    ///   type             = 2 (Listening) — shows progress bar in Discord UI
    ///   details          = track (clamp 128) || "Unknown track"
    ///   state            = "by {artist}" || source (clamp 128, min 2)
    ///   start/end (playing) = startedAt and startedAt+duration
    ///   start/end (paused)  = pinned so (now − start) = (pausedAt − startedAt),
    ///                         so the progress bar shows the frozen pause position
    ///                         (Phase J — was: no timestamps, Discord defaulted
    ///                         to "app-open time" which made the bar disappear)
    ///   large_image      = trackArt (https) || "mastersfm_logo"
    ///   large_text       = srcName (clamp 128)
    ///   small_image      = "mastersfm_logo"
    ///   small_text       = isPaused ? "⏸ Paused" : "Master's FM"
    ///   buttons[0]       = {label:"Listen on {src}"(clamp 32), url:originUrl} if https URL
    /// </summary>
    private static DiscordIpcActivity? BuildActivity(
        string artist, string track, string source,
        long startedAtMs, long durationMs, bool isPaused,
        long pausedAtMs,
        string trackArt, string originUrl)
    {
        if (string.IsNullOrEmpty(track)) return null;

        // Source name shown in large_text + button label
        var srcName = string.IsNullOrWhiteSpace(source) ? "Master's FM" : source.Trim();

        // Two visible lines. Discord enforces >= 2 chars per line.
        var details   = Clamp(track, 128);
        if (string.IsNullOrEmpty(details)) details = "Unknown track";

        // ── Compute the paused progress upfront (used by both the State text
        //    and the timestamps below, so we only do it once) ─────────────────
        long pausedProgressMs = 0;
        long nowWallMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (isPaused && startedAtMs > 0)
        {
            var refMs = pausedAtMs > 0 ? pausedAtMs : nowWallMs;
            pausedProgressMs = refMs - startedAtMs;
            if (pausedProgressMs < 0) pausedProgressMs = 0;
            if (durationMs > 0 && pausedProgressMs > durationMs) pausedProgressMs = durationMs;
        }

        // ── State (second line) ───────────────────────────────────────────────
        // Phase J rev2: when paused, the State text now embeds the frozen
        // "⏸ M:SS / M:SS" string.  Discord's progress bar interpolates client-
        // side between our pushes (so visually it advances by up to ~5 s
        // before our next push snaps it back), but the State text is plain
        // and doesn't move at all — that's the authoritative "we are paused
        // at exactly here" indicator the user sees, even mid-drift.
        var stateText = !string.IsNullOrEmpty(artist) ? $"by {artist}" : srcName;
        if (isPaused)
        {
            var posStr = FormatMmSs(pausedProgressMs);
            var durStr = durationMs > 0 ? $" / {FormatMmSs(durationMs)}" : string.Empty;
            stateText = !string.IsNullOrEmpty(artist)
                ? $"by {artist}  •  ⏸ {posStr}{durStr}"
                : $"⏸ {posStr}{durStr}";
        }
        stateText = Clamp(stateText, 128);
        if (stateText.Length < 2) stateText = "Master's FM";

        var activity = new DiscordIpcActivity
        {
            Type    = ActivityTypeListening,   // 2 = Listening → progress bar
            Details = details,
            State   = stateText,
        };

        // ── Timestamps ────────────────────────────────────────────────────────
        // Playing: start = original track-start wall-clock, end = start + duration.
        // Paused (Phase J): pin start so (now − start) == pausedProgressMs.
        // Bar shows the frozen pause position (with Discord's client
        // interpolating between our 5-s pushes — see PauseBucketMs).
        if (!isPaused && startedAtMs > 0)
        {
            activity.StartUnixMs = startedAtMs;
            if (durationMs > 0 && startedAtMs + durationMs > startedAtMs)
                activity.EndUnixMs = startedAtMs + durationMs;
        }
        else if (isPaused && startedAtMs > 0)
        {
            activity.StartUnixMs = nowWallMs - pausedProgressMs;
            if (durationMs > 0)
                activity.EndUnixMs = activity.StartUnixMs + durationMs;
        }

        // Images
        var hasHttpsArt = IsHttpUrl(trackArt);
        activity.LargeImage = hasHttpsArt ? trackArt : "mastersfm_logo";
        activity.LargeText  = Clamp(srcName, 128);
        activity.SmallImage = "mastersfm_logo";
        activity.SmallText  = isPaused ? "⏸ Paused" : "Master's FM";

        // Button -- "Listen on [Source]"; only when originUrl is an HTTPS URL.
        if (IsHttpUrl(originUrl))
        {
            var btnLabel = Clamp($"Listen on {srcName}", 32);
            activity.Buttons = new[] { new DiscordIpcButton { Label = btnLabel, Url = originUrl } };
        }

        return activity;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string Clamp(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen];

    /// <summary>Format a millisecond duration as "M:SS" (e.g. 154321 → "2:34").</summary>
    private static string FormatMmSs(long ms)
    {
        if (ms < 0) ms = 0;
        var totalSec = ms / 1000;
        var min = totalSec / 60;
        var sec = totalSec % 60;
        return $"{min}:{sec:D2}";
    }

    private static bool IsHttpUrl(string? s)
        => !string.IsNullOrEmpty(s)
           && (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("http://",  StringComparison.OrdinalIgnoreCase));
}
