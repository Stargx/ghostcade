using Attractor.Core.Catalog;

namespace Attractor.Core.Tests;

public class GameFilterTests
{
    private static GameEntry Game(string name, string year, string manufacturer) =>
        new(name, name, year, manufacturer, DriverStatus.Good, VerifyResult.Good, 0);

    [Theory]
    [InlineData("1981", 1980)]
    [InlineData("1975", 1970)]
    [InlineData("1990", 1990)]
    [InlineData("2001", 2000)]
    [InlineData("2010", 2010)]
    [InlineData("198?", 1980)] // partial year still resolves to its decade
    public void DecadeStart_groups_years(string year, int expected) =>
        Assert.Equal(expected, Game("g", year, "m").DecadeStart());

    [Theory]
    [InlineData("????")]
    [InlineData("19??")]
    [InlineData("")]
    [InlineData("19")]
    public void DecadeStart_is_null_for_unknown_or_too_vague(string year) =>
        Assert.Null(Game("g", year, "m").DecadeStart());

    [Fact]
    public void None_is_inactive_and_matches_everything()
    {
        Assert.False(GameFilter.None.IsActive);
        Assert.True(GameFilter.None.Matches(Game("g", "????", "whoever")));
    }

    [Fact]
    public void Decade_filter_matches_by_decade_and_excludes_unknown_year()
    {
        var f = new GameFilter([1980], []);
        Assert.True(f.IsActive);
        Assert.True(f.Matches(Game("a", "1984", "x")));
        Assert.False(f.Matches(Game("b", "1992", "x")));
        Assert.False(f.Matches(Game("c", "????", "x")));
    }

    [Fact]
    public void Manufacturer_filter_is_case_insensitive_but_exact()
    {
        var f = new GameFilter([], ["Capcom"]);
        Assert.True(f.Matches(Game("a", "1987", "capcom")));
        Assert.False(f.Matches(Game("b", "1987", "Capcom / Romstar")));
    }

    [Fact]
    public void Decade_and_manufacturer_combine_with_and()
    {
        var f = new GameFilter([1980], ["Capcom"]);
        Assert.True(f.Matches(Game("a", "1987", "Capcom")));
        Assert.False(f.Matches(Game("b", "1997", "Capcom"))); // wrong decade
        Assert.False(f.Matches(Game("c", "1987", "Konami"))); // wrong manufacturer
    }

    [Fact]
    public void RotationPool_applies_filter_and_bans_together()
    {
        MachineInfo[] machines =
        [
            new("a", "A", "1981", "Capcom", false, false, true, null, DriverStatus.Good, 0),
            new("b", "B", "1991", "Capcom", false, false, true, null, DriverStatus.Good, 0),
            new("c", "C", "1983", "Konami", false, false, true, null, DriverStatus.Good, 0),
        ];
        var verified = machines.ToDictionary(m => m.Name, _ => VerifyResult.Good);
        var db = GameDatabase.Assemble(machines, verified, new InMemoryTagStore(), new InMemoryTagStore());

        db.Filter = new GameFilter([1980], []);
        Assert.Equal(["a", "c"], db.RotationPool().OrderBy(x => x));
        Assert.True(db.MatchesFilter("a"));
        Assert.False(db.MatchesFilter("b"));

        db.Banned.Add("a");
        Assert.Equal(["c"], db.RotationPool());
    }

    [Fact]
    public void Genre_filter_matches_case_insensitively_and_excludes_games_without_genre_data()
    {
        var f = new GameFilter([], [], ["Shooter"]);
        Assert.True(f.IsActive);
        Assert.True(f.Matches(Game("a", "1987", "x") with { Genres = ["shooter"] }));
        Assert.False(f.Matches(Game("b", "1987", "x") with { Genres = ["Maze"] }));
        // no catver entry (or no catver.ini at all) = no genre = excluded while a
        // genre constraint is active
        Assert.False(f.Matches(Game("c", "1987", "x")));
    }

    [Fact]
    public void Genre_filter_matches_any_of_a_games_tags()
    {
        // Final Fight is "Fighter / 2.5D" — both tags must be selectable, and either
        // one (or an overlap) qualifies the game. This is the bug being fixed.
        var ffight = Game("ffight", "1989", "Capcom") with { Genres = ["Fighter", "2.5D"] };
        Assert.True(new GameFilter([], [], ["Fighter"]).Matches(ffight));         // top-level tag
        Assert.True(new GameFilter([], [], ["2.5D"]).Matches(ffight));            // subgenre tag
        Assert.True(new GameFilter([], [], ["Shooter", "2.5D"]).Matches(ffight)); // any overlap qualifies
        Assert.False(new GameFilter([], [], ["Shooter"]).Matches(ffight));        // no shared tag
    }

    [Fact]
    public void Genre_combines_with_decade_and_manufacturer_with_and()
    {
        var f = new GameFilter([1980], ["Capcom"], ["Shooter"]);
        Assert.True(f.Matches(Game("a", "1987", "Capcom") with { Genres = ["Shooter"] }));
        Assert.False(f.Matches(Game("b", "1987", "Capcom") with { Genres = ["Fighter"] })); // wrong genre
        Assert.False(f.Matches(Game("c", "1997", "Capcom") with { Genres = ["Shooter"] })); // wrong decade
    }

    [Fact]
    public void ApplyGenres_attaches_tags_with_clone_fallback_and_filters_on_any_tag()
    {
        MachineInfo[] machines =
        [
            new("puckman",  "Puck Man",  "1980", "Namco", false, false, true, null,      DriverStatus.Good, 90),
            new("puckmanb", "Puck Man B","1980", "Namco", false, false, true, "puckman", DriverStatus.Good, 90),
            new("mystery",  "Mystery",   "1984", "Nobody", false, false, true, null,     DriverStatus.Good, 0),
        ];
        var verified = machines.ToDictionary(m => m.Name, _ => VerifyResult.Good);
        var db = GameDatabase.Assemble(machines, verified, new InMemoryTagStore(), new InMemoryTagStore());

        db.ApplyGenres(new Dictionary<string, IReadOnlyList<string>> { ["puckman"] = ["Maze", "Collect"] });

        Assert.Equal(["Maze", "Collect"], db.Find("puckman")!.Genres);
        Assert.Equal(["Maze", "Collect"], db.Find("puckmanb")!.Genres); // clone inherits its parent's tags
        Assert.Null(db.Find("mystery")!.Genres);

        db.Filter = new GameFilter([], [], ["Maze"]);                    // top-level tag
        Assert.Equal(["puckman", "puckmanb"], db.RotationPool().OrderBy(x => x));

        db.Filter = new GameFilter([], [], ["Collect"]);                 // subgenre tag pulls the same games
        Assert.Equal(["puckman", "puckmanb"], db.RotationPool().OrderBy(x => x));
    }

    [Fact]
    public void FavoritesOnly_makes_the_filter_active_but_is_not_part_of_metadata_match()
    {
        var f = new GameFilter([], [], favoritesOnly: true);
        Assert.True(f.IsActive);
        // Matches() covers decade/manufacturer only — the favourites gate lives on
        // GameDatabase (which owns the set), so a non-favourite still passes Matches().
        Assert.True(f.Matches(Game("g", "1984", "whoever")));
    }

    [Fact]
    public void FavoritesOnly_pool_is_restricted_to_favourited_games()
    {
        MachineInfo[] machines =
        [
            new("a", "A", "1981", "Capcom", false, false, true, null, DriverStatus.Good, 0),
            new("b", "B", "1991", "Konami", false, false, true, null, DriverStatus.Good, 0),
            new("c", "C", "1983", "Sega",   false, false, true, null, DriverStatus.Good, 0),
        ];
        var verified = machines.ToDictionary(m => m.Name, _ => VerifyResult.Good);
        var favorites = new InMemoryTagStore();
        favorites.Add("a");
        favorites.Add("c");
        var db = GameDatabase.Assemble(machines, verified, new InMemoryTagStore(), favorites);

        db.Filter = new GameFilter([], [], favoritesOnly: true);
        Assert.Equal(["a", "c"], db.RotationPool().OrderBy(x => x));
        Assert.True(db.MatchesFilter("a"));
        Assert.False(db.MatchesFilter("b")); // not favourited
    }

    [Fact]
    public void FavoritesOnly_combines_with_decade_and_bans()
    {
        MachineInfo[] machines =
        [
            new("a", "A", "1981", "Capcom", false, false, true, null, DriverStatus.Good, 0),
            new("b", "B", "1991", "Capcom", false, false, true, null, DriverStatus.Good, 0),
            new("c", "C", "1983", "Konami", false, false, true, null, DriverStatus.Good, 0),
        ];
        var verified = machines.ToDictionary(m => m.Name, _ => VerifyResult.Good);
        var favorites = new InMemoryTagStore();
        favorites.Add("a");
        favorites.Add("b"); // favourited but in the wrong decade once we narrow to the 80s
        var db = GameDatabase.Assemble(machines, verified, new InMemoryTagStore(), favorites);

        // favourites-only AND the 1980s -> only "a" (b is 1991, c isn't favourited)
        db.Filter = new GameFilter([1980], [], favoritesOnly: true);
        Assert.Equal(["a"], db.RotationPool());
        Assert.False(db.MatchesFilter("b")); // favourited but filtered out by decade
        Assert.False(db.MatchesFilter("c")); // right decade but not favourited

        db.Banned.Add("a"); // ban the only match -> empty pool
        Assert.Empty(db.RotationPool());
    }

    [Fact]
    public void FavoritesOnly_combines_with_manufacturer()
    {
        MachineInfo[] machines =
        [
            new("a", "A", "1987", "capcom", false, false, true, null, DriverStatus.Good, 0), // fav + Capcom (lower-case pins OrdinalIgnoreCase)
            new("b", "B", "1987", "Konami", false, false, true, null, DriverStatus.Good, 0), // fav but wrong manufacturer
            new("c", "C", "1987", "Capcom", false, false, true, null, DriverStatus.Good, 0), // right manufacturer but not fav
        ];
        var verified = machines.ToDictionary(m => m.Name, _ => VerifyResult.Good);
        var favorites = new InMemoryTagStore();
        favorites.Add("a");
        favorites.Add("b");
        var db = GameDatabase.Assemble(machines, verified, new InMemoryTagStore(), favorites);

        db.Filter = new GameFilter([], ["Capcom"], favoritesOnly: true);
        Assert.Equal(["a"], db.RotationPool());
        Assert.False(db.MatchesFilter("b")); // favourited but wrong manufacturer
        Assert.False(db.MatchesFilter("c")); // right manufacturer but not favourited
    }

    [Fact]
    public void Clearing_FavoritesOnly_restores_the_full_pool()
    {
        MachineInfo[] machines =
        [
            new("a", "A", "1981", "Capcom", false, false, true, null, DriverStatus.Good, 0),
            new("b", "B", "1991", "Konami", false, false, true, null, DriverStatus.Good, 0),
            new("c", "C", "1983", "Sega",   false, false, true, null, DriverStatus.Good, 0),
        ];
        var verified = machines.ToDictionary(m => m.Name, _ => VerifyResult.Good);
        var favorites = new InMemoryTagStore();
        favorites.Add("a");
        var db = GameDatabase.Assemble(machines, verified, new InMemoryTagStore(), favorites);

        db.Filter = new GameFilter([], [], favoritesOnly: true);
        Assert.Equal(["a"], db.RotationPool());

        // The gate is keyed purely off the flag, not off favourites membership — turning it
        // off restores the whole pool even though "a" is still the only favourite.
        db.Filter = GameFilter.None;
        Assert.Equal(["a", "b", "c"], db.RotationPool().OrderBy(x => x));
        Assert.True(db.MatchesFilter("b"));
    }

    [Fact]
    public void FavoritesOnly_with_no_favourites_is_active_but_yields_an_empty_pool()
    {
        // The realistic first-use state: "favourites only" enabled before starring anything.
        // IsActive must stay true (so the app idles on "paused", not "no filter") while the
        // pool is empty (a recoverable idle the engine waits out).
        MachineInfo[] machines =
        [
            new("a", "A", "1981", "Capcom", false, false, true, null, DriverStatus.Good, 0),
            new("b", "B", "1991", "Konami", false, false, true, null, DriverStatus.Good, 0),
        ];
        var verified = machines.ToDictionary(m => m.Name, _ => VerifyResult.Good);
        var db = GameDatabase.Assemble(machines, verified, new InMemoryTagStore(), new InMemoryTagStore());

        db.Filter = new GameFilter([], [], favoritesOnly: true);
        Assert.True(db.Filter.IsActive);
        Assert.Empty(db.RotationPool());
        Assert.False(db.MatchesFilter("a"));
    }
}
