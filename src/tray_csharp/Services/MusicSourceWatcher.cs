// v14.2.0 — auto-detect "where is music playing right now?" and route the
// spectrum to that endpoint. Solves the Voicemeeter / SteelSeries Sonar / Razer
// Synapse case where music goes to a virtual playback device that's NOT the
// Windows default output. Validated against MasterShadex's own VB-Audio Matrix
// rig (v14.1.9 detection backend probe, Chrome → "Media (VB-Audio Matrix VAIO)").
//
// Flow:
//   1. Subscribe to ITrackResolver.TrackChanged (the existing SMTC signal).
//   2. On each TrackChanged, derive a process hint from TrackUpdate.Source.
//   3. GET http://127.0.0.1:4243/detect-music?app=<hint>
//      (backend in v14.1.9 walks every WASAPI render endpoint, finds the one
//       with the highest-scoring music session above silence).
//   4. If response.endpointId differs from what we last applied, POST
//      /set-device via AudioBackendBridge (which also persists to config).
//
// Honors a master enable flag (audio.autoDetectMusic in config, default true).
// Disabling from the tray menu pauses the watcher; manually-picked devices
// stay until the user re-enables auto-detect.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MastersFM.Tray.Detectors;

namespace MastersFM.Tray.Services;

public sealed class MusicSourceWatcher : IDisposable
{
    private const string Component       = "MusicSourceWatcher";
    private const string DetectUrl       = "http://127.0.0.1:4243/detect-music";
    private const string CfgEnableKey    = "audioAutoDetectMusic";  // flat root key, matches audioSpectrumBackend convention
    // Coalesce rapid SMTC events (scrub / pause / resume bursts). One detection
    // per debounce window is enough — music doesn't change rooms every second.
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(2500);

    private readonly ITrackResolver      _tracks;
    private readonly AudioBackendBridge  _bridge;
    private readonly IConfigService      _config;
    private readonly HttpClient          _http;
    private readonly ILogger             _logger;

    private readonly object _gate = new();
    private CancellationTokenSource? _inFlight;
    private DateTime _lastDetectAt = DateTime.MinValue;
    private string?  _lastAppliedEndpointId;
    private bool     _started;

    public MusicSourceWatcher(
        ITrackResolver tracks,
        AudioBackendBridge bridge,
        IConfigService config,
        HttpClient http,
        ILogger logger)
    {
        _tracks = tracks;
        _bridge = bridge;
        _config = config;
        _http   = http;
        _logger = logger;
    }

    /// <summary>
    /// Read-through to the persisted config flag. Default true (new installs
    /// opt in automatically; existing v14.1.x users get the upgrade behavior).
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                var v = _config.GetValue<bool?>(CfgEnableKey);
                return v ?? true;
            }
            catch { return true; }
        }
    }

    /// <summary>
    /// Persist the toggle (called from the tray menu). When disabled, the
    /// TrackChanged handler short-circuits without hitting /detect-music so
    /// it's effectively zero-cost.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        try
        {
            _config.SetValue(CfgEnableKey, enabled);
            _logger.Log($"auto-detect music source -> {(enabled ? "ON" : "OFF")}", Component);
        }
        catch (Exception ex)
        {
            _logger.LogErr("persist autoDetectMusic", ex, Component);
        }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _tracks.TrackChanged += OnTrackChanged;
        _logger.Log("started — subscribed to ITrackResolver.TrackChanged", Component);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _tracks.TrackChanged -= OnTrackChanged;
        try { _inFlight?.Cancel(); } catch { }
        _logger.Log("stopped", Component);
    }

    public void Dispose() => Stop();

    // ── SMTC handler ────────────────────────────────────────────────────────
    private void OnTrackChanged(object? sender, TrackUpdate update)
    {
        if (!IsEnabled) return;
        if (!update.IsPlaying) return;

        // Debounce: SMTC fires multiple events for a single track-start
        // (metadata-loaded, art-fetched, position-changed). One detection
        // per ~2.5 s window is plenty.
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            if (now - _lastDetectAt < Debounce) return;
            _lastDetectAt = now;
        }

        string hint = DeriveProcessHint(update);
        _ = DetectAndApplyAsync(hint, update.Source);
    }

    // SMTC's "Source" is a platform tag the bridge sets (e.g. "spotify",
    // "youtube_music", "chrome"). Map that back to a process-name hint the
    // /detect-music endpoint can match against Win32_Process sessions.
    private static string DeriveProcessHint(TrackUpdate update)
    {
        string src = (update.Source ?? "").Trim().ToLowerInvariant();
        if (src.Length == 0) return "";

        // Direct app -> process name mappings. Browsers stay as the browser
        // process name because YouTube Music / Tidal Web / SoundCloud run as
        // tabs inside one of these — the /detect-music hint will then bind
        // to whichever browser is hosting an audio session above silence.
        return src switch
        {
            "spotify"        => "Spotify",
            "tidal"          => "TIDAL",
            "apple_music"    => "AppleMusic",
            "youtube_music"  => "chrome",   // most common; tray's SmtcEventBridge.Source records the platform, not the browser
            "youtube"        => "chrome",
            "soundcloud"     => "chrome",
            "deezer"         => "chrome",
            "groove"         => "Music",    // Windows "Groove Music" → "Music.exe"
            "amazon_music"   => "Amazon Music",
            "foobar2000"     => "foobar2000",
            "aimp"           => "AIMP",
            "musicbee"       => "MusicBee",
            "vlc"            => "vlc",
            _                => src        // unknown — let the backend's known-app list catch it
        };
    }

    // ── HTTP + apply ────────────────────────────────────────────────────────
    private async Task DetectAndApplyAsync(string hint, string? source)
    {
        CancellationTokenSource cts = new();
        lock (_gate)
        {
            try { _inFlight?.Cancel(); } catch { }
            _inFlight = cts;
        }
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            string url = string.IsNullOrEmpty(hint)
                ? DetectUrl
                : DetectUrl + "?app=" + Uri.EscapeDataString(hint);

            using var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarn(
                    $"detect-music HTTP {(int)resp.StatusCode} (hint='{hint}', src='{source}')",
                    Component);
                return;
            }

            string body = await resp.Content.ReadAsStringAsync(cts.Token);
            DetectResponse? r;
            try { r = JsonSerializer.Deserialize<DetectResponse>(body); }
            catch (Exception ex)
            {
                _logger.LogWarn($"detect-music parse failed: {ex.Message} (body={body})", Component);
                return;
            }

            if (r is null || !r.Found || string.IsNullOrEmpty(r.EndpointId))
            {
                _logger.Log(
                    $"detect-music: no match (hint='{hint}', src='{source}', reason='{r?.Reason ?? "null"}')",
                    Component);
                return;
            }

            // Already on this endpoint? Don't churn audio_spectrum or persist again.
            if (string.Equals(r.EndpointId, _lastAppliedEndpointId, StringComparison.Ordinal))
                return;

            _logger.Log(
                $"detect-music: found process='{r.Process}' on '{r.EndpointName}' (peak={r.Peak:F3}, hint='{hint}', src='{source}') — routing spectrum",
                Component);

            await _bridge.PushRawAsync("wasapi_loopback", r.EndpointId, cts.Token);
            _lastAppliedEndpointId = r.EndpointId;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer track event or shutdown — silent.
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"detect-music failed: {ex.Message}", Component);
        }
    }

    private sealed record DetectResponse
    {
        [JsonPropertyName("found")]        public bool   Found        { get; init; }
        [JsonPropertyName("endpointId")]   public string? EndpointId  { get; init; }
        [JsonPropertyName("endpointName")] public string? EndpointName{ get; init; }
        [JsonPropertyName("process")]      public string? Process     { get; init; }
        [JsonPropertyName("peak")]         public double Peak         { get; init; }
        [JsonPropertyName("reason")]       public string? Reason      { get; init; }
        [JsonPropertyName("hintUsed")]     public string? HintUsed    { get; init; }
    }
}
