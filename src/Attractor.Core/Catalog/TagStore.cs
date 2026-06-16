using Attractor.Core.Configuration;

namespace Attractor.Core.Catalog;

/// <summary>A named set of game shortnames (banned, favorites).</summary>
public interface ITagStore
{
    bool Contains(string name);
    bool Add(string name);
    bool Remove(string name);
    bool Toggle(string name);
    IReadOnlyCollection<string> All { get; }
}

/// <summary>
/// One shortname per line in a plain text file, hand-editable while the app
/// is closed. Every mutation rewrites the file atomically.
/// </summary>
public sealed class FileTagStore : ITagStore
{
    private readonly string _path;
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private Func<IReadOnlyList<string>, IEnumerable<string>>? _formatter;

    public FileTagStore(string path)
    {
        _path = path;
        if (File.Exists(path))
            foreach (var line in File.ReadAllLines(path))
            {
                var name = KeyOf(line);
                if (name.Length > 0)
                    _names.Add(name);
            }
    }

    // The tag is the first whitespace-delimited token, so a line may carry extra
    // space- or tab-separated columns (favorites append title + rom folder) without
    // confusing the set. A bare single-token line — every banned.txt line, and legacy
    // favorites — is the tag itself.
    private static string KeyOf(string line)
    {
        var t = line.TrimStart();
        int i = 0;
        while (i < t.Length && !char.IsWhiteSpace(t[i]))
            i++;
        return t[..i];
    }

    /// <summary>
    /// Optional formatter turning the sorted tag set into the lines written to disk —
    /// favorites use it to render aligned `shortname  title  rom folder` columns
    /// (it needs the whole set to size each column). Default (null) writes bare tags,
    /// so banned.txt stays plain. Assigning it rewrites the file when the result
    /// differs, so existing entries pick up the columns; it won't create an empty file.
    /// </summary>
    public Func<IReadOnlyList<string>, IEnumerable<string>>? Formatter
    {
        get { lock (_lock) return _formatter; }
        set
        {
            lock (_lock)
            {
                _formatter = value;
                // Only rewrite when it would actually change the file: don't create an
                // empty file for someone who never favourited anything, and don't churn
                // (or clobber a hand-edited order) on every launch when nothing differs.
                if (_names.Count == 0 && !File.Exists(_path))
                    return;
                var desired = Render();
                if (!File.Exists(_path) || !File.ReadAllLines(_path).SequenceEqual(desired, StringComparer.Ordinal))
                    Save();
            }
        }
    }

    public bool Contains(string name)
    {
        lock (_lock) return _names.Contains(name);
    }

    public bool Add(string name)
    {
        lock (_lock)
        {
            if (!_names.Add(name)) return false;
            Save();
            return true;
        }
    }

    public bool Remove(string name)
    {
        lock (_lock)
        {
            if (!_names.Remove(name)) return false;
            Save();
            return true;
        }
    }

    public bool Toggle(string name)
    {
        lock (_lock)
        {
            bool nowSet = _names.Add(name);
            if (!nowSet) _names.Remove(name);
            Save();
            return nowSet;
        }
    }

    public IReadOnlyCollection<string> All
    {
        get { lock (_lock) return _names.ToArray(); }
    }

    private void Save() => AtomicFile.WriteAllLines(_path, Render());

    private string[] Render()
    {
        var tags = _names.Order(StringComparer.Ordinal).ToArray();
        return (_formatter?.Invoke(tags) ?? tags).ToArray();
    }
}

/// <summary>Test/ephemeral implementation.</summary>
public sealed class InMemoryTagStore : ITagStore
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    public bool Contains(string name) => _names.Contains(name);
    public bool Add(string name) => _names.Add(name);
    public bool Remove(string name) => _names.Remove(name);
    public bool Toggle(string name) { if (_names.Add(name)) return true; _names.Remove(name); return false; }
    public IReadOnlyCollection<string> All => _names;
}
