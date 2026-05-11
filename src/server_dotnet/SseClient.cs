using System;
using System.Threading.Channels;

namespace MastersFM.Server;

/// <summary>
/// Holds per-SSE-client state.
/// Each connected /events client gets one SseClient with its own bounded Channel.
/// The handler's drain loop is the sole reader; Broadcast() calls are sole writers.
/// This per-client queue eliminates concurrent writes to a single response stream (Risk R3).
/// </summary>
public class SseClient
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Bounded channel: producers (Broadcast callers) enqueue SSE frames;
    /// the handler's drain loop dequeues and writes them to the HTTP response.
    ///
    /// Capacity 32 (≈1.5 MB at 47 KB/frame) with DropOldest prevents unbounded
    /// memory growth when a client's drain loop stalls on a dead TCP connection
    /// (no FIN). Without a bound, stale connections accumulate 47 KB frames every
    /// second, causing ~14 MB/min growth during long-running soak tests.
    ///
    /// DropOldest: always accepts the newest (most-current) frame; drops stale ones.
    /// Reconnecting clients receive fresh state on connect regardless of what was dropped.
    /// </summary>
    public Channel<string> Queue { get; } =
        Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,   // only the drain loop reads
            SingleWriter = false,  // heartbeat + webhook + screenshot all write
        });
}
