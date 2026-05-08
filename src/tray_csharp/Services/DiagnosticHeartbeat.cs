// Stage 7.3: DiagnosticHeartbeat. Emits a structured heartbeat log line
// every 60 seconds with: working set MB, thread count, handle count,
// ring-buffer depth, telemetry counter summary. Runs on a DispatcherTimer
// (UI-thread tick to match WPF dispatcher invariants). Started from
// App.xaml.cs OnStartup after MainWindow is shown; stopped from
// App.xaml.cs OnExit before container disposal.

using System.Diagnostics;
using System.Text;
using System.Windows.Threading;

namespace MastersFM.Tray.Services;

public sealed class DiagnosticHeartbeat : IDisposable
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public DiagnosticHeartbeat(ILogger logger, ITelemetry telemetry)
    {
        _logger = logger;
        _telemetry = telemetry;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = Cadence
        };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        if (_disposed) return;
        _timer.Start();
        _logger.Log($"DiagnosticHeartbeat started (cadence {Cadence.TotalSeconds:F0}s; mode=skeleton)", "Diagnostic");
    }

    public void Stop()
    {
        if (_disposed) return;
        _timer.Stop();
        _logger.Log("DiagnosticHeartbeat stopped", "Diagnostic");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            var ws = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1);
            var threads = proc.Threads.Count;
            var handles = proc.HandleCount;
            var ring = _logger.SnapshotRingBuffer().Count;

            // Stage 7.5: include real telemetry summary inline. The Telemetry
            // type has a GetHeartbeatSummary helper that picks the highest-
            // signal counters; falls back to count/sum for NullTelemetry or
            // any other ITelemetry that doesn't expose the helper.
            string telemetrySummary;
            if (_telemetry is Telemetry concrete)
            {
                telemetrySummary = concrete.GetHeartbeatSummary();
            }
            else
            {
                var counters = _telemetry.SnapshotCounters();
                telemetrySummary = counters.Count == 0
                    ? "counters=0"
                    : $"counters={counters.Count}/sum={SafeSum(counters)}";
            }

            var sb = new StringBuilder(220);
            sb.Append("heartbeat: ws=").Append(ws).Append("MB");
            sb.Append(" threads=").Append(threads);
            sb.Append(" handles=").Append(handles);
            sb.Append(" ring=").Append(ring);
            sb.Append(' ').Append(telemetrySummary);

            _logger.Log(sb.ToString(), "Diagnostic");
        }
        catch (Exception ex)
        {
            _logger.LogErr("DiagnosticHeartbeat.OnTick", ex, "Diagnostic");
        }
    }

    private static long SafeSum(IReadOnlyDictionary<string, long> counters)
    {
        long total = 0;
        foreach (var v in counters.Values)
        {
            total += v;
        }
        return total;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
        catch
        {
            // best-effort
        }
    }
}
