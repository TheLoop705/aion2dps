using Aion2Dps.Contracts;
using static Aion2Dps.Simulator.Tests.Hex;

namespace Aion2Dps.Simulator.Tests;

/// <summary>
/// The <c>2A 38</c> encoder reproduces real Global frames (2026-10-06 expedition capture) byte for byte, using the
/// layout the decoder reads (PROTOCOL.md §8.13): count, flags (0x02 = source skill present), stack, buff, duration u64
/// (all FF = permanent), expiry u64, caster, level, [source skill], tail u8, 3 × f32. The same frames are decoded in
/// Aion2Dps.Protocol.Tests (RealVectors/real-frames.json: buff-2A38-13, buff-2A38-11, buff-2A38-permanent).
/// </summary>
public class BuffEncoderRealFrameTests
{
    [Fact]
    public void Source_skill_buff_matches_the_real_frame()
    {
        var e = new BuffAppliedEvent
        {
            Target = 13210, Stack = 43, BuffId = 22101031, DurationMs = 300000, ExpiryUnixMs = 1791316030299, Caster = 13210, SourceSkill = 2210103,
        };
        var o = new BuffEncodeOptions { Byte2 = 0x13, Level = 1, Tail = 0, X = F("e4764646"), Y = F("4e60b0c6"), Z = F("ac94b045") };
        Assert.Equal("9A6701132B273C5101E0930400000000005B73C112A10100009A670137B9210000E47646464E60B0C6AC94B045", Of(PacketEncoders.EncodeBuffApplied(e, o)));
    }

    [Fact]
    public void Buff_without_source_skill_omits_the_skill_field()
    {
        // Flags 0x11: the dodge window buff 200 (300 ms) has no source skill.
        var e = new BuffAppliedEvent
        {
            Target = 5951, Stack = 2601, BuffId = 200, DurationMs = 300, ExpiryUnixMs = 1791315685248, Caster = 5951,
        };
        var o = new BuffEncodeOptions { Byte2 = 0x11, Level = 1, Tail = 0, X = F("61a51f48"), Y = F("1f0d7947"), Z = F("0028e745") };
        Assert.Equal("BF2E0111A914C80000002C01000000000000802FBC12A1010000BF2E010061A51F481F0D79470028E745", Of(PacketEncoders.EncodeBuffApplied(e, o)));
    }

    [Fact]
    public void Permanent_buff_writes_an_all_ff_duration()
    {
        var e = new BuffAppliedEvent
        {
            Target = 11644, Stack = 1, BuffId = 12011, DurationMs = uint.MaxValue, ExpiryUnixMs = 4_102_412_400_000, Caster = 11644,
        };
        var o = new BuffEncodeOptions { Byte2 = 0x11, Level = 1, Tail = 0, X = F("ae592248"), Y = F("8a987947"), Z = F("0020e845") };
        Assert.Equal("FC5A011101EB2E0000FFFFFFFFFFFFFFFF8075D52ABB030000FC5A0100AE5922488A9879470020E845", Of(PacketEncoders.EncodeBuffApplied(e, o)));
    }
}
