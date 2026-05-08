// Stage 7.3: ITelemetry interface stub. NullTelemetry is the only
// implementation in 7.3; real implementation lands in 7.5 (per
// V14_S7_REPLAN_DETECTION_REDESIGN.md section 8.1 arm 4 telemetry pipeline).
//
// 7.5 will provide concrete implementation; 7.3 ships interface + NullTelemetry
// default for callsites that don't have real metrics yet.

namespace MastersFM.Tray.Services;

public interface ITelemetry
{
    /// <summary>Generic counter increment by delta. Counter named by string id.</summary>
    void IncrementCounter(string name, long delta = 1);

    /// <summary>Generic timing observation in milliseconds.</summary>
    void RecordTimingMs(string name, double milliseconds);

    /// <summary>Structured event with optional tag dictionary.</summary>
    void RecordEvent(string eventType, IReadOnlyDictionary<string, object>? tags = null);

    /// <summary>Snapshot of current counter values for diagnostic readout.</summary>
    IReadOnlyDictionary<string, long> SnapshotCounters();
}
