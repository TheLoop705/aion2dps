using System.Buffers.Binary;
using System.Net;

namespace Aion2Dps.Simulator;

/// <summary>Addresses and extras of an exported capture.</summary>
public sealed record PcapExportOptions
{
    public IPAddress ServerIp { get; init; } = IPAddress.Parse("193.202.112.47");
    public IPAddress ClientIp { get; init; } = IPAddress.Parse("192.168.1.50");
    public ushort ServerPort { get; init; } = 13328;
    public ushort ClientPort { get; init; } = 62311;
    public uint ServerInitialSeq { get; init; } = 0x3A1F_0000;
    public uint ClientInitialSeq { get; init; } = 0x7B20_0000;
    /// <summary>Write the SYN / SYN-ACK / ACK handshake before the data.</summary>
    public bool IncludeHandshake { get; init; } = true;
    /// <summary>Seconds between small client→server packets on the game flow (0 = none).</summary>
    public double ClientPacketIntervalSeconds { get; init; } = 3.0;
    /// <summary>Add a TLS decoy flow (client ↔ 104.18.20.30:443) with application-data records.</summary>
    public bool IncludeTlsDecoy { get; init; } = true;
    public ushort DecoyClientPort { get; init; } = 62400;
    public IPAddress DecoyServerIp { get; init; } = IPAddress.Parse("104.18.20.30");
    public int Seed { get; init; } = 7;
}

/// <summary>
/// Writes a server→client stream as a classic libpcap file (magic A1B2C3D4, µs timestamps, Ethernet II / IPv4 / TCP)
/// with correct sequence/ack numbers and checksums, plus a few client→server packets and a TLS decoy flow,
/// so capture detection, reassembly and replay can be tested without the game.
/// </summary>
public static class PcapExporter
{
    public const uint Magic = 0xA1B2C3D4;
    public const uint LinkTypeEthernet = 1;
    public const int MaxTcpPayload = 1460;

    private static readonly byte[] ServerMac = [0x00, 0x1C, 0x73, 0x5A, 0x10, 0x01];
    private static readonly byte[] ClientMac = [0x3C, 0x7C, 0x3F, 0x22, 0x91, 0x0E];

    /// <summary>Generates the stream of <paramref name="scenario"/> and writes it to <paramref name="path"/>.</summary>
    public static GeneratedStream WriteFile(string path, Scenario scenario, DateTime startUtc, StreamGeneratorOptions? streamOptions = null, PcapExportOptions? options = null)
    {
        var stream = new StreamGenerator(streamOptions).Generate(scenario, startUtc);
        using var file = File.Create(path);
        Write(file, stream.Chunks, options);
        return stream;
    }

    private readonly record struct Packet(DateTime Time, int Order, byte[] Bytes);

    /// <summary>Writes the chunks (server→client payloads in order) as a pcap stream.</summary>
    public static void Write(Stream output, IReadOnlyList<StreamChunk> chunks, PcapExportOptions? options = null)
    {
        var o = options ?? new PcapExportOptions();
        var rng = new Random(o.Seed);
        var packets = new List<Packet>();
        int order = 0;
        DateTime first = chunks.Count > 0 ? chunks[0].TimeUtc : DateTime.UtcNow;
        DateTime lastTime = chunks.Count > 0 ? chunks[^1].TimeUtc : first;

        uint sSeq = o.ServerInitialSeq, cSeq = o.ClientInitialSeq;
        ushort ipId = 0x1200;
        const byte Syn = 0x02, Ack = 0x10, Psh = 0x08;

        byte[] Server(byte flags, ReadOnlySpan<byte> payload) =>
            BuildPacket(ServerMac, ClientMac, o.ServerIp, o.ClientIp, o.ServerPort, o.ClientPort, sSeq, cSeq, flags, ipId++, 115, payload);
        byte[] Client(byte flags, ReadOnlySpan<byte> payload) =>
            BuildPacket(ClientMac, ServerMac, o.ClientIp, o.ServerIp, o.ClientPort, o.ServerPort, cSeq, sSeq, flags, ipId++, 128, payload);

        if (o.IncludeHandshake)
        {
            var t0 = first.AddMilliseconds(-80);
            packets.Add(new Packet(t0, order++, Client(Syn, default)));
            cSeq++;
            packets.Add(new Packet(t0.AddMilliseconds(30), order++, Server(Syn | Ack, default)));
            sSeq++;
            packets.Add(new Packet(t0.AddMilliseconds(31), order++, Client(Ack, default)));
        }
        else
        {
            sSeq++;
            cSeq++;
        }

        double nextClient = o.ClientPacketIntervalSeconds > 0 ? 0.5 : double.MaxValue;
        foreach (var chunk in chunks)
        {
            double offset = (chunk.TimeUtc - first).TotalSeconds;
            while (offset >= nextClient)
            {
                var payload = new byte[13];
                rng.NextBytes(payload);
                payload[0] = 0x11; // looks like a framed client record
                packets.Add(new Packet(first.AddSeconds(nextClient), order++, Client(Psh | Ack, payload)));
                cSeq += (uint)payload.Length;
                nextClient += o.ClientPacketIntervalSeconds;
            }

            for (int pos = 0; pos < chunk.Data.Length; pos += MaxTcpPayload)
            {
                var part = chunk.Data.AsSpan(pos, Math.Min(MaxTcpPayload, chunk.Data.Length - pos));
                packets.Add(new Packet(chunk.TimeUtc, order++, Server(Psh | Ack, part)));
                sSeq += (uint)part.Length;
            }
        }

        if (o.IncludeTlsDecoy) AddTlsDecoy(packets, ref order, o, rng, first, lastTime);

        packets.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Order.CompareTo(b.Order));

        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 65535);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], LinkTypeEthernet);
        output.Write(header);

        Span<byte> rec = stackalloc byte[16];
        foreach (var p in packets)
        {
            long micros = (p.Time.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10;
            BinaryPrimitives.WriteUInt32LittleEndian(rec, (uint)(micros / 1_000_000));
            BinaryPrimitives.WriteUInt32LittleEndian(rec[4..], (uint)(micros % 1_000_000));
            BinaryPrimitives.WriteUInt32LittleEndian(rec[8..], (uint)p.Bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[12..], (uint)p.Bytes.Length);
            output.Write(rec);
            output.Write(p.Bytes);
        }
    }

    private static void AddTlsDecoy(List<Packet> packets, ref int order, PcapExportOptions o, Random rng, DateTime first, DateTime last)
    {
        uint cs = 0x1000_0000, ss = 0x2000_0000;
        ushort id = 0x5000;
        byte[] Rec(int len)
        {
            var r = new byte[5 + len];
            r[0] = 0x17; r[1] = 0x03; r[2] = 0x03;
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(3), (ushort)len);
            rng.NextBytes(r.AsSpan(5));
            return r;
        }

        var t = first.AddMilliseconds(-200);
        packets.Add(new Packet(t, order++, BuildPacket(ClientMac, ServerMac, o.ClientIp, o.DecoyServerIp, o.DecoyClientPort, 443, cs++, 0, 0x02, id++, 128, default)));
        packets.Add(new Packet(t.AddMilliseconds(20), order++, BuildPacket(ServerMac, ClientMac, o.DecoyServerIp, o.ClientIp, 443, o.DecoyClientPort, ss++, cs, 0x12, id++, 57, default)));
        packets.Add(new Packet(t.AddMilliseconds(21), order++, BuildPacket(ClientMac, ServerMac, o.ClientIp, o.DecoyServerIp, o.DecoyClientPort, 443, cs, ss, 0x10, id++, 128, default)));
        for (var at = first.AddSeconds(1.3); at < last; at = at.AddSeconds(7))
        {
            var req = Rec(rng.Next(40, 200));
            packets.Add(new Packet(at, order++, BuildPacket(ClientMac, ServerMac, o.ClientIp, o.DecoyServerIp, o.DecoyClientPort, 443, cs, ss, 0x18, id++, 128, req)));
            cs += (uint)req.Length;
            var resp = Rec(rng.Next(200, 1400));
            packets.Add(new Packet(at.AddMilliseconds(25), order++, BuildPacket(ServerMac, ClientMac, o.DecoyServerIp, o.ClientIp, 443, o.DecoyClientPort, ss, cs, 0x18, id++, 57, resp)));
            ss += (uint)resp.Length;
        }
    }

    /// <summary>Ethernet II + IPv4 (no options, DF) + TCP (no options) with valid checksums.</summary>
    public static byte[] BuildPacket(byte[] srcMac, byte[] dstMac, IPAddress srcIp, IPAddress dstIp, ushort srcPort, ushort dstPort,
        uint seq, uint ack, byte flags, ushort ipId, byte ttl, ReadOnlySpan<byte> payload)
    {
        int ipLen = 20 + 20 + payload.Length;
        var p = new byte[14 + ipLen];
        dstMac.CopyTo(p, 0);
        srcMac.CopyTo(p, 6);
        p[12] = 0x08; p[13] = 0x00;

        var ip = p.AsSpan(14, 20);
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ipLen);
        BinaryPrimitives.WriteUInt16BigEndian(ip[4..], ipId);
        BinaryPrimitives.WriteUInt16BigEndian(ip[6..], 0x4000);
        ip[8] = ttl;
        ip[9] = 6;
        srcIp.GetAddressBytes().CopyTo(ip[12..]);
        dstIp.GetAddressBytes().CopyTo(ip[16..]);
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], Checksum(ip, 0));

        var tcp = p.AsSpan(34, 20 + payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], ack);
        tcp[12] = 0x50;
        tcp[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 0xFAF0);
        payload.CopyTo(tcp[20..]);

        uint pseudo = 0;
        var s = ip.Slice(12, 8);
        for (int i = 0; i < 8; i += 2) pseudo += (uint)(s[i] << 8 | s[i + 1]);
        pseudo += 6;
        pseudo += (uint)tcp.Length;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], Checksum(tcp, pseudo));
        return p;
    }

    /// <summary>Internet checksum (RFC 1071) of <paramref name="data"/> plus an initial partial sum.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> data, uint initial)
    {
        ulong sum = initial;
        int i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
