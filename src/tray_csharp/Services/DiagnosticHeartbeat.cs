// Stage 7.3 + 7.7: DiagnosticHeartbeat. Emits a structured heartbeat
// log line every 60 seconds.
//
// Stage 7.7 adds gc / priv / polls fields to the heartbeat output
// (Workstream 2; addresses Q-RCW-2 from 7.5C). The new fields surface
// in 7.10's 6h soak data: separating managed-heap growth (a C# leak
// signature) from total-WS growth (which includes unmanaged native /
// JIT / WinRT projection).
//
// Cadence: 60s. Runs on DispatcherTimer (UI-thread tick to match WPF
// dispatcher invariants). Started from App.xaml.cs OnStartup after
// MainWindow is shown; stopped from App.xaml.cs OnExit before
// container disposal.

using System.Diagnostics;
using System.Text;
using System.Windows.Threading;

namespace MastersFM.Tray.Services;

public sealed class DiagnosticHeartbeat : IDisposable
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(60);

    // Stage 7.7: detector poll-ms counter names emitted by DetectorOrchestrator
    // (e.g., osu_slow_ticks, vlc_slow_ticks, wmp-legacy_slow_ticks). The
    // heartbeat surfaces total slow-tick count and last-tick aggregate
    // approximations as a stub. Per-detector last/slowest accurate ms values
    // require an ITelemetry timing-snapshot accessor not added in 7.7's
    // locked-list (documented in Stage 7.7 FINAL_REPORT).

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
            // Stage 7.7: managed-heap (GC.GetTotalMemory) vs private-memory
            // (PrivateMemorySize64) separation for diagnostic clarity.
            var gc = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 1);
            var priv = Math.Round(proc.PrivateMemorySize64 / 1024.0 / 1024.0, 1);
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

            // Stage 7.6 STEP 4: Real per-detector P99 latencies via SnapshotTimingsP99().
            // Actual key names (Stage 7.6 STEP 1 inventory wins over brief's expected names):
            //   osu_poll_ms, vlc_poll_ms, wmp-legacy_poll_ms  (DetectorOrchestrator)
            //   webhook_latency_ms                             (WebhookClient)
            //   smtc_dispatch_ms                               (SmtcEventBridge, added STEP 4.3)
            // "-" is emitted for any key with no recorded samples yet.
            var p99 = _telemetry.SnapshotTimingsP99();
            string FmtMs(string key) => p99.TryGetValue(key, out var pv) ? $"{pv:F1}ms" : "-";

            var sb = new StringBuilder(320);
            sb.Append("heartbeat: ws=").Append(ws).Append("MB");
            sb.Append(" gc=").Append(gc).Append("MB");
            sb.Append(" priv=").Append(priv).Append("MB");
            sb.Append(" threads=").Append(threads);
            sb.Append(" handles=").Append(handles);
            sb.Append(" ring=").Append(ring);
            sb.Append(" | ").Append(telemetrySummary);
            sb.Append(" | osu=").Append(FmtMs("osu_poll_ms"));
            sb.Append(" vlc=").Append(FmtMs("vlc_poll_ms"));
            sb.Append(" wmp=").Append(FmtMs("wmp-legacy_poll_ms"));
            sb.Append(" webhook=").Append(FmtMs("webhook_latency_ms"));
            sb.Append(" smtc=").Append(FmtMs("smtc_dispatch_ms"));

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
