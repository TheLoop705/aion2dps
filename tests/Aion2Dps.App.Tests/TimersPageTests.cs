using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aion2Dps.App.Dashboard;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Rendering;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Aion2Dps.App.Timers;
using Aion2Dps.Combat.Timers;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

/// <summary>Timers page, star defaults/overrides, tray alerts and the idle overlay's "next timer" line.</summary>
public class TimersPageTests
{
    private static TimerService Service(AppServices services, TimerSettings settings, string? path = null) =>
        new(services.FieldBosses, services.GameData, () => settings, path);

    [Fact]
    public void Page_renders_events_and_demo_bosses_and_star_toggles_save() => Sta.Run(() =>
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            using var services = ServiceFactory.CreateDemo();
            var timers = Service(services, store.Current.Timers);
            var page = new TimersPage(new DashboardContext { Settings = store, Services = services, Timers = timers });
            page.Refresh();
            var bitmap = OffscreenRenderer.Render(OffscreenRenderer.Themed(page, ThemeCatalog.Obsidian), 1000);
            SaveForReview(bitmap, "timers-page.png");

            var texts = Descendants<TextBlock>(page).Select(t => t.Text).ToList();
            Assert.Contains("Spacetime Rift", texts);
            Assert.Contains("The Verdant Tyrant", texts);
            Assert.Contains("VERDANT REACH", texts);
            Assert.Contains(texts, t => t.StartsWith("respawn 12 h", StringComparison.Ordinal));

            // Priority bosses are starred by default (and badged), the others are not.
            Assert.Contains("TOP PRIORITY", texts);
            var rows = timers.BossRows(services.Now());
            Assert.True(rows.Single(r => r.Name == "The Verdant Tyrant").Starred);
            Assert.Equal("The Verdant Tyrant", rows.OrderByDescending(r => r.Priority).First().Name);
            Assert.True(rows.Single(r => r.Name == "Bloomrot Behemoth").Starred);
            Assert.False(rows.Single(r => r.Name == "Captain Orrevan").Starred);
            var grazer = rows.Single(r => r.Name == "Thornwing Harrier");
            Assert.False(grazer.Starred);

            // Star the 30 min boss through its star button: saved as an override.
            var star = Descendants<Button>(page).Where(b => b.ToolTip as string == "Star: alert before this timer")
                .Single(b => RowName(b) == "Thornwing Harrier");
            star.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(store.Current.Timers.Stars[grazer.Key]);
            store.Save();
            Assert.True(SettingsStore.LoadFrom(path).Timers.Stars[grazer.Key]);
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public void Own_timers_and_overrides_from_settings()
    {
        using var services = ServiceFactory.CreateDemo();
        var settings = new TimerSettings();
        var timers = Service(services, settings);
        var now = new DateTime(2026, 10, 10, 11, 30, 0, DateTimeKind.Utc);   // 13:30 CEST

        // Move the rift grid an hour later and hide the Shugo Festival.
        settings.Entries.Add(new TimerEntry { Id = "rift", Start = "03:00" });
        settings.Entries.Add(new TimerEntry { Id = "shugo", Enabled = false });
        // Own daily timer (Saturday 20:00 server time) and a 90 min countdown.
        settings.Entries.Add(new TimerEntry { Id = "custom-guild", Custom = true, Name = "Guild boss run", Start = "20:00", Days = [DayOfWeek.Saturday] });
        settings.Entries.Add(new TimerEntry { Id = "custom-cd", Custom = true, Name = "Lawa", CountdownMinutes = 90 });
        settings.Entries.Add(new TimerEntry { Id = "custom-bad", Custom = true, Name = "Broken", Start = "25:99" });

        var events = timers.Events();
        Assert.Equal(new TimeSpan(3, 0, 0), events.Single(e => e.Id == "rift").Start);
        Assert.DoesNotContain(events, e => e.Id == "shugo");
        Assert.DoesNotContain(events, e => e.Id == "custom-bad");
        var rows = timers.EventRows(now);
        var rift = rows.Single(r => r.Name == "Spacetime Rift");
        Assert.Equal(new DateTime(2026, 10, 10, 13, 0, 0, DateTimeKind.Utc), rift.AtUtc);     // 15:00 CEST
        var guild = rows.Single(r => r.Name == "Guild boss run");
        Assert.True(guild.Starred);
        Assert.Equal(new DateTime(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc), guild.AtUtc);

        var cd = rows.Single(r => r.Name == "Lawa");
        Assert.True(cd.IsCountdown);
        Assert.Equal("Ready", cd.Status);
        Assert.True(timers.StartCountdown(cd.Key, now));
        var counting = timers.EventRows(now.AddMinutes(10)).Single(r => r.Name == "Lawa");
        Assert.Equal("Counting", counting.Status);
        Assert.Equal(now.AddMinutes(90), counting.AtUtc);
        Assert.Contains(timers.CollectAlerts(now.AddMinutes(87)), a => a.Title == "Lawa" && a.Text.StartsWith("Due in 3m", StringComparison.Ordinal));
        Assert.Contains(timers.CollectAlerts(now.AddMinutes(90).AddSeconds(2)), a => a.Title == "Lawa" && a.Text == "Countdown finished");
        Assert.Equal("Done", timers.EventRows(now.AddMinutes(91)).Single(r => r.Name == "Lawa").Status);
        Assert.True(timers.StopCountdown(cd.Key));
        Assert.Equal("Ready", timers.EventRows(now.AddMinutes(91)).Single(r => r.Name == "Lawa").Status);
    }

    [Fact]
    public void Settings_timer_editor_adds_saves_and_resets() => Sta.Run(() =>
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            using var services = ServiceFactory.CreateDemo();
            var timers = Service(services, store.Current.Timers);
            var page = new SettingsPage(new DashboardContext { Settings = store, Services = services, Timers = timers });
            var host = OffscreenRenderer.Themed(page, ThemeCatalog.Obsidian);
            SaveForReview(OffscreenRenderer.Render(host, 1200), "settings-timers.png");
            var editor = Descendants<TimerEditor>(page).Single();

            Click(Descendants<Button>(editor).Single(b => ButtonText(b) == "Add timer"));
            Click(Descendants<Button>(editor).Single(b => ButtonText(b) == "Add countdown"));
            SaveForReview(OffscreenRenderer.Render(host, 1200), "settings-timers-added.png");
            Assert.Equal(2, store.Current.Timers.Entries.Count(e => e.Custom));
            Assert.Single(store.Current.Timers.Entries, e => e.IsCountdown);

            // Edit the built-in rift time: first box with "02:00".
            var riftTime = Descendants<TextBox>(editor).First(t => t.Text == "02:00");
            riftTime.Text = "03:00";
            Assert.Equal("03:00", store.Current.Timers.Entries.Single(e => e.Id == "rift").Start);
            riftTime.Text = "3:7x";    // invalid: ignored
            Assert.Equal("03:00", store.Current.Timers.Entries.Single(e => e.Id == "rift").Start);

            store.Save();
            var loaded = SettingsStore.LoadFrom(path).Timers;
            Assert.Equal(3, loaded.Entries.Count);
            Assert.Equal(60, loaded.Entries.Single(e => e.IsCountdown).CountdownMinutes);

            // Reset the rift back to the built-in time.
            var reset = Descendants<Button>(editor).Single(b => b.ToolTip as string == "Back to the built-in time" && b.Visibility == Visibility.Visible);
            Click(reset);
            Assert.DoesNotContain(store.Current.Timers.Entries, e => e.Id == "rift");
        }
        finally { Directory.Delete(directory, true); }
    });

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static string? ButtonText(Button b) => b.Content as string
        ?? (b.Content as StackPanel)?.Children.OfType<TextBlock>().LastOrDefault()?.Text;

    [Fact]
    public void Alerts_fire_once_per_occurrence_for_starred_timers()
    {
        using var services = ServiceFactory.CreateDemo();
        var settings = new TimerSettings { AlertMinutesBefore = 5, ServerRegion = "EU" };
        var timers = Service(services, settings);
        var rift = timers.Data.Events.Single(e => e.Id == "rift");
        var start = TimerSchedule.Upcoming(rift, timers.ServerZone, DateTime.UtcNow, 1)[0];

        Assert.DoesNotContain(timers.CollectAlerts(start.AddMinutes(-10)), a => a.Title == "Spacetime Rift");
        var soon = timers.CollectAlerts(start.AddMinutes(-4));
        Assert.Contains(soon, a => a.Title == "Spacetime Rift" && a.Text.StartsWith("Starts in 4m", StringComparison.Ordinal));
        Assert.DoesNotContain(timers.CollectAlerts(start.AddMinutes(-3)), a => a.Title == "Spacetime Rift");

        settings.Stars[TimerService.EventKey(rift)] = false;
        var next = TimerSchedule.Upcoming(rift, timers.ServerZone, start, 1)[0];
        Assert.DoesNotContain(timers.CollectAlerts(next.AddMinutes(-4)), a => a.Title == "Spacetime Rift");

        settings.AlertsEnabled = false;
        Assert.Empty(timers.CollectAlerts(next.AddMinutes(-4)));
    }

    [Fact]
    public void Starred_boss_alerts_before_and_at_respawn()
    {
        using var services = ServiceFactory.CreateDemo();
        var settings = new TimerSettings { AlertMinutesBefore = 5 };
        var timers = Service(services, settings);
        var tyrant = services.FieldBosses.Snapshot().Single(t => t.Name == "The Verdant Tyrant");
        var r = tyrant.RespawnUtc!.Value;
        Assert.Contains(timers.CollectAlerts(r.AddMinutes(-2)), a => a.Title == "The Verdant Tyrant" && a.Text.StartsWith("Respawns in", StringComparison.Ordinal));
        Assert.Contains(timers.CollectAlerts(r.AddSeconds(5)), a => a.Title == "The Verdant Tyrant" && a.Text == "Respawned now");
        Assert.DoesNotContain(timers.CollectAlerts(r.AddSeconds(10)), a => a.Title == "The Verdant Tyrant");
    }

    [Fact]
    public void Boss_book_is_saved_and_restored()
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, TimerService.BookFileName);
            using var services = ServiceFactory.CreateDemo();
            var timers = Service(services, new TimerSettings(), path);
            timers.SaveIfChanged();
            Assert.True(File.Exists(path));

            var restored = new TimerService(new FieldBossTimerBook(services.GameData, timers.Data), services.GameData, () => new TimerSettings(), path);
            restored.Load(DateTime.UtcNow);
            Assert.Equal(services.FieldBosses.Snapshot(), restored.Book.Snapshot());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Idle_bar_shows_the_next_starred_timer()
    {
        var snap = MeterSnapshot.Empty with { State = MeterState.WaitingForCombat };
        var model = CompactBarModel.Build(snap, new OverlayStatus { NextTimer = "Spacetime Rift in 12m 00s" });
        Assert.Equal("Spacetime Rift in 12m 00s", model.Status);
        Assert.Equal("Waiting for combat", CompactBarModel.Build(snap, new OverlayStatus()).Status);
    }

    private static string? RowName(DependencyObject star)
    {
        var grid = VisualTreeHelper.GetParent(star);
        while (grid is not null and not Grid) grid = VisualTreeHelper.GetParent(grid);
        return grid is null ? null : Descendants<TextBlock>(grid).FirstOrDefault(t => t.FontSize == 13)?.Text;
    }

    private static void SaveForReview(System.Windows.Media.Imaging.BitmapSource bitmap, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-tests", "timers");
        Directory.CreateDirectory(dir);
        OffscreenRenderer.SavePng(bitmap, Path.Combine(dir, name));
    }

    private static string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aion2dps-timers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
