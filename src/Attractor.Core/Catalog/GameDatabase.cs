namespace Attractor.Core.Catalog;

/// <summary>
/// The in-memory game catalog: verified, runnable, non-BIOS, non-device,
/// non-preliminary machines, joined with verify results and tag stores.
/// </summary>
public sealed class GameDatabase
{
    private readonly Dictionary<string, GameEntry> _byName;

    public GameDatabase(IEnumerable<GameEntry> entries, ITagStore banned, ITagStore favorites)
    {
        _byName = entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
        Banned = banned;
        Favorites = favorites;
    }

    public IReadOnlyCollection<GameEntry> All => _byName.Values;
    public ITagStore Banned { get; }
    public ITagStore Favorites { get; }

    /// <summary>Year/manufacturer filter applied live to the rotation pool; default
    /// none. Mutable so the UI can re-point the rotation without rebuilding.</summary>
    public GameFilter Filter { get; set; } = GameFilter.None;

    public GameEntry? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Names eligible for rotation right now (bans and the filter apply live).</summary>
    public IReadOnlyList<string> RotationPool() =>
        _byName.Values.Where(e => !Banned.Contains(e.Name) && Filter.Matches(e)).Select(e => e.Name).ToArray();

    /// <summary>True if the game passes the active filter (banning is handled separately).</summary>
    public bool MatchesFilter(string name) => Find(name) is { } e && Filter.Matches(e);

    public static GameDatabase Assemble(
        IEnumerable<MachineInfo> machines,
        IReadOnlyDictionary<string, VerifyResult> verified,
        ITagStore banned,
        ITagStore favorites)
    {
        var entries =
            from m in machines
            where m.Runnable && !m.IsBios && !m.IsDevice
            where m.Driver != DriverStatus.Preliminary
            where verified.ContainsKey(m.Name)
            select new GameEntry(
                m.Name,
                m.Description ?? m.Name,
                m.Year ?? "????",
                m.Manufacturer ?? "unknown",
                m.Driver,
                verified[m.Name],
                m.Rotate);
        return new GameDatabase(entries, banned, favorites);
    }
}
