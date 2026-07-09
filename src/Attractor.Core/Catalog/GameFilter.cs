namespace Attractor.Core.Catalog;

/// <summary>
/// A rotation filter on year (grouped by decade), manufacturer, genre (from a
/// user-supplied catver.ini), and a "favourites only" switch. An empty category
/// means "no constraint"; within a category any match qualifies; the categories
/// combine with AND. Decades are start years (1980 = the 1980s). Immutable —
/// toggling a selection produces a new filter.
/// </summary>
public sealed record GameFilter
{
    public IReadOnlySet<int> Decades { get; }
    public IReadOnlySet<string> Manufacturers { get; }

    /// <summary>Genre tags to include (catver.ini categories and subcategories, e.g.
    /// "Shooter" or "2.5D"); empty = all. A game matches when any of its own tags is
    /// listed here. A game with no genre (no catver.ini, or absent from it) only
    /// matches while this constraint is empty.</summary>
    public IReadOnlySet<string> Genres { get; }

    /// <summary>When true the rotation is restricted to favourited games — combined
    /// (AND) with the decade/manufacturer constraints. Favourite membership lives in a
    /// tag store, not on <see cref="GameEntry"/>, so <see cref="Matches"/> can't see it:
    /// the actual gate is applied by <see cref="GameDatabase"/> (which owns the set).</summary>
    public bool FavoritesOnly { get; }

    public GameFilter(
        IEnumerable<int> decades,
        IEnumerable<string> manufacturers,
        IEnumerable<string>? genres = null,
        bool favoritesOnly = false)
    {
        Decades = decades.ToHashSet();
        Manufacturers = manufacturers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Genres = (genres ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FavoritesOnly = favoritesOnly;
    }

    public static readonly GameFilter None = new([], []);

    public bool IsActive => Decades.Count > 0 || Manufacturers.Count > 0 || Genres.Count > 0 || FavoritesOnly;

    /// <summary>Decade/manufacturer/genre match only. The "favourites only" gate is
    /// applied separately by <see cref="GameDatabase"/>, which holds the favourites set —
    /// so a non-favourite can still pass this for a favourites-only filter; query the pool
    /// through <see cref="GameDatabase.RotationPool"/>/<see cref="GameDatabase.MatchesFilter"/>.</summary>
    public bool Matches(GameEntry e) =>
        (Decades.Count == 0 || (e.DecadeStart() is { } d && Decades.Contains(d)))
        && (Manufacturers.Count == 0 || Manufacturers.Contains(e.Manufacturer))
        && (Genres.Count == 0 || (e.Genres is { } g && Genres.Overlaps(g)));
}
