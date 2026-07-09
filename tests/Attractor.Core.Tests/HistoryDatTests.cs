using Attractor.Core.Catalog;

namespace Attractor.Core.Tests;

public class HistoryDatTests : IDisposable
{
    private readonly string _file = Path.GetTempFileName();

    public void Dispose() => File.Delete(_file);

    // The lead (untitled) section of a game's entry, as the side panel shows it.
    private static string LeadText(IReadOnlyList<AboutSection> sections) => sections[0].Body;

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
        Assert.StartsWith("Galaga (c) 1981 Namco.", LeadText(result["galaga"]));
        Assert.Contains("legendary fixed shooter", LeadText(result["galaga"]));
        Assert.Equal(result["galaga"], result["galagao"]);
        Assert.False(result.ContainsKey("unwanted"));
    }

    [Fact]
    public async Task Long_blurbs_are_truncated_at_a_word_boundary()
    {
        File.WriteAllText(_file,
            "$info=g\n$bio\n" + string.Join(' ', Enumerable.Repeat("word", 400)) + "\n$end\n");
        var result = await HistoryDat.LoadAsync(_file, new HashSet<string> { "g" }, maxChars: 100);
        Assert.True(LeadText(result["g"]).Length <= 105);
        Assert.EndsWith("…", LeadText(result["g"]));
    }

    [Fact]
    public async Task Missing_file_returns_empty() =>
        Assert.Empty(await HistoryDat.LoadAsync(_file + ".nope", new HashSet<string> { "g" }));

    [Fact]
    public async Task Splits_entries_into_sections_on_heading_lines()
    {
        File.WriteAllText(_file, """
            $info=galaga
            $bio
            Galaga (c) 1981 Namco.

            - TECHNICAL -
            Game ID : GG1

            - TRIVIA -
            An all-time classic.

            - TIPS AND TRICKS -
            Let the last bee circle.

            - CONTRIBUTE -
            Edit this entry: https://example.test/
            $end
            """);

        var sections = (await HistoryDat.LoadAsync(_file, new HashSet<string> { "galaga" }))["galaga"];

        // CONTRIBUTE is per-entry boilerplate and must be dropped.
        Assert.Equal(["ABOUT", "TECHNICAL", "TRIVIA", "TIPS AND TRICKS"], sections.Select(s => s.Title));
        Assert.StartsWith("Galaga (c) 1981 Namco.", sections[0].Body);
        Assert.Equal("An all-time classic.", sections[2].Body);
    }

    [Fact]
    public async Task Section_bodies_are_truncated_independently()
    {
        File.WriteAllText(_file,
            "$info=g\n$bio\nlead text\n- TRIVIA -\n" + string.Join(' ', Enumerable.Repeat("word", 400)) + "\n$end\n");

        var sections = (await HistoryDat.LoadAsync(_file, new HashSet<string> { "g" }, maxChars: 100))["g"];

        Assert.Equal("lead text", sections[0].Body); // short lead untouched
        Assert.True(sections[1].Body.Length <= 105);
        Assert.EndsWith("…", sections[1].Body);
    }

    [Fact]
    public async Task Prose_dashes_and_lowercase_dash_lines_do_not_split_sections()
    {
        File.WriteAllText(_file, """
            $info=g
            $bio
            An entry whose text contains
            - a bullet point -
            and carries on afterwards.
            $end
            """);

        var sections = (await HistoryDat.LoadAsync(_file, new HashSet<string> { "g" }))["g"];

        Assert.Single(sections); // "- a bullet point -" is prose, not an (uppercase) heading
        Assert.Equal("ABOUT", sections[0].Title);
        Assert.Contains("bullet point", sections[0].Body);
    }

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

                    The legendary fixed shooter &amp; sequel to Galaxian.

                    - TRIVIA -
                    Bee careful.</text>
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
        Assert.StartsWith("Galaga (c) 1981 Namco.", LeadText(result["galaga"]));
        Assert.Contains("legendary fixed shooter & sequel", LeadText(result["galaga"])); // &amp; decoded
        Assert.Equal(["ABOUT", "TRIVIA"], result["galaga"].Select(s => s.Title)); // xml entries split too
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
        Assert.StartsWith("Pac-Man (c) 1980 Namco.", LeadText(result["pacman"]));
    }
}
