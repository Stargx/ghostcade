using System.Globalization;

namespace Attractor.Core.Mame;

/// <summary>One MAME game session — a dwell chunk, or (with <paramref name="PlayMode"/>)
/// a real hands-on play session: launched activated (taking focus is the point),
/// no time cap (pass 0 seconds), and with the mouse left available for
/// trackball/spinner games. Play sessions live outside the &lt;300s rule — a human
/// is at the keyboard, so MAME's "press a key" warnings aren't fatal there.</summary>
public sealed record MameLaunchSpec(
    string MameExePath,
    string GameName,
    int SecondsToRun,
    IReadOnlyList<string>? ExtraArgs = null,
    bool Windowed = true,
    MameTimingMode TimingMode = MameTimingMode.SecondsToRun,
    int RefreshHz = 60,
    bool PlayMode = false)
{
    public string WorkingDirectory => Path.GetDirectoryName(MameExePath)!;

    public IReadOnlyList<string> BuildArgumentList()
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(GameName))
            args.Add(GameName);
        args.Add("-skip_gameinfo");
        // Legacy MAME blocks on a copyright/disclaimer screen with no
        // emulated-time auto-dismiss; -skip_disclaimer clears it. Modern MAME
        // has no such option (passing it is a fatal "unknown option") and
        // instead auto-suppresses startup screens while the run stays < 300s.
        if (TimingMode == MameTimingMode.FramesToRun)
            args.Add("-skip_disclaimer");
        if (!PlayMode)
            args.Add("-nomouse");
        if (Windowed)
        {
            args.Add("-window");
            args.Add("-nomaximize");
        }
        if (SecondsToRun > 0)
        {
            // Keeping each chunk under 300 emulated seconds makes MAME suppress
            // its startup screens, including the blocking "doesn't work
            // properly" warning. Under FramesToRun that invariant maps to
            // < 300 × refresh frames.
            if (TimingMode == MameTimingMode.SecondsToRun)
            {
                args.Add("-seconds_to_run");
                args.Add(SecondsToRun.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                long frames = checked((long)SecondsToRun * RefreshHz);
                args.Add("-frames_to_run");
                args.Add(frames.ToString(CultureInfo.InvariantCulture));
            }
        }
        if (ExtraArgs is { Count: > 0 })
            args.AddRange(ExtraArgs);
        return args;
    }
}
