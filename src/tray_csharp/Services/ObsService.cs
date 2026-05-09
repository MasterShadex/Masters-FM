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

using System.Collections.Concurrent;
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

    // live socket (non-null only while Connected; cleared in RunConnectedLoopAsync finally)
    private volatile ClientWebSocket? _socket;

    // pending SendRequestAsync calls keyed by requestId
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>>
        _pendingRequests = new();

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
                _socket = ws;  // expose to SendRequestAsync while Connected
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
                DispatchReceived(ms.ToArray());  // op=7 -> TCS; op=5 drained silently
            }
        }
        finally
        {
            heartbeatTimer.Dispose();
            try { await heartbeatTask; } catch { }
            // Release all pending SendRequestAsync callers
            foreach (var kvp in _pendingRequests)
                kvp.Value.TrySetResult(null);
            _pendingRequests.Clear();
            _socket = null;
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

    // Dispatch an op=7 response frame to the waiting SendRequestAsync TCS.
    // Stores the full root element so callers can navigate d.responseData.
    private void DispatchReceived(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opEl) || opEl.GetInt32() != 7) return;
            if (!root.TryGetProperty("d", out var d)) return;
            if (!d.TryGetProperty("requestId", out var ridEl)) return;
            var rid = ridEl.GetString();
            if (rid != null && _pendingRequests.TryRemove(rid, out var tcs))
                tcs.TrySetResult(root.Clone());  // root so callers do resp.Value.GetProperty("d")
        }
        catch { /* malformed frame; ignore */ }
    }

    // Send an OBS-WS v5 op=6 request and await the matching op=7 response (5s timeout).
    // Returns the full root JsonElement so callers can navigate d.responseData.
    // Returns null if not connected or if the request times out.
    private async Task<JsonElement?> SendRequestAsync(
        string requestType, object? requestData, CancellationToken ct)
    {
        var sock = _socket;
        if (sock == null || _state != ObsConnectionState.Connected)
        {
            _log.Log($"SendRequest: not connected (requestType={requestType})", Cmp);
            return null;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        try
        {
            var msg = JsonSerializer.SerializeToUtf8Bytes(new
            {
                op = 6,
                d = new { requestType, requestId, requestData }
            });

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await sock.SendAsync(msg, WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarn($"SendRequest timeout: {requestType} id={requestId[..8]}", Cmp);
                return null;
            }
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

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

    // ── browser-source operations (Stage 7.8B STEP 3 — WebSocket primary path) ─

    public async Task<ObsVersionInfo?> GetObsVersionAsync(CancellationToken ct = default)
    {
        var resp = await SendRequestAsync("GetVersion", null, ct).ConfigureAwait(false);
        if (resp == null) return null;
        try
        {
            var responseData = resp.Value.GetProperty("d").GetProperty("responseData");
            var ver = responseData.GetProperty("obsVersion").GetString() ?? "0.0.0";
            var p = ver.Split('.');
            return new ObsVersionInfo(
                p.Length > 0 && int.TryParse(p[0], out var maj) ? maj : 0,
                p.Length > 1 && int.TryParse(p[1], out var min) ? min : 0,
                p.Length > 2 && int.TryParse(p[2], out var pat) ? pat : 0);
        }
        catch (Exception ex)
        {
            _log.LogErr("GetObsVersionAsync parse", ex, Cmp);
            return null;
        }
    }

    public async Task<bool> BrowserSourceExistsAsync(CancellationToken ct = default)
    {
        var resp = await SendRequestAsync("GetInputList",
            new { inputKind = "browser_source" }, ct).ConfigureAwait(false);
        if (resp == null) return false;
        try
        {
            var inputs = resp.Value.GetProperty("d").GetProperty("responseData")
                .GetProperty("inputs").EnumerateArray();
            foreach (var input in inputs)
            {
                if (input.TryGetProperty("inputName", out var n) &&
                    n.GetString() == "Master's FM")
                    return true;
            }
            return false;
        }
        catch (Exception ex) { _log.LogErr("BrowserSourceExistsAsync parse", ex, Cmp); return false; }
    }

    public async Task<ObsBrowserSourceResult> AddBrowserSourceAsync(CancellationToken ct = default)
    {
        if (_state != ObsConnectionState.Connected)
            return ObsBrowserSourceResult.Fail("Not connected to OBS");

        // Version gate: OBS >= 28 required for WebSocket browser-source ops
        var version = await GetObsVersionAsync(ct).ConfigureAwait(false);
        if (version == null || !version.SupportsWebSocketBrowserOps)
        {
            _log.Log("AddBrowserSource: OBS < 28 or version unknown; WebSocket path unavailable", Cmp);
            return ObsBrowserSourceResult.Fail("OBS < 28 -- WebSocket path requires file-edit fallback");
        }

        // Idempotent check
        if (await BrowserSourceExistsAsync(ct).ConfigureAwait(false))
        {
            _log.Log("AddBrowserSource: source already present (idempotent no-op)", Cmp);
            return ObsBrowserSourceResult.Ok("WebSocket (already present)");
        }

        // Get scene list; inject into first scene found
        var sceneListResp = await SendRequestAsync("GetSceneList", null, ct).ConfigureAwait(false);
        if (sceneListResp == null)
            return ObsBrowserSourceResult.Fail("GetSceneList request failed or timed out");

        string? firstScene = null;
        try
        {
            foreach (var s in sceneListResp.Value.GetProperty("d")
                         .GetProperty("responseData").GetProperty("scenes").EnumerateArray())
            {
                firstScene = s.GetProperty("sceneName").GetString();
                break;
            }
        }
        catch (Exception ex) { _log.LogErr("AddBrowserSource: parse SceneList", ex, Cmp); }

        if (firstScene == null)
            return ObsBrowserSourceResult.Fail("No scenes found in OBS");

        // CreateInput (browser_source) on first scene
        var createResp = await SendRequestAsync("CreateInput", new
        {
            sceneName    = firstScene,
            inputName    = "Master's FM",
            inputKind    = "browser_source",
            inputSettings = new
            {
                url    = "http://localhost:4242/?renderer=webgl",
                width  = 1000,
                height = 200,
                fps    = 60,
                css    = "body { background-color: rgba(0,0,0,0) !important; margin: 0; overflow: hidden; }"
            },
            sceneItemEnabled = true
        }, ct).ConfigureAwait(false);

        if (createResp == null)
            return ObsBrowserSourceResult.Fail("CreateInput request failed or timed out");

        _log.Log($"AddBrowserSource: created 'Master's FM' on scene '{firstScene}'", Cmp);
        return ObsBrowserSourceResult.Ok("WebSocket");
    }

    public async Task<bool> RemoveBrowserSourceAsync(CancellationToken ct = default)
    {
        if (_state != ObsConnectionState.Connected) return false;

        // Idempotent: already absent is success
        if (!await BrowserSourceExistsAsync(ct).ConfigureAwait(false)) return true;

        var resp = await SendRequestAsync("RemoveInput",
            new { inputName = "Master's FM" }, ct).ConfigureAwait(false);

        if (resp != null)
            _log.Log("RemoveBrowserSource: 'Master's FM' removed via WebSocket", Cmp);
        else
            _log.LogWarn("RemoveBrowserSource: RemoveInput request failed or timed out", Cmp);

        return resp != null;
    }

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
