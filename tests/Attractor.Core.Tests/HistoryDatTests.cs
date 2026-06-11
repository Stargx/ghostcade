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
}
