using System;
using System.Threading.Channels;

namespace MastersFM.Server;

/// <summary>
/// Holds per-SSE-client state.
/// Each connected /events client gets one SseClient with its own unbounded Channel.
/// The handler's drain loop is the sole reader; Broadcast() calls are sole writers.
/// This per-client queue eliminates concurrent writes to a single response stream (Risk R3).
/// </summary>
public class SseClient
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Unbounded channel: producers (Broadcast callers) enqueue SSE frames;
    /// the handler's drain loop dequeues and writes them to the HTTP response.
    /// Channel.CreateUnbounded is lock-free for single-consumer scenarios.
    /// </summary>
    public Channel<string> Queue { get; } =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,   // only the drain loop reads
            SingleWriter = false,  // heartbeat + webhook + screenshot all write
        });
}
