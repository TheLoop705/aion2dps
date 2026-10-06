namespace Aion2Dps.Simulator;

/// <summary>
/// Frame construction, the exact inverse of the framer in PROTOCOL.md §4:
/// <c>frame := varint(L) payload</c> with <c>L = payload.Length + 4</c>, so that
/// <c>frame_total = L + varint_width(L) - 4</c>.
/// </summary>
public static class Framing
{
    /// <summary>The varint value written in front of a payload of <paramref name="payloadLength"/> bytes.</summary>
    public static uint LengthField(int payloadLength) => checked((uint)payloadLength + 4);

    /// <summary>Total frame size (prefix + payload) for a payload of <paramref name="payloadLength"/> bytes.</summary>
    public static int FrameSize(int payloadLength) => Varint.Size(LengthField(payloadLength)) + payloadLength;

    /// <summary>Width of the length prefix for a payload of <paramref name="payloadLength"/> bytes (1 up to 123 bytes, then 2).</summary>
    public static int PrefixWidth(int payloadLength) => Varint.Size(LengthField(payloadLength));

    /// <summary>Frames a payload (opcode + body, or a bundle payload).</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        uint l = LengthField(payload.Length);
        var frame = new byte[Varint.Size(l) + payload.Length];
        int w = Varint.Write(frame, l);
        payload.CopyTo(frame.AsSpan(w));
        return frame;
    }

    /// <summary>Frames <c>opcode + body</c>.</summary>
    public static byte[] Frame(ushort opcode, ReadOnlySpan<byte> body)
    {
        var payload = new byte[body.Length + 2];
        payload[0] = (byte)(opcode >> 8);
        payload[1] = (byte)opcode;
        body.CopyTo(payload.AsSpan(2));
        return Frame(payload);
    }

    /// <summary>Writes a framed payload into <paramref name="writer"/>.</summary>
    public static void WriteFrame(WireWriter writer, ReadOnlySpan<byte> payload)
    {
        writer.Varint((ulong)LengthField(payload.Length));
        writer.Bytes(payload);
    }
}
