using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// Art cascade orchestrator.
//
// History:
//   server.js:resolveArtwork (678-796) — sequential walk through 11 sources.
//   Stage 7.12 Batch B post-DIAG-10 — parallelised remote phase; "first HTTPS wins".
//   Stage 7.12 Batch B Phase I — per-platform routing.
//
// Phase I rationale: the parallel race had no concept of source relevance.
// For a YouTube video, Deezer / iTunes / MusicBrainz still searched their
// music catalogs by (artist + track) and the FIRST one to return any result
// won, even when that result was a completely unrelated song with a similar
// title.  Operator-reported 60-70 % accuracy across every platform was
// dominated by that race-the-wrong-source pattern.
//
// New design: a per-platform priority table.  Sources are organised into
// three tiers:
//   • Tier 1 (instant, local data sources) — SMTC thumbnail, webhook art,
//     SMTC fallback, SoundCloudDirect.  Walked sequentially, no HTTP.
//   • Tier 2 (platform-specific HTTP sources) — youtube search for YT,
//     SoundCloud oEmbed + api-v2 for SC, iTunes for Apple Music, etc.
//     Walked sequentially in the table-defined order, since these are the
//     ones we expect to nail it.
//   • Tier 3 (generic music-DB HTTP sources) — Deezer / iTunes / MusicBrainz
//     when the platform is a music platform we don't have a dedicated source
//     for.  Tier 3 is RACED in parallel (first HTTPS wins) because at this
//     point we've already exhausted authoritative options.
//   • Tier 4 (last-resort) — Bing image search.
//
// Source-filter shortcuts inside each IArtSource (e.g. "only fire for
// source=soundcloud") still apply, so this table just says WHICH sources to
// try, in what order.  Each source's own logic handles the "do I actually
// apply" check.
internal sealed class ArtCascade
{
    private readonly IReadOnlyDictionary<string, IArtSource> _byName;
    private readonly LruCache<string, string>                _cache;
    private readonly ILogger<ArtCascade>                     _logger;

    // Per-platform priority.  Each list is the FULL set of sources to try
    // for that platform, in order.  Sources are looked up in _byName at
    // resolve time.
    //
    // Tier 1 (instant locals) come first in every list; Tier 4 (bing-image)
    // comes last.  The middle is platform-specific.
    private static readonly Dictionary<string, string[]> _routes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browser-published video: YouTube's own thumbnail + Bing fallback.
        // No music DB sources — they only return wrong covers for video content.
        ["youtube"]      = new[] { "smtc", "webhook", "smtc-fallback", "youtube",          "bing-image" },
        ["youtubemusic"] = new[] { "smtc", "webhook", "smtc-fallback", "youtube", "itunes", "deezer", "musicbrainz", "bing-image" },

        // SoundCloud: prefer the authoritative oEmbed (when originUrl) and the
        // new api-v2 search.  Deezer/iTunes rarely have indie SC tracks; skip
        // them entirely to avoid wrong-cover false positives.
        ["soundcloud"]   = new[] { "smtc", "soundcloud-direct", "webhook", "smtc-fallback", "soundcloud-oembed", "soundcloud-api", "bing-image" },

        // Spotify (desktop OR Spotify Web in Chrome): SMTC thumbnail is
        // authoritative; remote DBs serve only as fallback when the thumbnail
        // is unavailable (rare).
        ["spotify"]      = new[] { "smtc", "webhook", "smtc-fallback", "deezer", "itunes", "musicbrainz", "bing-image" },

        // Apple Music: iTunes Search API IS the Apple Music catalog — that's
        // our primary, followed by the SMTC thumbnail when in-app.
        ["applemusic"]   = new[] { "smtc", "webhook", "smtc-fallback", "itunes", "deezer", "musicbrainz", "bing-image" },

        // Music platforms we don't have dedicated sources for — race the music DBs.
        ["tidal"]        = new[] { "smtc", "webhook", "smtc-fallback", "deezer", "itunes", "musicbrainz", "bing-image" },
        ["deezer"]       = new[] { "smtc", "webhook", "smtc-fallback", "deezer", "itunes", "musicbrainz", "bing-image" },
        ["bandcamp"]     = new[] { "smtc", "webhook", "smtc-fallback", "musicbrainz", "deezer", "itunes", "bing-image" },
        ["mixcloud"]     = new[] { "smtc", "webhook", "smtc-fallback", "musicbrainz", "deezer", "itunes", "bing-image" },
        ["amazonmusic"]  = new[] { "smtc", "webhook", "smtc-fallback", "deezer", "itunes", "musicbrainz", "bing-image" },
        ["pandora"]      = new[] { "smtc", "webhook", "smtc-fallback", "deezer", "itunes", "musicbrainz", "bing-image" },

        // osu!: dedicated osu-scraper + nothing else useful.
        ["osu"]          = new[] { "smtc", "webhook", "smtc-fallback", "osu-scraper", "bing-image" },

        // Twitch: streams have no album art.  SMTC thumbnail / webhook fallback
        // only — explicitly no music DBs (they'd return wrong cover for any
        // channel name that resembles a song title).
        ["twitch"]       = new[] { "smtc", "webhook", "smtc-fallback" },

        // Generic browser / unknown — race everything, but locals first.
        ["browser"]      = new[] { "smtc", "webhook", "smtc-fallback", "deezer", "itunes", "musicbrainz", "soundcloud-oembed", "youtube", "bing-image" },
        ["smtc-generic"] = new[] { "smtc", "webhook", "smtc-fallback", "deezer", "itunes", "musicbrainz", "bing-image" },
        ["wmpSMTC"]      = new[] { "smtc", "webhook", "smtc-fallback", "deezer", "itunes", "musicbrainz", "bing-image" },
    };
    private static readonly string[] _defaultRoute =
        new[] { "smtc", "webhook", "smtc-fallback", "deezer", "itunes", "musicbrainz", "bing-image" };

    // Sources that are pure local (no outbound HTTP) — walked sequentially
    // at the head of every route.  We never parallelise these because they
    // complete in microseconds and the first non-empty is authoritative.
    private static readonly HashSet<string> _localSourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "smtc", "soundcloud-direct", "webhook", "smtc-fallback",
    };

    public ArtCascade(
        SmtcSource smtc,
        SoundCloudDirectSource scDirect,
        OsuScraperSource osuScraper,
        WebhookArtSource webhookArt,
        SoundCloudOembedSource scOembed,
        SoundCloudApiSearchSource scApi,
        DeezerSource deezer,
        ItunesSource itunes,
        MusicBrainzSource mb,
        SmtcFallbackSource smtcFallback,
        YouTubeSource youTube,
        BingImageSource bingImage,
        LruCache<string, string> cache,
        ILogger<ArtCascade> logger)
    {
        // Resolution order is by-name lookup driven from the _routes table.
        // The injected IArtSource references give us each source's Name property.
        var all = new IArtSource[]
        {
            smtc, scDirect, osuScraper, webhookArt, scOembed, scApi,
            deezer, itunes, mb, smtcFallback, youTube, bingImage,
        };
        _byName = all.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        _cache  = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolve art for the given track.  Returns (primary, https) where:
    ///   - primary  = first non-empty result the cascade produced (data: URI or HTTPS URL)
    ///   - https    = first HTTPS URL the cascade produced (skips data: URI results)
    /// </summary>
    public async Task<(string art, string artHttps)> ResolveAsync(
        string rawArtist, string rawTrack,
        string? webhookArt, string? originUrl, string? source,
        CancellationToken ct)
    {
        var artist   = TextNormalization.CleanArtist(rawArtist);
        var track    = TextNormalization.CleanTrack(rawTrack);
        var key      = $"{artist}|||{track}".ToLowerInvariant();
        var keyHttps = "https||" + key;

        bool gotPrimary = _cache.TryGet(key,      out var cachedPrimary);
        bool gotHttps   = _cache.TryGet(keyHttps, out var cachedHttps);
        if (gotPrimary && gotHttps)
        {
            _logger.LogDebug("ArtCascade cache hit for {Key}", key);
            return (cachedPrimary ?? string.Empty, cachedHttps ?? string.Empty);
        }

        string primaryArt = gotPrimary ? (cachedPrimary ?? string.Empty) : string.Empty;
        string httpsArt   = gotHttps   ? (cachedHttps   ?? string.Empty) : string.Empty;

        var route = ResolveRoute(source);

        // Walk in two passes: sequential locals (instant), then sequential remotes.
        // We do NOT parallelise the remote pass — the per-platform table puts the
        // most-likely-correct source first, and we want it to definitively win
        // rather than race against a less-likely-but-faster source.
        foreach (var name in route)
        {
            if (!string.IsNullOrEmpty(primaryArt) && !string.IsNullOrEmpty(httpsArt))
                break;
            if (!_byName.TryGetValue(name, out var src))
            {
                _logger.LogWarning("ArtCascade: unknown source '{Name}' in route — skipping", name);
                continue;
            }

            string result;
            try
            {
                result = await src.TryGetArtAsync(artist, track, webhookArt, originUrl, source, ct);
            }
            catch (OperationCanceledException) { return (primaryArt, httpsArt); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ArtCascade: {Source} threw", src.Name);
                continue;
            }

            if (string.IsNullOrEmpty(result)) continue;

            if (string.IsNullOrEmpty(primaryArt))
            {
                primaryArt = result;
                _logger.LogInformation(
                    "ArtCascade resolved via {Source} for {Artist} - {Track} (source={SrcPlat})",
                    src.Name, artist, track, source ?? "(none)");
            }
            if (string.IsNullOrEmpty(httpsArt)
                && !result.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                httpsArt = result;
                _logger.LogInformation(
                    "ArtCascade-HTTPS resolved via {Source} for {Artist} - {Track}",
                    src.Name, artist, track);
            }
        }

        if (string.IsNullOrEmpty(primaryArt))
            _logger.LogInformation("ArtCascade: no art found for {Artist} - {Track} (source={SrcPlat})",
                artist, track, source ?? "(none)");

        _cache.Set(key, primaryArt);
        // v14 Phase 5: do NOT cache an EMPTY httpsArt. Caching empty would make a later retry
        // (for a browser source that first got only a data: icon) short-circuit to the cached
        // empty result forever. Leaving it uncached lets a bounded retry re-walk the remote
        // sources for an HTTPS cover. A non-empty httpsArt IS cached (stable once found). The
        // per-track retry cap (ServerState.ArtHttpsRetries) bounds the cost of the re-walks.
        if (!string.IsNullOrEmpty(httpsArt)) _cache.Set(keyHttps, httpsArt);
        return (primaryArt, httpsArt);
    }

    private static string[] ResolveRoute(string? source)
    {
        var key = (source ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return _defaultRoute;
        // exact match first
        if (_routes.TryGetValue(key, out var direct)) return direct;
        // Fuzzy fallback so sources like "apple music" (with space) still match
        // the "applemusic" key — and to catch upstream-tagged variants we
        // didn't enumerate.
        if (key.Contains("youtube") && key.Contains("music")) return _routes["youtubemusic"];
        if (key.Contains("youtube"))    return _routes["youtube"];
        if (key.Contains("apple"))      return _routes["applemusic"];
        if (key.Contains("soundcloud")) return _routes["soundcloud"];
        if (key.Contains("spotify"))    return _routes["spotify"];
        if (key.Contains("twitch"))     return _routes["twitch"];
        if (key.Contains("osu"))        return _routes["osu"];
        return _defaultRoute;
    }
}
