using System.Windows;
using System.Windows.Controls;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

public class BehaviourTests
{
    private static readonly FakeGameData Gd = new();

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    [Fact]
    public void BreakdownFallsBackToLocalPlayerForUnknownId() => Sta.Run(() =>
    {
        var view = new BreakdownView(SampleData.BossFight(3, 60), 424242, Gd);
        Assert.Equal(1001u, view.CombatantEntityId);
    });

    [Fact]
    public void PinnedBreakdownRequestsCompareWhenAnotherPlayerIsPicked() => Sta.Run(() =>
    {
        var record = SampleData.BossFight(3, 60);
        var view = new BreakdownView(record, 1001, Gd) { IsPinned = true };
        _ = Render.Host(view, true, 1000, 800);
        (uint, uint)? requested = null;
        view.CompareRequested += (a, b) => requested = (a, b);
        var picker = FindChild<ComboBox>(view)!;
        picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().First(i => (uint)i.Tag == 1003);
        Sta.DoEvents();
        Assert.Equal((1001u, 1003u), requested);
        Assert.Equal(1001u, view.CombatantEntityId);
    });

    [Fact]
    public void UnpinnedPickerSwitchesPlayer() => Sta.Run(() =>
    {
        var record = SampleData.BossFight(3, 60);
        var view = new BreakdownView(record, 1001, Gd);
        _ = Render.Host(view, false, 1000, 800);
        uint? changed = null;
        view.CombatantChanged += id => changed = id;
        var picker = FindChild<ComboBox>(view)!;
        picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().First(i => (uint)i.Tag == 1002);
        Sta.DoEvents();
        Assert.Equal(1002u, view.CombatantEntityId);
        Assert.Equal(1002u, changed);
    });

    [Fact]
    public void RibbonZoomAndSkillHighlight() => Sta.Run(() =>
    {
        var record = SampleData.BossFight(3, 90);
        var ribbon = new RotationRibbon();
        ribbon.SetData(record, 1002, Gd);
        _ = Render.Host(ribbon, true, 700, 150);
        double pps = ribbon.EffectivePixelsPerSecond;
        double width = ribbon.ContentWidth;
        ribbon.Zoom = 2;
        Assert.Equal(pps * 2, ribbon.EffectivePixelsPerSecond, 3);
        Assert.True(ribbon.ContentWidth > width);
        ribbon.HighlightSkillGroup = 14_010_000;
        Assert.Equal(14_010_000u, ribbon.HighlightSkillGroup);
        ribbon.ScrollToTime(30);
        Assert.True(ribbon.HorizontalOffset > 0);
    });

    [Fact]
    public void EncounterReportRaisesPlayerOpened() => Sta.Run(() =>
    {
        var view = new EncounterReportView(SampleData.BossFight(3, 60), Gd) { OpenBreakdownOnDoubleClick = false };
        uint? opened = null;
        view.PlayerOpened += id => opened = id;
        view.OpenPlayer(1003);
        Assert.Equal(1003u, opened);
        Assert.NotNull(view.DamageChart);
        Assert.NotNull(view.HpChart);
    });

    [Fact]
    public void EmptyAndMinimalRecordsRenderWithoutErrors() => Sta.Run(() =>
    {
        var empty = new EncounterRecord { Kind = EncounterKind.Trash, DurationSeconds = 1, StartUtc = DateTime.UtcNow };
        var solo = new EncounterRecord
        {
            Kind = EncounterKind.Training, DurationSeconds = 10, StartUtc = DateTime.UtcNow,
            Combatants = { new CombatantRecord { EntityId = 5, Name = "Solo", Class = CharacterClass.Brawler, IsLocal = true } },
        };
        foreach (var r in new[] { empty, solo })
        {
            var report = new EncounterReportView(r, Gd);
            Render.Host(report, true, 900, 700);
            var pvp = new PvpReviewView(r, Gd);
            Render.Host(pvp, true, 900, 700);
            var bd = new BreakdownView(r, 5, Gd);
            var host = Render.Host(bd, false, 900, 700);
            foreach (var tab in Enum.GetValues<BreakdownTab>())
            {
                bd.SelectedTab = tab;
                Render.Layout(host, 900, 700);
                Render.Snap(host);
            }
        }
    });

    [Fact]
    public void HistoryQueriesOffTheUiThreadAndFilters() => Sta.Run(() =>
    {
        var store = FakeFightStore.CreateSample(DateTime.UtcNow);
        var view = new HistoryView();
        _ = Render.Host(view, true, 1400, 900);
        view.Initialize(store, Gd);
        Sta.Wait(view.PendingRefresh);
        Assert.NotEqual(Environment.CurrentManagedThreadId, store.LastQueryThreadId);
        int all = view.Fights.Count;
        Assert.True(all >= 15);

        Sta.Wait(view.SetFiltersAsync(kind: EncounterKind.Pvp));
        Assert.All(view.Fights, f => Assert.Equal(EncounterKind.Pvp, f.Kind));
        Assert.Equal(2, view.Fights.Count);

        Sta.Wait(view.SetFiltersAsync(killsOnly: true, rangeDays: 30));
        Assert.All(view.Fights, f => Assert.Equal(EncounterOutcome.Kill, f.Outcome));
        Assert.All(view.Fights, f => Assert.True(f.StartUtc >= DateTime.UtcNow.AddDays(-31)));

        Sta.Wait(view.SetFiltersAsync(character: "Mirelle"));
        Assert.Single(view.Fights);
    });

    [Fact]
    public void HistoryStacksTheReportBelowTheListWhenNarrow() => Sta.Run(() =>
    {
        var view = new HistoryView();
        var host = Render.Host(view, true, 800, 700);
        Render.Layout(host, 800, 700);
        Assert.True(view.IsStacked);
        host.Width = 1400;
        host.Height = 900;
        Render.Layout(host, 1400, 900);
        Assert.False(view.IsStacked);
    });

    [Fact]
    public void HistoryDeletesAfterConfirmation() => Sta.Run(() =>
    {
        var store = FakeFightStore.CreateSample(DateTime.UtcNow);
        var view = new HistoryView();
        _ = Render.Host(view, true, 1400, 900);
        view.Initialize(store, Gd);
        Sta.Wait(view.PendingRefresh);
        int before = view.Fights.Count;
        var target = view.Fights[0].Id;
        Sta.Wait(view.SelectFightAsync(target));
        Assert.Equal(target, view.SelectedFightId);
        Guid? deleted = null;
        view.FightDeleted += id => deleted = id;
        view.RequestDelete();
        var ok = view.ConfirmDeleteAsync();
        Sta.Wait(ok);
        Assert.True(ok.Result);
        Assert.Equal(target, deleted);
        Assert.Null(store.Load(target));
        Assert.Equal(before - 1, view.Fights.Count);
        Assert.Null(view.SelectedFightId);
        Assert.Null(view.DetailView);
    });

    [Fact]
    public void TrendsSwitchCharacter() => Sta.Run(() =>
    {
        var store = FakeFightStore.CreateSample(DateTime.UtcNow);
        var view = new TrendsView();
        _ = Render.Host(view, true, 1000, 800);
        view.Initialize(store, Gd);
        Sta.Wait(view.PendingWork);
        Sta.Wait(view.SelectCharacterAsync("Mirelle"));
        Assert.Equal("Mirelle", view.Character);
        Assert.Single(view.Summaries);
        Assert.Equal(SampleData.BossMorrow, view.SelectedBoss);
        Assert.Single(view.Chart!.Points);
    });
}
