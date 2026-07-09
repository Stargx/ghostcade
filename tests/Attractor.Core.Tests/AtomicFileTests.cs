using Attractor.Core.Configuration;

namespace Attractor.Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private string PathOf(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WriteAllText_creates_the_file_and_leaves_no_tmp_behind()
    {
        var path = PathOf("a.json");
        AtomicFile.WriteAllText(path, "hello");
        Assert.Equal("hello", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void WriteAllText_replaces_an_existing_file()
    {
        var path = PathOf("a.json");
        AtomicFile.WriteAllText(path, "old");
        AtomicFile.WriteAllText(path, "new");
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllLines_round_trips_through_ReadAllLines()
    {
        var path = PathOf("tags.txt");
        AtomicFile.WriteAllLines(path, ["alpha", "beta  with columns", "gamma"]);
        Assert.Equal(["alpha", "beta  with columns", "gamma"], File.ReadAllLines(path));
    }

    [Fact]
    public void WriteAllLines_with_no_lines_writes_an_empty_file()
    {
        var path = PathOf("empty.txt");
        AtomicFile.WriteAllLines(path, []);
        Assert.Empty(File.ReadAllLines(path));
    }

    [Fact]
    public void A_write_that_cannot_replace_the_target_leaves_the_old_content_intact()
    {
        // Windows refuses to rename over a file somebody holds open (no share-delete):
        // the write must fail as an exception the callers guard (best-effort rule),
        // never as a truncated or half-replaced target.
        var path = PathOf("a.json");
        AtomicFile.WriteAllText(path, "first");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "second"));
        }
        Assert.Equal("first", File.ReadAllText(path));
    }
}
