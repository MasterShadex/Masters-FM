// Stage 7.9: DiscordToggleService. Writes `discord_rpc.enabled` config flag
// via IConfigService. Server.exe (already on .NET 8) polls config and
// picks up the flag on its next read cycle. Decoupled from server -- no
// HTTP /reload-config call (PS does this for instant feedback; C# defers
// to server's poll cadence which is sub-second).
//
// Config schema matches PS S9 (tray.ps1:4391-4491):
//   discord_rpc: {
//     enabled: bool (default true when missing),
//     client_id: string (preserved on write but not managed by toggle)
//   }

using MastersFM.Tray.Services;

namespace MastersFM.Tray.Services;

public sealed class DiscordToggleService : IDiscordToggleService
{
    private const string EnabledKey = "discord_rpc.enabled";
    private const string Component = "Discord";
    private const bool DefaultWhenMissing = true;

    private readonly ILogger _logger;
    private readonly IConfigService _config;
    private bool _lastKnownState;

    public event EventHandler<bool>? StateChanged;

    public bool IsEnabled => _config.GetValue<bool>(EnabledKey, DefaultWhenMissing);

    public DiscordToggleService(ILogger logger, IConfigService config)
    {
        _logger = logger;
        _config = config;
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
    }

    public void Disable()
    {
        _config.SetValue<bool>(EnabledKey, false);
        _logger.Log("disabled", Component);
        _lastKnownState = false;
        StateChanged?.Invoke(this, false);
    }

    public void Toggle()
    {
        if (IsEnabled) Disable();
        else Enable();
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
