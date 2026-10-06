using System.Diagnostics;
using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>Result of one replay.</summary>
public sealed record ReplayStatistics
{
    public string Path { get; init; } = "";
    /// <summary>"pcap", "pcapng" or "hexlog".</summary>
    public string Format { get; init; } = "";
    public long Packets { get; init; }
    public long TcpPackets { get; init; }
    public long BytesDelivered { get; init; }
    public long Gaps { get; init; }
    public int Locks { get; init; }
    public long TlsFlowsIgnored { get; init; }
    public long BadLines { get; init; }
    /// <summary>Server endpoint of the last locked flow (hex logs: synthetic endpoint per stream key).</summary>
    public string? LastServer { get; init; }
    public string? LastClient { get; init; }
    public DateTime? FirstPacketUtc { get; init; }
    public DateTime? LastPacketUtc { get; init; }
}

/// <summary>
/// Replays a capture file through the same flow detection (heartbeat signature / port 13328) and TCP reassembly as the
/// live service. Supports .pcap/.pcapng (managed reader) and taengu/RATmeter hex line logs (.log/.txt, see
/// <see cref="HexLogReader"/>). Emits <see cref="DiscontinuityReason.ReplayStarted"/> first. All sink calls happen on
/// one dedicated thread, in stream order.
/// </summary>
public sealed class PcapReplaySource : IReplaySource
{
    private readonly FlowTrackerOptions? _options;

    public PcapReplaySource(FlowTrackerOptions? options = null) => _options = options;

    /// <summary>Statistics of the most recently finished replay.</summary>
    public ReplayStatistics? LastStatistics { get; private set; }

    public Task ReplayAsync(string path, IStreamSink sink, double speed, Action<DateTime>? clock, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(sink);
        return Task.Factory.StartNew(() =>
        {
            LastStatistics = Replay(path, sink, speed, clock, cancellationToken, _options);
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Synchronous replay on the calling thread. Throws <see cref="OperationCanceledException"/> on cancellation.</summary>
    /// <param name="speed">1 = real time, 2 = twice as fast, 0 (or less) = as fast as possible.</param>
    /// <param name="clock">Called with the (monotonic) capture timestamp after every packet.</param>
    public static ReplayStatistics Replay(string path, IStreamSink sink, double speed = 0, Action<DateTime>? clock = null,
        CancellationToken cancellationToken = default, FlowTrackerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(sink);
        if (!File.Exists(path)) throw new FileNotFoundException("Replay file not found.", path);

        var guard = new StreamSinkGuard(sink);
        var tracker = new GameFlowTracker(guard, options);
        var run = new ReplayRun(tracker, speed, clock, cancellationToken);
        guard.OnDiscontinuity(DiscontinuityReason.ReplayStarted);

        string format;
        long badLines = 0;
        if (IsHexLogPath(path) || !PcapFileReader.IsCaptureFile(path))
        {
            format = "hexlog";
            badLines = ReplayHexLog(path, run);
        }
        else
        {
            using var reader = new PcapFileReader(path);
            format = reader.Format == CaptureFileFormat.PcapNg ? "pcapng" : "pcap";
            while (reader.TryReadNext(out var packet))
            {
                run.Before(packet.TimestampUtc);
                tracker.OnPacket(packet.TimestampUtc, packet.LinkType, packet.Data.Span, packet.InterfaceId);
                run.After(packet.TimestampUtc);
            }
        }

        var end = run.LastUtc ?? DateTime.UnixEpoch;
        tracker.TryFallbackLock(end);
        tracker.Flush(end);
        var lockInfo = tracker.CurrentLock;
        AppLog.Info("Capture", $"Replay of {System.IO.Path.GetFileName(path)} done: {tracker.PacketsSeen} packets, " +
                               $"{guard.BytesDelivered} bytes delivered, {guard.GapCount} gaps, {tracker.LockCount} locks");
        return new ReplayStatistics
        {
            Path = path,
            Format = format,
            Packets = tracker.PacketsSeen,
            TcpPackets = tracker.TcpPackets,
            BytesDelivered = guard.BytesDelivered,
            Gaps = guard.GapCount,
            Locks = tracker.LockCount,
            TlsFlowsIgnored = tracker.TlsFlowsIgnored,
            BadLines = badLines,
            LastServer = lockInfo?.Server.ToString(),
            LastClient = lockInfo?.Client.ToString(),
            FirstPacketUtc = run.FirstUtc,
            LastPacketUtc = run.LastUtc,
        };
    }

    public static bool IsHexLogPath(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hex logs carry only server→client payloads keyed by a stream key. Each key becomes a synthetic TCP flow
    /// (server 10.255.255.1:13328 → client 10.254.x.y:port) with consecutive sequence numbers, so detection and
    /// reassembly run unchanged.</summary>
    private static long ReplayHexLog(string path, ReplayRun run)
    {
        var flows = new Dictionary<string, SyntheticFlow>(StringComparer.Ordinal);
        var server = new IPv4Endpoint(0x0AFFFF01, GameSignature.GameServerPort);
        long bad = 0;
        foreach (var e in HexLogReader.ReadFile(path, (_, _) => bad++))
        {
            if (!flows.TryGetValue(e.StreamKey, out var flow))
            {
                int index = flows.Count + 1;
                ushort port = TryPortFromKey(e.StreamKey, out var p) && p != GameSignature.GameServerPort ? p : (ushort)(50000 + index % 10000);
                var client = new IPv4Endpoint(0x0AFE0000u | (uint)(index & 0xFFFF), port);
                flow = new SyntheticFlow(client, 1_000_000u * (uint)index);
                flows[e.StreamKey] = flow;
            }
            run.Before(e.TimestampUtc);
            var seg = new TcpSegment(server, flow.Client, flow.NextSeq, 1, TcpFlags.Ack | TcpFlags.Psh, e.Payload, 0);
            flow.NextSeq = unchecked(flow.NextSeq + (uint)e.Payload.Length);
            run.Tracker.OnSegment(e.TimestampUtc, seg, sourceId: 0);
            run.After(e.TimestampUtc);
        }
        return bad;
    }

    private static bool TryPortFromKey(string key, out ushort port)
    {
        port = 0;
        int colon = key.LastIndexOf(':');
        var digits = colon >= 0 ? key.AsSpan(colon + 1) : key.AsSpan();
        return ushort.TryParse(digits.Trim(), out port) && port != 0;
    }

    private sealed class SyntheticFlow(IPv4Endpoint client, uint seq)
    {
        public IPv4Endpoint Client { get; } = client;
        public uint NextSeq { get; set; } = seq;
    }

    /// <summary>Pacing, cancellation, clock callback.</summary>
    private sealed class ReplayRun(GameFlowTracker tracker, double speed, Action<DateTime>? clock, CancellationToken ct)
    {
        private readonly Stopwatch _watch = new();
        private long _clockErrors;

        public GameFlowTracker Tracker { get; } = tracker;
        public DateTime? FirstUtc { get; private set; }
        public DateTime? LastUtc { get; private set; }

        public void Before(DateTime ts)
        {
            ct.ThrowIfCancellationRequested();
            if (FirstUtc is null)
            {
                FirstUtc = ts;
                _watch.Start();
                return;
            }
            if (speed <= 0 || double.IsNaN(speed)) return;
            var target = TimeSpan.FromTicks((long)Math.Min((ts - FirstUtc.Value).Ticks / speed, TimeSpan.MaxValue.Ticks / 2.0));
            var wait = target - _watch.Elapsed;
            while (wait > TimeSpan.FromMilliseconds(1))
            {
                var chunk = wait > TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : wait;
                if (ct.WaitHandle.WaitOne(chunk)) ct.ThrowIfCancellationRequested();
                wait = target - _watch.Elapsed;
            }
        }

        public void After(DateTime ts)
        {
            if (LastUtc is null || ts > LastUtc) LastUtc = ts;
            Tracker.Tick(LastUtc.Value);
            if (clock is null) return;
            try
            {
                clock(LastUtc.Value);
            }
            catch (Exception ex)
            {
                if (++_clockErrors <= 3) AppLog.Error("Capture", "Replay clock callback threw", ex);
            }
        }
    }
}
