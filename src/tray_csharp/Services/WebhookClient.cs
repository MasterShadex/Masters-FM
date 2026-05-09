// Stage 7.5: WebhookClient. Posts TrackUpdate JSON to server.exe webhook
// endpoint. Preserves PS S15 webhook contract (byte-equivalent JSON for
// the same logical track) per absolute rule 11.
//
// PS S15 webhook reference (tray.ps1: Send-Webhook function around line
// 5344-5368): POST to http://127.0.0.1:4242/webhook with JSON body
// {artist, track, source, ...}. Tray version field added per Q3 default
// for parallel-period diagnostics.

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MastersFM.Tray.Detectors;

namespace MastersFM.Tray.Services;

public sealed class WebhookClient
{
    private const string Component = "Webhook";
    private const string EndpointUrl = "http://127.0.0.1:4242/webhook";
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;

    public WebhookClient(HttpClient http, ILogger logger, ITelemetry telemetry)
    {
        _http = http;
        _logger = logger;
        _telemetry = telemetry;
    }

    public async Task SendTrackUpdateAsync(TrackUpdate update, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var payload = BuildJsonPayload(update);
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SendTimeout);

            using var resp = await _http.PostAsync(EndpointUrl, content, cts.Token).ConfigureAwait(false);
            sw.Stop();
            _telemetry.RecordTimingMs("webhook_latency_ms", sw.Elapsed.TotalMilliseconds);

            if (resp.IsSuccessStatusCode)
            {
                _telemetry.IncrementCounter("webhook_sends");
            }
            else
            {
                _telemetry.IncrementCounter("webhook_send_errors");
                _logger.LogWarn($"webhook HTTP {(int)resp.StatusCode}: {update.Source} {update.Artist} - {update.Track}", Component);
            }
        }
        catch (TaskCanceledException)
        {
            // Server down or slow; PS pattern is to log once and continue
            _logger.LogWarn($"webhook send timeout/cancel: {update.Source} {update.Artist} - {update.Track}", Component);
            _telemetry.IncrementCounter("webhook_send_errors");
            _telemetry.IncrementCounter("webhook_send_failures");
        }
        catch (HttpRequestException)
        {
            // Server not running; warn once (PS S15 pattern: fire-and-forget, no retry)
            _logger.LogWarn($"webhook send HTTP error: {update.Source} {update.Artist} - {update.Track}", Component);
            _telemetry.IncrementCounter("webhook_send_errors");
            _telemetry.IncrementCounter("webhook_send_failures");
        }
        catch (Exception ex)
        {
            _logger.LogErr("webhook send", ex, Component);
            _telemetry.IncrementCounter("webhook_send_errors");
            _telemetry.IncrementCounter("webhook_send_failures");
        }
    }

    private static string BuildJsonPayload(TrackUpdate update)
    {
        // Match PS S15 / WebhookHandler contract. Key fields:
        // source, artist, track, album, duration (float seconds), positionMs,
        // isPaused, trackArt (URI). Plus 7.5 addition: tray="csharp14" version
        // field (Q3 default) so server logs which tray emitted during parallel period.
        // NOTE: server reads "duration" (seconds) and "trackArt" -- not "durationMs"/"art".
        var obj = new JsonObject
        {
            ["source"] = update.Source,
            ["artist"] = update.Artist,
            ["track"] = update.Track,
            ["album"] = update.Album,
            ["duration"] = update.Duration?.TotalSeconds,
            ["positionMs"] = update.Position?.TotalMilliseconds,
            ["isPaused"] = !update.IsPlaying,
            ["seek"] = update.IsSeek,       // INTERRUPT #3 STEP 5 (Issue 8)
            ["trackArt"] = update.ArtUri,
            ["tray"] = "csharp14",
            ["observedUtc"] = update.ObservedUtc.ToString("o"),
        };
        return obj.ToJsonString();
    }
}
