using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

/// <summary>
/// Text normalization helpers for the art cascade and duration resolver.
/// Ports server.js:349-417 (isPlaceholderArtist, cleanTrack, cleanArtist, isValidArt).
///
/// IMPORTANT: Do NOT simplify or alter the regex rules. The order and exact patterns
/// are faithful to server.js and have subtle precedence implications.
/// Unicode support: .NET Regex uses Unicode by default (no explicit flag needed for \p{L}\p{N}).
/// </summary>
internal static class TextNormalization
{
    // ── cleanTrack regexes ────────────────────────────────────────────────────
    // from server.js:368-370 (three sequential .replace() calls)
    // JS /gi flag = C# IgnoreCase; 'g' is implicit in Regex.Replace (replaces all matches).
    // All patterns compiled for reuse across many tracks during art cascade.

    /// <summary>Strips (feat. X) / [feat. X] / (ft. X) / (featuring X) / (with X) patterns.</summary>
    /// from server.js:368 /\s*[\(\[](?:feat|ft|featuring|with)\.?\s[^\)\]]*[\)\]]/gi
    private static readonly Regex s_featPattern = new Regex(
        @"\s*[\(\[](?:feat|ft|featuring|with)\.?\s[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>Strips (remix) / [remix] / (edit) / (version) / (remaster) etc. in parens/brackets.</summary>
    /// from server.js:369 /\s*[\(\[](?:remix|edit|version|mix|remaster|remastered|acoustic|live|radio)[^\)\]]*[\)\]]/gi
    private static readonly Regex s_remixBracketPattern = new Regex(
        @"\s*[\(\[](?:remix|edit|version|mix|remaster|remastered|acoustic|live|radio)[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>Strips " - remix" / " - remaster" etc. at end of string.</summary>
    /// from server.js:370 /\s*-\s*(?:remix|edit|version|mix|remaster|remastered|acoustic|live|radio)\s*$/gi
    private static readonly Regex s_remixSuffixPattern = new Regex(
        @"\s*-\s*(?:remix|edit|version|mix|remaster|remastered|acoustic|live|radio)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    // ── isPlaceholderArtist regexes ───────────────────────────────────────────
    // from server.js:358-360

    /// <summary>Matches strings made entirely of placeholder chars: spaces, ?, -, _, ., center-dot, bullet, asterisk.</summary>
    /// from server.js:358 /^[\s\?\-_.·•*]+$/
    private static readonly Regex s_placeholderCharsPattern = new Regex(
        @"^[\s\?\-_\.·•*]+$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>Matches any Unicode letter or digit. No letter/digit = can't be a real name.</summary>
    /// from server.js:360 /[\p{L}\p{N}]/u
    private static readonly Regex s_hasLetterOrDigit = new Regex(
        @"[\p{L}\p{N}]",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    // ── isPlaceholderArtist known-placeholder strings ─────────────────────────
    // from server.js:356 (lowercased for comparison)
    private static readonly string[] s_knownPlaceholders =
        { "unknown", "unknown artist", "various artists", "n/a" };

    // ── isValidArt reject strings ─────────────────────────────────────────────
    // from server.js:413-414
    private const string LastFmPlaceholderHash = "2a96cbd8b46e442fc41c2b86b821562f";
    private const string DefaultAvatarToken    = "default_avatar";

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Strips (feat. X), [remix], " - remaster" etc. from a track title so that
    /// Deezer/iTunes/MusicBrainz searches can find the base recording.
    ///
    /// Example: "Get You (feat. Kali Uchis)" -> "Get You"
    ///          "Track Name (Original Mix)" -> "Track Name"
    ///          "Track - Remastered" -> "Track"
    ///
    /// from server.js:366-372
    /// </summary>
    public static string CleanTrack(string t)
    {
        if (string.IsNullOrEmpty(t)) return t ?? string.Empty;
        t = s_featPattern.Replace(t, string.Empty);
        t = s_remixBracketPattern.Replace(t, string.Empty);
        t = s_remixSuffixPattern.Replace(t, string.Empty);
        return t.Trim();
    }

    /// <summary>
    /// Strips feat. collaborators from an artist field. Takes the first artist in a
    /// comma/ampersand-separated list.
    ///
    /// Example: "Daniel Caesar &amp; H.E.R." -> "Daniel Caesar"
    ///          "Artist A, Artist B" -> "Artist A"
    ///
    /// from server.js:375-377
    /// </summary>
    public static string CleanArtist(string a)
    {
        if (string.IsNullOrEmpty(a)) return a ?? string.Empty;
        // Split on /\s*[,&]\s*/ -- take first element
        // Regex.Split preserves behavior of JS String.prototype.split with regex
        var parts = Regex.Split(a, @"\s*[,&]\s*");
        return parts[0].Trim();
    }

    /// <summary>
    /// Returns true for placeholder / garbage artist strings that SMTC sources emit
    /// when metadata is missing (all '?', 'unknown', empty, etc.).
    ///
    /// Checks (in order from server.js:352-362):
    ///   1. Empty / whitespace-only
    ///   2. Known placeholder literals: "unknown", "unknown artist", "various artists", "n/a"
    ///   3. All placeholder chars: ?, -, _, ., center-dot, bullet, asterisk, whitespace
    ///   4. No Unicode letter or digit (can't be a real name)
    ///
    /// from server.js:352-362
    /// </summary>
    public static bool IsPlaceholderArtist(string? s)
    {
        var v = (s ?? string.Empty).Trim();

        // 1. Empty
        if (v.Length == 0) return true;

        // 2. Known placeholders (case-insensitive)
        var low = v.ToLowerInvariant();
        foreach (var p in s_knownPlaceholders)
            if (low == p) return true;

        // 3. All placeholder chars
        if (s_placeholderCharsPattern.IsMatch(v)) return true;

        // 4. No letter or digit
        if (!s_hasLetterOrDigit.IsMatch(v)) return true;

        return false;
    }

    /// <summary>
    /// from server.js:803-838 (enrichByTitle)
    /// When artist is missing/placeholder, searches Deezer then iTunes by track title
    /// to recover a real artist + starter art URL (VLC/WMP/SoundCloud-RPC placeholder cases).
    ///
    /// Matching precedence (Deezer, server.js:812-822):
    ///   1. First hit where hit.title (lowercased) == title (lowercased) -- exact match
    ///      OR htl.includes(ttl) / ttl.includes(htl) -- substring match either way
    ///   2. First result fallback (hits[0]) if none of the above matched
    ///   3. iTunes first result (server.js:826-835)
    ///   4. null -- could not enrich
    ///
    /// Returns null if not found. Caller updates artist/track/webhookArt from the result.
    /// MUST be called outside WebhookLock (async network calls).
    /// </summary>
    public static async Task<(string Artist, string Track, string Art)?> EnrichByTitleAsync(
        string title, IHttpClientFactory factory, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(title)) return null;

        var q   = HttpUtility.UrlEncode(title);
        var ttl = title.ToLowerInvariant().Trim();

        // 1. Deezer (server.js:806-823) -- no key, fast, best metadata coverage
        try
        {
            var body = await HttpHelpers.HttpsGetAsync(
                factory, "art-generic",
                $"https://api.deezer.com/search?q={q}&limit=5",
                logger, ct);
            if (!string.IsNullOrEmpty(body))
            {
                var node = JsonNode.Parse(body);
                var hits = node?["data"]?.AsArray();
                if (hits != null)
                {
                    // server.js:811-818: exact/substring match first
                    foreach (var hit in hits)
                    {
                        var hitTitle  = hit?["title"]?.GetValue<string>();
                        var hitArtist = hit?["artist"]?["name"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(hitTitle) || string.IsNullOrEmpty(hitArtist)) continue;
                        var htl = hitTitle.ToLowerInvariant().Trim();
                        if (htl == ttl || htl.Contains(ttl, StringComparison.Ordinal)
                            || ttl.Contains(htl, StringComparison.Ordinal))
                        {
                            var art = hit?["album"]?["cover_xl"]?.GetValue<string>()
                                   ?? hit?["album"]?["cover_big"]?.GetValue<string>()
                                   ?? string.Empty;
                            logger.LogInformation(
                                "Enriched (Deezer exact): \"{Title}\" -> \"{Artist} - {Track}\"",
                                title, hitArtist, hitTitle);
                            return (hitArtist, hitTitle, art);
                        }
                    }
                    // server.js:820-822: first-result fallback
                    var first       = hits.Count > 0 ? hits[0] : null;
                    var firstArtist = first?["artist"]?["name"]?.GetValue<string>();
                    var firstTitle  = first?["title"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(firstArtist))
                    {
                        var art = first?["album"]?["cover_xl"]?.GetValue<string>()
                               ?? first?["album"]?["cover_big"]?.GetValue<string>()
                               ?? string.Empty;
                        logger.LogInformation(
                            "Enriched (Deezer first): \"{Title}\" -> \"{Artist} - {Track}\"",
                            title, firstArtist, firstTitle);
                        return (firstArtist, firstTitle ?? title, art);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "enrichByTitle Deezer threw (\"{Title}\")", title);
        }

        // 2. iTunes (server.js:826-835)
        try
        {
            var body = await HttpHelpers.HttpsGetAsync(
                factory, "art-generic",
                $"https://itunes.apple.com/search?term={q}&media=music&limit=1",
                logger, ct);
            if (!string.IsNullOrEmpty(body))
            {
                var node       = JsonNode.Parse(body);
                var hit        = node?["results"]?[0];
                var artistName = hit?["artistName"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(artistName))
                {
                    var trackName = hit?["trackName"]?.GetValue<string>() ?? title;
                    var artUrl    = hit?["artworkUrl100"]?.GetValue<string>() ?? string.Empty;
                    if (!string.IsNullOrEmpty(artUrl))
                        artUrl = artUrl.Replace("100x100bb", "500x500bb", StringComparison.Ordinal);
                    logger.LogInformation(
                        "Enriched (iTunes): \"{Title}\" -> \"{Artist} - {Track}\"",
                        title, artistName, trackName);
                    return (artistName, trackName, artUrl);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "enrichByTitle iTunes threw (\"{Title}\")", title);
        }

        logger.LogInformation(
            "Could not enrich \"{Title}\" -- artist stays unknown", title);
        return null;
    }

    /// <summary>
    /// Returns true if the URL is a usable art URL. Rejects:
    ///   - null / empty
    ///   - Last.fm placeholder hash (2a96cbd8b46e442fc41c2b86b821562f)
    ///   - default_avatar URLs
    ///   - Anything that does not start with "http" or "data:image/"
    ///
    /// from server.js:411-417
    /// </summary>
    public static bool IsValidArt(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (url.Contains(LastFmPlaceholderHash, StringComparison.Ordinal)) return false;
        if (url.Contains(DefaultAvatarToken, StringComparison.Ordinal)) return false;
        // Accept http(s) URLs or base64 data URIs (SMTC thumbnails from tray)
        return url.StartsWith("http", StringComparison.Ordinal)
            || url.StartsWith("data:image/", StringComparison.Ordinal);
    }
}
