using System.Buffers.Binary;

namespace Aion2Dps.Simulator;

/// <summary>
/// A small LZ4 <b>raw block</b> compressor (no frame header, no checksum), the format the game uses inside
/// <c>FF FF</c> bundles (PROTOCOL.md §5). Greedy parsing with a bounded hash chain. Honours the block rules
/// every conforming decoder relies on: matches are at least 4 bytes, offsets are 1..65535, the last match starts
/// at least 12 bytes before the end, and the last 5 bytes are always literals.
/// </summary>
public static class Lz4BlockCompressor
{
    private const int MinMatch = 4;
    private const int LastLiterals = 5;
    private const int MatchFindLimit = 12;
    private const int MaxOffset = 65535;
    private const int HashBits = 14;

    /// <summary>Compresses <paramref name="source"/> into one LZ4 block.</summary>
    /// <param name="literalOnly">Emit a single literal-only sequence (valid, but no compression).</param>
    /// <param name="maxChainDepth">How many earlier positions with the same hash are tried per position.</param>
    public static byte[] Compress(ReadOnlySpan<byte> source, bool literalOnly = false, int maxChainDepth = 16)
    {
        var output = new WireWriter(source.Length + source.Length / 255 + 16);
        int n = source.Length;

        if (literalOnly || n < MatchFindLimit + 1)
        {
            WriteSequence(output, source, 0, n, 0, 0);
            return output.ToArray();
        }

        var head = new int[1 << HashBits];
        Array.Fill(head, -1);
        var chain = new int[n];
        int matchStartLimit = n - MatchFindLimit; // a match may start at <= this index
        int matchEndLimit = n - LastLiterals;     // a match must end at <= this index (exclusive end)

        int anchor = 0;
        int i = 0;
        while (i <= matchStartLimit)
        {
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(source[i..]);
            int h = Hash(seq);

            int bestLength = 0;
            int bestPosition = -1;
            int candidate = head[h];
            int depth = 0;
            while (candidate >= 0 && i - candidate <= MaxOffset && depth++ < maxChainDepth)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(source[candidate..]) == seq)
                {
                    int length = MinMatch;
                    while (i + length < matchEndLimit && source[candidate + length] == source[i + length]) length++;
                    if (length > bestLength)
                    {
                        bestLength = length;
                        bestPosition = candidate;
                    }
                }

                candidate = chain[candidate];
            }

            chain[i] = head[h];
            head[h] = i;

            if (bestLength >= MinMatch && i + bestLength <= matchEndLimit)
            {
                WriteSequence(output, source, anchor, i - anchor, i - bestPosition, bestLength);
                int end = i + bestLength;
                // Index the positions covered by the match so later data can refer back into it.
                for (int k = i + 1; k < end && k <= matchStartLimit; k++)
                {
                    int hk = Hash(BinaryPrimitives.ReadUInt32LittleEndian(source[k..]));
                    chain[k] = head[hk];
                    head[hk] = k;
                }

                i = end;
                anchor = i;
            }
            else
            {
                i++;
            }
        }

        WriteSequence(output, source, anchor, n - anchor, 0, 0);
        return output.ToArray();
    }

    private static int Hash(uint sequence) => (int)((sequence * 2654435761u) >> (32 - HashBits));

    /// <summary>Writes one sequence. <paramref name="matchLength"/> 0 = final literal-only sequence.</summary>
    private static void WriteSequence(WireWriter output, ReadOnlySpan<byte> source, int literalStart, int literalLength, int offset, int matchLength)
    {
        int matchCode = matchLength == 0 ? 0 : matchLength - MinMatch;
        byte token = (byte)((Math.Min(literalLength, 15) << 4) | Math.Min(matchCode, 15));
        output.U8(token);
        if (literalLength >= 15) WriteLengthExtension(output, literalLength - 15);
        output.Bytes(source.Slice(literalStart, literalLength));
        if (matchLength == 0) return;
        output.U16((ushort)offset);
        if (matchCode >= 15) WriteLengthExtension(output, matchCode - 15);
    }

    private static void WriteLengthExtension(WireWriter output, int remaining)
    {
        while (remaining >= 255)
        {
            output.U8(255);
            remaining -= 255;
        }

        output.U8((byte)remaining);
    }

    /// <summary>
    /// Reference LZ4 block decoder (PROTOCOL.md §5 pseudo-code). Used by the simulator's own self-checks; the
    /// production decoder lives in the Protocol module.
    /// </summary>
    public static bool TryDecompress(ReadOnlySpan<byte> block, int rawSize, out byte[] output)
    {
        output = new byte[rawSize];
        int i = 0, o = 0;
        try
        {
            while (i < block.Length)
            {
                byte token = block[i++];
                int literal = token >> 4;
                if (literal == 15)
                {
                    byte b;
                    do { b = block[i++]; literal += b; } while (b == 255);
                }

                if (o + literal > rawSize || i + literal > block.Length) return false;
                block.Slice(i, literal).CopyTo(output.AsSpan(o));
                i += literal;
                o += literal;
                if (i == block.Length) break;

                int offset = block[i] | (block[i + 1] << 8);
                i += 2;
                if (offset == 0 || offset > o) return false;
                int match = token & 15;
                if (match == 15)
                {
                    byte b;
                    do { b = block[i++]; match += b; } while (b == 255);
                }

                match += MinMatch;
                if (o + match > rawSize) return false;
                for (int k = 0; k < match; k++, o++) output[o] = output[o - offset];
            }
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }

        return o == rawSize;
    }
}
