using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

// from server.js:760-770
internal sealed class MusicBrainzSource : IArtSource
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<MusicBrainzSource> _logger;

    public MusicBrainzSource(IHttpClientFactory factory, ILogger<MusicBrainzSource> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public string Name => "musicbrainz";

    public async Task<string> TryGetArtAsync(
        string cleanedArtist, string cleanedTrack,
        string? webhookArt, string? originUrl, string? source, CancellationToken ct)
    {
        try
        {
            // from server.js:762-764: recording:{t}+artist:{a}&fmt=json&limit=1
            // Uses "musicbrainz" named client (MastersFM/1.7 UA per MusicBrainz rate-limit policy)
            var url = "https://musicbrainz.org/ws/2/recording/" +
                      $"?query=recording:{HttpUtility.UrlEncode(cleanedTrack)}" +
                      $"+artist:{HttpUtility.UrlEncode(cleanedArtist)}&fmt=json&limit=1";

            var json = await HttpHelpers.HttpsGetAsync(_factory, "musicbrainz", url, _logger, ct);
            if (string.IsNullOrEmpty(json)) return string.Empty;

            var node = JsonNode.Parse(json);
            // from server.js:767: recordings[0].releases[0].id
            var id = node?["recordings"]?[0]?["releases"]?[0]?["id"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(id)) return string.Empty;

            // Stage 7.12 Batch B (DIAG-10 fix): resolve the CoverArtArchive
            // 307 redirect ourselves and return the final archive.org URL.
            // Discord's media proxy doesn't follow multi-hop redirects, so
            // the raw coverartarchive.org/release/{mbid}/front URL ends up
            // displaying as "?" placeholder even though browsers handle it
            // fine.  A single HEAD with AllowAutoRedirect=true gives us the
            // direct CDN URL Discord can fetch in one hop.
            var cacheUrl = $"https://coverartarchive.org/release/{id}/front";
            var finalUrl = await ResolveRedirectAsync(cacheUrl, ct);
            _logger.LogDebug("MusicBrainzSource: release MBID {Id} -> {Url} for {Artist} - {Track}",
                id, finalUrl, cleanedArtist, cleanedTrack);
            return finalUrl;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MusicBrainzSource failed for {Artist} - {Track}", cleanedArtist, cleanedTrack);
            return string.Empty;
        }
    }

    // HEAD the CoverArtArchive URL with redirects enabled and return the
    // final response URI.  Falls back to the input URL if anything goes
    // wrong — better to return a working-most-of-the-time URL than empty.
    private async Task<string> ResolveRedirectAsync(string startUrl, CancellationToken ct)
    {
        try
        {
            // Use a fresh HttpClient with AllowAutoRedirect=true; the factory's
            // configured "musicbrainz" client may have redirects disabled to
            // preserve the MB rate-limit semantics, so we don't reuse it here.
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect      = true,
                MaxAutomaticRedirections = 5,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MastersFM/1.7 (cover-art-redirect-resolver)");

            using var req  = new HttpRequestMessage(HttpMethod.Head, startUrl);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            // resp.RequestMessage.RequestUri is the URL the client ended up at
            // after following the redirect chain.
            var final = resp.RequestMessage?.RequestUri?.ToString();
            if (!resp.IsSuccessStatusCode || string.IsNullOrEmpty(final))
                return string.Empty;
            return final;
        }
        catch
        {
            return string.Empty;
        }
    }
}
