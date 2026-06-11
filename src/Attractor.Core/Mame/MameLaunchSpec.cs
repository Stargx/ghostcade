using System.Globalization;

namespace Attractor.Core.Mame;

/// <summary>One MAME game session (a single dwell chunk).</summary>
public sealed record MameLaunchSpec(
    string MameExePath,
    string GameName,
    int SecondsToRun,
    IReadOnlyList<string>? ExtraArgs = null,
    bool Windowed = true)
{
    public string WorkingDirectory => Path.GetDirectoryName(MameExePath)!;

    public IReadOnlyList<string> BuildArgumentList()
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(GameName))
            args.Add(GameName);
        args.Add("-skip_gameinfo");
        args.Add("-nomouse");
        if (Windowed)
        {
            args.Add("-window");
            args.Add("-nomaximize");
        }
        if (SecondsToRun > 0)
        {
            // Staying under 300 emulated seconds makes every MAME version
            // (0.147 → current) suppress all startup screens, including the
            // blocking "this game doesn't work properly" warning.
            args.Add("-seconds_to_run");
            args.Add(SecondsToRun.ToString(CultureInfo.InvariantCulture));
        }
        if (ExtraArgs is { Count: > 0 })
            args.AddRange(ExtraArgs);
        return args;
    }
}
