using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Rendering;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

/// <summary>The bars-only overlay: just the DPS bars (name + DPS), no header/footer/box.</summary>
public class BarsOnlyOverlayTests
{
    private static OverlayViewOptions BarsOnly() => OverlayViewOptions.From(new AppSettings(), "test");

    [Fact]
    public void Bars_only_is_the_default_and_keeps_only_name_and_dps()
    {
        var o = BarsOnly();
        Assert.True(o.BarsOnly);
        Assert.Equal(MeterView.Dps, o.View);
        Assert.False(o.ShowRank || o.ShowClassEmblem || o.ShowTotal || o.ShowContribution || o.ShowColumnHeader);
    }

    [Fact]
    public void Bars_only_hides_header_footer_and_box_but_shows_rows() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 300 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian, backdrop: OffscreenRenderer.SceneBackdrop());
        view.Update(PreviewData.LiveBoss(60), PreviewData.Status(), BarsOnly());
        var bitmap = OffscreenRenderer.Render(root, 300);
        SaveForReview(bitmap, "bars-only-live.png");

        Assert.True(view.BarsOnlyActive);
        Assert.Equal(Visibility.Collapsed, view.DragHandle.Visibility);
        Assert.Equal(Visibility.Collapsed, view.CompactBar.Visibility);
        var rows = Descendants<PlayerRowView>(view).ToList();
        Assert.NotEmpty(rows);
        // Name + DPS: a spacer, the name and one number column.
        foreach (var row in rows) Assert.Equal(3, row.Children.OfType<Grid>().Single().ColumnDefinitions.Count);
    });

    [Fact]
    public void Bars_only_without_rows_falls_back_to_the_slim_bar() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 300 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian);
        view.Update(PreviewData.WaitingForCombat(), PreviewData.Status(), BarsOnly());
        OffscreenRenderer.Render(root, 300);
        Assert.Equal(Visibility.Visible, view.CompactBar.Visibility);
        Assert.Empty(Descendants<PlayerRowView>(view));
    });

    [Fact]
    public void Finished_fight_on_the_slim_bar_renders() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 300 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian, backdrop: OffscreenRenderer.SceneBackdrop());
        view.Update(PreviewData.EndedKill(), PreviewData.Status(), BarsOnly());
        view.SetCompact(true);
        SaveForReview(OffscreenRenderer.Render(root, 300), "bars-only-ended-slim.png");
        Assert.Equal("YOU", view.CompactBar.Model!.HintLabel);
    });

    [Fact]
    public void Clicking_a_bar_raises_row_clicked_for_that_row() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 300 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian);
        view.Update(PreviewData.LiveBoss(60), PreviewData.Status(), BarsOnly());
        OffscreenRenderer.Render(root, 300);
        PlayerRow? clicked = null;
        view.RowClicked += (r, _) => clicked = r;
        var second = Descendants<PlayerRowView>(view).OrderBy(r => r.Y).ElementAt(1);
        view.ClickRowAt(new Point(10, second.Y + 2), ctrl: false);
        Assert.Equal(second.Row.EntityId, clicked?.EntityId);
    });

    private static void SaveForReview(System.Windows.Media.Imaging.BitmapSource bitmap, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-tests", "bars-only");
        Directory.CreateDirectory(dir);
        OffscreenRenderer.SavePng(bitmap, Path.Combine(dir, name));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
