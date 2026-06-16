using Attractor.Core.Mame;

namespace Attractor.Core.Tests;

public class MameConfigTests
{
    [Theory]
    [InlineData("rompath                   roms", "roms")]
    [InlineData("rompath   roms;D:\\more roms", "roms;D:\\more roms")]
    [InlineData("  rompath\troms", "roms")]
    public void ParseRompathLine_extracts_the_value(string line, string expected) =>
        Assert.Equal(expected, MameConfig.ParseRompathLine(line));

    [Theory]
    [InlineData("samplepath               samples")]
    [InlineData("rompathx roms")] // a different key that merely starts with "rompath"
    [InlineData("# comment")]
    [InlineData("rompath")]       // key present but no value
    public void ParseRompathLine_ignores_other_lines(string line) =>
        Assert.Null(MameConfig.ParseRompathLine(line));

    [Fact]
    public void SplitPaths_resolves_relative_against_mame_dir_and_strips_quotes()
    {
        var dirs = MameConfig.SplitPaths("roms;\"D:\\abs roms\"", @"C:\mame");
        Assert.Equal([@"C:\mame\roms", @"D:\abs roms"], dirs);
    }

    [Fact]
    public void ResolveRomFolder_finds_set_then_parent_then_falls_back()
    {
        var root = Directory.CreateTempSubdirectory("attractor-rom-").FullName;
        try
        {
            var a = Path.Combine(root, "a");
            var b = Path.Combine(root, "b");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(b, "galaga.zip"), "");
            File.WriteAllText(Path.Combine(b, "puckman.zip"), ""); // parent of a merged clone

            Assert.Equal(b, MameConfig.ResolveRomFolder([a, b], "galaga"));
            Assert.Equal(b, MameConfig.ResolveRomFolder([a, b], "mspacman", "puckman")); // via parent set
            Assert.Equal(a, MameConfig.ResolveRomFolder([a, b], "missing"));             // fallback = first
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("anything")]
    [InlineData("missing")]
    public void ResolveRomFolder_with_one_rompath_returns_it_without_probing(string name) =>
        Assert.Equal(@"C:\only", MameConfig.ResolveRomFolder([@"C:\only"], name));

    [Fact]
    public void ResolveRomFolder_with_no_rompath_is_empty() =>
        Assert.Equal("", MameConfig.ResolveRomFolder([], "x"));
}
