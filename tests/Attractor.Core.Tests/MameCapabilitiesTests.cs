using Attractor.Core.Mame;

namespace Attractor.Core.Tests;

public class MameCapabilitiesTests
{
    [Theory]
    [InlineData("M.A.M.E. v0.78 (Dec 24 2003)", 78, MameTimingMode.FramesToRun, true)]
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
    public void The_seconds_floor_is_the_boundary()
    {
        // one below the floor still needs frames; the floor itself uses seconds
        Assert.Equal(MameTimingMode.FramesToRun, MameCapabilities.Parse("MAME 0.146").TimingMode);
        Assert.Equal(MameTimingMode.SecondsToRun, MameCapabilities.Parse("MAME 0.147").TimingMode);
    }

    [Fact]
    public void Version_label_formats_the_minor()
    {
        Assert.Equal("0.220", MameCapabilities.Parse("MAME 0.220").VersionLabel);
    }
}
