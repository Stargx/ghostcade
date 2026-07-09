using System.Text.RegularExpressions;

namespace Attractor.Core.Mame;

/// <summary>Which command-line dialect a MAME build speaks for timed runs.</summary>
public enum MameTimingMode
{
    /// <summary>Modern MAME (0.147+): <c>-seconds_to_run</c>.</summary>
    SecondsToRun,

    /// <summary>
    /// Legacy MAME (pre-0.147): <c>-frames_to_run</c> (seconds × refresh), and it
    /// also needs <c>-skip_disclaimer</c> — old MAME blocks on a copyright screen
    /// that modern MAME auto-suppresses (and which modern MAME has no option for).
    /// </summary>
    FramesToRun,
}

/// <summary>
/// What Attractor needs to know about a specific MAME build to drive it: its
/// version and which launch dialect it speaks. Detected once from the
/// <c>-help</c> banner at setup and persisted into config, so normal launches
/// never re-probe. All version-conditional behaviour lives here — there are no
/// scattered version checks elsewhere.
/// </summary>
public sealed partial record MameCapabilities(
    int? VersionMinor,
    MameTimingMode TimingMode,
    bool Supported,
    int RefreshHz = 60,
    string? RawBanner = null)
{
    /// <summary>Oldest MAME we support — we advertise and drive 0.147+ (equal to
    /// <see cref="SecondsToRunFloorMinor"/>). The frames-mode path below still parses older
    /// builds but gates them out: pre-0.147 used to work and has since regressed, so don't
    /// just lower this — the frames-mode launch/embed path needs fixing first.</summary>
    public const int MinSupportedMinor = 147;

    /// <summary>At/above this minor <c>-seconds_to_run</c> exists; below it we use <c>-frames_to_run</c>.</summary>
    public const int SecondsToRunFloorMinor = 147;

    // Matches the version token in banners like "M.A.M.E. v0.78 (Dec 24 2003)"
    // and "MAME 0.220 (mame0220)". Two-or-three digit minor covers every real
    // build (78, 147, 220, …); the leading "0." never appears elsewhere in a banner.
    [GeneratedRegex(@"v?0\.(\d{2,3})", RegexOptions.Compiled)]
    private static partial Regex VersionToken();

    /// <summary>Human-friendly version for the UI ("0.220", or "unknown").</summary>
    public string VersionLabel => VersionMinor is { } v ? $"0.{v}" : "unknown";

    /// <summary>
    /// Parse capabilities from a MAME <c>-help</c> banner. An unparseable banner
    /// defaults to the modern dialect and Supported=true, so a current MAME with
    /// an unusual banner still runs rather than being wrongly gated out.
    /// </summary>
    public static MameCapabilities Parse(string? banner)
    {
        var m = banner is null ? Match.Empty : VersionToken().Match(banner);
        if (!m.Success)
            return new MameCapabilities(null, MameTimingMode.SecondsToRun, Supported: true, RawBanner: banner);

        int minor = int.Parse(m.Groups[1].Value);
        var mode = minor < SecondsToRunFloorMinor ? MameTimingMode.FramesToRun : MameTimingMode.SecondsToRun;
        return new MameCapabilities(minor, mode, Supported: minor >= MinSupportedMinor, RawBanner: banner);
    }

    /// <summary>
    /// Spawn <c>mame -help</c> and parse the first banner line that carries a
    /// version. Reuses the same verb seam that catalog/verify run on, which is
    /// already proven across 0.147 → current.
    /// </summary>
    public static async Task<MameCapabilities> DetectAsync(string mameExePath, CancellationToken ct = default)
    {
        string? versionLine = null;
        await MameVerbRunner.RunLinesAsync(mameExePath, ["-help"], line =>
        {
            if (versionLine is null && VersionToken().IsMatch(line))
                versionLine = line.Trim();
        }, ct).ConfigureAwait(false);
        return Parse(versionLine);
    }
}
