// Stage 7.5: Real ITelemetry implementation (Arm 4). Replaces NullTelemetry
// from 7.3. Counters via ConcurrentDictionary; sliding-window timings via
// per-name ring buffer. Snapshot returns immutable view for
// DiagnosticHeartbeat to log.

using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace MastersFM.Tray.Services;

public sealed class Telemetry : ITelemetry
{
    private const int TimingWindowSize = 100;

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
