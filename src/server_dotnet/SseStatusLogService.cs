using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MastersFM.Server;

/// <summary>
/// Background service that logs SSE client count every 60 seconds.
/// Mirrors server.js v11.0.0: setInterval(() => { flog('[STATUS] uptime=Xs sseClients=N') }, 60000)
/// </summary>
public class SseStatusLogService : BackgroundService
{
    private readonly ServerState _state;
    private readonly ILogger<SseStatusLogService> _logger;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    public SseStatusLogService(ServerState state, ILogger<SseStatusLogService> logger)
    {
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(60000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds;
            // Format mirrors server.js: "[STATUS] uptime=Xs sseClients=N"
            _logger.LogInformation("[STATUS] uptime={Uptime}s sseClients={Count}",
                uptimeSeconds, _state.ClientCount);
        }
    }
}
