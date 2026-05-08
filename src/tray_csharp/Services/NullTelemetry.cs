// Stage 7.3: NullTelemetry no-op implementation. Default DI registration
// for ITelemetry in 7.3. Stage 7.5 swaps this for a real implementation
// when detection-layer counters become load-bearing.

namespace MastersFM.Tray.Services;

public sealed class NullTelemetry : ITelemetry
{
    private static readonly IReadOnlyDictionary<string, long> EmptyCounters =
        new Dictionary<string, long>();

    public void IncrementCounter(string name, long delta = 1)
    {
        // no-op
    }

    public void RecordTimingMs(string name, double milliseconds)
    {
        // no-op
    }

    public void RecordEvent(string eventType, IReadOnlyDictionary<string, object>? tags = null)
    {
        // no-op
    }

    public IReadOnlyDictionary<string, long> SnapshotCounters() => EmptyCounters;
}
