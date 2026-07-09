namespace Attractor.Core.Catalog;

/// <summary>
/// Parses catver.ini — the fan-maintained category file (progettosnaps.net) that
/// MAME front-ends use for genres; MAME itself ships no genre data. Format is an
/// INI whose <c>[Category]</c> section holds <c>shortname=Genre / Subgenre</c>
/// lines (sometimes suffixed <c>* Mature *</c>). Every '/'-separated segment is kept
/// as an independent, filterable tag (so <c>Fighter / 2.5D</c> yields both "Fighter"
/// and "2.5D") — the game then matches on any of them. Attractor never ships this
/// file; the user supplies it and everything here degrades to "no genre data"
/// without it.
/// </summary>
public static class CatverIni
{
    /// <summary>Where catver.ini conventionally lives relative to a MAME install:
    /// next to mame.exe, or in the extras' "folders" subdirectory.</summary>
    public static string? FindFile(string mameDir) =>
        new[] { Path.Combine(mameDir, "catver.ini"), Path.Combine(mameDir, "folders", "catver.ini") }
            .FirstOrDefault(File.Exists);

    /// <summary>shortname → its genre tags (top-level genre first, then each subgenre).
    /// Missing file → empty map. Lines outside the <c>[Category]</c> section are ignored
    /// (catver.ini also carries a <c>[VerAdded]</c> section), but a headerless file of
    /// bare <c>name=category</c> lines is accepted too — some hand-trimmed copies
    /// circulate that way.</summary>
    public static async Task<Dictionary<string, IReadOnlyList<string>>> LoadAsync(string path, CancellationToken ct = default)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;

        using var reader = new StreamReader(path);
        bool inCategory = true; // headerless files: treat the lead-in as [Category]
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
                continue;
            if (line[0] == '[')
            {
                inCategory = line.Equals("[Category]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inCategory)
                continue;
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = line[..eq].Trim();
            if (ExtractGenres(line[(eq + 1)..]) is { } genres)
                result[name] = genres;
        }
        return result;
    }

    /// <summary>Splits a category chain into its individual tags, top-level first:
    /// <c>"Fighter / 2.5D"</c> → <c>["Fighter", "2.5D"]</c>, and
    /// <c>"Puzzle / Toss * Mature *"</c> → <c>["Puzzle", "Toss"]</c> (the '*'-delimited
    /// content-rating marker is dropped). Blank segments are skipped and repeats within
    /// one line are collapsed case-insensitively. Null when nothing usable remains.</summary>
    private static IReadOnlyList<string>? ExtractGenres(string category)
    {
        List<string>? tags = null;
        foreach (var part in category.Split('/'))
        {
            var seg = part;
            int star = seg.IndexOf('*'); // strip "* Mature *" and any other marker
            if (star >= 0)
                seg = seg[..star];
            seg = seg.Trim();
            if (seg.Length == 0)
                continue;
            tags ??= [];
            if (!tags.Contains(seg, StringComparer.OrdinalIgnoreCase))
                tags.Add(seg);
        }
        return tags;
    }
}
