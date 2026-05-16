using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Latest-wins throttle + sliding-window rate limiter for Discord IPC
/// SET_ACTIVITY writes.
///
/// Discord enforces ~5 SET_ACTIVITY per 20 s per client at the IPC layer.
/// Exceeding the limit doesn't return an error — Discord just silently
/// suppresses updates until the window expires, causing the user-visible
/// "Discord paused for ~20 s" symptom after rapid track skipping.
///
/// To stay under that limit while still feeling instant on single events,
/// this throttle layers three mechanisms:
///
///   1. Base throttle (50 ms): coalesces back-to-back writes from the
///      same call site (e.g. post-cascade + always-fires for one webhook).
///
///   2. Adaptive burst-mode throttle (1500 ms): kicks in when ≥3 sends
///      have happened in the last 1 s.  A rapid skip burst collapses to
///      a single SetActivity showing the FINAL track instead of every
///      intermediate one.
///
///   3. Sliding-window rate limiter (5/20 s): hard safety net.  If 5
///      sends are already recorded in the last 20 s, defer the next
///      write until the oldest of those 5 expires.  The pending activity
///      gets latest-wins-replaced if more updates arrive during the wait,
///      so the deferred write always shows the most recent state.
///
/// Combined with #1 above (no early Discord push in WebhookHandler), a
/// burst of 5+ skips now sends 1-2 SetActivity calls (the final settled
/// state) instead of 10+, and Discord's rate limit is never tripped.
/// </summary>
internal sealed class DiscordRpcThrottle : IDisposable
{
    // ── Tunables ──────────────────────────────────────────────────────────────
    private readonly int  _baseIntervalMs;          // 50 ms — set by DiscordRpcService.ThrottleMs

    // Stage 7.12 Batch B Phase E #3: adaptive burst-mode throttle.
    private const int  BurstThreshold     = 3;     // ≥ this many sends in BurstWindowMs = burst
    private const long BurstWindowMs      = 1000;
    private const int  BurstIntervalMs    = 1500;  // throttle bumped to this in burst mode

    // Stage 7.12 Batch B Phase E #2: Discord's documented limit.
    private const int  MaxSendsPerWindow  = 5;
    private const long RateLimitWindowMs  = 20_000;
    private const long RateLimitSafetyMs  = 200;   // small cushion so we don't tickle the edge

    // ── State (guarded by _lock) ──────────────────────────────────────────────
    private readonly Func<DiscordIpcActivity?, Task> _send;
    private readonly ILogger _logger;
    private readonly object  _lock = new();

    private DiscordIpcActivity? _pending;
    private bool          _hasPending;    // true even when pending==null (clear activity)
    private bool          _timerRunning;
    private long          _lastSentAt;    // epoch ms — last successful DoSendAsync call

    // Sliding window of recent send timestamps.  Capped at MaxSendsPerWindow by
    // the eviction step that runs on every Queue call.  Used by the rate
    // limiter AND the burst detector (which counts entries within BurstWindowMs).
    private readonly Queue<long> _recentSends = new(MaxSendsPerWindow + 1);

    private CancellationTokenSource? _cts;

    public DiscordRpcThrottle(int intervalMs, Func<DiscordIpcActivity?, Task> send, ILogger logger)
    {
        _baseIntervalMs = intervalMs;
        _send           = send;
        _logger         = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Queue(DiscordIpcActivity? activity)
    {
        long now;
        long nextAllowed;
        bool sendNow = false;
        DiscordIpcActivity? toSend = null;
        bool rateLimited = false;

        lock (_lock)
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            EvictExpiredSends(now);
            nextAllowed = ComputeNextAllowedSendMs(now, out rateLimited);

            if (!_timerRunning && nextAllowed <= now)
            {
                // Send window is open — fire and record immediately.
                _lastSentAt = now;
                _recentSends.Enqueue(now);
                sendNow = true;
                toSend  = activity;
            }
            else
            {
                // Throttled or rate-limited — latest wins.
                _pending    = activity;
                _hasPending = true;

                if (!_timerRunning)
                {
                    _timerRunning = true;
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();
                    var waitMs = (int)Math.Max(0, nextAllowed - now);
                    _ = RunCooldownAsync(_cts.Token, waitMs);
                }
            }
        }

        if (rateLimited)
        {
            _logger.LogInformation(
                "Discord throttle: rate-limited (5 sends in last 20 s window); deferring");
        }
        if (sendNow) _ = DoSendAsync(toSend);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    /// <summary>Removes entries older than the rate-limit window. Caller holds lock.</summary>
    private void EvictExpiredSends(long now)
    {
        while (_recentSends.Count > 0 && now - _recentSends.Peek() > RateLimitWindowMs)
            _recentSends.Dequeue();
    }

    /// <summary>
    /// Compute the earliest moment the next send may go out.  Caller holds lock
    /// and has already called EvictExpiredSends.  Sets <paramref name="rateLimited"/>
    /// when the rate-limit window (not the throttle) is the binding constraint.
    /// </summary>
    private long ComputeNextAllowedSendMs(long now, out bool rateLimited)
    {
        rateLimited = false;

        // Base throttle floor: BurstIntervalMs while in burst mode, else _baseIntervalMs.
        int intervalMs = _baseIntervalMs;
        int recentInBurstWindow = 0;
        foreach (var t in _recentSends)
            if (now - t < BurstWindowMs) recentInBurstWindow++;
        if (recentInBurstWindow >= BurstThreshold) intervalMs = BurstIntervalMs;

        long minSendTime = _lastSentAt + intervalMs;

        // Rate-limit ceiling: if we've already sent MaxSendsPerWindow inside the
        // last RateLimitWindowMs, defer until the oldest of those rolls out.
        if (_recentSends.Count >= MaxSendsPerWindow)
        {
            var rateLimitedUntil = _recentSends.Peek() + RateLimitWindowMs + RateLimitSafetyMs;
            if (rateLimitedUntil > minSendTime)
            {
                minSendTime = rateLimitedUntil;
                rateLimited = true;
            }
        }

        return minSendTime;
    }

    private async Task RunCooldownAsync(CancellationToken ct, int initialDelayMs)
    {
        try
        {
            if (initialDelayMs > 0) await Task.Delay(initialDelayMs, ct);

            // Late updates may have arrived while we were waiting and pushed
            // the next-allowed time further out (rate-limit window contains a
            // newer barrier than we thought).  Re-check before sending.
            while (!ct.IsCancellationRequested)
            {
                long now;
                long nextAllowed;
                bool stillRateLimited;
                lock (_lock)
                {
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    EvictExpiredSends(now);
                    nextAllowed = ComputeNextAllowedSendMs(now, out stillRateLimited);
                }
                if (nextAllowed <= now) break;
                await Task.Delay((int)(nextAllowed - now), ct);
            }

            DiscordIpcActivity? toSend;
            bool                hasSend;
            long                sendTime;
            lock (_lock)
            {
                hasSend       = _hasPending;
                toSend        = _pending;
                _pending      = null;
                _hasPending   = false;
                _timerRunning = false;
                sendTime      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (hasSend)
                {
                    _lastSentAt = sendTime;
                    _recentSends.Enqueue(sendTime);
                }
            }

            if (hasSend) await DoSendAsync(toSend);
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
