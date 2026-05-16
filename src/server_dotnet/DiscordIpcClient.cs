// Stage 7.12 Batch B Phase B: native Discord IPC client.  Replaces
// Lachee.DiscordRPC 1.2.1.24 with a direct implementation of Discord's
// named-pipe protocol so we can:
//   - Set the activity type field directly (Lachee never exposed it; we had
//     been injecting it via a Newtonsoft JsonConverter hack against
//     JsonConvert.DefaultSettings, which works but is fragile).
//   - Eliminate Lachee's internal threading + connection-management
//     overhead (~10-30 ms per push, plus opaque queueing on disconnect).
//   - Have full visibility into every byte sent to Discord, both for
//     debugging and so future Discord-side protocol changes don't get
//     gated on a Lachee NuGet release.
//
// Protocol reference: https://discord.com/developers/docs/topics/rpc
//
// Frame layout (little-endian, on the wire):
//   uint32  opcode      0 = HANDSHAKE
//                       1 = FRAME (commands)
//                       2 = CLOSE
//                       3 = PING
//                       4 = PONG
//   uint32  payload_len (bytes)
//   byte[]  payload     UTF-8 JSON
//
// Pipe path (Windows):   \\.\pipe\discord-ipc-N   (N = 0..9)
//   Discord chooses the lowest free index; we try 0 first and walk up
//   until an open succeeds.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

internal enum DiscordIpcOpcode : uint
{
    Handshake = 0,
    Frame     = 1,
    Close     = 2,
    Ping      = 3,
    Pong      = 4,
}

internal sealed class DiscordIpcActivity
{
    public int      Type        { get; set; } = 2;   // 2 = Listening
    public string?  Details     { get; set; }
    public string?  State       { get; set; }
    public long?    StartUnixMs { get; set; }
    public long?    EndUnixMs   { get; set; }
    public string?  LargeImage  { get; set; }
    public string?  LargeText   { get; set; }
    public string?  SmallImage  { get; set; }
    public string?  SmallText   { get; set; }
    public DiscordIpcButton[]? Buttons { get; set; }
}

internal sealed class DiscordIpcButton
{
    public required string Label { get; set; }
    public required string Url   { get; set; }
}

internal sealed class DiscordIpcClient : IDisposable
{
    private readonly string _clientId;
    private readonly ILogger _logger;

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private string? _userDisplay;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _ready;
    private volatile bool _disposed;

    public bool IsReady => _ready && _pipe?.IsConnected == true && !_disposed;

    public event Action<string>? Ready;          // arg = "username#discrim" if available
    public event Action<string?>? Closed;        // arg = reason
    public event Action<string>?  Error;         // arg = message

    public DiscordIpcClient(string clientId, ILogger logger)
    {
        _clientId = clientId;
        _logger   = logger;
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>Walk discord-ipc-0..9 until one accepts a connection; send
    /// the handshake and start the read loop.  Returns true on READY.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;

        for (int i = 0; i < 10; i++)
        {
            var pipeName = "discord-ipc-" + i;
            try
            {
                var pipe = new NamedPipeClientStream(
                    ".", pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(500, ct);
                _pipe = pipe;
                _logger.LogInformation("Discord IPC: connected to \\\\.\\pipe\\{Pipe}", pipeName);
                break;
            }
            catch (Exception)
            {
                // Try next index.
            }
        }
        if (_pipe == null)
        {
            Error?.Invoke("no discord-ipc-N pipe accepted a connection");
            return false;
        }

        // Send HANDSHAKE.
        var handshake = new JsonObject
        {
            ["v"]         = 1,
            ["client_id"] = _clientId,
        };
        await SendFrameAsync(DiscordIpcOpcode.Handshake, handshake.ToJsonString(), ct);

        // Spin up the read loop (READY will set _ready=true).
        _readLoopCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));

        // Wait up to 5 s for READY before reporting success.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_ready && DateTime.UtcNow < deadline && !_disposed)
        {
            if (_pipe?.IsConnected != true) return false;
            await Task.Delay(20, ct);
        }
        return _ready;
    }

    // ── SET_ACTIVITY ──────────────────────────────────────────────────────────

    /// <summary>Send a SET_ACTIVITY frame with the given activity.  Pass
    /// activity=null to clear the current presence (matches Lachee
    /// SetPresence(null) semantics).</summary>
    public async Task SetActivityAsync(DiscordIpcActivity? activity, CancellationToken ct = default)
    {
        if (!IsReady) return;

        var args = new JsonObject
        {
            ["pid"]      = System.Diagnostics.Process.GetCurrentProcess().Id,
            ["activity"] = activity == null ? null : BuildActivityJson(activity),
        };
        var frame = new JsonObject
        {
            ["cmd"]   = "SET_ACTIVITY",
            ["args"]  = args,
            ["nonce"] = Guid.NewGuid().ToString(),
        };
        await SendFrameAsync(DiscordIpcOpcode.Frame, frame.ToJsonString(), ct);
    }

    private static JsonNode BuildActivityJson(DiscordIpcActivity a)
    {
        var obj = new JsonObject
        {
            ["type"]    = a.Type,                  // 2 = Listening → progress bar
            ["details"] = a.Details,
            ["state"]   = a.State,
        };
        if (a.StartUnixMs.HasValue || a.EndUnixMs.HasValue)
        {
            var ts = new JsonObject();
            if (a.StartUnixMs.HasValue) ts["start"] = a.StartUnixMs.Value;
            if (a.EndUnixMs.HasValue)   ts["end"]   = a.EndUnixMs.Value;
            obj["timestamps"] = ts;
        }
        if (a.LargeImage != null || a.SmallImage != null
            || a.LargeText != null || a.SmallText != null)
        {
            var assets = new JsonObject();
            if (a.LargeImage != null) assets["large_image"] = a.LargeImage;
            if (a.LargeText  != null) assets["large_text"]  = a.LargeText;
            if (a.SmallImage != null) assets["small_image"] = a.SmallImage;
            if (a.SmallText  != null) assets["small_text"]  = a.SmallText;
            obj["assets"] = assets;
        }
        if (a.Buttons is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var b in a.Buttons)
            {
                arr.Add(new JsonObject
                {
                    ["label"] = b.Label,
                    ["url"]   = b.Url,
                });
            }
            obj["buttons"] = arr;
        }
        return obj;
    }

    // ── Frame I/O ─────────────────────────────────────────────────────────────

    private async Task SendFrameAsync(DiscordIpcOpcode op, string payloadJson, CancellationToken ct)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) return;

        var payload = Encoding.UTF8.GetBytes(payloadJson);
        var header  = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)op);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)payload.Length);

        await _writeLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(header,  0, header.Length,  ct);
            await pipe.WriteAsync(payload, 0, payload.Length, ct);
            await pipe.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            Error?.Invoke("write: " + ex.Message);
            _ready = false;
            try { pipe.Dispose(); } catch { }
            _pipe = null;
            Closed?.Invoke("write failed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var pipe = _pipe;
        var header = new byte[8];
        try
        {
            while (!ct.IsCancellationRequested && pipe is { IsConnected: true })
            {
                int got = await ReadExactAsync(pipe, header, 0, 8, ct);
                if (got != 8) break;

                var op  = (DiscordIpcOpcode)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
                var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                if (len < 0 || len > 1_048_576) // 1 MB sanity cap
                {
                    Error?.Invoke($"unreasonable payload length {len}");
                    break;
                }

                byte[] body = len == 0 ? Array.Empty<byte>() : new byte[len];
                if (len > 0)
                {
                    int gotBody = await ReadExactAsync(pipe, body, 0, len, ct);
                    if (gotBody != len) break;
                }

                HandleFrame(op, body);
            }
        }
        catch (OperationCanceledException) { /* normal on Disconnect */ }
        catch (Exception ex)
        {
            Error?.Invoke("read: " + ex.Message);
        }
        finally
        {
            _ready = false;
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
            Closed?.Invoke("read loop ended");
        }
    }

    private static async Task<int> ReadExactAsync(
        Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await s.ReadAsync(buffer, offset + total, count - total, ct);
            if (n <= 0) return total;
            total += n;
        }
        return total;
    }

    private void HandleFrame(DiscordIpcOpcode op, byte[] payload)
    {
        if (op == DiscordIpcOpcode.Frame)
        {
            string? evt = null;
            string? user = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("evt", out var evtEl))
                    evt = evtEl.GetString();
                if (doc.RootElement.TryGetProperty("data", out var dataEl))
                {
                    if (dataEl.TryGetProperty("user", out var userEl))
                    {
                        var name = userEl.TryGetProperty("username", out var u) ? u.GetString() : null;
                        var disc = userEl.TryGetProperty("discriminator", out var d) ? d.GetString() : null;
                        user = string.IsNullOrEmpty(disc) || disc == "0"
                            ? name
                            : $"{name}#{disc}";
                    }
                }
            }
            catch { /* malformed JSON — ignore */ }

            if (evt == "READY")
            {
                _userDisplay = user;
                _ready = true;
                Ready?.Invoke(user ?? "unknown");
            }
            else if (evt == "ERROR")
            {
                _ready = false;
                Error?.Invoke("Discord returned ERROR frame");
            }
        }
        else if (op == DiscordIpcOpcode.Ping)
        {
            // Echo as PONG with the same payload.
            _ = SendFrameAsync(DiscordIpcOpcode.Pong, Encoding.UTF8.GetString(payload), CancellationToken.None);
        }
        else if (op == DiscordIpcOpcode.Close)
        {
            _ready = false;
            Closed?.Invoke("CLOSE frame received");
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready    = false;
        try { _readLoopCts?.Cancel(); } catch { }
        try { _pipe?.Dispose(); }      catch { }
        _pipe = null;
        try { _readLoopCts?.Dispose(); } catch { }
        _readLoopCts = null;
        try { _writeLock.Dispose(); }  catch { }
    }
}
