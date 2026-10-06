using System.Net;
using System.Net.Sockets;

namespace Aion2Dps.Capture;

/// <summary>An IPv4 address + TCP port. <see cref="Address"/> holds the address in network order as an integer
/// (a.b.c.d → a&lt;&lt;24 | b&lt;&lt;16 | c&lt;&lt;8 | d).</summary>
public readonly record struct IPv4Endpoint(uint Address, ushort Port) : IComparable<IPv4Endpoint>
{
    public static IPv4Endpoint Parse(string text)
    {
        if (!TryParse(text, out var ep)) throw new FormatException($"Not an IPv4 endpoint: '{text}'");
        return ep;
    }

    public static bool TryParse(string? text, out IPv4Endpoint endpoint)
    {
        endpoint = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!IPEndPoint.TryParse(text.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        endpoint = FromIPEndPoint(ip);
        return true;
    }

    public static IPv4Endpoint FromIPEndPoint(IPEndPoint endPoint)
    {
        if (endPoint.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 endpoints are supported.", nameof(endPoint));
        return new IPv4Endpoint(AddressToUInt(endPoint.Address), (ushort)endPoint.Port);
    }

    public static uint AddressToUInt(IPAddress address)
    {
        Span<byte> b = stackalloc byte[4];
        if (!address.TryWriteBytes(b, out int written) || written != 4)
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public IPAddress IPAddress => new(new[] { (byte)(Address >> 24), (byte)(Address >> 16), (byte)(Address >> 8), (byte)Address });

    public IPEndPoint ToIPEndPoint() => new(IPAddress, Port);

    public bool IsLoopback => (Address >> 24) == 127;

    public string AddressString => $"{Address >> 24}.{(Address >> 16) & 0xFF}.{(Address >> 8) & 0xFF}.{Address & 0xFF}";

    public override string ToString() => $"{AddressString}:{Port}";

    public int CompareTo(IPv4Endpoint other)
    {
        int c = Address.CompareTo(other.Address);
        return c != 0 ? c : Port.CompareTo(other.Port);
    }
}

/// <summary>Direction-independent TCP 4-tuple (endpoints stored in canonical order, <see cref="A"/> ≤ <see cref="B"/>).</summary>
public readonly record struct TcpFlowKey
{
    public IPv4Endpoint A { get; }
    public IPv4Endpoint B { get; }

    private TcpFlowKey(IPv4Endpoint a, IPv4Endpoint b) { A = a; B = b; }

    public static TcpFlowKey Create(IPv4Endpoint x, IPv4Endpoint y) => x.CompareTo(y) <= 0 ? new(x, y) : new(y, x);

    public bool Contains(IPv4Endpoint ep) => A == ep || B == ep;

    /// <summary>The endpoint that is not <paramref name="ep"/>.</summary>
    public IPv4Endpoint Other(IPv4Endpoint ep) => A == ep ? B : A;

    public override string ToString() => $"{A} <-> {B}";
}

/// <summary>A known game connection: <see cref="Client"/> is the local side, <see cref="Server"/> the game server.</summary>
public readonly record struct FlowHint(IPv4Endpoint Client, IPv4Endpoint Server)
{
    public TcpFlowKey Key => TcpFlowKey.Create(Client, Server);
    public override string ToString() => $"{Client} -> {Server}";
}
