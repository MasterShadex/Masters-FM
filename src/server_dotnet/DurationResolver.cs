using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:422-486
// Cascade: Deezer -> iTunes -> MusicBrainz. Returns first non-zero duration in ms.
// Returns 0 if no source resolves a duration.
// Intentional difference (ID-21): standalone -- callers decide whether to use the result.
internal sealed class DurationResolver
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<DurationResolver> _logger;

    public DurationResolver(IHttpClientFactory factory, ILogger<DurationResolver> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>Resolve duration for the given artist and track. Returns 0 if not found.</summary>
    public async Task<long> ResolveAsync(string rawArtist, string rawTrack, CancellationToken ct)
    {
        var artist = TextNormalization.CleanArtist(rawArtist);
        var track  = TextNormalization.CleanTrack(rawTrack);

        long ms;

        // 1. Deezer (server.js:444-454)
        ms = await TryDeezerAsync(artist, track, ct);
        if (ms > 0) { _logger.LogDebug("DurationResolver: Deezer resolved {Ms}ms for {Artist} - {Track}", ms, artist, track); return ms; }

        // 3. iTunes (server.js:457-465) -- labeled 3 in server.js (no step 2)
        ms = await TryItunesAsync(artist, track, ct);
        if (ms > 0) { _logger.LogDebug("DurationResolver: iTunes resolved {Ms}ms for {Artist} - {Track}", ms, artist, track); return ms; }

        // 4. MusicBrainz (server.js:468-478)
        ms = await TryMusicBrainzAsync(artist, track, ct);
        if (ms > 0) { _logger.LogDebug("DurationResolver: MusicBrainz resolved {Ms}ms for {Artist} - {Track}", ms, artist, track); return ms; }

        _logger.LogDebug("DurationResolver: no duration found for {Artist} - {Track}", artist, track);
        return 0;
    }

    // from server.js:444-454: data[0].duration * 1000 (Deezer returns seconds)
    private async Task<long> TryDeezerAsync(string artist, string track, CancellationToken ct)
    {
        try
        {
            var q = $"artist:\"{artist}\" track:\"{track}\"";
            var url = $"https://api.deezer.com/search?q={HttpUtility.UrlEncode(q)}&limit=1";
            var json = await HttpHelpers.HttpsGetAsync(_factory, "art-generic", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return 0;
            var node = JsonNode.Parse(json);
            var durSec = node?["data"]?[0]?["duration"]?.GetValue<double>() ?? 0;
            return (long)(durSec * 1000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DurationResolver Deezer failed for {Artist} - {Track}", artist, track);
            return 0;
        }
    }

    // from server.js:457-465: results[0].trackTimeMillis (already ms)
    private async Task<long> TryItunesAsync(string artist, string track, CancellationToken ct)
    {
        try
        {
            var q = HttpUtility.UrlEncode($"{artist} {track}");
            var url = $"https://itunes.apple.com/search?term={q}&media=music&limit=1";
            var json = await HttpHelpers.HttpsGetAsync(_factory, "art-generic", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return 0;
            var node = JsonNode.Parse(json);
            return node?["results"]?[0]?["trackTimeMillis"]?.GetValue<long>() ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DurationResolver iTunes failed for {Artist} - {Track}", artist, track);
            return 0;
        }
    }

    // from server.js:468-478: recordings[0].length (already ms)
    private async Task<long> TryMusicBrainzAsync(string artist, string track, CancellationToken ct)
    {
        try
        {
            var url = "https://musicbrainz.org/ws/2/recording/" +
                      $"?query=recording:{HttpUtility.UrlEncode(track)}" +
                      $"+artist:{HttpUtility.UrlEncode(artist)}&fmt=json&limit=1";
            var json = await HttpHelpers.HttpsGetAsync(_factory, "musicbrainz", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return 0;
            var node = JsonNode.Parse(json);
            return node?["recordings"]?[0]?["length"]?.GetValue<long>() ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DurationResolver MusicBrainz failed for {Artist} - {Track}", artist, track);
            return 0;
        }
    }
}
