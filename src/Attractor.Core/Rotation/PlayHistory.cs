namespace Attractor.Core.Rotation;

/// <summary>
/// Browser-style history for Previous/Skip. The cursor normally sits at the
/// end; Previous walks back, a forward move replays history until the cursor
/// re-reaches the end, after which fresh draws append.
/// </summary>
public sealed class PlayHistory
{
    private const int Capacity = 50;
    private readonly List<string> _entries = [];
    private int _cursor = -1; // index of the current game

    public string? Current => _cursor >= 0 && _cursor < _entries.Count ? _entries[_cursor] : null;
    public bool AtEnd => _cursor >= _entries.Count - 1;

    /// <summary>Record a freshly drawn game as the new current entry.</summary>
    public void Append(string game)
    {
        _entries.Add(game);
        if (_entries.Count > Capacity)
            _entries.RemoveAt(0);
        _cursor = _entries.Count - 1;
    }

    /// <summary>Step back, skipping entries the caller now excludes (e.g. banned meanwhile).</summary>
    public string? TryBack(Func<string, bool>? excluded = null)
    {
        for (int i = _cursor - 1; i >= 0; i--)
        {
            if (excluded is null || !excluded(_entries[i]))
            {
                _cursor = i;
                return _entries[i];
            }
        }
        return null;
    }

    /// <summary>Step forward within history; null when the cursor is at the end (draw fresh instead).</summary>
    public string? TryForward(Func<string, bool>? excluded = null)
    {
        for (int i = _cursor + 1; i < _entries.Count; i++)
        {
            if (excluded is null || !excluded(_entries[i]))
            {
                _cursor = i;
                return _entries[i];
            }
        }
        return null;
    }
}
