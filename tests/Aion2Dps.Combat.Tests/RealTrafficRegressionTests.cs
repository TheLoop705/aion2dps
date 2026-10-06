using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

/// <summary>Regressions found while replaying a real AION 2 capture through the integrated pipeline.</summary>
public sealed class RealTrafficRegressionTests
{
    [Fact]
    public void Party_scaled_boss_whose_spawn_max_is_below_its_hp_uses_the_hp_reading()
    {
        // Real capture: 41 36 hp_max 120,000 while hp_cur / 00 8D report 600,000 (and the HP check balances on 600,000).
        var s = new Script();
        s.Map(0, Script.Map1);
        s.Self(0);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 600_000, 120_000);
        long hp = 600_000;
        for (int i = 0; i < 60; i++)
        {
            s.Hit(1 + i, Script.Me, Script.Boss, 10_000);
            hp -= 10_000;
            s.Hp(1 + i + 0.05, Script.Boss, hp);
        }

        s.Death(61.2, Script.Boss);
        s.Tick(70);
        var record = Assert.Single(s.Completed);
        Assert.Equal(EncounterOutcome.Kill, record.Outcome);
        Assert.Equal(600_000, record.BossMaxHp);
        var me = Script.Combatant(record, Script.Me);
        Assert.Equal(1.0, me.Contribution, 6);
        Assert.True(record.HpCheck!.Passed);
    }

    [Fact]
    public void Hp_update_above_the_spawn_max_raises_the_max_and_keeps_fractions_valid()
    {
        var s = new Script();
        s.Map(0, Script.Map1);
        s.Self(0);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 120_000, 120_000);
        s.Hp(0.5, Script.Boss, 600_000);
        s.Hit(1, Script.Me, Script.Boss, 60_000);
        s.Hp(1.05, Script.Boss, 540_000);
        var snap = s.Snap(2);
        Assert.Equal(600_000, snap.Target!.MaxHp);
        Assert.Equal(0.9, snap.Target.HpFraction!.Value, 6);
        Assert.Equal(0.1, Script.Row(snap, Script.Me).Contribution, 6);
    }

    [Fact]
    public void Stat_kind_7_rescales_the_boss_max_hp_without_a_false_wipe()
    {
        // Real capture: 00 8D <boss> kind 7 = max HP grows with the party (120k → 240k → … → 600k), then kind 0 = current.
        var s = new Script();
        s.Map(0, Script.Map1);
        s.Self(0);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 120_000, 120_000);
        foreach (var (t, max) in new[] { (0.2, 240_000L), (0.4, 360_000L), (0.6, 480_000L), (0.8, 600_000L) })
        {
            s.Send(new EntityStatsEvent { Time = Script.At(t), Entity = Script.Boss, Format = 2, Stats64 = new Dictionary<byte, long> { [7] = max } });
            s.Send(new EntityStatsEvent { Time = Script.At(t + 0.05), Entity = Script.Boss, Format = 2, CurrentHp = max });
        }

        long hp = 600_000;
        for (int i = 0; i < 30; i++)
        {
            s.Hit(2 + i, Script.Me, Script.Boss, 20_000);
            hp -= 20_000;
            s.Hp(2 + i + 0.05, Script.Boss, hp);
        }

        s.Death(32.2, Script.Boss);
        s.Tick(40);
        var record = Assert.Single(s.Completed);
        Assert.Equal(EncounterOutcome.Kill, record.Outcome);
        Assert.Equal(0, record.ResetCount);
        Assert.Equal(600_000, record.BossMaxHp);
        Assert.Equal(1.0, Script.Combatant(record, Script.Me).Contribution, 6);
        Assert.True(record.HpCheck!.Passed);
    }

    [Fact]
    public void Implausible_buff_durations_and_expiries_do_not_throw()
    {
        // Real capture: BuffAppliedEvent handling threw "Value to add was out of range" for huge expiry values.
        var s = Script.Standard();
        s.Send(new HeartbeatEvent { Time = Script.At(0), ServerUnixMs = (ulong)(Script.At(0) - DateTime.UnixEpoch).TotalMilliseconds });
        s.Hit(0.5, Script.Me, Script.Boss, 100);
        s.Send(new BuffAppliedEvent { Time = Script.At(1), Target = Script.Me, BuffId = 181200301, DurationMs = 0, ExpiryUnixMs = ulong.MaxValue - 5, Caster = Script.Ally });
        s.Send(new BuffAppliedEvent { Time = Script.At(1), Target = Script.Me, BuffId = 181200302, DurationMs = 0, ExpiryUnixMs = 0x7FFF_FFFF_FFFF_FFF0, Caster = Script.Ally });
        s.Send(new BuffAppliedEvent { Time = Script.At(1), Target = Script.Me, BuffId = 181200303, DurationMs = uint.MaxValue - 1, Caster = Script.Ally });
        s.Hit(10, Script.Me, Script.Boss, 100);
        Assert.Equal(0, s.Engine.EventErrors);
        var buffs = Script.Combatant(s.Record(), Script.Me).Buffs;
        Assert.Equal(3, buffs.Count);
        Assert.All(buffs, b => Assert.InRange(b.Uptime, 0, 1));
    }
}
