using System.Windows.Media.Imaging;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Rendering;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

/// <summary>Offscreen rendering (no windows are shown). PNGs land in artifacts/screens for review.</summary>
public class RenderingTests
{
    private static readonly string Dir = Path.Combine(Repo.ScreensDir, "tests");

    private static void AssertNotBlank(string path)
    {
        Assert.True(File.Exists(path), path);
        var bmp = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.True(bmp.PixelWidth > 100 && bmp.PixelHeight > 100);
        // The image must contain more than one colour (i.e. something was drawn).
        var conv = new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = conv.PixelWidth * 4;
        var px = new byte[stride * conv.PixelHeight];
        conv.CopyPixels(px, stride, 0);
        var distinct = new HashSet<int>();
        for (int i = 0; i < px.Length && distinct.Count < 50; i += 4 * 97) distinct.Add(BitConverter.ToInt32(px, i));
        Assert.True(distinct.Count > 10, $"{path} looks blank");
    }

    [Theory]
    [InlineData("obsidian")]
    [InlineData("glacier")]
    [InlineData("daybreak")]
    [InlineData("arcade")]
    public void Overlay_cases_render_in_themes(string themeId) => Sta.Run(() =>
    {
        var theme = ThemeCatalog.Get(themeId);
        foreach (var c in ScreenCatalog.OverlayCases())
            AssertNotBlank(ScreenCatalog.RenderOverlay(c, theme, Dir));
    });

    [Fact]
    public void Overlay_updates_in_place_without_rebuilding_rows() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 380 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian);
        var opts = new OverlayViewOptions();
        view.Update(PreviewData.LiveBoss(60), PreviewData.Status(), opts);
        OffscreenRenderer.Render(root, 380);
        var before = FindRows(view);
        view.Update(PreviewData.LiveBoss(61), PreviewData.Status(), opts);
        view.Update(PreviewData.LiveBoss(62), PreviewData.Status(), opts);
        var after = FindRows(view);
        Assert.Equal(5, before.Count);
        Assert.Equal(before, after); // same instances: rows are updated, not recreated
        // Switching to PvP swaps the layout, and back again.
        view.Update(PreviewData.Pvp(), PreviewData.Status(), opts);
        Assert.Empty(FindRows(view));
        view.Update(PreviewData.LiveBoss(63), PreviewData.Status(), opts);
        Assert.Equal(5, FindRows(view).Count);
    });

    private static List<object> FindRows(System.Windows.DependencyObject root)
    {
        var list = new List<object>();
        void Walk(System.Windows.DependencyObject d)
        {
            if (d.GetType().Name == "PlayerRowView") list.Add(d);
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return list;
    }

    [Fact]
    public void Overlay_renders_with_custom_appearance() => Sta.Run(() =>
    {
        var custom = new AppearanceSettings { ThemeId = "obsidian", Accent = "#3FA7FF", Background = "#101820", CornerRadius = 0, ColorMode = RowColorMode.SingleColor, SingleColor = "#3FA7FF", BarStyle = BarStyle.Solid };
        var c = ScreenCatalog.OverlayCases().First(x => x.Name == "live-boss");
        AssertNotBlank(ScreenCatalog.RenderOverlay(c with { Name = "custom-palette" }, ThemeCatalog.Obsidian, Dir, custom));
    });

    [Theory]
    [InlineData("Meter")]
    [InlineData("History")]
    [InlineData("Trends")]
    [InlineData("Character")]
    [InlineData("Appearance")]
    [InlineData("Settings")]
    [InlineData("About")]
    public void Dashboard_pages_render(string page) => Sta.Run(() =>
    {
        AssertNotBlank(ScreenCatalog.RenderDashboardPage(page, ThemeCatalog.Obsidian, Dir));
    });

    [Fact]
    public void Dashboard_shell_and_breakdown_render_in_other_themes() => Sta.Run(() =>
    {
        AssertNotBlank(ScreenCatalog.RenderDashboardPage("Meter", ThemeCatalog.Abyssal, Dir, shell: true));
        AssertNotBlank(ScreenCatalog.RenderDashboardPage("Appearance", ThemeCatalog.Wisp, Dir, shell: true));
        AssertNotBlank(ScreenCatalog.RenderBreakdown(ThemeCatalog.Ember, Dir));
    });

    [Fact]
    public void Encounter_report_and_compare_views_build() => Sta.Run(() =>
    {
        var record = PreviewData.LiveRecord();
        var gd = new FakeGameData();
        var report = Integration.AnalysisBridge.CreateEncounterReport(record, gd);
        var root = OffscreenRenderer.Themed(report, ThemeCatalog.Obsidian, backdrop: ColorBrush(ThemeCatalog.Obsidian.DashboardBackground));
        var path = OffscreenRenderer.RenderToPng(root, 620, Path.Combine(Dir, "encounter-report-obsidian.png"), height: 700);
        AssertNotBlank(path);
        var trends = OffscreenRenderer.Themed(Integration.AnalysisBridge.CreateTrendsView(new InMemoryFightStore(), gd), ThemeCatalog.Obsidian);
        OffscreenRenderer.Render(trends, 600); // empty store renders a hint, does not throw
        Assert.NotNull(Integration.AnalysisBridge.CreateArmoryView());
    });

    private static System.Windows.Media.Brush ColorBrush(System.Windows.Media.Color c) => new System.Windows.Media.SolidColorBrush(c);

    [Fact]
    public void App_icon_renders() => Sta.Run(() =>
    {
        var bmp = Controls.AppIcon.Render(32);
        Assert.Equal(32, bmp.PixelWidth);
    });
}
