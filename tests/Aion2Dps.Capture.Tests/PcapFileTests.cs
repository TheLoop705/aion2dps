using System.Buffers.Binary;

namespace Aion2Dps.Capture.Tests;

public class PcapFileTests
{
    private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567);

    private static List<(DateTime Ts, int LinkType, byte[] Data)> SamplePackets()
    {
        var conn = new SyntheticConnection("192.168.178.81:62311", "87.232.75.150:13328");
        return new List<(DateTime, int, byte[])>
        {
            (T0, LinkTypes.Ethernet, conn.ServerSend(GameBytes.Heartbeat(1))),
            (T0.AddMilliseconds(1.5), LinkTypes.Ethernet, conn.ClientSend(new byte[] { 1, 2, 3 })),
            (T0.AddSeconds(2), LinkTypes.Ethernet, conn.ServerSend(GameBytes.IdentityFrame)),
            (T0.AddSeconds(3), LinkTypes.Ethernet, new byte[61]), // odd length → padding in pcapng
        };
    }

    [Fact]
    public void PcapNg_RoundTrip_IsExact_WithMultipleLinkTypes()
    {
        var ms = new MemoryStream();
        var packets = SamplePackets();
        var loop = SyntheticPacketBuilder.Wrap(LinkTypes.Null, SyntheticPacketBuilder.IPv4Tcp(
            IPv4Endpoint.Parse("127.0.0.1:5000"), IPv4Endpoint.Parse("127.0.0.1:6000"), 1, 1, TcpFlags.Ack, new byte[] { 9 }));
        packets.Insert(2, (T0.AddSeconds(1), LinkTypes.Null, loop));
        using (var w = new PcapFileWriter(ms, CaptureFileFormat.PcapNg, leaveOpen: true))
        {
            foreach (var p in packets) w.WritePacket(p.Ts, p.LinkType, p.Data);
            Assert.Equal(packets.Count, w.PacketsWritten);
        }
        ms.Position = 0;
        using var r = new PcapFileReader(ms);
        Assert.Equal(CaptureFileFormat.PcapNg, r.Format);
        var read = r.ReadAll().ToList();
        Assert.Equal(packets.Count, read.Count);
        for (int i = 0; i < packets.Count; i++)
        {
            Assert.Equal(packets[i].Ts, read[i].TimestampUtc); // if_tsresol 7 = exact ticks
            Assert.Equal(packets[i].LinkType, read[i].LinkType);
            Assert.Equal(packets[i].Data, read[i].Data.ToArray());
            Assert.Equal(packets[i].Data.Length, read[i].OriginalLength);
        }
        Assert.Equal(new[] { LinkTypes.Ethernet, LinkTypes.Null }, r.InterfaceLinkTypes);
        Assert.Equal(1, read[2].InterfaceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pcap_RoundTrip(bool nanoseconds)
    {
        var ms = new MemoryStream();
        var packets = SamplePackets();
        using (var w = new PcapFileWriter(ms, CaptureFileFormat.Pcap, leaveOpen: true, nanosecondTimestamps: nanoseconds))
        {
            foreach (var p in packets) w.WritePacket(p.Ts, p.LinkType, p.Data);
            w.WritePacket(T0, LinkTypes.Null, new byte[8]); // different link type: skipped in classic pcap
            Assert.Equal(1, w.PacketsSkipped);
        }
        ms.Position = 0;
        using var r = new PcapFileReader(ms);
        Assert.Equal(CaptureFileFormat.Pcap, r.Format);
        Assert.Equal(nanoseconds, r.NanosecondTimestamps);
        Assert.False(r.IsBigEndian);
        Assert.Equal(65535, r.SnapLength);
        var read = r.ReadAll().ToList();
        Assert.Equal(packets.Count, read.Count);
        for (int i = 0; i < packets.Count; i++)
        {
            long expectedTicks = nanoseconds ? packets[i].Ts.Ticks : packets[i].Ts.Ticks / 10 * 10; // µs truncation
            Assert.Equal(expectedTicks, read[i].TimestampUtc.Ticks);
            Assert.Equal(DateTimeKind.Utc, read[i].TimestampUtc.Kind);
            Assert.Equal(LinkTypes.Ethernet, read[i].LinkType);
            Assert.Equal(packets[i].Data, read[i].Data.ToArray());
        }
    }

    [Fact]
    public void Pcap_EmptyFile_WritesHeaderWithDefaultLinkType()
    {
        var ms = new MemoryStream();
        using (new PcapFileWriter(ms, CaptureFileFormat.Pcap, LinkTypes.Raw, leaveOpen: true)) { }
        Assert.Equal(24, ms.Length);
        ms.Position = 0;
        using var r = new PcapFileReader(ms);
        Assert.Equal(new[] { LinkTypes.Raw }, r.InterfaceLinkTypes);
        Assert.False(r.TryReadNext(out _));
    }

    [Fact]
    public void Pcap_BigEndian_Nanoseconds_HandBuilt()
    {
        var data = new byte[] { 0x45, 0, 0, 20, 0, 0, 0, 0, 64, 6, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var ms = new MemoryStream();
        Span<byte> h = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(h, 0xA1B23C4D);
        BinaryPrimitives.WriteUInt16BigEndian(h.Slice(4), 2);
        BinaryPrimitives.WriteUInt16BigEndian(h.Slice(6), 4);
        BinaryPrimitives.WriteUInt32BigEndian(h.Slice(16), 262144);
        BinaryPrimitives.WriteUInt32BigEndian(h.Slice(20), LinkTypes.Raw);
        ms.Write(h);
        Span<byte> rec = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(rec, 1_759_754_096);
        BinaryPrimitives.WriteUInt32BigEndian(rec.Slice(4), 123_456_789);
        BinaryPrimitives.WriteUInt32BigEndian(rec.Slice(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(rec.Slice(12), 1500);
        ms.Write(rec);
        ms.Write(data);
        ms.Write(new byte[7]); // truncated trailing record
        ms.Position = 0;

        using var r = new PcapFileReader(ms);
        Assert.True(r.IsBigEndian);
        Assert.True(r.NanosecondTimestamps);
        Assert.True(r.TryReadNext(out var p));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_759_754_096).AddTicks(1_234_567), p.TimestampUtc);
        Assert.Equal(LinkTypes.Raw, p.LinkType);
        Assert.Equal(1500, p.OriginalLength);
        Assert.Equal(data, p.Data.ToArray());
        Assert.False(r.TryReadNext(out _));
    }

    [Fact]
    public void PcapNg_HandBuilt_BigEndian_TsResolMicro_SimplePacket_AndUnknownBlocks()
    {
        var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); ms.Write(b); }

        // SHB (28 bytes, big endian)
        U32(0x0A0D0D0A); U32(28); U32(0x1A2B3C4D); U16(1); U16(0); U32(0xFFFFFFFF); U32(0xFFFFFFFF); U32(28);
        // IDB 0: Ethernet, snaplen 0, if_tsresol = 6 (µs), if_tsoffset = 10 s
        U32(1); U32(16 + 8 + 12 + 4 + 4); U16(1); U16(0); U32(0);
        U16(9); U16(1); ms.Write(new byte[] { 6, 0, 0, 0 });
        U16(14); U16(8); U32(0); U32(10);
        U16(0); U16(0);
        U32(16 + 8 + 12 + 4 + 4);
        // IDB 1: RAW, if_tsresol = 2^-10
        U32(1); U32(20 + 8 + 4); U16(LinkTypes.Raw); U16(0); U32(65535);
        U16(9); U16(1); ms.Write(new byte[] { 0x8A, 0, 0, 0 });
        U16(0); U16(0);
        U32(20 + 8 + 4);
        // Unknown block type 0x0BAD (12 bytes body) — must be skipped
        U32(0x0BAD); U32(24); ms.Write(new byte[12]); U32(24);
        // EPB on interface 0: ts = 1_000_000 µs (+10 s offset), 5 bytes
        U32(6); U32(32 + 8); U32(0); U32(0); U32(1_000_000); U32(5); U32(60); ms.Write(new byte[] { 1, 2, 3, 4, 5, 0, 0, 0 }); U32(32 + 8);
        // EPB on interface 1: ts = 2048 units of 1/1024 s = 2 s
        U32(6); U32(32 + 4); U32(1); U32(0); U32(2048); U32(3); U32(3); ms.Write(new byte[] { 7, 8, 9, 0 }); U32(32 + 4);
        // SPB (interface 0): 6 bytes
        U32(3); U32(16 + 8); U32(6); ms.Write(new byte[] { 10, 11, 12, 13, 14, 15, 0, 0 }); U32(16 + 8);
        ms.Position = 0;

        using var r = new PcapFileReader(ms);
        Assert.Equal(CaptureFileFormat.PcapNg, r.Format);
        var list = r.ReadAll().ToList();
        Assert.Equal(3, list.Count);
        Assert.True(r.IsBigEndian);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(11), list[0].TimestampUtc);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, list[0].Data.ToArray());
        Assert.Equal(60, list[0].OriginalLength);
        Assert.Equal(LinkTypes.Ethernet, list[0].LinkType);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(2), list[1].TimestampUtc);
        Assert.Equal(LinkTypes.Raw, list[1].LinkType);
        Assert.Equal(new byte[] { 7, 8, 9 }, list[1].Data.ToArray());
        Assert.Equal(new byte[] { 10, 11, 12, 13, 14, 15 }, list[2].Data.ToArray());
    }

    [Fact]
    public void PcapNg_SecondSection_ResetsInterfaces()
    {
        var ms = new MemoryStream();
        using (var w = new PcapFileWriter(ms, CaptureFileFormat.PcapNg, leaveOpen: true))
            w.WritePacket(T0, LinkTypes.Ethernet, new byte[20]);
        using (var w = new PcapFileWriter(ms, CaptureFileFormat.PcapNg, leaveOpen: true))
            w.WritePacket(T0.AddSeconds(1), LinkTypes.Raw, new byte[21]);
        ms.Position = 0;
        using var r = new PcapFileReader(ms);
        var list = r.ReadAll().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(LinkTypes.Ethernet, list[0].LinkType);
        Assert.Equal(LinkTypes.Raw, list[1].LinkType);
        Assert.Equal(0, list[1].InterfaceId);
    }

    [Fact]
    public void Files_OnDisk_AndFormatByExtension()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-capture-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string ng = Path.Combine(dir, "a.pcapng");
            string pc = Path.Combine(dir, "b.pcap");
            Assert.Equal(CaptureFileFormat.PcapNg, PcapFileWriter.FormatForPath(ng));
            Assert.Equal(CaptureFileFormat.Pcap, PcapFileWriter.FormatForPath(pc));
            foreach (var path in new[] { ng, pc })
            {
                using (var w = PcapFileWriter.Create(path, PcapFileWriter.FormatForPath(path)))
                    foreach (var p in SamplePackets()) w.WritePacket(p.Ts, p.LinkType, p.Data);
                Assert.True(PcapFileReader.IsCaptureFile(path));
                using var r = new PcapFileReader(path);
                Assert.Equal(SamplePackets().Count, r.ReadAll().Count());
            }
            File.WriteAllText(Path.Combine(dir, "c.txt"), "hello");
            Assert.False(PcapFileReader.IsCaptureFile(Path.Combine(dir, "c.txt")));
            Assert.Throws<InvalidDataException>(() => new PcapFileReader(Path.Combine(dir, "c.txt")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
