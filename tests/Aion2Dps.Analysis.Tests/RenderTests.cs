using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

/// <summary>Renders every chart, view and window content offscreen (dark and light) to artifacts/screens/*.png.</summary>
public class RenderTests
{
    private static readonly Lazy<EncounterRecord> BossLazy = new(() => SampleData.BossFight());
    private static readonly Lazy<EncounterRecord> PvpLazy = new(() => SampleData.PvpSession());
    private static readonly Lazy<EncounterRecord> WipeLazy = new(() => SampleData.WipeFight());
    private static EncounterRecord Boss => BossLazy.Value;
    private static EncounterRecord Pvp => PvpLazy.Value;
    private static readonly FakeGameData Gd = new();

    private static void AssertDrawn(System.Windows.Media.Imaging.BitmapSource bmp) =>
        Assert.True(Render.DistinctColors(bmp) > 8, "render looks blank");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Charts(bool dark) => Sta.Run(() =>
    {
        string th = Render.Theme(dark);

        var timeline = new DpsTimelineChart();
        timeline.SetEncounter(Boss, 1001);
        var host = Render.Host(timeline, dark, 900, 240);
        timeline.SetHover(new Point(520, 120));
        Render.Layout(host, 900, 240);
        AssertDrawn(Render.Snap(host));
        Render.Save(Render.Snap(host), $"chart_timeline_{th}");

        var cumulative = new CumulativeDamageChart();
        cumulative.SetEncounter(Boss);
        host = Render.Host(cumulative, dark, 900, 280);
        cumulative.SetHover(new Point(600, 150));
        Render.Layout(host, 900, 280);
        Render.Save(Render.Snap(host), $"chart_cumulative_{th}");
        Assert.Equal(Boss.BossMaxHp, cumulative.BossMaxHp);
        Assert.NotNull(cumulative.KillTimeSeconds);

        var ribbon = new RotationRibbon();
        ribbon.SetData(Boss, 1001, Gd);
        host = Render.Host(ribbon, dark, 900, 160);
        ribbon.SetHover(new Point(118, 46));
        Render.Layout(host, 900, 160);
        AssertDrawn(Render.Snap(host));
        Render.Save(Render.Snap(host), $"chart_ribbon_{th}");
        Assert.True(ribbon.ContentWidth > 900);
        ribbon.HorizontalOffset = 1e9;
        Assert.Equal(ribbon.ContentWidth - ribbon.ActualWidth, ribbon.HorizontalOffset, 3);
        ribbon.HorizontalOffset = 2400;
        ribbon.SetHover(null);
        Render.Layout(host, 900, 160);
        Render.Save(Render.Snap(host), $"chart_ribbon_panned_{th}");

        var hp = new BossHpChart();
        hp.SetEncounter(WipeLazy.Value);
        host = Render.Host(hp, dark, 900, 160);
        hp.SetHover(new Point(300, 80));
        Render.Layout(host, 900, 160);
        Render.Save(Render.Snap(host), $"chart_bosshp_wipe_{th}");
        Assert.Single(hp.ResetTimes);

        var store = FakeFightStore.CreateSample(DateTime.UtcNow);
        var trend = new TrendChart();
        trend.SetData(store.GetBossTrend(SampleData.BossVorgrath, "Kaelith"), store.GetPersonalBest(SampleData.BossVorgrath, "Kaelith"));
        host = Render.Host(trend, dark, 900, 260);
        trend.SetHover(new Point(450, 120));
        Render.Layout(host, 900, 260);
        Render.Save(Render.Snap(host), $"chart_trend_{th}");
        Assert.True(trend.Best >= trend.Median);

        var bars = new StackPanel { Margin = new Thickness(10) };
        foreach (var v in new[] { 0.0, 0.25, 0.7, 1.0 }) bars.Children.Add(new MiniBar { Value = v, Width = 200, Margin = new Thickness(0, 4, 0, 4) });
        var tw = new TwoWayBar { Width = 200, Height = 12 };
        tw.SetValues(800, 300, 1000);
        bars.Children.Add(tw);
        var tiles = new WrapPanel();
        foreach (var c in ClassInfo.All) tiles.Children.Add(new ClassEmblem { Class = c, Width = 32, Height = 32, Margin = new Thickness(3) });
        var st = new SkillTile { Width = 32, Height = 32, Margin = new Thickness(3) };
        st.SetSkill("Ferocious Strike", 11_020_030, CharacterClass.Gladiator);
        tiles.Children.Add(st);
        bars.Children.Add(tiles);
        host = Render.Host(bars, dark, 420, 160);
        Render.Save(Render.Snap(host), $"chart_small_{th}");
        Assert.Equal("FS", st.Initials);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BreakdownWindowAllTabs(bool dark) => Sta.Run(() =>
    {
        string th = Render.Theme(dark);
        var window = new BreakdownWindow(Boss, 1001, Gd);
        var view = window.View;
        window.Content = null;
        Assert.Contains("Kaelith", window.Title);
        var host = Render.Host(view, dark, 1000, 900);
        foreach (var tab in Enum.GetValues<BreakdownTab>())
        {
            view.SelectedTab = tab;
            Render.Layout(host, 1000, 900);
            var bmp = Render.Snap(host);
            AssertDrawn(bmp);
            Render.Save(bmp, $"breakdown_{tab.ToString().ToLowerInvariant()}_{th}");
            var full = view.RenderToBitmap();
            Assert.True(full.PixelHeight >= 600);
            Render.Save(full, $"breakdown_{tab.ToString().ToLowerInvariant()}_full_{th}");
        }
        view.SelectedTab = BreakdownTab.Dps;
        Render.Layout(host, 1000, 900);
        var dpsFull = view.RenderToBitmap();
        Assert.True(dpsFull.PixelHeight > 900, "copy-as-image must include rows scrolled out of view");

        // Other players, including the summoner and the healer.
        foreach (uint id in new uint[] { 1004, 1005 })
        {
            view.ShowCombatant(id);
            Assert.Equal(id, view.CombatantEntityId);
            Render.Layout(host, 1000, 900);
            Render.Save(view.RenderToBitmap(), $"breakdown_player{id}_full_{th}");
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompareWindowSideBySide(bool dark) => Sta.Run(() =>
    {
        var window = new CompareWindow(Boss, 1001, 1002, Gd);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        Assert.True(window.Pinned.IsPinned);
        Assert.False(window.Other.IsPinned);
        Assert.Equal(1002u, window.Other.CombatantEntityId);
        var host = Render.Host(content, dark, 1680, 900);
        Render.Save(Render.Snap(host), $"compare_dps_{Render.Theme(dark)}");
        window.SelectTab(BreakdownTab.Accuracy);
        Render.Layout(host, 1680, 900);
        Render.Save(Render.Snap(host), $"compare_accuracy_{Render.Theme(dark)}");
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EncounterReport(bool dark) => Sta.Run(() =>
    {
        var view = new EncounterReportView(Boss, Gd);
        var host = Render.Host(view, dark, 1100, 1000);
        AssertDrawn(Render.Snap(host));
        Render.Save(Render.Snap(host), $"report_{Render.Theme(dark)}");
        Render.Save(view.RenderToBitmap(), $"report_full_{Render.Theme(dark)}");

        var wipe = new EncounterReportView(WipeLazy.Value, Gd);
        host = Render.Host(wipe, dark, 1100, 1000);
        Render.Save(wipe.RenderToBitmap(), $"report_wipe_full_{Render.Theme(dark)}");
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PvpReview(bool dark) => Sta.Run(() =>
    {
        var view = new PvpReviewView(Pvp, Gd);
        var host = Render.Host(view, dark, 1100, 1000);
        Assert.Equal(7001u, view.SelectedOpponentId);
        AssertDrawn(Render.Snap(host));
        Render.Save(Render.Snap(host), $"pvp_{Render.Theme(dark)}");
        view.SelectOpponent(7003);
        Render.Layout(host, 1100, 1000);
        Render.Save(view.RenderToBitmap(), $"pvp_full_{Render.Theme(dark)}");

        var breakdown = new BreakdownView(Pvp, 7001, Gd);
        host = Render.Host(breakdown, dark, 1000, 900);
        Render.Save(Render.Snap(host), $"breakdown_pvp_enemy_{Render.Theme(dark)}");
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void History(bool dark) => Sta.Run(() =>
    {
        var store = FakeFightStore.CreateSample(DateTime.UtcNow);
        var view = new HistoryView();
        var host = Render.Host(view, dark, 1560, 980);
        view.Initialize(store, Gd);
        Sta.Wait(view.PendingRefresh);
        Assert.NotEmpty(view.Fights);
        var kill = view.Fights.First(f => f.Kind == EncounterKind.Boss && f.Outcome == EncounterOutcome.Kill);
        Sta.Wait(view.SelectFightAsync(kill.Id));
        Assert.IsType<EncounterReportView>(view.DetailView);
        Render.Layout(host, 1560, 980);
        AssertDrawn(Render.Snap(host));
        Render.Save(Render.Snap(host), $"history_{Render.Theme(dark)}");

        var pvp = view.Fights.First(f => f.Kind == EncounterKind.Pvp);
        Sta.Wait(view.SelectFightAsync(pvp.Id));
        Assert.IsType<PvpReviewView>(view.DetailView);
        view.RequestDelete();
        Render.Layout(host, 1560, 980);
        Render.Save(Render.Snap(host), $"history_pvp_delete_{Render.Theme(dark)}");
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Trends(bool dark) => Sta.Run(() =>
    {
        var store = FakeFightStore.CreateSample(DateTime.UtcNow);
        var view = new TrendsView();
        var host = Render.Host(view, dark, 1100, 900);
        view.Initialize(store, Gd);
        Sta.Wait(view.PendingWork);
        Assert.Equal("Kaelith", view.Character);
        Assert.NotEmpty(view.Summaries);
        Assert.NotNull(view.SelectedBoss);
        Sta.Wait(view.SelectBossAsync(SampleData.BossVorgrath));
        Assert.NotNull(view.Chart);
        Render.Layout(host, 1100, 900);
        AssertDrawn(Render.Snap(host));
        Render.Save(Render.Snap(host), $"trends_{Render.Theme(dark)}");
    });
}
