using Attractor.Core.Diagnostics;

namespace Attractor.Core.Tests;

public class FileLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("attractor-log-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Writes_a_dated_file_with_the_message()
    {
        using (var log = new FileLog(_dir))
        {
            log.Info("hello");
            log.Error("boom", new InvalidOperationException("nope"));
        }
        var file = Directory.GetFiles(_dir, "attractor-*.log").Single();
        var text = File.ReadAllText(file);
        Assert.Contains("INF hello", text);
        Assert.Contains("ERR boom", text);
        Assert.Contains("nope", text); // exception detail included
    }

    [Fact]
    public void Respects_the_minimum_level()
    {
        using (var log = new FileLog(_dir, LogLevel.Warn))
        {
            log.Info("quiet");
            log.Warn("loud");
        }
        var text = File.ReadAllText(Directory.GetFiles(_dir, "*.log").Single());
        Assert.DoesNotContain("quiet", text);
        Assert.Contains("loud", text);
    }

    [Fact]
    public void Prunes_logs_older_than_the_retention_window()
    {
        var stale = Path.Combine(_dir, "attractor-20200101.log");
        File.WriteAllText(stale, "old");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

        using var log = new FileLog(_dir, retentionDays: 7); // prunes on construction
        Assert.False(File.Exists(stale));
    }
}
