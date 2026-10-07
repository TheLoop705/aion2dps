using Aion2Dps.Contracts;
using static Aion2Dps.Combat.Tests.Script;

namespace Aion2Dps.Combat.Tests;

/// <summary>
/// Regression tests for the correctness review of the merged engine (charmed summons, boss mechanic entities, false
/// wipes, kill grace, unseen players, revive, character switch). Scenarios mirror real traffic of the 2026-10-06
/// Fire Temple capture.
/// </summary>
public class ReviewCorrectnessTests
{
    private const uint CharmSkill = 1801117;   // NPC "Attack" used by mind-controlled spirits / Kromede's entities
    private const uint GasRockSkill = 1600750; // Murute's Gas Rock explosion
    private const uint GasRockCode = 2920091;

    private static void GasRock(Script s, double t, uint id, string targetName) =>
        s.Send(new SpawnEvent
        {
            Time = At(t), Entity = id, KindByte = 0x1C, KindFlags = 0x00, NpcCode = GasRockCode, HpCurrent = 100, HpMax = 100,
            AnchorId = id, CasterName = targetName, IsSummonLike = true,
        });

    // ───────────── PvP classification ─────────────

    [Fact]
    public void Charmed_ally_summon_hitting_me_with_an_npc_skill_is_damage_taken_not_pvp()
    {
        var s = Standard(); // no roster
        s.SpawnSummon(0, 9000, owner: Ally);
        s.Hit(1, Me, Boss, 1000);
        s.Hit(1.5, Ally, Boss, 5000, skill: SorSkill);
        s.Hit(2, 9000, Me, 800, skill: CharmSkill);
        s.Hit(3, Ally, Boss, 5000, skill: SorSkill);
        s.Hit(10, Me, Boss, 1000);

        var snap = s.Snap(10);
        Assert.Empty(snap.PvpRows);
        Assert.Equal(10_000, Row(snap, Ally).Damage);
        var rec = s.Record();
        Assert.Equal(12_000, rec.TotalDamage);
        Assert.DoesNotContain(rec.Combatants, c => c.Kind == CombatantKind.EnemyPlayer);
        Assert.Equal(800, Combatant(rec, Me).DamageTaken);
    }

    [Fact]
    public void Charmed_ally_hitting_me_with_a_class_skill_after_pve_damage_is_not_pvp()
    {
        var s = Standard();
        s.Hit(1, Me, Boss, 1000);
        s.Hit(1.5, Ally, Boss, 5000, skill: SorSkill);
        s.Hit(2, Ally, Me, 800, skill: SorSkill);
        s.Hit(3, Ally, Boss, 5000, skill: SorSkill);

        var rec = s.Record();
        Assert.Equal(11_000, rec.TotalDamage);
        Assert.Equal(CombatantKind.Player, Combatant(rec, Ally).Kind);
        Assert.Equal(10_000, Combatant(rec, Ally).Damage);
        Assert.Empty(s.Snap(3).PvpRows);
    }

    [Fact]
    public void A_player_flagged_before_the_roster_arrived_is_unflagged_by_the_roster()
    {
        var s = Standard();
        s.Hit(1, Ally, Me, 800, skill: SorSkill); // e.g. charmed before any PvE damage
        s.Roster(2, ("Me", 6), ("Ally", 26));
        s.Hit(40, Me, Boss, 1000);
        s.Hit(41, Ally, Boss, 5000, skill: SorSkill);

        var snap = s.Snap(41);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        Assert.Equal(5000, Row(snap, Ally).Damage);
    }

    // ───────────── boss mechanic entities named after their target ─────────────

    [Fact]
    public void Gas_rocks_named_after_their_target_are_boss_damage_not_player_summons()
    {
        var s = Standard();
        s.Hit(1, Me, Boss, 1000);
        s.Hit(1.5, Ally, Boss, 5000, skill: SorSkill);
        GasRock(s, 2, 9100, "Me");
        GasRock(s, 2, 9101, "Ally");
        s.Hit(4, 9100, Me, 1444, skill: GasRockSkill, layout: 6); // a rock explodes on its own target
        s.Hit(4, 9101, Me, 1260, skill: GasRockSkill, layout: 6); // the neighbour's rock hits me too
        s.Hit(4.5, 9100, Trash, 3000, skill: GasRockSkill);       // a rock hitting a mob is never my DPS
        s.Hit(5, Ally, Boss, 5000, skill: SorSkill);

        var rec = s.Record();
        Assert.Equal(11_000, rec.TotalDamage);
        Assert.Equal(1000, Combatant(rec, Me).Damage);
        Assert.Equal(10_000, Combatant(rec, Ally).Damage);
        Assert.Equal(2704, Combatant(rec, Me).DamageTaken);
        Assert.DoesNotContain(rec.Combatants, c => c.Kind == CombatantKind.EnemyPlayer);
        Assert.Empty(s.Snap(5).PvpRows);
    }

    [Fact]
    public void A_caster_name_still_resolves_a_summon_acting_with_player_skills()
    {
        var s = Standard();
        s.SpawnSummon(0, 9300, casterName: "Ally");
        s.Hit(1, Me, Boss, 1000);
        s.Hit(1.2, 9300, Boss, 4000, skill: SpiritSkill);
        var snap = s.Snap(2);
        Assert.Equal(4000, Row(snap, Ally).Damage);
    }

    // ───────────── unvalidated damage records ─────────────

    [Fact]
    public void Damage_records_that_fail_the_effect_check_are_not_counted()
    {
        var s = Standard();
        s.Hit(1, Me, Boss, 1000);
        s.Send(new DamageEvent
        {
            Time = At(2), Target = Ally, Actor = 33812, SkillRaw = 1801117, SkillId = 1801117, Switch = 6, Layout = 6,
            HitTag = 1, DamageType = 2, Mods = HitMods.Smite, EffectId = 3158925056, HitIndex = 266, PowerScalar = 0,
            Amount = 10_000, EffectValidated = false,
        });
        s.Send(new DamageEvent
        {
            Time = At(3), Target = Boss, Actor = Me, SkillRaw = GlaSkill, SkillId = GlaSkill, Switch = 6, Layout = 6,
            HitTag = 1, DamageType = 2, EffectId = 12345, HitIndex = 1, PowerScalar = 10170, Amount = 15_000,
            EffectValidated = false,
        });
        var rec = s.Record();
        Assert.Equal(1000, rec.TotalDamage);
        Assert.Equal(0, rec.Combatants.SingleOrDefault(c => c.EntityId == Ally)?.DamageTaken ?? 0);
    }

    // ───────────── boss self-heal vs wipe ─────────────

    [Fact]
    public void Boss_self_heal_back_to_first_seen_hp_is_not_a_wipe()
    {
        var s = new Script();
        s.Map(0, Map1);
        s.Self(0);
        s.Hp(0.5, Boss, 6_000_000); // meter started mid-fight: max = highest HP seen
        for (int i = 1; i <= 20; i++)
        {
            s.Hit(i, Me, Boss, 40_000);
            s.Hp(i + 0.1, Boss, 6_000_000 - 40_000 * i); // → 5.2 M (86.7 %)
        }
        s.Dot(21, Me, Boss, 0x0A, 800_000, effect: 230000011, skill: GlaSkill); // boss self-heal trap
        s.Hp(21.1, Boss, 6_000_000);
        s.Hit(22, Me, Boss, 20_000);
        s.Hp(22.1, Boss, 5_980_000);

        var snap = s.Snap(22);
        Assert.DoesNotContain(s.Completed, r => r.Outcome == EncounterOutcome.Wipe);
        Assert.Equal(EncounterOutcome.InProgress, snap.Outcome);
        Assert.Equal(820_000, snap.TotalDamage);
    }

    [Fact]
    public void Boss_heal_mechanic_with_a_trusted_max_is_not_a_wipe()
    {
        var s = Standard();
        s.Hit(1, Me, Boss, 400_000);
        s.Hp(1.1, Boss, 3_200_000);
        s.Hit(2, Me, Boss, 200_000);
        s.Hp(2.1, Boss, 3_000_000); // 83 %
        s.Dot(3, Me, Boss, 0x0A, 600_000, effect: 230000011, skill: GlaSkill);
        s.Hp(3.1, Boss, 3_600_000);
        s.Hit(4, Me, Boss, 100_000);

        Assert.DoesNotContain(s.Completed, r => r.Outcome == EncounterOutcome.Wipe);
        Assert.Equal(700_000, s.Snap(4).TotalDamage);
    }

    [Fact]
    public void A_real_reset_with_a_highest_seen_max_is_still_a_wipe()
    {
        var s = new Script();
        s.Map(0, Map1);
        s.Self(0);
        s.Hp(0.5, Boss, 6_000_000);
        for (int i = 1; i <= 20; i++)
        {
            s.Hit(i, Me, Boss, 40_000);
            s.Hp(i + 0.1, Boss, 6_000_000 - 40_000 * i);
        }
        s.Hp(28, Boss, 6_000_000); // party dead, nobody hitting, boss back to full

        Assert.Contains(s.Completed, r => r.Outcome == EncounterOutcome.Wipe);
    }

    // ───────────── kill grace ─────────────

    [Fact]
    public void Late_killing_blow_after_an_add_hit_goes_to_the_kill_and_never_restarts_a_boss_fight()
    {
        var s = Standard();
        s.SpawnNpc(0, Add, FakeGameData.AddCode, 50_000, 50_000);
        s.Hit(1, Me, Boss, 1_000_000);
        s.Hit(6, Me, Boss, 2_600_000);
        s.Hp(6.05, Boss, 0);
        s.Hit(6.2, Me, Add, 1000);
        s.Hit(6.5, Ally, Boss, 5000, skill: SorSkill); // killing-blow packet arriving late

        var snap = s.Snap(7);
        Assert.False(snap.EncounterKind == EncounterKind.Boss && snap.Outcome == EncounterOutcome.InProgress,
            $"bogus boss encounter on the corpse: {snap.EncounterKind} {snap.Outcome}");
        s.Tick(60);
        var kill = Assert.Single(s.Completed, r => r.Kind == EncounterKind.Boss);
        Assert.Equal(EncounterOutcome.Kill, kill.Outcome);
        Assert.Equal(3_605_000, kill.TotalDamage);
    }

    // ───────────── unseen players ─────────────

    [Fact]
    public void Unseen_player_hit_by_a_charmed_spirit_keeps_their_damage()
    {
        var s = Standard();
        s.SpawnSummon(0, 9000, owner: Ally);
        s.Hit(1, Me, Boss, 1000);
        s.Hit(2, 9000, 700, 800, skill: CharmSkill); // 700 = party member whose 45 36 we missed
        s.Hit(3, 700, Boss, 9000, skill: GlaSkill2, power: 15_000);
        s.Hit(4, 700, Boss, 9000, skill: GlaSkill2, power: 15_000);

        var rec = s.Record();
        Assert.Equal(19_000, rec.TotalDamage);
        Assert.Equal(18_000, Combatant(rec, 700).Damage);
    }

    [Fact]
    public void Unknown_npc_that_acts_with_a_class_skill_is_rebound_as_a_player()
    {
        var s = Standard();
        s.Hit(1, Me, Boss, 1000);
        s.Hit(2, Ally, 700, 800, skill: SorSkill); // a friendly record onto an id we have never seen → "unknown NPC"
        s.Hit(3, 700, Boss, 9000, skill: GlaSkill2, power: 15_000);
        s.Hit(4, 700, Boss, 9000, skill: GlaSkill2, power: 15_000);

        var rec = s.Record();
        Assert.Equal(18_000, Combatant(rec, 700).Damage);
        Assert.DoesNotContain(rec.Targets, t => t.EntityId == 700);
        Assert.Equal(0, rec.Combatants.SingleOrDefault(c => c.EntityId == Ally)?.Damage ?? 0);
    }

    [Fact]
    public void A_provisional_player_drinking_a_potion_stays_a_player()
    {
        var s = Standard();
        s.Hit(1, 701, Boss, 4000, skill: GlaSkill2, power: 15_000); // known only from its skills
        s.Hit(2, 701, 701, 500, skill: 2010101);                    // real traffic: consumables use NPC-range ids, self-targeted
        s.Hit(3, 701, Boss, 4000, skill: GlaSkill2, power: 15_000);

        Assert.Equal(8000, Combatant(s.Record(), 701).Damage);
    }

    // ───────────── unbound summon owners ─────────────

    [Fact]
    public void Boss_owned_entity_hit_before_the_boss_is_known_never_makes_the_boss_a_player()
    {
        var s = new Script();
        s.Map(0, Map1);
        s.Self(0);
        s.SpawnSummon(1, 9200, owner: Boss, code: 2920342);
        s.Hit(2, Me, 9200, 500);
        s.Hit(3, Me, Boss, 100_000);
        s.Hit(4, Me, Boss, 100_000);

        var snap = s.Snap(4);
        Assert.NotEqual(EncounterKind.Pvp, snap.EncounterKind);
        Assert.Empty(snap.PvpRows);
        Assert.Equal(200_000, snap.TotalDamage);
    }

    // ───────────── re-spawn with an unrecognised head ─────────────

    [Fact]
    public void Respawn_with_an_unrecognised_head_keeps_the_npc_code_and_max_hp()
    {
        var s = Standard();
        s.Send(new SpawnEvent { Time = At(0.5), Entity = Boss, KindByte = 0x0C, KindFlags = 0x22, NpcCode = 0 });
        s.Hit(1, Me, Boss, 10_000);

        var snap = s.Snap(1);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        var rec = s.Record();
        Assert.Equal(FakeGameData.BossCode, rec.BossNpcCode);
        Assert.Equal(3_600_000, rec.BossMaxHp);
    }

    // ───────────── revive ─────────────

    [Fact]
    public void A_revived_player_who_dies_again_counts_two_deaths()
    {
        var s = Standard();
        s.Hit(1, Me, Boss, 1000);
        s.Hit(1.5, Ally, Boss, 5000, skill: SorSkill);
        s.Death(2, Ally);
        s.Hp(2.1, Ally, 0);
        s.Hp(5, Ally, 6000); // revived, no respawn record
        s.Death(8, Ally);
        s.Hit(9, Me, Boss, 1000);

        Assert.Equal(2, Combatant(s.Record(), Ally).Deaths);
    }

    [Fact]
    public void An_enemy_killed_in_an_earlier_fight_is_not_shown_killed_after_reviving()
    {
        var s = Standard();
        s.Player(0, Enemy, "Rival", 14);
        s.Hit(1, Me, Enemy, 1000);
        s.Kill(1.5, Enemy, Me, "Me");
        s.Tick(30); // PvP encounter over
        s.Hp(40, Enemy, 8000); // revived
        s.Hit(45, Me, Enemy, 500);

        var rec = s.Record();
        var t = Assert.Single(rec.Targets, x => x.EntityId == Enemy);
        Assert.False(t.Killed);
    }

    // ───────────── character switch ─────────────

    [Fact]
    public void Character_switch_saves_the_old_fight_under_the_old_character()
    {
        var s = Standard();
        s.Hit(1, Me, Boss, 10_000);
        s.Hit(12, Me, Boss, 10_000);
        s.Self(13, id: 150, name: "Alt", classCode: 26);

        var rec = Assert.Single(s.Completed);
        Assert.Equal("Me", rec.LocalPlayerName);
        Assert.Equal(CharacterClass.Gladiator, rec.LocalPlayerClass);
        Assert.Equal("Me", Combatant(rec, Me).Name);
    }
}
