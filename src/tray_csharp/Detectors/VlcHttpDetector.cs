// Stage 7.5: VlcHttpDetector. Gap-filler for VLC media player via its
// HTTP control interface (default port 8080). VLC must have HTTP control
// enabled in Preferences. PS S15 VLC detection ports the same approach.

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.Detectors;

public sealed class VlcHttpDetector : IPlatformDetector
{
    private const string Component = "Detect-vlc";
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;
    private readonly PlatformDetectorOptions _options;
    private readonly HttpClient _http;

    public string Name => "vlc";
    public bool IsEnabled => _options.VlcEnabled;

    public VlcHttpDetector(ILogger logger, ITelemetry telemetry, PlatformDetectorOptions options, HttpClient http)
    {
        _logger = logger;
        _telemetry = telemetry;
        _options = options;
        _http = http;
    }

    public async Task<TrackUpdate?> PollAsync(CancellationToken ct)
    {
        if (!IsEnabled) return null;
        // Stage 7.5 Strike 1 recovery: pre-check VLC process is running.
        // Without this guard, HTTP-to-localhost-not-listening goes through
        // full TCP connect timeout (~200ms+) on every tick, triggering
        // SAFETY FLOOR's "3 consecutive SLOW TICK" rule. Matches PS S15
        // approach (process-cached check before HTTP).
        var vlcRunning = System.Diagnostics.Process.GetProcessesByName("vlc").Length > 0;
        if (!vlcRunning) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var url = $"http://127.0.0.1:{_options.VlcPort}/requests/status.xml";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // VLC HTTP uses Basic auth with empty username + password.
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_options.VlcPassword}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(body)) return null;

            var xml = XDocument.Parse(body);
            var info = xml.Descendants("info");
            string? artist = null;
            string? track = null;
            string? album = null;
            foreach (var i in info)
            {
                var nameAttr = i.Attribute("name")?.Value ?? string.Empty;
                if (nameAttr == "artist") artist = i.Value;
                else if (nameAttr == "title") track = i.Value;
                else if (nameAttr == "album") album = i.Value;
            }

            if (string.IsNullOrEmpty(track)) return null;

            var stateEl = xml.Descendants("state").FirstOrDefault();
            var isPlaying = stateEl != null && stateEl.Value == "playing";

            return new TrackUpdate
            {
                Source = Name,
                Artist = artist,
                Track = track,
                Album = album,
                IsPlaying = isPlaying,
                PlatformIdentity = $"vlc:{_options.VlcPort}"
            };
        }
        catch (TaskCanceledException)
        {
            // Timeout or VLC not running: silent null
            return null;
        }
        catch (HttpRequestException)
        {
            // VLC HTTP not configured / not running
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogErr("vlc poll", ex, Component);
            _telemetry.IncrementCounter("vlc_poll_errors");
            return null;
        }
    }
}
