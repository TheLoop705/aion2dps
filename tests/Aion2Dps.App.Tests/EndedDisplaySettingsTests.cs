using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aion2Dps.App.Dashboard;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Rendering;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

public class EndedDisplaySettingsTests
{
    [Fact]
    public void Existing_settings_without_a_display_delay_use_fifteen_seconds()
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"General\":{\"LivePlayerClock\":true}}");
            Assert.Equal(15, new AppSettings().General.EndedDisplaySeconds);
            Assert.Equal(15, SettingsStore.LoadFrom(path).General.EndedDisplaySeconds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(double.NaN, 15)]
    [InlineData(double.PositiveInfinity, 15)]
    [InlineData(double.NegativeInfinity, 15)]
    [InlineData(-5, 0)]
    [InlineData(125, 120)]
    [InlineData(20, 20)]
    public void Display_delay_normalization_is_finite_bounded_and_retains_custom_values(double input, double expected)
    {
        var settings = new AppSettings();
        settings.General.EndedDisplaySeconds = input;
        SettingsStore.Normalize(settings);
        Assert.Equal(expected, settings.General.EndedDisplaySeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(30)]
    public void Display_delay_survives_save_load_and_clone(double delay)
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = new AppSettings();
            settings.General.EndedDisplaySeconds = delay;
            SettingsStore.SaveTo(path, settings);
            Assert.Equal(delay, SettingsStore.LoadFrom(path).General.EndedDisplaySeconds);
            Assert.Equal(delay, settings.Clone().General.EndedDisplaySeconds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Demo_hides_finished_numbers_after_five_seconds_and_keeps_the_completed_encounter()
    {
        DateTime origin = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        using var services = ServiceFactory.CreateDemo(originUtc: origin);
        var engine = services.Engine;
        engine.Options.EndedDisplaySeconds = 5;
        int historyBefore = services.Store.Query(new FightQuery()).Count;
        var completed = new List<EncounterRecord>();
        engine.EncounterCompleted += record =>
        {
            completed.Add(record);
            services.Store.Save(record);
        };
        MeterSnapshot? ended = null;
        for (int seconds = 0; seconds <= 300; seconds++)
        {
            var snapshot = engine.GetSnapshot(origin.AddSeconds(seconds));
            if (snapshot.State == MeterState.Ended) { ended = snapshot; break; }
        }
        Assert.NotNull(ended);
        Assert.NotEmpty(ended.Rows);
        var record = Assert.Single(completed);
        var beforeExpiry = engine.GetSnapshot(record.EndUtc.AddSeconds(4.999));
        Assert.Equal(MeterState.Ended, beforeExpiry.State);
        Assert.NotEmpty(beforeExpiry.Rows);

        var expired = engine.GetSnapshot(record.EndUtc.AddSeconds(5));
        Assert.Equal(MeterState.WaitingForCombat, expired.State);
        Assert.Empty(expired.Rows);
        Assert.Null(expired.Target);
        Assert.Null(expired.EncounterId);
        Assert.Equal(0, expired.TotalDamage);
        var retained = engine.GetCurrentEncounter();
        Assert.NotNull(retained);
        Assert.Equal(record.Id, retained.Id);
        Assert.Equal(record.TotalDamage, retained.TotalDamage);
        Assert.Equal(record.TotalDamage, services.Store.Load(record.Id)!.TotalDamage);
        engine.GetSnapshot(record.EndUtc.AddSeconds(6));
        Assert.Single(completed);
        Assert.Equal(historyBefore + 1, services.Store.Query(new FightQuery()).Count);
    }

    [Fact]
    public void Settings_slider_applies_and_saves_delay_changes_including_keep_until_next_fight() => Sta.Run(() =>
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            store.Current.General.EndedDisplaySeconds = 20;
            using var services = ServiceFactory.CreateDemo();
            var appliedDelays = new List<double>();
            var context = new DashboardContext
            {
                Settings = store,
                Services = services,
                ApplyServiceSettings = () => appliedDelays.Add(store.Current.General.EndedDisplaySeconds),
            };
            var page = new SettingsPage(context);
            var root = OffscreenRenderer.Themed(page, ThemeCatalog.Obsidian);
            OffscreenRenderer.Render(root, 1000);
            var title = Assert.Single(Descendants<TextBlock>(page), text => text.Text == "Clear finished fights after");
            var field = Assert.IsType<StackPanel>(title.Parent);
            var slider = Assert.Single(Descendants<Slider>(field));
            Assert.Equal(0, slider.Minimum);
            Assert.Equal(120, slider.Maximum);
            Assert.Equal(20, slider.Value);
            Assert.Empty(appliedDelays); // Opening settings does not silently change a custom delay.
            Assert.Contains(Descendants<TextBlock>(field), text => text.Text == "20 s");

            slider.Value = 30;
            Assert.Equal(30, store.Current.General.EndedDisplaySeconds);
            Assert.Equal(new[] { 30d }, appliedDelays);
            Assert.Contains(Descendants<TextBlock>(field), text => text.Text == "30 s");
            Assert.Equal(30, SettingsStore.LoadFrom(path).General.EndedDisplaySeconds);

            slider.Value = 0;
            Assert.Equal(0, store.Current.General.EndedDisplaySeconds);
            Assert.Equal(new[] { 30d, 0d }, appliedDelays);
            Assert.Contains(Descendants<TextBlock>(field), text => text.Text == "Keep until next fight");
            Assert.Equal(0, SettingsStore.LoadFrom(path).General.EndedDisplaySeconds);
        }
        finally { Directory.Delete(directory, true); }
    });

    private static string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aion2dps-ended-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
