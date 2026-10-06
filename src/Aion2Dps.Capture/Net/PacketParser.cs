using System.Buffers.Binary;

namespace Aion2Dps.Capture;

/// <summary>pcap LINKTYPE_* values handled by <see cref="PacketParser"/> (PROTOCOL.md §2.1).</summary>
public static class LinkTypes
{
    /// <summary>BSD loopback: 4-byte address family in host byte order (Npcap loopback: <c>02 00 00 00</c>).</summary>
    public const int Null = 0;
    public const int Ethernet = 1;
    /// <summary>DLT_RAW as used by some BSDs.</summary>
    public const int RawBsd12 = 12;
    /// <summary>DLT_RAW as used by OpenBSD.</summary>
    public const int RawBsd14 = 14;
    /// <summary>LINKTYPE_RAW: the frame starts with the IP header.</summary>
    public const int Raw = 101;
    /// <summary>OpenBSD loopback: 4-byte address family in network byte order.</summary>
    public const int Loop = 108;
    /// <summary>Linux "cooked" capture v1 (16-byte header).</summary>
    public const int LinuxSll = 113;
    /// <summary>LINKTYPE_IPV4: raw IPv4.</summary>
    public const int IPv4 = 228;
    /// <summary>Linux "cooked" capture v2 (20-byte header).</summary>
    public const int LinuxSll2 = 276;

    public static bool IsSupported(int linkType) => linkType is Null or Ethernet or RawBsd12 or RawBsd14 or Raw or Loop
        or LinuxSll or IPv4 or LinuxSll2;

    public static string Name(int linkType) => linkType switch
    {
        Null => "NULL",
        Ethernet => "EN10MB",
        RawBsd12 or RawBsd14 or Raw => "RAW",
        Loop => "LOOP",
        LinuxSll => "LINUX_SLL",
        IPv4 => "IPV4",
        LinuxSll2 => "LINUX_SLL2",
        _ => $"LINKTYPE_{linkType}",
    };
}

[Flags]
public enum TcpFlags : byte
{
    None = 0,
    Fin = 0x01,
    Syn = 0x02,
    Rst = 0x04,
    Psh = 0x08,
    Ack = 0x10,
    Urg = 0x20,
    Ece = 0x40,
    Cwr = 0x80,
}

/// <summary>A parsed IPv4/TCP segment. <see cref="Payload"/> points into the original frame.</summary>
public readonly ref struct TcpSegment
{
    public TcpSegment(IPv4Endpoint source, IPv4Endpoint destination, uint sequence, uint acknowledgment, TcpFlags flags,
        ReadOnlySpan<byte> payload, int payloadOffset)
    {
        Source = source;
        Destination = destination;
        Sequence = sequence;
        Acknowledgment = acknowledgment;
        Flags = flags;
        Payload = payload;
        PayloadOffset = payloadOffset;
    }

    public IPv4Endpoint Source { get; }
    public IPv4Endpoint Destination { get; }
    public uint Sequence { get; }
    public uint Acknowledgment { get; }
    public TcpFlags Flags { get; }
    public ReadOnlySpan<byte> Payload { get; }
    /// <summary>Offset of <see cref="Payload"/> inside the frame that was parsed.</summary>
    public int PayloadOffset { get; }
}

/// <summary>Allocation-free link-layer → IPv4 → TCP parser. IPv4 only, TCP only, IP fragments are skipped.</summary>
public static class PacketParser
{
    private const ushort EtherTypeIPv4 = 0x0800;
    private const ushort EtherTypeVlan = 0x8100;
    private const ushort EtherTypeQinQ = 0x88A8;
    private const ushort EtherTypeVlanOld = 0x9100;
    private const int AfInet = 2;

    /// <summary>Parses a captured frame of the given link type into a TCP segment.</summary>
    public static bool TryParse(int linkType, ReadOnlySpan<byte> frame, out TcpSegment segment)
    {
        segment = default;
        if (!TryGetIPv4Offset(linkType, frame, out int ipOffset)) return false;
        return TryParseIPv4Tcp(frame, ipOffset, out segment);
    }

    /// <summary>Finds the offset of the IPv4 header inside a link-layer frame.</summary>
    public static bool TryGetIPv4Offset(int linkType, ReadOnlySpan<byte> frame, out int ipOffset)
    {
        ipOffset = -1;
        switch (linkType)
        {
            case LinkTypes.Ethernet:
            {
                if (frame.Length < 14) return false;
                ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12));
                int off = 14;
                for (int tags = 0; tags < 3 && etherType is EtherTypeVlan or EtherTypeQinQ or EtherTypeVlanOld; tags++)
                {
                    if (frame.Length < off + 4) return false;
                    etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(off + 2));
                    off += 4;
                }
                if (etherType != EtherTypeIPv4) return false;
                ipOffset = off;
                break;
            }
            case LinkTypes.Null:
            {
                if (frame.Length < 4) return false;
                uint le = BinaryPrimitives.ReadUInt32LittleEndian(frame);
                uint be = BinaryPrimitives.ReadUInt32BigEndian(frame);
                if (le != AfInet && be != AfInet) return false;
                ipOffset = 4;
                break;
            }
            case LinkTypes.Loop:
            {
                if (frame.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(frame) != AfInet) return false;
                ipOffset = 4;
                break;
            }
            case LinkTypes.Raw:
            case LinkTypes.RawBsd12:
            case LinkTypes.RawBsd14:
            case LinkTypes.IPv4:
                ipOffset = 0;
                break;
            case LinkTypes.LinuxSll:
            {
                if (frame.Length < 16 || BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(14)) != EtherTypeIPv4) return false;
                ipOffset = 16;
                break;
            }
            case LinkTypes.LinuxSll2:
            {
                if (frame.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(frame) != EtherTypeIPv4) return false;
                ipOffset = 20;
                break;
            }
            default:
                return false;
        }
        return frame.Length > ipOffset && (frame[ipOffset] >> 4) == 4;
    }

    /// <summary>Parses an IPv4 packet starting at <paramref name="ipOffset"/> and its TCP header.</summary>
    public static bool TryParseIPv4Tcp(ReadOnlySpan<byte> frame, int ipOffset, out TcpSegment segment)
    {
        segment = default;
        if (ipOffset < 0 || frame.Length - ipOffset < 20) return false;
        var ip = frame.Slice(ipOffset);
        if ((ip[0] >> 4) != 4) return false;
        int ihl = (ip[0] & 0x0F) * 4;
        if (ihl < 20 || ihl > ip.Length) return false;
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(ip.Slice(2));
        // 0 = TCP segmentation offload on the sending host (Windows captures outgoing super-frames this way).
        if (totalLength == 0 || totalLength > ip.Length) totalLength = ip.Length;
        if (totalLength < ihl + 20) return false;
        ushort fragment = BinaryPrimitives.ReadUInt16BigEndian(ip.Slice(6));
        if ((fragment & 0x3FFF) != 0) return false; // MF set or non-zero offset: fragment, not handled
        if (ip[9] != 6) return false; // TCP only

        uint src = BinaryPrimitives.ReadUInt32BigEndian(ip.Slice(12));
        uint dst = BinaryPrimitives.ReadUInt32BigEndian(ip.Slice(16));
        var tcp = ip.Slice(ihl, totalLength - ihl);
        ushort srcPort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
        ushort dstPort = BinaryPrimitives.ReadUInt16BigEndian(tcp.Slice(2));
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(4));
        uint ack = BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(8));
        int dataOffset = (tcp[12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > tcp.Length) return false;
        var flags = (TcpFlags)tcp[13];
        segment = new TcpSegment(new IPv4Endpoint(src, srcPort), new IPv4Endpoint(dst, dstPort), seq, ack, flags,
            tcp.Slice(dataOffset), ipOffset + ihl + dataOffset);
        return true;
    }
}
