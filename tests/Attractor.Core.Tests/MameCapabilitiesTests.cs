using Attractor.Core.Mame;

namespace Attractor.Core.Tests;

public class MameCapabilitiesTests
{
    [Theory]
    [InlineData("M.A.M.E. v0.78 (Dec 24 2003)", 78, MameTimingMode.FramesToRun, false)]  // parses, but pre-0.147 = unsupported
    [InlineData("MAME v0.147 (mame0147)", 147, MameTimingMode.SecondsToRun, true)]
    [InlineData("MAME 0.220 (mame0220)", 220, MameTimingMode.SecondsToRun, true)]
    [InlineData("M.A.M.E. v0.62 (older build)", 62, MameTimingMode.FramesToRun, false)]
    public void Parses_version_dialect_and_support(string banner, int minor, MameTimingMode mode, bool supported)
    {
        var caps = MameCapabilities.Parse(banner);
        Assert.Equal(minor, caps.VersionMinor);
        Assert.Equal(mode, caps.TimingMode);
        Assert.Equal(supported, caps.Supported);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("some unrecognised banner with no version")]
    public void Unparseable_banner_defaults_to_modern_supported(string? banner)
    {
        var caps = MameCapabilities.Parse(banner);
        Assert.Null(caps.VersionMinor);
        Assert.Equal(MameTimingMode.SecondsToRun, caps.TimingMode);
        Assert.True(caps.Supported);
        Assert.Equal("unknown", caps.VersionLabel);
    }

    [Fact]
    public void The_0_147_floor_is_the_boundary_for_dialect_and_support()
    {
        // 0.147 is both the seconds-dialect floor and the support floor: one below
        // still parses (as frames) but is unsupported; the floor itself uses seconds and is supported.
        var below = MameCapabilities.Parse("MAME 0.146");
        Assert.Equal(MameTimingMode.FramesToRun, below.TimingMode);
        Assert.False(below.Supported);
        var floor = MameCapabilities.Parse("MAME 0.147");
        Assert.Equal(MameTimingMode.SecondsToRun, floor.TimingMode);
        Assert.True(floor.Supported);
    }

    [Fact]
    public void Version_label_formats_the_minor()
    {
        Assert.Equal("0.220", MameCapabilities.Parse("MAME 0.220").VersionLabel);
    }
}
