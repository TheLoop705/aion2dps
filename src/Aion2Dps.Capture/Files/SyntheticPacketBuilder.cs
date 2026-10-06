using System.Buffers.Binary;

namespace Aion2Dps.Capture;

/// <summary>
/// Builds well-formed link-layer/IPv4/TCP frames and game frames (tests, demo captures, tools). Checksums are computed.
/// </summary>
public static class SyntheticPacketBuilder
{
    private static readonly byte[] ServerMac = [0x02, 0x00, 0x5E, 0x10, 0x00, 0x01];
    private static readonly byte[] ClientMac = [0x02, 0x00, 0x5E, 0x10, 0x00, 0x02];

    /// <summary>An IPv4 packet carrying one TCP segment.</summary>
    public static byte[] IPv4Tcp(IPv4Endpoint source, IPv4Endpoint destination, uint sequence, uint acknowledgment, TcpFlags flags,
        ReadOnlySpan<byte> payload, ushort window = 0xFFFF, ushort ipId = 0)
    {
        int total = 20 + 20 + payload.Length;
        if (total > 65535) throw new ArgumentException("Payload too large for one IPv4 packet.", nameof(payload));
        var p = new byte[total];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)total);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), ipId);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), 0x4000); // DF
        p[8] = 64;
        p[9] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), source.Address);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), destination.Address);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), Checksum(p.AsSpan(0, 20), 0));

        var tcp = p.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, source.Port);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(2), destination.Port);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.Slice(4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.Slice(8), acknowledgment);
        tcp[12] = 5 << 4;
        tcp[13] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(14), window);
        payload.CopyTo(tcp.Slice(20));
        uint pseudo = (source.Address >> 16) + (source.Address & 0xFFFF) + (destination.Address >> 16) + (destination.Address & 0xFFFF)
                      + 6u + (uint)tcp.Length;
        BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(16), Checksum(tcp, pseudo));
        return p;
    }

    /// <summary>Ethernet II frame (optionally 802.1Q tagged) around an IPv4 packet.</summary>
    public static byte[] Ethernet(ReadOnlySpan<byte> ipPacket, bool toClient = true, ushort? vlanId = null)
    {
        int header = 14 + (vlanId is null ? 0 : 4);
        var f = new byte[header + ipPacket.Length];
        (toClient ? ClientMac : ServerMac).CopyTo(f, 0);
        (toClient ? ServerMac : ClientMac).CopyTo(f, 6);
        int o = 12;
        if (vlanId is { } vid)
        {
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(o), 0x8100);
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(o + 2), (ushort)(vid & 0x0FFF));
            o += 4;
        }
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(o), 0x0800);
        ipPacket.CopyTo(f.AsSpan(header));
        return f;
    }

    /// <summary>Wraps an IPv4 packet for the given link type (Ethernet, NULL, LOOP, RAW, IPV4, LINUX_SLL, LINUX_SLL2).</summary>
    public static byte[] Wrap(int linkType, ReadOnlySpan<byte> ipPacket)
    {
        switch (linkType)
        {
            case LinkTypes.Ethernet:
                return Ethernet(ipPacket);
            case LinkTypes.Null:
            {
                var f = new byte[4 + ipPacket.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(f, 2);
                ipPacket.CopyTo(f.AsSpan(4));
                return f;
            }
            case LinkTypes.Loop:
            {
                var f = new byte[4 + ipPacket.Length];
                BinaryPrimitives.WriteUInt32BigEndian(f, 2);
                ipPacket.CopyTo(f.AsSpan(4));
                return f;
            }
            case LinkTypes.Raw:
            case LinkTypes.RawBsd12:
            case LinkTypes.RawBsd14:
            case LinkTypes.IPv4:
                return ipPacket.ToArray();
            case LinkTypes.LinuxSll:
            {
                var f = new byte[16 + ipPacket.Length];
                BinaryPrimitives.WriteUInt16BigEndian(f, 0);      // packet type: to us
                BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), 1); // ARPHRD_ETHER
                BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 6);
                ServerMac.CopyTo(f, 6);
                BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(14), 0x0800);
                ipPacket.CopyTo(f.AsSpan(16));
                return f;
            }
            case LinkTypes.LinuxSll2:
            {
                var f = new byte[20 + ipPacket.Length];
                BinaryPrimitives.WriteUInt16BigEndian(f, 0x0800);
                BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(4), 2); // interface index
                BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(8), 1); // ARPHRD_ETHER
                f[10] = 0;
                f[11] = 6;
                ServerMac.CopyTo(f, 12);
                ipPacket.CopyTo(f.AsSpan(20));
                return f;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(linkType), linkType, "Unsupported link type.");
        }
    }

    /// <summary>One game frame: varint(L) + opcode/body, where L = body length + 4 (PROTOCOL.md §4.1).
    /// <paramref name="opcodeAndBody"/> starts with the two opcode bytes.</summary>
    public static byte[] GameFrame(ReadOnlySpan<byte> opcodeAndBody)
    {
        uint l = (uint)opcodeAndBody.Length + 4;
        Span<byte> v = stackalloc byte[5];
        int w = 0;
        do
        {
            byte b = (byte)(l & 0x7F);
            l >>= 7;
            if (l != 0) b |= 0x80;
            v[w++] = b;
        } while (l != 0);
        var f = new byte[w + opcodeAndBody.Length];
        v.Slice(0, w).CopyTo(f);
        opcodeAndBody.CopyTo(f.AsSpan(w));
        return f;
    }

    /// <summary>The 11-byte heartbeat frame <c>0E 00 36</c> + u64 server time (Unix ms, little endian).</summary>
    public static byte[] Heartbeat(ulong serverUnixMs)
    {
        var f = new byte[11];
        f[0] = 0x0E;
        f[1] = 0x00;
        f[2] = 0x36;
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(3), serverUnixMs);
        return f;
    }

    private static ushort Checksum(ReadOnlySpan<byte> data, uint initial)
    {
        uint sum = initial;
        int i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
