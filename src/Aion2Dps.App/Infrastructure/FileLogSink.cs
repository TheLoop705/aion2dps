using System.Collections.Concurrent;
using System.Text;

namespace Aion2Dps.App.Infrastructure;

/// <summary>
/// AppLog sink writing daily files (logs/aion2dps-yyyyMMdd.log) from a background thread, so logging never blocks the
/// capture or UI thread. Keeps the last <see cref="RecentCapacity"/> lines in memory for the dashboard.
/// </summary>
public sealed class FileLogSink : IDisposable
{
    public const int RecentCapacity = 300;

    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly Thread _writer;
    private StreamWriter? _stream;
    private DateTime _streamDate;

    public FileLogSink(string directory)
    {
        _directory = directory;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Aion2Dps log writer" };
        _writer.Start();
    }

    public IReadOnlyList<string> Recent => _recent.ToArray();

    public void Write(LogLevel level, string area, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {area}: {message}";
        _recent.Enqueue(line);
        while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _)) { }
        _queue.TryAdd(line);
    }

    /// <summary>Installs this sink into <see cref="AppLog"/> (also mirrors to Trace for debuggers).</summary>
    public void Install()
    {
        AppLog.SetSink((level, area, message) =>
        {
            Write(level, area, message);
            System.Diagnostics.Trace.WriteLine($"[{level}] {area}: {message}");
        });
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    EnsureStream();
                    _stream?.WriteLine(line);
                    if (_queue.Count == 0) _stream?.Flush();
                }
                catch { /* disk full / locked: drop the line, never crash */ }
            }
        }
        catch (ObjectDisposedException) { }
        finally
        {
            try { _stream?.Flush(); _stream?.Dispose(); } catch { }
        }
    }

    private void EnsureStream()
    {
        var today = DateTime.Now.Date;
        if (_stream is not null && _streamDate == today) return;
        _stream?.Dispose();
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"aion2dps-{today:yyyyMMdd}.log");
        _stream = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
        _streamDate = today;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }
}
