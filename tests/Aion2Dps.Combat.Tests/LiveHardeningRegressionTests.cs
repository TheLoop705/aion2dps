using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public sealed class LiveHardeningRegressionTests
{
    private const uint Summon = 9001;

    [Fact]
    public void Late_first_spawn_credits_the_orphan_summons_earlier_damage()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(2, Summon, Script.Boss, 444, Script.SpiritSkill, power: 33333);
        Assert.Equal(444, Script.Row(s.Snap(2), CombatEngine.UnknownSummonsEntityId).Damage);

        s.SpawnSummon(3, Summon, owner: Script.Ally);
        Assert.Equal(444, Script.Row(s.Snap(3), Script.Ally).Damage);
        Assert.DoesNotContain(s.Snap(3).Rows, r => r.Kind == CombatantKind.UnknownSummons);
        s.Hit(4, Summon, Script.Boss, 200, Script.SpiritSkill, power: 33333);

        var rec = s.Record();
        Assert.Equal(644, Script.Combatant(rec, Script.Ally).Damage);
        Assert.All(rec.Hits.Where(h => h.Source == Summon), h => Assert.Equal(Script.Ally, h.Actor));
    }

    [Fact]
    public void Late_npc_owner_removes_provisional_party_damage_and_recalculates_closed_hp_windows()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hp(1.1, Script.Boss, 3_599_900);
        s.Hit(2, Summon, Script.Boss, 444, Script.SpiritSkill, power: 33333);
        s.Hp(2.1, Script.Boss, 3_599_900); // the hostile entity's attack did not damage the boss
        s.Hit(3, Script.Me, Script.Boss, 100);
        s.Hp(3.1, Script.Boss, 3_599_800);
        Assert.True(s.Record().HpCheck!.Ratio > 1.01);

        s.SpawnSummon(3.2, Summon, owner: Script.Boss);

        var rec = s.Record();
        Assert.Equal(200, rec.TotalDamage);
        Assert.Equal(200, Assert.Single(rec.Targets).DamageTaken);
        Assert.DoesNotContain(rec.Combatants, c => c.Kind == CombatantKind.UnknownSummons);
        Assert.DoesNotContain(rec.Hits, h => h.Source == Summon);
        Assert.Equal(200, rec.HpCheck!.DecodedDamage);
        Assert.Equal(1.0, rec.HpCheck.Ratio, 8);
        Assert.True(rec.HpCheck.Passed);
        Assert.Equal(200, s.Snap(3.2).TotalDamage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lingering_dots_and_pets_do_not_revive_their_dead_owner_or_duplicate_deaths(bool pet)
    {
        var s = Script.Standard();
        s.SpawnSummon(0, Summon, owner: Script.Ally);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1.1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        s.Death(2, Script.Ally);
        if (pet) s.Hit(2.1, Summon, Script.Boss, 123, Script.SpiritSkill);
        else s.Dot(2.1, Script.Ally, Script.Boss, 0x0A, 123, effect: 1_528_024_011, skill: Script.SorSkill);

        Assert.True(Script.Row(s.Snap(2.1), Script.Ally).IsDead);
        s.Kill(2.2, Script.Ally, Script.Boss, null, Script.NpcSkill);
        s.Death(2.3, Script.Ally);
        Assert.Equal(1, Script.Combatant(s.Record(), Script.Ally).Deaths);
        Assert.Equal(223, Script.Combatant(s.Record(), Script.Ally).Damage);

        s.Hp(3, Script.Ally, 6000);
        s.Death(4, Script.Ally);
        Assert.Equal(2, Script.Combatant(s.Record(), Script.Ally).Deaths);
    }

    [Fact]
    public void Lingering_hot_does_not_revive_the_dead_healer()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1.1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        s.Death(2, Script.Ally);
        s.Dot(2.1, Script.Ally, Script.Me, 0x0B, 500, heal: 123, effect: 1_701_001_011, skill: FakeGameData.HealSkill);
        Assert.True(Script.Row(s.Snap(2.1), Script.Ally).IsDead);
        s.Death(2.2, Script.Ally);
        Assert.Equal(1, Script.Combatant(s.Record(), Script.Ally).Deaths);
        Assert.Equal(123, Script.Combatant(s.Record(), Script.Ally).Healing);
    }

    [Fact]
    public void Incoming_pvp_damage_does_not_revive_a_dead_local_player()
    {
        var s = Script.Standard();
        s.Player(0, Script.Enemy, "Enemy", 14);
        s.Hit(1, Script.Enemy, Script.Me, 100, Script.RanSkill);
        s.Death(2, Script.Me);
        s.Hit(2.1, Script.Enemy, Script.Me, 123, Script.RanSkill);
        Assert.True(Script.Row(s.Snap(2.1), Script.Me).IsDead);
        s.Death(2.2, Script.Me);
        Assert.Equal(1, Script.Combatant(s.Record(), Script.Me).Deaths);
    }

    [Fact]
    public void Rebuilding_after_discarded_hits_recomputes_the_encounter_and_target_clocks()
    {
        var enc = new Encounter(Script.T0, EncounterKind.Boss, _ => { });
        enc.Targets[Script.Boss] = new TargetState(Script.Boss);
        var wrong = new Hit { Time = Script.At(1), Actor = Script.Me, Target = Script.Boss, Amount = 100, InScope = true };
        enc.AddHit(wrong);
        enc.AddHit(new Hit { Time = Script.At(5), Actor = Script.Me, Target = Script.Boss, Amount = 200, InScope = true });
        enc.Hits.Remove(wrong);

        enc.RebuildLive();

        Assert.Equal(Script.At(5), enc.StartUtc);
        Assert.Equal(Script.At(5), enc.EndUtc);
        Assert.Equal(Script.At(5), enc.Targets[Script.Boss].First);
        Assert.Equal(200, enc.Targets[Script.Boss].DamageTaken);
    }

    [Fact]
    public void An_inferred_npc_acting_with_an_explicit_player_dot_skill_is_rebound_as_a_player()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(2, Script.Ally, 701, 800, Script.SorSkill); // unknown recipient, provisionally an NPC
        s.Dot(3, 701, Script.Boss, 0x0A, 300, effect: 1_103_003_011, skill: Script.GlaSkill2);

        var rec = s.Record();
        Assert.Equal(300, Script.Combatant(rec, 701).Damage);
        Assert.DoesNotContain(rec.Targets, t => t.EntityId == 701);
        Assert.Equal(400, rec.TotalDamage);
    }

    [Fact]
    public void The_first_authoritative_spawn_names_an_inferred_boss_without_losing_its_damage()
    {
        var s = new Script();
        s.Map(0, Script.Map1);
        s.Self(0);
        s.Hp(0.5, Script.Boss, 6_000_000);
        s.Hit(1, Script.Me, Script.Boss, 1000);
        Assert.Null(s.Record().BossNpcCode);

        s.SpawnNpc(2, Script.Boss, FakeGameData.BossCode, 5_999_000, 6_000_000);

        var rec = s.Record();
        Assert.Equal(FakeGameData.BossCode, rec.BossNpcCode);
        Assert.Equal(FakeGameData.BossCode, Assert.Single(rec.Bosses).NpcCode);
        Assert.Equal(1000, rec.TotalDamage);
        Assert.Equal("Enhanced Harcon", s.Snap(2).Target!.Name);
        Assert.Equal(1000.0 / 6_000_000, Script.Row(s.Snap(2), Script.Me).Contribution, 8);
    }

    [Fact]
    public void A_respawn_reusing_a_known_boss_id_does_not_rewrite_the_existing_encounter_identity()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 1000);
        s.SpawnNpc(2, Script.Boss, FakeGameData.BossCode2, 2_000_000, 2_000_000);
        Assert.Equal(FakeGameData.BossCode, s.Record().BossNpcCode);
        Assert.Equal(3_600_000, s.Record().BossMaxHp);
    }

    [Fact]
    public void An_unbound_owner_later_spawned_as_an_npc_loses_its_provisional_player_damage()
    {
        const uint owner = 9100;
        var s = Script.Standard();
        s.SpawnSummon(0, Summon, owner: owner);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hp(1.1, Script.Boss, 3_599_900);
        s.Hit(2, Summon, Script.Boss, 444, Script.SpiritSkill);
        s.Hp(2.1, Script.Boss, 3_599_900);
        s.Hit(3, Script.Me, Script.Boss, 100);
        s.Hp(3.1, Script.Boss, 3_599_800);
        Assert.Equal(444, Script.Combatant(s.Record(), owner).Damage);

        s.SpawnNpc(3.2, owner, FakeGameData.AddCode, 10_000, 10_000);

        var rec = s.Record();
        Assert.Equal(200, rec.TotalDamage);
        Assert.DoesNotContain(rec.Combatants, c => c.EntityId == owner);
        Assert.DoesNotContain(rec.Hits, h => h.Actor == owner);
        Assert.Equal(200, Assert.Single(rec.Targets).DamageTaken);
        Assert.Equal(1.0, rec.HpCheck!.Ratio, 8);
    }

    [Fact]
    public void Reusing_a_dead_orphan_id_does_not_move_the_previous_generations_damage()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(2, Summon, Script.Boss, 444, Script.SpiritSkill, power: 33333);
        s.Death(2.1, Summon);
        s.SpawnSummon(3, Summon, owner: Script.Ally);
        s.Hit(4, Summon, Script.Boss, 200, Script.SpiritSkill);

        var rec = s.Record();
        Assert.Equal(200, Script.Combatant(rec, Script.Ally).Damage);
        Assert.Equal(444, rec.Combatants.Single(c => c.Kind == CombatantKind.UnknownSummons).Damage);
    }

    [Fact]
    public void Discarded_hostile_damage_does_not_extend_the_boss_join_window()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Boss2, FakeGameData.BossCode2, 3_600_000, 3_600_000);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(20, Summon, Script.Boss, 444, Script.SpiritSkill, power: 33333);
        s.SpawnSummon(20.2, Summon, owner: Script.Boss);
        s.Hit(25, Script.Me, Script.Boss2, 200);

        var rec = s.Record();
        Assert.Equal(FakeGameData.BossCode2, Assert.Single(rec.Bosses).NpcCode);
        Assert.Equal(200, rec.TotalDamage);
    }

    [Fact]
    public void Discarded_hostile_damage_does_not_keep_the_boss_encounter_alive()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(20, Summon, Script.Boss, 444, Script.SpiritSkill, power: 33333);
        s.SpawnSummon(20.2, Summon, owner: Script.Boss);
        Assert.Equal(0, Assert.Single(s.Record().Bosses).LastHitSeconds);
        s.Tick(32);
        Assert.Equal(EncounterOutcome.Timeout, s.Record().Outcome);
    }

    [Fact]
    public void Reusing_an_authoritative_player_id_for_an_npc_keeps_that_players_earlier_damage()
    {
        var s = Script.Standard();
        s.SpawnSummon(0, Summon, owner: Script.Ally);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(2, Summon, Script.Boss, 444, Script.SpiritSkill);
        s.SpawnNpc(3, Script.Ally, FakeGameData.AddCode, 10_000, 10_000);

        Assert.Equal(444, Script.Combatant(s.Record(), Script.Ally).Damage);
        Assert.Equal(544, s.Record().TotalDamage);
    }

    [Fact]
    public void Npc_ally_healing_counts_effective_hp_restoration_and_preserves_raw_heal_source()
    {
        const uint healAllies = 1_220_140;
        var s = Script.Standard();
        s.Data.HealSkills.Add(healAllies);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 1_500_000, 1_500_000);
        s.SpawnNpc(0, Script.Add, FakeGameData.AddCode, 10_000, 10_000);
        // Exact heal windows from Draupnir: the first 225,000 heal contains 132,851 overheal.
        s.Hit(1, Script.Me, Script.Boss, 90_117);
        s.Hp(1.1, Script.Boss, 1_409_883);
        s.Hit(1.2, Script.Me, Script.Boss, 2_032);
        s.Hit(1.2, Script.Add, Script.Boss, 225_000, healAllies);
        s.Hp(1.21, Script.Boss, 1_500_000);
        s.Hit(2, Script.Me, Script.Boss, 359_358);
        s.Hp(2.1, Script.Boss, 1_140_642);
        s.Hit(2.2, Script.Add, Script.Boss, 189_000, healAllies);
        s.Hit(2.2, Script.Me, Script.Boss, 414);
        s.Hp(2.21, Script.Boss, 1_329_228);

        var rec = s.Record();
        Assert.Equal(451_921, rec.TotalDamage);
        Assert.DoesNotContain(rec.Combatants, c => c.EntityId == Script.Add);
        Assert.Equal(414_000, rec.Targets.Single(t => t.EntityId == Script.Boss).SelfHealing);
        Assert.Equal(new long[] { 225_000, 189_000 }, rec.Hits.Where(h => h.Source == Script.Add).Select(h => h.Amount));
        Assert.All(rec.Hits.Where(h => h.Source == Script.Add), h => Assert.True(h.Flags.HasFlag(HitFlags.Heal)));
        Assert.Equal(281_149, rec.HpCheck!.BossSelfHealing);
        Assert.Equal(170_772, rec.HpCheck.HpLost);
        Assert.Equal(1.0, rec.HpCheck.Ratio, 8);
        Assert.True(rec.HpCheck.Passed);
    }

    [Fact]
    public void Known_npc_ally_healing_explains_a_full_hp_rise_but_a_later_unexplained_rise_is_a_wipe()
    {
        const uint healAllies = 1_220_140;
        var s = Script.Standard();
        s.Data.HealSkills.Add(healAllies);
        s.SpawnNpc(0, Script.Add, FakeGameData.AddCode, 10_000, 10_000);
        s.Hit(1, Script.Me, Script.Boss, 1_800_000);
        s.Hp(1.1, Script.Boss, 1_800_000);
        s.Hit(2, Script.Add, Script.Boss, 2_000_000, healAllies);
        s.Hp(2.1, Script.Boss, 3_600_000);
        Assert.Empty(s.Completed);
        Assert.Equal(EncounterOutcome.InProgress, s.Record().Outcome);
        Assert.Equal(1_800_000, s.Record().HpCheck!.BossSelfHealing);
        Assert.Equal(1.0, s.Record().HpCheck!.Ratio, 8);

        s.Hit(3, Script.Me, Script.Boss, 1_800_000);
        s.Hp(3.1, Script.Boss, 1_800_000);
        s.Hp(4, Script.Boss, 3_600_000); // no healing record explains this later reset
        Assert.Equal(EncounterOutcome.Wipe, s.Record().Outcome);
        Assert.Equal(1, Assert.Single(s.Record().Bosses).Resets);
    }

    [Fact]
    public void Unknown_npc_skills_do_not_fabricate_healing_to_make_the_hp_check_pass()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Add, FakeGameData.AddCode, 10_000, 10_000);
        s.Hit(1, Script.Me, Script.Boss, 100_000);
        s.Hp(1.1, Script.Boss, 3_500_000);
        s.Hit(2, Script.Add, Script.Boss, 50_000, Script.NpcSkill);
        s.Hp(2.1, Script.Boss, 3_550_000);

        var rec = s.Record();
        Assert.Equal(0, rec.HpCheck!.BossSelfHealing);
        Assert.False(rec.HpCheck.Passed);
        Assert.Equal(100_000, rec.TotalDamage);
        Assert.DoesNotContain(rec.Hits, h => h.Source == Script.Add);
    }

    [Fact]
    public void Npc_ally_hot_ticks_record_only_the_current_heal_and_can_prevent_false_wipes()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Add, FakeGameData.AddCode, 10_000, 10_000);
        s.Hit(1, Script.Me, Script.Boss, 1_800_000);
        s.Hp(1.1, Script.Boss, 1_800_000);
        s.Dot(2, Script.Add, Script.Boss, 0x0B, 3_000_000, heal: 1_800_000,
            effect: 122_014_011, skill: 1_220_140);
        s.Hp(2.1, Script.Boss, 3_600_000);

        Assert.Equal(EncounterOutcome.InProgress, s.Record().Outcome);
        Assert.Equal(1_800_000, s.Record().HpCheck!.BossSelfHealing);
        Assert.Equal(1.0, s.Record().HpCheck!.Ratio, 8);
        var heal = s.Record().Hits.Single(h => h.Source == Script.Add);
        Assert.Equal(1_800_000, heal.Amount);
        Assert.True(heal.Flags.HasFlag(HitFlags.Dot));
        Assert.True(heal.Flags.HasFlag(HitFlags.Heal));
    }

    [Fact]
    public void Removing_hostile_provisional_damage_recalculates_overheal_in_original_event_order()
    {
        const uint healAllies = 1_220_140;
        var s = Script.Standard();
        s.Data.HealSkills.Add(healAllies);
        s.SpawnNpc(0, Script.Add, FakeGameData.AddCode, 10_000, 10_000);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hp(1.1, Script.Boss, 3_599_900);
        s.Hit(2, Summon, Script.Boss, 444, Script.SpiritSkill, power: 33333);
        s.Hit(2.1, Script.Add, Script.Boss, 1000, healAllies);
        s.Hp(2.2, Script.Boss, 3_600_000);
        Assert.Equal(544, s.Record().HpCheck!.BossSelfHealing);
        s.SpawnSummon(2.3, Summon, owner: Script.Boss);

        Assert.Equal(100, s.Record().HpCheck!.BossSelfHealing);
        Assert.Equal(100, s.Record().TotalDamage);
        Assert.Equal(1.0, s.Record().HpCheck!.Ratio, 8);
    }
}
