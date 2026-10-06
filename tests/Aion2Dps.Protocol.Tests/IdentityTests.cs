using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

/// <summary><c>33 36</c>, <c>45 36</c>, <c>04 8D</c>, <c>02 97</c>, <c>21 36</c> and the smaller records (§8.6-§8.16).</summary>
public class IdentityTests
{
    [Fact]
    public void Self_record_naicha()
    {
        var s = Run.Single<SelfInfoEvent>("3336ed745e91c12837064e616963686118051e000000011c0000007f0100007f0100001c000000d002040000000000");
        Assert.Equal(14957u, s.Entity);
        Assert.Equal("Naicha", s.Name);
        Assert.Equal(1304, s.ServerId);
        Assert.Equal(30u, s.ClassCode);
        Assert.Equal(CharacterClass.Cleric, s.Class);
        Assert.Equal(28u, s.Level);
    }

    [Fact]
    public void Self_record_aahz_with_equipment_block()
    {
        var s = Run.Single<SelfInfoEvent>("3336cb025fa1c12837044161687a18050600000001210000000000000000000000b6ec460f8ac1900600010000000000000000000000000003000000000000000000000000990eb788890c000200000000000000000000000000030000000000000000000000000e570f8b0c00030000000000000000000000000003000000000000000000000000dd0e777b860c000400000000000000000000000000030000000000000000000000000e1702880c00050000000000000000000000000003000000000000000000000000dd0efe958c0c000600000000000000000000000000030000000000000000000000000e961c8e0c00070000000000000000000000000003000000000000000000000000dd0e36a38f0c000800000000000000000000000000030000000000000000000000000e7e5c7c1200090000000000000000000000000003000000000000000000000000dd0e2e0a7e12000a00000000000000000000000000030000000000000000000000000e2c0a7e12000b0000000000000000000000000003000000000000000000000000dd0e0000000000");
        Assert.Equal(331u, s.Entity);
        Assert.Equal("Aahz", s.Name);
        Assert.Equal(1304, s.ServerId);
        Assert.Equal(CharacterClass.Gladiator, s.Class);
        Assert.Equal(33u, s.Level);
    }

    [Fact]
    public void Self_record_nimara_cyber_variant()
    {
        var s = Run.Single<SelfInfoEvent>("3336ed745e91c12837064e696d61726118051e000000011c0000007f0100007f0100001c000000d002040000000000");
        Assert.Equal("Nimara", s.Name);
        Assert.Equal(14957u, s.Entity);
    }

    [Fact]
    public void Self_record_with_bad_server_fails()
    {
        var (events, diag) = Run.Payload("3336ed745e91c12837064e61696368610100" + "1e000000011c000000");
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
    }

    [Theory]
    [InlineData("4536ea1b17b0a001070950656e63696c676f6e12000000", 3562u, "Pencilgon", CharacterClass.Assassin)]
    [InlineData("4536b81c1730a001070a426f6168616e636f6f6b1e000000", 3640u, "Boahancook", CharacterClass.Cleric)]
    [InlineData("4536e11b17b0a0010709436172616d656c6c7922000000", 3553u, "Caramelly", CharacterClass.Chanter)]
    public void Player_records(string payload, uint entity, string name, CharacterClass cls)
    {
        var p = Run.Single<PlayerInfoEvent>(payload);
        Assert.Equal(entity, p.Entity);
        Assert.Equal(name, p.Name);
        Assert.Equal(cls, p.Class);
        Assert.Null(p.GuildName);
        Assert.Null(p.ServerId);
    }

    [Fact]
    public void Player_record_with_guild_run_gives_server_and_guild()
    {
        var p = Run.Single<PlayerInfoEvent>("4536F47F0320A00107074D696C697269611E00000001028012869FD3C63950104700CB0947753F804366B601F142F1420A0C00000A0C00000000000000000000B0940100B094010000000000F049020001000000A0860100A086010084DE010000E2040001000000017FD3CC011705EA000000000017050C436F6E76C3A87267656E636501000200");
        Assert.Equal(16372u, p.Entity);
        Assert.Equal("Miliria", p.Name);
        Assert.Equal(CharacterClass.Cleric, p.Class);
        Assert.Equal((ushort)1303, p.ServerId);
        Assert.Equal("Convèrgence", p.GuildName);
    }

    [Fact]
    public void Player_record_with_gear_block()
    {
        var p = Run.Single<PlayerInfoEvent>("4536fc3e0320a0010707496363617275732100000000000000000000e0f9460fde489b06000100000000000000000000000000030000000000000000000000000eb888890c000200000000000000005758ca010003000000000000000000000000dd0e5e0f8b0c000300000000000000005858ca0100030000000000000000000000000e7e7b860c000400000000000000005558ca010003000000000000000000000000dd0e1702880c000500000000000000000000000000030000000000000000000000000ef7958c0c00060000000000000000000000000003000000000000000000000000dd0e981c8e0c000700000000000000000000000000030000000000000000000000000e3ea38f0c00080000000000000000ab21ca010003000000000000000000000000dd0e7e5c7c12000900000000000000000000000000030000000000000000000000000e18e37d12000a0000000000000000000000000003000000000000000000000000dd0e18e37d12000b00000000000000000000000000030000");
        Assert.Equal(8060u, p.Entity);
        Assert.Equal("Iccarus", p.Name);
        Assert.Equal(33u, p.ClassCode);
        Assert.Equal(CharacterClass.Chanter, p.Class);
    }

    [Fact]
    public void Kill_record_stacys_aahz_akatsuki()
    {
        var k = Run.Single<KillEvent>("048d8484030e4ea800be081805044161687a08416b617473756b69010000000000000100");
        Assert.Equal(49668u, k.Target);
        Assert.Equal(11030030u, k.SkillRaw);
        Assert.Equal(1086u, k.Killer);
        Assert.Equal(1304, k.KillerServerId);
        Assert.Equal("Aahz", k.KillerName);
        Assert.Equal("Akatsuki", k.KillerGuildName);
    }

    [Fact]
    public void Kill_record_taengu_apexz_ventus()
    {
        var k = Run.Single<KillEvent>("048d" + "ecde02" + "7228e900" + "ae0b" + "1905" + "05" + Convert.ToHexString("ApexZ"u8) + "06" + Convert.ToHexString("Ventus"u8) + "01000000");
        Assert.Equal(44908u, k.Target);
        Assert.Equal(15280242u, k.SkillRaw);
        Assert.Equal(1454u, k.Killer);
        Assert.Equal(1305, k.KillerServerId);
        Assert.Equal("ApexZ", k.KillerName);
        Assert.Equal("Ventus", k.KillerGuildName);
    }

    [Fact]
    public void Kill_record_despawn_form()
    {
        var k = Run.Single<KillEvent>("048d" + "ecde02" + "00000000" + "00");
        Assert.Equal(44908u, k.Target);
        Assert.Equal(0u, k.SkillRaw);
        Assert.Equal(0u, k.Killer);
        Assert.Null(k.KillerName);
    }

    [Theory]
    [InlineData("213601000000d52709003b1a350000000000f7e646460d7fb0c6", 1u, 600021u)]
    [InlineData("213602000000d5270900d74c390000000000a8f805c610861245", 2u, 600021u)]
    [InlineData("213601000000f2030000dd7f3c00000000006868d047d0c62c47", 1u, 1010u)]
    public void Map_loads(string payload, uint count, uint map)
    {
        var m = Run.Single<MapLoadEvent>(payload);
        Assert.Equal(count, m.LoadCount);
        Assert.Equal(map, m.MapId);
    }

    /// <summary>taengu stream_processor.rs:2808-2819: complete wire frames (varint included) of the Fire Temple loads.</summary>
    [Fact]
    public void Map_load_wire_frames_with_their_length_prefix()
    {
        var wire = W.Concat(
            W.H("34213601000000d52709003b1a350000000000f7e646460d7fb0c60080b045409da54200000000000000000000004f0000"),
            W.H("34213602000000d5270900d74c390000000000a8f805c610861245008036453ccd24c204000000000000000000004f0000"),
            W.H("34213601000000f2030000dd7f3c00000000006868d047d0c62c470098da46fa63284300000000000000000000004f0000"));
        var (events, pipe) = Run.Wire(wire);
        Assert.Equal(new uint[] { 600021, 600021, 1010 }, events.Of<MapLoadEvent>().Select(m => m.MapId));
        Assert.Equal(wire.Length, pipe.Diagnostics.BytesIn);
        Assert.Equal(0, pipe.ProtocolDiagnostics.ResyncSkippedBytes);
        Assert.Equal(0, pipe.FrameDecoder.BufferedBytes);
    }

    private const string Roster = "02971c590000134573206765687420736f666f7274206c6f732e05cb2709000003ae2e030000001805ff0103051e01ae2e0300000018050950656e63696c676f6e120000001e000000c40100001805d810048b5800000000000000210000000000000001011e02353b030000001805044161687a060000001e000000b10100001805d81004e25600000000000000570000000000000001011e03434a0300000018050a426f6168616e636f6f6b1e0000001e000000a10100001805d81004a44e00000000000000200000000000000001011e042b3003000000180509436172616d656c6c79220000001e00000070010000011805d810040749000000000000001c00000000000000010100050000000000000000000000000000000000000000000400000000000000000000000004";

    [Fact]
    public void Party_roster_five_slots_four_members()
    {
        var r = Run.Single<PartyRosterEvent>(Roster);
        Assert.Equal(0x591Cu, r.PartyKey);
        Assert.Equal(600011u, r.DungeonId);
        Assert.Equal(new[] { "Pencilgon", "Aahz", "Boahancook", "Caramelly" }, r.Members.Select(m => m.Name));
        Assert.Equal(new[] { 1, 2, 3, 4 }, r.Members.Select(m => m.Slot));
        Assert.All(r.Members, m => Assert.Equal(1304, m.ServerId));
        Assert.All(r.Members, m => Assert.Equal(30u, m.Level));
        Assert.Equal(new[] { CharacterClass.Assassin, CharacterClass.Gladiator, CharacterClass.Cleric, CharacterClass.Chanter },
            r.Members.Select(m => m.Class));
        Assert.Equal(0x32EAEu, r.Members[0].CharacterId);
        Assert.Equal(0x0518_0000_0003_2EAEUL, r.Members[0].DbId);
        Assert.Equal(new uint?[] { 452, 433, 417, 368 }, r.Members.Select(m => m.GearScore));
        Assert.Equal(22667UL, r.Members[0].CombatPower);
        Assert.Equal(22242UL, r.Members[1].CombatPower);
        Assert.Equal(20132UL, r.Members[2].CombatPower);
        Assert.Equal(18695UL, r.Members[3].CombatPower); // re-acquired despite the extra byte after its gear score
    }

    [Fact]
    public void Party_roster_with_vacant_slot_and_utf8_title()
    {
        string twoLeft =
            "0297994A01001244C3A97061727420696D6DC3A9646961742E05CC2709000003BCA50300000017051B0203051C01BCA5030000001705094D6168C3A9" +
            "72616E6E1A0000002D000000790500001705E81004E4F600000000000000020000000000000001010002000000000000000000000000000000000000" +
            "0000000400000000000000000000001E03068E0300000017050C426F756C656E626F75636865150000002D000000B90700001705E81004B731010000" +
            "000000003200000000000000010100040000000000000000000000000000000000000000000400000000000000000000000005000000000000000000" +
            "0000000000000000000000000400000000000000000000000004";
        var r = Run.Single<PartyRosterEvent>(twoLeft);
        Assert.Equal(600012u, r.DungeonId);
        Assert.Equal(2, r.Members.Count);
        Assert.Equal("Mahérann", r.Members[0].Name);
        Assert.Equal(1, r.Members[0].Slot);
        Assert.Equal(CharacterClass.Sorcerer, r.Members[0].Class);
        Assert.Equal(45u, r.Members[0].Level);
        Assert.Equal(1303, r.Members[0].ServerId);
        Assert.Equal(1401u, r.Members[0].GearScore);
        Assert.Equal(63204UL, r.Members[0].CombatPower);
        Assert.Equal("Boulenbouche", r.Members[1].Name);
        Assert.Equal(3, r.Members[1].Slot);
        Assert.Equal(CharacterClass.Elementalist, r.Members[1].Class);
        Assert.Equal(78263UL, r.Members[1].CombatPower);

        string aloneNow =
            "0297994A01001244C3A97061727420696D6DC3A9646961742E05CC2709000003068E030000001705030203051C01068E0300000017050C426F756C65" +
            "6E626F75636865150000002D000000B90700001705E81004B73101000000000000320000000000000001010002000000000000000000000000000000" +
            "000000000000040000000000000000000000000300000000000000000000000000000000000000000004000000000000000000000000040000000000" +
            "000000000000000000000000000000000400000000000000000000000005000000000000000000000000000000000000000000040000000000000000" +
            "0000000004";
        var alone = Run.Single<PartyRosterEvent>(aloneNow);
        Assert.Equal("Boulenbouche", Assert.Single(alone.Members).Name);
    }

    [Fact]
    public void Other_party_roster_and_field_boss_list_are_recognised_but_not_decoded()
    {
        var (events, diag) = Run.Body(Opcodes.OtherPartyRoster, "0102");
        Assert.Empty(events);
        Assert.Equal(1, diag.GetStat(Opcodes.OtherPartyRoster).Decoded);
        (events, diag) = Run.Body(Opcodes.FieldBossList, "000056040000");
        Assert.Empty(events);
        Assert.Equal(0, diag.DecodeErrors);
    }
}

public class SmallRecordTests
{
    [Fact]
    public void Heartbeat_is_the_server_clock()
    {
        var h = Run.Single<HeartbeatEvent>("0036857120f4a0010000");
        Assert.Equal(new DateTime(2026, 9, 30, 21, 2, 42, 53, DateTimeKind.Utc),
            DateTimeOffset.FromUnixTimeMilliseconds((long)h.ServerUnixMs).UtcDateTime);
    }

    [Fact]
    public void Larger_heartbeat_frames_are_containers_without_an_event()
    {
        var (events, diag) = Run.Body(Opcodes.Heartbeat, "857120f4a00100000000");
        Assert.Empty(events);
        Assert.Equal(0, diag.DecodeErrors);
    }

    [Theory]
    [InlineData("c743000c28e9009202d1e802b0f930c38dbae8470b71d3c700faa546ea5d01dc6f", 8647u, 46161u, 15280140u)]
    [InlineData("d1ca02000d28e9000102d1ca02a263c741d3c7ea47bd1ed1c70048a546ee64029a3a", 42321u, 42321u, 15280141u)]
    public void Cast_announcements(string body, uint actor, uint target, uint skill)
    {
        var (events, _) = Run.Body(Opcodes.Cast, body);
        var c = Assert.IsType<CastEvent>(Assert.Single(events));
        Assert.Equal(actor, c.Actor);
        Assert.Equal(target, c.Target);
        Assert.Equal(skill, c.SkillRaw);
    }

    [Fact]
    public void Death_teleport_hp_update_ping_global_id_toggle()
    {
        var death = Run.Single<DeathEvent>("4236 848403 00 03");
        Assert.Equal(49668u, death.Entity);
        Assert.Equal(3u, death.Flag);

        Assert.Equal(0u, Run.Single<TeleportEvent>("2336 00 0000803F").Entity);

        var hp = Run.Single<HpUpdateEvent>("1B92 dbbc01 80dddb01 80859f03");
        Assert.Equal(24155u, hp.Entity);
        Assert.Equal(3_600_000, hp.Hp);
        Assert.Equal(6_800_000, hp.HpMax);

        long qpc = 16_777_216_000 + 123_456;
        var ping = Run.Single<PingEvent>("0336 0000" + Convert.ToHexString(BitConverter.GetBytes(qpc)));
        Assert.Equal(qpc, ping.ClientSentMs);

        var link = Run.Single<GlobalIdLinkEvent>("2036 0000 ED74 00000000 AE2E0300");
        Assert.Equal(14957u, link.Entity);
        Assert.Equal(0x32EAEu, link.CharacterId);

        Assert.True(Run.Single<BattleToggleEvent>("218D 848403 00 01").Engaged);
        Assert.False(Run.Single<BattleToggleEvent>("218D 848403 00 00").Engaged);
        var (events, diag) = Run.Payload("218D 848403 00 01 FF");
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
    }

    [Fact]
    public void Buff_applied_and_removed_global_layout()
    {
        var b = Run.Single<BuffAppliedEvent>("2a38f81b011335ade5cc0ae02e00000000000065a020f4a0010000ec1b0c5e7d1401004f576fc60aa7724600c02144");
        Assert.Equal(3576u, b.Target);
        Assert.Equal(53u, b.Stack);
        Assert.Equal(181200301u, b.BuffId);
        Assert.Equal(12000u, b.DurationMs);
        Assert.Equal(0x01A0F4207185UL + 12000, b.ExpiryUnixMs);
        Assert.Equal(3564u, b.Caster);
        Assert.Equal(18120030u, b.SourceSkill);

        // Real 2B 38 (Global, 2026-10-06): no count byte, the entry starts right after the target.
        var b2 = Run.Single<BuffAppliedEvent>("2b38BF2E13B9114F93D70A60090000000000007D38BC12A1010000BF2E06BB8E150102BFDC1F48A1D578474F0EE645");
        Assert.Equal(5951u, b2.Target);
        Assert.Equal(2233u, b2.Stack);
        Assert.Equal(181900111u, b2.BuffId);
        Assert.Equal(2400u, b2.DurationMs);
        Assert.Equal(18190011u, b2.SourceSkill);

        var r = Run.Single<BuffRemovedEvent>("0e92f81b01ade5cc0a");
        Assert.Equal(3576u, r.Target);
        Assert.Equal(181200301u, r.BuffId);

        // The old "skip 2 bytes" reading of 2B 38 would misalign; a 2A 38-shaped body under 2B 38 must not decode.
        var (wrong, diag) = Run.Payload("2b38f81b011335ade5cc0ae02e00000000000065a020f4a0010000ec1b0c5e7d1401004f576fc60aa7724600c02144");
        Assert.Empty(wrong);
        Assert.Equal(1, diag.DecodeErrors);
    }

    [Fact]
    public void Unknown_opcodes_are_counted_not_failed()
    {
        var (events, diag) = Run.Body(0x1234, "00");
        Assert.Empty(events);
        Assert.Equal(1, diag.UnhandledFrames);
        Assert.Equal(0, diag.DecodeErrors);
    }

    [Fact]
    public void Events_carry_time_and_bundle_depth()
    {
        var (events, _) = Run.Body(Opcodes.Teleport, "05", depth: 2);
        var t = Assert.IsType<TeleportEvent>(Assert.Single(events));
        Assert.Equal(2, t.BundleDepth);
        Assert.Equal(W.T0, t.Time);
    }
}
