// INTERRUPT #3 STEP 5 (Issues 1+8): IHeartbeatService contract.
// HeartbeatService sends periodic position+pause webhooks on a 2s cadence,
// bypassing the TrackResolver dedup gate so position and pause-state changes
// are always forwarded to server.exe even when the track identity is unchanged.

namespace MastersFM.Tray.Services;

public interface IHeartbeatService
{
    void Start();
    void Stop();
}
