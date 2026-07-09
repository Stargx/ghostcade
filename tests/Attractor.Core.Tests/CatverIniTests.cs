using Attractor.Core.Catalog;

namespace Attractor.Core.Tests;

public class CatverIniTests : IDisposable
{
    private readonly string _file = Path.GetTempFileName();

    public void Dispose() => File.Delete(_file);

    [Fact]
    public async Task Parses_the_Category_section_splitting_every_tag_top_level_first()
    {
        File.WriteAllText(_file, """
            ;; catver.ini
            [FOLDER_SETTINGS]
            RootFolderIcon mmix

            [Category]
            puckman=Maze / Collect
            1942=Shooter / Flying Vertical
            gals=Puzzle / Toss * Mature *
            sf2=Fighter / Versus

            [VerAdded]
            puckman=.26
            1942=.28
            """);

        var genres = await CatverIni.LoadAsync(_file);

        Assert.Equal(["Maze", "Collect"], genres["puckman"]);
        Assert.Equal(["Shooter", "Flying Vertical"], genres["1942"]);
        Assert.Equal(["Puzzle", "Toss"], genres["gals"]);   // mature marker stripped, subgenre kept
        Assert.Equal(["Fighter", "Versus"], genres["sf2"]);
        Assert.Equal(4, genres.Count);                       // [VerAdded] lines must not leak in
    }

    [Fact]
    public async Task Subgenre_is_an_independently_filterable_tag()
    {
        // The bug this fixes: only the top-level genre used to survive, so "2.5D" was lost.
        File.WriteAllText(_file, "[Category]\nffight=Fighter / 2.5D\n");
        Assert.Equal(["Fighter", "2.5D"], (await CatverIni.LoadAsync(_file))["ffight"]);
    }

    [Fact]
    public async Task Accepts_a_headerless_file_of_bare_category_lines()
    {
        File.WriteAllText(_file, "puckman=Maze / Collect\r\ndigdug=Maze / Digging\r\n");
        var genres = await CatverIni.LoadAsync(_file);
        Assert.Equal(["Maze", "Collect"], genres["puckman"]);
        Assert.Equal(2, genres.Count);
    }

    [Fact]
    public async Task Category_without_a_subgenre_is_a_single_tag()
    {
        File.WriteAllText(_file, "[Category]\nkicker=Sports\n");
        Assert.Equal(["Sports"], (await CatverIni.LoadAsync(_file))["kicker"]);
    }

    [Fact]
    public async Task Repeated_tags_on_one_line_are_collapsed_case_insensitively()
    {
        File.WriteAllText(_file, "[Category]\nfoo=Puzzle / puzzle / Drop\n");
        Assert.Equal(["Puzzle", "Drop"], (await CatverIni.LoadAsync(_file))["foo"]);
    }

    [Fact]
    public async Task Missing_file_yields_an_empty_map() =>
        Assert.Empty(await CatverIni.LoadAsync(_file + ".nope"));

    [Fact]
    public async Task Blank_and_comment_lines_are_ignored()
    {
        File.WriteAllText(_file, "[Category]\n\n; a comment\npuckman=Maze / Collect\nnovalue=\n");
        var genres = await CatverIni.LoadAsync(_file);
        Assert.Single(genres); // the empty-valued line contributes nothing
    }

    [Fact]
    public void FindFile_probes_next_to_mame_then_the_folders_subdir()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Null(CatverIni.FindFile(root.FullName));

            var folders = Directory.CreateDirectory(Path.Combine(root.FullName, "folders"));
            var inFolders = Path.Combine(folders.FullName, "catver.ini");
            File.WriteAllText(inFolders, "");
            Assert.Equal(inFolders, CatverIni.FindFile(root.FullName));

            var beside = Path.Combine(root.FullName, "catver.ini");
            File.WriteAllText(beside, "");
            Assert.Equal(beside, CatverIni.FindFile(root.FullName)); // next-to-exe wins
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
