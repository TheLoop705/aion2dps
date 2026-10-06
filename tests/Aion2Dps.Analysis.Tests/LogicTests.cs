using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(999.4, "999")]
    [InlineData(999.5, "1.0K")]
    [InlineData(1_000, "1.0K")]
    [InlineData(12_345, "12.3K")]
    [InlineData(999_949, "999.9K")]
    [InlineData(999_950, "1.00M")]
    [InlineData(4_560_000, "4.56M")]
    [InlineData(17_880_000, "17.88M")]
    [InlineData(1_234_567_890, "1.23B")]
    [InlineData(-12_345, "-12.3K")]
    public void Abbreviates(double value, string expected) => Assert.Equal(expected, Fmt.Abbrev(value));

    [Fact]
    public void ExactUsesSeparators()
    {
        Assert.Equal("12,345,678", Fmt.Exact(12_345_678L));
        Assert.Equal("1,235", Fmt.Exact(1234.6));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5.9, "0:05")]
    [InlineData(65.4, "1:05")]
    [InlineData(600, "10:00")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-3, "0:00")]
    [InlineData(double.NaN, "0:00")]
    public void FormatsDurations(double seconds, string expected) => Assert.Equal(expected, Fmt.Duration(seconds));

    [Fact]
    public void PreciseTimeHasTenths() => Assert.Equal("0:42.3", Fmt.PreciseTime(42.37));

    [Theory]
    [InlineData(200_000_000, "200M")]
    [InlineData(1_500_000, "1.5M")]
    [InlineData(50_000, "50K")]
    [InlineData(12_500, "12.5K")]
    [InlineData(250, "250")]
    [InlineData(0, "0")]
    public void AxisLabelsDropTrailingZeros(double v, string expected) => Assert.Equal(expected, Fmt.Axis(v));

    [Fact]
    public void CountsRatiosAndSecondsAreCultureInvariant()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1 run", Fmt.Count(1, "run"));
            Assert.Equal("3 runs", Fmt.Count(3, "run"));
            Assert.Equal("0.71×", Fmt.Ratio(0.7123));
            Assert.Equal("12.5 s", Fmt.Seconds(12.46));
            Assert.Equal("12.3K", Fmt.Abbrev(12_345));
            Assert.Equal("45.6%", Fmt.Percent(0.456));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void PercentagesDashWhenUnmeasured()
    {
        Assert.Equal("12.3%", Fmt.Percent(0.123));
        Assert.Equal("100.0%", Fmt.Percent(1));
        Assert.Equal("12%", Fmt.Percent(0.123, 0));
        Assert.Equal(Fmt.Dash, Fmt.Percent(null));
        Assert.Equal(Fmt.Dash, Fmt.Percent(double.NaN));
        Assert.Equal(Fmt.Dash, Fmt.Rate(0, 0));
        Assert.Equal("0.0%", Fmt.Rate(0, 10));
        Assert.Equal("75.0%", Fmt.Rate(3, 4));
        Assert.Null(Fmt.RateValue(5, 0));
    }

    [Fact]
    public void TimeAgo()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("just now", Fmt.TimeAgo(now.AddSeconds(-20), now));
        Assert.Equal("12m ago", Fmt.TimeAgo(now.AddMinutes(-12), now));
        Assert.Equal("3h ago", Fmt.TimeAgo(now.AddHours(-3.5), now));
        Assert.Equal("5d ago", Fmt.TimeAgo(now.AddDays(-5), now));
    }

    [Fact]
    public void OutcomeLabels()
    {
        Assert.Equal("KILL", Fmt.OutcomeLabel(EncounterOutcome.Kill));
        Assert.Equal(ThemeKeys.Negative, Fmt.OutcomeBrushKey(EncounterOutcome.Wipe));
    }
}

public class SeriesMathTests
{
    [Fact]
    public void RollingDpsUsesTrailingWindowAndShrinksAtStart()
    {
        long[] perSecond = [100, 100, 100, 100, 100, 600, 0, 0, 0, 0, 0];
        var r = SeriesMath.RollingDps(perSecond, 5);
        Assert.Equal(100, r[0]);
        Assert.Equal(100, r[4]);
        Assert.Equal(200, r[5]);            // (100*4 + 600) / 5
        Assert.Equal(120, r[9]);            // only the 600 is left in the window
        Assert.Equal(0, r[10]);
    }

    [Fact]
    public void RollingDpsPadsToLength()
    {
        var r = SeriesMath.RollingDps(new long[] { 50 }, 3, 4);
        Assert.Equal([50, 25, 50.0 / 3, 0], r);
    }

    [Fact]
    public void CumulativeAndStack()
    {
        Assert.Equal([1L, 3, 6, 6], SeriesMath.Cumulative(new long[] { 1, 2, 3 }, 4));
        var stack = SeriesMath.Stack([new long[] { 1, 2 }, new long[] { 10, 20 }], 2);
        Assert.Equal([1L, 2], stack[0]);
        Assert.Equal([11L, 22], stack[1]);
    }

    [Fact]
    public void PerSecondBuckets()
    {
        var hits = new[]
        {
            new HitRecord { T = 0.2f, Amount = 5 }, new HitRecord { T = 0.9f, Amount = 7 }, new HitRecord { T = 2.5f, Amount = 1 },
            new HitRecord { T = 9f, Amount = 100 },
        };
        Assert.Equal([12L, 0, 1], SeriesMath.PerSecond(hits, _ => true, 3));
    }

    [Fact]
    public void MedianAndTicks()
    {
        Assert.Equal(2, SeriesMath.Median([3, 1, 2]));
        Assert.Equal(2.5, SeriesMath.Median([4, 1, 2, 3]));
        Assert.True(double.IsNaN(SeriesMath.Median([])));
        var ticks = SeriesMath.NiceTicks(87, 4, out double max);
        Assert.Equal(100, max);
        Assert.Equal([0.0, 25, 50, 75, 100], ticks);
    }

    [Fact]
    public void DetectsResetsOnlyAfterDroppingBelowNinety()
    {
        HpSample[] s = [new(0, 1000), new(10, 950), new(20, 1000), new(30, 800), new(40, 600), new(45, 1000), new(50, 999)];
        var resets = SeriesMath.DetectResets(s, 1000);
        Assert.Single(resets);
        Assert.Equal(45, resets[0]);
    }
}

public class CastGrouperTests
{
    private static HitRecord H(float t, uint skill, byte tag, long amount, HitFlags flags = HitFlags.None, uint actor = 1, uint target = 9) =>
        new() { T = t, Actor = actor, Source = actor, Target = target, Skill = skill, HitTag = tag, Amount = amount, Flags = flags };

    [Fact]
    public void GroupsHitsOfOneCastAcrossTargets()
    {
        var rot = CastGrouper.Build(
        [
            H(1.0f, 100, 7, 10), H(1.1f, 100, 7, 20, HitFlags.Crit, target: 10), H(1.3f, 100, 7, 30),
            H(2.0f, 200, 8, 50),
        ], actor: 1);
        Assert.Equal(2, rot.Casts.Count);
        var first = rot.Casts[0];
        Assert.Equal(100u, first.Skill);
        Assert.Equal(60, first.Amount);
        Assert.Equal(3, first.Hits);
        Assert.True(first.Crit);
        Assert.Equal(30, first.MaxHit);
        Assert.Equal(1.0f, first.StartT);
        Assert.Equal(1.3f, first.EndT);
    }

    [Fact]
    public void SameTagAfterWindowStartsNewCast()
    {
        var rot = CastGrouper.Build([H(1.0f, 100, 7, 10), H(2.7f, 100, 7, 10), H(2.9f, 100, 7, 10)], 1);
        Assert.Equal(2, rot.Casts.Count);   // 2.7 − 1.0 > 1.5 s
        Assert.Equal(1, rot.Casts[0].Hits);
        Assert.Equal(2, rot.Casts[1].Hits);
    }

    [Fact]
    public void DifferentTagsAreDifferentCasts()
    {
        var rot = CastGrouper.Build([H(1.0f, 100, 7, 10), H(1.2f, 100, 8, 10)], 1);
        Assert.Equal(2, rot.Casts.Count);
    }

    [Fact]
    public void DotTicksAndIncomingAndOtherActorsAreSeparated()
    {
        var rot = CastGrouper.Build(
        [
            H(1.0f, 100, 7, 10), H(2.0f, 100, 7, 3, HitFlags.Dot), H(3.0f, 100, 7, 3, HitFlags.Dot),
            H(1.5f, 900, 1, 999, HitFlags.Incoming), H(1.6f, 100, 7, 10, actor: 2),
        ], 1);
        Assert.Single(rot.Casts);
        Assert.Equal(2, rot.DotTicks.Count);
        Assert.Equal(16, rot.TotalDamage);
    }

    [Fact]
    public void HealsCanBeExcludedAndSummonsAreMarked()
    {
        var hits = new[] { H(1, 100, 1, 50, HitFlags.Heal), H(2, 300, 2, 20, HitFlags.Summon) };
        Assert.Equal(2, CastGrouper.Build(hits, 1).Casts.Count);
        var noHeals = CastGrouper.Build(hits, 1, includeHeals: false);
        Assert.Single(noHeals.Casts);
        Assert.True(noHeals.Casts[0].FromSummon);
    }

    [Fact]
    public void EnemyPlayerRotationIncludesTheirIncomingFlaggedHits()
    {
        var r = SampleData.PvpSession();
        Assert.Empty(CastGrouper.Build(r.Hits, 7001).Casts);
        var rot = CastGrouper.Build(r.Hits, 7001, includeIncoming: true);
        Assert.NotEmpty(rot.Casts);
        Assert.Equal(r.Combatants.First(c => c.EntityId == 7001).Damage, rot.TotalDamage);
    }

    [Fact]
    public void OrdersByStartTimeEvenWhenInputIsNot()
    {
        var rot = CastGrouper.Build([H(5, 100, 1, 1), H(1, 200, 2, 1)], 1);
        Assert.Equal(200u, rot.Casts[0].Skill);
    }

    [Fact]
    public void CountsByGroup()
    {
        var rot = CastGrouper.Build([H(1, 11020030, 1, 1), H(3, 11020040, 2, 1), H(5, 11030010, 3, 1)], 1);
        var counts = CastGrouper.CountByGroup(rot, SkillIds.BaseId);
        Assert.Equal(2, counts[11020000]);
        Assert.Equal(1, counts[11030000]);
    }

    [Fact]
    public void SampleFightProducesPlausibleRotation()
    {
        var r = SampleData.BossFight();
        Assert.InRange(r.Hits.Count, 17_000, 26_000);
        var rot = CastGrouper.Build(r.Hits, 1001);
        Assert.InRange(rot.Casts.Count, 500, 1100);
        Assert.NotEmpty(rot.DotTicks);
        long fromRecord = r.Combatants.First(c => c.EntityId == 1001).Damage;
        Assert.Equal(fromRecord, rot.TotalDamage);
    }
}

public class ChartDataTests
{
    [Fact]
    public void TimelineSeriesCoverWholeFightForFriendlyDamageDealers()
    {
        var r = SampleData.BossFight();
        var series = ChartData.TimelineSeries(r);
        Assert.Equal(5, series.Count);
        int n = ChartData.SecondsLength(r);
        Assert.All(series, s => Assert.Equal(n, s.Values.Count));
        Assert.Equal(series.OrderByDescending(s => r.Combatants.First(c => c.EntityId == s.EntityId).Damage).Select(s => s.EntityId), series.Select(s => s.EntityId));
        // The sorcerer dies at 62 %: their rolling DPS is 0 at the end of the fight.
        var sorc = series.First(s => s.Class == CharacterClass.Sorcerer);
        Assert.Equal(0, sorc.Values[^1]);
        Assert.True(sorc.Values.Take(n / 2).Max() > 0);
    }

    [Fact]
    public void CumulativeSeriesEndAtCombatantDamage()
    {
        var r = SampleData.BossFight();
        var series = ChartData.CumulativeSeries(r);
        foreach (var s in series) Assert.Equal(r.Combatants.First(c => c.EntityId == s.EntityId).Damage, s.Values[^1]);
        var stack = SeriesMath.Stack(series.Select(s => s.Values).ToList(), series[0].Values.Count);
        Assert.Equal(r.TotalDamage, stack[^1][^1]);
        Assert.Equal(r.BossMaxHp, r.TotalDamage); // kill: party removed exactly the boss HP
    }

    [Fact]
    public void PvpSeriesExcludeEnemiesByDefault()
    {
        var r = SampleData.PvpSession();
        Assert.Single(ChartData.TimelineSeries(r));
        Assert.Equal(4, ChartData.Enemies(r).Count());
        Assert.Equal(4, ChartData.TimelineSeries(r, include: c => c.Kind == CombatantKind.EnemyPlayer).Count);
    }

    [Fact]
    public void KillTimeAndTitle()
    {
        var gd = new FakeGameData();
        var kill = SampleData.BossFight();
        Assert.NotNull(ChartData.KillTime(kill));
        Assert.Null(ChartData.KillTime(SampleData.WipeFight()));
        Assert.Equal("Vorgrath the Ashen", ChartData.Title(kill, gd));
        Assert.Equal("PvP session", ChartData.Title(SampleData.PvpSession(), gd));
    }
}

public class SkillVisualsTests
{
    [Theory]
    [InlineData("Ferocious Strike", 0u, "FS")]
    [InlineData("Cleave", 0u, "Cl")]
    [InlineData("Hunter's Mark", 0u, "HM")]
    [InlineData("Blessing of Light", 0u, "BL")]
    [InlineData("Skill 11020030", 11020030u, "30")]
    [InlineData("", 11020045u, "45")]
    [InlineData("烈火斩", 0u, "烈火")]
    public void Initials(string name, uint id, string expected) => Assert.Equal(expected, SkillVisuals.Initials(name, id));

    [Fact]
    public void TileClassFallsBackToOwner()
    {
        var gd = new FakeGameData();
        Assert.Equal(CharacterClass.Ranger, SkillVisuals.TileClass(gd, 14_010_010, CharacterClass.Cleric));
        Assert.Equal(CharacterClass.Cleric, SkillVisuals.TileClass(gd, 2_301_010, CharacterClass.Cleric));
    }
}
