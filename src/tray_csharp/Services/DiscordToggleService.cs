// Stage 7.9 → Stage 7.12 Batch B DIAG-10: DiscordToggleService.
//
// Writes `discord_rpc.enabled` to config.json AND immediately POSTs to the
// server's /reload-config endpoint so server.exe re-initialises (or tears
// down) its DiscordRpcService inside ~1 s, instead of waiting until the
// next server restart.  DiscordRpcService has no internal config watcher,
// so a config-file write alone is not enough — the diag-10 failure was
// the tray flipping the flag, the server keeping its previous state.
//
// Config schema matches PS S9 (tray.ps1:4391-4491):
//   discord_rpc: {
//     enabled: bool (default true when missing),
//     client_id: string (preserved on write but not managed by toggle)
//   }

using System.Net.Http;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.Services;

public sealed class DiscordToggleService : IDiscordToggleService
{
    private const string EnabledKey       = "discord_rpc.enabled";
    private const string Component        = "Discord";
    private const string ReloadConfigUrl  = "http://127.0.0.1:4242/reload-config";
    private const bool   DefaultWhenMissing = true;

    private readonly ILogger _logger;
    private readonly IConfigService _config;
    private readonly HttpClient _http;
    private bool _lastKnownState;

    public event EventHandler<bool>? StateChanged;

    public bool IsEnabled => _config.GetValue<bool>(EnabledKey, DefaultWhenMissing);

    public DiscordToggleService(ILogger logger, IConfigService config, HttpClient http)
    {
        _logger = logger;
        _config = config;
        _http   = http;
        _lastKnownState = IsEnabled;
        _config.Changed += OnConfigChanged;
        _logger.Log($"DiscordToggleService initialized; initial state={_lastKnownState}", Component);
    }

    public void Enable()
    {
        _config.SetValue<bool>(EnabledKey, true);
        _logger.Log("enabled", Component);
        _lastKnownState = true;
        StateChanged?.Invoke(this, true);
        _ = NotifyServerReloadAsync();
    }

    public void Disable()
    {
        _config.SetValue<bool>(EnabledKey, false);
        _logger.Log("disabled", Component);
        _lastKnownState = false;
        StateChanged?.Invoke(this, false);
        _ = NotifyServerReloadAsync();
    }

    public void Toggle()
    {
        if (IsEnabled) Disable();
        else Enable();
    }

    // Fire-and-forget POST to the server's /reload-config endpoint so
    // DiscordRpcService re-reads config.json and connects/disconnects from
    // Discord IPC without needing a full server restart.  Failures
    // (server not yet listening, network hiccup) are logged at WARN; the
    // server's own background reload-on-startup will pick the change up.
    private async Task NotifyServerReloadAsync()
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var request    = new HttpRequestMessage(HttpMethod.Post, ReloadConfigUrl)
            {
                Content = new StringContent(string.Empty)
            };
            var response = await _http.SendAsync(request, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
                _logger.Log($"notified server: POST /reload-config → {(int)response.StatusCode}", Component);
            else
                _logger.LogWarn($"server /reload-config returned {(int)response.StatusCode}", Component);
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"notify server /reload-config failed: {ex.Message}", Component);
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        // External writes to the config file (e.g., PS tray flipped flag, or
        // user edited config.json directly) should re-fire StateChanged so
        // the tray menu (7.6) stays in sync.
        var current = IsEnabled;
        if (current != _lastKnownState)
        {
            _lastKnownState = current;
            _logger.Log($"external config change: state={current}", Component);
            StateChanged?.Invoke(this, current);
        }
    }
}
