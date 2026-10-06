using Aion2Dps.Contracts;

namespace Aion2Dps.Capture.Tests;

/// <summary>Records everything a stream sink receives.</summary>
internal sealed class CollectingSink : IStreamSink
{
    private readonly MemoryStream _data = new();
    public List<string> Events { get; } = new();
    public List<DiscontinuityReason> Discontinuities { get; } = new();
    public List<DateTime> Times { get; } = new();

    public byte[] Data => _data.ToArray();

    public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
    {
        _data.Write(data);
        Times.Add(timeUtc);
        Events.Add($"D{data.Length}");
    }

    public void OnDiscontinuity(DiscontinuityReason reason)
    {
        Discontinuities.Add(reason);
        Events.Add(reason.ToString());
    }
}

/// <summary>Generates frames for one synthetic TCP connection (Ethernet unless another link type is given).</summary>
internal sealed class SyntheticConnection
{
    private uint _serverSeq;
    private uint _clientSeq;

    public SyntheticConnection(string client, string server, uint serverIsn = 100_000, uint clientIsn = 5_000, int linkType = LinkTypes.Ethernet)
    {
        Client = IPv4Endpoint.Parse(client);
        Server = IPv4Endpoint.Parse(server);
        _serverSeq = serverIsn;
        _clientSeq = clientIsn;
        LinkType = linkType;
    }

    public IPv4Endpoint Client { get; }
    public IPv4Endpoint Server { get; }
    public int LinkType { get; }
    public uint ServerSeq => _serverSeq;

    public byte[] ServerSend(ReadOnlySpan<byte> payload, TcpFlags flags = TcpFlags.Ack | TcpFlags.Psh)
    {
        var f = ServerSegmentAt(_serverSeq, payload, flags);
        _serverSeq = unchecked(_serverSeq + (uint)payload.Length);
        return f;
    }

    /// <summary>Server segment at an explicit sequence number (does not advance the counter).</summary>
    public byte[] ServerSegmentAt(uint seq, ReadOnlySpan<byte> payload, TcpFlags flags = TcpFlags.Ack | TcpFlags.Psh) =>
        SyntheticPacketBuilder.Wrap(LinkType, SyntheticPacketBuilder.IPv4Tcp(Server, Client, seq, _clientSeq, flags, payload));

    public byte[] ClientSend(ReadOnlySpan<byte> payload, TcpFlags flags = TcpFlags.Ack | TcpFlags.Psh)
    {
        var f = SyntheticPacketBuilder.Wrap(LinkType, SyntheticPacketBuilder.IPv4Tcp(Client, Server, _clientSeq, _serverSeq, flags, payload));
        _clientSeq = unchecked(_clientSeq + (uint)payload.Length);
        return f;
    }

    public byte[] ClientSyn()
    {
        var f = SyntheticPacketBuilder.Wrap(LinkType, SyntheticPacketBuilder.IPv4Tcp(Client, Server, _clientSeq, 0, TcpFlags.Syn, default));
        _clientSeq++;
        return f;
    }

    public byte[] ServerSynAck()
    {
        var f = SyntheticPacketBuilder.Wrap(LinkType, SyntheticPacketBuilder.IPv4Tcp(Server, Client, _serverSeq, _clientSeq, TcpFlags.Syn | TcpFlags.Ack, default));
        _serverSeq++;
        return f;
    }
}

internal static class GameBytes
{
    /// <summary>Own-character record from PROTOCOL.md §8.6 ("Naicha", server 1304, Cleric, level 28), body form.</summary>
    public static readonly byte[] IdentityBody = Convert.FromHexString("3336ED745E91C128" + "3706" + "4E6169636861" + "1805" + "1E000000" + "01" + "1C000000");

    public static byte[] IdentityFrame => SyntheticPacketBuilder.GameFrame(IdentityBody);

    public static byte[] Heartbeat(int i) => SyntheticPacketBuilder.Heartbeat(1_759_600_000_000UL + (ulong)i * 52);

    /// <summary>A plausible damage-like frame (opcode 04 38) of the given body size.</summary>
    public static byte[] DamageFrame(int seed, int bodyLength = 40)
    {
        var body = new byte[bodyLength];
        body[0] = 0x04;
        body[1] = 0x38;
        var rng = new Random(seed);
        for (int i = 2; i < body.Length; i++) body[i] = (byte)rng.Next(1, 256);
        return SyntheticPacketBuilder.GameFrame(body);
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
