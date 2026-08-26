using System.IO;
using Attractor.Core.Art;

namespace Attractor.Core.Tests;

/// <summary>Covers marquee/snap resolution and the clone→parent (cloneof) fallback.</summary>
public sealed class ArtLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "attractor-art-" + Guid.NewGuid().ToString("N"));
    private readonly string _marquees;
    private readonly string _snaps;

    public ArtLocatorTests()
    {
        _marquees = Path.Combine(_root, "marquees");
        _snaps = Path.Combine(_root, "snap");
        Directory.CreateDirectory(_marquees);
        Directory.CreateDirectory(_snaps);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private ArtLocator Locator() => new(_root, ["marquees"], ["snap"]);

    private static void Touch(string dir, string name) => File.WriteAllText(Path.Combine(dir, name), "");

    [Fact]
    public void Own_marquee_wins_over_parent()
    {
        Touch(_marquees, "clone.png");
        Touch(_marquees, "parent.png");
        Assert.Equal(Path.Combine(_marquees, "clone.png"), Locator().FindMarquee("clone", "parent"));
    }

    [Fact]
    public void Falls_back_to_parent_when_own_missing()
    {
        Touch(_marquees, "parent.png");
        Assert.Equal(Path.Combine(_marquees, "parent.png"), Locator().FindMarquee("clone", "parent"));
    }

    [Fact]
    public void No_own_and_null_parent_returns_null()
    {
        Assert.Null(Locator().FindMarquee("clone", null));
        Assert.Null(Locator().FindMarquee("clone")); // parent defaults to null
    }

    [Fact]
    public void No_own_and_parent_also_missing_returns_null()
    {
        Assert.Null(Locator().FindMarquee("clone", "parent"));
    }

    [Fact]
    public void Snap_also_falls_back_to_parent()
    {
        Touch(_snaps, "parent.png");
        Assert.Equal(Path.Combine(_snaps, "parent.png"), Locator().FindSnap("clone", "parent"));
    }

    [Fact]
    public void Png_is_preferred_over_jpg()
    {
        Touch(_marquees, "g.jpg");
        Touch(_marquees, "g.png");
        Assert.Equal(Path.Combine(_marquees, "g.png"), Locator().FindMarquee("g"));
    }
}
