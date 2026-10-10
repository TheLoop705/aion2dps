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
    public void Auto_scope_in_the_open_world_shows_only_you_solo_and_your_party_in_a_party()
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
        var solo = s.Snap(1);
        Assert.Equal(Script.Me, Assert.Single(solo.Rows).EntityId); // solo: just you, never the strangers on the boss
        Assert.Equal(GroupScope.Solo, solo.Scope);
        Assert.Equal(1, solo.GroupSize);
        Assert.Equal(FightContext.FieldBoss, solo.Context);

        s.Roster(1.5, ("Me", 6), ("Ally", 26));
        var party = s.Snap(2);
        Assert.Equal(2, party.Rows.Count);
        Assert.DoesNotContain(party.Rows, r => r.EntityId == Script.Stranger);
        Assert.Equal(GroupScope.Party, party.Scope);
        Assert.Equal(2, party.GroupSize);
    }

    [Fact]
    public void A_force_at_a_field_boss_ranks_every_force_member_but_never_strangers()
    {
        // [real] 2B 96 arrives for every other member of the force (your own party included); 1B 92 only for your party.
        const uint OtherParty = 250, Bystander = 650;
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, Script.Ally, "Ally", 26);
        s.Player(0, OtherParty, "OtherParty", 14);
        s.Player(0, Bystander, "Bystander", 14);
        s.PartyHp(0.2, Script.Ally);
        s.ForceHp(0.2, Script.Ally);
        s.ForceHp(0.2, OtherParty);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, Script.Ally, Script.Boss, 200, Script.SorSkill);
        s.Hit(1, OtherParty, Script.Boss, 300, Script.RanSkill);
        s.Hit(1, Bystander, Script.Boss, 900, Script.RanSkill);

        var snap = s.Snap(2);
        Assert.Equal(new uint[] { OtherParty, Script.Ally, Script.Me }, snap.Rows.Select(r => r.EntityId));
        Assert.True(Script.Row(snap, Script.Ally).IsPartyMember);
        Assert.False(Script.Row(snap, Script.Ally).IsForceMember);
        Assert.True(Script.Row(snap, OtherParty).IsForceMember);
        Assert.False(Script.Row(snap, OtherParty).IsPartyMember);
        Assert.Equal(GroupScope.Force, snap.Scope);
        Assert.Equal(3, snap.GroupSize); // you + the two force members seen

        var rec = s.Record();
        Assert.True(Script.Combatant(rec, OtherParty).IsForceMember);
        Assert.False(Script.Combatant(rec, Script.Ally).IsForceMember);
    }

    [Fact]
    public void A_force_member_is_never_an_enemy_and_their_heals_count()
    {
        const uint OtherParty = 250;
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, OtherParty, "OtherHealer", 30);
        s.ForceHp(0.1, OtherParty);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Me, Script.Boss, 100);
        // A player attack between you and a force member (a charm or mechanic) never turns them into an enemy.
        s.Hit(1.5, OtherParty, Script.Me, 50, Script.RanSkill);
        s.Hit(2, OtherParty, Script.Me, 4_000, FakeGameData.HealSkill);
        var snap = s.Snap(3);
        Assert.Empty(snap.PvpRows);
        var healer = Script.Row(snap, OtherParty);
        Assert.Equal(CombatantKind.Player, healer.Kind);
        Assert.Equal(4_000, healer.Healing);
    }

    [Fact]
    public void Party_only_inside_an_instance_keeps_the_other_parties_of_your_force()
    {
        var s = Script.Standard(new EngineOptions { BossFightsOnly = true, AutoPartyScope = true, PartyOnly = true });
        s.Player(0, Script.Stranger, "OtherPartyMember", 14);
        s.Roster(0.5, ("Me", 6), ("Ally", 26));
        s.ForceHp(0.5, Script.Ally);
        s.ForceHp(0.5, Script.Stranger);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        s.Hit(1, Script.Stranger, Script.Boss, 300, Script.RanSkill);
        var snap = s.Snap(1);
        Assert.Equal(3, snap.Rows.Count);
        Assert.Equal(GroupScope.Force, snap.Scope);
        Assert.Equal(FightContext.DungeonBoss, snap.Context);
    }

    [Fact]
    public void Force_members_are_forgotten_on_a_zone_change()
    {
        const uint OtherParty = 250;
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, OtherParty, "OtherParty", 14);
        s.ForceHp(0.1, OtherParty);
        s.Map(5, Script.Map1);
        s.Player(5, OtherParty, "OtherParty", 14);
        s.SpawnNpc(5, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(6, Script.Me, Script.Boss, 100);
        s.Hit(6, OtherParty, Script.Boss, 100, Script.RanSkill);
        var snap = s.Snap(7);
        Assert.False(Script.Row(snap, OtherParty).IsForceMember);
        Assert.NotEqual(GroupScope.Force, snap.Scope);
    }

    [Fact]
    public void A_training_run_shows_only_you_even_when_strangers_hit_the_same_dummy()
    {
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.SpawnNpc(0, Script.Dummy, FakeGameData.DummyCode, 1_000_000, 1_000_000);
        s.Engine.StartTraining(TimeSpan.FromSeconds(30));
        s.Hit(1, Script.Me, Script.Dummy, 500);
        s.Hit(1.5, Script.Stranger, Script.Dummy, 9_000, Script.RanSkill);
        s.Hit(2, Script.Me, Script.Dummy, 700);
        var snap = s.Snap(3);
        Assert.Equal(EncounterKind.Training, snap.EncounterKind);
        Assert.Equal(FightContext.Training, snap.Context);
        Assert.Equal(Script.Me, Assert.Single(snap.Rows).EntityId);
    }

    [Fact]
    public void A_strangers_unresolved_spirit_never_starts_a_fight_on_a_boss_flagged_scarecrow()
    {
        // [real] dungeon-snapshot 2026-10-07 21:47: another player's spirit (owner unknown at the time) hit the Melee
        // Training Scarecrow next to yours and started (and saved) a dummy fight with 0 damage of yours.
        const uint Spirit = 7100;
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.SpawnNpc(0, Script.Dummy, FakeGameData.BossDummyCode, 1_000_000, 1_000_000);
        s.SpawnSummon(0.5, Spirit);
        s.Hit(1, Spirit, Script.Dummy, 6, Script.SpiritSkill);
        s.Hit(2, Spirit, Script.Dummy, 6, Script.SpiritSkill);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(2).State);

        s.Hit(3, Script.Me, Script.Dummy, 500);
        var snap = s.Snap(3);
        Assert.Equal(EncounterKind.Dummy, snap.EncounterKind);
        Assert.Equal(FightContext.TrainingDummy, snap.Context);
        Assert.Equal(Script.Me, Assert.Single(snap.Rows).EntityId);
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
    public void Force_size_counts_only_members_seen_around_the_fight()
    {
        // [real] Gartua: 21 distinct 2B 96 ids in one zone visit as members came and went; a force has 20 places.
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        for (uint i = 0; i < 6; i++) s.ForceHp(0, 1000 + i);   // left the force long before the fight
        for (uint i = 0; i < 25; i++) s.ForceHp(900, 2000 + i); // around the fight (more ids than places)
        s.SpawnNpc(900, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1000, Script.Me, Script.Boss, 100);
        var snap = s.Snap(1001);
        Assert.Equal(GroupScope.Force, snap.Scope);
        Assert.Equal(20, snap.GroupSize);

        var t = new Script(AppLike());
        t.Map(0, FakeGameData.OpenWorldMap);
        t.Self(0);
        for (uint i = 0; i < 6; i++) t.ForceHp(0, 1000 + i);
        for (uint i = 0; i < 3; i++) t.ForceHp(900, 2000 + i);
        t.SpawnNpc(900, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        t.Hit(1000, Script.Me, Script.Boss, 100);
        Assert.Equal(4, t.Snap(1001).GroupSize); // you + the 3 seen within 5 minutes of the pull
    }

    [Fact]
    public void A_force_that_broke_up_on_the_same_map_is_forgotten_by_your_next_solo_fight()
    {
        // Review finding: membership never expired within a zone, so an ex-member's DoTs ranked as "force" 20 min later.
        const uint ExMember = 250;
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, ExMember, "ExMember", 14);
        for (int t = 0; t <= 60; t += 10) s.ForceHp(t, ExMember);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(30, Script.Me, Script.Boss, 100);
        s.Dot(31, ExMember, Script.Boss, 0x0A, 5_000);
        Assert.Equal(GroupScope.Force, s.Snap(32).Scope);
        s.Hp(40, Script.Boss, 0);
        s.Death(40, Script.Boss);
        for (int t = 41; t <= 200; t += 5) s.Tick(t);

        // 20 minutes later on the same map (no zone change), solo, the ex-member DoTs the next boss.
        s.SpawnNpc(1250, Script.Boss2, FakeGameData.BossCode2, 3_600_000, 3_600_000);
        s.Hit(1260, Script.Me, Script.Boss2, 100);
        s.Dot(1261, ExMember, Script.Boss2, 0x0A, 50_000);
        var snap = s.Snap(1262);
        Assert.Equal(GroupScope.Solo, snap.Scope);
        Assert.Equal(Script.Me, Assert.Single(snap.Rows).EntityId);

        // They rejoin (their 2B 96 comes again): back in the force, flags refreshed mid-fight.
        s.ForceHp(1263, ExMember);
        var back = s.Snap(1264);
        Assert.Equal(GroupScope.Force, back.Scope);
        Assert.True(Script.Row(back, ExMember).IsForceMember);
    }

    [Fact]
    public void A_force_member_wrongly_flagged_as_an_enemy_is_released_with_their_pvp_state()
    {
        const uint OtherParty = 250;
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, OtherParty, "OtherParty", 14);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1.5, OtherParty, Script.Me, 50, Script.RanSkill); // a charm/mechanic hit before their first 2B 96
        s.ForceHp(2, OtherParty);
        s.Dot(3, OtherParty, Script.Boss, 0x0A, 4_000);
        var snap = s.Snap(4);
        Assert.Empty(snap.PvpRows);
        Assert.True(Script.Row(snap, OtherParty).IsForceMember);
        Assert.Equal(CombatantKind.Player, Script.Combatant(s.Record(), OtherParty).Kind);
    }

    [Fact]
    public void A_finished_solo_fight_stays_solo_when_you_join_a_party_afterwards()
    {
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Me, Script.Boss, 3_600_000);
        s.Hp(1, Script.Boss, 0);
        s.Death(1, Script.Boss);
        for (int t = 2; t <= 20; t += 2) s.Tick(t);
        s.Roster(30, ("Me", 6), ("Ally", 26), ("Third", 14));
        var snap = s.Snap(31);
        Assert.Equal(MeterState.Ended, snap.State);
        Assert.Equal(GroupScope.Solo, snap.Scope);
        Assert.Equal(1, snap.GroupSize);
    }

    [Fact]
    public void A_finished_fight_without_a_map_does_not_take_the_instance_you_ported_into()
    {
        // Meter started mid-session: no map load before the fight, so the fight's map is unknown.
        const uint OtherParty = 250;
        var s = new Script(AppLike());
        s.Self(0);
        s.Player(0, OtherParty, "OtherParty", 14);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.ForceHp(0.5, OtherParty);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Me, Script.Boss, 1_000_000);
        s.Dot(1.5, OtherParty, Script.Boss, 0x0A, 4_000);
        s.Dot(1.5, Script.Stranger, Script.Boss, 0x0A, 3_000);
        s.Hit(2, Script.Me, Script.Boss, 2_600_000);
        s.Hp(2, Script.Boss, 0);
        s.Death(2, Script.Boss);
        for (int t = 3; t <= 20; t += 2) s.Tick(t);
        s.Map(21, Script.Map1);
        var snap = s.Snap(22);
        Assert.DoesNotContain(snap.Rows, r => r.EntityId == Script.Stranger);
        Assert.NotEqual(FightContext.DungeonBoss, snap.Context);
    }

    [Fact]
    public void Before_you_are_identified_a_known_group_does_not_hide_your_own_hits()
    {
        // Mid-fight start in a force: 2B 96 arrives before the local player is inferred. Your hits (the only direct hits
        // the open world sends) must stay on the meter until you are known.
        const uint OtherParty = 250;
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Player(0, OtherParty, "OtherParty", 14);
        s.ForceHp(0.5, OtherParty);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Me, Script.Boss, 160_000);
        s.Dot(1.5, OtherParty, Script.Boss, 0x0A, 5_000);
        var snap = s.Snap(2);
        Assert.Contains(snap.Rows, r => r.EntityId == Script.Me && r.Damage == 160_000);
    }

    [Fact]
    public void Strangers_never_start_a_fight_on_a_boss_flagged_scarecrow_before_you_are_known()
    {
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.SpawnNpc(0, Script.Dummy, FakeGameData.BossDummyCode, 1_000_000, 1_000_000);
        for (int t = 1; t <= 8; t++) s.Hit(t, Script.Stranger, Script.Dummy, 500, Script.RanSkill);
        for (int t = 9; t <= 60; t += 5) s.Tick(t);
        Assert.Null(s.Engine.GetCurrentEncounter());
        Assert.Empty(s.Completed);
    }

    [Fact]
    public void Solo_is_just_you_even_before_the_map_is_known()
    {
        // Meter started mid-session: no map load yet, but you are known and have no group.
        var s = new Script(AppLike());
        s.Self(0);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 6_000_000, 6_000_000);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, Script.Stranger, Script.Boss, 900, Script.RanSkill);
        var snap = s.Snap(2);
        Assert.Equal(Script.Me, Assert.Single(snap.Rows).EntityId);
        Assert.Equal(GroupScope.Solo, snap.Scope);
    }

    [Fact]
    public void A_finished_force_fight_keeps_its_group_after_you_leave_the_zone()
    {
        // [real] Gartua capture: porting out after the Lagta kill cleared the group evidence, and the finished fight
        // suddenly listed "Others (122)" and strangers. The fight's own members and map decide its rows.
        const uint OtherParty = 250;
        var s = new Script(AppLike());
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.Player(0, Script.Ally, "Ally", 26);
        s.Player(0, OtherParty, "OtherParty", 14);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.PartyHp(0.1, Script.Ally);
        s.ForceHp(0.1, Script.Ally);
        s.ForceHp(0.1, OtherParty);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Me, Script.Boss, 1_000_000);
        s.Dot(1.5, Script.Ally, Script.Boss, 0x0A, 5_000);
        s.Dot(1.5, OtherParty, Script.Boss, 0x0A, 4_000);
        s.Dot(1.5, Script.Stranger, Script.Boss, 0x0A, 3_000);
        s.Hit(2, Script.Me, Script.Boss, 2_600_000);
        s.Hp(2, Script.Boss, 0);
        s.Death(2, Script.Boss);
        for (int t = 3; t <= 20; t += 2) s.Tick(t);
        s.Map(21, Script.Map1); // into an instance: the live group evidence is gone

        var snap = s.Snap(22);
        Assert.Equal(MeterState.Ended, snap.State);
        Assert.Equal(new uint[] { Script.Me, Script.Ally, OtherParty }.Order(), snap.Rows.Select(r => r.EntityId).Order());
        Assert.Equal(GroupScope.Force, snap.Scope);
        Assert.Equal(3, snap.GroupSize);
        Assert.Equal(FightContext.FieldBoss, snap.Context);
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
