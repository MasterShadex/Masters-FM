// Stage 7.5: OsuDetector. Gap-filler for osu! (rhythm game). Reads the
// foreground osu! window title; parses "Artist - Track" pattern. Skips
// menu / lobby / spectator titles. Matches PS S15 osu detection logic.

using System.Diagnostics;
using System.Runtime.InteropServices;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.Detectors;

public sealed class OsuDetector : IPlatformDetector
{
    private const string Component = "Detect-osu";

    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;
    private readonly PlatformDetectorOptions _options;

    public string Name => "osu";
    public bool IsEnabled => _options.OsuEnabled;

    public OsuDetector(ILogger logger, ITelemetry telemetry, PlatformDetectorOptions options)
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
            var procs = Process.GetProcessesByName("osu!");
            if (procs.Length == 0)
            {
                procs = Process.GetProcessesByName("osu!.exe");
            }
            foreach (var p in procs)
            {
                try
                {
                    var title = p.MainWindowTitle;
                    if (string.IsNullOrEmpty(title)) continue;

                    // osu! window title format when playing:
                    //   "osu!  -  Artist - Track [Difficulty]"
                    var idx = title.IndexOf(" - ", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    if (!title.StartsWith("osu!", StringComparison.OrdinalIgnoreCase)) continue;

                    var afterPrefix = title.Substring(idx + 3).Trim();
                    if (string.IsNullOrEmpty(afterPrefix)) continue;
                    if (afterPrefix.Equals("Main Menu", StringComparison.OrdinalIgnoreCase)
                        || afterPrefix.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase)
                        || afterPrefix.StartsWith("Spectating", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Strip difficulty bracket: "Artist - Track [Diff]" -> "Artist - Track"
                    var bracket = afterPrefix.LastIndexOf('[');
                    if (bracket > 0) afterPrefix = afterPrefix.Substring(0, bracket).Trim();

                    var sep = afterPrefix.IndexOf(" - ", StringComparison.Ordinal);
                    if (sep < 0) continue;

                    var artist = afterPrefix.Substring(0, sep).Trim();
                    var track = afterPrefix.Substring(sep + 3).Trim();
                    if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(track)) continue;

                    return Task.FromResult<TrackUpdate?>(new TrackUpdate
                    {
                        Source = Name,
                        Artist = artist,
                        Track = track,
                        IsPlaying = true,
                        PlatformIdentity = $"osu!:{p.Id}"
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
            _logger.LogErr("osu poll", ex, Component);
            _telemetry.IncrementCounter("osu_poll_errors");
            return Task.FromResult<TrackUpdate?>(null);
        }
    }
}
