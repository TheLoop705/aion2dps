using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public class EntityTests
{
    [Fact]
    public void Kill_record_names_the_killer()
    {
        var s = Script.Standard();
        s.Hit(1, 1086, Script.Trash, 381, Script.GlaSkill2);
        s.Kill(2, Script.Trash, 1086, "Aahz");
        var row = Script.Row(s.Snap(2), 1086);
        Assert.Equal("Aahz", row.Name);
        Assert.True(s.Snap(2).Targets.Single(t => t.EntityId == Script.Trash).IsDead);
    }

    [Fact]
    public void Global_id_link_joins_roster_member_to_entity()
    {
        var s = Script.Standard();
        s.Send(new PartyRosterEvent
        {
            Time = Script.At(0),
            Members = new[]
            {
                new PartyMember { Slot = 1, Name = "Me", ClassCode = 6, Class = CharacterClass.Gladiator, CharacterId = 11 },
                new PartyMember { Slot = 2, Name = "Linked", ClassCode = 14, Class = CharacterClass.Ranger, CharacterId = 4242 },
                new PartyMember { Slot = 3, Name = "Other", ClassCode = 13, Class = CharacterClass.Ranger, CharacterId = 4343 },
            },
        });
        s.Hit(1, 960, Script.Boss, 100, Script.RanSkill); // two unnamed Rangers in the roster: class match is ambiguous
        Assert.Equal("Player 960", Script.Row(s.Snap(1), 960).Name);
        s.Send(new GlobalIdLinkEvent { Time = Script.At(2), Entity = 960, CharacterId = 4242 });
        var row = Script.Row(s.Snap(2), 960);
        Assert.Equal("Linked", row.Name);
        Assert.True(row.IsPartyMember);
    }

    [Fact]
    public void Self_stats_frames_identify_local_player_without_self_info()
    {
        var s = new Script();
        s.Map(0, Script.Map1);
        for (int i = 0; i < 6; i++)
            s.Send(new EntityStatsEvent { Time = Script.At(i * 0.1), Entity = 123, Format = 0x03, CurrentHp = 5000 });
        s.SpawnNpc(1, Script.Trash, FakeGameData.TrashCode, 50_000, 50_000);
        s.Hit(2, 123, Script.Trash, 100, Script.GlaSkill);
        Assert.True(Script.Row(s.Snap(2), 123).IsLocal);
        Assert.Null(s.Engine.LocalPlayer); // no authoritative name yet
    }

    [Fact]
    public void Player_info_replaces_previous_npc_binding()
    {
        var s = Script.Standard();
        s.Player(1, Script.Trash, "Reborn", 26); // id 6001 reissued to a player
        s.Hit(2, Script.Trash, Script.Boss, 500, Script.SorSkill);
        Assert.Equal("Reborn", Script.Row(s.Snap(2), Script.Trash).Name);
    }

    [Fact]
    public void Spawn_with_existing_id_is_a_new_npc()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Trash, 100);
        s.Hp(1.5, Script.Trash, 0);
        s.SpawnNpc(3, Script.Trash, FakeGameData.TrashCode, 50_000, 50_000); // respawn reuses the id
        s.Hit(4, Script.Me, Script.Trash, 100);
        Assert.Equal(200, Script.Row(s.Snap(4), Script.Me).Damage);
    }

    [Fact]
    public void Malformed_events_never_throw()
    {
        var s = Script.Standard();
        s.Send(new DamageEvent { Time = Script.At(1), Actor = 0, Target = 0, SkillId = 0, Layout = 4, Amount = 5 });
        s.Send(new DotEvent { Time = Script.At(1), Flags = 0xFF });
        s.Send(new SpawnEvent { Time = Script.At(1), Entity = 0 });
        s.Send(new PartyRosterEvent { Time = Script.At(1) });
        s.Send(new KillEvent { Time = Script.At(1), Target = 99999, Killer = 5, KillerName = "" });
        s.Engine.OnEvent(null!);
        Assert.Equal(0, s.Engine.EventErrors);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(1).State);
    }
}
