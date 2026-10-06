using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

/// <summary>Real Global <c>04 38</c> records (PROTOCOL.md §8.2.4, §16).</summary>
public class DamageTests
{
    private static DamageEvent One(string bodyHex)
    {
        var (events, diag) = Run.Body(Opcodes.Damage, bodyHex);
        Assert.True(diag.DecodeErrors == 0, string.Join("\n", diag.GetRecentErrors()));
        return Assert.IsType<DamageEvent>(Assert.Single(events));
    }

    [Fact]
    public void Keen_strike_layout6_front()
    {
        var d = One("848403 06 00 be08 fe26a800 4f 02 000002 433baf41 01000000 ba4f fd02 0100");
        Assert.Equal(49668u, d.Target);
        Assert.Equal(1086u, d.Actor);
        Assert.Equal(6, d.Layout);
        Assert.Equal(11020030u, d.SkillRaw);
        Assert.Equal(11020030u, d.SkillId);
        Assert.Equal(0x4F, d.HitTag);
        Assert.Equal(2, d.DamageType);
        Assert.False(d.IsCritical);
        Assert.Equal(HitDirection.Front, d.Direction);
        Assert.Equal(HitMods.None, d.Mods);
        Assert.Equal(1102003011u, d.EffectId);
        Assert.Equal(1u, d.HitIndex);
        Assert.Equal(10170u, d.PowerScalar);
        Assert.Equal(381, d.Amount);
        Assert.Empty(d.ExtraHits);
        Assert.True(d.EffectValidated);
        Assert.False(d.IsCastNotice);
        Assert.Equal(W.T0, d.Time);
    }

    [Fact]
    public void Keen_strike_crit_with_one_extra_hit()
    {
        var d = One("848403 26 00 be08 fe26a800 57 03 000002 433baf41 01000000 ba4f cc04 01 02 0100");
        Assert.True(d.IsCritical);
        Assert.Equal(588, d.Amount);
        Assert.Equal(new uint[] { 2 }, d.ExtraHits);
        Assert.Equal(0x26u, d.Switch);
    }

    [Fact]
    public void Blood_absorption_self_heal_layout4()
    {
        var d = One("be08 04 00 be08 57fcb200 4a 02 0792ea45 01000000 ba4f 41 0100");
        Assert.Equal(1086u, d.Target);
        Assert.Equal(1086u, d.Actor);
        Assert.Equal(4, d.Layout);
        Assert.Equal(11730007u, d.SkillId);
        Assert.Equal(65, d.Amount);
        Assert.True(d.EffectValidated);
    }

    [Fact]
    public void Combustion_layout6_back_mods80_two_extra_hits_without_trailer()
    {
        var d = One("b1ea01 36 00 f30a 40c0f400 7a 03 800001 0b199b5f 01000000 ac52 a40d 02 18 18");
        Assert.Equal(30001u, d.Target);
        Assert.Equal(1395u, d.Actor);
        Assert.Equal(16040000u, d.SkillId);
        Assert.Equal(HitDirection.Back, d.Direction);
        Assert.Equal(HitMods.Unknown80, d.Mods);
        Assert.Equal(10540u, d.PowerScalar);
        Assert.Equal(1700, d.Amount);
        Assert.Equal(new uint[] { 24, 24 }, d.ExtraHits);
        Assert.True(d.IsCritical);
    }

    [Fact]
    public void Layout4_with_sw10_field_and_one_extra_hit()
    {
        var d = One("b1ea01 34 00 f30a e0b7f800 cd 02 8bd32761 01000000 ac52 d330 01 01 59");
        Assert.Equal(16300000u, d.SkillId);
        Assert.Equal(6227, d.Amount);
        Assert.Equal(new uint[] { 89 }, d.ExtraHits);
        Assert.True(d.EffectValidated);
    }

    [Fact]
    public void Bittercold_wind_summon_layout4_with_field()
    {
        var d = One("A9B102 14 00 EBC501 8227E900 03 02 D36E135B 01000000 F252 9105 01 0100");
        Assert.Equal(39081u, d.Target);
        Assert.Equal(25323u, d.Actor);
        Assert.Equal(15280002u, d.SkillId);
        Assert.Equal(10610u, d.PowerScalar);
        Assert.Equal(657, d.Amount);
        Assert.True(d.EffectValidated);
    }

    /// <summary>Deadshot 30,697 including 4 × 3,069 extra hits, verified against boss HP (§8.2.2).</summary>
    [Fact]
    public void Deadshot_30697_with_four_extra_hits()
    {
        var d = One("A2BC02 36 00 B436 83C7D500 EE 02 800001 18A18153 01000000 FA60 E9EF01 04 FD17 FD17 FD17 FD17 0100");
        Assert.Equal(40482u, d.Target);
        Assert.Equal(6964u, d.Actor);
        Assert.Equal(14010243u, d.SkillId);
        Assert.Equal(1401004312u, d.EffectId);
        Assert.True(d.EffectValidated); // variant id: only the 10,000-base check holds
        Assert.Equal(12410u, d.PowerScalar);
        Assert.Equal(30697, d.Amount);
        Assert.Equal(new uint[] { 3069, 3069, 3069, 3069 }, d.ExtraHits);
        Assert.Equal(HitDirection.Back, d.Direction);
        Assert.Equal(CharacterClass.Ranger, SkillIds.ClassOf(d.SkillId));
    }

    [Fact]
    public void Noia_sample_perfect_double_back()
    {
        var d = One("91C102 26 00 A114 48F1CA00 20 02 0C0001 2C40464F 01000000 8C9101 B1D114 04 9736 9736 9736 9736 0100");
        Assert.Equal(HitMods.Perfect | HitMods.Double, d.Mods);
        Assert.Equal(HitDirection.Back, d.Direction);
        Assert.Equal(18572u, d.PowerScalar);
        Assert.Equal(338097, d.Amount);
        Assert.Equal(4, d.ExtraHits.Count);
        Assert.All(d.ExtraHits, h => Assert.Equal(6935u, h));
    }

    [Fact]
    public void Layout0_cast_notice_has_no_amount()
    {
        var d = One("BAEB02 00 00 8A7C 7028E900 4B 02 D5CB135B 02000000 8A64 0200");
        Assert.Equal(46522u, d.Target);
        Assert.Equal(15882u, d.Actor);
        Assert.Equal(15280240u, d.SkillId);
        Assert.Equal(0, d.Layout);
        Assert.True(d.IsCastNotice);
        Assert.Null(d.Amount);
        Assert.Equal(2u, d.HitIndex);
        Assert.Equal(12810u, d.PowerScalar);
        Assert.True(d.EffectValidated);
    }

    /// <summary>stacys SelfCheckAion2.cs:104-107 (frames with opcode; amount/crit as the in-game log showed).</summary>
    [Theory]
    [InlineData("04388484030600be08fe26a8004f02000002433baf4101000000ba4ffd020100", 381L, false, 11020030u)]
    [InlineData("04388484032600be08fe26a8005703000002433baf4101000000ba4fcc0401020100", 588L, true, 11020030u)]
    [InlineData("04388484032600be080e4ea8004b02000002837dbe4101000000ba4fb00301010100", 432L, false, 11030030u)]
    [InlineData("0438be080400be0857fcb2004a020792ea4501000000ba4f410100", 65L, false, 11730007u)]
    public void Stacys_real_capture_frames(string payload, long amount, bool crit, uint skill)
    {
        var d = Run.Single<DamageEvent>(payload);
        Assert.Equal(amount, d.Amount);
        Assert.Equal(crit, d.IsCritical);
        Assert.Equal(skill, d.SkillId);
        Assert.True(d.EffectValidated);
    }

    /// <summary>Krao Cave frames: real hits and their layout-0 companions (stacys SelfCheckAion2.cs, no-damage scenario).</summary>
    [Theory]
    [InlineData("0438E1AD010600C522E0B7F80011020000028BD3276101000000A85ADA3E0100", 8026L, false)]
    [InlineData("0438C5220000C522E2B7F8000F0253D4276101000000A85A0100", null, false)]
    [InlineData("0438C5220000C522E0B7F800110295D3276102000000A85A0200", null, false)]
    [InlineData("0438A5C7012600C522102DF90036030000014B9A556101000000A85A8D1701A7020100", 2957L, true)]
    [InlineData("0438E1AD010000C522102DF90014024C9A556101000000A85A0100", null, false)]
    [InlineData("0438A9A6010400C522E046F6009D038BAF336001000000A85AA50F0100", 1957L, true)]
    [InlineData("0438A9A6010000C522E046F6009D028DAF336001000000A85A0100", null, false)]
    [InlineData("0438A9A6010000C52240C0F40072020D199B5F01000000A85A0100", null, false)]
    [InlineData("043885AE010400D92440C0F40009020B199B5F010000009656E7040100", 615L, false)]
    public void Krao_cave_hits_and_notices(string payload, long? amount, bool crit)
    {
        var d = Run.Single<DamageEvent>(payload);
        Assert.Equal(amount, d.Amount);
        Assert.Equal(amount is null, d.IsCastNotice);
        Assert.Equal(crit, d.IsCritical);
        Assert.True(d.EffectValidated);
    }

    /// <summary>cyberbadger ProtocolTests.cs:20-37 / taengu stream_processor.rs:2776-2803 (EU 2026-10-04).</summary>
    [Theory]
    [InlineData("b1ea013600f30a40c0f4007a038000010b199b5f01000000ac52a40d021818", 30001u, 1395u, 1700L, 2, 48u, true)]
    [InlineData("b1ea013400f30ae0b7f800cd028bd3276101000000ac52d330010159", 30001u, 1395u, 6227L, 1, 89u, false)]
    [InlineData("fe9e0224009e9b01c3f8f5000302792b156002000000ac52e10301060200", 36734u, 19870u, 481L, 1, 6u, false)]
    [InlineData("fe9e0224009e9b01c18601001702a7a2980001000000ac52e401030303030100", 36734u, 19870u, 228L, 3, 9u, false)]
    [InlineData("fe9e0204009e9b01c3f8f5000302792b156003000000ac52b7030300", 36734u, 19870u, 439L, 0, 0u, false)]
    [InlineData("b1ea011600f30a40c0f40063028000010b199b5f01000000ac52d007", 30001u, 1395u, 976L, 0, 0u, false)]
    public void Taengu_cyber_vectors(string body, uint target, uint actor, long amount, int extraCount, uint extraSum, bool crit)
    {
        var d = One(body);
        Assert.Equal(target, d.Target);
        Assert.Equal(actor, d.Actor);
        Assert.Equal(amount, d.Amount);
        Assert.Equal(extraCount, d.ExtraHits.Count);
        Assert.Equal(extraSum, (uint)d.ExtraHits.Sum(h => (long)h));
        Assert.Equal(crit, d.IsCritical);
        Assert.Equal(10540u, d.PowerScalar);
        Assert.True(d.EffectValidated);
    }

    [Fact]
    public void Spirit_skill_ids_and_link_record()
    {
        // Water Spirit hit (100027), spirit spawn self-heal link record 16990004 of 110,000, Fire Spirit crit.
        var water = Run.Single<DamageEvent>("0438EC91010600A5AE01BB86010002020000024FA09800010000009E5A690100");
        Assert.Equal(22309u, water.Actor);
        Assert.Equal(100027u, water.SkillId);
        Assert.Equal(SkillKind.Spirit, SkillIds.GetKind(water.SkillId));
        Assert.Equal(105, water.Amount);

        var link = Run.Single<DamageEvent>("0438A5AE010400A5AE01343F030101025CB04465010000009E5AB0DB060100");
        Assert.Equal(SkillKind.Link, SkillIds.GetKind(link.SkillId));
        Assert.Equal(link.Actor, link.Target);
        Assert.Equal(110000, link.Amount);

        var fire = Run.Single<DamageEvent>("0438EC91012600B7DD01AE8601000503000002D99A980001000000C0528B0501410100");
        Assert.Equal(28343u, fire.Actor);
        Assert.Equal(651, fire.Amount);
        Assert.True(fire.IsCritical);
    }

    [Fact]
    public void Summon_hits_carry_their_owners_power_scalar()
    {
        var lumyCast = Run.Single<DamageEvent>("0438BAEB0200008A7C7028E9004B02D5CB135B020000008A640200");
        var lumyWind = Run.Single<DamageEvent>("0438BAEB021400FC80017328E9000203F7CC135B010000008A64C30C010100");
        var aurulioCast = Run.Single<DamageEvent>("0438BAEB020000BD7D9E27E900CF02CD79135B020000009E550200");
        var aurulioWind = Run.Single<DamageEvent>("0438BAEB020400E3A302A127E9000203EF7A135B010000009E55DC070100");
        Assert.Equal(15882u, lumyCast.Actor);
        Assert.Equal(16508u, lumyWind.Actor);
        Assert.Equal(lumyCast.PowerScalar, lumyWind.PowerScalar);
        Assert.Equal(12810u, lumyWind.PowerScalar);
        Assert.Equal(1603, lumyWind.Amount);
        Assert.Equal(16061u, aurulioCast.Actor);
        Assert.Equal(37347u, aurulioWind.Actor);
        Assert.Equal(10910u, aurulioWind.PowerScalar);
        Assert.Equal(aurulioCast.PowerScalar, aurulioWind.PowerScalar);
        Assert.Equal(988, aurulioWind.Amount);
    }

    [Fact]
    public void Divine_aura_hit()
    {
        var d = Run.Single<DamageEvent>("0438EC91010600C3CD0150B00501020200000193D3386601000000BC50C2070100");
        Assert.Equal(26307u, d.Actor);
        Assert.Equal(962, d.Amount);
    }

    [Fact]
    public void Chained_records_are_decoded_when_valid()
    {
        var a = "848403 06 00 be08 fe26a800 4f 02 000002 433baf41 01000000 ba4f fd02 0100";
        var b = "be08 04 00 be08 57fcb200 4a 02 0792ea45 01000000 ba4f 41 0100";
        var (events, diag) = Run.Body(Opcodes.Damage, a + b);
        Assert.Equal(2, events.Count);
        Assert.Equal(381, ((DamageEvent)events[0]).Amount);
        Assert.Equal(65, ((DamageEvent)events[1]).Amount);
        Assert.Equal(0, diag.DamageTrailingBytes);
        Assert.Equal(2, diag.DamageRecords);
    }

    [Fact]
    public void Invalid_chained_record_is_not_emitted()
    {
        // A second "record" whose effect id does not match its skill.
        var a = "848403 06 00 be08 fe26a800 4f 02 000002 433baf41 01000000 ba4f fd02 0100";
        var bad = "be08 04 00 be08 57fcb200 4a 02 11111111 01000000 ba4f 41 0100";
        var (events, diag) = Run.Body(Opcodes.Damage, a + bad);
        Assert.Single(events);
        Assert.Equal(1, diag.DamageTrailingBytes);
        Assert.Equal(0, diag.DecodeErrors);
    }

    [Fact]
    public void Theostone_skill_is_normalized()
    {
        // Synthetic layout-4 record with raw skill 3,012,345 (theostone) and effect raw*100+11.
        uint raw = 3_012_345;
        var body = W.DamageBody(100, 200, 500, raw);
        var (events, _) = Run.Body(Opcodes.Damage, Convert.ToHexString(body));
        var d = Assert.IsType<DamageEvent>(Assert.Single(events));
        Assert.Equal(raw, d.SkillRaw);
        Assert.Equal(30_123_451u, d.SkillId);
        Assert.True(d.EffectValidated);
    }

    [Fact]
    public void Layout5_and_7_locate_the_effect_by_validator()
    {
        // Layout 5: 4 unknown bytes between dmg_type and the effect id.
        var l5 = "848403 05 00 be08 fe26a800 4f 02 AABBCCDD 433baf41 01000000 ba4f fd02 0100";
        var d5 = One(l5);
        Assert.Equal(5, d5.Layout);
        Assert.Equal(1102003011u, d5.EffectId);
        Assert.Equal(381, d5.Amount);

        // Layout 7: mods/00/dir + 4 unknown bytes.
        var l7 = "848403 07 00 be08 fe26a800 4f 03 080001 01020304 433baf41 01000000 ba4f fd02 0100";
        var d7 = One(l7);
        Assert.Equal(HitMods.Double, d7.Mods);
        Assert.Equal(HitDirection.Back, d7.Direction);
        Assert.Equal(381, d7.Amount);

        // No effect id to be found: rejected.
        var (events, diag) = Run.Body(Opcodes.Damage, "848403 05 00 be08 fe26a800 4f 02 AABBCCDD 11111111 01000000 ba4f fd02 0100");
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
    }

    [Theory]
    [InlineData("848403 03 00 be08 fe26a800 4f 02 433baf41 01000000 ba4f fd02 0100")]   // layout 3
    [InlineData("848403 08 00 be08 fe26a800 4f 02 433baf41 01000000 ba4f fd02 0100")]   // layout 8
    [InlineData("848403 06 00 be08 fe26a800 4f 02 000002 433baf41")]                     // truncated
    [InlineData("848403 24 00 be08 fe26a800 4f 02 433baf41 01000000 ba4f fd02 00 0100")] // 0 extra hits
    [InlineData("848403 24 00 be08 fe26a800 4f 02 433baf41 01000000 ba4f 05 01 06 0100")] // extras >= amount
    [InlineData("848403 04 00 be08 fe26a800 4f 02 433baf41 01000000 ba4f 80C2D72F 0100")] // amount 100,000,000 above cap
    [InlineData("")]
    public void Malformed_records_are_failures_not_exceptions(string body)
    {
        var (events, diag) = Run.Body(Opcodes.Damage, body);
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
        Assert.Equal(1, diag.GetStat(Opcodes.Damage).Failed);
        Assert.NotEmpty(diag.GetRecentErrors());
    }
}
