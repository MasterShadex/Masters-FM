// Stage 7.5: WmpLegacyDetector. Gap-filler for legacy Windows Media Player
// + Win10 WMP (NOT the Win11 Media Player which goes through SMTC).
// Title-bar parsing (the most-portable path; PS S15 has 4 paths but the
// title-bar fallback is the most reliable across WMP versions).

using System.Diagnostics;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.Detectors;

public sealed class WmpLegacyDetector : IPlatformDetector
{
    private const string Component = "Detect-wmp";

    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;
    private readonly PlatformDetectorOptions _options;

    public string Name => "wmp-legacy";
    public bool IsEnabled => _options.WmpLegacyEnabled;

    public WmpLegacyDetector(ILogger logger, ITelemetry telemetry, PlatformDetectorOptions options)
    {
        _logger = logger;
        _telemetry = telemetry;
        _options = options;
    }

    public Task<TrackUpdate?> PollAsync(CancellationToken ct)
    {
        if (!IsEnabled) return Task.FromResult<TrackUpdate?>(null);
        try
        {
            var procs = Process.GetProcessesByName("wmplayer");
            foreach (var p in procs)
            {
                try
                {
                    var title = p.MainWindowTitle;
                    if (string.IsNullOrEmpty(title)) continue;

                    // WMP window title when playing:
                    //   "Track - Artist" or "Artist - Album - Track" or just "Windows Media Player"
                    if (title.Equals("Windows Media Player", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sep = title.IndexOf(" - ", StringComparison.Ordinal);
                    if (sep < 0) continue;

                    var first = title.Substring(0, sep).Trim();
                    var rest = title.Substring(sep + 3).Trim();
                    // Heuristic: WMP shows "Track - Artist" most commonly
                    var track = first;
                    var artist = rest;

                    if (string.IsNullOrEmpty(track) || string.IsNullOrEmpty(artist)) continue;

                    return Task.FromResult<TrackUpdate?>(new TrackUpdate
                    {
                        Source = Name,
                        Artist = artist,
                        Track = track,
                        IsPlaying = true,
                        PlatformIdentity = $"wmp:{p.Id}"
                    });
                }
                finally
                {
                    p.Dispose();
                }
            }
            return Task.FromResult<TrackUpdate?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogErr("wmp poll", ex, Component);
            _telemetry.IncrementCounter("wmp_poll_errors");
            return Task.FromResult<TrackUpdate?>(null);
        }
    }
}
