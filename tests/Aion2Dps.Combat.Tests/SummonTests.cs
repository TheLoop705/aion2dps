using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public class SummonTests
{
    private const uint Summon = 9001;
    private const uint Summon2 = 9002;

    private static void AssertCredited(Script s, uint owner, long damage)
    {
        var rec = s.Record();
        var c = Script.Combatant(rec, owner);
        Assert.Equal(damage, c.Damage);
        Assert.Contains(c.Skills, x => x.FromSummon);
        Assert.DoesNotContain(rec.Combatants, x => x.EntityId == Summon);
        Assert.DoesNotContain(rec.Combatants, x => x.Kind == CombatantKind.UnknownSummons);
        var hit = rec.Hits.First(h => h.Source == Summon);
        Assert.Equal(owner, hit.Actor);
        Assert.True(hit.Flags.HasFlag(HitFlags.Summon));
    }

    [Fact]
    public void Owner_from_spawn_marker()
    {
        var s = Script.Standard();
        s.SpawnSummon(1, Summon, owner: Script.Ally);
        s.Hit(2, Summon, Script.Boss, 657, Script.SorSkill + 1, power: 12810);
        AssertCredited(s, Script.Ally, 657);
    }

    [Fact]
    public void Owner_from_anchor_when_not_self()
    {
        var s = Script.Standard();
        s.SpawnSummon(1, Summon, anchor: Summon); // anchor = itself: ignored
        s.SpawnSummon(1, Summon2, anchor: Script.Ally);
        s.Hit(2, Summon2, Script.Boss, 100, Script.SorSkill + 1, power: 44444);
        var rec = s.Record();
        Assert.Equal(100, Script.Combatant(rec, Script.Ally).Damage);
    }

    [Fact]
    public void Owner_from_inline_caster_name()
    {
        var s = Script.Standard();
        s.SpawnSummon(1, Summon, casterName: "Ally");
        s.Hit(2, Summon, Script.Boss, 900, 17_120_000, power: 55555);
        AssertCredited(s, Script.Ally, 900);
    }

    [Fact]
    public void Owner_from_cast_variant_link()
    {
        var s = Script.Standard();
        s.Notice(9.5, Script.Ally, Script.Ally, Script.SorSkill, power: 12810);
        s.Notice(9.6, Script.Me, Script.Me, Script.GlaSkill, power: 10170);
        s.SpawnSummon(10, Summon);
        s.Hit(11, Summon, Script.Boss, 657, Script.SorSkill + 1, power: 77777);
        AssertCredited(s, Script.Ally, 657);
    }

    [Fact]
    public void Cast_link_via_cast_event()
    {
        var s = Script.Standard();
        s.Send(new CastEvent { Time = Script.At(9.9), Actor = Script.Ally, SkillRaw = 15280140, Target = Script.Boss });
        s.SpawnSummon(10, Summon);
        s.Hit(10.5, Summon, Script.Boss, 321, 15280141, power: 77777);
        AssertCredited(s, Script.Ally, 321);
    }

    [Fact]
    public void Cast_link_outside_window_does_not_match()
    {
        var s = Script.Standard();
        s.Notice(5, Script.Ally, Script.Ally, Script.SorSkill, power: 12810); // 5 s before the spawn: too early
        s.SpawnSummon(10, Summon);
        s.Hit(11, Summon, Script.Boss, 657, Script.SorSkill + 1, power: 77777);
        var rec = s.Record();
        Assert.Equal(657, rec.Combatants.Single(c => c.Kind == CombatantKind.UnknownSummons).Damage);
    }

    [Fact]
    public void Owner_from_power_scalar_match()
    {
        var s = Script.Standard();
        s.Hit(5, Script.Me, Script.Boss, 100, power: 10170);
        s.Hit(5, Script.Ally, Script.Boss, 200, Script.SorSkill, power: 12810);
        s.SpawnSummon(10, Summon);
        s.Hit(11, Summon, Script.Boss, 657, 15_281_111, power: 12810);
        var rec = s.Record();
        Assert.Equal(857, Script.Combatant(rec, Script.Ally).Damage);
        Assert.Equal(100, Script.Combatant(rec, Script.Me).Damage);
    }

    [Fact]
    public void Ambiguous_power_scalar_does_not_match()
    {
        var s = Script.Standard();
        s.Hit(5, Script.Me, Script.Boss, 100, power: 12810);
        s.Hit(5, Script.Ally, Script.Boss, 200, Script.SorSkill, power: 12810);
        s.SpawnSummon(10, Summon);
        s.Hit(11, Summon, Script.Boss, 657, 15_281_111, power: 12810);
        var rec = s.Record();
        Assert.Equal(657, rec.Combatants.Single(c => c.Kind == CombatantKind.UnknownSummons).Damage);
    }

    [Fact]
    public void Spirit_skill_goes_to_the_only_elementalist()
    {
        var s = Script.Standard();
        s.Player(0, Script.Ele, "Spirity", 22);
        s.SpawnSummon(1, Summon);
        s.Hit(2, Summon, Script.Boss, 1031, Script.SpiritSkill, power: 66666);
        AssertCredited(s, Script.Ele, 1031);
    }

    [Fact]
    public void Orphan_spirit_without_spawn_goes_to_the_only_elementalist()
    {
        var s = Script.Standard();
        s.Player(0, Script.Ele, "Spirity", 22);
        s.Hit(2, Summon, Script.Boss, 964, Script.SpiritSkill, power: 66666);
        AssertCredited(s, Script.Ele, 964);
    }

    [Fact]
    public void Missed_helper_spawn_folds_into_caster_by_scalar_and_class()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Ally, Script.Boss, 500, Script.SorSkill, power: 12810);
        s.Hit(2, 9100, Script.Boss, 657, Script.SorSkill + 1, power: 12810); // unseen entity, Sorcerer skill, Ally's scalar
        var rec = s.Record();
        Assert.Equal(1157, Script.Combatant(rec, Script.Ally).Damage);
        Assert.DoesNotContain(rec.Combatants, c => c.EntityId == 9100);
    }

    [Fact]
    public void Unresolved_summon_goes_to_unknown_summons_bucket()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.SpawnSummon(1, Summon);
        s.Hit(2, Summon, Script.Boss, 444, 15_999_990, power: 33333);
        var snap = s.Snap(2);
        var bucket = Script.Row(snap, CombatEngine.UnknownSummonsEntityId);
        Assert.Equal(CombatantKind.UnknownSummons, bucket.Kind);
        Assert.Equal("Unknown summons", bucket.Name);
        Assert.Equal(444, bucket.Damage);
        Assert.DoesNotContain(snap.Rows, r => r.EntityId == Summon);
        var rec = s.Record();
        Assert.Equal(444, rec.Combatants.Single(c => c.Kind == CombatantKind.UnknownSummons).Damage);
        Assert.Equal(544, rec.TotalDamage);
    }

    [Fact]
    public void Late_owner_reattributes_earlier_hits()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.SpawnSummon(1, Summon, casterName: "Late");
        s.Hit(2, Summon, Script.Boss, 400, 15_999_990, power: 33333);
        s.Hit(3, Summon, Script.Boss, 600, 15_999_990, power: 33333);
        Assert.Equal(1000, Script.Row(s.Snap(3), CombatEngine.UnknownSummonsEntityId).Damage);

        s.Player(4, 250, "Late", 26);
        var snap = s.Snap(4);
        Assert.Equal(1000, Script.Row(snap, 250).Damage);
        Assert.DoesNotContain(snap.Rows, r => r.Kind == CombatantKind.UnknownSummons);

        s.Hit(5, Summon, Script.Boss, 500, 15_999_990, power: 33333);
        var rec = s.Record();
        var late = Script.Combatant(rec, 250);
        Assert.Equal(1500, late.Damage);
        Assert.Equal("Late", late.Name);
        Assert.Equal(3, late.Quality.Hits);
        Assert.DoesNotContain(rec.Combatants, c => c.Kind == CombatantKind.UnknownSummons);
        Assert.All(rec.Hits.Where(h => h.Source == Summon), h => Assert.Equal(250u, h.Actor));
    }

    [Fact]
    public void Late_cast_link_reattributes_when_summon_resolves_on_a_later_hit()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.SpawnSummon(10, Summon);
        s.Hit(10.05, Summon, Script.Boss, 300, Script.SorSkill + 1, power: 77777); // before the cast arrives
        s.Notice(10.2, Script.Ally, Script.Ally, Script.SorSkill, power: 12810);    // within +0.3 s of the spawn
        s.Hit(11, Summon, Script.Boss, 300, Script.SorSkill + 1, power: 77777);
        var rec = s.Record();
        Assert.Equal(600, Script.Combatant(rec, Script.Ally).Damage);
        Assert.DoesNotContain(rec.Combatants, c => c.Kind == CombatantKind.UnknownSummons);
    }

    [Fact]
    public void Owner_chain_is_followed_recursively()
    {
        var s = Script.Standard();
        s.SpawnSummon(1, Summon2, owner: Script.Me);
        s.SpawnSummon(1, Summon, owner: Summon2);
        s.Hit(2, Summon, Script.Boss, 250, 11_990_000, power: 1);
        Assert.Equal(250, Script.Combatant(s.Record(), Script.Me).Damage);
    }

    [Fact]
    public void Summon_owned_by_monster_is_hostile()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.SpawnSummon(1, Summon, owner: Script.Boss, code: 2100009);
        s.Hit(2, Summon, Script.Me, 700, Script.NpcSkill);
        s.Hit(3, Script.Me, Summon, 50);
        var rec = s.Record();
        var me = Script.Combatant(rec, Script.Me);
        Assert.Equal(700, me.DamageTaken);
        Assert.Equal(100, me.Damage); // boss encounter: the add is out of scope
        Assert.Equal(Summon, me.DamageTakenBySource.Single().SourceEntityId);
    }

    [Fact]
    public void Reused_summon_id_does_not_move_old_hits_to_new_owner()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.SpawnSummon(1, Summon, casterName: "Ghost");
        s.Hit(2, Summon, Script.Boss, 400, 15_999_990, power: 33333);
        s.SpawnSummon(3, Summon, owner: Script.Ally); // id reused
        s.Hit(4, Summon, Script.Boss, 200, Script.SorSkill + 1, power: 12810);
        s.Player(5, 251, "Ghost", 26);
        var rec = s.Record();
        Assert.Equal(200, Script.Combatant(rec, Script.Ally).Damage);
        Assert.Equal(400, rec.Combatants.Single(c => c.Kind == CombatantKind.UnknownSummons).Damage);
    }
}
