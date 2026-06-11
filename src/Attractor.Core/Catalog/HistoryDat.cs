using System.Text;

namespace Attractor.Core.Catalog;

/// <summary>
/// Parses MAME's classic history.dat for per-game blurbs. Format:
///   $info=name,clone1,clone2
///   $bio
///   ...text...
///   $end
/// Only entries for wanted games are kept; blurbs are trimmed for a side panel.
/// </summary>
public static class HistoryDat
{
    public static async Task<Dictionary<string, string>> LoadAsync(
        string path, IReadOnlySet<string> wanted, int maxChars = 520, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;

        using var reader = new StreamReader(path);
        List<string>? names = null;
        StringBuilder? bio = null;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("$info=", StringComparison.Ordinal))
            {
                names = line[6..]
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(wanted.Contains)
                    .ToList();
                bio = null;
            }
            else if (line.StartsWith("$bio", StringComparison.Ordinal))
            {
                if (names is { Count: > 0 })
                    bio = new StringBuilder();
            }
            else if (line.StartsWith("$end", StringComparison.Ordinal))
            {
                if (bio is not null && names is not null)
                {
                    var text = Clean(bio.ToString(), maxChars);
                    if (text.Length > 0)
                        foreach (var name in names)
                            result.TryAdd(name, text);
                }
                names = null;
                bio = null;
            }
            else
            {
                bio?.AppendLine(line);
            }
        }
        return result;
    }

    private static string Clean(string raw, int maxChars)
    {
        var text = raw.Replace("\r\n", "\n").Trim('\n', ' ');
        // collapse runs of blank lines, then flatten: side panel wants prose
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        bool lastBlank = false;
        foreach (var line in lines)
        {
            bool blank = string.IsNullOrWhiteSpace(line);
            if (blank && (lastBlank || sb.Length == 0))
                continue;
            sb.Append(blank ? "\n\n" : (sb.Length > 0 && !lastBlank ? " " : "")).Append(blank ? "" : line.Trim());
            lastBlank = blank;
        }
        var flat = sb.ToString().Trim();
        if (flat.Length <= maxChars)
            return flat;
        int cut = flat.LastIndexOf(' ', maxChars);
        return flat[..(cut > maxChars - 80 ? cut : maxChars)] + " …";
    }
}
