namespace Attractor.Core.Art;

/// <summary>
/// Resolves marquee/snap art paths for a game shortname. Directories may be
/// relative (resolved against the MAME directory — the usual convention) or
/// absolute. Decoding is the host's job; Core only finds files.
/// </summary>
public sealed class ArtLocator
{
    private static readonly string[] Extensions = [".png", ".jpg"];
    private readonly string[] _marqueeDirs;
    private readonly string[] _snapDirs;

    public ArtLocator(string mameDir, IEnumerable<string> marqueeDirs, IEnumerable<string> snapDirs)
    {
        _marqueeDirs = Resolve(mameDir, marqueeDirs);
        _snapDirs = Resolve(mameDir, snapDirs);
    }

    private static string[] Resolve(string mameDir, IEnumerable<string> dirs) =>
        dirs.Select(d => Path.IsPathRooted(d) ? d : Path.Combine(mameDir, d)).ToArray();

    /// <summary>Find a game's marquee, falling back to its <paramref name="parent"/> (cloneof)
    /// when the clone/bootleg has none of its own.</summary>
    public string? FindMarquee(string game, string? parent = null) => Find(_marqueeDirs, game, parent);

    /// <summary>Find a game's snap, falling back to its <paramref name="parent"/> (cloneof)
    /// when the clone has none of its own (a clone runs the same screen as its parent).</summary>
    public string? FindSnap(string game, string? parent = null) => Find(_snapDirs, game, parent);

    private static string? Find(string[] dirs, string game, string? parent) =>
        FindOne(dirs, game) ?? (parent is { } p ? FindOne(dirs, p) : null);

    private static string? FindOne(string[] dirs, string game)
    {
        foreach (var dir in dirs)
            foreach (var ext in Extensions)
            {
                var path = Path.Combine(dir, game + ext);
                if (File.Exists(path))
                    return path;
            }
        return null;
    }
}
