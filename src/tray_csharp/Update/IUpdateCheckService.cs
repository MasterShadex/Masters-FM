// Stage 7.2: IUpdateCheckService interface. The contract is a HARD
// commitment to Stage 7.6 (which subscribes to StateChanged for tray
// menu update-row label updates). Brief absolute rule 15: the contract
// MUST be stable so 7.6's subscription wiring is straightforward.

namespace MastersFM.Tray.Update;

public interface IUpdateCheckService
{
    /// <summary>Current state of the update-check machine.</summary>
    UpdateState CurrentState { get; }

    /// <summary>The available remote version, or null if not in Available/Ready/Installing states.</summary>
    string? AvailableVersion { get; }

    /// <summary>Last-checked timestamp in UTC, or null if never checked. Persisted via IConfigService key `update.lastChecked`.</summary>
    DateTime? LastCheckedUtc { get; }

    /// <summary>Most recent error message if state is Error; otherwise null.</summary>
    string? LastErrorMessage { get; }

    /// <summary>
    /// Triggers a manifest fetch. Transitions Idle -> Checking, then to
    /// Available (if newer stable release), Idle (if up-to-date or remote
    /// is pre-release), or Error briefly. Returns final state.
    /// </summary>
    Task<UpdateState> CheckNowAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the MSI from the manifest's msi_url. Transitions
    /// Available -> Downloading -> Ready (after SHA256 + Authenticode
    /// verification) or back to Available on download failure / signature
    /// failure.
    /// </summary>
    Task DownloadAsync(CancellationToken ct = default);

    /// <summary>Cancels any in-flight operation; transitions to Idle.</summary>
    void Cancel();

    /// <summary>
    /// Installs the verified MSI by spawning msiexec via ProcessStartInfo
    /// with ArgumentList (closes B-003 quoting bug). Transitions
    /// Ready -> Installing, then calls Application.Current.Shutdown(0).
    /// </summary>
    Task InstallAsync(CancellationToken ct = default);

    /// <summary>
    /// Fires on every state transition. Stage 7.6 will subscribe to
    /// drive the tray menu's dynamic update-row label. Args carry old +
    /// new state, optional progress percent (during Downloading), optional
    /// detail string (e.g. error message during Error transition).
    /// </summary>
    event EventHandler<UpdateStateChangedEventArgs>? StateChanged;
}

public sealed class UpdateStateChangedEventArgs : EventArgs
{
    public UpdateState OldState { get; }
    public UpdateState NewState { get; }
    public int? ProgressPercent { get; }
    public string? Detail { get; }

    public UpdateStateChangedEventArgs(UpdateState oldState, UpdateState newState, int? progressPercent = null, string? detail = null)
    {
        OldState = oldState;
        NewState = newState;
        ProgressPercent = progressPercent;
        Detail = detail;
    }
}
