using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// Art cascade orchestrator.
// Source order matches server.js:resolveArtwork (678-796) exactly.
// 4.9d: full 11-source cascade including scraping sources.
// Intentional difference (ID-20): LRU cache added (not in server.js) per V14 port plan.
internal sealed class ArtCascade
{
    private readonly IReadOnlyList<IArtSource> _sources;
    private readonly LruCache<string, string> _cache;
    private readonly ILogger<ArtCascade> _logger;

    public ArtCascade(
        SmtcSource smtc,
        SoundCloudDirectSource scDirect,
        OsuScraperSource osuScraper,
        WebhookArtSource webhookArt,
        SoundCloudOembedSource scOembed,
        DeezerSource deezer,
        ItunesSource itunes,
        MusicBrainzSource mb,
        SmtcFallbackSource smtcFallback,
        YouTubeSource youTube,
        BingImageSource bingImage,
        LruCache<string, string> cache,
        ILogger<ArtCascade> logger)
    {
        // 11-source cascade order per server.js:678-796:
        // 1.  SmtcSource           -- smtcThumb + isBrowserPlatform (server.js:686-696)
        // 2.  SoundCloudDirect     -- source=='soundcloud' + SC CDN webhookArt placeholder (server.js:700-703)
        // 3.  OsuScraperSource     -- source.includes('osu'), full beatmapset search (server.js:707-710)
        // 4.  WebhookArtSource     -- isValidArt + t500x500 upgrade + isUrlAccessible (server.js:713-718)
        // 5.  SoundCloudOembed     -- originUrl.includes('soundcloud.com') (server.js:722-731)
        // 6.  DeezerSource         -- api.deezer.com search, cover_xl (server.js:734-743)
        // 7.  ItunesSource         -- itunes search, 500x500bb (server.js:746-757)
        // 8.  MusicBrainzSource    -- MB recording search + coverartarchive (server.js:760-770)
        // 9.  SmtcFallbackSource   -- data: URI last-resort fallback (server.js:772-776)
        // 10. YouTubeSource        -- source.includes('youtube'), videoId scrape (server.js:783-786)
        // 11. BingImageSource      -- always fires, 1.2s deadline (server.js:793-794)
        _sources = new IArtSource[]
        {
            smtc, scDirect, osuScraper, webhookArt,
            scOembed,
            deezer, itunes, mb,
            smtcFallback,
            youTube,
            bingImage,
        };
        _cache  = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolve art for the given track. Returns (primary, https) where:
    ///   - primary  = first non-empty result the cascade produced (data: URI or HTTPS URL)
    ///   - https    = first HTTPS URL the cascade produced (skips data: URI results)
    /// Both fields default to string.Empty.  Single pass through sources;
    /// caches both results under separate LRU keys.
    ///
    /// Stage 7.12 Batch B (post DIAG-10): a separate HTTPS-only art field is
    /// needed for Discord Rich Presence (Discord's CDN rejects data: URIs),
    /// while the overlay uses the primary art directly (data URIs are fine
    /// for the local WPF/WebView2 renderers).
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
            if (string.IsNullOrEmpty(cachedPrimary))
                _logger.LogDebug("ArtCascade cache: known no-art for {Key}", key);
            else
                _logger.LogDebug("ArtCascade cache hit for {Key}", key);
            return (cachedPrimary ?? string.Empty, cachedHttps ?? string.Empty);
        }

        string primaryArt = gotPrimary ? (cachedPrimary ?? string.Empty) : string.Empty;
        string httpsArt   = gotHttps   ? (cachedHttps   ?? string.Empty) : string.Empty;

        foreach (var src in _sources)
        {
            // Early-out: both already found.
            if (!string.IsNullOrEmpty(primaryArt) && !string.IsNullOrEmpty(httpsArt))
                break;

            string result;
            try
            {
                result = await src.TryGetArtAsync(artist, track, webhookArt, originUrl, source, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ArtCascade: source {Source} threw unexpectedly", src.Name);
                result = string.Empty;
            }

            if (string.IsNullOrEmpty(result)) continue;

            // First non-empty wins the primary slot.
            if (string.IsNullOrEmpty(primaryArt))
            {
                primaryArt = result;
                _logger.LogInformation(
                    "ArtCascade resolved via {Source} for {Artist} - {Track}",
                    src.Name, artist, track);
            }

            // First HTTPS URL (skips data: URI results) wins the https slot.
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
            _logger.LogInformation("ArtCascade: no art found for {Artist} - {Track}", artist, track);

        _cache.Set(key,      primaryArt);
        _cache.Set(keyHttps, httpsArt);
        return (primaryArt, httpsArt);
    }
}
