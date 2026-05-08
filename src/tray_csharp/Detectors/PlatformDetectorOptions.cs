// Stage 7.5: PlatformDetectorOptions. Config-backed per-detector enable
// flags. Reads `platforms.{Name}.enabled` from IConfigService. Defaults
// all true per Q-MOCK-06a (locked at re-plan).
//
// VLC HTTP detector additionally reads its port + password from config
// (PS S15 reads these too).

using MastersFM.Tray.Services;

namespace MastersFM.Tray.Detectors;

public sealed class PlatformDetectorOptions
{
    private readonly IConfigService _config;

    public PlatformDetectorOptions(IConfigService config)
    {
        _config = config;
    }

    public bool OsuEnabled => _config.GetValue<bool>("platforms.osu", true);
    public bool VlcEnabled => _config.GetValue<bool>("platforms.VLC", true);
    public bool WmpLegacyEnabled => _config.GetValue<bool>("platforms.WMP", true);
    public bool SpotifyEnabled => _config.GetValue<bool>("platforms.Spotify", true);
    public bool SoundCloudEnabled => _config.GetValue<bool>("platforms.SoundCloud", true);
    public bool BrowserEnabled => _config.GetValue<bool>("platforms.Browser", true);

    /// <summary>VLC HTTP control port (default 8080 matches PS S15 default).</summary>
    public int VlcPort => _config.GetValue<int>("vlc.port", 8080);

    /// <summary>VLC HTTP control password (default empty matches PS S15).</summary>
    public string VlcPassword => _config.GetValue<string>("vlc.password") ?? string.Empty;
}
