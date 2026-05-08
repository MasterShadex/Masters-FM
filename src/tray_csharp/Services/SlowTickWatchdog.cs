// Stage 7.3: SlowTickWatchdog. Threshold-based slow-tick logger; in 7.3
// there is no work loop, so this class is idle. 7.5 (detection) will call
// RecordTickMs from per-detector tick handlers.
//
// Threshold 200 ms aligned with Windows UI contract (anything >200 ms is
// perceptibly laggy) and with PS tray's S16 SLOW TICK pattern at
// tray.ps1:8786 (which triggers at 200 ms tick duration). The C# baseline
// inherits the same threshold rather than re-tuning.

namespace MastersFM.Tray.Services;

public sealed class SlowTickWatchdog
{
    private const double DefaultThresholdMs = 200.0;

    private readonly ILogger _logger;
    private readonly double _thresholdMs;

    public SlowTickWatchdog(ILogger logger)
        : this(logger, DefaultThresholdMs)
    {
    }

    public SlowTickWatchdog(ILogger logger, double thresholdMs)
    {
        _logger = logger;
        _thresholdMs = thresholdMs;
    }

    /// <summary>
    /// Records a tick duration in milliseconds for the named component.
    /// If the duration exceeds the threshold, emits a SLOW TICK warning log line.
    /// </summary>
    public void RecordTickMs(string component, double milliseconds)
    {
        if (milliseconds > _thresholdMs)
        {
            _logger.LogWarn(
                $"SLOW TICK in {component}: {milliseconds:F1}ms (threshold {_thresholdMs:F0}ms)",
                "Diagnostic");
        }
    }
}
