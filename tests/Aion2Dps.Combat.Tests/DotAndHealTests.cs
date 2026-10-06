using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public class DotAndHealTests
{
    [Fact]
    public void Dot_ticks_add_damage_but_not_hits()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 1000, dmgType: 3);
        s.Dot(2, Script.Me, Script.Boss, 0x0A, 500, effect: 1102003011, skill: Script.GlaSkill);
        s.Dot(3, Script.Me, Script.Boss, 0x02, 300, effect: 1103003011);                 // no skill field → effect / 100
        s.Dot(4, Script.Me, Script.Boss, 0x09, null, heal: 1000, effect: 1102003011);   // announcement: ignored
        s.Dot(5, Script.Me, Script.Boss, 0x08, null, effect: 1102003011);               // status tick: ignored
        s.Dot(6, Script.Me, Script.Me, 0x0A, 999, effect: 1102003011, skill: Script.GlaSkill); // actor == target: ignored
        s.Dot(7, Script.Me, Script.Boss, 0x0A, 200_000_000, effect: 1102003011, skill: Script.GlaSkill); // > 1e8

        var rec = s.Record();
        var me = Script.Combatant(rec, Script.Me);
        Assert.Equal(1800, me.Damage);
        Assert.Equal(1, me.Quality.Hits);
        Assert.Equal(1, me.Quality.Crits);
        Assert.Equal(2, me.Quality.DotTicks);
        Assert.Equal(800, me.Quality.DotDamage);
        var skill = me.Skills.Single(x => x.SkillId == SkillIds.BaseId(Script.GlaSkill));
        Assert.Equal(1, skill.Hits);
        Assert.Equal(1, skill.DotTicks);
        Assert.Equal(1500, skill.Damage);
        Assert.Contains(me.Skills, x => x.SkillId == SkillIds.BaseId(11030030) && x.DotDamage == 300);
        Assert.Equal(2, rec.Hits.Count(h => h.Flags.HasFlag(HitFlags.Dot)));

        var row = Script.Row(s.Snap(7), Script.Me);
        Assert.Equal(1, row.Hits);
        Assert.Equal(1.0, row.CritRate);
        Assert.Equal(1800, row.Damage);
    }

    [Fact]
    public void Dot_tick_does_not_start_an_encounter()
    {
        var s = Script.Standard();
        s.Dot(1, Script.Me, Script.Boss, 0x0A, 500, effect: 1102003011, skill: Script.GlaSkill);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(1).State);
    }

    [Fact]
    public void Hot_tick_heals_by_the_heal_field()
    {
        var s = Script.Standard();
        s.Player(0, Script.Cleric, "Chanty", 34);
        s.Hit(1, Script.Me, Script.Boss, 1000);
        s.Dot(2, Script.Cleric, Script.Me, 0x09, null, heal: 334, effect: 1805000001);
        s.Dot(3, Script.Cleric, Script.Me, 0x0B, 251, heal: 83, effect: 1805000001, skill: FakeGameData.RecuperationSkill);
        s.Dot(4, Script.Cleric, Script.Me, 0x0B, 168, heal: 83, effect: 1805000001, skill: FakeGameData.RecuperationSkill);
        var rec = s.Record();
        var chanter = Script.Combatant(rec, Script.Cleric);
        Assert.Equal(166, chanter.Healing);
        Assert.Equal(0, chanter.Damage);
        Assert.Equal(166, Script.Combatant(rec, Script.Me).Defense.HealingReceived);
        Assert.All(rec.Hits.Where(h => h.Actor == Script.Cleric), h => Assert.Equal(HitFlags.Heal | HitFlags.Dot, h.Flags));
    }

    [Fact]
    public void Boss_self_heal_trap_is_target_self_heal_not_player_damage()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 30_697, Script.RanSkill);
        s.Dot(2, Script.Me, Script.Boss, 0x0A, 122_788, effect: 230010401, skill: Script.RanSkill);
        var rec = s.Record();
        Assert.Equal(30_697, Script.Combatant(rec, Script.Me).Damage);
        Assert.Equal(0, Script.Combatant(rec, Script.Me).Quality.DotTicks);
        var boss = rec.Targets.Single(t => t.EntityId == Script.Boss);
        Assert.Equal(122_788, boss.SelfHealing);
        Assert.Equal(30_697, boss.DamageTaken);
        var heal = rec.Hits.Single(h => h.Amount == 122_788);
        Assert.Equal(Script.Boss, heal.Actor);
        Assert.Equal(Script.Boss, heal.Target);
        Assert.True(heal.Flags.HasFlag(HitFlags.Heal));
    }

    [Fact]
    public void Monster_dot_on_player_is_incoming_damage()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Dot(2, Script.Boss, Script.Me, 0x0A, 400, effect: 150000101, skill: Script.NpcSkill);
        var me = Script.Combatant(s.Record(), Script.Me);
        Assert.Equal(400, me.DamageTaken);
        Assert.Equal(0, me.Defense.HitsTaken);
    }
}
