using Attractor.Core.Mame;

namespace Attractor.Core.Tests;

public class MameLauncherTests
{
    // CreateProcessW takes ONE command-line string; these rules are what
    // CommandLineToArgvW (and MAME's argv) will apply in reverse.
    [Theory]
    [InlineData("pacman", "pacman")]                     // plain args stay bare
    [InlineData("-rompath", "-rompath")]
    [InlineData(@"D:\Arcade Roms", "\"D:\\Arcade Roms\"")]  // spaces get quotes
    [InlineData("", "\"\"")]                             // empty arg must survive
    [InlineData("a\tb", "\"a\tb\"")]                     // tabs separate args too
    public void QuoteArgument_quotes_only_when_needed(string arg, string expected) =>
        Assert.Equal(expected, MameLauncher.QuoteArgument(arg));

    [Fact]
    public void Trailing_backslashes_before_the_closing_quote_are_doubled()
    {
        // "D:\Arcade Roms\" would escape the closing quote; it must double to \\".
        Assert.Equal("\"D:\\Arcade Roms\\\\\"", MameLauncher.QuoteArgument(@"D:\Arcade Roms\"));
        // ...but backslashes NOT followed by a quote stay single inside quotes.
        Assert.Equal("\"D:\\Arcade Roms\\sub dir\"", MameLauncher.QuoteArgument(@"D:\Arcade Roms\sub dir"));
    }

    [Fact]
    public void Embedded_quotes_are_escaped()
    {
        Assert.Equal("\"say \\\"hi\\\"\"", MameLauncher.QuoteArgument("say \"hi\""));
        // a backslash before an embedded quote doubles, plus the quote's own escape
        Assert.Equal("\"c:\\\\\\\"x\"", MameLauncher.QuoteArgument("c:\\\"x"));
    }
}
