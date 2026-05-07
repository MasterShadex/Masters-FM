using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Background service that broadcasts ": ping\n\n" to all SSE clients every 15 seconds.
/// Mirrors server.js: setInterval(() => { ... res.write(': ping\n\n') }, 15000)
/// IHostedService lifecycle ensures clean shutdown when the ASP.NET Core host stops.
/// </summary>
public class HeartbeatService : BackgroundService
{
    private readonly ServerState _state;

    public HeartbeatService(ServerState state)
    {
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Initial delay matches server.js setInterval semantics:
        // first heartbeat fires 15s after server start, not at t=0.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(15000, ct);
            }
            catch (OperationCanceledException)
            {
                return; // host shutting down
            }

            // Mirrors server.js: if (sseClients.size === 0) return;
            // BroadcastHeartbeat() is a no-op when no clients are connected,
            // so no explicit guard needed here.
            _state.BroadcastHeartbeat();
        }
    }
}
