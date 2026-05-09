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

    // ── Browser source operations (Stage 7.8B; was deferred as ID-28) ────────

    /// <summary>
    /// Adds a Master's FM browser source to all OBS scenes. WebSocket primary;
    /// file-edit fallback when not connected or OBS is older than v28.
    /// Idempotent: no-op if the source already exists.
    /// </summary>
    Task<ObsBrowserSourceResult> AddBrowserSourceAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes the Master's FM browser source from OBS. WebSocket primary;
    /// file-edit fallback. Idempotent: returns true if already absent.
    /// </summary>
    Task<bool> RemoveBrowserSourceAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if a browser_source named "Master's FM" exists in OBS.
    /// Requires Connected state; returns false if disconnected.
    /// </summary>
    Task<bool> BrowserSourceExistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns OBS version info via GetVersion request, or null if not connected.
    /// Used to version-gate WebSocket browser-source ops (requires OBS >= 28).
    /// </summary>
    Task<ObsVersionInfo?> GetObsVersionAsync(CancellationToken ct = default);
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

// ── Supporting types for browser-source operations (Stage 7.8B) ──────────────

/// <summary>Result of AddBrowserSourceAsync. Carries the path used (WebSocket/file-edit).</summary>
public sealed record ObsBrowserSourceResult(bool Success, string? Method, string? ErrorMessage)
{
    public static ObsBrowserSourceResult Ok(string method)   => new(true, method, null);
    public static ObsBrowserSourceResult Fail(string error)  => new(false, null, error);
}

/// <summary>OBS version from GetVersion response. SupportsWebSocketBrowserOps requires v28+.</summary>
public sealed record ObsVersionInfo(int Major, int Minor, int Patch)
{
    public bool SupportsWebSocketBrowserOps => Major >= 28;
}
