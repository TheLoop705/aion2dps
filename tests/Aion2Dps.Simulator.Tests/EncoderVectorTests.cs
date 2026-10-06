using Aion2Dps.Contracts;
using static Aion2Dps.Simulator.Tests.Hex;

namespace Aion2Dps.Simulator.Tests;

/// <summary>
/// Every encoder reproduces REAL captured bodies byte-for-byte from their decoded values
/// (PROTOCOL.md §8.2.4, §8.3, §8.4, §8.5, §8.6, §8.7, §8.8, §8.10, §8.13, §8.14 and the clone fixtures listed in §16).
/// </summary>
public class EncoderVectorTests
{
    private static DamageEvent D(uint target, uint sw, uint actor, uint skill, byte tag, byte dmg, byte mods, byte dir,
        uint effect, uint hit, uint scalar, long? amount, params uint[] extras) => new()
        {
            Target = target, Switch = sw, Layout = (byte)(sw & 0x0F), Actor = actor, SkillRaw = skill, SkillId = SkillIds.Normalize(skill),
            HitTag = tag, DamageType = dmg, Mods = (HitMods)mods, Direction = (HitDirection)dir, EffectId = effect, HitIndex = hit,
            PowerScalar = scalar, Amount = amount, ExtraHits = extras,
        };

    private static readonly DamageEncodeOptions NoTrailer = new() { Trailer = false };

    public static TheoryData<string, DamageEvent, bool> DamageVectors => new()
    {
        // §8.2.4 / stacys SelfCheckAion2.cs:104-107: Keen Strike 381, crit 588 (1 extra hit of 2), Rupture Strike 432, Blood Absorption self-heal 65.
        { "8484030600be08fe26a8004f02000002433baf4101000000ba4ffd020100", D(49668, 0x06, 1086, 11020030, 0x4F, 2, 0, 2, 0x41AF3B43, 1, 10170, 381), true },
        { "8484032600be08fe26a8005703000002433baf4101000000ba4fcc0401020100", D(49668, 0x26, 1086, 11020030, 0x57, 3, 0, 2, 0x41AF3B43, 1, 10170, 588, 2), true },
        { "8484032600be080e4ea8004b02000002837dbe4101000000ba4fb00301010100", D(49668, 0x26, 1086, 11030030, 0x4B, 2, 0, 2, 0x41BE7D83, 1, 10170, 432, 1), true },
        { "be080400be0857fcb2004a020792ea4501000000ba4f410100", D(1086, 0x04, 1086, 11730007, 0x4A, 2, 0, 0, 0x45EA9207, 1, 10170, 65), true },
        // taengu/cyber: Combustion layout 6 crit, mods 0x80, Back, 1700 incl. 2×24 (no trailer); layout 4 + sw&0x10 field, 6227 incl. 1×89.
        { "b1ea013600f30a40c0f4007a038000010b199b5f01000000ac52a40d021818", D(30001, 0x36, 1395, 16040000, 0x7A, 3, 0x80, 1, 0x5F9B190B, 1, 10540, 1700, 24, 24), false },
        { "b1ea013400f30ae0b7f800cd028bd3276101000000ac52d330010159", D(30001, 0x34, 1395, 16300000, 0xCD, 2, 0, 0, 0x6127D38B, 1, 10540, 6227, 89), false },
        // cyber: spirit layout 4 (no field), hit 2: 481 incl. 1×6.
        { "fe9e0224009e9b01c3f8f5000302792b156002000000ac52e10301060200", D(36734, 0x24, 19870, 16120003, 0x03, 2, 0, 0, 0x60152B79, 2, 10540, 481, 6), true },
        // Bittercold Wind (summon) layout 4 + field, 657.
        { "A9B1021400EBC5018227E9000302D36E135B01000000F25291050101" + "00", D(39081, 0x14, 25323, 15280002, 0x03, 2, 0, 0, 0x5B136ED3, 1, 10610, 657), true },
        // Deadshot 30,697 incl. 4×3069 (verified by the boss HP equation).
        { "A2BC023600B43683C7D500EE0280000118A1815301000000FA60E9EF0104FD17FD17FD17FD170100", D(40482, 0x36, 6964, 14010243, 0xEE, 2, 0x80, 1, 0x5381A118, 1, 12410, 30697, 3069, 3069, 3069, 3069), true },
        // noia doc (TW/KR): mods 0x0C Perfect+Double, Back, 338,097 incl. 4×6935.
        { "91C1022600A11448F1CA0020020C00012C40464F010000008C9101B1D114049736973697369736" + "0100", D(41105, 0x26, 2593, 13300040, 0x20, 2, 0x0C, 1, 0x4F46402C, 1, 18572, 338097, 6935, 6935, 6935, 6935), true },
        // Layout-0 cast notice of 15280240 by 15882 (no amount), hit 2.
        { "BAEB0200008A7C7028E9004B02D5CB135B020000008A640200", D(46522, 0x00, 15882, 15280240, 0x4B, 2, 0, 0, 0x5B13CBD5, 2, 12810, null), true },
        // Inner frames of the real stacys bundle: a Recuperation heal of 1031 and a dmg_type-0 notice.
        { "b81c0400ec1b5e7d14011a02c3f8006c01000000c24e87080100", D(3640, 0x04, 3564, 18120030, 0x1A, 2, 0, 0, 0x6C00F8C3, 1, 10050, 1031), true },
        { "f81b0000ec1b5e7d14011a00c5f8006c01000000c24e0100", D(3576, 0x00, 3564, 18120030, 0x1A, 0, 0, 0, 0x6C00F8C5, 1, 10050, null), true },
    };

    [Theory]
    [MemberData(nameof(DamageVectors))]
    public void Damage_records_match_real_bytes(string hex, DamageEvent e, bool trailer)
    {
        var body = PacketEncoders.EncodeDamage(e, trailer ? null : NoTrailer);
        Assert.Equal(hex.ToUpperInvariant(), Of(body));
    }

    [Fact]
    public void Damage_full_payload_and_frame()
    {
        var e = D(49668, 0x06, 1086, 11020030, 0x4F, 2, 0, 2, 0x41AF3B43, 1, 10170, 381);
        Assert.Equal("04388484030600BE08FE26A8004F02000002433BAF4101000000BA4FFD020100", Of(PacketEncoders.EncodePayload(e)));
        var frame = PacketEncoders.EncodeFrame(e);
        Assert.Equal(32 + 4, frame[0]); // L = payload (32) + 4
        Assert.Equal(33, frame.Length);
    }

    [Fact]
    public void Damage_chain_concatenates_records()
    {
        var a = D(49668, 0x06, 1086, 11020030, 0x4F, 2, 0, 2, 0x41AF3B43, 1, 10170, 381);
        var b = D(1086, 0x04, 1086, 11730007, 0x4A, 2, 0, 0, 0x45EA9207, 1, 10170, 65);
        Assert.Equal(Of(PacketEncoders.EncodeDamage(a)) + Of(PacketEncoders.EncodeDamage(b)), Of(PacketEncoders.EncodeDamageChain([a, b])));
    }

    [Fact]
    public void Damage_rejects_invalid_extra_hits()
    {
        var bad = D(1, 0x24, 2, 11020030, 0, 2, 0, 0, 1102003011, 1, 10000, 10, 6, 5);
        Assert.Throws<ArgumentException>(() => PacketEncoders.EncodeDamage(bad));
    }

    public static TheoryData<string, DotEvent> DotVectors => new()
    {
        // stacys real Krao Cave ticks: Jointstrike: Curse damage tick 552.
        { "D490020AC522108BAF3360A804E046F600", new DotEvent { Target = 34900, Flags = 0x0A, Actor = 4421, Stack = 0x10, EffectId = 0x6033AF8B, Amount = 552, SkillId = 16140000 } },
        // Monster DoT on a player (9-digit effect), 106.
        { "C5220AD49002F402E71327076AE0771B00", new DotEvent { Target = 4421, Flags = 0x0A, Actor = 34900, Stack = 372, EffectId = 0x072713E7, Amount = 106, SkillId = 0x001B77E0 } },
        // Recuperation HoT: announcement 334 (0x09), tick heal 83 with 251 remaining (0x0B).
        { "862D09833A240BED006CCE02407D1401", new DotEvent { Target = 5766, Flags = 0x09, Actor = 7427, Stack = 0x24, EffectId = 0x6C00ED0B, Heal = 334, SkillId = 18120000 } },
        { "862D0B833A240BED006CFB0153407D1401", new DotEvent { Target = 5766, Flags = 0x0B, Actor = 7427, Stack = 0x24, EffectId = 0x6C00ED0B, Amount = 251, Heal = 83, SkillId = 18120000 } },
        // Status tick without amount (0x08).
        { "C52208C522EC0195D32761E2B7F800", new DotEvent { Target = 4421, Flags = 0x08, Actor = 4421, Stack = 236, EffectId = 0x6127D395, SkillId = 0x00F8B7E2 } },
        // The boss self-heal trap: monster effect 160466011 with the Ranger's Deadshot, 122,788.
        { "A2BC020AB436175B849009A4BF0783C7D500", new DotEvent { Target = 40482, Flags = 0x0A, Actor = 6964, Stack = 0x17, EffectId = 160466011, Amount = 122788, SkillId = 14010243 } },
        // Inner frame of the real stacys bundle.
        { "f81b09ec1b35c3f8006cc00b5e7d1401", new DotEvent { Target = 3576, Flags = 0x09, Actor = 3564, Stack = 0x35, EffectId = 0x6C00F8C3, Heal = 1472, SkillId = 18120030 } },
    };

    [Theory]
    [MemberData(nameof(DotVectors))]
    public void Dot_ticks_match_real_bytes(string hex, DotEvent e) => Assert.Equal(hex.ToUpperInvariant(), Of(PacketEncoders.EncodeDot(e)));

    [Fact]
    public void Entity_stats_match_real_bytes()
    {
        // §8.4 vector: entity 18126, HP 122,120.
        Assert.Equal("CE8D0102010008DD010000000000", Of(PacketEncoders.EncodeEntityStats(new EntityStatsEvent { Entity = 18126, CurrentHp = 122120 })));
        // The boss (40482) of the self-heal sequence at 665,980.
        Assert.Equal("A2BC020201007C290A0000000000", Of(PacketEncoders.EncodeEntityStats(new EntityStatsEvent { Entity = 40482, Format = 0x02, CurrentHp = 665980 })));
        // A local player's frame mixing 4-byte stats with the 8-byte HP 9,405.
        Assert.Equal("CF19030201900A000003D89E01000100BD24000000000000", Of(PacketEncoders.EncodeEntityStats(new EntityStatsEvent
        {
            Entity = 3279, CurrentHp = 9405, Stats32 = new Dictionary<byte, uint> { [3] = 0x19ED8, [1] = 0xA90 },
        })));
        // Stats only, with an 8-byte kind 7 that is not HP.
        Assert.Equal("CF19030508CD0700000A60B201000BF04902000CA08601000D00E2040001075B1B000000000000", Of(PacketEncoders.EncodeEntityStats(new EntityStatsEvent
        {
            Entity = 3279,
            Stats32 = new Dictionary<byte, uint> { [8] = 0x7CD, [10] = 0x1B260, [11] = 0x249F0, [12] = 0x186A0, [13] = 0x4E200 },
            Stats64 = new Dictionary<byte, long> { [7] = 0x1B5B },
        })));
    }

    public static TheoryData<string, SpawnEvent, SpawnEncodeOptions> SpawnHeads => new()
    {
        // stacys real Canyon Urugugu frames: Water Spirit (owner 10894), Fire Spirit (owner 3279).
        {
            "A5AE011F1000C18E2C0000020028A04500788245008031440E86B1437AFC01DF28DF28",
            new SpawnEvent { Entity = 22309, KindByte = 0x1F, KindFlags = 0x10, NpcCode = 2920129, X = F("0028A045"), Y = F("00788245"), Z = F("00803144"), HpCurrent = 5215, HpMax = 5215, IsSummonLike = true },
            new SpawnEncodeOptions { Heading = F("0E86B143"), Unknown16 = 0xFC7A, CodeSuffix = 0x00, IncludeTail = false }
        },
        {
            "B7DD011F1000B28E2C00400200E8E44500F06B4500806C443AAF7D4366B40198639863",
            new SpawnEvent { Entity = 28343, KindByte = 0x1F, KindFlags = 0x10, NpcCode = 2920114, X = F("00E8E445"), Y = F("00F06B45"), Z = F("00806C44"), HpCurrent = 12696, HpMax = 12696, IsSummonLike = true },
            new SpawnEncodeOptions { Heading = F("3AAF7D43"), Unknown16 = 0xB466, IncludeTail = false }
        },
        // Divine Aura with the inline owner name "Psefon" (gate bit 0).
        {
            "C3CD011F000106507365666F6EC0902C00400200B8D3450090624500005F44D235EC41FF1401C620C620",
            new SpawnEvent { Entity = 26307, KindByte = 0x1F, KindFlags = 0x00, CasterName = "Psefon", NpcCode = 2920640, X = F("00B8D345"), Y = F("00906245"), Z = F("00005F44"), HpCurrent = 4166, HpMax = 4166, IsSummonLike = true },
            new SpawnEncodeOptions { Heading = F("D235EC41"), Unknown16 = 0x14FF, IncludeTail = false }
        },
        // Boss Ultimate Berk 2300171 with 6,800,000 max HP (stacys :1341).
        {
            "DBBC010C22000B19230000026DF41AC6973A684300002CC500983243007F0180859F0380859F03",
            new SpawnEvent { Entity = 24155, KindByte = 0x0C, KindFlags = 0x22, NpcCode = 2300171, X = F("6DF41AC6"), Y = F("973A6843"), Z = F("00002CC5"), HpCurrent = 6_800_000, HpMax = 6_800_000 },
            new SpawnEncodeOptions { Heading = F("00983243"), Unknown16 = 0x7F00, IncludeTail = false }
        },
        // Monster Phantasmal Lakshmi, 1,500,000 HP.
        {
            "A9B1020C22000141230000" + "02B9331CC7DC0BA3C6005C28C6006001420017" + "01E0C65BE0C65B",
            new SpawnEvent { Entity = 39081, KindByte = 0x0C, KindFlags = 0x22, NpcCode = 2310401, X = F("B9331CC7"), Y = F("DC0BA3C6"), Z = F("005C28C6"), HpCurrent = 1_500_000, HpMax = 1_500_000 },
            new SpawnEncodeOptions { Heading = F("00600142"), Unknown16 = 0x1700, IncludeTail = false }
        },
    };

    [Theory]
    [MemberData(nameof(SpawnHeads))]
    public void Spawn_heads_match_real_bytes(string hex, SpawnEvent e, SpawnEncodeOptions o) =>
        Assert.Equal(hex.ToUpperInvariant(), Of(PacketEncoders.EncodeSpawn(e, o)));

    [Fact]
    public void Spawn_owner_block_follows_the_real_layout()
    {
        // The §8.5 owner rule on the REAL frames: Water Spirit → 10894 (anchor = owner), Bittercold Wind → 15422 (anchor = itself), monster → none.
        var water = Parse("A5AE011F1000C18E2C0000020028A04500788245008031440E86B1437AFC01DF28DF2819070000190700000000000000000000000000005892010064000000F04902000100000000000000A08601000000000090D00300010111010F329A09FFFFFFFFFFFFFFFF8075D52ABB0300008E5509022B5A9B45CADA7B4590C525440702068E2A000002CD00C4040000D0003D0100001E000000E31D030000");
        var wind = Parse("EBC5011F00004B8E2C004002B9331CC7DC0BA3C6005C28C648E9AE43C3F801B645B6457A0D00007A0D0000000000000000000000000000508B010064000000F04902000100000000000000A08601000000000000E20400010101110181969800FFFFFFFFFFFFFFFF8075D52ABB030000EBC5010102B9331CC7DC0BA3C6005C28C60702063E3C000002CD008C050000D000310100002D00000000");
        Assert.Equal(10894u, SpawnProbe.Owner(water));
        Assert.Equal(10894u, SpawnProbe.AnchorId(water));
        Assert.Equal(15422u, SpawnProbe.Owner(wind));
        Assert.Equal(25323u, SpawnProbe.AnchorId(wind));

        // Our encoder's tail obeys the same rule.
        var spirit = new SpawnEvent { Entity = 22309, KindByte = 0x1F, KindFlags = 0x10, NpcCode = 2920129, HpMax = 5215, OwnerId = 10894, AnchorId = 10894, IsSummonLike = true, X = 5000.5f, Y = 4175.0f, Z = 709.0f };
        var spiritBody = PacketEncoders.EncodeSpawn(spirit);
        Assert.Equal(10894u, SpawnProbe.Owner(spiritBody));
        Assert.Equal(10894u, SpawnProbe.AnchorId(spiritBody));

        var windEvent = spirit with { Entity = 25323, KindFlags = 0, NpcCode = 2920011, OwnerId = 15422, AnchorId = 25323 };
        var windBody = PacketEncoders.EncodeSpawn(windEvent);
        Assert.Equal(15422u, SpawnProbe.Owner(windBody));
        Assert.Equal(25323u, SpawnProbe.AnchorId(windBody));

        var monster = new SpawnEvent { Entity = 40610, KindByte = 0x0C, KindFlags = 0x22, NpcCode = 2300171, HpMax = 6_800_000, X = -9988.6f, Y = 15286.3f, Z = -143f };
        var monsterBody = PacketEncoders.EncodeSpawn(monster);
        Assert.Null(SpawnProbe.Owner(monsterBody));
        Assert.Equal(40610u, SpawnProbe.AnchorId(monsterBody));
    }

    [Fact]
    public void Spawn_tail_never_leaks_a_false_owner_marker()
    {
        var rng = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            var monster = new SpawnEvent
            {
                Entity = (uint)rng.Next(1, 9_999_999), KindByte = 0x0C, KindFlags = 0x22, NpcCode = (uint)rng.Next(2_000_000, 2_999_999),
                HpMax = rng.Next(1, int.MaxValue), X = BitConverter.Int32BitsToSingle(0x00060207 + i), Y = (float)rng.NextDouble() * 1e4f, Z = 7.0f,
            };
            Assert.Null(SpawnProbe.Owner(PacketEncoders.EncodeSpawn(monster)));
        }
    }

    [Fact]
    public void Self_info_matches_real_bytes()
    {
        // taengu stream_processor.rs: Naicha, 14957, server 1304, Cleric (30), level 28, with the real tail.
        var e = new SelfInfoEvent { Entity = 14957, Name = "Naicha", ServerId = 1304, ClassCode = 30, Class = CharacterClass.Cleric, Level = 28 };
        var o = new SelfInfoEncodeOptions { Mask1 = 0x28C1915E, Tail = Parse("7f0100007f0100001c000000d002040000000000") };
        Assert.Equal("ED745E91C12837064E616963686118051E000000011C0000007F0100007F0100001C000000D002040000000000", Of(PacketEncoders.EncodeSelfInfo(e, o)));

        // §8.6 vector Aahz (331, Gladiator code 6, level 33) with default masks.
        var aahz = new SelfInfoEvent { Entity = 331, Name = "Aahz", ServerId = 1304, ClassCode = 6, Class = CharacterClass.Gladiator, Level = 33 };
        Assert.Equal("CB025FA1C12837044161687A1805060000000121000000", Of(PacketEncoders.EncodeSelfInfo(aahz)));
        Assert.Equal("3336CB025FA1C12837044161687A1805060000000121000000", Of(PacketEncoders.EncodePayload(aahz)));
    }

    [Fact]
    public void Player_info_matches_real_bytes()
    {
        Assert.Equal("EA1B17B0A001070950656E63696C676F6E12000000",
            Of(PacketEncoders.EncodePlayerInfo(new PlayerInfoEvent { Entity = 3562, Name = "Pencilgon", ClassCode = 18, Class = CharacterClass.Assassin })));
        Assert.Equal("B81C1730A001070A426F6168616E636F6F6B1E000000",
            Of(PacketEncoders.EncodePlayerInfo(new PlayerInfoEvent { Entity = 3640, Name = "Boahancook", ClassCode = 30, Class = CharacterClass.Cleric },
                new PlayerInfoEncodeOptions { Mask1 = 0x01A03017 })));
        // The class code is derived from the class when not given (Assassin = 4×4 + 2).
        Assert.Equal("EA1B17B0A001070950656E63696C676F6E12000000",
            Of(PacketEncoders.EncodePlayerInfo(new PlayerInfoEvent { Entity = 3562, Name = "Pencilgon", Class = CharacterClass.Assassin })));
    }

    [Fact]
    public void Kill_record_matches_real_bytes()
    {
        // stacys :1092 — mob 49668 killed by Aahz (1086, server 1304, legion Akatsuki) with Rupture Strike.
        var k = new KillEvent { Target = 49668, SkillRaw = 11030030, Killer = 1086, KillerServerId = 1304, KillerName = "Aahz", KillerGuildName = "Akatsuki" };
        Assert.Equal("8484030E4EA800BE081805044161687A08416B617473756B69010000000000000100", Of(PacketEncoders.EncodeKill(k)));
        // taengu — mob 44908, 15280242 by ApexZ (1454, server 1305, legion Ventus).
        var k2 = new KillEvent { Target = 44908, SkillRaw = 15280242, Killer = 1454, KillerServerId = 1305, KillerName = "ApexZ", KillerGuildName = "Ventus" };
        Assert.Equal("ECDE027228E900AE0B190505417065785A0656656E74757301000000",
            Of(PacketEncoders.EncodeKill(k2, new KillEncodeOptions { Trailer = [0x01, 0x00, 0x00, 0x00] })));
    }

    [Fact]
    public void Map_load_matches_real_bytes()
    {
        // taengu :2808 — load 1 into 600021 Fire Temple (52-byte frame).
        var o = new MapLoadEncodeOptions { InstanceKey = 0x00351A3B, X = F("f7e64646"), Y = F("0d7fb0c6"), Z = F("0080b045"), Heading = F("409da542") };
        var body = PacketEncoders.EncodeMapLoad(new MapLoadEvent { LoadCount = 1, MapId = 600021 }, o);
        Assert.Equal("01000000D52709003B1A350000000000F7E646460D7FB0C60080B045409DA54200000000000000000000004F0000", Of(body));
        Assert.Equal(0x34u, Framing.LengthField(body.Length + 2));
    }

    [Fact]
    public void Buff_records_and_heartbeat_match_the_real_bundle()
    {
        // Inner frames of the real stacys bundle (decoded in PROTOCOL.md §8.13): buff 181200301 for 12,000 ms.
        var buff = new BuffAppliedEvent
        {
            Target = 3576, Stack = 0x35, BuffId = 181200301, DurationMs = 12000, ExpiryUnixMs = 0x000001A0F420A065, Caster = 3564, SourceSkill = 18120030,
        };
        var o = new BuffEncodeOptions { X = F("4f576fc6"), Y = F("0aa77246"), Z = F("00c02144") };
        Assert.Equal("F81B011335ADE5CC0AE02E00000000000065A020F4A0010000EC1B0C5E7D1401004F576FC60AA7724600C02144", Of(PacketEncoders.EncodeBuffApplied(buff, o)));
        Assert.Equal("F81B01ADE5CC0A", Of(PacketEncoders.EncodeBuffRemoved(new BuffRemovedEvent { Target = 3576, BuffId = 181200301 })));
        // 00 36 with server time 2026-09-30 21:02:42.053 UTC; the buff expires exactly 12 s later.
        Assert.Equal("857120F4A0010000", Of(PacketEncoders.EncodeHeartbeat(new HeartbeatEvent { ServerUnixMs = 0x01A0F4207185 })));
        Assert.Equal(12000ul, buff.ExpiryUnixMs - 0x01A0F4207185);
    }

    [Fact]
    public void Cast_announcements_match_real_bytes()
    {
        // cyber ProtocolTests.cs:42-43 — a Sorcerer's Bittercold Wind (seq 0x92) on boss 46161, then the wind 42321 announcing 15280141.
        var c1 = new CastEvent { Actor = 8647, SkillRaw = 15280140, Target = 46161 };
        var o1 = new CastEncodeOptions { Sequence = 0x92, X = F("b0f930c3"), Y = F("8dbae847"), Z = F("0b71d3c7"), Heading = F("00faa546"), Tail = Parse("ea5d01dc6f") };
        Assert.Equal("C743000C28E90092" + "02D1E802B0F930C38DBAE8470B71D3C700FAA546EA5D01DC6F", Of(PacketEncoders.EncodeCast(c1, o1)));
        var c2 = new CastEvent { Actor = 42321, SkillRaw = 15280141, Target = 42321 };
        var o2 = new CastEncodeOptions { Sequence = 0x01, X = F("a263c741"), Y = F("d3c7ea47"), Z = F("bd1ed1c7"), Heading = F("0048a546"), Tail = Parse("ee64029a3a") };
        Assert.Equal("D1CA02000D28E9000102D1CA02A263C741D3C7EA47BD1ED1C70048A546EE64029A3A", Of(PacketEncoders.EncodeCast(c2, o2)));
    }

    [Fact]
    public void Ping_echo_layout()
    {
        // taengu ping_tracker.rs: 18 | 03 36 00 00 | i64 client clock | i64 server.
        var body = PacketEncoders.EncodePing(new PingEvent { ClientSentMs = 17_552_452_660 }, 1_790_000_000_064);
        Assert.Equal("0000" + Of(BitConverter.GetBytes(17_552_452_660L)) + Of(BitConverter.GetBytes(1_790_000_000_064L)), Of(body));
        Assert.Equal(0x18, Framing.Frame(PacketEncoders.Payload(Opcodes.Ping, body))[0]);
    }

    [Fact]
    public void Party_roster_head_and_member_prefix_match_real_bytes()
    {
        // stacys :1103 — party 0x591C "Es geht sofort los.", dungeon 600011, 5 slots, first member Pencilgon (server 1304).
        var members = new List<PartyMember>
        {
            new() { Slot = 1, CharacterId = 0x32EAE, ServerId = 1304, Name = "Pencilgon", ClassCode = 18, Class = CharacterClass.Assassin, Level = 30, GearScore = 452, CombatPower = 22667 },
        };
        var e = new PartyRosterEvent { PartyKey = 0x591C, DungeonId = 600011, Members = members };
        var o = new RosterEncodeOptions { Title = "Es geht sofort los.", PartySize = 5, SlotCount = 5 };
        string real = "1C590000134573206765687420736F666F7274206C6F732E05CB2709000003AE2E030000001805FF0103051E01AE2E0300000018050950656E63696C676F6E120000001E000000C40100001805D810048B58000000000000";
        Assert.StartsWith(real, Of(PacketEncoders.EncodePartyRoster(e, o)));
    }

    [Fact]
    public void Small_records_follow_the_spec_layouts()
    {
        Assert.Equal("A2BC020003", Of(PacketEncoders.EncodeDeath(new DeathEvent { Entity = 40482, Flag = 3 })));
        Assert.Equal("A2BC0280DDDB0180DDDB01", Of(PacketEncoders.EncodeHpUpdate(new HpUpdateEvent { Entity = 40482, Hp = 3_600_000, HpMax = 3_600_000 })));
        Assert.Equal("0000ED7400000000353B0300", Of(PacketEncoders.EncodeGlobalIdLink(new GlobalIdLinkEvent { Entity = 14957, CharacterId = 0x33B35 })));
        Assert.Equal("A2BC020001", Of(PacketEncoders.EncodeBattleToggle(new BattleToggleEvent { Entity = 40482, Engaged = true })));
        Assert.Equal(Opcodes.Damage, PacketEncoders.OpcodeOf(new DamageEvent()));
        Assert.Equal(Opcodes.Spawn, PacketEncoders.OpcodeOf(new SpawnEvent()));
    }

    [Fact]
    public void Effect_ids_pass_the_validator()
    {
        foreach (uint skill in new uint[] { 11020030, 14010243, 16140030, 17100030, 15280243, 100015 })
            Assert.True(SkillIds.EffectMatchesSkill(PacketEncoders.EffectFor(skill), skill), skill.ToString());
    }
}
