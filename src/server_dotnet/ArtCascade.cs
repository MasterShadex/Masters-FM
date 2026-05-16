using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// Art cascade orchestrator.
// Source order matches server.js:resolveArtwork (678-796) exactly.
// 4.9d: full 11-source cascade including scraping sources.
// Intentional difference (ID-20): LRU cache added (not in server.js) per V14 port plan.
//
// Stage 7.12 Batch B post DIAG-10: split into two phases:
//   Phase 1 (sequential, instant) — local data-URI-producing sources.
//     Walks SMTC / SoundCloudDirect / WebhookArt / SmtcFallback in priority
//     order so the primary art slot is filled with the cheapest source
//     that has art for this track (typically SMTC's thumbnail in <10 ms).
//   Phase 2 (parallel, ~500 ms-2 s) — remote HTTPS-providing sources.
//     Fans out the 7 HTTP sources concurrently and races them for the
//     trackArtHttps slot.  First non-empty HTTPS URL wins; the rest are
//     cancelled.  Replaces the previous sequential walk that could take
//     5-10 s when MusicBrainz/iTunes/Deezer all had to be tried in turn
//     before the cascade gave up.
internal sealed class ArtCascade
{
    private readonly IReadOnlyList<IArtSource> _localSources;
    private readonly IReadOnlyList<IArtSource> _remoteSources;
    private readonly LruCache<string, string>  _cache;
    private readonly ILogger<ArtCascade>       _logger;

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
        // Local sources (return webhookArt-derived data URIs or string.Empty;
        // no outbound HTTP — they all complete synchronously or within a few
        // ms). Walked sequentially in priority order to preserve the
        // server.js:678-796 semantics for the primary slot.
        _localSources = new IArtSource[]
        {
            smtc, scDirect, webhookArt, smtcFallback,
        };

        // Remote sources (do an outbound HTTP request, each takes anywhere
        // from ~200 ms (Deezer cached) to several seconds (MusicBrainz on a
        // cold lookup)). Raced in parallel — any HTTPS URL the operator can
        // get is better than a slower-but-prettier one.
        _remoteSources = new IArtSource[]
        {
            osuScraper, scOembed, deezer, itunes, mb, youTube, bingImage,
        };

        _cache  = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolve art for the given track. Returns (primary, https) where:
    ///   - primary  = first non-empty result the cascade produced (data: URI or HTTPS URL)
    ///   - https    = first HTTPS URL the cascade produced (skips data: URI results)
    /// Both fields default to string.Empty.  LRU-cached under separate keys
    /// so the next play of the same track skips both phases entirely.
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

        // ── Phase 1: sequential locals ────────────────────────────────────────
        foreach (var src in _localSources)
        {
            if (!string.IsNullOrEmpty(primaryArt) && !string.IsNullOrEmpty(httpsArt))
                break;

            string result;
            try
            {
                result = await src.TryGetArtAsync(artist, track, webhookArt, originUrl, source, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ArtCascade (local): {Source} threw", src.Name);
                continue;
            }

            if (string.IsNullOrEmpty(result)) continue;

            if (string.IsNullOrEmpty(primaryArt))
            {
                primaryArt = result;
                _logger.LogInformation(
                    "ArtCascade resolved via {Source} for {Artist} - {Track}",
                    src.Name, artist, track);
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

        // ── Phase 2: parallel remotes, race for HTTPS ─────────────────────────
        if (string.IsNullOrEmpty(httpsArt))
        {
            using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = _remoteSources
                .Select(src => (src, task: SafeInvoke(src, artist, track, webhookArt, originUrl, source, phaseCts.Token)))
                .ToList();

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending.Select(p => p.task));
                var winner    = pending.First(p => p.task == completed);
                pending.Remove(winner);

                var result = await completed; // SafeInvoke never throws
                if (string.IsNullOrEmpty(result)) continue;

                if (string.IsNullOrEmpty(primaryArt))
                {
                    primaryArt = result;
                    _logger.LogInformation(
                        "ArtCascade resolved via {Source} for {Artist} - {Track}",
                        winner.src.Name, artist, track);
                }

                if (string.IsNullOrEmpty(httpsArt)
                    && !result.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    httpsArt = result;
                    _logger.LogInformation(
                        "ArtCascade-HTTPS resolved via {Source} for {Artist} - {Track}",
                        winner.src.Name, artist, track);
                    // Cancel the rest — first HTTPS wins.
                    try { phaseCts.Cancel(); } catch { }
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(primaryArt))
            _logger.LogInformation("ArtCascade: no art found for {Artist} - {Track}", artist, track);

        _cache.Set(key,      primaryArt);
        _cache.Set(keyHttps, httpsArt);
        return (primaryArt, httpsArt);
    }

    // Belt-and-suspenders wrapper: sources MUST NOT throw but if one does, the
    // parallel phase shouldn't crash everyone else with it.  Returns empty
    // string for any failure path.
    private async Task<string> SafeInvoke(
        IArtSource src, string artist, string track,
        string? webhookArt, string? originUrl, string? source,
        CancellationToken ct)
    {
        try
        {
            return await src.TryGetArtAsync(artist, track, webhookArt, originUrl, source, ct);
        }
        catch (OperationCanceledException) { return string.Empty; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArtCascade (remote): {Source} threw", src.Name);
            return string.Empty;
        }
    }
}
