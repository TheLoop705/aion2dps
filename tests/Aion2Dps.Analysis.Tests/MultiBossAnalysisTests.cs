using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

/// <summary>Report / history display of multi-boss encounters (two bosses fought at the same time).</summary>
public class MultiBossAnalysisTests
{
    private static readonly FakeGameData Gd = new();

    /// <summary>The sample boss fight re-cut as a split pull: Ilsa (40 % of the HP, dies at 60 %) + Vorgrath (primary).</summary>
    internal static EncounterRecord MultiBossFight(bool failCheckOnFirst = false)
    {
        var r = SampleData.BossFight(5, 180);
        long total = r.BossMaxHp!.Value;
        long ilsaMax = (long)(total * 0.4), vorgrathMax = total - ilsaMax;
        double d = r.DurationSeconds, ilsaKill = d * 0.6;
        var ilsa = new BossResult
        {
            NpcCode = SampleData.BossIlsa, EntityId = 9101, MaxHp = ilsaMax, MaxHpTrusted = true, HpStart = ilsaMax, HpEnd = 0,
            Killed = true, KillTimeSeconds = ilsaKill, EngagedSeconds = 0, LastHitSeconds = ilsaKill, DamageTaken = ilsaMax,
            HpCheck = new HpCheckResult { DecodedDamage = ilsaMax, HpLost = ilsaMax, Ratio = failCheckOnFirst ? 1.08 : 1.0, Passed = !failCheckOnFirst },
        };
        var vorgrath = new BossResult
        {
            NpcCode = SampleData.BossVorgrath, EntityId = SampleData.BossEntity, IsPrimary = true, MaxHp = vorgrathMax, MaxHpTrusted = true,
            HpStart = vorgrathMax, HpEnd = 0, Killed = true, KillTimeSeconds = d, EngagedSeconds = 12, LastHitSeconds = d,
            DamageTaken = vorgrathMax, HpCheck = new HpCheckResult { DecodedDamage = vorgrathMax, HpLost = vorgrathMax, Ratio = 1.0, Passed = true },
        };
        for (int s = 0; s <= (int)d; s++)
        {
            ilsa.HpTimeline.Add(new HpSample(s, s >= ilsaKill ? 0 : (long)(ilsaMax * (1 - s / ilsaKill))));
            if (s >= 12) vorgrath.HpTimeline.Add(new HpSample(s, (long)(vorgrathMax * (1 - (s - 12) / (d - 12)))));
        }
        foreach (var c in r.Combatants.Where(c => c.Kind != CombatantKind.EnemyPlayer))
        {
            long toIlsa = (long)(c.Damage * 0.4);
            ilsa.DamageByCombatant.Add(new BossDamageShare { EntityId = c.EntityId, Damage = toIlsa, Contribution = (double)toIlsa / ilsaMax });
            vorgrath.DamageByCombatant.Add(new BossDamageShare { EntityId = c.EntityId, Damage = c.Damage - toIlsa, Contribution = (double)(c.Damage - toIlsa) / vorgrathMax });
            c.AllBossesDamage = c.Damage;
            c.BossDamage = c.Damage - toIlsa;
            c.Contribution = (double)c.Damage / total;
        }
        ilsa.DamageByCombatant.Sort((a, b) => b.Damage.CompareTo(a.Damage));
        vorgrath.DamageByCombatant.Sort((a, b) => b.Damage.CompareTo(a.Damage));
        r.Bosses = [ilsa, vorgrath];
        r.BossNpcCode = SampleData.BossVorgrath;
        r.BossMaxHp = vorgrathMax;
        r.BossHpTimeline = vorgrath.HpTimeline;
        r.HpCheck = vorgrath.HpCheck;
        r.OverallHpCheck = new HpCheckResult { DecodedDamage = total, HpLost = total, Ratio = failCheckOnFirst ? 1.03 : 1.0, Passed = !failCheckOnFirst };
        return r;
    }

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

    [Fact]
    public void Title_joins_the_bosses_in_engagement_order()
    {
        var r = MultiBossFight();
        Assert.True(ChartData.IsMultiBoss(r));
        Assert.Equal("Pyre Warden Ilsa + Vorgrath the Ashen", ChartData.Title(r, Gd));

        var single = SampleData.BossFight();
        Assert.False(ChartData.IsMultiBoss(single));
        Assert.Equal("Vorgrath the Ashen", ChartData.Title(single, Gd));
        single.Bosses = [new BossResult { NpcCode = SampleData.BossVorgrath, IsPrimary = true }];
        Assert.Equal("Vorgrath the Ashen", ChartData.Title(single, Gd));
    }

    [Fact]
    public void Boss_hp_chart_draws_one_line_per_boss() => Sta.Run(() =>
    {
        var hp = new BossHpChart();
        hp.SetEncounter(MultiBossFight(), Gd);
        Assert.Equal(2, hp.SeriesCount);
        Assert.Equal(new[] { "Pyre Warden Ilsa", "Vorgrath the Ashen" }, hp.SeriesNames);

        // Without game data (or for a single boss) the classic single-line chart is kept.
        hp.SetEncounter(MultiBossFight());
        Assert.Equal(1, hp.SeriesCount);
        hp.SetEncounter(SampleData.BossFight(), Gd);
        Assert.Equal(1, hp.SeriesCount);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Report_shows_the_combined_title_per_boss_hp_checks_and_hp_chart(bool dark) => Sta.Run(() =>
    {
        var r = MultiBossFight(failCheckOnFirst: true);
        var view = new EncounterReportView(r, Gd) { OpenBreakdownOnDoubleClick = false };
        var host = Render.Host(view, dark, 1100, 1000);
        var texts = Texts(view);
        Assert.Contains("Pyre Warden Ilsa + Vorgrath the Ashen", texts);
        Assert.Contains(texts, t => t.StartsWith("Pyre Warden Ilsa · HP check 108", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Vorgrath the Ashen · HP check 100", StringComparison.Ordinal));
        Assert.Contains("Bosses", texts);
        Assert.Contains("PRIMARY", texts);
        Assert.Contains("Bosses' HP", texts);
        Assert.Contains(texts, t => t.StartsWith("2 bosses' HP", StringComparison.Ordinal));
        Assert.NotNull(view.HpChart);
        Assert.Equal(2, view.HpChart!.SeriesCount);
        Assert.True(Render.DistinctColors(Render.Snap(host)) > 8);
        Render.Save(view.RenderToBitmap(), $"report_multiboss_full_{Render.Theme(dark)}");

        var chart = new BossHpChart();
        chart.SetEncounter(r, Gd);
        var chartHost = Render.Host(chart, dark, 900, 180);
        chart.SetHover(new Point(420, 90));
        Render.Layout(chartHost, 900, 180);
        Render.Save(Render.Snap(chartHost), $"chart_bosshp_multiboss_{Render.Theme(dark)}");
    });

    [Fact]
    public void Single_boss_report_keeps_its_layout() => Sta.Run(() =>
    {
        var view = new EncounterReportView(SampleData.BossFight(3, 60), Gd);
        Render.Host(view, true, 1100, 1000);
        var texts = Texts(view);
        Assert.Contains("Boss HP", texts);
        Assert.DoesNotContain("Bosses", texts);
        Assert.DoesNotContain("PRIMARY", texts);
        Assert.Equal(1, view.HpChart!.SeriesCount);
    });

    [Fact]
    public void History_files_a_multi_boss_fight_under_its_primary_boss_with_the_combined_title() => Sta.Run(() =>
    {
        var store = new FakeFightStore();
        var multi = MultiBossFight();
        store.Save(multi);
        store.Save(SampleData.BossFight(9, 120, startUtc: multi.StartUtc.AddMinutes(-20), bossCode: SampleData.BossIlsa));
        var view = new HistoryView();
        var host = Render.Host(view, true, 1560, 980);
        view.Initialize(store, Gd);
        Sta.Wait(view.PendingRefresh);
        Assert.Equal(2, view.Fights.Count);
        Assert.Equal(SampleData.BossVorgrath, view.Fights.Single(f => f.Id == multi.Id).BossNpcCode);

        Sta.Wait(view.SelectFightAsync(multi.Id));
        Render.Layout(host, 1560, 980);
        var texts = Texts(view);
        Assert.Contains(texts, t => t.StartsWith("Pyre Warden Ilsa + Vorgrath the Ashen · ", StringComparison.Ordinal));
        // Group headers: the multi-boss fight sits under its primary boss, the solo Ilsa kill under Ilsa.
        Assert.Contains("Vorgrath the Ashen", texts);
        Assert.Contains("Pyre Warden Ilsa", texts);
        Render.Save(Render.Snap(host), "history_multiboss_dark");
    });
}
