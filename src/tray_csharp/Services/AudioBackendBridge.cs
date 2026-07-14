// Stage 7.12 Batch B Phase O — bridge between the tray's audio-device dialog
// and the running audio_spectrum process.
//
// Phase K + K rev2 surfaced the device lists.  Phase N polished the UI.
// But the click handler only wrote to config keys (audio.outputDeviceId,
// audio.outputDeviceName, audio.selectedBackend) — and those aren't the
// keys audio_spectrum.cs:BootstrapFromConfig reads (it expects
// "audioSpectrumBackend" + "audioSpectrumDevice" with wire-protocol values
// like "wasapi_loopback" / "asio" / "wdm_ks").  Even if the keys matched,
// the running process wouldn't pick up the change until the next restart.
//
// This bridge does three things on every device-selection event:
//   1. Map the tray's display-backend label ("WASAPI" / "MME" / "KS" /
//      "ASIO") to audio_spectrum's wire format.  Strip MME prefix on the
//      device id (audio_spectrum expects bare integer indices).
//   2. POST {backend, id} to http://127.0.0.1:4243/set-device so the
//      running process switches immediately.
//   3. Write the wire-format values back to the config keys
//      audio_spectrum's bootstrap reader looks for, so the selection
//      survives a launcher restart.
//
// Failure mode: the bridge logs and continues.  If audio_spectrum is
// momentarily unreachable (e.g. starting up), the live push fails but
// the config write succeeds — next audio_spectrum start will pick it up
// via BootstrapFromConfig.

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray.Services;

public sealed class AudioBackendBridge
{
    private const string Component       = "AudioBridge";
    private const string SetDeviceUrl    = "http://127.0.0.1:4243/set-device";
    private const string CfgBackendKey   = "audioSpectrumBackend";
    private const string CfgDeviceIdKey  = "audioSpectrumDevice";

    private readonly HttpClient     _http;
    private readonly IConfigService _config;
    private readonly ILogger        _logger;

    public AudioBackendBridge(HttpClient http, IConfigService config, ILogger logger)
    {
        _http   = http;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// v14.2.0: fires after a manual device selection (via PushAsync). The
    /// MusicSourceWatcher subscribes to this and disables itself so the
    /// auto-detect can't override the user's deliberate pick on the next
    /// track change. Event arg = the wire-format backend name (e.g.
    /// "wasapi_loopback", "asio"). PushRawAsync (auto-detect path) does
    /// NOT fire this event — only deliberate user picks count.
    /// </summary>
    public event System.EventHandler<string>? ManualPickPushed;

    /// <summary>
    /// Apply the operator's device selection to the running audio_spectrum AND
    /// persist it to the config keys the spectrum's bootstrap reader consults
    /// (so the selection survives process restart).  Fire-and-forget from the
    /// view-model — exceptions are logged, never thrown.
    /// </summary>
    public async Task PushAsync(AudioDeviceInfo device, CancellationToken ct = default)
    {
        var (backend, id) = MapToWireFormat(device);
        if (backend == null || id == null)
        {
            _logger.LogWarn(
                $"unknown backend '{device.Backend}' for '{device.Name}' — skipping push",
                Component);
            return;
        }

        // 1. Persist to the config keys audio_spectrum.cs:BootstrapFromConfig
        //    actually reads (regex-matched on the raw JSON, so writing at root
        //    level is correct).  These are FLAT keys, not nested under "audio".
        try
        {
            _config.SetValue(CfgBackendKey,  backend);
            _config.SetValue(CfgDeviceIdKey, id);
        }
        catch (System.Exception ex)
        {
            _logger.LogErr("persist spectrum keys", ex, Component);
        }

        // 2. Push live to the running process so the change is immediate.
        await PushToSpectrumAsync(backend, id, ct);

        // 3. v14.2.0: notify subscribers (MusicSourceWatcher) that the user
        //    just made a deliberate pick. The watcher uses this to disable
        //    auto-detect so it can't immediately swap the user's choice
        //    away on the next track change.
        try { ManualPickPushed?.Invoke(this, backend); }
        catch (System.Exception ex) { _logger.LogErr("ManualPickPushed handler", ex, Component); }
    }

    /// <summary>
    /// v14.2.0: raw push for the MusicSourceWatcher — already has (backend, id)
    /// from /detect-music, doesn't want to enumerate AudioDeviceInfo just to
    /// re-derive the wire format. Persists to config (same flat keys as
    /// PushAsync) then POSTs /set-device. Logs a context tag so it's clear in
    /// the diagnostics this came from auto-detect, not a manual pick.
    /// </summary>
    public async Task PushRawAsync(string backend, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(backend) || string.IsNullOrEmpty(id))
        {
            _logger.LogWarn($"PushRawAsync: empty backend or id (backend='{backend}', id='{id}')", Component);
            return;
        }
        // v14.2.7 (red-team fix delta): the AUTO-detect path must NOT persist to
        // config. A "follow the music" auto-switch is a TRANSIENT runtime decision,
        // not a new home device. Persisting it overwrote the operator's saved ASIO
        // pick with a dead wasapi_loopback VB-Matrix bus, so the spectrum then BOOTED
        // onto silence on every restart (config poisoning -> cold-start-on-dead-bus,
        // confirmed live 2026-06-30). Only deliberate manual picks (PushAsync) set the
        // boot default now; auto-detect just pushes the live device for this session
        // and lets the saved home device win again on the next restart.
        await PushToSpectrumAsync(backend, id, ct);
    }

    /// <summary>
    /// Send a single set-device POST to audio_spectrum.  3-second timeout —
    /// audio_spectrum's HandleSetDevice is fast and synchronous; if it doesn't
    /// answer in that window something is wrong (process crashed, port held).
    /// </summary>
    private async Task PushToSpectrumAsync(string backend, string id, CancellationToken ct)
    {
        try
        {
            var bodyJson = JsonSerializer.Serialize(new { backend, id });
            using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(System.TimeSpan.FromSeconds(3));

            using var resp = await _http.PostAsync(SetDeviceUrl, content, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                _logger.Log(
                    $"set-device pushed OK: backend={backend} id={id}",
                    Component);
            }
            else
            {
                _logger.LogWarn(
                    $"set-device HTTP {(int)resp.StatusCode}: backend={backend} id={id}",
                    Component);
            }
        }
        catch (System.Exception ex)
        {
            // audio_spectrum not yet up, port not listening, network glitch.
            // The config write above already happened so audio_spectrum will
            // pick up the selection on its next BootstrapFromConfig.
            _logger.LogWarn(
                $"set-device push failed (spectrum unreachable?): {ex.Message}",
                Component);
        }
    }

    /// <summary>
    /// Translate the tray's display backend tag + DeviceId into the
    /// {backend, id} pair audio_spectrum's /set-device + BootstrapFromConfig
    /// expect.
    ///
    /// Wire-format reference from audio_spectrum.cs:2437-2441:
    ///   "wasapi_loopback", "wasapi_input", "wasapi_exclusive",
    ///   "wdm_ks" | "wdmks" | "ks",  "mme" | "wavein",  "asio".
    /// </summary>
    private static (string? backend, string? id) MapToWireFormat(AudioDeviceInfo device)
    {
        var backend = (device.Backend ?? string.Empty).Trim();
        var rawId   = device.DeviceId ?? string.Empty;

        if (string.Equals(backend, "WASAPI", System.StringComparison.OrdinalIgnoreCase))
        {
            // The tray's WASAPI tab enumerates RENDER endpoints (output devices).
            // For capture, audio_spectrum opens these as loopback — which is
            // exactly the wasapi_loopback backend.  ID is the WinRT endpoint
            // ID which equals the MMDevice ID (audio_spectrum:OpenCapture
            // looks them up via MMDeviceEnumerator.GetDevice(id)).
            return ("wasapi_loopback", rawId);
        }
        if (string.Equals(backend, "WASAPI_INPUT", System.StringComparison.OrdinalIgnoreCase))
        {
            // The tray's Input tab enumerates CAPTURE endpoints (input devices) --
            // microphones, Stereo Mix, AND virtual mics from Sonar / Voicemeeter /
            // VB-Cable. Spectrum opens these in shared-mode WASAPI capture.
            return ("wasapi_input", rawId);
        }
        if (string.Equals(backend, "MME", System.StringComparison.OrdinalIgnoreCase))
        {
            // Tray DeviceId format: "mme-out-N" (set by AudioDeviceViewModel
            // when reading WaveOut caps).  Spectrum expects a bare integer.
            const string Prefix = "mme-out-";
            var idx = rawId.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase)
                ? rawId.Substring(Prefix.Length)
                : rawId;
            return ("mme", idx);
        }
        if (string.Equals(backend, "KS", System.StringComparison.OrdinalIgnoreCase))
        {
            // audio_spectrum treats "wdm_ks" / "wdmks" / "ks" / "wasapi_exclusive"
            // identically — all run the WdmKsCaptureAdapter against an
            // MMDevice capture endpoint.  Use "wasapi_exclusive" as the
            // canonical wire tag since that's what /devices emits.
            return ("wasapi_exclusive", rawId);
        }
        if (string.Equals(backend, "ASIO", System.StringComparison.OrdinalIgnoreCase))
        {
            // DeviceId is already the compound "DriverName|channelOffset"
            // (Phase K rev2 — fetched from spectrum's own /devices, which
            // emits exactly the format set-device expects).
            return ("asio", rawId);
        }

        return (null, null);
    }
}
