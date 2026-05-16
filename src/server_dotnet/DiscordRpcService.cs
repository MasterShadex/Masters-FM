using DiscordRPC;
using DiscordRPC.Message;
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
/// Ports server.js lines 205-336 (discord_rpc init + pushDiscord) via Lachee.DiscordRPC NuGet.
///
/// Lachee handles IPC protocol; this service adds:
///   - Config loading  (discord_rpc.enabled, discord_rpc.client_id)
///   - Connection lifecycle + 30 s reconnect loop on disconnect
///   - 2000 ms throttle + latest-wins coalescer (via DiscordRpcThrottle)
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
    private const int    ThrottleMs          = 2000;
    private const int    ReconnectIntervalMs = 30_000;   // loop interval for reconnect attempts
    private const long   DedupMaxAgeMs       = 30_000;   // 30 s max dedup age -- self-heal window

    // ── DI ────────────────────────────────────────────────────────────────────
    private readonly ServerState             _state;
    private readonly ILogger<DiscordRpcService> _logger;

    // ── Config (guarded by _lock) ─────────────────────────────────────────────
    private bool   _enabled  = false;
    private string _clientId = DefaultClientId;

    // ── Client + throttle (all guarded by _lock) ──────────────────────────────
    private readonly object       _lock = new();
    private DiscordRpcClient?     _client;
    private DiscordRpcThrottle?   _throttle;
    private bool                  _connected;        // READY received and not yet closed
    private RichPresence?         _pendingPresence;  // queued while disconnected; flushed on READY

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

            lock (_lock)
            {
                if (_enabled && !_connected)
                {
                    _logger.LogInformation("Discord RPC: reconnect attempt");
                    TryInitClient();
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock) { DisposeClient(); }
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
        lock (_lock)
        {
            prevEnabled  = _enabled;
            prevClientId = _clientId;
            _enabled     = newEnabled;
            _clientId    = newClientId;

            bool clientIdChanged = !string.Equals(prevClientId, newClientId, StringComparison.Ordinal);
            bool enabledChanged  = prevEnabled != newEnabled;

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
                    _lastSig = string.Empty;
                    TryInitClient();
                }
            }
            else if (enabledChanged)
            {
                // Stage 7.12 Batch B DIAG-10 rev2: only the enabled flag flipped.
                // Don't churn the Lachee DiscordRpcClient on every toggle — a
                // Dispose/Initialize cycle inside the same process leaves Discord
                // in a state where the re-connected client is acknowledged by IPC
                // but not surfaced in the user's profile activity.  Keep the
                // pipe alive, just clear or restore the displayed presence.
                if (newEnabled)
                {
                    _logger.LogInformation("Discord RPC: re-enabled (client kept alive)");
                    _lastSig = string.Empty;   // force next PushDiscord through
                    if (_client == null) TryInitClient();
                    // Caller (PushDiscord on next track update) will set presence.
                }
                else
                {
                    _logger.LogInformation("Discord RPC: disabled (clearing presence, client kept alive)");
                    if (_connected)
                    {
                        try   { _client?.SetPresence(null); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Discord RPC: clear presence on disable failed"); }
                    }
                    _lastSig    = "__cleared__";
                    _lastPushAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            // (no change to either field — nothing to do)
        }
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
            lock (_lock)
            {
                if (_lastSig == "__cleared__") return;
                _lastSig    = "__cleared__";
                _lastPushAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_connected) _client?.SetPresence(null);
            }
            return;
        }

        // ── Extract track fields ──────────────────────────────────────────────
        var  artist    = (string?)currentTrack["artist"]    ?? string.Empty;
        var  track     = (string?)currentTrack["track"]     ?? string.Empty;
        var  source    = (string?)currentTrack["source"]    ?? string.Empty;
        long startedAt = (long?)currentTrack["startedAt"]?.GetValue<long>() ?? 0L;
        long duration  = (long?)currentTrack["duration"]?.GetValue<long>()  ?? 0L;
        bool isPaused  = currentTrack["isPaused"]?.GetValue<bool>() == true;
        var  artRaw    = (string?)currentTrack["trackArt"]  ?? string.Empty;
        var  originUrl = (string?)currentTrack["originUrl"] ?? string.Empty;

        // safeArt: filter data: URIs -- Discord CDN rejects them (server.js line 312)
        var safeArt = IsHttpUrl(artRaw) ? artRaw : string.Empty;

        // ── Dedup (mirrors server.js _lastDiscordSig, lines 315-322) ─────────
        // Includes startedAt so seeks re-push the activity (shifted end time).
        var  sig   = string.Join("|", artist, track, source, startedAt, duration, isPaused ? 1 : 0, safeArt, originUrl);
        var  nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_lock)
        {
            var ageMs = nowMs - _lastPushAt;
            if (sig == _lastSig && ageMs < DedupMaxAgeMs) return;
            _lastSig    = sig;
            _lastPushAt = nowMs;
        }

        // ── Build RichPresence ────────────────────────────────────────────────
        var presence = BuildPresence(artist, track, source, startedAt, duration, isPaused, safeArt, originUrl);
        if (presence == null) return;

        // ── Queue through throttle (or hold as pending if disconnected) ───────
        lock (_lock)
        {
            if (!_connected)
            {
                // Hold for flush on READY (mirrors discord_rpc.js pendingState, line 277)
                _pendingPresence = presence;
                return;
            }
            _throttle?.Queue(presence);
        }
    }

    // ── Client lifecycle (all called inside _lock) ────────────────────────────

    /// <summary>
    /// Initialize (or re-initialize) the Discord client.
    /// Creates a fresh throttle (resets rate-limit state, mirrors discord_rpc.js line 103).
    /// MUST be called inside _lock.
    /// </summary>
    private void TryInitClient()
    {
        DisposeClient();
        try
        {
            _client = new DiscordRpcClient(_clientId);

            _client.OnReady            += OnReady;
            _client.OnConnectionFailed += OnConnectionFailed;
            _client.OnClose            += OnClose;
            _client.OnError            += OnError;

            // Fresh throttle for each connection: resets lastSentAt to 0, so the
            // first post-READY update fires immediately (mirrors discord_rpc.js line 103).
            _throttle = new DiscordRpcThrottle(ThrottleMs, SendToClient, _logger);

            _client.Initialize();
            _logger.LogInformation("Discord RPC: client initialized (client_id={ClientId})", _clientId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord RPC: TryInitClient failed");
            DisposeClient();
        }
    }

    /// <summary>Dispose client + throttle. MUST be called inside _lock.</summary>
    private void DisposeClient()
    {
        _connected = false;
        try { _client?.Dispose(); } catch { }
        _client = null;
        _throttle?.Dispose();
        _throttle = null;
    }

    /// <summary>Throttle callback -- actually calls SetPresence on the Discord client.</summary>
    private void SendToClient(RichPresence? presence)
    {
        DiscordRpcClient? client;
        lock (_lock)
        {
            if (!_connected) return;
            client = _client;
        }
        try
        {
            client?.SetPresence(presence);
            _logger.LogInformation(
                "Discord RPC: SetPresence sent (details='{Details}' state='{State}' largeImg='{Large}' tsStart={TsStart} tsEnd={TsEnd} largeImgIsUrl={IsUrl})",
                presence?.Details ?? "(null)",
                presence?.State   ?? "(null)",
                presence?.Assets?.LargeImageKey ?? "(null)",
                presence?.Timestamps?.Start?.ToString("o") ?? "(null)",
                presence?.Timestamps?.End?.ToString("o")   ?? "(null)",
                IsHttpUrl(presence?.Assets?.LargeImageKey));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Discord RPC: SetPresence error"); }
    }

    // ── Discord event handlers ────────────────────────────────────────────────
    // Events fire on Lachee's background thread (AutoEvents=true by default).

    private void OnReady(object sender, ReadyMessage e)
    {
        _logger.LogInformation(
            "Discord RPC: connected as {User}", e.User?.Username ?? "unknown");
        RichPresence? pending;
        lock (_lock)
        {
            _connected       = true;
            // Reset dedup so the next PushDiscord always fires a fresh SET_ACTIVITY.
            // Mirrors discord_rpc.js _onDiscordReady: _lastDiscordSig = '' (server.js line 281).
            _lastSig         = string.Empty;
            pending          = _pendingPresence;
            _pendingPresence = null;
        }
        // Flush any activity queued while we were disconnected.
        if (pending != null) _throttle?.Queue(pending);
    }

    private void OnConnectionFailed(object sender, ConnectionFailedMessage e)
    {
        _logger.LogInformation("Discord RPC: connection failed -- is Discord running?");
        lock (_lock) { _connected = false; }
    }

    private void OnClose(object sender, CloseMessage e)
    {
        _logger.LogInformation(
            "Discord RPC: disconnected (code={Code} reason={Reason})", e.Code, e.Reason);
        lock (_lock) { _connected = false; }
    }

    private void OnError(object sender, ErrorMessage e)
    {
        _logger.LogWarning(
            "Discord RPC: error code={Code} msg={Msg}", (int)e.Code, e.Message);
        // Rate-limit errors are logged but not acted on -- the 2000 ms throttle
        // should prevent hitting Discord's 5/20 s limit in normal operation.
    }

    // ── Activity building ─────────────────────────────────────────────────────

    /// <summary>
    /// Build a Lachee RichPresence from track state.
    /// Mirrors discord_rpc.js buildActivity() (lines 209-258) field-by-field.
    ///
    /// Mapping:
    ///   details          = track (clamp 128) || "Unknown track"
    ///   state            = "by {artist}" || source (clamp 128, min 2)
    ///   timestamps.start = startedAt/1000 (only when !isPaused and startedAt > 0)
    ///   timestamps.end   = (startedAt+duration)/1000 (only when !isPaused and end > start)
    ///   assets.large_image = trackArt (https) || "mastersfm_logo"
    ///   assets.large_text  = srcName (clamp 128)
    ///   assets.small_image = "mastersfm_logo" (always)
    ///   assets.small_text  = isPaused ? "Paused" : "Master's FM"
    ///   buttons[0]       = {label:"Listen on {src}"(clamp 32), url:originUrl} if https URL
    /// </summary>
    private static RichPresence? BuildPresence(
        string artist, string track, string source,
        long startedAtMs, long durationMs, bool isPaused,
        string trackArt, string originUrl)
    {
        if (string.IsNullOrEmpty(track)) return null;

        // Source name shown in large_text + button label
        var srcName = string.IsNullOrWhiteSpace(source) ? "Master's FM" : source.Trim();

        // Two visible lines. Discord enforces >= 2 chars per line.
        var details   = Clamp(track, 128);
        if (string.IsNullOrEmpty(details)) details = "Unknown track";

        var stateText = !string.IsNullOrEmpty(artist)
            ? $"by {artist}"
            : srcName;
        stateText = Clamp(stateText, 128);
        if (stateText.Length < 2) stateText = "Master's FM";

        var presence = new RichPresence
        {
            Details = details,
            State   = stateText,
        };

        // Timestamps -- only while playing; paused tracks show no timer.
        if (!isPaused && startedAtMs > 0)
        {
            var startSec = startedAtMs / 1000L;
            var ts = new Timestamps
            {
                Start = DateTimeOffset.FromUnixTimeSeconds(startSec).UtcDateTime,
            };
            if (durationMs > 0)
            {
                var endSec = (startedAtMs + durationMs) / 1000L;
                if (endSec > startSec)
                    ts.End = DateTimeOffset.FromUnixTimeSeconds(endSec).UtcDateTime;
            }
            presence.Timestamps = ts;
        }

        // Images -- data: URIs rejected; large_image uses track art URL or logo asset.
        var hasHttpsArt = IsHttpUrl(trackArt);
        presence.Assets = new Assets
        {
            LargeImageKey  = hasHttpsArt ? trackArt : "mastersfm_logo",
            LargeImageText = Clamp(srcName, 128),
            SmallImageKey  = "mastersfm_logo",
            SmallImageText = isPaused ? "⏸ Paused" : "Master's FM",
        };

        // Button -- "Listen on [Source]"; only when originUrl is an HTTPS URL.
        if (IsHttpUrl(originUrl))
        {
            var btnLabel = Clamp($"Listen on {srcName}", 32);
            presence.Buttons = new[] { new Button { Label = btnLabel, Url = originUrl } };
        }

        return presence;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string Clamp(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen];

    private static bool IsHttpUrl(string? s)
        => !string.IsNullOrEmpty(s)
           && (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("http://",  StringComparison.OrdinalIgnoreCase));
}
