using System.Buffers.Binary;
using Aion2Dps.Contracts;
using K4os.Compression.LZ4;
using static Aion2Dps.Simulator.Tests.Hex;

namespace Aion2Dps.Simulator.Tests;

public class FramingTests
{
    [Theory]
    [InlineData(30001u, "B1EA01")]
    [InlineData(1395u, "F30A")]
    [InlineData(14957u, "ED74")]
    [InlineData(3_600_000u, "80DDDB01")]
    [InlineData(0u, "00")]
    [InlineData(127u, "7F")]
    [InlineData(128u, "8001")]
    [InlineData(uint.MaxValue, "FFFFFFFF0F")]
    public void Varints_match_spec_examples(uint value, string hex)
    {
        Assert.Equal(hex, Of(Varint.Encode(value)));
        Assert.Equal(hex.Length / 2, Varint.Size(value));
    }

    [Fact]
    public void Heartbeat_frame_is_eleven_bytes_with_L_14()
    {
        var frame = PacketEncoders.EncodeFrame(new HeartbeatEvent { ServerUnixMs = 0x01A0F4207185 });
        Assert.Equal("0E0036857120F4A0010000", Of(frame));
        Assert.Equal(11, frame.Length);
    }

    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(10, 1, 11)]
    [InlineData(123, 1, 124)]   // L = 127, last 1-byte length
    [InlineData(124, 2, 126)]   // L = 128 = 80 01
    [InlineData(300, 2, 302)]   // §4.1 worked example: L = 304 = B0 02, total 302
    [InlineData(16379, 2, 16381)]
    [InlineData(16380, 3, 16383)] // L = 16384 needs 3 bytes
    public void Frame_length_math(int payloadLength, int width, int total)
    {
        Assert.Equal(width, Framing.PrefixWidth(payloadLength));
        Assert.Equal(total, Framing.FrameSize(payloadLength));
        var frame = Framing.Frame(new byte[payloadLength]);
        Assert.Equal(total, frame.Length);

        // Decoder rule (§4.3): total = L + width − 4.
        uint l = 0;
        int shift = 0, w = 0;
        while (true)
        {
            byte b = frame[w++];
            l |= (uint)(b & 0x7F) << shift;
            if (b < 0x80) break;
            shift += 7;
        }

        Assert.Equal(width, w);
        Assert.Equal(total, (int)l + w - 4);
    }

    [Fact]
    public void Three_hundred_byte_payload_uses_B0_02()
    {
        var frame = Framing.Frame(new byte[300]);
        Assert.Equal(0xB0, frame[0]);
        Assert.Equal(0x02, frame[1]);
    }

    [Fact]
    public void Wire_writer_primitives()
    {
        var w = new WireWriter(4);
        w.U8(1).U16(0x0302).U32(0x07060504).U64(0x0F0E0D0C0B0A0908).Opcode(0x0438).String("Zoë").Varint(300UL).F32(1.0f).I64(-1);
        Assert.Equal("01020304050607" + "08090A0B0C0D0E0F" + "0438" + "045A6FC3AB" + "AC02" + "0000803F" + "FFFFFFFFFFFFFFFF", Of(w.ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Varint((ulong)uint.MaxValue + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Varint(-1L));
    }
}

public class Lz4Tests
{
    private const string RealBundleHex = "ffff08020000ff13200438b81c0400ec1b5e7d14011a02c3f8006c01000000c24e87080100200438f81b1d000150c40701001e1d0015003a0015c43a000c1b002700c51b004f200438ea53000212cd53001fea530007071b000c53001fe153000212b353001fe1530007071b00095300f100332a38f81b011335ade5cc0ae02e0001006065a020f4a0019e00f0090c5e7d1401004f576fc60aa7724600c021440d0e92f81b012e0090160538f81b09ec1b354a0120c00b2a0080332a38ea1b0113391f000f4d001415ea4d0010ea4d0010394d00118d77004f332a38e19a001c15e14d0010e14d00019a0011854d00b00e0036857120f4a0010000";

    private static byte[] OracleDecode(byte[] block, int rawSize)
    {
        var output = new byte[rawSize];
        int n = LZ4Codec.Decode(block, 0, block.Length, output, 0, rawSize);
        Assert.Equal(rawSize, n);
        return output;
    }

    private static void RoundTrip(byte[] data, bool literalOnly = false)
    {
        var block = Lz4BlockCompressor.Compress(data, literalOnly);
        Assert.Equal(data, OracleDecode(block, data.Length));
        Assert.True(Lz4BlockCompressor.TryDecompress(block, data.Length, out var mine));
        Assert.Equal(data, mine);
    }

    [Fact]
    public void Random_data_round_trips_through_the_oracle()
    {
        var rng = new Random(11);
        foreach (int size in new[] { 1, 4, 5, 11, 12, 13, 14, 15, 16, 17, 31, 64, 255, 256, 270, 1000, 4096, 70_000 })
        {
            var data = new byte[size];
            rng.NextBytes(data);
            RoundTrip(data);
            RoundTrip(data, literalOnly: true);
        }
    }

    [Fact]
    public void Repetitive_data_round_trips_and_compresses()
    {
        var rng = new Random(3);
        var zeros = new byte[10_000];
        RoundTrip(zeros);
        Assert.True(Lz4BlockCompressor.Compress(zeros).Length < 100);

        var pattern = Enumerable.Range(0, 20_000).Select(i => (byte)(i % 7)).ToArray();
        RoundTrip(pattern);

        // Game-like: repeated frames with small differences, long literal runs and long matches (length extensions).
        var mixed = new List<byte>();
        for (int i = 0; i < 400; i++)
        {
            mixed.AddRange(Parse("1F0438F81B0000EC1B5E7D14011A02C4F8006C01000000C24E0100"));
            if (i % 9 == 0) { var noise = new byte[rng.Next(1, 300)]; rng.NextBytes(noise); mixed.AddRange(noise); }
        }

        RoundTrip(mixed.ToArray());
        Assert.True(Lz4BlockCompressor.Compress(mixed.ToArray()).Length < mixed.Count / 2);

        // Offsets near the 64 KiB window limit.
        var far = new byte[140_000];
        rng.NextBytes(far);
        Array.Copy(far, 0, far, 65_530, 2000);
        Array.Copy(far, 10, far, 131_000, 3000);
        RoundTrip(far);
    }

    [Fact]
    public void Literal_only_block_matches_the_spec_example()
    {
        Assert.Equal("30010203", Of(Lz4BlockCompressor.Compress([1, 2, 3], literalOnly: true)));
        Assert.Equal("00", Of(Lz4BlockCompressor.Compress([])));
        Assert.True(Lz4BlockCompressor.TryDecompress([0x00], 0, out var empty));
        Assert.Empty(empty);
        Assert.True(Lz4BlockCompressor.TryDecompress([0x30, 1, 2, 3], 3, out var lit));
        Assert.Equal(new byte[] { 1, 2, 3 }, lit);
        Assert.False(Lz4BlockCompressor.TryDecompress([0x11, 9, 0x05, 0x00], 10, out _)); // offset beyond output
    }

    [Fact]
    public void Real_bundle_decodes_and_rebuilds()
    {
        var payload = Parse(RealBundleHex);
        int raw = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2));
        Assert.Equal(520, raw);
        var inner = OracleDecode(payload[6..], raw);
        Assert.True(Lz4BlockCompressor.TryDecompress(payload.AsSpan(6), raw, out var mine));
        Assert.Equal(inner, mine);

        var frames = ReferenceFramer.Parse(inner);
        Assert.Equal(20, frames.Count);
        Assert.Equal(10, frames.Count(f => f.Payload[0] == 0x04 && f.Payload[1] == 0x38));

        // Rebuild the bundle with our compressor: same inner stream.
        var rebuilt = BundleBuilder.BuildPayload(inner);
        Assert.Equal(0xFF, rebuilt[0]);
        Assert.Equal(0xFF, rebuilt[1]);
        Assert.Equal(520u, BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(2)));
        Assert.Equal(inner, OracleDecode(rebuilt[6..], 520));
        Assert.True(rebuilt.Length < 520);

        var frame = BundleBuilder.BuildFrame(inner);
        var parsed = ReferenceFramer.Parse(frame);
        Assert.Equal(frames.Select(f => Of(f.Payload)), parsed.Select(f => Of(f.Payload)));
        Assert.All(parsed, f => Assert.Equal(1, f.Depth));
    }
}
