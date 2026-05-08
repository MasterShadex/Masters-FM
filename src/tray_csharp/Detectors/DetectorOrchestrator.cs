// Stage 7.5: DetectorOrchestrator (Arm 2 chain runner). Polls registered
// IPlatformDetector instances at 1-second cadence (per Q-DETREDESIGN-3).
// First non-null wins; ITrackResolver receives the TrackUpdate. SMTC arm
// (Arm 1, SmtcEventBridge) operates independently on its own 250ms drain
// timer; the resolver dedups across both arms.

using System.Diagnostics;
using System.Windows.Threading;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.Detectors;

public sealed class DetectorOrchestrator : IDisposable
{
    private const int CadenceMs = 1000;
    private const int SlowTickThresholdMs = 200;
    private const string Component = "Detect-Orch";

    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;
    private readonly ITrackResolver _resolver;
    private readonly IPlatformDetector[] _detectors;
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _tickCts;
    private bool _started;
    private bool _disposed;

    public DetectorOrchestrator(ILogger logger, ITelemetry telemetry, ITrackResolver resolver, IEnumerable<IPlatformDetector> detectors)
    {
        _logger = logger;
        _telemetry = telemetry;
        _resolver = resolver;
        _detectors = detectors.ToArray();
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(CadenceMs)
        };
        _timer.Tick += OnTick;
        _timer.Start();
        _logger.Log($"started; cadence={CadenceMs}ms detectors={_detectors.Length} ({string.Join(",", _detectors.Select(d => d.Name))})", Component);
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _telemetry.IncrementCounter("polls_per_min");

        // Cancel any straggling previous tick before starting new one.
        try { _tickCts?.Cancel(); } catch { }
        _tickCts = new CancellationTokenSource();
        var ct = _tickCts.Token;

        foreach (var det in _detectors)
        {
            if (ct.IsCancellationRequested) return;
            if (!det.IsEnabled) continue;

            var sw = Stopwatch.StartNew();
            TrackUpdate? result = null;
            try
            {
                result = await det.PollAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogErr($"{det.Name} poll", ex, Component);
                _telemetry.IncrementCounter($"{det.Name}_poll_errors");
            }
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            _telemetry.RecordTimingMs($"{det.Name}_poll_ms", ms);
            if (ms > SlowTickThresholdMs)
            {
                _logger.LogWarn($"SLOW TICK {det.Name}: {ms:F1}ms (threshold {SlowTickThresholdMs}ms)", "Diagnostic");
                _telemetry.IncrementCounter($"{det.Name}_slow_ticks");
            }

            if (result != null)
            {
                _telemetry.IncrementCounter($"detector_hit_{det.Name}");
                _resolver.OnTrackChanged(result);
                return;  // first non-null wins
            }
        }
    }

    public void Stop()
    {
        try { _timer?.Stop(); } catch { }
        try { _tickCts?.Cancel(); } catch { }
        _logger.Log("stopped", Component);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _tickCts?.Dispose(); } catch { }
    }
}
