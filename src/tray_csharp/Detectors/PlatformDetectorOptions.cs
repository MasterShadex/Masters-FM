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

    // v14 fix: read the EXACT keys PlatformsViewModel.PersistToggle writes --
    // "platforms.<id>.enabled" with the lowercase/camelCase PlatformId from
    // SeedPlatforms. These previously read flat PascalCase keys (platforms.Spotify,
    // platforms.VLC, platforms.WMP, ...) with NO ".enabled" leaf, so the case-sensitive
    // ConfigService.GetValue never resolved them and always returned the default true --
    // i.e. the Platform Detection toggles were dead (a disabled platform stayed enabled).
    public bool OsuEnabled => _config.GetValue<bool>("platforms.osu.enabled", true);
    public bool VlcEnabled => _config.GetValue<bool>("platforms.vlc.enabled", true);
    public bool WmpLegacyEnabled => _config.GetValue<bool>("platforms.wmpLegacy.enabled", true);
    public bool SpotifyEnabled => _config.GetValue<bool>("platforms.spotify.enabled", true);
    public bool SoundCloudEnabled => _config.GetValue<bool>("platforms.soundcloud.enabled", true);
    public bool BrowserEnabled => _config.GetValue<bool>("platforms.browser.enabled", true);
    // v14 fix: the only two SMTC source strings that hit IsSourceEnabled's
    // default-true fallback are "smtc-generic" and "wmpSMTC"; wiring these two
    // accessors makes the Generic SMTC + Windows 11 Media toggles actually work.
    public bool GenericSmtcEnabled => _config.GetValue<bool>("platforms.smtcGeneric.enabled", true);
    public bool Win11MediaEnabled => _config.GetValue<bool>("platforms.win11Media.enabled", true);

    /// <summary>VLC HTTP control port (default 8080 matches PS S15 default).</summary>
    public int VlcPort => _config.GetValue<int>("vlc.port", 8080);

    /// <summary>VLC HTTP control password (default empty matches PS S15).</summary>
    public string VlcPassword => _config.GetValue<string>("vlc.password") ?? string.Empty;
}
