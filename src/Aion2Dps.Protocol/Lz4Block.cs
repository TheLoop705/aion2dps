namespace Aion2Dps.Protocol;

/// <summary>
/// Safe decoder for raw LZ4 blocks (no frame header, no checksum), as used by the AION 2 <c>FF FF</c> bundles
/// (PROTOCOL.md §5). Every read and write is bounds-checked; malformed input returns false, never throws.
/// </summary>
public static class Lz4Block
{
    private const int MinMatch = 4;

    /// <summary>
    /// Decompresses <paramref name="src"/> into <paramref name="dst"/>.
    /// </summary>
    /// <param name="src">The compressed block. It must be consumed completely.</param>
    /// <param name="dst">Output buffer. With <paramref name="exact"/> (the default) the block must produce exactly
    /// <c>dst.Length</c> bytes (the bundle's rawSize).</param>
    /// <param name="written">Bytes produced (also set on failure, for diagnostics).</param>
    /// <param name="exact">Require the output to fill <paramref name="dst"/> exactly.</param>
    /// <returns>True when the block is well-formed (offsets non-zero and within the produced output, no overrun).</returns>
    public static bool TryDecompress(ReadOnlySpan<byte> src, Span<byte> dst, out int written, bool exact = true)
    {
        int si = 0;
        int di = 0;
        int srcLen = src.Length;
        int dstLen = dst.Length;
        written = 0;

        if (srcLen == 0) return !exact || dstLen == 0;

        while (true)
        {
            if (si >= srcLen) { written = di; return false; } // a sequence needs at least a token
            int token = src[si++];

            // Literal length.
            int lit = token >> 4;
            if (lit == 15)
            {
                int b;
                do
                {
                    if (si >= srcLen) { written = di; return false; }
                    b = src[si++];
                    lit += b;
                    if (lit > dstLen) { written = di; return false; } // also guards int overflow
                } while (b == 255);
            }

            if (lit > srcLen - si || lit > dstLen - di) { written = di; return false; }
            if (lit > 0)
            {
                src.Slice(si, lit).CopyTo(dst.Slice(di, lit));
                si += lit;
                di += lit;
            }

            // The last sequence holds literals only.
            if (si == srcLen) break;

            // Match.
            if (srcLen - si < 2) { written = di; return false; }
            int offset = src[si] | (src[si + 1] << 8);
            si += 2;
            if (offset == 0 || offset > di) { written = di; return false; }

            int matchLen = token & 0x0F;
            if (matchLen == 15)
            {
                int b;
                do
                {
                    if (si >= srcLen) { written = di; return false; }
                    b = src[si++];
                    matchLen += b;
                    if (matchLen > dstLen) { written = di; return false; }
                } while (b == 255);
            }

            matchLen += MinMatch;
            if (matchLen > dstLen - di) { written = di; return false; }

            int from = di - offset;
            if (offset >= matchLen)
            {
                dst.Slice(from, matchLen).CopyTo(dst.Slice(di, matchLen));
                di += matchLen;
            }
            else
            {
                // Overlapping copy: repeat the last `offset` bytes. Copy in growing non-overlapping chunks.
                int remaining = matchLen;
                while (remaining > 0)
                {
                    int chunk = Math.Min(di - from, remaining);
                    dst.Slice(from, chunk).CopyTo(dst.Slice(di, chunk));
                    di += chunk;
                    remaining -= chunk;
                }
            }
        }

        written = di;
        return !exact || di == dstLen;
    }
}
