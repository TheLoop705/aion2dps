using K4os.Compression.LZ4;

namespace Aion2Dps.Protocol.Tests;

public class Lz4BlockTests
{
    [Fact]
    public void Literal_only_block_decodes()
    {
        var dst = new byte[3];
        Assert.True(Lz4Block.TryDecompress(new byte[] { 0x30, 1, 2, 3 }, dst, out int n));
        Assert.Equal(3, n);
        Assert.Equal(new byte[] { 1, 2, 3 }, dst);
    }

    [Fact]
    public void Back_reference_beyond_produced_output_is_rejected()
    {
        // 1 literal, then a match at offset 5 when only 1 byte exists.
        Assert.False(Lz4Block.TryDecompress(new byte[] { 0x11, 9, 0x05, 0x00 }, new byte[10], out _));
    }

    [Fact]
    public void Zero_offset_is_rejected()
    {
        Assert.False(Lz4Block.TryDecompress(new byte[] { 0x10, 9, 0x00, 0x00, 0x00 }, new byte[16], out _));
    }

    [Fact]
    public void Overlapping_match_repeats_the_pattern()
    {
        // literal "ab", then match offset 2 length 4+6=10 -> "ab" * 6, then final literal "Z".
        var src = new byte[] { 0x26, (byte)'a', (byte)'b', 0x02, 0x00, 0x10, (byte)'Z' };
        var dst = new byte[13];
        Assert.True(Lz4Block.TryDecompress(src, dst, out int n));
        Assert.Equal(13, n);
        Assert.Equal("ababababababZ", System.Text.Encoding.ASCII.GetString(dst));
    }

    [Fact]
    public void Output_size_must_match_exactly()
    {
        var data = Enumerable.Range(0, 500).Select(i => (byte)(i % 7)).ToArray();
        var block = W.Lz4(data);
        Assert.True(Lz4Block.TryDecompress(block, new byte[500], out _));
        Assert.False(Lz4Block.TryDecompress(block, new byte[501], out _));   // too short output produced
        Assert.False(Lz4Block.TryDecompress(block, new byte[499], out _));   // would overrun
        Assert.True(Lz4Block.TryDecompress(block, new byte[600], out int n, exact: false));
        Assert.Equal(500, n);
    }

    [Fact]
    public void Truncated_blocks_are_rejected_and_never_throw()
    {
        var data = Enumerable.Range(0, 2000).Select(i => (byte)((i * 31) % 13)).ToArray();
        var block = W.Lz4(data);
        var dst = new byte[data.Length];
        for (int cut = 0; cut < block.Length; cut++)
            Assert.False(Lz4Block.TryDecompress(block.AsSpan(0, cut), dst, out _));
        Assert.True(Lz4Block.TryDecompress(block, dst, out _));
        Assert.Equal(data, dst);
    }

    [Fact]
    public void Random_garbage_never_throws()
    {
        var rng = new Random(1234);
        var dst = new byte[4096];
        for (int i = 0; i < 20_000; i++)
        {
            var src = new byte[rng.Next(0, 64)];
            rng.NextBytes(src);
            _ = Lz4Block.TryDecompress(src, dst.AsSpan(0, rng.Next(0, dst.Length)), out _);
            _ = Lz4Block.TryDecompress(src, dst, out _, exact: false);
        }
    }

    public static IEnumerable<object[]> OracleCases()
    {
        int[] sizes = [1, 2, 5, 12, 13, 64, 255, 256, 1000, 4096, 65_535, 70_000, 300_000];
        int[] alphabets = [1, 2, 4, 16, 256];
        foreach (int size in sizes)
        foreach (int alpha in alphabets)
            yield return [size, alpha];
    }

    /// <summary>Cross-check against the K4os LZ4 encoder on random data of varied compressibility (fast and HC).</summary>
    [Theory]
    [MemberData(nameof(OracleCases))]
    public void Matches_k4os_oracle(int size, int alphabet)
    {
        var rng = new Random(size * 31 + alphabet);
        var data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            // Mix runs (long overlapping matches) with random symbols.
            data[i] = rng.Next(8) == 0 && i > 0 ? data[i - 1] : (byte)rng.Next(alphabet);
        }

        foreach (var level in new[] { LZ4Level.L00_FAST, LZ4Level.L09_HC, LZ4Level.L12_MAX })
        {
            var block = W.Lz4(data, level);
            var dst = new byte[size];
            Assert.True(Lz4Block.TryDecompress(block, dst, out int n), $"level {level}");
            Assert.Equal(size, n);
            Assert.Equal(data, dst);

            // And the oracle agrees with itself on the same block.
            var oracle = new byte[size];
            Assert.Equal(size, LZ4Codec.Decode(block, 0, block.Length, oracle, 0, oracle.Length));
            Assert.Equal(oracle, dst);
        }
    }
}
