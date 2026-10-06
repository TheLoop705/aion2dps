using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

/// <summary>Real <c>41 36</c> spawns (§8.5; stacys SelfCheckAion2.cs:262-266, 659, 696-699, 739-745, 1340-1341; cyber ProtocolTests.cs:103-113).</summary>
public class SpawnTests
{
    private const string WaterSpirit = "4136A5AE011F1000C18E2C0000020028A04500788245008031440E86B1437AFC01DF28DF2819070000190700000000000000000000000000005892010064000000F04902000100000000000000A08601000000000090D00300010111010F329A09FFFFFFFFFFFFFFFF8075D52ABB0300008E5509022B5A9B45CADA7B4590C525440702068E2A000002CD00C4040000D0003D0100001E000000E31D030000";
    private const string FireSpirit = "4136B7DD011F1000B28E2C00400200E8E44500F06B4500806C443AAF7D4366B40198639863630B0000630B000000000000000000000000000060B2010064000000F04902000100000000000000A08601000000000000E20400010101110144AA9809FFFFFFFFFFFFFFFF8075D52ABB030000CF190E02FB71E7451296784571BE6044070206CF0C000002CD005A000000D000300100002D000000DD1D030000";
    private const string DivineAura = "4136C3CD011F000106507365666F6EC0902C00400200B8D3450090624500005F44D235EC41FF1401C620C620620800006208000000000000000000000000000010D0010064000000F04902000100000000000000A08601000000000090D00300010101110181969800FFFFFFFFFFFFFFFF8075D52ABB030000C3CD01010200B8D3450090624500005F44070206FE10000002CD00A0000000D000360100001E00000000";
    private const string Lakshmi = "4136A9B1020C2200014123000002B9331CC7DC0BA3C6005C28C600600142001701E0C65BE0C65B640000006400000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000603110181969800FFFFFFFFFFFFFFFF8075D52ABB030000A9B1020128B9331CC7DC0BA3C6005C28C6110284969800FFFFFFFFFFFFFFFF8075D52ABB030000A9B10201B9331CC7DC0BA3C6005C28C61103BC060000FFFFFFFFFFFFFFFF8075D52ABB030000A9B10205B9331CC7DC0BA3C6005C28C601002D0000000301EE020000EE020000B67153BE00";
    private const string MaRioWind = "4136EBC5011F00004B8E2C004002B9331CC7DC0BA3C6005C28C648E9AE43C3F801B645B6457A0D00007A0D0000000000000000000000000000508B010064000000F04902000100000000000000A08601000000000000E20400010101110181969800FFFFFFFFFFFFFFFF8075D52ABB030000EBC5010102B9331CC7DC0BA3C6005C28C60702063E3C000002CD008C050000D000310100002D00000000";
    private const string MonsterSpawn = "4136BAEB020C22000341230000028B6CCCC64DE0044600C8D7C50098B24300FE01E0C65BE0C65B640000006400000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000603110181969800FFFFFFFFFFFFFFFF8075D52ABB030000BAEB0201288B6CCCC64DE0044600C8D7C5110284969800FFFFFFFFFFFFFFFF8075D52ABB030000BAEB02018B6CCCC64DE0044600C8D7C51103BC060000FFFFFFFFFFFFFFFF8075D52ABB030000BAEB02058B6CCCC64DE0044600C8D7C501002D0000000301EE020000EE0200000E7253C500";
    private const string LumyWind = "4136FC80011F0000D2902C004002B1E2BDC649CAFD4500B8D8C5D61EC340560401D24BD24B5A0E00005A0E0000000000000000000000000000E4B5010064000000F04902000100000000000000A086010000000000CE180500010101110181969800FFFFFFFFFFFFFFFF8075D52ABB030000FC80010102B1E2BDC649CAFD4500B8D8C50702060A3E000002CD001A090000D000330100002D00000000";
    private const string AurulioWind = "4136E3A3021F00000A8F2C004002B1E2BDC649CAFD4500B8D8C5E7420543C35E01EF3FEF3FD00E0000D00E0000000000000000000000000000508B010064000000F04902000100000000000000A08601000000000000E20400010101110181969800FFFFFFFFFFFFFFFF8075D52ABB030000E3A3020102B1E2BDC649CAFD4500B8D8C5070206BD3E000002CD00AA000000D0003C0100002D00000000";
    private const string Harcon = "4136caed010c2200c81823000002291745c609d96e4600000fc300a00c4300640180dddb0180dddb01640000006400000000000000000000000000000000000000504600005046000001000000000000000000000000000000000000000602110181969800ffffffffffffffff8075d52abb030000caed010114291745c609d96e4600000fc3110284969800ffffffffffffffff8075d52abb030000caed0101291745c609d96e4600000fc301002d0000000301b0040000b004000006b6e88b00";
    private const string Berk = "4136dbbc010c22000b19230000026df41ac6973a684300002cc500983243007f0180859f0380859f03640000006400000000000000000000000000000000000000409c0000409c000001000000000000000000000000000000000000000603110181969800ffffffffffffffff8075d52abb030000dbbc0101086df41ac6973a684300002cc5110284969800ffffffffffffffff8075d52abb030000dbbc01016df41ac6973a684300002cc511045dae1201ffffffffffffffff8075d52abb030000dbbc01016df41ac6973a684300002cc501002d0000000301f0000000f0000000a5b5688a00";

    [Fact]
    public void Water_spirit_owner_via_070206_is_10894()
    {
        var s = Run.Single<SpawnEvent>(WaterSpirit);
        Assert.Equal(22309u, s.Entity);
        Assert.Equal(0x1F, s.KindByte);
        Assert.Equal(0x10, s.KindFlags);
        Assert.True(s.IsSummonLike);
        Assert.Null(s.CasterName);
        Assert.Equal(2920129u, s.NpcCode);
        Assert.Equal(5215, s.HpCurrent);
        Assert.Equal(5215, s.HpMax);
        Assert.Equal(10894u, s.OwnerId);
        Assert.Equal(10894u, s.AnchorId);
        Assert.InRange(s.X, 5000f, 6000f);
    }

    [Fact]
    public void Fire_spirit_owner_via_070206_is_3279()
    {
        var s = Run.Single<SpawnEvent>(FireSpirit);
        Assert.Equal(28343u, s.Entity);
        Assert.Equal(2920114u, s.NpcCode);
        Assert.Equal(12696, s.HpMax);
        Assert.Equal(3279u, s.OwnerId);
        Assert.Equal(3279u, s.AnchorId);
    }

    [Fact]
    public void Divine_aura_has_inline_owner_name_and_owner_4350()
    {
        var s = Run.Single<SpawnEvent>(DivineAura);
        Assert.Equal(26307u, s.Entity);
        Assert.Equal("Psefon", s.CasterName);
        Assert.Equal(2920640u, s.NpcCode);
        Assert.Equal(4350u, s.OwnerId);
        Assert.Equal(s.Entity, s.AnchorId); // the anchor names the entity itself for auras/winds
        Assert.True(s.IsSummonLike);
    }

    [Theory]
    [InlineData(MaRioWind, 25323u, 15422u)]
    [InlineData(LumyWind, 16508u, 15882u)]
    [InlineData(AurulioWind, 37347u, 16061u)]
    public void Bittercold_winds_owner_via_070206(string hex, uint entity, uint owner)
    {
        var s = Run.Single<SpawnEvent>(hex);
        Assert.Equal(entity, s.Entity);
        Assert.Equal(owner, s.OwnerId);
        Assert.Equal(entity, s.AnchorId);
        Assert.True(s.IsSummonLike);
        Assert.Equal(0x00, s.KindFlags);
    }

    [Fact]
    public void Monsters_have_no_owner_and_anchor_themselves()
    {
        var l = Run.Single<SpawnEvent>(Lakshmi);
        Assert.Equal(39081u, l.Entity);
        Assert.Equal(0x0C, l.KindByte);
        Assert.Equal(0x22, l.KindFlags);
        Assert.False(l.IsSummonLike);
        Assert.Equal(2310401u, l.NpcCode);
        Assert.Equal(1_500_000, l.HpMax);
        Assert.Null(l.OwnerId);
        Assert.Equal(39081u, l.AnchorId);

        var m = Run.Single<SpawnEvent>(MonsterSpawn);
        Assert.Equal(46522u, m.Entity);
        Assert.Equal(2310403u, m.NpcCode);
        Assert.Null(m.OwnerId);
    }

    [Fact]
    public void Boss_spawns_carry_max_hp_3_6M_and_6_8M()
    {
        var harcon = Run.Single<SpawnEvent>(Harcon);
        Assert.Equal(30410u, harcon.Entity);
        Assert.Equal(2300104u, harcon.NpcCode);
        Assert.Equal(3_600_000, harcon.HpCurrent);
        Assert.Equal(3_600_000, harcon.HpMax);
        Assert.Null(harcon.OwnerId);

        var berk = Run.Single<SpawnEvent>(Berk);
        Assert.Equal(24155u, berk.Entity);
        Assert.Equal(2300171u, berk.NpcCode);
        Assert.Equal(6_800_000, berk.HpMax);
        Assert.Null(berk.OwnerId);
        Assert.False(berk.IsSummonLike);
    }

    [Fact]
    public void Ownerless_wind_spawn()
    {
        var s = Run.Single<SpawnEvent>("4136" + "d1ca021f0000cf902c0040025ec7ea47f11ed1c70048a546a263c741b911019d549d54d20f0000d20f0000" +
                                       "0000000000000000000000000000c8f401006400000000f049020001000000");
        Assert.Equal(42321u, s.Entity);
        Assert.Equal(2920655u, s.NpcCode);
        Assert.Equal(10781, s.HpMax);
        Assert.Null(s.OwnerId);
        Assert.Null(s.AnchorId);
        Assert.True(s.IsSummonLike);
    }

    [Fact]
    public void Owner_equal_to_entity_or_out_of_range_is_rejected()
    {
        // The Water Spirit with its 07 02 06 owner replaced by its own id (22309 = 0x5725), then by 0.
        var self = WaterSpirit.Replace("0702068E2A0000", "07020625570000", StringComparison.Ordinal);
        Assert.Null(Run.Single<SpawnEvent>(self).OwnerId);
        var zero = WaterSpirit.Replace("0702068E2A0000", "07020600000000", StringComparison.Ordinal);
        Assert.Null(Run.Single<SpawnEvent>(zero).OwnerId);
        var huge = WaterSpirit.Replace("0702068E2A0000", "07020680969800", StringComparison.Ordinal); // 10,000,000
        Assert.Null(Run.Single<SpawnEvent>(huge).OwnerId);
    }

    [Fact]
    public void Caster_anchor_only_spawn_with_unrecognised_head_is_still_emitted()
    {
        // taengu synthetic spirit: entity 47324, kind 1F, caster anchor -> 6332, no structured head.
        var bytes = W.Concat(W.H("dcf1021f1000c6"), Enumerable.Repeat((byte)0x22, 24).ToArray(), W.H("8075d52abb030000bc310c02"));
        var (events, diag) = Run.Body(Opcodes.Spawn, Convert.ToHexString(bytes));
        var s = Assert.IsType<SpawnEvent>(Assert.Single(events));
        Assert.Equal(47324u, s.Entity);
        Assert.Equal(6332u, s.AnchorId);
        Assert.Equal(0u, s.NpcCode);
        Assert.True(s.IsSummonLike);
        Assert.Equal(1, diag.GetStat(Opcodes.Spawn).Failed);
    }

    [Fact]
    public void Gate_at_plus4_fallback()
    {
        // Same Lakshmi head with a u32 mask (two extra bytes before the gate).
        Assert.StartsWith("4136A9B1020C22", Lakshmi);
        var widened = "4136A9B1020C22" + "AAAA" + Lakshmi[14..];
        var s = Run.Single<SpawnEvent>(widened);
        Assert.Equal(2310401u, s.NpcCode);
        Assert.Equal(1_500_000, s.HpMax);
    }

    [Theory]
    [InlineData(0x1F, true)]
    [InlineData(0x1D, true)]
    [InlineData(0x5D, true)]
    [InlineData(0x5F, true)]
    [InlineData(0x1C, true)]
    [InlineData(0x0C, false)]
    [InlineData(0x0D, false)]
    public void Summon_kinds(byte kind, bool summon) => Assert.Equal(summon, PacketDecoder.IsSummonKind(kind));
}
