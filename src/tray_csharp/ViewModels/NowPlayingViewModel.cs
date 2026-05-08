// Stage 7.5: NowPlayingViewModel. The first of the three viewmodels
// (NowPlaying / UpdateCheck / DetectionStatus). UpdateCheck landed at
// 7.2; NowPlaying lands here. DetectionStatus may follow in a later
// sub-stage if the tray menu surface justifies it.
//
// Subscribes to ITrackResolver.TrackChanged; marshals to UI thread via
// Dispatcher.BeginInvoke. Stage 7.6 will bind tray menu's now-playing
// label to its observable properties.

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using MastersFM.Tray.Detectors;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.ViewModels;

public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly ITrackResolver _resolver;
    private readonly ILogger _logger;

    [ObservableProperty]
    private string? _source;

    [ObservableProperty]
    private string? _artist;

    [ObservableProperty]
    private string? _track;

    [ObservableProperty]
    private TimeSpan? _duration;

    [ObservableProperty]
    private TimeSpan? _position;

    [ObservableProperty]
    private string? _artUri;

    [ObservableProperty]
    private bool _isPlaying;

    public NowPlayingViewModel(ITrackResolver resolver, ILogger logger)
    {
        _resolver = resolver;
        _logger = logger;
        _resolver.TrackChanged += OnTrackChanged;

        // Initial sync (resolver may have a track from before this VM was wired)
        var current = _resolver.CurrentTrack;
        if (current != null) ApplyUpdate(current);

        _logger.Log("NowPlayingViewModel subscribed to ITrackResolver.TrackChanged", "NowPlayingVM");
    }

    private void OnTrackChanged(object? sender, TrackUpdate update)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnTrackChanged(sender, update)));
            return;
        }
        ApplyUpdate(update);
    }

    private void ApplyUpdate(TrackUpdate update)
    {
        Source = update.Source;
        Artist = update.Artist;
        Track = update.Track;
        Duration = update.Duration;
        Position = update.Position;
        ArtUri = update.ArtUri;
        IsPlaying = update.IsPlaying;
    }
}
