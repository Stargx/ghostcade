using Attractor.Core.Rotation;

namespace Attractor.Core.Tests;

public class ShuffleBagTests
{
    [Fact]
    public void Sees_every_game_once_per_cycle()
    {
        string[] pool = ["a", "b", "c", "d", "e"];
        var bag = new ShuffleBag(() => pool, new Random(42));

        for (int cycle = 0; cycle < 3; cycle++)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < pool.Length; i++)
                Assert.True(seen.Add(bag.Draw()!), "repeat within a cycle");
            Assert.Equal(pool.ToHashSet(), seen);
        }
    }

    [Fact]
    public void Excluded_games_are_skipped_mid_cycle()
    {
        string[] pool = ["a", "b", "c"];
        var bag = new ShuffleBag(() => pool, new Random(1));
        for (int i = 0; i < 10; i++)
            Assert.NotEqual("b", bag.Draw(g => g == "b"));
    }

    [Fact]
    public void Empty_pool_returns_null() =>
        Assert.Null(new ShuffleBag(() => Array.Empty<string>()).Draw());

    [Fact]
    public void Fully_excluded_pool_returns_null_instead_of_spinning()
    {
        var bag = new ShuffleBag(() => new[] { "a", "b" }, new Random(7));
        Assert.Null(bag.Draw(_ => true));
    }

    [Fact]
    public void Pool_changes_apply_at_refill()
    {
        var pool = new List<string> { "a" };
        var bag = new ShuffleBag(() => pool, new Random(3));
        Assert.Equal("a", bag.Draw());
        pool.Add("b");
        var next = new HashSet<string> { bag.Draw()!, bag.Draw()! };
        Assert.Equal(["a", "b"], next.Order());
    }
}
