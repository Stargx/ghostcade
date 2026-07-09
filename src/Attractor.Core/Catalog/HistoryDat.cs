using System.Text;
using System.Xml;

namespace Attractor.Core.Catalog;

/// <summary>One titled slice of a game's history entry — the side panel cycles
/// through these. The lead text before any heading is titled "ABOUT"; the rest
/// follow the file's own "- TRIVIA -" style headings.</summary>
public sealed record AboutSection(string Title, string Body);

/// <summary>
/// Parses a MAME history file for per-game blurbs, in either format:
///   • Classic history.dat (line based):
///       $info=name,clone1,clone2
///       $bio
///       ...text...
///       $end
///   • Modern history.xml (MAME 0.196+):
///       &lt;entry&gt;&lt;systems&gt;&lt;system name="name"/&gt;&lt;/systems&gt;&lt;text&gt;...&lt;/text&gt;&lt;/entry&gt;
/// The format is detected from the file's first non-whitespace character (a '&lt;'
/// means XML), not the extension, so a renamed file still works. Only entries for
/// wanted games are kept; each entry is split on its "- TRIVIA -"-style heading
/// lines into <see cref="AboutSection"/>s, each flattened/trimmed for a side
/// panel. The file can be hundreds of MB, so the XML path streams via XmlReader
/// and never builds a DOM.
/// </summary>
public static class HistoryDat
{
    // Modern history.xml wins over the classic .dat when both sit in one folder.
    private static readonly string[] FileNames = ["history.xml", "history.dat"];

    /// <summary>
    /// First existing history.xml/history.dat across the given directories (in
    /// order); null if none is found. Non-existent directories are skipped.
    /// </summary>
    public static string? FindFile(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir))
                continue;
            foreach (var file in FileNames)
            {
                var path = Path.Combine(dir, file);
                if (File.Exists(path))
                    return path;
            }
        }
        return null;
    }

    public static async Task<Dictionary<string, IReadOnlyList<AboutSection>>> LoadAsync(
        string path, IReadOnlySet<string> wanted, int maxChars = 520, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return new Dictionary<string, IReadOnlyList<AboutSection>>(StringComparer.Ordinal);

        return await IsXmlAsync(path, ct).ConfigureAwait(false)
            ? await LoadXmlAsync(path, wanted, maxChars, ct).ConfigureAwait(false)
            : await LoadDatAsync(path, wanted, maxChars, ct).ConfigureAwait(false);
    }

    private static async Task<bool> IsXmlAsync(string path, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        var head = new char[512];
        int n = await reader.ReadAsync(head, ct).ConfigureAwait(false);
        for (int i = 0; i < n; i++)
        {
            if (!char.IsWhiteSpace(head[i]))
                return head[i] == '<';
        }
        return false; // empty / whitespace-only
    }

    private static async Task<Dictionary<string, IReadOnlyList<AboutSection>>> LoadDatAsync(
        string path, IReadOnlySet<string> wanted, int maxChars, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<AboutSection>>(StringComparer.Ordinal);
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
                Emit(result, names, bio, maxChars);
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

    private static async Task<Dictionary<string, IReadOnlyList<AboutSection>>> LoadXmlAsync(
        string path, IReadOnlySet<string> wanted, int maxChars, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<AboutSection>>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Ignore,
        };
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        using var reader = XmlReader.Create(stream, settings);

        // wanted system shortnames for the entry currently being read; <systems>
        // always precedes <text>, so this is populated by the time text is reached.
        var matched = new List<string>();
        bool advance = true;
        while (!advance || await reader.ReadAsync().ConfigureAwait(false))
        {
            advance = true;
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            switch (reader.Name)
            {
                case "entry":
                    matched.Clear();
                    break;
                case "system": // <software><item> entries are intentionally ignored
                    if (reader.GetAttribute("name") is { } name && wanted.Contains(name))
                        matched.Add(name);
                    break;
                case "text":
                    if (matched.Count == 0)
                        break; // skip the (possibly large) body of an unwanted entry
                    var sections = ToSections(
                        await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), maxChars);
                    if (sections.Count > 0)
                        foreach (var n in matched)
                            result.TryAdd(n, sections);
                    // ReadElementContentAsString already advanced onto the node after
                    // </text>; consuming it again here would skip the next entry.
                    advance = false;
                    break;
            }
        }
        return result;
    }

    private static void Emit(
        Dictionary<string, IReadOnlyList<AboutSection>> result, List<string>? names, StringBuilder? bio, int maxChars)
    {
        if (bio is null || names is null)
            return;
        var sections = ToSections(bio.ToString(), maxChars);
        if (sections.Count > 0)
            foreach (var name in names)
                result.TryAdd(name, sections);
    }

    /// <summary>Split an entry on its "- TRIVIA -" heading lines; the lead block gets
    /// the title "ABOUT". Each body is flattened/truncated for the panel. "CONTRIBUTE"
    /// is dropped — it's the same edit-this-entry boilerplate in every entry.</summary>
    private static IReadOnlyList<AboutSection> ToSections(string raw, int maxChars)
    {
        var sections = new List<AboutSection>();
        var title = "ABOUT";
        var body = new StringBuilder();
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            if (TryParseHeading(line, out var heading))
            {
                Flush();
                title = heading;
            }
            else
            {
                body.AppendLine(line);
            }
        }
        Flush();
        return sections;

        void Flush()
        {
            var text = Clean(body.ToString(), maxChars);
            body.Clear();
            if (text.Length > 0 && !title.Equals("CONTRIBUTE", StringComparison.OrdinalIgnoreCase))
                sections.Add(new AboutSection(title, text));
        }
    }

    /// <summary>A heading is a whole line of the form "- TRIVIA -" (uppercase, by the
    /// file's convention — which also keeps prose like "- yes -" from splitting a bio).</summary>
    private static bool TryParseHeading(string line, out string heading)
    {
        heading = "";
        var t = line.Trim();
        if (t.Length is < 5 or > 48
            || !t.StartsWith("- ", StringComparison.Ordinal) || !t.EndsWith(" -", StringComparison.Ordinal))
            return false;
        var inner = t[2..^2].Trim();
        if (inner.Length == 0 || !inner.Any(char.IsLetter) || inner.Any(char.IsLower))
            return false;
        heading = inner;
        return true;
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
