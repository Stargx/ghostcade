namespace Attractor.Core.Mame;

/// <summary>
/// Discovers where MAME keeps its ROMs. Attractor otherwise leaves ROM location
/// entirely to MAME, but the favorites list records each game's folder so it can
/// be played later, which needs the effective rompath: an explicit -rompath in the
/// launch args wins, else MAME's own configuration via -showconfig, else "roms".
/// Relative entries resolve against the MAME directory.
/// </summary>
public static class MameConfig
{
    public static async Task<IReadOnlyList<string>> GetRomPathsAsync(
        string mameExePath, IReadOnlyList<string>? extraArgs, CancellationToken ct = default)
    {
        var raw = ExtraArgsValue(extraArgs, "-rompath");
        if (raw is null)
        {
            string? found = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15)); // -showconfig is quick; cap a hung exe
            await MameVerbRunner.RunLinesAsync(mameExePath, ["-showconfig"],
                line => found ??= ParseRompathLine(line), timeout.Token).ConfigureAwait(false);
            raw = found;
        }

        var mameDir = Path.GetDirectoryName(Path.GetFullPath(mameExePath))!;
        return SplitPaths(raw ?? "roms", mameDir);
    }

    /// <summary>"rompath   roms;D:\more" → "roms;D:\more"; null for any other line.</summary>
    public static string? ParseRompathLine(string line)
    {
        var t = line.TrimStart();
        if (!t.StartsWith("rompath", StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = t["rompath".Length..];
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[0]))
            return null; // a different key that merely starts with "rompath"
        rest = rest.Trim();
        return rest.Length == 0 ? null : rest;
    }

    /// <summary>Split a ';'-separated rompath into absolute directories.</summary>
    public static IReadOnlyList<string> SplitPaths(string raw, string baseDir) =>
        raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
           .Select(p => p.Trim('"'))
           .Where(p => p.Length > 0)
           .Select(p => Path.GetFullPath(p, baseDir))
           .ToArray();

    /// <summary>The rompath directory that actually holds this set — its own .zip/.7z or
    /// unpacked folder, or (for a merged-set clone with no file of its own) the parent
    /// set's. With a single rompath there's nothing to disambiguate, so it's returned
    /// without touching the disk; otherwise the first rompath is the best-guess fallback.</summary>
    public static string ResolveRomFolder(IReadOnlyList<string> romDirs, string shortname, string? parent = null)
    {
        if (romDirs.Count <= 1)
            return romDirs.Count == 1 ? romDirs[0] : "";

        foreach (var name in parent is null ? [shortname] : new[] { shortname, parent })
            foreach (var dir in romDirs)
                if (File.Exists(Path.Combine(dir, name + ".zip"))
                    || File.Exists(Path.Combine(dir, name + ".7z"))
                    || Directory.Exists(Path.Combine(dir, name)))
                    return dir;
        return romDirs[0];
    }

    // MAME applies the LAST occurrence of a repeated option, so scan from the end.
    private static string? ExtraArgsValue(IReadOnlyList<string>? args, string flag)
    {
        if (args is null)
            return null;
        for (int i = args.Count - 2; i >= 0; i--)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
