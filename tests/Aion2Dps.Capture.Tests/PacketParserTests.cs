using System.Buffers.Binary;

namespace Aion2Dps.Capture.Tests;

public class PacketParserTests
{
    private static readonly IPv4Endpoint Server = IPv4Endpoint.Parse("87.232.75.150:13328");
    private static readonly IPv4Endpoint Client = IPv4Endpoint.Parse("192.168.178.81:62311");
    private static readonly byte[] Payload = GameBytes.Heartbeat(1);

    private static byte[] Ip(ReadOnlySpan<byte> payload, TcpFlags flags = TcpFlags.Ack | TcpFlags.Psh) =>
        SyntheticPacketBuilder.IPv4Tcp(Server, Client, 0xDEADBEEF, 0x01020304, flags, payload);

    public static IEnumerable<object[]> LinkTypeCases() => new[]
    {
        new object[] { LinkTypes.Ethernet },
        new object[] { LinkTypes.Null },
        new object[] { LinkTypes.Loop },
        new object[] { LinkTypes.Raw },
        new object[] { LinkTypes.IPv4 },
        new object[] { LinkTypes.RawBsd12 },
        new object[] { LinkTypes.LinuxSll },
        new object[] { LinkTypes.LinuxSll2 },
    };

    [Theory]
    [MemberData(nameof(LinkTypeCases))]
    public void ParsesEveryLinkType(int linkType)
    {
        var frame = SyntheticPacketBuilder.Wrap(linkType, Ip(Payload));
        Assert.True(PacketParser.TryParse(linkType, frame, out var seg));
        Assert.Equal(Server, seg.Source);
        Assert.Equal(Client, seg.Destination);
        Assert.Equal(0xDEADBEEFu, seg.Sequence);
        Assert.Equal(0x01020304u, seg.Acknowledgment);
        Assert.Equal(TcpFlags.Ack | TcpFlags.Psh, seg.Flags);
        Assert.Equal(Payload, seg.Payload.ToArray());
        Assert.Equal(Payload, frame.AsSpan(seg.PayloadOffset, Payload.Length).ToArray());
    }

    [Fact]
    public void NullLinkType_AcceptsBigEndianFamily()
    {
        var ip = Ip(Payload);
        var frame = new byte[4 + ip.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, 2);
        ip.CopyTo(frame, 4);
        Assert.True(PacketParser.TryParse(LinkTypes.Null, frame, out var seg));
        Assert.Equal(Payload, seg.Payload.ToArray());
    }

    [Fact]
    public void NpcapLoopbackHeader_Is02000000()
    {
        var frame = SyntheticPacketBuilder.Wrap(LinkTypes.Null, Ip(Payload));
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00 }, frame[..4]);
    }

    [Theory]
    [InlineData((ushort)7)]
    [InlineData((ushort)4095)]
    public void Ethernet_Vlan8021Q(ushort vlan)
    {
        var frame = SyntheticPacketBuilder.Ethernet(Ip(Payload), vlanId: vlan);
        Assert.Equal(0x81, frame[12]);
        Assert.True(PacketParser.TryParse(LinkTypes.Ethernet, frame, out var seg));
        Assert.Equal(Payload, seg.Payload.ToArray());
    }

    [Fact]
    public void Ethernet_QinQ_DoubleTag()
    {
        var single = SyntheticPacketBuilder.Ethernet(Ip(Payload), vlanId: 10);
        var frame = new byte[single.Length + 4];
        single.AsSpan(0, 12).CopyTo(frame);
        frame[12] = 0x88; frame[13] = 0xA8; frame[14] = 0x00; frame[15] = 0x64; // outer S-tag
        single.AsSpan(12).CopyTo(frame.AsSpan(16));
        Assert.True(PacketParser.TryParse(LinkTypes.Ethernet, frame, out var seg));
        Assert.Equal(Payload, seg.Payload.ToArray());
    }

    [Fact]
    public void EthernetPadding_IsExcludedByIpTotalLength()
    {
        var frame = SyntheticPacketBuilder.Ethernet(Ip(ReadOnlySpan<byte>.Empty, TcpFlags.Ack)).Concat(new byte[6]).ToArray();
        Assert.True(PacketParser.TryParse(LinkTypes.Ethernet, frame, out var seg));
        Assert.Equal(0, seg.Payload.Length);
    }

    [Fact]
    public void TsoZeroTotalLength_UsesCapturedLength()
    {
        var ip = Ip(Payload);
        ip[2] = 0; ip[3] = 0;
        Assert.True(PacketParser.TryParse(LinkTypes.Raw, ip, out var seg));
        Assert.Equal(Payload, seg.Payload.ToArray());
    }

    [Fact]
    public void RespectsIhlAndTcpDataOffset()
    {
        // Build IPv4 with 4 bytes of options and TCP with 12 bytes of options by hand.
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        int ihl = 24, doff = 32;
        var p = new byte[ihl + doff + payload.Length];
        p[0] = 0x46; // IHL 6
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
        p[9] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), Server.Address);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), Client.Address);
        p[20] = 1; p[21] = 1; p[22] = 1; p[23] = 0; // NOP NOP NOP EOL
        var tcp = p.AsSpan(ihl);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, Server.Port);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(2), Client.Port);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.Slice(4), 42);
        tcp[12] = (byte)((doff / 4) << 4);
        tcp[13] = (byte)TcpFlags.Ack;
        payload.CopyTo(tcp.Slice(doff));
        Assert.True(PacketParser.TryParse(LinkTypes.Raw, p, out var seg));
        Assert.Equal(42u, seg.Sequence);
        Assert.Equal(payload, seg.Payload.ToArray());
        Assert.Equal(ihl + doff, seg.PayloadOffset);
    }

    [Fact]
    public void RejectsNonTcp_Fragments_IPv6_AndGarbage()
    {
        var udp = Ip(Payload);
        udp[9] = 17;
        Assert.False(PacketParser.TryParse(LinkTypes.Raw, udp, out _));

        var frag = Ip(Payload);
        frag[6] = 0x20; frag[7] = 0x00; // MF
        Assert.False(PacketParser.TryParse(LinkTypes.Raw, frag, out _));
        var frag2 = Ip(Payload);
        frag2[6] = 0x00; frag2[7] = 0x10; // offset
        Assert.False(PacketParser.TryParse(LinkTypes.Raw, frag2, out _));

        var eth6 = SyntheticPacketBuilder.Ethernet(Ip(Payload));
        eth6[12] = 0x86; eth6[13] = 0xDD;
        Assert.False(PacketParser.TryParse(LinkTypes.Ethernet, eth6, out _));

        var v6 = Ip(Payload);
        v6[0] = 0x60;
        Assert.False(PacketParser.TryParse(LinkTypes.Raw, v6, out _));

        Assert.False(PacketParser.TryParse(LinkTypes.Ethernet, new byte[10], out _));
        Assert.False(PacketParser.TryParse(LinkTypes.Raw, new byte[] { 0x45, 0, 0 }, out _));
        Assert.False(PacketParser.TryParse(999, Ip(Payload), out _));

        var badOffset = Ip(Payload);
        badOffset[20 + 12] = 0x20; // data offset 8 bytes (< 20)
        Assert.False(PacketParser.TryParse(LinkTypes.Raw, badOffset, out _));

        var nullV6 = SyntheticPacketBuilder.Wrap(LinkTypes.Null, Ip(Payload));
        nullV6[0] = 24; // AF_INET6 on BSD
        Assert.False(PacketParser.TryParse(LinkTypes.Null, nullV6, out _));
    }

    [Fact]
    public void Endpoint_RoundTrips()
    {
        var ep = IPv4Endpoint.Parse("87.232.75.150:13328");
        Assert.Equal("87.232.75.150:13328", ep.ToString());
        Assert.Equal(ep, IPv4Endpoint.FromIPEndPoint(ep.ToIPEndPoint()));
        Assert.False(ep.IsLoopback);
        Assert.True(IPv4Endpoint.Parse("127.0.0.1:1").IsLoopback);
        Assert.Equal(TcpFlowKey.Create(Server, Client), TcpFlowKey.Create(Client, Server));
    }

    [Fact]
    public void Signature_CountsHeartbeats_AndDetectsTls()
    {
        var payload = GameBytes.Concat(GameBytes.Heartbeat(1), GameBytes.DamageFrame(1), GameBytes.Heartbeat(2), new byte[] { 0x06, 0x00, 0x36 });
        Assert.Equal(3, GameSignature.CountHeartbeats(payload));
        Assert.Equal(0, GameSignature.CountHeartbeats(new byte[] { 0x0E, 0x00, 0x36, 1, 2 })); // truncated heartbeat
        Assert.True(GameSignature.LooksLikeTls(new byte[] { 0x16, 0x03, 0x01, 0x02, 0x00 }));
        Assert.True(GameSignature.LooksLikeTls(new byte[] { 0x17, 0x03, 0x03, 0x00, 0x20 }));
        Assert.False(GameSignature.LooksLikeTls(new byte[] { 0x18, 0x03, 0x03 }));
        Assert.False(GameSignature.LooksLikeTls(GameBytes.Heartbeat(0)));
    }
}
