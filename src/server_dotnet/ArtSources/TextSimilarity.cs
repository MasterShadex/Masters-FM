// Stage 7.12 Batch B Phase I #3: Sørensen-Dice coefficient on character bigrams.
//
// Used by the music-DB art sources (Deezer / iTunes / MusicBrainz) to reject
// confidently-wrong matches.  Before Phase I they returned the first search
// result no matter how far it was from the query; now they request the top 5,
// score each result's (artist+title) against the query, and accept only
// matches scoring ≥ 0.75 (75 % similarity).
//
// Why Dice over Levenshtein:
//   - O(n + m) instead of O(n*m).  Streaming millions of webhooks/day → matters.
//   - Order-insensitive — "RemK - Falling 4 You" matches "Falling 4 You - RemK".
//   - Naturally normalizes for length — single-char additions don't drop score.
//
// The bigram-set comparison runs over case-folded, whitespace-collapsed strings
// with punctuation stripped; that handles the common variations ("feat." vs
// "ft.", quotation marks, parenthetical remix tags) without bespoke regex.

using System;
using System.Collections.Generic;
using System.Text;

namespace MastersFM.Server;

internal static class TextSimilarity
{
    /// <summary>
    /// Sørensen-Dice on character bigrams of normalized <paramref name="a"/> and
    /// <paramref name="b"/>.  Result in [0, 1]: 1 = identical post-normalization.
    /// Empty strings or strings with no bigrams score 0.
    /// </summary>
    public static double Dice(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length < 2 || nb.Length < 2) return 0.0;

        var bigramsA = BigramCounts(na);
        var bigramsB = BigramCounts(nb);

        int overlap = 0;
        int totalA  = 0;
        foreach (var kv in bigramsA) totalA += kv.Value;
        int totalB  = 0;
        foreach (var kv in bigramsB) totalB += kv.Value;

        foreach (var kv in bigramsA)
        {
            if (bigramsB.TryGetValue(kv.Key, out var cb))
                overlap += Math.Min(kv.Value, cb);
        }

        return (2.0 * overlap) / (totalA + totalB);
    }

    /// <summary>
    /// Lower-case, collapse whitespace, strip ASCII punctuation except hyphens.
    /// Keeps Unicode letters / digits.
    /// </summary>
    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        bool lastWasSpace = false;
        foreach (var rawCh in s)
        {
            var ch = char.ToLowerInvariant(rawCh);
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }
            // Keep letters, digits, hyphens.  Drop punctuation.
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        // Trim trailing space.
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    private static Dictionary<string, int> BigramCounts(string s)
    {
        var dict = new Dictionary<string, int>(s.Length);
        for (int i = 0; i + 1 < s.Length; i++)
        {
            var key = s.Substring(i, 2);
            dict.TryGetValue(key, out var n);
            dict[key] = n + 1;
        }
        return dict;
    }
}
