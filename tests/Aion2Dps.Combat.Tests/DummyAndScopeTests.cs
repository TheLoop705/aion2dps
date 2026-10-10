using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

/// <summary>Training dummies only track your own (and your party's) hits; automatic row scope party vs solo.</summary>
public class DummyAndScopeTests
{
    private static EngineOptions AppLike() => new() { BossFightsOnly = true, AutoPartyScope = true };

    [Fact]
    public void Strangers_hitting_dummies_never_start_a_fight()
    {
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.SpawnNpc(0, Script.Dummy, FakeGameData.DummyCode, 1_000_000, 1_000_000);
        s.Hit(1, Script.Stranger, Script.Dummy, 500, Script.RanSkill);
        s.Hit(2, Script.Stranger, Script.Dummy, 500, Script.RanSkill);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(2).State);
    }

    [Fact]
    public void Strangers_hitting_dummies_before_the_local_player_is_known_start_nothing()
    {
        // Mid-session start: the local player is unknown, so everyone counts as "core" for bosses, but not for dummies.
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.SpawnNpc(0, Script.Dummy, FakeGameData.DummyCode, 1_000_000, 1_000_000);
        s.Hit(1, Script.Stranger, Script.Dummy, 500, Script.RanSkill);
        Assert.NotEqual(MeterState.InCombat, s.Snap(1).State);
        Assert.Null(s.Engine.GetCurrentEncounter());
    }

    [Fact]
    public void Your_dummy_fight_shows_only_you_even_when_strangers_hit_the_same_dummy()
    {
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.SpawnNpc(0, Script.Dummy, FakeGameData.DummyCode, 1_000_000, 1_000_000);
        s.Hit(1, Script.Me, Script.Dummy, 500);
        s.Hit(1.5, Script.Stranger, Script.Dummy, 9_000, Script.RanSkill);
        s.Hit(3, Script.Me, Script.Dummy, 700);

        var snap = s.Snap(3);
        Assert.Equal(EncounterKind.Dummy, snap.EncounterKind);
        var row = Assert.Single(snap.Rows);
        Assert.Equal(Script.Me, row.EntityId);
        Assert.Equal(1_200, row.Damage);
        Assert.True(row.Dps > 0);
    }

    [Fact]
    public void Auto_scope_in_the_open_world_shows_everyone_solo_and_only_the_party_in_a_party()
    {
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, Script.Ally, "Ally", 26);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        s.Hit(1, Script.Stranger, Script.Boss, 100, Script.RanSkill);
        Assert.Equal(3, s.Snap(1).Rows.Count); // solo: everyone on the boss

        s.Roster(1.5, ("Me", 6), ("Ally", 26));
        var party = s.Snap(2);
        Assert.Equal(2, party.Rows.Count);
        Assert.DoesNotContain(party.Rows, r => r.EntityId == Script.Stranger);
    }

    [Fact]
    public void Auto_scope_in_an_instance_ranks_the_whole_force_not_just_your_party()
    {
        // A Force (several parties) inside an instance: your roster lists only your own party, but every member of the
        // other parties is in the same instance and belongs on the ranking.
        var s = Script.Standard(AppLike()); // Map1 is an instance
        s.Player(0, Script.Stranger, "OtherPartyMember", 14);
        s.Roster(0.5, ("Me", 6), ("Ally", 26));
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        s.Hit(1, Script.Stranger, Script.Boss, 300, Script.RanSkill);
        var snap = s.Snap(1);
        Assert.Equal(3, snap.Rows.Count);
        Assert.Equal(Script.Stranger, snap.Rows[0].EntityId);
    }

    [Fact]
    public void Finished_fight_is_kept_when_the_ended_display_delay_is_zero()
    {
        var s = Script.Standard(AppLike());
        s.Hit(1, Script.Me, Script.Boss, 3_600_000);
        s.Hp(1, Script.Boss, 0);
        s.Death(1, Script.Boss);
        for (int t = 2; t <= 600; t += 30) s.Tick(t);
        var snap = s.Snap(600);
        Assert.Equal(MeterState.Ended, snap.State);
        Assert.Equal(Script.Me, Assert.Single(snap.Rows).EntityId);
    }

    [Fact]
    public void Death_recap_records_when_and_which_attack_killed()
    {
        var s = Script.Standard(AppLike());
        s.Hit(1, Script.Me, Script.Boss, 1_000);
        // Real order: the death record (42 36) first, the kill record naming the boss attack right after.
        s.Death(10, Script.Me);
        s.Kill(10.05, Script.Me, Script.Boss, null, skill: 1604390);
        s.Hp(14, Script.Me, 17_573);                 // revived
        s.Hit(15, Script.Me, Script.Boss, 1_000);
        s.Kill(30, Script.Me, Script.Boss, null, skill: 1604425); // kill record alone
        s.Hit(40, Script.Me, Script.Boss, 1_000);

        var me = Script.Combatant(s.Record(), Script.Me);
        Assert.Equal(2, me.Deaths);
        Assert.Equal(2, me.DeathLog.Count);
        Assert.Equal(9, me.DeathLog[0].T, 1);
        Assert.Equal(1604390u, me.DeathLog[0].KillerSkillId);
        Assert.Equal(FakeGameData.BossCode, me.DeathLog[0].KillerNpcCode);
        Assert.Equal(29, me.DeathLog[1].T, 1);
        Assert.Equal(1604425u, me.DeathLog[1].KillerSkillId);
    }

    [Theory]
    [InlineData(5_220_000, 0, 0.01)]
    [InlineData(5_220_000, 1_000_000, 0.01)]
    [InlineData(5_220_000, 1_597_040, 0.03)] // Thamon: heals itself for 31 % of its HP
    public void Hp_check_tolerates_more_for_bosses_that_heal_themselves_heavily(long lost, long heal, double tolerance) =>
        Assert.Equal(tolerance, HpCheckTracker.Tolerance(lost, heal));
}
