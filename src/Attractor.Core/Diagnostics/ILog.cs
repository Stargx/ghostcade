namespace Attractor.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Minimal logging seam so Core stays free of any concrete sink.</summary>
public interface ILog
{
    void Log(LogLevel level, string message, Exception? ex = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Log(LogLevel.Debug, message);
    public static void Info(this ILog log, string message) => log.Log(LogLevel.Info, message);
    public static void Warn(this ILog log, string message, Exception? ex = null) => log.Log(LogLevel.Warn, message, ex);
    public static void Error(this ILog log, string message, Exception? ex = null) => log.Log(LogLevel.Error, message, ex);
}

/// <summary>No-op sink — the default so nothing ever NREs on a missing logger.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Log(LogLevel level, string message, Exception? ex = null) { }
}
