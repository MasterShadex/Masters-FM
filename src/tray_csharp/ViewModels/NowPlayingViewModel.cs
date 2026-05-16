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
//
// Stage 7.12 Batch B Phase L (operator follow-up to Phase I):
//   The tray menu + Platforms dialog were stuck on the RAW SMTC thumbnail
//   data URI from TrackResolver.TrackChanged — they never saw the server's
//   cascade-resolved art (which Phase I made 95-99 % accurate via per-
//   platform routing + similarity scoring).  Now:
//     1. ArtImageSource decoder also handles HTTPS URLs (async fetch).
//     2. After every track change, we poll /current on the server (with
//        a few retries) to pick up the cascade-resolved trackArt/
//        trackArtHttps once the cascade finishes (~80-500 ms typical).
//        Identity check on the response prevents races when the user
//        skips tracks mid-cascade.

using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MastersFM.Tray.Detectors;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.ViewModels;

public sealed partial class NowPlayingViewModel : ObservableObject
{
    private const string ServerCurrentUrl = "http://127.0.0.1:4242/current";
    private const string Component = "NowPlayingVM";

    private readonly ITrackResolver _resolver;
    private readonly ILogger        _logger;
    private readonly HttpClient     _http;

    // Monotonic token used to invalidate stale async art fetches when the
    // user skips tracks mid-load.  Compared at the awaited resumption point;
    // older tokens silently abandon.
    private int _artLoadToken;

    [ObservableProperty] private string?    _source;
    [ObservableProperty] private string?    _artist;
    [ObservableProperty] private string?    _track;
    [ObservableProperty] private TimeSpan?  _duration;
    [ObservableProperty] private TimeSpan?  _position;
    [ObservableProperty] private string?    _artUri;
    [ObservableProperty] private bool       _isPlaying;

    /// <summary>
    /// Decoded thumbnail, ready for WPF Image.Source binding.  Null when no
    /// art is available.  Updated automatically via OnArtUriChanged whenever
    /// ArtUri changes — supports `data:image/...;base64,` URIs (decoded
    /// synchronously, microseconds) AND `http(s)://...` URLs (fetched and
    /// decoded asynchronously off-thread, then dispatched to the UI thread).
    /// </summary>
    [ObservableProperty]
    private BitmapImage? _artImageSource;

    public NowPlayingViewModel(ITrackResolver resolver, ILogger logger, HttpClient http)
    {
        _resolver = resolver;
        _logger   = logger;
        _http     = http;
        _resolver.TrackChanged += OnTrackChanged;

        // Initial sync (resolver may have a track from before this VM was wired)
        var current = _resolver.CurrentTrack;
        if (current != null) ApplyUpdate(current);

        _logger.Log("NowPlayingViewModel subscribed to ITrackResolver.TrackChanged", Component);
    }

    // ── TrackResolver event hook ──────────────────────────────────────────────

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
        Source    = update.Source;
        Artist    = update.Artist;
        Track     = update.Track;
        Duration  = update.Duration;
        Position  = update.Position;
        ArtUri    = update.ArtUri;     // initial raw SMTC art; may be upgraded below
        IsPlaying = update.IsPlaying;

        // Phase L: kick off the server-side art upgrade.  Fire-and-forget;
        // the inner async logic handles its own errors and identity races.
        _ = UpgradeArtFromServerAsync(update.Artist, update.Track);
    }

    // ── Art-URI → BitmapImage decoder (data: synchronous, https: async) ──────

    // CommunityToolkit.Mvvm partial callback: runs whenever ArtUri is set.
    partial void OnArtUriChanged(string? value)
    {
        // Invalidate any in-flight HTTPS fetch — the newer ArtUri supersedes.
        var token = Interlocked.Increment(ref _artLoadToken);

        if (string.IsNullOrEmpty(value))
        {
            ArtImageSource = null;
            return;
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // Synchronous base64 decode — microseconds, no off-thread needed.
            ArtImageSource = DecodeDataUri(value);
            return;
        }

        if (value.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Async fetch + decode.  Older tokens self-abandon on resumption.
            _ = LoadHttpArtAsync(value, token);
            return;
        }

        // Unknown scheme — clear rather than show a placeholder we'd be
        // guessing on.  The OBS overlay / Discord still get their art via
        // the cascade; only the tiny tray-menu thumbnail is affected.
        ArtImageSource = null;
    }

    /// <summary>Decodes a "data:image/...;base64,..." URI into a frozen BitmapImage.</summary>
    private static BitmapImage? DecodeDataUri(string dataUri)
    {
        const string marker = "base64,";
        var idx = dataUri.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        try
        {
            return DecodeBytes(Convert.FromBase64String(dataUri[(idx + marker.Length)..]));
        }
        catch { return null; }
    }

    /// <summary>
    /// Fetch <paramref name="url"/> via HttpClient, decode to a frozen
    /// <see cref="BitmapImage"/>, marshal back to the UI thread and assign
    /// to ArtImageSource — but only if <paramref name="token"/> still
    /// matches the latest art-load request (otherwise a newer track has
    /// arrived and we silently drop this result).
    /// </summary>
    private async Task LoadHttpArtAsync(string url, int token)
    {
        try
        {
            byte[] bytes;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                bytes = await _http.GetByteArrayAsync(url, cts.Token);
            }
            if (token != _artLoadToken) return;

            var bmp = DecodeBytes(bytes);
            if (bmp == null || token != _artLoadToken) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (token != _artLoadToken) return;
                    ArtImageSource = bmp;
                });
            }
            else
            {
                if (token != _artLoadToken) return;
                ArtImageSource = bmp;
            }
        }
        catch (Exception ex)
        {
            // Network blip / 404 / CDN refusing requests — leave the existing
            // ArtImageSource (probably the raw SMTC thumbnail) in place.
            _logger.LogWarn("art HTTP fetch failed: " + ex.Message, Component);
        }
    }

    private static BitmapImage? DecodeBytes(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();   // cross-thread safe
            return bmp;
        }
        catch { return null; }
    }

    // ── Server cascade-resolved-art upgrade poller ────────────────────────────

    /// <summary>
    /// After a TrackChanged event, the server's art cascade typically resolves
    /// a better artwork (Phase I per-platform routing + similarity scoring).
    /// Poll /current with backoff to pick it up.  Identity-checked on every
    /// step so a rapid skip burst silently abandons stale upgrades.
    /// </summary>
    private async Task UpgradeArtFromServerAsync(string? artist, string? track)
    {
        if (string.IsNullOrEmpty(track)) return;

        // Cascade is usually ~80-500 ms; allow up to ~3 s in case a slow art
        // source (e.g., MusicBrainz cold lookup) holds it up.
        int[] delaysMs = { 500, 1500, 3000 };

        foreach (var delayMs in delaysMs)
        {
            try
            {
                await Task.Delay(delayMs);

                // Bail if the user already skipped to a different track.
                if (!string.Equals(Track,  track,  StringComparison.Ordinal) ||
                    !string.Equals(Artist, artist, StringComparison.Ordinal))
                    return;

                using var fetchCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var json = await _http.GetStringAsync(ServerCurrentUrl, fetchCts.Token);
                var node = JsonNode.Parse(json);

                var srvArtist = node?["artist"]?.GetValue<string>();
                var srvTrack  = node?["track"]?.GetValue<string>();
                if (!string.Equals(srvArtist, artist, StringComparison.Ordinal) ||
                    !string.Equals(srvTrack,  track,  StringComparison.Ordinal))
                    continue;   // server hasn't seen the new track yet — retry

                // Prefer the cascade's HTTPS-only field (built specifically for
                // Discord — guaranteed HTTPS, no data: URIs).  Fall back to
                // the primary trackArt (which may be a data URI from SMTC if
                // the cascade hasn't resolved an HTTPS source yet).
                var bestArt = node?["trackArtHttps"]?.GetValue<string>();
                if (string.IsNullOrEmpty(bestArt))
                    bestArt = node?["trackArt"]?.GetValue<string>();
                if (string.IsNullOrEmpty(bestArt))
                    continue;   // cascade still running — retry

                // Skip the update if it's the same string we already have
                // (typically the case for SMTC-direct browser sources).
                if (string.Equals(ArtUri, bestArt, StringComparison.Ordinal))
                    return;

                // Apply on the UI thread — OnArtUriChanged fires the
                // synchronous-data / async-HTTPS decode path automatically.
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (string.Equals(Track,  track,  StringComparison.Ordinal) &&
                            string.Equals(Artist, artist, StringComparison.Ordinal))
                            ArtUri = bestArt;
                    });
                }
                else
                {
                    if (string.Equals(Track,  track,  StringComparison.Ordinal) &&
                        string.Equals(Artist, artist, StringComparison.Ordinal))
                        ArtUri = bestArt;
                }
                return;   // upgraded — done
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Server down, malformed JSON, etc.  Try again on the next
                // backoff step.
                _logger.LogWarn("server art upgrade attempt failed: " + ex.Message, Component);
            }
        }
    }
}
