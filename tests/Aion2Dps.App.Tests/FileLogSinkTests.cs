using Aion2Dps.App.Infrastructure;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

/// <summary>Regression (review: robustness): the log directory must not grow without bound.</summary>
public class FileLogSinkTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Old_daily_files_are_deleted_and_recent_ones_kept()
    {
        string dir = NewDir();
        try
        {
            var today = new DateTime(2026, 10, 6);
            foreach (int daysAgo in new[] { 0, 3, 14, 15, 90 })
                File.WriteAllText(Path.Combine(dir, $"aion2dps-{today.AddDays(-daysAgo):yyyyMMdd}.log"), "x");
            File.WriteAllText(Path.Combine(dir, "aion2dps-notes.log"), "not a daily file");
            File.WriteAllText(Path.Combine(dir, "other-20200101.log"), "not ours");

            Assert.Equal(2, FileLogSink.DeleteOldFiles(dir, 14, today));
            var left = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "aion2dps-20260922.log", "aion2dps-20261003.log", "aion2dps-20261006.log", "aion2dps-notes.log", "other-20200101.log" }, left);
            Assert.Equal(0, FileLogSink.DeleteOldFiles(dir, 0, today)); // 0 = keep everything
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_daily_file_is_capped()
    {
        string dir = NewDir();
        try
        {
            using (var sink = new FileLogSink(dir, 14, 4096))
            {
                for (int i = 0; i < 1000; i++) sink.Write(LogLevel.Warn, "Capture", $"Cannot open adapter {i}: access denied");
            }

            var file = Assert.Single(Directory.GetFiles(dir));
            long size = new FileInfo(file).Length;
            Assert.InRange(size, 1, 4096 + 200); // the cap plus one notice line
            Assert.Contains("further lines of today are dropped", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
