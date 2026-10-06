using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public class PvpTests
{
    [Fact]
    public void Pvp_rows_track_damage_both_ways_and_kills()
    {
        var s = Script.Standard();
        s.Player(0, Script.Enemy, "Rival", 14, guild: "Ventus");
        s.Hit(1, Script.Me, Script.Enemy, 1000);
        s.Hit(2, Script.Enemy, Script.Me, 500, Script.RanSkill);
        s.Hit(6, Script.Me, Script.Enemy, 2000, layout: 6, dmgType: 3);
        s.Send(new HpUpdateEvent { Time = Script.At(6.1), Entity = Script.Enemy, Hp = 4000, HpMax = 8000 });

        s.Engine.Mode = MeterMode.Pvp;
        var snap = s.Snap(6.2);
        Assert.StartsWith("PvP", snap.StatusText);
        Assert.Equal(EncounterKind.Pvp, snap.EncounterKind);
        var row = Assert.Single(snap.PvpRows);
        Assert.Equal("Rival", row.Name);
        Assert.Equal("Ventus", row.GuildName);
        Assert.Equal(CharacterClass.Ranger, row.Class);
        Assert.Equal(3000, row.DamageDealt);
        Assert.Equal(500, row.DamageTaken);
        Assert.Equal(0.5, row.HpFraction!.Value, 6);
        Assert.False(row.Killed);
        Assert.DoesNotContain(snap.Rows, r => r.EntityId == Script.Enemy);

        s.Kill(7, Script.Enemy, Script.Me, "Me");
        var after = s.Snap(7);
        Assert.True(Assert.Single(after.PvpRows).Killed);
        Assert.Equal(1, after.PvpKills);

        s.Tick(20);
        var rec = Assert.Single(s.Completed);
        Assert.Equal(EncounterKind.Pvp, rec.Kind);
        var enemy = rec.Combatants.Single(c => c.EntityId == Script.Enemy);
        Assert.Equal(CombatantKind.EnemyPlayer, enemy.Kind);
        Assert.Equal(500, enemy.DamageToLocal);
        Assert.Equal(3000, enemy.DamageFromLocal);
        Assert.True(enemy.KilledByLocal);
        Assert.Equal(2, enemy.Defense.HitsTaken);
        var me = Script.Combatant(rec, Script.Me);
        Assert.Equal(3000, me.Damage);
        Assert.Equal(500, me.DamageTaken);
        Assert.Equal("Rival", me.DamageTakenBySource.Single().SourcePlayerName);
        Assert.Equal(3000, rec.TotalDamage);
    }

    [Fact]
    public void Pvp_never_mixes_into_pve_rows()
    {
        var s = Script.Standard();
        s.Player(0, Script.Enemy, "Rival", 14);
        s.Hit(1, Script.Me, Script.Boss, 10_000);
        s.Hit(2, Script.Me, Script.Enemy, 7000);
        s.Hit(3, Script.Enemy, Script.Me, 900, Script.RanSkill);
        s.Hit(4, Script.Enemy, Script.Boss, 5000, Script.RanSkill); // enemies hitting mobs are not party damage
        var snap = s.Snap(4);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        Assert.Equal(10_000, Script.Row(snap, Script.Me).Damage);
        Assert.DoesNotContain(snap.Rows, r => r.EntityId == Script.Enemy);
        Assert.Equal(7000, Assert.Single(snap.PvpRows).DamageDealt);

        s.Engine.Mode = MeterMode.AllTargets;
        Assert.Equal(10_000, Script.Row(s.Snap(4), Script.Me).Damage);
        var rec = s.Record();
        Assert.Equal(10_000, rec.TotalDamage);
        Assert.Equal(10_000, Script.Combatant(rec, Script.Me).Damage);
    }

    [Fact]
    public void Party_members_are_never_flagged_as_enemies()
    {
        var s = Script.Standard();
        s.Roster(0, ("Me", 6), ("Ally", 26));
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(2, Script.Me, Script.Ally, 300, 11_050_000); // a non-heal record onto a party member
        var snap = s.Snap(2);
        Assert.Empty(snap.PvpRows);
        s.Hit(3, Script.Ally, Script.Boss, 100, Script.SorSkill);
        Assert.Contains(s.Snap(3).Rows, r => r.EntityId == Script.Ally);
    }

    [Fact]
    public void Enemy_death_shortly_after_local_damage_counts_as_kill()
    {
        var s = Script.Standard();
        s.Player(0, Script.Enemy, "Rival", 14);
        s.Hit(1, Script.Me, Script.Enemy, 1000);
        s.Death(3, Script.Enemy);
        Assert.Equal(1, s.Snap(3).PvpKills);
    }
}
