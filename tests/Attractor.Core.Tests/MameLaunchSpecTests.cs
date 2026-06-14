using Attractor.Core.Mame;

namespace Attractor.Core.Tests;

public class MameLaunchSpecTests
{
    [Fact]
    public void Seconds_mode_emits_seconds_to_run_and_no_disclaimer_skip()
    {
        var args = new MameLaunchSpec(@"c:\mame.exe", "pacman", 299).BuildArgumentList();

        Assert.Equal("299", ArgAfter(args, "-seconds_to_run"));
        Assert.DoesNotContain("-frames_to_run", args);
        Assert.DoesNotContain("-skip_disclaimer", args); // modern MAME rejects it
    }

    [Fact]
    public void Frames_mode_emits_frames_to_run_and_skip_disclaimer()
    {
        var args = new MameLaunchSpec(@"c:\mame.exe", "pacman", 299,
            TimingMode: MameTimingMode.FramesToRun, RefreshHz: 60).BuildArgumentList();

        Assert.Equal("17940", ArgAfter(args, "-frames_to_run")); // 299 * 60
        Assert.Contains("-skip_disclaimer", args);              // legacy MAME needs it
        Assert.DoesNotContain("-seconds_to_run", args);
    }

    [Fact]
    public void Frames_count_scales_with_refresh()
    {
        var args = new MameLaunchSpec(@"c:\mame.exe", "pacman", 100,
            TimingMode: MameTimingMode.FramesToRun, RefreshHz: 50).BuildArgumentList();

        Assert.Equal("5000", ArgAfter(args, "-frames_to_run")); // 100 * 50
    }

    [Theory]
    [InlineData(MameTimingMode.SecondsToRun)]
    [InlineData(MameTimingMode.FramesToRun)]
    public void Zero_seconds_emits_no_timing_arg(MameTimingMode mode)
    {
        var args = new MameLaunchSpec(@"c:\mame.exe", "pacman", 0, TimingMode: mode).BuildArgumentList();

        Assert.DoesNotContain("-seconds_to_run", args);
        Assert.DoesNotContain("-frames_to_run", args);
    }

    [Fact]
    public void Game_name_leads_and_skip_gameinfo_is_always_present()
    {
        var args = new MameLaunchSpec(@"c:\mame.exe", "pacman", 60).BuildArgumentList();

        Assert.Equal("pacman", args[0]);
        Assert.Contains("-skip_gameinfo", args);
    }

    private static string ArgAfter(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i + 1 < args.Count; i++)
            if (args[i] == flag)
                return args[i + 1];
        return "";
    }
}
