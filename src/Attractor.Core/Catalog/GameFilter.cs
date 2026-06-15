namespace Attractor.Core.Catalog;

/// <summary>
/// A rotation filter on year (grouped by decade) and manufacturer. An empty
/// category means "no constraint"; within a category any match qualifies; the
/// two categories combine with AND. Decades are start years (1980 = the 1980s).
/// Immutable — toggling a selection produces a new filter.
/// </summary>
public sealed record GameFilter
{
    public IReadOnlySet<int> Decades { get; }
    public IReadOnlySet<string> Manufacturers { get; }

    public GameFilter(IEnumerable<int> decades, IEnumerable<string> manufacturers)
    {
        Decades = decades.ToHashSet();
        Manufacturers = manufacturers.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static readonly GameFilter None = new([], []);

    public bool IsActive => Decades.Count > 0 || Manufacturers.Count > 0;

    public bool Matches(GameEntry e) =>
        (Decades.Count == 0 || (e.DecadeStart() is { } d && Decades.Contains(d)))
        && (Manufacturers.Count == 0 || Manufacturers.Contains(e.Manufacturer));
}
