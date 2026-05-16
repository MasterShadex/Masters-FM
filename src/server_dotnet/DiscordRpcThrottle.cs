using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Latest-wins throttle/coalescer for Discord IPC SET_ACTIVITY writes.
///
/// Discord enforces ~5 SET_ACTIVITY per 20 s per client. Exceeding it causes the pipe
/// to hang or silently stop updating. Track changes typically produce 2+ writes each
/// (initial push with placeholder art, second push when real art resolves), so even
/// moderate skipping can push 10+ writes in a few seconds.
///
/// Pattern from discord_rpc.js lines 40-62 (v6.0.4): minimum gap between writes +
/// latest-wins coalescing. If Queue is called during the throttle window the incoming
/// activity REPLACES any pending state and a timer fires the coalesced write at the
/// window boundary.
///
/// Stage 7.12 Batch B (Phase B): swapped Lachee RichPresence for DiscordIpcActivity
/// (our native protocol type) and made the send delegate async so we can await the
/// pipe write in flight rather than fire-and-forget.
///
/// Stage 7.12 Batch B (real-time sync): _intervalMs lowered from 2000 ms to 250 ms
/// (configured in DiscordRpcService.ThrottleMs). 250 ms is Discord's documented
/// rate-limit floor (5/20 s average) and matches the throughput pattern used by
/// official clients during a rapid-skip burst.
/// </summary>
internal sealed class DiscordRpcThrottle : IDisposable
{
    private readonly int                                  _intervalMs;
    private readonly Func<DiscordIpcActivity?, Task>      _send;
    private readonly ILogger                              _logger;
    private readonly object                               _lock = new();

    private DiscordIpcActivity? _pending;
    private bool                _hasPending;    // true even when pending==null (clear activity)
    private bool                _timerRunning;
    private long                _lastSentAt;    // epoch ms of last actual DoSend call
    private CancellationTokenSource? _cts;

    public DiscordRpcThrottle(int intervalMs, Func<DiscordIpcActivity?, Task> send, ILogger logger)
    {
        _intervalMs = intervalMs;
        _send       = send;
        _logger     = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Queue an activity update (or null to clear activity).
    /// If called within the throttle window the latest value replaces any pending one.
    /// Mirrors discord_rpc.js sendActivity() logic (lines 276-303).
    /// </summary>
    public void Queue(DiscordIpcActivity? activity)
    {
        lock (_lock)
        {
            var now     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var elapsed = now - _lastSentAt;

            if (!_timerRunning && elapsed >= _intervalMs)
            {
                // Window is open -- send immediately, no timer needed
                _lastSentAt = now;
                _ = DoSendAsync(activity);
                return;
            }

            // Throttled: latest wins (coalesce intermediate skips)
            _pending    = activity;
            _hasPending = true;

            if (!_timerRunning)
            {
                _timerRunning = true;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _ = RunCooldownAsync(_cts.Token);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task RunCooldownAsync(CancellationToken ct)
    {
        try
        {
            // Wait until the throttle window re-opens
            var waitMs = (int)Math.Max(0,
                _intervalMs - (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastSentAt));
            if (waitMs > 0) await Task.Delay(waitMs, ct);

            DiscordIpcActivity? toSend;
            bool                hasSend;
            lock (_lock)
            {
                hasSend     = _hasPending;
                toSend      = _pending;
                _pending    = null;
                _hasPending = false;
                _timerRunning = false;
            }

            if (hasSend)
            {
                lock (_lock) _lastSentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await DoSendAsync(toSend);
            }
        }
        catch (OperationCanceledException) { /* normal on dispose / reconnect */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DiscordRpcThrottle cooldown error");
            lock (_lock) _timerRunning = false;
        }
    }

    private async Task DoSendAsync(DiscordIpcActivity? activity)
    {
        try { await _send(activity); }
        catch (Exception ex) { _logger.LogWarning(ex, "DiscordRpcThrottle send error"); }
    }
}
