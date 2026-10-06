namespace Aion2Dps.Protocol.Tests;

public class VarIntTests
{
    // §6.1 examples.
    [Theory]
    [InlineData("B1EA01", 30001u, 3)]
    [InlineData("F30A", 1395u, 2)]
    [InlineData("ED74", 14957u, 2)]
    [InlineData("80DDDB01", 3_600_000u, 4)]
    [InlineData("00", 0u, 1)]
    [InlineData("7F", 127u, 1)]
    [InlineData("8001", 128u, 2)]
    [InlineData("FFFFFFFF0F", uint.MaxValue, 5)]
    public void Decodes_spec_examples(string hex, uint expected, int width)
    {
        Assert.Equal(VarIntStatus.Ok, VarInt.TryRead(W.H(hex), out uint v, out int w));
        Assert.Equal(expected, v);
        Assert.Equal(width, w);
        Assert.Equal(width, VarInt.GetWidth(expected));
        Assert.Equal(W.H(hex), W.VarInt(expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("8080")]
    [InlineData("FFFFFFFF")]
    public void Truncated_input_is_reported_as_truncated(string hex) =>
        Assert.Equal(VarIntStatus.Truncated, VarInt.TryRead(W.H(hex), out _, out _));

    [Theory]
    [InlineData("8080808080")]   // 5th byte has the continuation bit
    [InlineData("FFFFFFFF80")]
    [InlineData("FFFFFFFF10")]   // overflows u32
    [InlineData("808080808001")]
    public void Five_byte_continuation_or_overflow_is_invalid(string hex) =>
        Assert.Equal(VarIntStatus.Invalid, VarInt.TryRead(W.H(hex), out _, out _));

    [Fact]
    public void Roundtrips_many_values()
    {
        var rng = new Random(7);
        var buf = new byte[5];
        for (int i = 0; i < 10_000; i++)
        {
            uint v = (uint)rng.NextInt64(0, uint.MaxValue + 1L) >> rng.Next(0, 32);
            int n = VarInt.Write(v, buf);
            Assert.Equal(VarIntStatus.Ok, VarInt.TryRead(buf.AsSpan(0, n), out uint back, out int w));
            Assert.Equal(v, back);
            Assert.Equal(n, w);
        }
    }
}

public class SpanReaderTests
{
    [Fact]
    public void Reads_little_endian_fixed_width_values()
    {
        var r = new SpanReader(W.H("01 0201 04030201 0807060504030201 FFFFFFFFFFFFFFFF 0000803F"));
        Assert.True(r.TryReadU8(out byte a)); Assert.Equal(1, a);
        Assert.True(r.TryReadU16(out ushort b)); Assert.Equal(0x0102, b);
        Assert.True(r.TryReadU32(out uint c)); Assert.Equal(0x01020304u, c);
        Assert.True(r.TryReadU64(out ulong d)); Assert.Equal(0x0102030405060708UL, d);
        Assert.True(r.TryReadI64(out long e)); Assert.Equal(-1L, e);
        Assert.True(r.TryReadF32(out float f)); Assert.Equal(1.0f, f);
        Assert.True(r.IsAtEnd);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void Truncation_returns_false_and_keeps_position()
    {
        var r = new SpanReader(W.H("010203"));
        Assert.True(r.TryReadU8(out _));
        int pos = r.Position;
        Assert.False(r.TryReadU32(out _));
        Assert.False(r.TryReadU64(out _));
        Assert.False(r.TryReadI64(out _));
        Assert.False(r.TryReadF32(out _));
        Assert.Equal(pos, r.Position);
        Assert.True(r.TryReadU16(out ushort v));
        Assert.Equal(0x0302, v);
        Assert.False(r.TryReadU8(out _));
        Assert.False(r.TryReadU16(out _));
        Assert.False(r.TryReadVarUInt(out _));
        Assert.False(r.TryReadString(out _));
    }

    [Fact]
    public void Reads_varints_and_rejects_bad_ones_without_moving()
    {
        var r = new SpanReader(W.H("B1EA01 F30A 8080808080"));
        Assert.True(r.TryReadVarUInt(out uint a)); Assert.Equal(30001u, a);
        Assert.True(r.TryReadVarUInt(out uint b)); Assert.Equal(1395u, b);
        int pos = r.Position;
        Assert.False(r.TryReadVarUInt(out _));
        Assert.Equal(pos, r.Position);
    }

    [Fact]
    public void Reads_length_prefixed_utf8_strings()
    {
        var r = new SpanReader(W.H("06 4E6169636861 0C 436F6E76C3A87267656E6365 00"));
        Assert.True(r.TryReadString(out string a)); Assert.Equal("Naicha", a);
        Assert.True(r.TryReadString(out string b)); Assert.Equal("Convèrgence", b);
        Assert.True(r.TryReadString(out string c)); Assert.Equal("", c);
        Assert.True(r.IsAtEnd);
    }

    [Theory]
    [InlineData("05 41424344")]   // length beyond the data
    [InlineData("02 C328")]       // invalid UTF-8
    [InlineData("01 C3")]         // cut-off UTF-8
    public void Bad_strings_fail_without_moving(string hex)
    {
        var r = new SpanReader(W.H(hex));
        Assert.False(r.TryReadString(out _));
        Assert.Equal(0, r.Position);
    }

    [Fact]
    public void Skip_bytes_and_position_clamp()
    {
        var r = new SpanReader(W.H("0102030405"));
        Assert.True(r.TrySkip(2));
        Assert.True(r.TryPeekU8(out byte p)); Assert.Equal(3, p);
        Assert.True(r.TryReadBytes(2, out var bytes)); Assert.Equal(new byte[] { 3, 4 }, bytes.ToArray());
        Assert.False(r.TrySkip(2));
        Assert.False(r.TrySkip(-1));
        r.Position = 100;
        Assert.Equal(5, r.Position);
        r.Position = -3;
        Assert.Equal(0, r.Position);
    }
}
