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
    /// <summary>Game flows locked during the replay (= <see cref="Flows"/>, kept for compatibility).</summary>
    public int Locks { get; init; }
    /// <summary>Game flows followed during the replay.</summary>
    public int Flows => FlowDetails.Count > 0 ? FlowDetails.Count : Locks;
    /// <summary>Largest number of game flows that were open at the same time.</summary>
    public int MaxConcurrentFlows { get; init; }
    /// <summary>Every game flow of the replay (server, client, bytes, gaps, why it ended), in lock order.</summary>
    public IReadOnlyList<CaptureFlowInfo> FlowDetails { get; init; } = Array.Empty<CaptureFlowInfo>();
    public long TlsFlowsIgnored { get; init; }
    public long BadLines { get; init; }
    /// <summary>Bytes skipped by TCP gap give-ups (lost data) over all flows.</summary>
    public long SkippedBytes { get; init; }
    /// <summary>Bytes of game flows a single-stream sink could not take (only with a plain <see cref="IStreamSink"/>;
    /// 0 with a flow-aware <see cref="IStreamSinkFactory"/>).</summary>
    public long DroppedBytes { get; init; }
    /// <summary>Packets that were read out of timestamp order and delivered after re-sorting.</summary>
    public long ReorderedPackets { get; init; }
    /// <summary>Server endpoint of the last locked flow (hex logs: synthetic endpoint per stream key).</summary>
    public string? LastServer { get; init; }
    public string? LastClient { get; init; }
    /// <summary>Distinct game server endpoints of all flows, joined with " + ".</summary>
    public string? Servers { get; init; }
    public DateTime? FirstPacketUtc { get; init; }
    public DateTime? LastPacketUtc { get; init; }
}

/// <summary>
/// Replays a capture file through the same flow detection (heartbeat signature / port 13328) and TCP reassembly as the
/// live service. Every game flow of the file (e.g. the world and the dungeon-instance connection, LIVE-FINDINGS NEW 1) is
/// followed concurrently and delivered to its own sink when the sink is an <see cref="IStreamSinkFactory"/>; packets
/// of all flows are processed interleaved in capture-timestamp order (a short re-sort window absorbs out-of-order
/// records, e.g. several pcapng interfaces). Supports .pcap/.pcapng (managed reader) and taengu/RATmeter hex line logs
/// (.log/.txt, see <see cref="HexLogReader"/>). Emits <see cref="DiscontinuityReason.ReplayStarted"/> first (session-wide).
/// All sink calls happen on one dedicated thread, in capture order. At the end of the file every open flow is flushed
/// and released.
/// </summary>
public sealed class PcapReplaySource : IReplaySource
{
    /// <summary>Default re-sort window for out-of-order capture records.</summary>
    public static readonly TimeSpan DefaultReorderWindow = TimeSpan.FromSeconds(2);

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
    /// <param name="sink">An <see cref="IStreamSink"/> that is also an <see cref="IStreamSinkFactory"/> gets one sink per game
    /// flow (session discontinuities on itself); a plain sink gets one flow at a time (<see cref="SingleSinkFlowAdapter"/>).</param>
    /// <param name="speed">1 = real time, 2 = twice as fast, 0 (or less) = as fast as possible.</param>
    /// <param name="clock">Called with the (monotonic) capture timestamp after every packet.</param>
    public static ReplayStatistics Replay(string path, IStreamSink sink, double speed = 0, Action<DateTime>? clock = null,
        CancellationToken cancellationToken = default, FlowTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return Run(path, new GameFlowTracker(sink, options), speed, clock, cancellationToken);
    }

    /// <summary>Synchronous multi-flow replay into a flow-aware factory. <paramref name="sessionSink"/> (optional) receives
    /// <see cref="DiscontinuityReason.ReplayStarted"/>.</summary>
    public static ReplayStatistics Replay(string path, IStreamSinkFactory factory, IStreamSink? sessionSink, double speed = 0,
        Action<DateTime>? clock = null, CancellationToken cancellationToken = default, FlowTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Run(path, new GameFlowTracker(factory, options, sessionSink), speed, clock, cancellationToken);
    }

    private static ReplayStatistics Run(string path, GameFlowTracker tracker, double speed, Action<DateTime>? clock, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Replay file not found.", path);

        var run = new ReplayRun(tracker, speed, clock, cancellationToken);
        tracker.OnSessionDiscontinuity(DiscontinuityReason.ReplayStarted);

        string format;
        long badLines = 0, reordered = 0;
        if (IsHexLogPath(path) || !PcapFileReader.IsCaptureFile(path))
        {
            format = "hexlog";
            badLines = ReplayHexLog(path, run);
        }
        else
        {
            using var reader = new PcapFileReader(path);
            format = reader.Format == CaptureFileFormat.PcapNg ? "pcapng" : "pcap";
            reordered = ReplayPackets(reader, run, DefaultReorderWindow);
        }

        var end = run.LastUtc ?? DateTime.UnixEpoch;
        tracker.TryFallbackLock(end);
        tracker.Flush(end);
        var lastLock = tracker.CurrentLock;
        int maxConcurrent = tracker.MaxConcurrentFlows;
        tracker.UnlockAll(end, "end of capture file");
        var flows = tracker.GetFlows().OrderBy(f => f.LockedAtUtc).ToList();
        long dropped = (tracker.Factory as SingleSinkFlowAdapter)?.DroppedBytes ?? 0;
        // The last locked flow: the newest one still open at the end, else the newest one that ended.
        var newest = flows.Count == 0 ? null : flows.OrderBy(f => f.LockedAtUtc).Last();
        string? lastServer = lastLock?.Server.ToString() ?? newest?.ServerEndpoint;
        string? lastClient = lastLock?.Client.ToString() ?? newest?.LocalEndpoint;

        string? servers = flows.Count == 0 ? null : string.Join(" + ", flows.Select(f => f.ServerEndpoint).Distinct());
        AppLog.Info("Capture", $"Replay of {System.IO.Path.GetFileName(path)} done: {tracker.PacketsSeen} packets, " +
                               $"{tracker.BytesDelivered} bytes delivered, {tracker.GapCount} gaps, {tracker.LockCount} flows " +
                               $"(max {maxConcurrent} concurrent)");
        return new ReplayStatistics
        {
            Path = path,
            Format = format,
            Packets = tracker.PacketsSeen,
            TcpPackets = tracker.TcpPackets,
            BytesDelivered = tracker.BytesDelivered,
            Gaps = tracker.GapCount,
            Locks = tracker.LockCount,
            MaxConcurrentFlows = maxConcurrent,
            FlowDetails = flows,
            TlsFlowsIgnored = tracker.TlsFlowsIgnored,
            BadLines = badLines,
            SkippedBytes = tracker.SkippedBytes,
            DroppedBytes = dropped,
            ReorderedPackets = reordered,
            LastServer = lastServer,
            LastClient = lastClient,
            Servers = servers,
            FirstPacketUtc = run.FirstUtc,
            LastPacketUtc = run.LastUtc,
        };
    }

    /// <summary>Feeds the packets in capture-timestamp order: records are held in a min-heap until the newest timestamp
    /// read is <paramref name="window"/> ahead of them (stable for equal timestamps, i.e. file order). Returns the number of
    /// records that were read out of order.</summary>
    private static long ReplayPackets(PcapFileReader reader, ReplayRun run, TimeSpan window)
    {
        var heap = new PriorityQueue<CapturedPacket, (long Ticks, long Seq)>();
        long seq = 0, reordered = 0;
        long newest = long.MinValue;
        while (reader.TryReadNext(out var packet))
        {
            long ticks = packet.TimestampUtc.Ticks;
            if (ticks < newest) reordered++;
            else newest = ticks;
            heap.Enqueue(packet, (ticks, seq++));
            while (heap.TryPeek(out _, out var key) && key.Ticks <= newest - window.Ticks)
                Process(run, heap.Dequeue());
        }

        while (heap.Count > 0) Process(run, heap.Dequeue());
        return reordered;

        static void Process(ReplayRun run, CapturedPacket p)
        {
            run.Before(p.TimestampUtc);
            run.Tracker.OnPacket(p.TimestampUtc, p.LinkType, p.Data.Span, p.InterfaceId);
            run.After(p.TimestampUtc);
        }
    }

    public static bool IsHexLogPath(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hex logs carry only server→client payloads keyed by a stream key. Each key becomes a synthetic TCP flow
    /// (server 10.255.255.1:13328 → client 10.254.x.y:port) with consecutive sequence numbers, so detection and
    /// reassembly run unchanged (several keys = several concurrent flows).</summary>
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
