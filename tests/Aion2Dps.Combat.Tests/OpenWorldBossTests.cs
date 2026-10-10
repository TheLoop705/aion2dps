using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

/// <summary>
/// Open-world field boss joined mid-fight (real capture 2026-10-08: Special Operations Leader Linx). The boss's spawn and
/// the local player's <c>33 36</c> were never captured; the server sends only the local player's direct hits, other
/// players appear through DoT ticks and heals; the party is visible only through <c>1B 92</c> HP updates.
/// </summary>
public class OpenWorldBossTests
{
    private const long BossMax = 20_000_000;
    private const float SlotX = 100_000f, SlotY = -50_000f, SlotZ = 2_000f;

    private static FieldBossListEvent List(double t, params FieldBossSlot[] slots) =>
        new() { Time = Script.At(t), MapId = FakeGameData.OpenWorldMap, Slots = slots };

    private static FieldBossSlot Slot(int place, float x, float y, float z, bool alive = true) =>
        new() { Slot = FakeGameData.OpenWorldMap * 100 + (uint)place, Alive = alive, X = x, Y = y, Z = z };

    private static void Cast(Script s, double t, uint actor, float x, float y, float z) =>
        s.Send(new CastEvent { Time = Script.At(t), Actor = actor, SkillRaw = Script.NpcSkill, Target = Script.Me, X = x, Y = y, Z = z });

    /// <summary>Meter started mid-fight: no map load, no 33 36, no spawn. The local player is inferred from self stats.</summary>
    private static Script MidFight(EngineOptions? options = null, bool fieldBossList = true)
    {
        var s = new Script(options);
        for (int i = 0; i < 6; i++)
            s.Send(new EntityStatsEvent { Time = Script.At(i * 0.05), Entity = Script.Me, Format = 0x03, CurrentHp = 9000 });
        if (fieldBossList)
            s.Send(List(0.3, Slot(1, -80_000, -75_000, 28_000), Slot(2, SlotX, SlotY, SlotZ), Slot(3, 200_000, -110_000, 19_000)));
        s.Hp(0.5, Script.Boss, BossMax); // the boss's first reading: full HP (the only max-HP evidence)
        s.Hit(1, Script.Me, Script.Boss, 1_000);
        return s;
    }

    [Fact]
    public void Missed_spawn_boss_is_named_from_the_field_boss_list_by_position()
    {
        var s = MidFight();
        Cast(s, 2, Script.Boss, SlotX + 900, SlotY - 1_200, SlotZ + 250); // real: ~1,550 units from its slot
        var snap = s.Snap(2);
        Assert.Equal("Special Operations Leader Linx", snap.Target!.Name);
        Assert.Equal(FakeGameData.FieldBossCode, snap.Target.NpcCode);
        Assert.Equal(FakeGameData.OpenWorldMap, snap.MapId); // map hint from the field-boss list
        Assert.Equal("Altgard", snap.MapName);
        var rec = s.Record();
        Assert.Equal(FakeGameData.FieldBossCode, rec.BossNpcCode);
        Assert.Equal(FakeGameData.FieldBossCode, Assert.Single(rec.Bosses).NpcCode);
        Assert.Equal(FakeGameData.OpenWorldMap, rec.MapId);
    }

    [Fact]
    public void Boss_far_from_every_slot_or_between_two_keeps_a_max_hp_label()
    {
        var s = MidFight();
        Cast(s, 2, Script.Boss, 0, 0, 0); // 112,000 from the nearest slot
        Assert.Equal("World boss (20.0M HP)", s.Snap(2).Target!.Name);
        Assert.Null(s.Record().BossNpcCode);

        var t = MidFight();
        // Two living slots 4,000 apart and the boss midway: ambiguous, never guessed.
        t.Send(List(1.5, Slot(1, SlotX - 2_000, SlotY, SlotZ), Slot(2, SlotX + 2_000, SlotY, SlotZ)));
        Cast(t, 2, Script.Boss, SlotX, SlotY, SlotZ);
        Assert.Null(t.Record().BossNpcCode);
        Assert.Equal("World boss (20.0M HP)", t.Snap(2).Target!.Name);
    }

    [Fact]
    public void Without_any_list_the_unnamed_boss_reads_boss_with_its_max_hp()
    {
        var s = MidFight(fieldBossList: false);
        Assert.Equal("Boss (20.0M HP)", s.Snap(1).Target!.Name); // map unknown: not called a world boss
        Assert.Null(s.Snap(1).MapId);
    }

    [Fact]
    public void Hp_loss_far_above_decoded_damage_is_a_partial_view_with_hp_based_contribution()
    {
        var s = MidFight();
        for (int i = 0; i < 10; i++) s.Hit(1.1 + i * 0.1, Script.Me, Script.Boss, 1_000);
        s.Hp(3, Script.Boss, BossMax - 1_000_000); // others removed far more HP than we saw
        var snap = s.Snap(3);
        Assert.True(snap.PartialView);
        Assert.Contains("Partial view", snap.PartialViewText);
        var me = Script.Row(snap, Script.Me);
        Assert.Equal(11_000.0 / BossMax, me.Contribution, 9); // damage / boss max HP, not 100 % of the visible damage
        Assert.Equal("You", me.Name);
        Assert.True(me.IsLocal);

        var rec = s.Record();
        Assert.True(rec.PartialView);
        Assert.Equal(0.011, rec.VisibleDamageRatio!.Value, 3);
        Assert.Contains("1.1 %", rec.PartialViewReason);
        Assert.Equal(11_000.0 / BossMax, Script.Combatant(rec, Script.Me).Contribution, 9);
        Assert.NotNull(rec.HpCheck); // kept as data, shown as "partial view" instead of FAILED
    }

    [Fact]
    public void Open_world_boss_is_partial_until_the_hp_check_proves_the_whole_fight_is_visible()
    {
        var s = new Script();
        s.Map(0, FakeGameData.OpenWorldMap);
        s.Self(0);
        s.SpawnNpc(0, Script.Boss, FakeGameData.FieldBossCode, BossMax, BossMax);
        s.Hit(1, Script.Me, Script.Boss, 100_000);
        var early = s.Snap(1);
        Assert.True(early.PartialView);
        Assert.Contains("only part of the damage", early.PartialViewText);
        Assert.True(s.Record().PartialView);
        Assert.Contains("open world", s.Record().PartialViewReason);

        // Our party alone fights it and every point of HP is explained: a normal fight after all.
        for (int i = 0; i < 10; i++)
        {
            s.Hit(2 + i, Script.Me, Script.Boss, 100_000);
            s.Hp(2 + i + 0.5, Script.Boss, BossMax - 100_000 * (i + 2));
        }
        Assert.False(s.Snap(12).PartialView);
        Assert.False(s.Record().PartialView);
    }

    [Fact]
    public void Instance_boss_with_matching_hp_is_never_a_partial_view()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100_000);
        s.Hp(1.5, Script.Boss, 3_500_000);
        s.Hit(2, Script.Ally, Script.Boss, 100_000, Script.SorSkill);
        s.Hp(2.5, Script.Boss, 3_400_000);
        Assert.False(s.Snap(3).PartialView);
        Assert.False(s.Record().PartialView);
        Assert.All(s.Snap(3).Rows, r => Assert.Equal(0, r.AggregateCount));
    }

    [Fact]
    public void Partial_view_ranks_you_and_the_party_first_and_folds_dot_only_strangers()
    {
        var s = MidFight();
        s.Send(new HpUpdateEvent { Time = Script.At(1.2), Entity = Script.Ally, Hp = 9000, HpMax = 9000 }); // group HP = party
        s.Dot(1.5, Script.Ally, Script.Boss, 0x0A, 300, effect: Script.SorSkill * 100 + 11, skill: Script.SorSkill);
        s.Dot(1.6, Script.Stranger, Script.Boss, 0x0A, 50_000, effect: Script.SorSkill * 100 + 11, skill: Script.SorSkill);
        s.Dot(1.7, 601, Script.Boss, 0x0A, 40_000, effect: Script.RanSkill * 100 + 11, skill: Script.RanSkill);
        s.Hp(3, Script.Boss, BossMax - 2_000_000);
        var snap = s.Snap(3);
        Assert.True(snap.PartialView);
        Assert.Equal(new uint[] { Script.Me, Script.Ally, CombatEngine.OthersEntityId }, snap.Rows.Select(r => r.EntityId));
        var ally = Script.Row(snap, Script.Ally);
        Assert.True(ally.IsPartyMember);
        var others = Script.Row(snap, CombatEngine.OthersEntityId);
        Assert.Equal(2, others.AggregateCount);
        Assert.Equal(90_000, others.Damage);
        Assert.Equal(90_000.0 / BossMax, others.Contribution, 9);
        Assert.StartsWith("Others (2", others.Name);
        Assert.Equal(1.0, others.RelativeToTop); // the strangers' DoTs out-damage our one hit

        // Party only: strangers are hidden entirely.
        s.Engine.Options.PartyOnly = true;
        var party = s.Snap(3);
        Assert.Equal(new uint[] { Script.Me, Script.Ally }, party.Rows.Select(r => r.EntityId));

        var rec = s.Record();
        Assert.True(Script.Combatant(rec, Script.Ally).IsPartyMember);
        Assert.False(Script.Combatant(rec, Script.Stranger).IsPartyMember);
    }

    [Fact]
    public void Group_hp_updates_mark_party_members_without_a_roster()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Ally, Script.Boss, 500, Script.SorSkill);
        Assert.False(Script.Row(s.Snap(1), Script.Ally).IsPartyMember);
        s.Send(new HpUpdateEvent { Time = Script.At(1.5), Entity = Script.Ally, Hp = 9000, HpMax = 9000 });
        Assert.True(Script.Row(s.Snap(2), Script.Ally).IsPartyMember);
        s.Send(new HpUpdateEvent { Time = Script.At(2), Entity = Script.Me, Hp = 9000, HpMax = 9000 });
        Assert.False(Script.Row(s.Snap(2), Script.Me).IsPartyMember); // never yourself
        s.Map(3, Script.Map2); // ids are reissued on a zone change: the evidence goes with them
        s.Self(3);
        s.Player(3, Script.Ally, "Ally", 26);
        s.SpawnNpc(3, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(4, Script.Ally, Script.Boss, 500, Script.SorSkill);
        Assert.False(Script.Row(s.Snap(4), Script.Ally).IsPartyMember);
    }

    [Fact]
    public void Remembered_character_names_the_inferred_local_player_when_the_class_agrees()
    {
        var s = MidFight(new EngineOptions { KnownLocalCharacter = new KnownCharacter("Hisoka", CharacterClass.Gladiator, 1304) });
        var me = Script.Row(s.Snap(1), Script.Me);
        Assert.Equal("Hisoka", me.Name);
        Assert.True(me.IsLocal);
        var lp = s.Engine.LocalPlayer!;
        Assert.True(lp.Inferred);
        Assert.Equal("Hisoka", lp.Name);
        Assert.Equal("Hisoka", s.Record().LocalPlayerName);

        var other = MidFight(new EngineOptions { KnownLocalCharacter = new KnownCharacter("Hisoka", CharacterClass.Cleric, 1304) });
        Assert.Equal("You", Script.Row(other.Snap(1), Script.Me).Name); // an alt of another class: not named
        Assert.Null(other.Record().LocalPlayerName);
    }

    [Fact]
    public void Party_roster_names_the_inferred_local_player()
    {
        var s = MidFight();
        s.Player(1.2, Script.Ally, "Ally", 26);
        s.Roster(1.5, ("Ally", 26), ("Hero", 6)); // the only unbound member, and a Gladiator like us
        Assert.Equal("Hero", Script.Row(s.Snap(2), Script.Me).Name);
        Assert.Equal("Hero", s.Engine.LocalPlayer!.Name);
    }

    [Fact]
    public void Own_identity_record_is_offered_to_the_host_for_the_next_start()
    {
        var s = new Script();
        var seen = new List<KnownCharacter>();
        s.Engine.LocalCharacterIdentified += seen.Add;
        s.Map(0, Script.Map1);
        s.Self(0, name: "Hisoka", classCode: 18);
        var c = Assert.Single(seen);
        Assert.Equal("Hisoka", c.Name);
        Assert.Equal(CharacterClass.Assassin, c.Class);
        Assert.Equal((ushort)1304, c.ServerId);
    }
}
