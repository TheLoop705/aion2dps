using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace Aion2Dps.Simulator.Tests;

internal static class Hex
{
    public static byte[] Parse(string hex) => Convert.FromHexString(hex.Replace(" ", "").Replace("\n", ""));

    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    public static float F(string hex4) => BinaryPrimitives.ReadSingleLittleEndian(Parse(hex4));
}

/// <summary>
/// Independent reference framer for the tests (PROTOCOL.md §4/§5), using K4os as the LZ4 oracle. Returns every
/// payload (opcode + body) in wire order with its bundle depth.
/// </summary>
internal static class ReferenceFramer
{
    public static List<(byte[] Payload, int Depth)> Parse(ReadOnlySpan<byte> stream, int depth = 0)
    {
        var result = new List<(byte[], int)>();
        int i = 0;
        while (i < stream.Length)
        {
            if (stream[i] == 0) { i++; continue; }
            uint l = 0;
            int shift = 0, w = 0;
            while (true)
            {
                byte b = stream[i + w];
                l |= (uint)(b & 0x7F) << shift;
                w++;
                if (b < 0x80) break;
                shift += 7;
                if (w == 5) throw new InvalidDataException("varint too long");
            }

            int total = (int)l + w - 4;
            if (total <= w || i + total > stream.Length) throw new InvalidDataException($"bad frame at {i}: L={l}");
            var payload = stream.Slice(i + w, total - w);
            if (payload.Length >= 6 && payload[0] == 0xFF && payload[1] == 0xFF)
            {
                int raw = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]);
                var block = payload[6..].ToArray();
                var output = new byte[raw];
                int n = LZ4Codec.Decode(block, 0, block.Length, output, 0, raw);
                if (n != raw) throw new InvalidDataException($"LZ4 decode gave {n}, expected {raw}");
                result.AddRange(Parse(output, depth + 1));
            }
            else
            {
                result.Add((payload.ToArray(), depth));
            }

            i += total;
        }

        return result;
    }
}

/// <summary>Owner / anchor lookup inside a <c>41 36</c> body exactly as PROTOCOL.md §8.5 describes it.</summary>
internal static class SpawnProbe
{
    private static readonly byte[] Ff8 = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[] Anchor = [0x80, 0x75, 0xD5, 0x2A, 0xBB, 0x03, 0x00, 0x00];

    public static uint? Owner(ReadOnlySpan<byte> body)
    {
        int ff = body.IndexOf(Ff8);
        if (ff < 0) return null;
        var rest = body[(ff + 8)..];
        int m = rest.IndexOf((ReadOnlySpan<byte>)[0x07, 0x02, 0x06]);
        if (m < 0 || m + 7 > rest.Length) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(rest[(m + 3)..]);
    }

    public static uint? AnchorId(ReadOnlySpan<byte> body)
    {
        int a = body.IndexOf(Anchor);
        if (a < 0) return null;
        uint v = 0;
        int shift = 0;
        for (int i = a + 8; i < body.Length; i++)
        {
            v |= (uint)(body[i] & 0x7F) << shift;
            if (body[i] < 0x80) return v;
            shift += 7;
        }

        return null;
    }
}
