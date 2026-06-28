// SmtcSnapshot — runtime SMTC inspection for the headless tester.
//
// Run: HeadlessTester.exe --smtc
//
// Asks Windows for every live SMTC session right now and dumps every field
// the tray's SmtcEventBridge could read, including the PlaybackType field
// (Music / Video / Image / Unknown). Then predicts what the v14.1.9
// DetectVideoPlatform + StripPlatformPrefix pipeline would do with each
// session — useful for confirming that "Master's FM shows wrong info while
// Prime Video / Netflix is playing" cases get correctly reclassified.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace MastersFM.HeadlessTester;

internal static class SmtcSnapshot
{
    public static int Run()
    {
        Console.WriteLine("=== SMTC runtime snapshot ===");
        Console.WriteLine($"taken at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        var report = new StringBuilder();
        report.AppendLine("# SMTC runtime snapshot");
        report.AppendLine();
        report.AppendLine($"- taken at: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");
        report.AppendLine();

        try
        {
            var task = DumpAsync(report);
            task.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            report.AppendLine($"## FATAL ERROR");
            report.AppendLine();
            report.AppendLine($"`{ex.GetType().Name}`: {ex.Message}");
            report.AppendLine();
            report.AppendLine("```");
            report.AppendLine(ex.ToString());
            report.AppendLine("```");
        }

        var outDir = Path.Combine(AppContext.BaseDirectory, "snapshots",
            "smtc_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(outDir);
        var reportPath = Path.Combine(outDir, "smtc_report.md");
        File.WriteAllText(reportPath, report.ToString());

        Console.WriteLine();
        Console.WriteLine($"report written: {reportPath}");
        return 0;
    }

    private static async Task DumpAsync(StringBuilder report)
    {
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var sessions = mgr.GetSessions();
        var current = mgr.GetCurrentSession();
        var currentSaumid = current?.SourceAppUserModelId ?? "(none)";

        Console.WriteLine($"active sessions: {sessions.Count}");
        Console.WriteLine($"current session : {currentSaumid}");
        Console.WriteLine();

        report.AppendLine($"- active sessions: **{sessions.Count}**");
        report.AppendLine($"- current session: `{currentSaumid}`");
        report.AppendLine();

        if (sessions.Count == 0)
        {
            Console.WriteLine("No SMTC sessions are publishing.");
            report.AppendLine("> No SMTC sessions are publishing.");
            return;
        }

        report.AppendLine("## Per-session detail");
        report.AppendLine();

        int idx = 0;
        foreach (var s in sessions)
        {
            idx++;
            var saumid = s.SourceAppUserModelId ?? "(null)";
            var isCurrent = saumid == currentSaumid ? " <- CURRENT" : "";

            string title = "", artist = "", album = "", albumArtist = "";
            string playbackTypeRaw = "(unread)";
            int playbackTypeInt = -1;
            string genres = "";
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                if (props != null)
                {
                    title = props.Title ?? "";
                    artist = props.Artist ?? "";
                    album = props.AlbumTitle ?? "";
                    albumArtist = props.AlbumArtist ?? "";
                    playbackTypeRaw = props.PlaybackType?.ToString() ?? "(null - Unknown)";
                    playbackTypeInt = props.PlaybackType.HasValue ? (int)props.PlaybackType.Value : 0;
                    try { genres = string.Join(", ", props.Genres ?? Array.Empty<string>()); }
                    catch { genres = "(threw)"; }
                }
            }
            catch (Exception ex)
            {
                playbackTypeRaw = $"(threw: {ex.GetType().Name})";
            }

            string status = "(unread)";
            try
            {
                var info = s.GetPlaybackInfo();
                status = info.PlaybackStatus.ToString();
            }
            catch { }

            string position = "(unread)";
            try
            {
                var tl = s.GetTimelineProperties();
                position = $"{tl.Position.TotalSeconds:0.00}s of {tl.EndTime.TotalSeconds:0.00}s";
            }
            catch { }

            Console.WriteLine($"--- session #{idx}{isCurrent} ---");
            Console.WriteLine($"  SaUMID         : {saumid}");
            Console.WriteLine($"  Title          : {title}");
            Console.WriteLine($"  Artist         : {artist}");
            Console.WriteLine($"  AlbumTitle     : {album}");
            Console.WriteLine($"  PlaybackType   : {playbackTypeRaw}");
            Console.WriteLine($"  Genres         : {genres}");
            Console.WriteLine($"  PlaybackStatus : {status}");
            Console.WriteLine($"  Position       : {position}");
            Console.WriteLine();

            report.AppendLine($"### Session #{idx}{isCurrent}");
            report.AppendLine();
            report.AppendLine($"| Field | Value |");
            report.AppendLine($"|---|---|");
            report.AppendLine($"| SaUMID | `{saumid}` |");
            report.AppendLine($"| Title | `{title}` |");
            report.AppendLine($"| Artist | `{artist}` |");
            report.AppendLine($"| AlbumTitle | `{album}` |");
            report.AppendLine($"| AlbumArtist | `{albumArtist}` |");
            report.AppendLine($"| PlaybackType | `{playbackTypeRaw}` |");
            report.AppendLine($"| Genres | `{genres}` |");
            report.AppendLine($"| PlaybackStatus | `{status}` |");
            report.AppendLine($"| Position | `{position}` |");
            report.AppendLine();
        }

        report.AppendLine("## v14.1.9 production filter prediction");
        report.AppendLine();
        report.AppendLine("What the tray would show in the overlay after");
        report.AppendLine("`DetectVideoPlatform` + `StripPlatformPrefix` run on each session:");
        report.AppendLine();

        int reclassified = 0;
        foreach (var s in sessions)
        {
            string saumid = s.SourceAppUserModelId ?? "";
            string title = "", artist = "", playbackTypeRaw = "(null)";
            int playbackTypeInt = -1;
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                if (props != null)
                {
                    title = props.Title ?? "";
                    artist = props.Artist ?? "";
                    playbackTypeRaw = props.PlaybackType?.ToString() ?? "(null)";
                    playbackTypeInt = props.PlaybackType.HasValue ? (int)props.PlaybackType.Value : 0;
                }
            }
            catch { }

            var (isVideo, platform, sourceKey, reason) = SimulateDetect(saumid, title, playbackTypeInt);
            if (isVideo)
            {
                reclassified++;
                var stripped = StripPlatformPrefix(title, platform);
                report.AppendLine($"- **RECLASSIFY** `{saumid}`");
                report.AppendLine($"    - matched by: {reason}");
                report.AppendLine($"    - raw title: `\"{title}\"`");
                report.AppendLine($"    - overlay shows: track=`\"{stripped}\"` artist=`\"{platform}\"` source=`{sourceKey}`");
                report.AppendLine();
            }
            else
            {
                report.AppendLine($"- pass-through `{saumid}` -- `\"{title}\"` (artist=`{artist}`, playbackType=`{playbackTypeRaw}`)");
                report.AppendLine();
            }
        }

        report.AppendLine($"**{reclassified} of {sessions.Count} session(s) reclassified as video-streaming.**");
    }

    /// <summary>
    /// Mirrors SmtcEventBridge.DetectVideoPlatform (v14.1.9) exactly. Keep
    /// these two tables in lockstep with the production file.
    /// </summary>
    private static (bool IsVideo, string Platform, string SourceKey, string Reason)
        SimulateDetect(string saumid, string title, int playbackTypeInt)
    {
        var idLow = (saumid ?? "").ToLowerInvariant();
        (string T, string P, string K)[] saumidPlatforms =
        {
            ("primevideo",  "Prime Video", "primevideo"),
            ("amazonvideo", "Prime Video", "primevideo"),
            ("amazon.tv",   "Prime Video", "primevideo"),
            ("netflix",     "Netflix",     "netflix"),
            ("disneyplus",  "Disney+",     "disneyplus"),
            ("disney.plus", "Disney+",     "disneyplus"),
            ("hulu",        "Hulu",        "hulu"),
            ("hbo",         "HBO Max",     "hbomax"),
            ("max.app",     "Max",         "max"),
            ("paramount",   "Paramount+",  "paramount"),
            ("peacock",     "Peacock",     "peacock"),
            ("appletv",     "Apple TV+",   "appletv"),
            ("apple.tv",    "Apple TV+",   "appletv"),
            ("crunchyroll", "Crunchyroll", "crunchyroll"),
            ("plex.app",    "Plex",        "plex"),
            ("jellyfin",    "Jellyfin",    "jellyfin"),
            ("vlc",         "VLC",         "vlc"),
            ("mpc-hc",      "MPC-HC",      "mpchc"),
            ("mpv",         "mpv",         "mpv"),
            ("films",       "Films & TV",  "filmstv"),
            ("movies",      "Films & TV",  "filmstv"),
            ("zunevideo",   "Films & TV",  "filmstv"),
        };
        foreach (var p in saumidPlatforms)
            if (idLow.Contains(p.T))
                return (true, p.P, p.K, $"SaUMID contains \"{p.T}\"");

        bool isBrowser = idLow.Contains("chrome") || idLow.Contains("edge")
                      || idLow.Contains("firefox") || idLow.Contains("brave");
        if (isBrowser)
        {
            var titleLow = (title ?? "").TrimStart().ToLowerInvariant();
            (string Pre, string P, string K)[] titlePlatforms =
            {
                ("amazon prime video", "Prime Video", "primevideo"),
                ("prime video",        "Prime Video", "primevideo"),
                ("amazon video",       "Prime Video", "primevideo"),
                ("netflix",            "Netflix",     "netflix"),
                ("disney+",            "Disney+",     "disneyplus"),
                ("disney plus",        "Disney+",     "disneyplus"),
                ("hulu",               "Hulu",        "hulu"),
                ("hbo max",            "HBO Max",     "hbomax"),
                ("hbo:",               "HBO Max",     "hbomax"),
                ("paramount+",         "Paramount+",  "paramount"),
                ("paramount plus",     "Paramount+",  "paramount"),
                ("peacock",            "Peacock",     "peacock"),
                ("apple tv+",          "Apple TV+",   "appletv"),
                ("apple tv:",          "Apple TV+",   "appletv"),
                ("apple tv ",          "Apple TV+",   "appletv"),
                ("crunchyroll",        "Crunchyroll", "crunchyroll"),
                ("twitch",             "Twitch",      "twitch"),
                ("plex",               "Plex",        "plex"),
                ("jellyfin",           "Jellyfin",    "jellyfin"),
                ("vimeo",              "Vimeo",       "vimeo"),
                ("youtube tv",         "YouTube TV",  "youtubetv"),
                ("films & tv",         "Films & TV",  "filmstv"),
                ("movies & tv",        "Films & TV",  "filmstv"),
                ("max ",               "Max",         "max"),
                ("max:",               "Max",         "max"),
            };
            foreach (var p in titlePlatforms)
                if (titleLow.StartsWith(p.Pre))
                    return (true, p.P, p.K, $"browser title prefix \"{p.Pre}\"");
        }

        if (playbackTypeInt == 2)
            return (true, "Video", "video", "PlaybackType=Video (platform unknown)");

        return (false, "", "", "");
    }

    private static string StripPlatformPrefix(string title, string platform)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(platform)) return title ?? "";
        var t = title.TrimStart();
        if (t.StartsWith(platform, StringComparison.OrdinalIgnoreCase))
        {
            var rest = t.Substring(platform.Length).TrimStart();
            if (rest.Length > 0 && (rest[0] == ':' || rest[0] == '-'))
                rest = rest.Substring(1).TrimStart();
            if (rest.Length > 0) return rest;
        }
        return title;
    }
}
