using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2Dps.App.Dashboard;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Formatting;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Rendering;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

/// <summary>
/// The compact (out-of-combat) bar: offscreen renders, size budget, its content model, the in-place switch, the window
/// sizing (constructed, never shown) and the setting. No window is ever shown.
/// </summary>
public class CompactOverlayRenderTests
{
    private static readonly string Dir = Path.Combine(Repo.ScreensDir, "tests");

    private static ScreenCatalog.OverlayCase Case(string name) => ScreenCatalog.CompactCases().First(c => c.Name == name);

    private static void AssertDrawn(string path)
    {
        Assert.True(File.Exists(path), path);
        var bmp = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.True(bmp.PixelWidth > 150 && bmp.PixelHeight > 50, $"{path}: {bmp.PixelWidth}x{bmp.PixelHeight}");
        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = conv.PixelWidth * 4;
        var px = new byte[stride * conv.PixelHeight];
        conv.CopyPixels(px, stride, 0);
        var distinct = new HashSet<int>();
        for (int i = 0; i < px.Length && distinct.Count < 50; i += 4 * 31) distinct.Add(BitConverter.ToInt32(px, i));
        Assert.True(distinct.Count > 10, $"{path} looks blank");
    }

    [Theory]
    [InlineData("obsidian")]
    [InlineData("glacier")]
    [InlineData("daybreak")]
    [InlineData("arcade")]
    [InlineData("wisp")]
    public void Compact_cases_render_in_themes(string themeId) => Sta.Run(() =>
    {
        var theme = ThemeCatalog.Get(themeId);
        foreach (var c in ScreenCatalog.CompactCases())
            AssertDrawn(ScreenCatalog.RenderCompact(c, theme, Dir));
        AssertDrawn(ScreenCatalog.RenderCompactComparison(Case("waiting-zone-lastfight"),
            ScreenCatalog.OverlayCases().First(c => c.Name == "waiting-for-combat"), theme, Dir));
    });

    [Fact]
    public void Compact_bar_is_one_slim_row_and_much_smaller_than_the_expanded_overlay() => Sta.Run(() =>
    {
        var theme = ThemeCatalog.Obsidian;
        var expanded = ScreenCatalog.MeasureExpanded(ScreenCatalog.OverlayCases().First(c => c.Name == "waiting-for-combat"), theme);
        foreach (var c in ScreenCatalog.CompactCases())
        {
            var size = ScreenCatalog.MeasureCompact(c, theme);
            Assert.InRange(size.Width, CompactBarView.MinBarWidth, CompactBarView.MaxBarWidth);
            Assert.InRange(size.Height, 20, 30);
            Assert.True(size.Width * size.Height < 0.25 * expanded.Width * expanded.Height, $"{c.Name}: {size} vs {expanded}");
        }
        var normal = ScreenCatalog.MeasureCompact(Case("waiting-normal"), theme);
        Assert.InRange(normal.Height, 26, 30);
        Assert.True(expanded.Width >= 380 && expanded.Height > 120, expanded.ToString());
    });

    [Fact]
    public void Optional_parts_fit_the_width_budget_hint_first_then_zone_trims() => Sta.Run(() =>
    {
        var theme = ThemeCatalog.Obsidian;
        var (shortView, _) = Built("waiting-lastfight", theme);
        Assert.True(IsShown(shortView, "612K"), "the last-fight hint fits next to a short status");
        var (longView, _) = Built("long-zone", theme);
        var zone = Texts(longView).Single(t => t.Text.StartsWith("Sanctuary"));
        Assert.True(zone.ActualWidth < zone.DesiredSize.Width + 1 && zone.ActualWidth < 140, "a long zone name trims");
        Assert.Equal(CompactBarView.MaxBarWidth, Math.Ceiling(longView.ActualWidth));
    });

    private static (OverlayView View, Border Root) Built(string name, ThemeDefinition theme)
    {
        var (view, root) = ScreenCatalog.BuildCompact(Case(name), theme);
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        return (view, root);
    }

    private static bool IsShown(OverlayView view, string text)
    {
        var tb = Texts(view).Single(t => t.Text == text);
        var p = tb.TransformToAncestor(view.CompactBar).Transform(new Point(0, 0));
        return p.X >= 0 && p.X + tb.ActualWidth <= view.CompactBar.ActualWidth + 0.5;
    }

    private static IEnumerable<TextBlock> Texts(DependencyObject root) => Descendants<TextBlock>(root);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    [Fact]
    public void Switching_presentation_keeps_the_same_rows_and_view() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 380 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian);
        view.Update(PreviewData.LiveBoss(60), PreviewData.Status(), new OverlayViewOptions());
        OffscreenRenderer.Render(root, 380);
        var rowsBefore = Descendants<PlayerRowView>(view).ToList();
        view.SetCompact(true);
        Assert.True(view.IsCompact);
        Assert.Equal(Visibility.Visible, view.CompactBar.Visibility);
        view.Update(PreviewData.LiveBoss(61), PreviewData.Status(), new OverlayViewOptions());
        // Collapsed during a fight: the bar says so and shows the live DPS.
        Assert.StartsWith("In combat · ", view.CompactBar.Model!.Status);
        Assert.Equal("DPS", view.CompactBar.Model.HintLabel);
        view.SetCompact(false);
        OffscreenRenderer.Render(root, 380);
        Assert.Equal(Visibility.Collapsed, view.CompactBar.Visibility);
        Assert.Equal(rowsBefore, Descendants<PlayerRowView>(view).ToList());
    });

    [Fact]
    public void Collapse_button_follows_the_setting() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 380 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian);
        Button Collapse() => Descendants<Button>(view).Single(b => Equals(b.Content, OverlayView.IconCollapse));
        view.Update(PreviewData.WaitingForCombat(), PreviewData.Status(), new OverlayViewOptions { ShrinkWhenIdle = true });
        OffscreenRenderer.Render(root, 380);
        Assert.Equal(Visibility.Visible, Collapse().Visibility);
        int collapses = 0, expands = 0;
        view.CollapseRequested += () => collapses++;
        view.ExpandRequested += () => expands++;
        Collapse().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        view.CompactBar.Chevron.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(1, collapses);
        Assert.Equal(1, expands);
        view.Update(PreviewData.WaitingForCombat(), PreviewData.Status(), new OverlayViewOptions { ShrinkWhenIdle = false });
        Assert.Equal(Visibility.Collapsed, Collapse().Visibility);
    });

    [Fact]
    public void Toast_visibility_is_reported_for_the_presentation_policy() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false };
        Assert.False(view.ToastVisible);
        view.ShowToast("New personal best!", "Warden: 612K DPS");
        Assert.True(view.ToastVisible);
        view.HideToast();
        Assert.False(view.ToastVisible);
    });

    [Fact]
    public void Window_switches_sizing_in_place_and_remembers_the_expanded_width_and_anchor() => Sta.Run(() =>
    {
        // Constructed but never shown (no HWND): only the WPF sizing properties are exercised.
        var settings = new OverlaySettings { Left = 200, Top = 150, Width = 420 };
        var window = new OverlayWindow(settings);
        try
        {
            Assert.Equal(new Point(200, 150), window.Anchor);
            Assert.Equal(SizeToContent.Height, window.SizeToContent);
            window.SetPresentation(compact: true, keepInWorkArea: true);
            Assert.True(window.IsCompact);
            Assert.True(window.View.IsCompact);
            Assert.Equal(SizeToContent.WidthAndHeight, window.SizeToContent);
            Assert.Equal(0, window.MinWidth);
            Assert.Equal((200d, 150d), (window.Left, window.Top));
            window.SetPresentation(compact: false, keepInWorkArea: true);
            Assert.False(window.View.IsCompact);
            Assert.Equal(SizeToContent.Height, window.SizeToContent);
            Assert.Equal(420, window.Width);
            Assert.Equal(420, window.ExpandedWidth);
            Assert.Equal((200d, 150d), (window.Left, window.Top));
        }
        finally { window.Close(); }
    });

    // ───────────── Model ─────────────

    [Fact]
    public void Compact_model_texts_for_the_out_of_combat_states()
    {
        var waiting = CompactBarModel.Build(PreviewData.WaitingForCombat(), PreviewData.Status());
        Assert.Equal("Waiting for combat", waiting.Status);
        Assert.Equal(ThemeKeys.Positive, waiting.DotKey);
        Assert.Equal("Ashen Spire Sanctum", waiting.Zone);
        Assert.Null(waiting.Hint);

        var noGame = CompactBarModel.Build(PreviewData.WaitingForCombat() with { State = MeterState.Idle }, PreviewData.Status(CaptureState.WaitingForGame));
        Assert.Equal("Waiting for game", noGame.Status);

        var last = CompactBarModel.Build(PreviewData.WaitingForCombat(), PreviewData.Status() with { LastFightDps = 612_400 });
        Assert.Equal("612K", last.Hint);
        Assert.Equal("LAST", last.HintLabel);

        var flash = CompactBarModel.Build(PreviewData.WaitingForCombat(), PreviewData.Status() with { Flash = "Meter reset" });
        Assert.Equal("Meter reset", flash.Status);
        Assert.True(flash.IsFlash);

        // A finished fight stays on the bar: boss name, result and your DPS in that fight.
        var endedSnap = PreviewData.EndedKill();
        var ended = CompactBarModel.Build(endedSnap, PreviewData.Status());
        Assert.StartsWith(endedSnap.Target!.Name + " · Kill · ", ended.Status);
        Assert.Equal("YOU", ended.HintLabel);
        Assert.Equal(Fmt.Abbrev(endedSnap.Rows.First(r => r.IsLocal).Dps), ended.Hint);

        var blankZone = CompactBarModel.Build(PreviewData.WaitingForCombat() with { MapName = "  " }, PreviewData.Status());
        Assert.Null(blankZone.Zone);
    }

    // ───────────── Setting ─────────────

    [Fact]
    public void Shrink_setting_defaults_on_and_old_files_load_with_the_default()
    {
        string dir = TempDirectory();
        try
        {
            Assert.True(new AppSettings().Overlay.ShrinkWhenIdle);
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"Overlay\":{\"Width\":420,\"Locked\":true}}");
            var loaded = SettingsStore.LoadFrom(path);
            Assert.True(loaded.Overlay.ShrinkWhenIdle);
            Assert.Equal(420, loaded.Overlay.Width);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shrink_setting_round_trips_and_reaches_the_view_options(bool value)
    {
        string dir = TempDirectory();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var s = new AppSettings();
            s.Overlay.ShrinkWhenIdle = value;
            SettingsStore.SaveTo(path, s);
            Assert.Contains("\"ShrinkWhenIdle\"", File.ReadAllText(path));
            Assert.Equal(value, SettingsStore.LoadFrom(path).Overlay.ShrinkWhenIdle);
            Assert.Equal(value, s.Clone().Overlay.ShrinkWhenIdle);
            Assert.Equal(value, OverlayViewOptions.From(s, "1").ShrinkWhenIdle);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Settings_page_checkbox_toggles_and_saves_the_shrink_setting() => Sta.Run(() =>
    {
        string dir = TempDirectory();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            using var services = ServiceFactory.CreateDemo();
            int changed = 0;
            store.Changed += _ => changed++;
            var page = new SettingsPage(new DashboardContext { Settings = store, Services = services });
            OffscreenRenderer.Render(OffscreenRenderer.Themed(page, ThemeCatalog.Obsidian), 1000);
            var check = Descendants<CheckBox>(page).Single(c => Equals(c.Content, "Shrink overlay when not in combat"));
            Assert.True(check.IsChecked);
            check.IsChecked = false;
            Assert.False(store.Current.Overlay.ShrinkWhenIdle);
            Assert.False(SettingsStore.LoadFrom(path).Overlay.ShrinkWhenIdle);
            check.IsChecked = true;
            Assert.True(SettingsStore.LoadFrom(path).Overlay.ShrinkWhenIdle);
            Assert.Equal(2, changed); // the overlay controller re-evaluates on every change
        }
        finally { Directory.Delete(dir, true); }
    });

    private static string TempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
