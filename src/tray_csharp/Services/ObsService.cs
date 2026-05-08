// Stage 7.8: ObsService — persistent OBS-WS v5 connection monitoring service.
// STEP 4: state machine skeleton only; no WebSocket yet.
// STEP 5: ClientWebSocket + auth + reconnect backoff + heartbeat loop added here.
//
// Auth algorithm (SHA256 double-hash; matches PS tray Add-OBSBrowserSource):
//   secret  = base64(SHA256(password + salt))
//   authStr = base64(SHA256(secret + challenge))
//
// Reconnect backoff sequence: 5s / 10s / 20s / 40s / 60s (cap; stays at 60s).
// Heartbeat: GetCurrentProgramScene request every 30s when Connected.
// All telemetry via ITelemetry.IncrementCounter; see TelCounters class.

using System.Threading;

namespace MastersFM.Tray.Services;

public sealed class ObsService : IObsService, IDisposable
{
    // -------------------------------------------------------------------------
    // Config key constants (all new; none exist in PS tray)
    // -------------------------------------------------------------------------
    private const string KeyEnabled     = "obs.enabled";
    private const string KeyHost        = "obs.host";
    private const string KeyPort        = "obs.port";
    private const string KeyPassword    = "obs.password";
    private const string KeyAutoConnect = "obs.auto_connect";

    private const string DefaultHost    = "localhost";
    private const int    DefaultPort    = 4455;
    private const string DefaultPwd     = "";

    // -------------------------------------------------------------------------
    // Telemetry counter names
    // -------------------------------------------------------------------------
    private static class TelCounters
    {
        public const string ConnectAttempts   = "obs_connect_attempts";
        public const string ConnectSuccesses  = "obs_connect_successes";
        public const string AuthFailures      = "obs_auth_failures";
        public const string Disconnects       = "obs_disconnects";
        public const string ReconnectAttempts = "obs_reconnect_attempts";
    }

    private const string Component = "OBS";

    // -------------------------------------------------------------------------
    // Reconnect backoff table (seconds)
    // -------------------------------------------------------------------------
    private static readonly int[] BackoffSeconds = { 5, 10, 20, 40, 60 };

    // -------------------------------------------------------------------------
    // Dependencies
    // -------------------------------------------------------------------------
    private readonly ILogger        _logger;
    private readonly IConfigService _config;
    private readonly ITelemetry     _telemetry;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private volatile ObsConnectionState _state = ObsConnectionState.Disabled;
    private CancellationTokenSource?    _lifetimeCts;
    private bool                        _started;
    private readonly object             _lock = new();

    // -------------------------------------------------------------------------
    // IObsService implementation
    // -------------------------------------------------------------------------

    public ObsConnectionState ConnectionState => _state;

    public bool IsEnabled => _config.GetValue<bool>(KeyEnabled, false);

    public event EventHandler<ObsConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public ObsService(ILogger logger, IConfigService config, ITelemetry telemetry)
    {
        _logger    = logger;
        _config    = config;
        _telemetry = telemetry;
        _logger.Log("ObsService created; state=Disabled (Start not yet called)", Component);
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
            _lifetimeCts = new CancellationTokenSource();
        }

        var enabled     = _config.GetValue<bool>(KeyEnabled, false);
        var autoConnect = _config.GetValue<bool>(KeyAutoConnect, false);
        var host        = _config.GetValue<string>(KeyHost, DefaultHost) ?? DefaultHost;
        var port        = _config.GetValue<int>(KeyPort, DefaultPort);

        _logger.Log(
            $"Start: enabled={enabled} auto_connect={autoConnect} host={host} port={port}",
            Component);

        if (!enabled)
        {
            SetState(ObsConnectionState.Disabled);
            _logger.Log("obs.enabled=false; staying Disabled", Component);
            return;
        }

        SetState(ObsConnectionState.Disconnected);

        if (autoConnect)
        {
            _logger.Log("obs.auto_connect=true; scheduling initial connect", Component);
            // TODO STEP 5: kick off ConnectLoop here
            // _ = Task.Run(() => ConnectLoopAsync(_lifetimeCts!.Token));
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_lock)
        {
            if (!_started) return;
            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        }

        // TODO STEP 5: await / close WebSocket here
        _logger.Log("Stop: lifetime CTS cancelled; WebSocket will close in STEP 5", Component);
        SetState(ObsConnectionState.Disabled);
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            _logger.LogWarn("ConnectAsync called but obs.enabled=false; no-op", Component);
            return Task.CompletedTask;
        }

        _telemetry.IncrementCounter(TelCounters.ConnectAttempts);
        _logger.Log("ConnectAsync: initiating (STEP 5 will add ClientWebSocket)", Component);

        // TODO STEP 5: replace with real async connect + auth + receive loop
        SetState(ObsConnectionState.Connecting);

        // Temporary stub: immediately revert to Disconnected (no socket yet)
        SetState(ObsConnectionState.Disconnected);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _logger.Log("DisconnectAsync: requested (STEP 5 will close socket)", Component);
        _telemetry.IncrementCounter(TelCounters.Disconnects);

        // TODO STEP 5: close ClientWebSocket cleanly
        var wasEnabled = IsEnabled;
        if (wasEnabled)
        {
            _config.SetValue<bool>(KeyEnabled, false);
            _logger.Log("obs.enabled written false", Component);
        }

        SetState(ObsConnectionState.Disabled);
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // State machine helpers
    // -------------------------------------------------------------------------

    private void SetState(ObsConnectionState newState)
    {
        var oldState = _state;
        if (oldState == newState) return;

        _state = newState;
        _logger.Log($"state {oldState} → {newState}", Component);
        ConnectionStateChanged?.Invoke(this, new ObsConnectionStateChangedEventArgs(oldState, newState));
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
    }
}
