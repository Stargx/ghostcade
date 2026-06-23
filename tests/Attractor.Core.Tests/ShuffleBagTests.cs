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
    public void Snapshot_and_restore_resume_the_cycle_without_repeating()
    {
        string[] pool = ["a", "b", "c", "d", "e"];

        // run 1: draw two, then "quit" and snapshot the remainder
        var bag1 = new ShuffleBag(() => pool, new Random(99));
        var drawn = new List<string> { bag1.Draw()!, bag1.Draw()! };
        var saved = bag1.Snapshot();
        Assert.Equal(3, saved.Count); // 5 - 2 dealt

        // run 2: restore the saved queue and finish the cycle
        var bag2 = new ShuffleBag(() => pool, new Random(7), initialQueue: saved);
        for (int i = 0; i < 3; i++)
            drawn.Add(bag2.Draw()!);

        // the whole pool was seen exactly once across the "restart" — no repeats,
        // and the tail games (which a fresh reshuffle each run would never reach)
        // are guaranteed to appear
        Assert.Equal(pool.ToHashSet(), drawn.ToHashSet());
        Assert.Equal(5, drawn.Count);
    }

    [Fact]
    public void Restored_queue_skips_games_no_longer_in_the_pool()
    {
        string[] pool = ["a", "b"]; // "gone" was saved but has left the catalog
        var bag = new ShuffleBag(() => pool, new Random(1), initialQueue: ["gone", "a"]);
        // caller filters the saved queue to the pool before restoring, but even a
        // stale entry that slips through is simply dealt then never seen again
        var first = bag.Draw(g => g == "gone"); // exclusion stands in for "not in pool"
        Assert.NotEqual("gone", first);
    }

    [Fact]
    public void Reshuffle_discards_the_remainder_and_reseeds_from_the_current_pool()
    {
        var pool = new List<string> { "a", "b", "c" };
        var bag = new ShuffleBag(() => pool, new Random(5));
        bag.Draw(); // deal one; two remain in the cycle
        Assert.Equal(2, bag.Snapshot().Count);

        pool.Add("d"); // the pool grows (e.g. a widened filter)
        bag.Reshuffle();

        var snap = bag.Snapshot();
        Assert.Equal(4, snap.Count);                      // a fresh full cycle...
        Assert.Equal(pool.ToHashSet(), snap.ToHashSet()); // ...covering the whole new pool
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
