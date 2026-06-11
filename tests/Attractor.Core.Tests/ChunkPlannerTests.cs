using Attractor.Core.Rotation;

namespace Attractor.Core.Tests;

public class ChunkPlannerTests
{
    [Theory]
    [InlineData(300, new[] { 299 })]          // the daily-driver case: 1s remainder dropped
    [InlineData(299, new[] { 299 })]
    [InlineData(45, new[] { 45 })]            // a tiny only-chunk survives
    [InlineData(420, new[] { 299, 121 })]
    [InlineData(359, new[] { 299, 60 })]      // 60s trailing chunk is kept...
    [InlineData(358, new[] { 299 })]          // ...59s is dropped
    [InlineData(600, new[] { 299, 299 })]     // 2s remainder dropped
    [InlineData(0, new[] { 1 })]              // degenerate input clamps to 1s
    public void Plans_match_prototype_rule(int dwell, int[] expected) =>
        Assert.Equal(expected, ChunkPlanner.Plan(dwell));

    [Fact]
    public void Every_chunk_stays_under_the_startup_screen_threshold()
    {
        for (int dwell = 1; dwell <= 4000; dwell += 37)
            Assert.All(ChunkPlanner.Plan(dwell), c => Assert.InRange(c, 1, 299));
    }
}
