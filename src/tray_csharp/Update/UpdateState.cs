// Stage 7.2: UpdateState enum. 6-state machine port from tray.ps1:5191
// + 7th state Error for transient errors that hold visible state before
// reverting to Idle on the next tick.

namespace MastersFM.Tray.Update;

public enum UpdateState
{
    /// <summary>No check in progress; ready to start a check on demand or cadence.</summary>
    Idle,

    /// <summary>Manifest fetch in flight (HTTP GetStringAsync against version.json URL).</summary>
    Checking,

    /// <summary>Remote version is newer + stable; user can click Download.</summary>
    Available,

    /// <summary>MSI streaming to disk; progress events fire during this state.</summary>
    Downloading,

    /// <summary>MSI downloaded + SHA256 verified + Authenticode-verified; user can click Install.</summary>
    Ready,

    /// <summary>msiexec spawned; tray exiting so MSI can replace files.</summary>
    Installing,

    /// <summary>Transient error (network failure, parse failure, signature failure); next tick reverts to Idle.</summary>
    Error
}
