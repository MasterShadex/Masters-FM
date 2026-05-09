// Stage 7.8 STEP 5: ObsService — OBS-WS v5 persistent connection monitoring.
// BCL only: ClientWebSocket + System.Text.Json (no new NuGets).
//
// Auth (SHA256 double-hash; exact PS tray Add-OBSBrowserSource algorithm):
//   secret  = base64(SHA256(UTF8(password + salt)))
//   authStr = base64(SHA256(UTF8(secret + challenge)))
//
// Reconnect backoff: 5s / 10s / 20s / 40s / 60s (stays at 60s cap).
// Heartbeat: GetCurrentProgramScene every 30s when Connected.
// Log prefix: [OBS].

using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MastersFM.Tray.Services;

public sealed class ObsService : IObsService, IDisposable
{
    // ── config keys (all new; none in PS tray) ────────────────────────────────
    private const string KeyEnabled     = "obs.enabled";
    private const string KeyHost        = "obs.host";
    private const string KeyPort        = "obs.port";
    private const string KeyPassword    = "obs.password";
    private const string KeyAutoConnect = "obs.auto_connect";

    private const string DefaultHost = "localhost";
    private const int    DefaultPort = 4455;
    private const string DefaultPwd  = "";

    // ── telemetry counter names ───────────────────────────────────────────────
    private static class Tel
    {
        public const string ConnectAttempts   = "obs_connect_attempts";
        public const string ConnectSuccesses  = "obs_connect_successes";
        public const string AuthFailures      = "obs_auth_failures";
        public const string Disconnects       = "obs_disconnects";
        public const string ReconnectAttempts = "obs_reconnect_attempts";
    }

    private const string Cmp = "OBS";

    // ── reconnect backoff (seconds) ───────────────────────────────────────────
    private static readonly int[] Backoff = { 5, 10, 20, 40, 60 };

    // ── dependencies ─────────────────────────────────────────────────────────
    private readonly ILogger        _log;
    private readonly IConfigService _cfg;
    private readonly ITelemetry     _tel;

    // ── state ─────────────────────────────────────────────────────────────────
    private volatile ObsConnectionState _state = ObsConnectionState.Disabled;
    private CancellationTokenSource?    _lifetimeCts;
    private CancellationTokenSource?    _connectCts;
    private readonly SemaphoreSlim      _sendLock = new(1, 1);
    private bool                        _started;
    private readonly object             _lock = new();

    // ── IObsService ───────────────────────────────────────────────────────────

    public ObsConnectionState ConnectionState => _state;
    public bool IsEnabled => _cfg.GetValue<bool>(KeyEnabled, false);

    public event EventHandler<ObsConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public ObsService(ILogger log, IConfigService cfg, ITelemetry tel)
    {
        _log = log;
        _cfg = cfg;
        _tel = tel;
        _log.Log("ObsService created; state=Disabled", Cmp);
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
            _lifetimeCts = new CancellationTokenSource();
        }

        var enabled     = _cfg.GetValue<bool>(KeyEnabled, false);
        var autoConnect = _cfg.GetValue<bool>(KeyAutoConnect, false);
        var host        = _cfg.GetValue<string>(KeyHost, DefaultHost) ?? DefaultHost;
        var port        = _cfg.GetValue<int>(KeyPort, DefaultPort);

        _log.Log($"Start: enabled={enabled} auto_connect={autoConnect} host={host} port={port}", Cmp);

        if (!enabled)
        {
            SetState(ObsConnectionState.Disabled);
            _log.Log("obs.enabled=false; staying Disabled", Cmp);
            return;
        }

        SetState(ObsConnectionState.Disconnected);
        if (autoConnect) StartConnectLoop();
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (!_started) return;
            cts = _lifetimeCts;
            _lifetimeCts = null;
            _connectCts?.Cancel();
            _connectCts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
        _log.Log("Stop: lifetime cancelled; connect loop will exit", Cmp);
        SetState(ObsConnectionState.Disabled);
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            _log.LogWarn("ConnectAsync: obs.enabled=false; writing true", Cmp);
            _cfg.SetValue<bool>(KeyEnabled, true);
        }

        if (_state == ObsConnectionState.Connecting ||
            _state == ObsConnectionState.Authenticating ||
            _state == ObsConnectionState.Connected)
        {
            _log.Log($"ConnectAsync: already in state={_state}; no-op", Cmp);
            return Task.CompletedTask;
        }

        SetState(ObsConnectionState.Disconnected);
        StartConnectLoop();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _log.Log("DisconnectAsync: requested", Cmp);
        _tel.IncrementCounter(Tel.Disconnects);

        _cfg.SetValue<bool>(KeyEnabled, false);
        lock (_lock)
        {
            _connectCts?.Cancel();
            _connectCts = null;
        }

        SetState(ObsConnectionState.Disabled);
        return Task.CompletedTask;
    }

    // ── private: connect loop entry ───────────────────────────────────────────

    private void StartConnectLoop()
    {
        CancellationToken lifetime;
        lock (_lock)
        {
            if (_lifetimeCts == null) return;
            _connectCts?.Cancel();
            _connectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            lifetime = _connectCts.Token;
        }

        _ = Task.Run(() => ConnectLoopAsync(lifetime), lifetime);
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        int backoffIdx = 0;

        while (!ct.IsCancellationRequested && _state != ObsConnectionState.Disabled)
        {
            var host = _cfg.GetValue<string>(KeyHost, DefaultHost) ?? DefaultHost;
            var port = _cfg.GetValue<int>(KeyPort, DefaultPort);
            var pwd  = _cfg.GetValue<string>(KeyPassword, DefaultPwd) ?? DefaultPwd;

            bool success = false;
            try
            {
                success = await ConnectOnceAsync(host, port, pwd, ct);
                if (success) backoffIdx = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogErr("connect iteration", ex, Cmp); }

            if (ct.IsCancellationRequested || _state == ObsConnectionState.Disabled) break;

            var delaySec = Backoff[Math.Min(backoffIdx, Backoff.Length - 1)];
            backoffIdx++;
            _tel.IncrementCounter(Tel.ReconnectAttempts);
            _log.Log($"reconnect in {delaySec}s (attempt #{backoffIdx})", Cmp);
            SetState(ObsConnectionState.Error);

            try { await Task.Delay(TimeSpan.FromSeconds(delaySec), ct); }
            catch (OperationCanceledException) { break; }

            if (!ct.IsCancellationRequested && _state != ObsConnectionState.Disabled)
                SetState(ObsConnectionState.Disconnected);
        }

        _log.Log("connect loop exited", Cmp);
    }

    // ── private: single connect attempt ──────────────────────────────────────

    private async Task<bool> ConnectOnceAsync(string host, int port, string pwd, CancellationToken ct)
    {
        var uri = new Uri($"ws://{host}:{port}");
        _log.Log($"connecting to {uri}", Cmp);
        _tel.IncrementCounter(Tel.ConnectAttempts);
        SetState(ObsConnectionState.Connecting);

        using var ws = new ClientWebSocket();
        try { await ws.ConnectAsync(uri, ct); }
        catch (Exception ex)
        {
            _log.LogErr($"connect to {uri} failed", ex, Cmp);
            return false;
        }

        // op=0 Hello
        var (op0, d0) = await ReadOpAsync(ws, ct);
        if (op0 != 0) { _log.LogErr($"expected op=0, got op={op0}", null, Cmp); return false; }

        // Build op=1 Identify
        string identify;
        var hasSalt = d0.TryGetProperty("authentication", out var authEl) &&
                      authEl.TryGetProperty("salt", out _);
        if (hasSalt)
        {
            var salt = authEl.GetProperty("salt").GetString() ?? "";
            var chal = authEl.GetProperty("challenge").GetString() ?? "";
            var authStr = ComputeAuth(pwd, salt, chal);
            identify = $"{{\"op\":1,\"d\":{{\"rpcVersion\":1,\"authentication\":\"{authStr}\"}}}}";
        }
        else
        {
            identify = "{\"op\":1,\"d\":{\"rpcVersion\":1}}";
        }

        SetState(ObsConnectionState.Authenticating);
        try { await SendJsonAsync(ws, identify, ct); }
        catch (Exception ex) { _log.LogErr("send Identify", ex, Cmp); return false; }

        // op=2 Identified (skip op=5 events)
        for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
        {
            var (op, _) = await ReadOpAsync(ws, ct);
            if (op == 5) continue;  // Event frame; skip
            if (op == 2)
            {
                _tel.IncrementCounter(Tel.ConnectSuccesses);
                SetState(ObsConnectionState.Connected);
                _log.Log($"connected to OBS at {uri}", Cmp);
                await RunConnectedLoopAsync(ws, ct);
                return true;
            }
            _log.LogErr($"auth failed (op={op} while waiting for op=2; check password)", null, Cmp);
            _tel.IncrementCounter(Tel.AuthFailures);
            return false;
        }

        _log.LogErr("op=2 Identified not received", null, Cmp);
        _tel.IncrementCounter(Tel.AuthFailures);
        return false;
    }

    // ── private: connected receive + heartbeat ────────────────────────────────

    private async Task RunConnectedLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var heartbeatTask = RunHeartbeatAsync(ws, heartbeatTimer, ct);

        var buf = new byte[65536];
        var ms  = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                // Incoming op=5 Events / op=7 Responses — drained, not acted on
            }
        }
        finally
        {
            heartbeatTimer.Dispose();
            try { await heartbeatTask; } catch { }
            if (_state == ObsConnectionState.Connected)
            {
                _tel.IncrementCounter(Tel.Disconnects);
                SetState(ObsConnectionState.Disconnected);
                _log.Log("connection closed by remote / socket error", Cmp);
            }
        }
    }

    private async Task RunHeartbeatAsync(ClientWebSocket ws, PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (ws.State != WebSocketState.Open) break;
                var reqId = Guid.NewGuid().ToString("N")[..8];
                var req = $"{{\"op\":6,\"d\":{{\"requestType\":\"GetCurrentProgramScene\",\"requestId\":\"{reqId}\"}}}}";
                await _sendLock.WaitAsync(ct);
                try { await SendJsonAsync(ws, req, ct); }
                catch (Exception ex) { _log.LogErr("heartbeat send", ex, Cmp); break; }
                finally { _sendLock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex) { _log.LogErr("heartbeat loop", ex, Cmp); }
    }

    // ── private: protocol helpers ─────────────────────────────────────────────

    private static string ComputeAuth(string password, string salt, string challenge)
    {
        using var sha = SHA256.Create();
        var secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<(int op, JsonElement d)> ReadOpAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[65536];
        var ms  = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            if (r.MessageType == WebSocketMessageType.Close) return (-1, default);
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);

        using var doc = JsonDocument.Parse(ms.ToArray());
        var root = doc.RootElement;
        var op = root.TryGetProperty("op", out var opEl) ? opEl.GetInt32() : -1;
        root.TryGetProperty("d", out var d);
        return (op, d.Clone()); // Clone so it survives doc disposal
    }

    // ── state machine ─────────────────────────────────────────────────────────

    private void SetState(ObsConnectionState next)
    {
        var prev = _state;
        if (prev == next) return;
        _state = next;
        _log.Log($"state {prev} → {next}", Cmp);
        ConnectionStateChanged?.Invoke(this, new ObsConnectionStateChangedEventArgs(prev, next));
    }

    // ── browser-source operations (stubs; replaced in STEP 3 / STEP 4) ──────────

    public Task<ObsBrowserSourceResult> AddBrowserSourceAsync(CancellationToken ct = default)
        => Task.FromResult(ObsBrowserSourceResult.Fail("Not yet implemented (STEP 2 stub)"));

    public Task<bool> RemoveBrowserSourceAsync(CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> BrowserSourceExistsAsync(CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<ObsVersionInfo?> GetObsVersionAsync(CancellationToken ct = default)
        => Task.FromResult<ObsVersionInfo?>(null);

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _sendLock.Dispose();
    }
}
