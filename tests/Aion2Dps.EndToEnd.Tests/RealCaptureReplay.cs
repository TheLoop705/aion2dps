using Aion2Dps.Capture;
using Aion2Dps.Contracts;
using Aion2Dps.Protocol;

namespace Aion2Dps.EndToEnd.Tests;

/// <summary>
/// Locates the user's real capture files (never committed: they contain player names) and replays them through the
/// protocol layer with one TCP reassembler and one <see cref="FrameDecoder"/> per server→client port-13328 flow, all
/// feeding one shared <see cref="PacketDecoder"/> (LIVE-FINDINGS NEW 1: the game uses several concurrent connections,
/// and entity ids are shared between them). This deliberately does not use the capture module's single-flow lock so
/// that the protocol checks are independent of it.
/// </summary>
internal static class RealCaptureReplay
{
    public const string EnvVar = "AION2DPS_REAL_CAPTURES";

    public const string ExpeditionFile = "expedition-2026-10-06.pcap";
    public const string LiveFile = "real1.pcapng";

    private static IEnumerable<string> DefaultLocations()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                yield return Path.Combine(directory.FullName, "captures");
    }

    /// <summary>
    /// Full path of a real capture with this file name, or null. <c>AION2DPS_REAL_CAPTURES</c> may list files and/or
    /// directories separated by ';'. Without the variable the default locations of the developer machine are tried.
    /// </summary>
    public static string? Find(string fileName)
    {
        var env = Environment.GetEnvironmentVariable(EnvVar);
        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(env)
            ? DefaultLocations().Distinct(StringComparer.OrdinalIgnoreCase)
            : env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var c in candidates)
        {
            try
            {
                if (Directory.Exists(c))
                {
                    var p = Path.Combine(c, fileName);
                    if (File.Exists(p)) return p;
                }
                else if (File.Exists(c) && string.Equals(Path.GetFileName(c), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }
            catch (Exception)
            {
                // unreadable location: try the next one
            }
        }

        return null;
    }

    public sealed class FlowStats
    {
        public required string Server { get; init; }
        public required string Client { get; init; }
        public long Segments { get; set; }
        public long Bytes => Reassembler.BytesDelivered;
        public long Gaps => Reassembler.GapCount;
        public required TcpReassembler Reassembler { get; init; }
        public required FrameDecoder Decoder { get; init; }
        public bool IgnoredTls { get; set; }
    }

    public sealed class Result
    {
        public required ProtocolDiagnostics Diagnostics { get; init; }
        public required IReadOnlyList<FlowStats> Flows { get; init; }
        public long Packets { get; init; }
        public DateTime FirstUtc { get; init; }
        public DateTime LastUtc { get; init; }
        public long Gaps => Flows.Sum(f => f.Gaps);
    }

    /// <summary>Replays every server→client flow from port 13328 of <paramref name="path"/> into <paramref name="sink"/>.</summary>
    /// <param name="clock">Called with each packet's capture time after it was processed.</param>
    public static Result Run(string path, IGameEventSink sink, ProtocolDiagnostics? diagnostics = null, Action<DateTime>? clock = null)
    {
        var diag = diagnostics ?? new ProtocolDiagnostics(4096);
        var ops = OpcodeTable.LoadOrDefault();
        var decoder = new PacketDecoder(sink, ops, diag);
        var flows = new Dictionary<(IPv4Endpoint, IPv4Endpoint), FlowStats>();
        long packets = 0;
        DateTime first = default, last = default;

        using var reader = new PcapFileReader(path);
        while (reader.TryReadNext(out var packet))
        {
            packets++;
            if (first == default) first = packet.TimestampUtc;
            if (packet.TimestampUtc > last) last = packet.TimestampUtc;
            if (!PacketParser.TryParse(packet.LinkType, packet.Data.Span, out var seg)) continue;
            if (seg.Source.Port != 13328) continue;

            var key = (seg.Source, seg.Destination);
            if (!flows.TryGetValue(key, out var flow))
            {
                if (seg.Payload.IsEmpty) continue;
                var frameDecoder = new FrameDecoder(decoder, ops, diag);
                flow = new FlowStats
                {
                    Server = seg.Source.ToString(), Client = seg.Destination.ToString(), Decoder = frameDecoder,
                    Reassembler = new TcpReassembler(frameDecoder),
                    IgnoredTls = LooksLikeTls(seg.Payload),
                };
                flows[key] = flow;
            }

            flow.Segments++;
            if (!flow.IgnoredTls) flow.Reassembler.Push(seg.Sequence, seg.Payload, packet.TimestampUtc);
            clock?.Invoke(last);
        }

        foreach (var f in flows.Values) f.Reassembler.Flush(last);
        return new Result
        {
            Diagnostics = diag, Flows = flows.Values.ToList(), Packets = packets, FirstUtc = first, LastUtc = last,
        };
    }

    private static bool LooksLikeTls(ReadOnlySpan<byte> p) =>
        p.Length >= 3 && p[0] is >= 0x14 and <= 0x17 && p[1] == 0x03 && p[2] <= 0x04;
}
