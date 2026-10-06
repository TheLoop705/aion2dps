using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

/// <summary>Regression tests from the correctness review (real Global frames, entity ids only).</summary>
public class ReviewRegressionTests
{
    /// <summary>
    /// Layout 6 with mods 0x20: the field between mods and dir is a varint. With a 2-byte value (0xAB01 = 171) a
    /// fixed 1-byte skip misaligned the record (effect 3158925056, "amount" 10,000 = the power scalar).
    /// </summary>
    [Fact]
    public void Mods_0x20_value_is_a_varint_even_when_two_bytes_long()
    {
        var e = Run.Single<DamageEvent>("0438876E06009488029D7B1B00010220AB01005F49BC0A01000000904EDA060100");
        Assert.Equal(HitMods.Smite, e.Mods);
        Assert.Equal(180111711u, e.EffectId);
        Assert.True(e.EffectValidated);
        Assert.Equal(10_000u, e.PowerScalar);
        Assert.Equal(858, e.Amount);
    }

    [Fact]
    public void Mods_0x20_value_of_one_byte_still_decodes()
    {
        var e = Run.Single<DamageEvent>("0438876E0600DCAB028EB912001F02204F02837B500702000000904E8C030400");
        Assert.True(e.EffectValidated);
        Assert.Equal(396, e.Amount);
    }
}
