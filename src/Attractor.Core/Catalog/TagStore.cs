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

    public FileTagStore(string path)
    {
        _path = path;
        if (File.Exists(path))
            foreach (var line in File.ReadAllLines(path))
                if (!string.IsNullOrWhiteSpace(line))
                    _names.Add(line.Trim());
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

    private void Save() => AtomicFile.WriteAllLines(_path, _names.Order(StringComparer.Ordinal));
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
