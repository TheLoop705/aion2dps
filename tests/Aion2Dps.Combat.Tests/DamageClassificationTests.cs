using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public class DamageClassificationTests
{
    [Fact]
    public void Dps_uses_encounter_clock_in_record_and_own_clock_live()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Trash2, FakeGameData.TrashCode, 1_000_000, 1_000_000);
        s.Hit(10, Script.Me, Script.Trash2, 1000);
        s.Hit(15, Script.Me, Script.Trash2, 2000);
        s.Hit(15, Script.Ally, Script.Trash2, 1500, Script.SorSkill);
        s.Hit(20, Script.Me, Script.Trash2, 3000);
        s.Hit(20, Script.Ally, Script.Trash2, 1500, Script.SorSkill);

        var live = s.Snap(20);
        Assert.Equal(MeterState.InCombat, live.State);
        Assert.Equal(600, Script.Row(live, Script.Me).Dps, 6);        // 6000 / (20 − 10)
        Assert.Equal(600, Script.Row(live, Script.Ally).Dps, 6);      // 3000 / (20 − 15): own clock
        Assert.Equal(400, Script.Row(s.Snap(25), Script.Me).Dps, 6);  // rolling: 6000 / 15
        Assert.Equal(9000, live.TotalDamage);
        Assert.Equal(900, live.PartyDps, 6);
        Assert.Equal(TimeSpan.FromSeconds(10), live.Elapsed);

        var rec = s.Record();
        Assert.Equal(10, rec.DurationSeconds, 6);
        Assert.Equal(600, Script.Combatant(rec, Script.Me).Dps, 6);
        Assert.Equal(300, Script.Combatant(rec, Script.Ally).Dps, 6);
        Assert.Equal(600, Script.Combatant(rec, Script.Ally).ActiveDps, 6);
        Assert.Equal(900, rec.PartyDps, 6);
        Assert.Equal(9000, rec.TotalDamage);

        s.Tick(31); // idle 10 s
        var ended = s.Snap(40);
        Assert.Equal(MeterState.Ended, ended.State);
        Assert.Equal(EncounterOutcome.Timeout, ended.Outcome);
        Assert.Equal(300, Script.Row(ended, Script.Ally).Dps, 6); // final numbers: encounter clock
    }

    [Fact]
    public void Shared_encounter_clock_when_live_player_clock_is_off()
    {
        var s = Script.Standard(new EngineOptions { LivePlayerClock = false });
        s.Hit(10, Script.Me, Script.Trash, 1000);
        s.Hit(15, Script.Ally, Script.Trash, 3000, Script.SorSkill);
        var snap = s.Snap(20);
        Assert.Equal(300, Script.Row(snap, Script.Ally).Dps, 6); // 3000 / (20 − 10)
        Assert.Equal(100, Script.Row(snap, Script.Me).Dps, 6);
    }

    [Fact]
    public void Single_hit_encounter_has_minimum_one_second_duration()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Trash, 777);
        var rec = s.Record();
        Assert.Equal(1, rec.DurationSeconds);
        Assert.Equal(777, Script.Combatant(rec, Script.Me).Dps, 6);
    }

    [Fact]
    public void Hit_quality_rates_and_measured_hits()
    {
        var s = Script.Standard();
        // layout 6 (measured): crit+back+perfect, front+double, smite, parry, plain
        s.Hit(1, Script.Me, Script.Boss, 100, layout: 6, dmgType: 3, mods: HitMods.Perfect, dir: HitDirection.Back, hitTag: 1);
        s.Hit(2, Script.Me, Script.Boss, 100, layout: 6, mods: HitMods.Double, dir: HitDirection.Front, hitTag: 2);
        s.Hit(3, Script.Me, Script.Boss, 100, layout: 6, mods: HitMods.Smite | HitMods.Perfect, hitTag: 3);
        s.Hit(4, Script.Me, Script.Boss, 50, layout: 6, mods: HitMods.Parry, hitTag: 4);
        s.Hit(5, Script.Me, Script.Boss, 100, layout: 6, hitTag: 5);
        // layout 4 (not measured): crit, and a direction value that must be ignored
        s.Hit(6, Script.Me, Script.Boss, 100, layout: 4, dmgType: 3, dir: HitDirection.Back, mods: HitMods.Perfect, hitTag: 6);

        var q = Script.Combatant(s.Record(), Script.Me).Quality;
        Assert.Equal(6, q.Hits);
        Assert.Equal(2, q.Crits);
        Assert.Equal(5, q.QualityMeasuredHits);
        Assert.Equal(1, q.Back);
        Assert.Equal(1, q.Front);
        Assert.Equal(2, q.Perfect);
        Assert.Equal(1, q.Double);
        Assert.Equal(1, q.Smite);
        Assert.Equal(1, q.Parried);
        Assert.Equal(100, q.MaxHit);

        var skill = Assert.Single(Script.Combatant(s.Record(), Script.Me).Skills);
        Assert.Equal(SkillIds.BaseId(Script.GlaSkill), skill.SkillId);
        Assert.Equal(5, skill.QualityMeasuredHits);
        Assert.Equal(2, skill.Perfect);
        Assert.Equal(1, skill.Parried);
        Assert.Equal(50, skill.MinHit);
        Assert.Equal(100, skill.MaxHit);
        Assert.Equal(6, skill.Casts);

        var row = Script.Row(s.Snap(6), Script.Me);
        Assert.Equal(2.0 / 6, row.CritRate, 6);
        Assert.Equal(6, row.Hits);

        var hits = s.Record().Hits.Where(h => h.Actor == Script.Me).ToList();
        Assert.True(hits[0].Flags.HasFlag(HitFlags.Crit | HitFlags.Back | HitFlags.Perfect));
        Assert.True(hits[3].Flags.HasFlag(HitFlags.Parry));
        Assert.False(hits[5].Flags.HasFlag(HitFlags.Back));
        Assert.False(hits[5].Flags.HasFlag(HitFlags.Perfect));
    }

    [Fact]
    public void Quality_unmeasured_when_no_mods_layout()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(2, Script.Me, Script.Boss, 100);
        var q = Script.Combatant(s.Record(), Script.Me).Quality;
        Assert.Equal(2, q.Hits);
        Assert.Equal(0, q.QualityMeasuredHits);
    }

    [Fact]
    public void Multi_hit_amount_already_includes_extra_hits()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 588, layout: 6, dmgType: 3, extra: new uint[] { 2 });
        s.Hit(2, Script.Me, Script.Boss, 30_697, Script.RanSkill, layout: 6, extra: new uint[] { 3069, 3069, 3069, 3069 });
        var c = Script.Combatant(s.Record(), Script.Me);
        Assert.Equal(588 + 30_697, c.Damage);
        Assert.Equal(2, c.Quality.Hits);
        Assert.Equal(2, c.Quality.MultiHits);
        Assert.Equal(5, c.Quality.ExtraHitCount);
        var hit = s.Record().Hits.First(h => h.Skill == Script.RanSkill);
        Assert.True(hit.Flags.HasFlag(HitFlags.MultiHit));
        Assert.Equal(4, hit.ExtraHits);
    }

    [Fact]
    public void Amount_sanity_cap_drops_placeholder_records()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 1000);
        s.Hit(2, Script.Me, Script.Boss, 250_000_000);
        s.Hit(3, Script.Me, Script.Boss, 99_999_999);
        Assert.Equal(1000 + 99_999_999, Script.Combatant(s.Record(), Script.Me).Damage);
        Assert.Equal(1, s.Engine.DroppedRecords);
    }

    [Fact]
    public void Layout0_cast_notices_are_not_hits()
    {
        var s = Script.Standard();
        s.Notice(1, Script.Me, Script.Me, Script.GlaSkill);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(1).State);
        s.Hit(2, Script.Me, Script.Boss, 381, layout: 6);
        s.Notice(2, Script.Me, Script.Boss, Script.GlaSkill);
        s.Notice(2, Script.Me, Script.Me, Script.GlaSkill);
        var c = Script.Combatant(s.Record(), Script.Me);
        Assert.Equal(1, c.Quality.Hits);
        Assert.Equal(381, c.Damage);
        Assert.Single(s.Record().Hits);
    }

    [Fact]
    public void Link_skills_are_ignored()
    {
        var s = Script.Standard();
        s.SpawnSummon(0, 9001, owner: Script.Me);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(2, 9001, 9001, 110_000, 16_990_001);
        s.Hit(3, Script.Me, 9001, 500, 16_770_005);
        s.Hit(4, 9001, Script.Boss, 5000, 16_995_000);
        Assert.Equal(100, Script.Combatant(s.Record(), Script.Me).Damage);
        Assert.Single(s.Record().Hits);
    }

    [Fact]
    public void Heal_family_skill_is_healing_not_damage()
    {
        var s = Script.Standard();
        s.Player(0, Script.Cleric, "Healer", 30);
        s.Hit(1, Script.Me, Script.Boss, 1000);
        s.Hit(2, Script.Cleric, Script.Me, 5000, FakeGameData.HealSkill);
        s.Hit(3, Script.Cleric, Script.Ally, 4000, FakeGameData.HealSkill + 10);
        var rec = s.Record();
        var cleric = Script.Combatant(rec, Script.Cleric);
        Assert.Equal(0, cleric.Damage);
        Assert.Equal(9000, cleric.Healing);
        Assert.Equal(5000, Script.Combatant(rec, Script.Me).Defense.HealingReceived);
        Assert.Equal(9000, cleric.Skills.Sum(x => x.Healing));
        Assert.Equal(1000, rec.TotalDamage);
        Assert.All(rec.Hits.Where(h => h.Actor == Script.Cleric), h => Assert.True(h.Flags.HasFlag(HitFlags.Heal)));
        Assert.Equal(9000, Script.Row(s.Snap(3), Script.Cleric).Healing);
        Assert.False(Script.Row(s.Snap(3), Script.Me).IsDead);
    }

    [Fact]
    public void Heal_family_skill_on_a_monster_is_damage()
    {
        var s = Script.Standard();
        s.Player(0, Script.Cleric, "Healer", 30);
        s.Hit(1, Script.Cleric, Script.Boss, 2500, FakeGameData.HealSkill);
        Assert.Equal(2500, Script.Combatant(s.Record(), Script.Cleric).Damage);
    }

    [Fact]
    public void Heal_on_unseen_target_never_becomes_damage()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 1000);
        s.Hit(2, Script.Me, 4242, 900, FakeGameData.RecuperationSkill);
        var rec = s.Record();
        Assert.Equal(1000, rec.TotalDamage);
        Assert.Equal(900, Script.Combatant(rec, Script.Me).Healing);
        Assert.DoesNotContain(rec.Targets, t => t.EntityId == 4242);
    }

    [Fact]
    public void Actor_equal_target_from_player_is_self_heal()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 381);
        s.Hit(1.1, Script.Me, Script.Me, 65, Script.SelfHealSkill);
        var me = Script.Combatant(s.Record(), Script.Me);
        Assert.Equal(381, me.Damage);
        Assert.Equal(65, me.Healing);
        Assert.Equal(1, me.Quality.Hits);
    }

    [Fact]
    public void Dodge_pseudo_skill_counts_dodges_not_damage()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 1000);
        s.Hit(2, Script.Me, Script.Boss, 0, SkillIds.Dodge);           // the boss dodged me
        s.Hit(3, Script.Boss, Script.Me, 0, SkillIds.Dodge);           // I dodged the boss
        s.Hit(4, Script.Boss, Script.Me, 2000, Script.NpcSkill, layout: 6, dmgType: 3, dir: HitDirection.Back);
        var me = Script.Combatant(s.Record(), Script.Me);
        Assert.Equal(1000, me.Damage);
        Assert.Equal(1, me.Quality.Dodged);
        Assert.Equal(1, me.Quality.Hits);
        Assert.Equal(1, me.Defense.Dodged);
        Assert.Equal(1, me.Defense.HitsTaken);
        Assert.Equal(1, me.Defense.CritsTaken);
        Assert.Equal(1, me.Defense.BackHitsTaken);
        Assert.Equal(2000, me.DamageTaken);
        Assert.Null(me.Defense.Blocked);
        Assert.Null(me.Defense.Endured);
        Assert.Null(me.Defense.Resisted);
        Assert.Equal(2, s.Record().Hits.Count(h => h.Flags.HasFlag(HitFlags.Dodged)));
    }

    [Fact]
    public void Incoming_damage_is_damage_taken_by_source()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 1000);
        s.Hit(2, Script.Boss, Script.Me, 3000, Script.NpcSkill);
        s.Hit(3, Script.Boss, Script.Me, 1000, Script.NpcSkill);
        s.Hit(4, Script.Boss, Script.Me, 500, Script.NpcSkill + 1);
        s.Dot(5, Script.Boss, Script.Me, 0x0A, 250, effect: 150000201, skill: Script.NpcSkill);
        var rec = s.Record();
        var me = Script.Combatant(rec, Script.Me);
        Assert.Equal(4750, me.DamageTaken);
        Assert.Equal(3, me.Defense.HitsTaken);
        var src = me.DamageTakenBySource.First();
        Assert.Equal(Script.Boss, src.SourceEntityId);
        Assert.Equal(FakeGameData.BossCode, src.SourceNpcCode);
        Assert.Equal(Script.NpcSkill, src.SkillId);
        Assert.Equal(4250, src.Damage);
        Assert.Equal(2, src.Hits);
        Assert.All(rec.Hits.Where(h => h.Actor == Script.Boss), h => Assert.True(h.Flags.HasFlag(HitFlags.Incoming)));
        Assert.Equal(1000, rec.TotalDamage);
        Assert.Equal(4750, Script.Row(s.Snap(5), Script.Me).DamageTaken);
    }

    [Fact]
    public void Unknown_attacker_with_class_skill_becomes_provisional_player_and_unknown_target_an_npc()
    {
        var s = Script.Standard();
        s.Hit(1, 777, 9999, 1234, Script.RanSkill);
        s.Hit(2, 777, 9999, 1000, Script.RanSkill);
        var snap = s.Snap(2);
        var row = Script.Row(snap, 777);
        Assert.Equal("Player 777", row.Name);
        Assert.Equal(CharacterClass.Ranger, row.Class);
        Assert.Equal(2234, row.Damage);
        Assert.Equal(9999u, snap.Target!.EntityId);

        s.Player(3, 777, "Pencilgon", 18);
        Assert.Equal("Pencilgon", Script.Row(s.Snap(3), 777).Name);
    }

    [Fact]
    public void Generic_skill_from_unknown_actor_is_ignored()
    {
        var s = Script.Standard();
        s.Hit(1, 888, Script.Boss, 1000, 11000500); // sub-id 0 = generic
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(1).State);
    }

    [Fact]
    public void Theostone_proc_counts_for_player()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(2, Script.Me, Script.Boss, 400, 3_000_123); // raw theostone id → 30001231
        var rec = s.Record();
        Assert.Equal(500, Script.Combatant(rec, Script.Me).Damage);
        Assert.Contains(rec.Hits, h => h.Skill == 30_001_231);
    }
}
