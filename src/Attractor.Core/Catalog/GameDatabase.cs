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

    public GameEntry? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Names eligible for rotation right now (bans apply live).</summary>
    public IReadOnlyList<string> RotationPool() =>
        _byName.Values.Where(e => !Banned.Contains(e.Name)).Select(e => e.Name).ToArray();

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
