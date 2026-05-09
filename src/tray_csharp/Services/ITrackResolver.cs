// Stage 7.5: ITrackResolver (Arm 3 contract). Single point that consumes
// TrackUpdate from BOTH arms (SmtcEventBridge + DetectorOrchestrator).
// Owns: track-identity dedup, art LRU coordination, webhook emission to
// server.exe. Closes B-013 (stale art via cache wrapper) and B-017
// (server-side art amplifier) by collapsing 3 art-resolution paths into
// one.

using MastersFM.Tray.Detectors;

namespace MastersFM.Tray.Services;

public interface ITrackResolver
{
    /// <summary>Most recently emitted track. Null at startup.</summary>
    TrackUpdate? CurrentTrack { get; }

    /// <summary>
    /// Called by SmtcEventBridge, DetectorOrchestrator, or HeartbeatService.
    /// forcePositionRefresh=false (default): dedup gate active; fires TrackChanged + webhook only for new tracks.
    /// forcePositionRefresh=true (heartbeat path): bypasses dedup; always sends webhook for
    /// position/pause/seek; does NOT fire TrackChanged (avoids spurious UI updates).
    /// Stage 7.8B: heartbeat now routes through here instead of WebhookClient directly.
    /// </summary>
    void OnTrackChanged(TrackUpdate update, bool forcePositionRefresh = false);

    /// <summary>Fires when CurrentTrack identity differs from prior. NowPlayingViewModel + tray menu (7.6) subscribe.</summary>
    event EventHandler<TrackUpdate>? TrackChanged;
}
