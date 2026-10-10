using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

public class FightInsightsTests
{
    [Fact]
    public void Downtime_counts_idle_seconds_between_first_and_last_damage()
    {
        //                    0  1    2    3  4  5    6  7
        long[] perSecond = [0, 100, 200, 0, 0, 0, 300, 0];
        var d = FightInsights.ComputeDowntime(perSecond);
        Assert.Equal(6, d.ActiveSeconds);        // seconds 1..6
        Assert.Equal(3, d.IdleSeconds);          // 3, 4, 5
        Assert.Equal(3, d.LongestGapSeconds);
        Assert.Equal(3, d.LongestGapStart);
        Assert.Equal(0.5, d.Uptime, 6);
    }

    [Fact]
    public void Downtime_of_no_damage_is_empty()
    {
        var d = FightInsights.ComputeDowntime([0, 0]);
        Assert.Equal(0, d.ActiveSeconds);
        Assert.Null(d.LongestGapStart);
        Assert.Equal(0, d.Uptime);
    }

    [Fact]
    public void Best_burst_finds_the_strongest_window()
    {
        long[] perSecond = [10, 10, 100, 100, 10, 10];
        var b = FightInsights.BestBurst(perSecond, 2);
        Assert.NotNull(b);
        Assert.Equal(100, b.Value.Dps, 6);
        Assert.Equal(2, b.Value.Start);
        // A window longer than the fight uses the whole fight.
        Assert.Equal(40, FightInsights.BestBurst(perSecond, 60)!.Value.Dps, 6);
        Assert.Null(FightInsights.BestBurst([0, 0], 10));
    }

    [Fact]
    public void Healing_received_groups_by_healer_and_skill_and_ignores_other_targets()
    {
        const uint me = 1, cleric = 2, other = 3;
        var r = new EncounterRecord
        {
            Hits =
            [
                new HitRecord { Actor = me, Target = me, Skill = 900, Amount = 500, Flags = HitFlags.Heal },
                new HitRecord { Actor = me, Target = me, Skill = 900, Amount = 700, Flags = HitFlags.Heal },
                new HitRecord { Actor = cleric, Target = me, Skill = 800, Amount = 2_000, Flags = HitFlags.Heal | HitFlags.Dot },
                new HitRecord { Actor = cleric, Target = other, Skill = 800, Amount = 9_999, Flags = HitFlags.Heal },
                new HitRecord { Actor = 50, Target = me, Skill = 1, Amount = 300, Flags = HitFlags.Incoming },
            ],
        };
        var heals = FightInsights.HealingReceived(r, me);
        Assert.Equal(2, heals.Count);
        Assert.Equal(new FightInsights.HealSource(cleric, 800, false, 2_000, 1, 2_000), heals[0]);
        Assert.Equal(new FightInsights.HealSource(me, 900, true, 1_200, 2, 700), heals[1]);
    }

    [Fact]
    public void Max_incoming_hit_is_keyed_like_the_source_table()
    {
        const uint me = 1;
        var r = new EncounterRecord
        {
            Hits =
            [
                new HitRecord { Actor = 50, Source = 51, Target = me, Skill = 7, Amount = 300, Flags = HitFlags.Incoming },
                new HitRecord { Actor = 50, Source = 51, Target = me, Skill = 7, Amount = 900, Flags = HitFlags.Incoming },
                new HitRecord { Actor = 50, Source = 51, Target = 2, Skill = 7, Amount = 5_000, Flags = HitFlags.Incoming },
                new HitRecord { Actor = me, Source = me, Target = 50, Skill = 7, Amount = 8_000 },
            ],
        };
        var max = FightInsights.MaxIncomingHits(r, me);
        Assert.Equal(900, Assert.Single(max).Value);
        Assert.Equal(900, max[(51, 7)]);
    }
}
