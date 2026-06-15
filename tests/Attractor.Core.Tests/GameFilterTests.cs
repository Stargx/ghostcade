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
}
