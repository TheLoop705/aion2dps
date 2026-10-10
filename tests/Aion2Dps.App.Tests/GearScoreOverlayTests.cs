using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Formatting;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Rendering;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

public class GearScoreOverlayTests
{
    [Theory]
    [InlineData(RowSize.Normal)]
    [InlineData(RowSize.Compact)]
    [InlineData(RowSize.Micro)]
    public void Gear_score_toggles_on_reused_rows_and_tracks_known_or_missing_values(RowSize size) => Sta.Run(() =>
    {
        var sample = PreviewData.LiveBoss(60);
        var snapshot = sample with
        {
            Rows = sample.Rows.Select((row, index) => row with { GearScore = index == 0 ? 825u : null }).ToArray(),
        };
        var view = new OverlayView { AnimationsEnabled = false, Width = 380 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian);
        var options = new OverlayViewOptions { RowSize = size, ShowGearScore = false };
        view.Update(snapshot, PreviewData.Status(), options);
        OffscreenRenderer.Render(root, 380);
        var originalRows = Descendants<PlayerRowView>(view).OrderBy(row => row.EntityId).ToArray();
        Assert.Equal(snapshot.Rows.Count, originalRows.Length);
        var knownRow = originalRows.Single(row => row.EntityId == snapshot.Rows[0].EntityId);
        var missingRow = originalRows.First(row => row.EntityId != knownRow.EntityId);
        int disabledColumnCount = RowContent(knownRow).ColumnDefinitions.Count;
        Assert.DoesNotContain(Descendants<TextBlock>(knownRow), text => text.Text == "825");
        Assert.DoesNotContain(Descendants<TextBlock>(view), text => text.Text == "GS" && IsShownInTree(text));

        options = options with { ShowGearScore = true };
        view.Update(snapshot, PreviewData.Status(), options);
        OffscreenRenderer.Render(root, 380);
        Assert.Equal(originalRows, Descendants<PlayerRowView>(view).OrderBy(row => row.EntityId).ToArray());
        Assert.Equal(disabledColumnCount + 1, RowContent(knownRow).ColumnDefinitions.Count);
        var score = Assert.Single(Descendants<TextBlock>(knownRow), text => text.Text == "825");
        Assert.Equal("Gear score: 825", score.ToolTip);
        int scoreColumn = Grid.GetColumn(score);
        var missingScore = Assert.Single(RowContent(missingRow).Children.OfType<TextBlock>(), text => Grid.GetColumn(text) == scoreColumn);
        Assert.Equal(Fmt.Dash, missingScore.Text);
        Assert.Equal("Gear score unknown", missingScore.ToolTip);

        if (size != RowSize.Micro)
        {
            var header = Assert.Single(Descendants<TextBlock>(view), text => text.Text == "GS");
            Assert.True(IsShownInTree(header));
            var headerGrid = Assert.IsType<Grid>(header.Parent);
            Assert.Equal(scoreColumn, Grid.GetColumn(header));
            Assert.Equal(RowContent(knownRow).ColumnDefinitions[scoreColumn].ActualWidth,
                headerGrid.ColumnDefinitions[scoreColumn].ActualWidth, 1);
            Assert.InRange(Math.Abs(score.TranslatePoint(new Point(score.ActualWidth, 0), view).X -
                header.TranslatePoint(new Point(header.ActualWidth, 0), view).X), 0, 1);
        }
        else
        {
            Assert.DoesNotContain(Descendants<TextBlock>(view), text => text.Text == "GS" && IsShownInTree(text));
        }

        string screenshot = Path.Combine(Repo.ScreensDir, "tests", $"overlay-gear-score-{size.ToString().ToLowerInvariant()}.png");
        OffscreenRenderer.RenderToPng(root, 380, screenshot);
        Assert.True(File.Exists(screenshot));

        // A later roster update must replace the value without creating a new row or leaving stale text.
        snapshot = snapshot with
        {
            Rows = snapshot.Rows.Select(row => row.EntityId == knownRow.EntityId ? row with { GearScore = 1493 } : row).ToArray(),
        };
        view.Update(snapshot, PreviewData.Status(), options);
        Assert.Equal("1493", score.Text);
        Assert.DoesNotContain(Descendants<TextBlock>(knownRow), text => text.Text == "825");
        snapshot = snapshot with
        {
            Rows = snapshot.Rows.Select(row => row.EntityId == knownRow.EntityId ? row with { GearScore = null } : row).ToArray(),
        };
        view.Update(snapshot, PreviewData.Status(), options);
        Assert.Equal(Fmt.Dash, score.Text);

        view.Update(snapshot, PreviewData.Status(), options with { ShowGearScore = false });
        OffscreenRenderer.Render(root, 380);
        Assert.Equal(originalRows, Descendants<PlayerRowView>(view).OrderBy(row => row.EntityId).ToArray());
        Assert.Equal(disabledColumnCount, RowContent(knownRow).ColumnDefinitions.Count);
        Assert.DoesNotContain(RowContent(knownRow).Children.OfType<TextBlock>(), text => ReferenceEquals(text, score));
        Assert.DoesNotContain(Descendants<TextBlock>(view), text => text.Text == "GS" && IsShownInTree(text));
    });

    [Fact]
    public void Gear_score_preference_survives_restart_and_projects_to_overlay_options()
    {
        var settings = new AppSettings();
        settings.Overlay.BarsOnly = false; // the bars-only overlay shows name + DPS only (checked below)
        Assert.False(settings.Overlay.ShowGearScore);
        Assert.False(OverlayViewOptions.From(settings, "test").ShowGearScore);
        settings.Overlay.ShowGearScore = true;
        Assert.True(settings.Clone().Overlay.ShowGearScore);
        string path = Path.Combine(Path.GetTempPath(), "aion2dps-tests", Guid.NewGuid().ToString("N"), "settings.json");
        SettingsStore.SaveTo(path, settings);
        var restored = SettingsStore.LoadFrom(path);
        Assert.True(restored.Overlay.ShowGearScore);
        Assert.True(OverlayViewOptions.From(restored, "test").ShowGearScore);
        restored.Overlay.BarsOnly = true;
        Assert.False(OverlayViewOptions.From(restored, "test").ShowGearScore);
    }

    private static Grid RowContent(PlayerRowView row) => Assert.Single(row.Children.OfType<Grid>());

    // IsVisible also depends on a window/PresentationSource; these views deliberately render without a window.
    private static bool IsShownInTree(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement visual && visual.Visibility != Visibility.Visible) return false;
        return true;
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
