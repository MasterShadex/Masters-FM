// Stage 7.5: IPlatformDetector. Gap-filler arm (Arm 2) interface. The
// 3 gap-fillers (osu / VLC HTTP / WMP-legacy) cover platforms that don't
// publish to SMTC. SMTC-publishing platforms (Spotify, SoundCloud,
// browser tabs, Win11 Media Player) are handled by Arm 1 (SmtcEventBridge)
// via the v12.0.0 SMTCWatcher's event queue.
//
// DetectorOrchestrator runs registered IPlatformDetector instances in
// priority order at 1-second cadence. First non-null wins; resolver
// receives the TrackUpdate.

namespace MastersFM.Tray.Detectors;

public interface IPlatformDetector
{
    /// <summary>Source string emitted in TrackUpdate.Source (e.g., "osu", "vlc", "wmp-legacy").</summary>
    string Name { get; }

    /// <summary>True when the per-platform config flag is set AND the underlying detection target is reachable. False short-circuits PollAsync to avoid pointless work.</summary>
    bool IsEnabled { get; }

    /// <summary>Returns null when no track is currently detected on this platform OR the detector is disabled.</summary>
    Task<TrackUpdate?> PollAsync(CancellationToken ct);
}
