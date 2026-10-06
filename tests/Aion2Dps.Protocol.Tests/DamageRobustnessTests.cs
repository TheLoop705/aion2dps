using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

public sealed class DamageRobustnessTests
{
    // Real NPC area hit: hit_index 3, trailer sequence 9, amount 405.
    private const string AreaHit = "876E0600DCAB028EB912001F02000002837B500703000000904E95030900";
    private const string PlayerHit = "8484030600BE08FE26A8004F02000002433BAF4101000000BA4FFD020100";
    private const string Placeholder = "DCAB020400DCAB025E420F00010251E1F50501000000904E80CAB5EE010100";

    [Fact]
    public void Generic_retaliation_companion_preserves_explicit_shield_effect_and_net_amount()
    {
        // Sanitized Draupnir shield interaction: generic type-9 companion, 76 damage after absorption.
        var (events, diag) = Run.Body(Opcodes.Damage,
            "644400C8010000000000094B33636500000000004C01AFDEEA690000");
        var d = Assert.IsType<DamageEvent>(Assert.Single(events));
        Assert.Equal(100u, d.Target);
        Assert.Equal(200u, d.Actor);
        Assert.Equal(0u, d.SkillRaw);
        Assert.Equal(0x44u, d.Switch);
        Assert.Equal(9, d.DamageType);
        Assert.Equal(76, d.Amount);
        Assert.Equal(new uint[] { 1777000111 }, d.AbsorbEffects);
        Assert.False(d.EffectValidated);
        Assert.Equal(0, diag.DecodeErrors);
        Assert.Equal(0, diag.DamageTrailingBytes);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    public void Absorb_effects_are_preserved_for_switch_and_flag_blocks(int count, bool flagBlock)
    {
        var effects = Enumerable.Range(0, count).Select(i => 1777000111u + (uint)i).ToArray();
        var body = W.DamageBody(100, 200, 500);
        if (flagBlock) body[2] |= 0x01;
        else body[1] |= 0x40;
        var wire = W.Concat(body[..^2], W.VarInt((uint)count),
            effects.SelectMany(BitConverter.GetBytes).ToArray(), body[^2..]);
        var (events, diag) = Run.Body(Opcodes.Damage, Convert.ToHexString(wire));

        Assert.Equal(effects, Assert.IsType<DamageEvent>(Assert.Single(events)).AbsorbEffects);
        Assert.Equal(0, diag.DecodeErrors);
        Assert.Equal(0, diag.DamageTrailingBytes);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 9)]
    [InlineData(1, 0)]
    [InlineData(8, 7)]
    public void Malformed_absorb_counts_and_truncated_effects_are_rejected(int announced, int present)
    {
        var body = W.DamageBody(100, 200, 500);
        body[1] |= 0x40;
        var wire = W.Concat(body[..^2], W.VarInt((uint)announced), new byte[present * 4], body[^2..]);
        var (events, diag) = Run.Body(Opcodes.Damage, Convert.ToHexString(wire));

        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
    }

    [Fact]
    public void Area_hit_trailer_does_not_hide_the_next_valid_record()
    {
        var (events, diag) = Run.Body(Opcodes.Damage, AreaHit + PlayerHit);

        Assert.Equal(new long?[] { 405, 381 }, events.OfType<DamageEvent>().Select(e => e.Amount));
        Assert.Equal(2, diag.DamageRecords);
        Assert.Equal(0, diag.DamageTrailingBytes);
        Assert.Equal(0, diag.DecodeErrors);
    }

    [Fact]
    public void Several_area_records_with_differing_trailer_sequences_chain()
    {
        var (events, diag) = Run.Body(Opcodes.Damage, AreaHit + AreaHit + PlayerHit);

        Assert.Equal(new long?[] { 405, 405, 381 }, events.OfType<DamageEvent>().Select(e => e.Amount));
        Assert.Equal(3, diag.DamageRecords);
        Assert.Equal(0, diag.DamageTrailingBytes);
    }

    [Fact]
    public void Trailer_lookahead_does_not_count_the_next_placeholder_twice()
    {
        var (events, diag) = Run.Body(Opcodes.Damage, AreaHit + Placeholder);

        Assert.Equal(405, Assert.IsType<DamageEvent>(Assert.Single(events)).Amount);
        Assert.Equal(1, diag.DamagePlaceholders);
        Assert.Equal(0, diag.DamageTrailingBytes);
    }

    [Fact]
    public void Different_trailer_sequence_with_flag_4_consumes_its_varint_tail_before_chaining()
    {
        byte[] first = W.DamageBody(100, 200, 500);
        first[2] = 0x04; // flag 4 owns one varint after the trailer.
        first[^2] = 9; // Sequence differs from hit_index 1.
        var wire = W.Concat(first, W.VarInt(300), W.H(PlayerHit));

        var (events, diag) = Run.Body(Opcodes.Damage, Convert.ToHexString(wire));

        Assert.Equal(new long?[] { 500, 381 }, events.OfType<DamageEvent>().Select(e => e.Amount));
        Assert.Equal(0, diag.DamageTrailingBytes);
    }

    [Fact]
    public void Area_trailer_does_not_turn_invalid_chained_bytes_into_damage()
    {
        var invalid = PlayerHit.Replace("433BAF41", "11111111", StringComparison.Ordinal);
        var (events, diag) = Run.Body(Opcodes.Damage, AreaHit + invalid);

        Assert.Equal(405, Assert.IsType<DamageEvent>(Assert.Single(events)).Amount);
        Assert.Equal(1, diag.DamageTrailingBytes);
        Assert.Equal(0, diag.DecodeErrors);
    }

    [Theory]
    [InlineData(64, 0)]
    [InlineData(65, 1)]
    public void Area_record_chaining_keeps_the_existing_record_limit(int count, int trailing)
    {
        var (events, diag) = Run.Body(Opcodes.Damage, string.Concat(Enumerable.Repeat(AreaHit, count)));

        Assert.Equal(Math.Min(count, 64), events.Count);
        Assert.All(events, e => Assert.Equal(405, Assert.IsType<DamageEvent>(e).Amount));
        Assert.Equal(trailing, diag.DamageTrailingBytes);
        Assert.Equal(0, diag.DecodeErrors);
    }

    [Fact]
    public void Maximum_valid_entity_id_is_accepted()
    {
        var (events, diag) = Run.Body(Opcodes.Damage,
            Convert.ToHexString(W.DamageBody(PacketDecoder.MaxEntityId, PacketDecoder.MaxEntityId, 500)));

        Assert.Equal(PacketDecoder.MaxEntityId, Assert.IsType<DamageEvent>(Assert.Single(events)).Actor);
        Assert.Equal(0, diag.DecodeErrors);
    }

    [Theory]
    [InlineData(0u, 200u)]
    [InlineData(100u, 0u)]
    [InlineData(10_000_000u, 200u)]
    [InlineData(100u, 10_000_000u)]
    [InlineData(uint.MaxValue, 200u)]
    [InlineData(100u, uint.MaxValue)]
    public void Invalid_entity_ids_are_rejected_in_the_first_record(uint target, uint actor)
    {
        var (events, diag) = Run.Body(Opcodes.Damage, Convert.ToHexString(W.DamageBody(target, actor, 500)));

        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
        Assert.Equal(0, diag.DamageRecords);
    }
}
