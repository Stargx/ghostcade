using Attractor.Core.Rotation;

namespace Attractor.Core.Tests;

public class FaultPolicyTests
{
    [Fact]
    public void Two_consecutive_faults_quarantine_the_game()
    {
        var p = new FaultPolicy(new FakeTime());
        Assert.Equal(FaultVerdict.SkipGame, p.RecordFault("g"));
        Assert.Equal(FaultVerdict.QuarantineGame, p.RecordFault("g"));
        Assert.True(p.IsQuarantined("g"));
    }

    [Fact]
    public void Success_resets_the_consecutive_count()
    {
        var p = new FaultPolicy(new FakeTime());
        p.RecordFault("g");
        p.RecordSuccess("g");
        Assert.Equal(FaultVerdict.SkipGame, p.RecordFault("g"));
        Assert.False(p.IsQuarantined("g"));
    }

    [Fact]
    public void Five_faults_in_two_minutes_fault_the_engine()
    {
        var time = new FakeTime();
        var p = new FaultPolicy(time);
        FaultVerdict last = default;
        for (int i = 0; i < 5; i++)
        {
            last = p.RecordFault($"g{i}");
            time.Now += TimeSpan.FromSeconds(10);
        }
        Assert.Equal(FaultVerdict.EngineFaulted, last);
    }

    [Fact]
    public void A_success_clears_the_global_fault_window()
    {
        // 4 rapid faults, then one game runs fine: the streak is broken, so the
        // next fault must count as 1-of-5 again, not tip the engine over.
        var time = new FakeTime();
        var p = new FaultPolicy(time);
        for (int i = 0; i < 4; i++)
        {
            p.RecordFault($"g{i}");
            time.Now += TimeSpan.FromSeconds(5);
        }
        p.RecordSuccess("healthy");
        Assert.Equal(FaultVerdict.SkipGame, p.RecordFault("another"));
    }

    [Fact]
    public void Slow_faults_do_not_fault_the_engine()
    {
        var time = new FakeTime();
        var p = new FaultPolicy(time);
        for (int i = 0; i < 10; i++)
        {
            Assert.NotEqual(FaultVerdict.EngineFaulted, p.RecordFault($"g{i}"));
            time.Now += TimeSpan.FromMinutes(3); // outside the window every time
        }
    }
}
