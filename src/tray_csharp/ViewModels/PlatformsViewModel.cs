// Stage 7.7: PlatformsViewModel. Backs PlatformsWindow (Surface 06).
// 8 platforms total per dialog inventory; toggles persist to config
// keys platforms.{platformId}.enabled.
//
// Per Q-MOCK-06a=A2 default: ALL platforms ON on first run. Enforced
// via App.xaml.cs first-run logic before the dialog is shown.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.ViewModels;

public sealed partial class PlatformsViewModel : ObservableObject
{
    private const string Component = "Platforms";

    private readonly ILogger _logger;
    private readonly IConfigService _config;

    [ObservableProperty]
    private ObservableCollection<PlatformToggle> platforms = new();

    public PlatformsViewModel(ILogger logger, IConfigService config)
    {
        _logger = logger;
        _config = config;
        SeedPlatforms();
        RefreshFromConfig();
    }

    private void SeedPlatforms()
    {
        // 8 platforms per V14_S7_S7_7_DIALOG_INVENTORY.md
        Platforms.Clear();
        Platforms.Add(new PlatformToggle("soundcloud",   "SoundCloud",          "Browser tab + soundcloud-rpc bridge",   this));
        Platforms.Add(new PlatformToggle("spotify",      "Spotify",             "Spotify desktop app via SMTC",          this));
        Platforms.Add(new PlatformToggle("browser",      "Browser tabs",        "YouTube Music, Apple Music, others",    this));
        Platforms.Add(new PlatformToggle("smtcGeneric",  "Generic SMTC",        "Catches anything publishing to SMTC",   this));
        Platforms.Add(new PlatformToggle("win11Media",   "Windows 11 Media",    "Win11 Media Player app",                this));
        Platforms.Add(new PlatformToggle("osu",          "osu!",                "Currently playing beatmap",             this));
        Platforms.Add(new PlatformToggle("vlc",          "VLC",                 "VLC HTTP control interface",            this));
        Platforms.Add(new PlatformToggle("wmpLegacy",    "Windows Media Player","Legacy WMP detection (window title)",   this));
    }

    public void RefreshFromConfig()
    {
        foreach (var p in Platforms)
        {
            try
            {
                var key = "platforms." + p.PlatformId + ".enabled";
                var enabled = _config.GetValue<bool?>(key);
                // Defaults: if no config entry yet, default to true (per Q-MOCK-06a=A2;
                // first-run enforcement in App.xaml.cs will write all-true before show).
                p.SuppressPersist = true;
                try { p.IsEnabled = enabled.GetValueOrDefault(true); }
                finally { p.SuppressPersist = false; }
            }
            catch (Exception ex)
            {
                _logger.LogWarn($"read platforms.{p.PlatformId}.enabled: {ex.Message}", Component);
            }
        }
    }

    internal void PersistToggle(PlatformToggle toggle)
    {
        try
        {
            var key = "platforms." + toggle.PlatformId + ".enabled";
            _config.SetValue(key, toggle.IsEnabled);
            _logger.Log("toggle " + toggle.PlatformId + "=" + toggle.IsEnabled, Component);
        }
        catch (Exception ex)
        {
            _logger.LogErr("persist toggle " + toggle.PlatformId, ex, Component);
        }
    }

    [RelayCommand]
    private void Close() { }
}

public sealed partial class PlatformToggle : ObservableObject
{
    public string PlatformId { get; }
    public string DisplayName { get; }
    public string HelperText { get; }

    [ObservableProperty]
    private bool isEnabled;

    internal bool SuppressPersist;
    private readonly PlatformsViewModel _owner;

    public PlatformToggle(string id, string name, string helper, PlatformsViewModel owner)
    {
        PlatformId = id;
        DisplayName = name;
        HelperText = helper;
        _owner = owner;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (SuppressPersist) return;
        _owner.PersistToggle(this);
    }
}
