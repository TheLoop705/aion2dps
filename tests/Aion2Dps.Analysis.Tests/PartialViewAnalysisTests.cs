using System.Windows;
using System.Windows.Controls;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

/// <summary>Report of an open-world boss fight where only part of the damage was visible (EncounterRecord.PartialView).</summary>
public class PartialViewAnalysisTests
{
    private static readonly FakeGameData Gd = new();

    /// <summary>The sample fight re-cut as a world boss joined mid-fight: you + one party member with direct hits, two
    /// strangers seen only through DoT ticks, spawn (NPC code) missed.</summary>
    internal static EncounterRecord PartialFight()
    {
        var r = SampleData.BossFight(5, 120);
        var friendly = r.Combatants.Where(c => c.Kind == CombatantKind.Player).ToList();
        var me = friendly.Single(c => c.IsLocal);
        var mate = friendly.First(c => !c.IsLocal);
        mate.IsPartyMember = true;
        foreach (var c in friendly.Where(c => c != me && c != mate))
        {
            c.IsPartyMember = false;
            c.Quality = new HitQualityStats { DotTicks = 12, DotDamage = c.Damage };
        }
        long max = 45_634_088;
        foreach (var c in friendly) c.Contribution = (double)c.Damage / max;
        r.BossNpcCode = null;
        r.Bosses = [];
        r.BossMaxHp = max;
        r.PartialView = true;
        r.PartialViewReason = "decoded damage explains 5.4 % of the boss HP lost";
        r.VisibleDamageRatio = 0.054;
        r.HpCheck = new HpCheckResult { DecodedDamage = 2_454_086, HpLost = max, Ratio = 0.0538, Passed = false };
        return r;
    }

    [Fact]
    public void Visible_friendly_puts_you_and_the_party_first_and_folds_dot_only_strangers()
    {
        var r = PartialFight();
        var rows = ChartData.VisibleFriendly(r, out var folded);
        Assert.Equal(2, rows.Count);
        Assert.True(rows.All(c => c.IsLocal || c.IsPartyMember));
        Assert.Equal(r.Combatants.Count(c => c.Kind == CombatantKind.Player) - 2, folded.Count);
        Assert.All(folded, c => Assert.Equal(0, c.Quality.Hits));

        r.PartialView = false; // a normal fight lists everyone
        Assert.Equal(ChartData.Friendly(r).Count(), ChartData.VisibleFriendly(r, out var none).Count);
        Assert.Empty(none);
    }

    [Fact]
    public void Visible_friendly_keeps_force_members_with_the_group_not_with_the_strangers()
    {
        var r = PartialFight();
        var forceMember = r.Combatants.First(c => c.Kind == CombatantKind.Player && !c.IsLocal && !c.IsPartyMember);
        forceMember.IsForceMember = true; // another party of your force, seen only through DoT ticks
        var rows = ChartData.VisibleFriendly(r, out var folded);
        Assert.Contains(forceMember, rows);
        Assert.DoesNotContain(forceMember, folded);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Unnamed_boss_title_uses_its_max_hp()
    {
        var r = PartialFight();
        Assert.Equal("Boss (45.6M HP)", ChartData.Title(r, Gd)); // the sample map is not an overworld map
        Assert.Equal("World boss (45.6M HP)", BossLabel.Unnamed(r.BossMaxHp, openWorld: true));
        Assert.Equal("Open world (101007)", BossLabel.UnnamedMap(101007, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Report_explains_the_partial_view_instead_of_a_failed_hp_check(bool dark) => Sta.Run(() =>
    {
        var r = PartialFight();
        var view = new EncounterReportView(r, Gd) { OpenBreakdownOnDoubleClick = false };
        Render.Host(view, dark, 1100, 1000);
        var texts = Texts(view);
        Assert.Contains(texts, t => t.StartsWith("Partial view: only part of the damage is visible", StringComparison.Ordinal));
        Assert.Contains("partial view", texts); // HP check tile subtitle, not "check the capture"
        Assert.DoesNotContain("check the capture", texts);
        Assert.Contains(texts, t => string.Equals(t, "Visible DPS", StringComparison.OrdinalIgnoreCase));
        Render.Save(view.RenderToBitmap(), $"report_partial_view_{Render.Theme(dark)}");
    });

    private static List<string> Texts(DependencyObject root)
    {
        var list = new List<string>();
        void Walk(DependencyObject o)
        {
            if (o is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) list.Add(tb.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(o))
                if (child is DependencyObject d) Walk(d);
        }
        Walk(root);
        return list;
    }
}
