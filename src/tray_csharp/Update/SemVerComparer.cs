// Stage 7.2: SemVerComparer. Closes R6 (pre-release detection bug from
// V14_S7_REPLAN_RISKS.md) with EXPLICIT regex rejection and 11 synthetic
// test cases. Replaces PS S12's accidental [version]-cast-throws-on-rc
// rejection (tray.ps1:5745+catch at 5783) with a documented, tested path.
//
// Project policy: any pre-release version (rc / beta / alpha) is REJECTED
// by the auto-updater. Stable releases only. No opt-in flag (Q3=A locked).

using System.Text.RegularExpressions;

namespace MastersFM.Tray.Update;

public static class SemVerComparer
{
    // Pre-release suffix regex. Matches:
    //   -rc, -RC, -rc.1, -RC.99, -beta, -beta.X, -alpha, -alpha.NN
    // Does NOT match build metadata (+build.1) -- per SemVer spec, build
    // metadata is NOT pre-release; it's an annotation only.
    private static readonly Regex _preReleaseRegex = new(
        @"-(rc|beta|alpha)\.?\d*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // SemVer parse regex: MAJOR.MINOR.PATCH with optional -prerelease and +build.
    private static readonly Regex _semVerRegex = new(
        @"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns true if the version string contains a pre-release suffix
    /// matching -(rc|beta|alpha)\.?\d* case-insensitive. Build metadata
    /// (+build.X) does NOT count as pre-release.
    /// </summary>
    public static bool IsPreRelease(string? version)
    {
        if (string.IsNullOrEmpty(version)) return false;
        return _preReleaseRegex.IsMatch(version);
    }

    /// <summary>
    /// Compares two SemVer-like version strings. Returns negative if a < b,
    /// zero if equal (or both pre-release of same version), positive if a > b.
    ///
    /// Project policy: any pre-release is considered LESS THAN any stable
    /// release of the same major.minor.patch. Two pre-releases of the same
    /// version are treated as EQUAL (we never auto-update RC to RC; brief
    /// Q3=A locked).
    ///
    /// Returns int.MinValue if either string is unparseable (signal: caller
    /// should treat as "no comparison possible" and skip the update).
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return int.MinValue;

        var ma = _semVerRegex.Match(a);
        var mb = _semVerRegex.Match(b);
        if (!ma.Success || !mb.Success) return int.MinValue;

        var (majA, minA, patA) = (int.Parse(ma.Groups[1].Value), int.Parse(ma.Groups[2].Value), int.Parse(ma.Groups[3].Value));
        var (majB, minB, patB) = (int.Parse(mb.Groups[1].Value), int.Parse(mb.Groups[2].Value), int.Parse(mb.Groups[3].Value));

        // Project policy: any pre-release is LESS than any stable, regardless
        // of major.minor.patch. So 14.0.1-beta < 14.0.0 (the stable) even though
        // 14.0.1 has a higher patch. Two pre-releases of any versions are
        // treated as EQUAL (we never auto-update RC->RC; brief Q3=A locked).
        // This rule fires BEFORE the numeric comparison.
        var aIsPre = IsPreRelease(a);
        var bIsPre = IsPreRelease(b);
        if (aIsPre && !bIsPre) return -1;  // any pre-release < any stable
        if (!aIsPre && bIsPre) return 1;
        if (aIsPre && bIsPre) return 0;    // two pre-releases: equivalent

        // Both stable: standard SemVer numeric comparison.
        if (majA != majB) return majA.CompareTo(majB);
        if (minA != minB) return minA.CompareTo(minB);
        if (patA != patB) return patA.CompareTo(patB);
        return 0;
    }

    /// <summary>
    /// Runs the 11-case synthetic test suite that closes R6. Logs results to
    /// the provided logger via [Update] [TEST] component channel. Returns
    /// number of failures (0 on full PASS).
    /// </summary>
    public static int RunSelfTest(Action<string, string> logFn)
    {
        var cases = new (string Description, bool Expected, bool Actual)[]
        {
            ("Compare(14.0.0, 14.0.0-rc.1) > 0",          true,  Compare("14.0.0", "14.0.0-rc.1") > 0),
            ("Compare(14.0.0-rc.1, 14.0.0) < 0",          true,  Compare("14.0.0-rc.1", "14.0.0") < 0),
            ("Compare(14.0.0, 14.0.1) < 0",               true,  Compare("14.0.0", "14.0.1") < 0),
            ("Compare(14.0.0-rc.1, 14.0.0-rc.2) == 0",    true,  Compare("14.0.0-rc.1", "14.0.0-rc.2") == 0),
            ("Compare(14.0.1-beta.5, 14.0.0) < 0",        true,  Compare("14.0.1-beta.5", "14.0.0") < 0),
            ("IsPreRelease(14.0.0) == false",             true,  IsPreRelease("14.0.0") == false),
            ("IsPreRelease(14.0.0-rc.1) == true",         true,  IsPreRelease("14.0.0-rc.1") == true),
            ("IsPreRelease(14.0.0-RC.1) == true",         true,  IsPreRelease("14.0.0-RC.1") == true),
            ("IsPreRelease(14.0.0-beta) == true",         true,  IsPreRelease("14.0.0-beta") == true),
            ("IsPreRelease(14.0.0-alpha.99) == true",     true,  IsPreRelease("14.0.0-alpha.99") == true),
            ("IsPreRelease(14.0.0+build.1) == false",     true,  IsPreRelease("14.0.0+build.1") == false),
        };

        int failures = 0;
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var verdict = (c.Expected == c.Actual) ? "PASS" : "FAIL";
            if (c.Expected != c.Actual) failures++;
            logFn("TEST", $"case={i + 1}/{cases.Length} expected={c.Expected} actual={c.Actual} {verdict}  -- {c.Description}");
        }
        if (failures == 0)
        {
            logFn("TEST", $"R6 closure: ALL {cases.Length} pre-release regex synthetic test cases PASS");
        }
        else
        {
            logFn("TEST", $"R6 closure FAILURE: {failures}/{cases.Length} cases failed; HALT recommended");
        }
        return failures;
    }
}
