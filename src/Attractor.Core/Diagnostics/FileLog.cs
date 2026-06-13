namespace Attractor.Core.Diagnostics;

/// <summary>
/// Thread-safe rolling file log: one file per day (attractor-YYYYMMDD.log),
/// auto-flushed so a crash still leaves the trail, older than the retention
/// window pruned on startup. Shared-read so the file can be opened while live.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly string _dir;
    private readonly LogLevel _min;
    private readonly int _retentionDays;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _openDate;

    public FileLog(string logsDir, LogLevel min = LogLevel.Info, int retentionDays = 7)
    {
        _dir = logsDir;
        _min = min;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_dir);
        Prune();
    }

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        if (level < _min) return;
        try
        {
            lock (_gate)
            {
                var writer = EnsureWriter();
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Tag(level)} {message}");
                if (ex is not null)
                    writer.WriteLine(ex);
            }
        }
        catch (IOException) { /* logging must never throw into the app */ }
        catch (UnauthorizedAccessException) { }
    }

    private StreamWriter EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is null || today != _openDate)
        {
            _writer?.Dispose();
            var path = Path.Combine(_dir, $"attractor-{today:yyyyMMdd}.log");
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            _openDate = today;
        }
        return _writer;
    }

    private void Prune()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_dir, "attractor-*.log"))
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
