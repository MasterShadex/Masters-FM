using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Singleton server state. Holds current track, boot ID, and the SSE client registry.
/// All public members are thread-safe.
///
/// Sub-stage 4.2: CurrentTrack + ServerBootId
/// Sub-stage 4.3: SSE client registry + Broadcast / BroadcastHeartbeat
/// </summary>
public class ServerState
{
    // ── Boot ID ───────────────────────────────────────────────────────────────
    // Set once at startup. Overlay polls /version and reloads when bootId changes.
    public long ServerBootId { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── Webhook concurrency lock ──────────────────────────────────────────────
    // SemaphoreSlim(1,1) serializes multiple concurrent POST /webhook calls.
    // Mirrors JS single-threaded behavior. NEVER held while doing async I/O.
    // Broadcast always called outside this lock to avoid blocking SSE writes.
    public readonly SemaphoreSlim WebhookLock = new SemaphoreSlim(1, 1);

    // ── Art resolution state ──────────────────────────────────────────────────
    // Mirrors server.js module-level: let artResolved = false; let artResolving = false;
    // Accessed only inside WebhookLock -- no additional locking needed.
    public bool ArtResolved;
    public bool ArtResolving;

    // ── Timestamp diagnostic counter ──────────────────────────────────────────
    // Mirrors server.js: _tsDiagCount, logged every 5 ticks.
    // Interlocked for safe increment without holding WebhookLock.
    private int _tsDiagCount;
    public int IncrementTsDiagCount() => Interlocked.Increment(ref _tsDiagCount);

    // ── Current Track ─────────────────────────────────────────────────────────
    // Lock-protected JsonNode; DeepClone on read/write prevents aliasing.
    // _currentTrackJson caches the serialized form so broadcast-only callers
    // don't pay a DeepClone + ToJsonString() on every 1-second heartbeat.
    // The cache is always kept in sync with _currentTrack under the same lock.
    private readonly object _trackLock = new();
    private JsonNode? _currentTrack;
    private string _currentTrackJson = "null";

    public JsonNode? CurrentTrack
    {
        get { lock (_trackLock) return _currentTrack?.DeepClone(); }
        set
        {
            lock (_trackLock)
            {
                _currentTrack = value?.DeepClone();
                _currentTrackJson = _currentTrack?.ToJsonString() ?? "null";
            }
        }
    }

    /// <summary>
    /// Returns the current track as a pre-serialized JSON string.
    /// Use instead of <c>CurrentTrack?.ToJsonString()</c> in broadcast-only paths
    /// to avoid an unnecessary DeepClone + re-serialization on every heartbeat.
    /// The string is updated atomically whenever CurrentTrack is set.
    /// </summary>
    public string CurrentTrackJson
    {
        get { lock (_trackLock) return _currentTrackJson; }
    }

    // ── Config file paths ─────────────────────────────────────────────────────
    // Mirrors server.js getUserDataDir() + findConfigPath() + getPresetsDir()
    // %APPDATA%\MastersFM\ is Roaming AppData -- survives uninstall/reinstall.

    /// <summary>config.json path: %APPDATA%\MastersFM\config.json</summary>
    public string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MastersFM", "config.json");

    /// <summary>
    /// config_default.json path -- ships with each release in the install folder.
    /// Checks CWD first (server.js primary candidate), then exe directory.
    /// </summary>
    public string ConfigDefaultPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "config_default.json"),
                Path.Combine(AppContext.BaseDirectory, "config_default.json"),
            };
            foreach (var p in candidates)
                if (File.Exists(p)) return p;
            return candidates[0]; // best guess even if missing
        }
    }

    /// <summary>Presets directory: %APPDATA%\MastersFM\presets\</summary>
    public string PresetsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MastersFM", "presets");

    // ── Config file concurrency lock ──────────────────────────────────────────
    // Serializes concurrent POST /save-overlay-config calls.
    // NEVER held during async I/O. Broadcast always called outside this lock.
    public readonly SemaphoreSlim ConfigFileLock = new SemaphoreSlim(1, 1);

    // ── In-memory preview config ──────────────────────────────────────────────
    // Set by POST /preview-config. Cleared when POST /save-overlay-config saves.
    // GET /preview-config returns this if non-null; falls back to saved overlay-config.
    private JsonObject? _previewConfig;
    private readonly object _previewLock = new();

    public JsonObject? PreviewConfig
    {
        get { lock (_previewLock) return _previewConfig?.DeepClone()?.AsObject(); }
        set { lock (_previewLock) _previewConfig = value?.DeepClone()?.AsObject(); }
    }

    // ── SSE Client Registry ───────────────────────────────────────────────────
    // ConcurrentDictionary: thread-safe add/remove without locks.
    // Values are per-client Channel queues; see SseClient.cs.
    private readonly ConcurrentDictionary<Guid, SseClient> _sseClients = new();

    /// <summary>Number of connected SSE clients. Used by the status-log service.</summary>
    public int ClientCount => _sseClients.Count;

    /// <summary>
    /// Registers a new SSE client. Called by the /events handler on connect.
    /// </summary>
    public void Subscribe(SseClient client) =>
        _sseClients[client.Id] = client;

    /// <summary>
    /// Removes an SSE client. Called by the /events handler on disconnect (finally block).
    /// </summary>
    public void Unsubscribe(Guid clientId) =>
        _sseClients.TryRemove(clientId, out _);

    /// <summary>
    /// Broadcasts an SSE frame to all connected clients.
    ///
    /// Frame formats (matches server.js exactly):
    ///   No event name:  "data: {data}\n\n"
    ///   With event:     "event: {eventName}\ndata: {data}\n\n"
    ///
    /// TryWrite is non-blocking and thread-safe for the concurrent-producer case.
    /// Each client's drain loop is the sole consumer of its own channel (Risk R3).
    /// </summary>
    public void Broadcast(string data, string? eventName = null)
    {
        if (_sseClients.IsEmpty) return;

        var sb = new StringBuilder();
        if (eventName != null)
            sb.Append("event: ").Append(eventName).Append('\n');
        // Single-line data: server.js uses JSON.stringify which never produces newlines
        // in the output (compact mode). No multi-line split needed.
        sb.Append("data: ").Append(data).Append('\n');
        sb.Append('\n');
        var frame = sb.ToString();

        // Stage 7.12 Batch B Phase C #6: track the last sent JSON so the
        // BroadcastIfChanged path can dedup heartbeat frames.  Only updates
        // when eventName == null (the track-state channel) — overlay-config
        // and preview-config frames go through unconditionally.
        if (eventName == null)
        {
            lock (_broadcastDedupLock) { _lastBroadcastData = data; }
        }

        foreach (var client in _sseClients.Values)
        {
            // TryWrite is always successful for unbounded channels unless the
            // channel is already completed (client disconnected). Safe to ignore.
            client.Queue.Writer.TryWrite(frame);
        }
    }

    // ── Broadcast dedup (Stage 7.12 Batch B Phase C #6) ──────────────────────
    // The webhook handler unconditionally broadcasts on every webhook, but
    // heartbeats (every 100 ms) typically produce IDENTICAL JSON to the prior
    // broadcast.  Without dedup we push ~470 KB/s of identical SSE frames over
    // loopback to OBS — CEF eats it but it's pure waste.  BroadcastIfChanged
    // compares against _lastBroadcastData and skips when unchanged.  Real
    // changes always go through.
    private readonly object _broadcastDedupLock = new();
    private string _lastBroadcastData = string.Empty;

    /// <summary>
    /// Broadcasts only when <paramref name="data"/> differs from the previous
    /// broadcast (string equality).  Returns true if the frame was sent.
    /// Heartbeats with no state change become no-ops.  Used by the webhook
    /// handler's always-fires path.
    /// </summary>
    public bool BroadcastIfChanged(string data)
    {
        if (_sseClients.IsEmpty) return false;
        lock (_broadcastDedupLock)
        {
            if (string.Equals(data, _lastBroadcastData, StringComparison.Ordinal))
                return false;
            // Note: Broadcast() updates _lastBroadcastData under the same lock.
        }
        Broadcast(data);
        return true;
    }

    // ── Screenshot state ──────────────────────────────────────────────────────
    // Mirrors server.js _ssScreenshot: { counter, pending }.
    // counter: atomically incremented; 0-based string IDs (matches JS String(counter++)).
    // pending: at most ONE pending screenshot at a time.
    //
    // Intentional difference (ID-15): a second /screenshot while one is pending
    // returns 409 (reject) rather than silently replacing the first (server.js bug).

    private long _screenshotCounter = -1; // Increment-to-0 on first call
    private readonly object _screenshotLock = new();
    private ScreenshotPending? _screenshotPending;

    /// <summary>
    /// Returns the next screenshot request ID as a 0-based string.
    /// Mirrors server.js: <c>String(_ssScreenshot.counter++)</c> (first value is "0").
    /// </summary>
    public string NextScreenshotRequestId() =>
        (Interlocked.Increment(ref _screenshotCounter)).ToString();

    /// <summary>
    /// Tries to register a new pending screenshot. Returns false if one is already pending (ID-15).
    /// </summary>
    public bool TryRegisterScreenshot(ScreenshotPending pending)
    {
        lock (_screenshotLock)
        {
            if (_screenshotPending != null) return false;
            _screenshotPending = pending;
            return true;
        }
    }

    /// <summary>
    /// Atomically takes the pending screenshot if it matches <paramref name="requestId"/>.
    /// Sets <paramref name="hadPending"/> to distinguish "no pending" (false) from "mismatch" (true).
    /// Returns null if nothing matched; the caller must check <paramref name="hadPending"/> for the 409 message.
    /// </summary>
    public ScreenshotPending? TryTakeScreenshot(string requestId, out bool hadPending)
    {
        lock (_screenshotLock)
        {
            hadPending = _screenshotPending != null;
            if (_screenshotPending == null) return null;
            if (_screenshotPending.RequestId != requestId) return null; // mismatch
            var taken = _screenshotPending;
            _screenshotPending = null;
            return taken;
        }
    }

    /// <summary>
    /// Takes the current pending screenshot regardless of requestId.
    /// Used by the parse-error path in /screenshot-response to call fail() on whatever is pending.
    /// Mirrors server.js catch block: "if (_ssScreenshot.pending) _ssScreenshot.pending.fail(...)".
    /// </summary>
    public ScreenshotPending? TryTakeAnyScreenshot()
    {
        lock (_screenshotLock)
        {
            var taken = _screenshotPending;
            _screenshotPending = null;
            return taken;
        }
    }

    /// <summary>
    /// Clears the pending slot only if it still matches <paramref name="requestId"/>.
    /// Called from the finally block in /screenshot to prevent state leaks on timeout or exception.
    /// </summary>
    public void ClearScreenshotIfMatch(string requestId)
    {
        lock (_screenshotLock)
        {
            if (_screenshotPending?.RequestId == requestId)
                _screenshotPending = null;
        }
    }

    /// <summary>
    /// Broadcasts the SSE heartbeat comment to all connected clients.
    /// Mirrors server.js: res.write(': ping\n\n')
    /// Called by HeartbeatService every 15 seconds.
    /// </summary>
    public void BroadcastHeartbeat()
    {
        if (_sseClients.IsEmpty) return;
        const string heartbeat = ": ping\n\n";
        foreach (var client in _sseClients.Values)
        {
            client.Queue.Writer.TryWrite(heartbeat);
        }
    }
}

/// <summary>
/// Pending screenshot slot -- holds the request ID and the TCS that /screenshot is awaiting.
/// Resolved by /screenshot-response with PNG bytes; faulted on overlay error or bad payload.
/// </summary>
public class ScreenshotPending
{
    public required string RequestId { get; init; }
    public required TaskCompletionSource<byte[]> Tcs { get; init; }
    public DateTimeOffset RegisteredAt { get; } = DateTimeOffset.UtcNow;
}
