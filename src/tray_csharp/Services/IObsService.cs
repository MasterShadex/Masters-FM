// Stage 7.8: IObsService. Persistent OBS-WS v5 connection monitoring service.
// Architecturally distinct from the PS tray's one-shot browser-source-add
// utility (tray.ps1:3805 Add-OBSBrowserSourceDirect): this service maintains
// a live WebSocket connection to OBS for connection-state reporting and
// heartbeat pings. Browser source add/remove is ID-28 (deferred post-V14).
//
// Config keys (all new; none in PS tray):
//   obs.enabled       bool   default false  -- global enable/disable
//   obs.host          string default "localhost"
//   obs.port          int    default 4455
//   obs.password      string default ""     -- empty = no auth
//   obs.auto_connect  bool   default false  -- connect on startup when enabled

namespace MastersFM.Tray.Services;

/// <summary>
/// Connection state machine for the OBS-WS v5 persistent connection.
/// Transitions: Disabled ↔ Disconnected → Connecting → Authenticating → Connected.
/// Error is a transient fault state; service retries with exponential backoff.
/// </summary>
public enum ObsConnectionState
{
    /// <summary>OBS integration disabled in config (obs.enabled = false).</summary>
    Disabled,

    /// <summary>Enabled but not connected (initial state, or after clean disconnect / exhausted retries).</summary>
    Disconnected,

    /// <summary>TCP + WS handshake in progress (awaiting op=0 Hello).</summary>
    Connecting,

    /// <summary>Hello received; op=1 Identify sent; awaiting op=2 Identified.</summary>
    Authenticating,

    /// <summary>op=2 Identified received; heartbeat loop active.</summary>
    Connected,

    /// <summary>Transient fault (connect refused, auth failed, socket error). Retry backoff in progress.</summary>
    Error,
}

public interface IObsService
{
    /// <summary>Current connection state. Updated on the thread-pool; observe via ConnectionStateChanged.</summary>
    ObsConnectionState ConnectionState { get; }

    /// <summary>True when obs.enabled = true in config. Does not imply Connected.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Fires on every ConnectionState transition. EventArgs.NewState is the state
    /// entered. TrayMenuViewModel (STEP 6) subscribes to drive ObsLabel / ObsTooltip.
    /// Raised on a thread-pool thread — marshal to Dispatcher for UI updates.
    /// </summary>
    event EventHandler<ObsConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Enables OBS integration and initiates a connection attempt (if obs.auto_connect
    /// or caller requests). Writes obs.enabled = true to config.
    /// Safe to call from any thread; returns promptly (connection is async).
    /// </summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Disables OBS integration, closes the WebSocket, and transitions to Disabled.
    /// Writes obs.enabled = false to config. Safe to call from any thread.
    /// </summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Starts the service (reads config, begins auto-connect loop if obs.auto_connect).
    /// Called once from App.OnStartup after DI build. Idempotent.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the service cleanly (closes WebSocket, cancels retry timers).
    /// Called from App.OnExit. Idempotent.
    /// </summary>
    void Stop();
}

/// <summary>Event args for IObsService.ConnectionStateChanged.</summary>
public sealed class ObsConnectionStateChangedEventArgs : EventArgs
{
    public ObsConnectionState OldState { get; }
    public ObsConnectionState NewState { get; }

    public ObsConnectionStateChangedEventArgs(ObsConnectionState oldState, ObsConnectionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}
