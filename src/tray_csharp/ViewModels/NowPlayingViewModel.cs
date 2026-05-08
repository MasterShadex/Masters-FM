// Stage 7.5: NowPlayingViewModel. The first of the three viewmodels
// (NowPlaying / UpdateCheck / DetectionStatus). UpdateCheck landed at
// 7.2; NowPlaying lands here. DetectionStatus may follow in a later
// sub-stage if the tray menu surface justifies it.
//
// Subscribes to ITrackResolver.TrackChanged; marshals to UI thread via
// Dispatcher.BeginInvoke. Stage 7.6 will bind tray menu's now-playing
// label to its observable properties.
//
// Stage 7.6 STEP 7: adds ArtImageSource (BitmapImage?) derived from ArtUri
// via OnArtUriChanged partial callback. Lets the tray menu 24x24 thumbnail
// bind to a decoded BitmapImage without a separate IValueConverter file.

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
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

    /// <summary>
    /// Decoded thumbnail, ready for WPF Image.Source binding. Null when no art is available.
    /// Updated automatically via OnArtUriChanged whenever ArtUri changes.
    /// </summary>
    [ObservableProperty]
    private BitmapImage? _artImageSource;

    // CommunityToolkit.Mvvm partial callback: runs whenever ArtUri is set.
    partial void OnArtUriChanged(string? value)
    {
        ArtImageSource = DecodeDataUri(value);
    }

    /// <summary>Decodes a "data:image/...;base64,..." URI into a frozen BitmapImage.</summary>
    private static BitmapImage? DecodeDataUri(string? dataUri)
    {
        if (string.IsNullOrEmpty(dataUri)) return null;
        const string marker = "base64,";
        var idx = dataUri.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        try
        {
            var bytes = Convert.FromBase64String(dataUri.Substring(idx + marker.Length));
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze(); // cross-thread safe
            return bmp;
        }
        catch { return null; }
    }

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
