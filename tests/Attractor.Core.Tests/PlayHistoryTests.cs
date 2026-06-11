using Attractor.Core.Rotation;

namespace Attractor.Core.Tests;

public class PlayHistoryTests
{
    [Fact]
    public void Back_and_forward_walk_like_a_browser()
    {
        var h = new PlayHistory();
        h.Append("a"); h.Append("b"); h.Append("c");

        Assert.Equal("b", h.TryBack());
        Assert.Equal("a", h.TryBack());
        Assert.Null(h.TryBack());

        Assert.Equal("b", h.TryForward());
        Assert.Equal("c", h.TryForward());
        Assert.Null(h.TryForward());
        Assert.True(h.AtEnd);
    }

    [Fact]
    public void Back_skips_excluded_entries()
    {
        var h = new PlayHistory();
        h.Append("a"); h.Append("banned"); h.Append("c");
        Assert.Equal("a", h.TryBack(g => g == "banned"));
    }

    [Fact]
    public void Capacity_is_bounded()
    {
        var h = new PlayHistory();
        for (int i = 0; i < 200; i++)
            h.Append($"g{i}");
        int steps = 0;
        while (h.TryBack() is not null)
            steps++;
        Assert.True(steps < 60, $"history grew unbounded ({steps} steps back)");
    }
}
