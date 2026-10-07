using System.Collections.Concurrent;
using System.Text;

namespace Aion2Dps.App.Infrastructure;

/// <summary>
/// AppLog sink writing daily files (logs/aion2dps-yyyyMMdd.log) from a background thread, so logging never blocks the
/// capture or UI thread. Keeps the last <see cref="RecentCapacity"/> lines in memory for the dashboard. Files older than
/// the retention period are deleted at startup and each daily file is capped (the meter may sit in the tray for weeks).
/// </summary>
public sealed class FileLogSink : IDisposable
{
    public const int RecentCapacity = 300;
    public const int DefaultRetentionDays = 14;
    public const long DefaultMaxFileBytes = 32L * 1024 * 1024;
    private const string FilePrefix = "aion2dps-";

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly long _maxFileBytes;
    private readonly Func<DateTime> _clock;
    private long _fileBytes;
    private bool _capped;
    private int _disposed;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly Thread _writer;
    private StreamWriter? _stream;
    private DateTime _streamDate;

    public FileLogSink(string directory) : this(directory, DefaultRetentionDays, DefaultMaxFileBytes)
    {
    }

    /// <param name="retentionDays">Daily files older than this many days are deleted at startup (0 = keep all).</param>
    /// <param name="maxFileBytes">Lines beyond this size of one daily file are dropped (one notice line is written).</param>
    public FileLogSink(string directory, int retentionDays, long maxFileBytes)
        : this(directory, retentionDays, maxFileBytes, () => DateTime.Now)
    {
    }

    internal FileLogSink(string directory, int retentionDays, long maxFileBytes, Func<DateTime> clock)
    {
        _directory = directory;
        _retentionDays = retentionDays;
        _maxFileBytes = maxFileBytes > 0 ? maxFileBytes : long.MaxValue;
        _clock = clock;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Aion2Dps log writer" };
        _writer.Start();
    }

    public IReadOnlyList<string> Recent => _recent.ToArray();

    public void Write(LogLevel level, string area, string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        string line = $"{_clock():yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {area}: {message}";
        _recent.Enqueue(line);
        while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _)) { }
        try { _queue.TryAdd(line); }
        catch (InvalidOperationException) { /* shutdown can finish the queue after the check above */ }
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
        try { DeleteOldFiles(_directory, _retentionDays, _clock().Date); } catch { /* best effort */ }
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    EnsureStream();
                    if (_stream is null) continue;
                    long size = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                    if (_fileBytes + size > _maxFileBytes)
                    {
                        if (!_capped)
                        {
                            _capped = true;
                            _stream.WriteLine($"{_clock():yyyy-MM-dd HH:mm:ss.fff} [Warn ] Log: this file reached " +
                                              $"{_maxFileBytes / (1024 * 1024)} MB; further lines of today are dropped.");
                            _stream.Flush();
                        }
                        continue;
                    }

                    _stream.WriteLine(line);
                    _fileBytes += size;
                    if (_queue.Count == 0) _stream.Flush();
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
        var today = _clock().Date;
        if (_stream is not null && _streamDate == today) return;
        _stream?.Dispose();
        if (_streamDate != default)
        {
            try { DeleteOldFiles(_directory, _retentionDays, today); } catch { /* disk problems must not stop logging */ }
        }
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{FilePrefix}{today:yyyyMMdd}.log");
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _fileBytes = fs.Length;
        _capped = false;
        _stream = new StreamWriter(fs, new UTF8Encoding(false));
        _streamDate = today;
    }

    /// <summary>Deletes <c>aion2dps-yyyyMMdd.log</c> files dated more than <paramref name="retentionDays"/> days before
    /// <paramref name="today"/>. Returns how many were deleted.</summary>
    internal static int DeleteOldFiles(string directory, int retentionDays, DateTime today)
    {
        if (retentionDays <= 0 || !Directory.Exists(directory)) return 0;
        var cutoff = today.Date.AddDays(-retentionDays);
        int deleted = 0;
        foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(path).Substring(FilePrefix.Length);
            if (!DateTime.TryParseExact(stamp, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date) || date >= cutoff) continue;
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch { /* in use or read-only: try again next start */ }
        }

        return deleted;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }
}
