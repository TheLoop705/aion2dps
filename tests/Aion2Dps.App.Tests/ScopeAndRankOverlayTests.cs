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

/// <summary>Top 5 with your own row lifted into the last slot, and the fight context / group scope on the overlay.</summary>
public class ScopeAndRankOverlayTests
{
    /// <summary>A force of 12 at a field boss: you rank 9th, so the 5th row is you with rank 9.</summary>
    private static MeterSnapshot ForceFight(bool partial = false) => new()
    {
        State = MeterState.InCombat,
        Elapsed = TimeSpan.FromSeconds(90),
        Context = FightContext.FieldBoss,
        Scope = GroupScope.Force,
        GroupSize = 12,
        PartialView = partial,
        Rows = Enumerable.Range(1, 12).Select(i => new PlayerRow
        {
            EntityId = (uint)i, Name = "P" + i, Class = CharacterClass.Gladiator, Damage = 1_300_000 - i * 100_000, Dps = 13_000 - i * 1_000,
            DamageShare = 0.08, IsLocal = i == 9, IsPartyMember = i is 2 or 5 or 9, IsForceMember = i is not (2 or 5 or 9),
            Hits = partial && i != 9 ? 0 : 100,
        }).ToList(),
    };

    [Fact]
    public void The_5th_row_is_you_with_your_real_rank_when_you_are_outside_the_top_5()
    {
        var rows = OverlayView.ComputeRows(ForceFight(), new OverlayViewOptions());
        Assert.Equal(new uint[] { 1, 2, 3, 4, 9 }, rows.Select(r => r.Row.EntityId));
        Assert.Equal(new[] { 1, 2, 3, 4, 9 }, rows.Select(r => r.Rank));
        Assert.True(rows[^1].Lifted);
        Assert.True(rows.Take(4).All(r => !r.Lifted));
    }

    [Fact]
    public void Every_view_keeps_your_row_at_the_bottom_when_you_are_outside_the_top()
    {
        var snap = ForceFight() with
        {
            Rows = ForceFight().Rows.Select(r => r with { DamageTaken = r.IsLocal ? 0 : 1_000 * r.EntityId, Healing = r.IsLocal ? 0 : 500 * r.EntityId }).ToList(),
        };
        foreach (var view in new[] { MeterView.Dps, MeterView.Total, MeterView.Taken, MeterView.Heal })
        {
            var rows = OverlayView.ComputeRows(snap, new OverlayViewOptions { View = view, MaxRows = 3 });
            Assert.Equal(3, rows.Count);
            Assert.True(rows[^1].Row.IsLocal, view.ToString());
            Assert.True(rows[^1].Lifted, view.ToString());
            Assert.True(rows[^1].Rank > 3, view.ToString());
        }
    }

    [Fact]
    public void Partial_view_damage_lists_only_players_whose_damage_is_actually_sent()
    {
        // Open-world boss in a force: the server sends only your own direct hits, so the force's DoT-only numbers are
        // not shown; a player whose direct hits do arrive (real data) still is.
        var snap = ForceFight(partial: true) with
        {
            Rows = ForceFight(partial: true).Rows
                .Append(new PlayerRow { EntityId = 50, Name = "Visible", Damage = 5_000_000, Dps = 50_000, Hits = 30 })
                .Append(new PlayerRow { EntityId = 99, Name = "Others (40)", Damage = 900_000, Dps = 9_000, AggregateCount = 40 })
                .ToList(),
        };
        foreach (var view in new[] { MeterView.Dps, MeterView.Total })
        {
            var rows = OverlayView.ComputeRows(snap, new OverlayViewOptions { View = view, MaxRows = 24 });
            Assert.Equal(new uint[] { 50, 9 }, rows.Select(r => r.Row.EntityId));
            Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Rank));
        }

        // Healing and damage taken still list the whole force.
        var heal = OverlayView.ComputeRows(snap with { Rows = snap.Rows.Select(r => r with { Healing = 1_000 }).ToList() },
            new OverlayViewOptions { View = MeterView.Heal, MaxRows = 24 });
        Assert.Contains(heal, r => r.Row.IsForceMember);
    }

    [Theory]
    [InlineData(FightContext.FieldBoss, GroupScope.Force, 20, "Field boss · Force 20")]
    [InlineData(FightContext.DungeonBoss, GroupScope.Party, 5, "Dungeon boss · Party 5")]
    [InlineData(FightContext.TrainingDummy, GroupScope.Solo, 1, "Training dummy · Solo")]
    [InlineData(FightContext.Pvp, GroupScope.Everyone, 0, "PvP")]
    [InlineData(FightContext.None, GroupScope.Everyone, 0, "")]
    public void Context_text_names_the_fight_and_who_is_ranked(FightContext context, GroupScope scope, int size, string expected)
    {
        var s = new MeterSnapshot { State = MeterState.InCombat, Context = context, Scope = scope, GroupSize = size };
        Assert.Equal(expected, OverlayView.ContextText(s));
    }

    [Fact]
    public void Bars_caption_mentions_a_partial_view_and_stays_empty_out_of_combat()
    {
        Assert.Equal("Field boss · Force 12 · only your damage is sent", OverlayView.BarsCaption(ForceFight(partial: true)));
        Assert.Equal("Field boss · Force 12", OverlayView.BarsCaption(ForceFight()));
        Assert.Equal("", OverlayView.BarsCaption(ForceFight() with { State = MeterState.WaitingForCombat }));
    }

    [Fact]
    public void Bars_only_shows_the_caption_and_your_real_rank_without_a_rank_column() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 300 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian, backdrop: OffscreenRenderer.SceneBackdrop());
        var options = OverlayViewOptions.From(new AppSettings(), "test");
        view.Update(ForceFight() with { Context = FightContext.DungeonBoss }, PreviewData.Status(), options);
        SaveForReview(OffscreenRenderer.Render(root, 300), "bars-only-force-lifted.png");

        var caption = Descendants<TextBlock>(view).Single(t => t.Text.StartsWith("Dungeon boss", StringComparison.Ordinal));
        Assert.Equal("Dungeon boss · Force 12", caption.Text);
        for (DependencyObject? d = caption; d is not null && d != view; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement el) Assert.Equal(Visibility.Visible, el.Visibility);

        var rows = Descendants<PlayerRowView>(view).OrderBy(r => r.Y).ToList();
        Assert.Equal(5, rows.Count);
        var mine = rows[^1];
        Assert.True(mine.Row.IsLocal);
        // Still name + DPS only (no rank column), but the name says where you really are.
        Assert.Equal(3, mine.Children.OfType<Grid>().Single().ColumnDefinitions.Count);
        Assert.Contains(Descendants<TextBlock>(mine), t => t.Text == "#9 P9");
    });

    [Fact]
    public void Bars_only_at_an_open_world_boss_shows_only_you_and_says_why() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 300 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian, backdrop: OffscreenRenderer.SceneBackdrop());
        view.Update(ForceFight(partial: true), PreviewData.Status(), OverlayViewOptions.From(new AppSettings(), "test"));
        SaveForReview(OffscreenRenderer.Render(root, 300), "bars-only-field-boss-force.png");

        Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Field boss · Force 12 · only your damage is sent");
        var row = Assert.Single(Descendants<PlayerRowView>(view));
        Assert.True(row.Row.IsLocal);
        Assert.Contains(Descendants<TextBlock>(row), t => t.Text == "P9"); // rank 1 of the shown rows: no "#9" lift
    });

    [Fact]
    public void Classic_overlay_shows_the_real_rank_and_the_context_in_the_header() => Sta.Run(() =>
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 380 };
        var root = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian);
        var options = new OverlayViewOptions { ShowRank = true };
        var snap = ForceFight() with
        {
            MapName = "Altgard",
            Target = new TargetInfo { EntityId = 9000, Name = "High Commander Lagta", IsBoss = true, Hp = 50, MaxHp = 100, HpFraction = 0.5 },
        };
        view.Update(snap, PreviewData.Status(), options);
        SaveForReview(OffscreenRenderer.Render(root, 380), "classic-force-lifted.png");

        var mine = Descendants<PlayerRowView>(view).OrderBy(r => r.Y).Last();
        Assert.Contains(Descendants<TextBlock>(mine), t => t.Text == "9");
        Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "ALTGARD · FIELD BOSS");
        Assert.Contains(Descendants<Button>(view), b => b.Content as string == "FORCE 12");
    });

    private static void SaveForReview(System.Windows.Media.Imaging.BitmapSource bitmap, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-tests", "scope");
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
