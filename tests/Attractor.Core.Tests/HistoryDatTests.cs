using Attractor.Core.Catalog;

namespace Attractor.Core.Tests;

public class HistoryDatTests : IDisposable
{
    private readonly string _file = Path.GetTempFileName();

    public void Dispose() => File.Delete(_file);

    [Fact]
    public async Task Parses_bios_for_wanted_games_and_clones()
    {
        File.WriteAllText(_file, """
            $info=galaga,galagao
            $bio
            Galaga (c) 1981 Namco.

            The legendary fixed shooter.
            $end
            $info=unwanted
            $bio
            Should not be loaded.
            $end
            """);

        var result = await HistoryDat.LoadAsync(_file, new HashSet<string> { "galaga", "galagao", "1942" });

        Assert.Equal(2, result.Count);
        Assert.StartsWith("Galaga (c) 1981 Namco.", result["galaga"]);
        Assert.Contains("legendary fixed shooter", result["galaga"]);
        Assert.Equal(result["galaga"], result["galagao"]);
        Assert.False(result.ContainsKey("unwanted"));
    }

    [Fact]
    public async Task Long_blurbs_are_truncated_at_a_word_boundary()
    {
        File.WriteAllText(_file,
            "$info=g\n$bio\n" + string.Join(' ', Enumerable.Repeat("word", 400)) + "\n$end\n");
        var result = await HistoryDat.LoadAsync(_file, new HashSet<string> { "g" }, maxChars: 100);
        Assert.True(result["g"].Length <= 105);
        Assert.EndsWith("…", result["g"]);
    }

    [Fact]
    public async Task Missing_file_returns_empty() =>
        Assert.Empty(await HistoryDat.LoadAsync(_file + ".nope", new HashSet<string> { "g" }));

    [Fact]
    public async Task Parses_modern_history_xml_for_wanted_systems_and_clones()
    {
        // Detected as XML by the leading '<' (the temp file's extension is .tmp).
        File.WriteAllText(_file, """
            <?xml version="1.0" encoding="UTF-8"?>
            <history version="2.88">
                <entry>
                    <software>
                        <item list="nes" name="ignored" />
                    </software>
                    <text>A software-list cart that must not load.</text>
                </entry>
                <entry>
                    <systems>
                        <system name="galaga" game="yes" />
                        <system name="galagao" game="yes" />
                    </systems>
                    <text>Galaga (c) 1981 Namco.

                    The legendary fixed shooter &amp; sequel to Galaxian.</text>
                </entry>
                <entry>
                    <systems>
                        <system name="unwanted" />
                    </systems>
                    <text>Should not be loaded.</text>
                </entry>
            </history>
            """);

        var result = await HistoryDat.LoadAsync(_file, new HashSet<string> { "galaga", "galagao", "1942" });

        Assert.Equal(2, result.Count);
        Assert.StartsWith("Galaga (c) 1981 Namco.", result["galaga"]);
        Assert.Contains("legendary fixed shooter & sequel", result["galaga"]); // &amp; decoded
        Assert.Equal(result["galaga"], result["galagao"]);
        Assert.False(result.ContainsKey("unwanted")); // wanted-set filtered
        Assert.False(result.ContainsKey("ignored"));  // <software> ignored, not matched to text
    }

    [Fact]
    public async Task Detects_xml_regardless_of_leading_whitespace()
    {
        File.WriteAllText(_file, "\n\n   <history><entry><systems><system name=\"pacman\"/></systems>"
            + "<text>Pac-Man (c) 1980 Namco.</text></entry></history>");
        var result = await HistoryDat.LoadAsync(_file, new HashSet<string> { "pacman" });
        Assert.StartsWith("Pac-Man (c) 1980 Namco.", result["pacman"]);
    }
}
