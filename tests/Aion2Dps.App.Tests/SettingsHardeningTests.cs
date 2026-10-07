using Aion2Dps.App.Settings;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

public class SettingsHardeningTests
{
    [Fact]
    public void Log_retention_runs_when_a_long_running_app_crosses_into_a_new_day()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aion2dps-log-rollover-" + Guid.NewGuid().ToString("N"));
        long ticks = new DateTime(2026, 10, 6, 12, 0, 0).Ticks;
        using var sink = new FileLogSink(directory, 14, 4096, () => new DateTime(Interlocked.Read(ref ticks)));
        try
        {
            sink.Write(LogLevel.Info, "Test", "first day");
            var first = Path.Combine(directory, "aion2dps-20261006.log");
            Assert.True(SpinWait.SpinUntil(() => File.Exists(first) && new FileInfo(first).Length > 0, TimeSpan.FromSeconds(3)));
            Interlocked.Exchange(ref ticks, new DateTime(2026, 10, 26, 12, 0, 0).Ticks);
            sink.Write(LogLevel.Info, "Test", "new day");
            var next = Path.Combine(directory, "aion2dps-20261026.log");
            Assert.True(SpinWait.SpinUntil(() => File.Exists(next), TimeSpan.FromSeconds(3)));
            Assert.False(File.Exists(first));
        }
        finally
        {
            sink.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Nonfinite_settings_cannot_disable_timeouts_or_break_window_layout(double value)
    {
        var settings = new AppSettings();
        settings.General.IdleTimeoutSeconds = value;
        settings.General.BossIdleTimeoutSeconds = value;
        settings.Appearance.CornerRadius = value;
        settings.Appearance.FontSize = value;
        settings.Dashboard = new WindowBounds { Left = 0, Top = 0, Width = value, Height = 600 };

        Assert.False(settings.Dashboard.IsValid);
        SettingsStore.Normalize(settings);

        Assert.Equal(10, settings.General.IdleTimeoutSeconds);
        Assert.Equal(30, settings.General.BossIdleTimeoutSeconds);
        Assert.Null(settings.Appearance.CornerRadius);
        Assert.Null(settings.Appearance.FontSize);
        Assert.Null(settings.Dashboard);
    }

    [Fact]
    public void Unknown_numeric_enum_values_use_supported_defaults()
    {
        var settings = new AppSettings();
        settings.Overlay.RowSize = (RowSize)999;
        settings.Overlay.DefaultMode = (MeterMode)999;
        settings.Overlay.View = (MeterView)999;
        settings.General.Language = (GameLanguage)999;
        settings.Appearance.BarStyle = (BarStyle)999;

        SettingsStore.Normalize(settings);

        Assert.Equal(RowSize.Compact, settings.Overlay.RowSize);
        Assert.Equal(MeterMode.BossOnly, settings.Overlay.DefaultMode);
        Assert.Equal(MeterView.Dps, settings.Overlay.View);
        Assert.Equal(GameLanguage.English, settings.General.Language);
        Assert.Equal(BarStyle.Gradient, settings.Appearance.BarStyle);
    }

    [Fact]
    public async Task Independent_settings_writers_do_not_share_a_temporary_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aion2dps-settings-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            {
                var settings = new AppSettings();
                settings.Overlay.Width = 400 + i;
                SettingsStore.SaveTo(path, settings);
            })));
            Assert.InRange(SettingsStore.LoadFrom(path).Overlay.Width, 400, 423);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.False(File.Exists(path + ".bad"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Logging_can_race_shutdown_without_losing_accepted_lines_or_throwing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aion2dps-log-shutdown-" + Guid.NewGuid().ToString("N"));
        var sink = new FileLogSink(directory);
        try
        {
            sink.Write(LogLevel.Info, "Test", "accepted before shutdown");
            await Task.WhenAll(Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) sink.Write(LogLevel.Info, "Test", "racing shutdown");
            }), Task.Run(sink.Dispose));
            sink.Dispose();
            sink.Write(LogLevel.Info, "Test", "after shutdown");
            var text = File.ReadAllText(Assert.Single(Directory.GetFiles(directory)));
            Assert.Contains("accepted before shutdown", text);
            Assert.DoesNotContain("after shutdown", text);
        }
        finally
        {
            sink.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
