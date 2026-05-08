// Stage 7.5: Real ITelemetry implementation (Arm 4). Replaces NullTelemetry
// from 7.3. Counters via ConcurrentDictionary; sliding-window timings via
// per-name ring buffer. Snapshot returns immutable view for
// DiagnosticHeartbeat to log.

using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace MastersFM.Tray.Services;

public sealed class Telemetry : ITelemetry
{
    private const int TimingWindowSize = 1024; // Stage 7.6 STEP 3: expanded from 100 for P99 accuracy

    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _timings = new();

    public void IncrementCounter(string name, long delta = 1)
    {
        if (string.IsNullOrEmpty(name)) return;
        _counters.AddOrUpdate(name, delta, (_, v) => v + delta);
    }

    public void RecordTimingMs(string name, double milliseconds)
    {
        if (string.IsNullOrEmpty(name)) return;
        var q = _timings.GetOrAdd(name, _ => new ConcurrentQueue<double>());
        q.Enqueue(milliseconds);
        while (q.Count > TimingWindowSize)
        {
            q.TryDequeue(out _);
        }
    }

    public void RecordEvent(string eventType, IReadOnlyDictionary<string, object>? tags = null)
    {
        // For 7.5, simplest: increment a counter named after the event type.
        // Tags ignored (heavyweight; future improvement).
        IncrementCounter($"event_{eventType}");
    }

    public IReadOnlyDictionary<string, long> SnapshotCounters()
    {
        return _counters.ToImmutableDictionary();
    }

    /// <summary>
    /// Returns the P99 latency (ms) for every timing key that has at least one recorded sample.
    /// Thread-safe: ToArray() on ConcurrentQueue produces a consistent point-in-time snapshot.
    /// Algorithm: sort snapshot, pick index = Ceiling(0.99 * (n-1)).
    /// </summary>
    public IReadOnlyDictionary<string, double> SnapshotTimingsP99()
    {
        var result = new Dictionary<string, double>(_timings.Count);
        foreach (var kv in _timings)
        {
            var samples = kv.Value.ToArray(); // thread-safe point-in-time snapshot
            if (samples.Length == 0) continue;
            Array.Sort(samples);
            int idx = (int)Math.Ceiling(0.99 * (samples.Length - 1));
            result[kv.Key] = samples[idx];
        }
        return result;
    }

#if DEBUG
    /// <summary>
    /// Debug self-test: enqueue 1000 samples (1.0..1000.0), compute P99, assert ≈ 990 ±2.
    /// Called once from App.OnStartup #if DEBUG to catch algorithmic regressions.
    /// </summary>
    internal static void SelfTestP99()
    {
        var t = new Telemetry();
        for (int i = 1; i <= 1000; i++)
            t.RecordTimingMs("_selftest", i);
        var snap = t.SnapshotTimingsP99();
        if (!snap.TryGetValue("_selftest", out var p99))
            throw new InvalidOperationException("SnapshotTimingsP99 SelfTest: key missing");
        // Window is 1024 ≥ 1000, so all samples are retained.
        // sorted = [1..1000], n=1000, idx = Ceiling(0.99 * 999) = Ceiling(989.01) = 990
        // samples[990] = 991.0 → |991 - 990| = 1 ≤ 2 ✓
        const double expected = 990.0;
        if (Math.Abs(p99 - expected) > 2.0)
            throw new InvalidOperationException($"SnapshotTimingsP99 SelfTest FAIL: p99={p99:F1} expected≈{expected}±2");
    }
#endif

    /// <summary>Diagnostic helper for DiagnosticHeartbeat: returns a one-line summary of the most-relevant counters.</summary>
    public string GetHeartbeatSummary()
    {
        long Get(string n) => _counters.TryGetValue(n, out var v) ? v : 0;
        var events = Get("smtc_events");
        var polls = Get("polls_per_min");
        var webhooks = Get("webhook_sends");
        var artHits = Get("art_cache_hits");
        var artMisses = Get("art_cache_misses");
        var trackChanges = Get("track_changes");
        return $"events={events} polls={polls} webhooks={webhooks} cache={artHits}/{artMisses} tracks={trackChanges}";
    }
}
