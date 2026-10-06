namespace Aion2Dps.Contracts;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Minimal process-wide logger. Libraries log through it; the host (App/CLI) installs a sink (file, console, UI).
/// Thread-safe. Default sink writes to <see cref="System.Diagnostics.Trace"/>.
/// </summary>
public static class AppLog
{
    private static volatile Action<LogLevel, string, string>? _sink =
        (level, area, message) => System.Diagnostics.Trace.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {area}: {message}");

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Replace the sink (null = discard).</summary>
    public static void SetSink(Action<LogLevel, string, string>? sink) => _sink = sink;

    public static void Write(LogLevel level, string area, string message)
    {
        if (level < MinimumLevel) return;
        try { _sink?.Invoke(level, area, message); } catch { /* logging must never throw */ }
    }

    public static void Debug(string area, string message) => Write(LogLevel.Debug, area, message);
    public static void Info(string area, string message) => Write(LogLevel.Info, area, message);
    public static void Warn(string area, string message) => Write(LogLevel.Warn, area, message);
    public static void Error(string area, string message, Exception? ex = null) =>
        Write(LogLevel.Error, area, ex is null ? message : $"{message}: {ex}");
}
