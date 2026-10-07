using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using Aion2Dps.Contracts;

namespace Aion2Dps.Capture.Tests;

/// <summary>Flow-aware test sink: one <see cref="CollectingSink"/> per flow, plus the session sink and the call threads.</summary>
internal sealed class RecordingFactory : IStreamSink, IStreamSinkFactory
{
    private readonly object _gate = new();
    public Dictionary<string, CollectingSink> Flows { get; } = new();
    public List<string> Created { get; } = new();
    public List<string> Released { get; } = new();
    public List<string> Log { get; } = new();
    public List<DiscontinuityReason> SessionDiscontinuities { get; } = new();
    public ConcurrentDictionary<int, int> Threads { get; } = new();
    public int ConcurrentCalls;
    private int _inCall;

    private void Enter()
    {
        Threads.AddOrUpdate(Environment.CurrentManagedThreadId, 1, (_, n) => n + 1);
        if (Interlocked.Increment(ref _inCall) > 1) Interlocked.Increment(ref ConcurrentCalls);
    }

    private void Exit() => Interlocked.Decrement(ref _inCall);

    public IStreamSink CreateFlowSink(string flowKey)
    {
        Enter();
        try
        {
            lock (_gate)
            {
                Created.Add(flowKey);
                Log.Add("create " + flowKey);
                var sink = new CollectingSink();
                Flows[flowKey + "#" + Created.Count(k => k == flowKey)] = sink;
                return new Tracked(this, flowKey, sink);
            }
        }
        finally { Exit(); }
    }

    public void ReleaseFlowSink(string flowKey)
    {
        Enter();
        try { lock (_gate) { Released.Add(flowKey); Log.Add("release " + flowKey); } }
        finally { Exit(); }
    }

    public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data) => throw new InvalidOperationException("flow-aware capture must not use the default stream");

    public void OnDiscontinuity(DiscontinuityReason reason)
    {
        Enter();
        try { lock (_gate) SessionDiscontinuities.Add(reason); }
        finally { Exit(); }
    }

    /// <summary>The sink of the n-th creation of <paramref name="flowKey"/>.</summary>
    public CollectingSink Of(string flowKey, int generation = 1) => Flows[flowKey + "#" + generation];

    public CollectingSink Of(SyntheticConnection c, int generation = 1) => Of($"{c.Server}>{c.Client}", generation);

    private sealed class Tracked(RecordingFactory owner, string key, CollectingSink inner) : IStreamSink
    {
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
        {
            owner.Enter();
            try { inner.OnData(timeUtc, data); lock (owner._gate) owner.Log.Add($"data {key} {data.Length}"); }
            finally { owner.Exit(); }
        }

        public void OnDiscontinuity(DiscontinuityReason reason)
        {
            owner.Enter();
            try { inner.OnDiscontinuity(reason); lock (owner._gate) owner.Log.Add($"{reason} {key}"); }
            finally { owner.Exit(); }
        }
    }
}

/// <summary>LIVE-FINDINGS NEW 1: the world and the dungeon-instance connection are open at the same time and both carry game data.</summary>
public class MultiFlowTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 19, 41, 0, DateTimeKind.Utc);

    private static string Key(SyntheticConnection c) => $"{c.Server}>{c.Client}";

    private static SyntheticConnection World() => new("192.168.178.81:53334", "87.232.75.150:13328", serverIsn: 10_000);
    private static SyntheticConnection Instance() => new("192.168.178.81:64331", "193.202.112.155:13328", serverIsn: 0xFFFF_FF00);

    [Fact]
    public void TwoConcurrentGameFlows_AreBothLocked_EachIntoItsOwnSink_WithPreLockReplay()
    {
        var f = new RecordingFactory();
        var tracker = new GameFlowTracker(f);
        var world = World();
        var inst = Instance();
        var expectWorld = new MemoryStream();
        var expectInst = new MemoryStream();
        var t = T0;

        tracker.OnPacket(t, LinkTypes.Ethernet, world.ClientSyn());
        tracker.OnPacket(t, LinkTypes.Ethernet, world.ServerSynAck());
        void W(byte[] p) { expectWorld.Write(p); tracker.OnPacket(t, LinkTypes.Ethernet, world.ServerSend(p)); }
        void I(byte[] p) { expectInst.Write(p); tracker.OnPacket(t, LinkTypes.Ethernet, inst.ServerSend(p)); }

        W(GameBytes.IdentityFrame);
        for (int i = 0; i < 4; i++) { t = t.AddMilliseconds(50); W(GameBytes.Heartbeat(i)); }
        Assert.Single(f.Created);

        // The instance connection opens later; its connect-time records arrive before its first heartbeat.
        tracker.OnPacket(t, LinkTypes.Ethernet, inst.ClientSyn());
        tracker.OnPacket(t, LinkTypes.Ethernet, inst.ServerSynAck());
        I(GameBytes.IdentityFrame);
        I(GameBytes.DamageFrame(1, 300));
        for (int i = 0; i < 20; i++)
        {
            t = t.AddMilliseconds(50);
            W(GameBytes.Heartbeat(100 + i));
            I(GameBytes.Concat(GameBytes.Heartbeat(200 + i), GameBytes.DamageFrame(10 + i, 40 + i * 7)));
            if (i % 5 == 0) W(GameBytes.DamageFrame(50 + i, 20)); // world-side noise
        }

        Assert.Equal(new[] { Key(world), Key(inst) }, f.Created);
        Assert.Equal(2, tracker.ActiveFlowCount);
        Assert.Equal(2, tracker.MaxConcurrentFlows);
        Assert.Equal(expectWorld.ToArray(), f.Of(world).Data);
        Assert.Equal(expectInst.ToArray(), f.Of(inst).Data);  // includes the buffered identity + damage from before the lock
        Assert.Empty(f.Of(world).Discontinuities);            // no NewConnection just because a second flow exists
        Assert.Empty(f.Of(inst).Discontinuities);
        Assert.Equal(expectWorld.Length + expectInst.Length, tracker.BytesDelivered);
        Assert.Empty(f.Released);
        Assert.Equal(0, f.ConcurrentCalls);
    }

    [Fact]
    public void TcpGap_AffectsOnlyThatFlow()
    {
        var f = new RecordingFactory();
        var tracker = new GameFlowTracker(f);
        var world = World();
        var inst = Instance();
        var t = T0;
        for (int i = 0; i < 40; i++)
        {
            t = t.AddMilliseconds(100);
            tracker.OnPacket(t, LinkTypes.Ethernet, world.ServerSend(GameBytes.Heartbeat(i)));
            var frame = inst.ServerSend(GameBytes.Concat(GameBytes.Heartbeat(1000 + i), GameBytes.DamageFrame(i, 60)));
            if (i == 20) continue; // lost on the wire
            tracker.OnPacket(t, LinkTypes.Ethernet, frame);
        }

        Assert.Equal(new[] { DiscontinuityReason.TcpGap }, f.Of(inst).Discontinuities);
        Assert.Empty(f.Of(world).Discontinuities);
        Assert.Equal(1, tracker.GapCount);
        Assert.Equal(40 * 11, f.Of(world).Data.Length);
        Assert.Equal(2, tracker.ActiveFlowCount);
    }

    [Fact]
    public void FlowEnds_OnFinBothWays_RstAndIdle_ReleasingOnlyThatSink()
    {
        var f = new RecordingFactory();
        var tracker = new GameFlowTracker(f);
        var world = World();
        var inst = Instance();
        var third = new SyntheticConnection("192.168.178.81:64400", "193.202.112.156:13328");
        var t = T0;
        for (int i = 0; i < 4; i++)
        {
            t = t.AddMilliseconds(50);
            tracker.OnPacket(t, LinkTypes.Ethernet, world.ServerSend(GameBytes.Heartbeat(i)));
            tracker.OnPacket(t, LinkTypes.Ethernet, inst.ServerSend(GameBytes.Heartbeat(i)));
            tracker.OnPacket(t, LinkTypes.Ethernet, third.ServerSend(GameBytes.Heartbeat(i)));
        }

        Assert.Equal(3, tracker.ActiveFlowCount);

        // Instance: orderly close (FIN both ways) → released at once.
        tracker.OnPacket(t, LinkTypes.Ethernet, inst.ServerSend(default, TcpFlags.Fin | TcpFlags.Ack));
        Assert.Equal(3, tracker.ActiveFlowCount);
        tracker.OnPacket(t, LinkTypes.Ethernet, inst.ClientSend(default, TcpFlags.Fin | TcpFlags.Ack));
        Assert.Equal(new[] { Key(inst) }, f.Released);
        // Its final ACK lingers as an ended flow: no new candidate, no new lock.
        tracker.OnPacket(t, LinkTypes.Ethernet, inst.ServerSend(default, TcpFlags.Ack));
        Assert.Equal(0, tracker.CandidateCount);

        // Third: RST → released at once.
        tracker.OnPacket(t, LinkTypes.Ethernet, third.ServerSend(default, TcpFlags.Rst));
        Assert.Equal(new[] { Key(inst), Key(third) }, f.Released);
        Assert.Equal(1, tracker.ActiveFlowCount);

        // World: idle → released after 90 s of silence (capture clock).
        tracker.Tick(t.AddSeconds(89));
        Assert.Equal(1, tracker.ActiveFlowCount);
        tracker.Tick(t.AddSeconds(91));
        Assert.Equal(new[] { Key(inst), Key(third), Key(world) }, f.Released);
        Assert.Null(tracker.CurrentLock);

        var flows = tracker.GetFlows();
        Assert.Equal(3, flows.Count);
        Assert.All(flows, fl => Assert.False(fl.IsOpen));
        Assert.Contains(flows, fl => fl.EndReason!.Contains("FIN"));
        Assert.Contains(flows, fl => fl.EndReason!.Contains("RST"));
        Assert.Contains(flows, fl => fl.EndReason!.Contains("90 s"));
    }

    [Fact]
    public void OneSidedFin_EndsAfterTheGracePeriod_AndAReconnectGetsAFreshSink()
    {
        var f = new RecordingFactory();
        var tracker = new GameFlowTracker(f);
        var game = World();
        var t = T0;
        for (int i = 0; i < 3; i++) tracker.OnPacket(t = t.AddMilliseconds(50), LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(i)));
        tracker.OnPacket(t, LinkTypes.Ethernet, game.ServerSend(default, TcpFlags.Fin | TcpFlags.Ack));
        Assert.True(tracker.LockedFlowClosed);
        tracker.Tick(t.AddSeconds(1));
        Assert.Empty(f.Released);
        tracker.Tick(t.AddSeconds(2.5));
        Assert.Equal(new[] { Key(game) }, f.Released);

        // The same 4-tuple is reused by a new connection (SYN) shortly after: it is a new flow with a new sink.
        var again = new SyntheticConnection(game.Client.ToString(), game.Server.ToString(), serverIsn: 9_000_000);
        tracker.OnPacket(t.AddSeconds(3), LinkTypes.Ethernet, again.ClientSyn());
        tracker.OnPacket(t.AddSeconds(3), LinkTypes.Ethernet, again.ServerSynAck());
        for (int i = 0; i < 3; i++) tracker.OnPacket(t.AddSeconds(3.1 + i * 0.05), LinkTypes.Ethernet, again.ServerSend(GameBytes.Heartbeat(i)));
        Assert.Equal(new[] { Key(game), Key(game) }, f.Created);
        Assert.Equal(33, f.Of(Key(game), 2).Data.Length);
        Assert.Empty(f.Of(Key(game), 2).Discontinuities);
    }

    [Fact]
    public void NewSynOnALockedFlow_RestartsOnlyThatFlowsStream()
    {
        var f = new RecordingFactory();
        var tracker = new GameFlowTracker(f);
        var world = World();
        var inst = Instance();
        var t = T0;
        for (int i = 0; i < 3; i++)
        {
            tracker.OnPacket(t, LinkTypes.Ethernet, world.ServerSend(GameBytes.Heartbeat(i)));
            tracker.OnPacket(t, LinkTypes.Ethernet, inst.ServerSend(GameBytes.Heartbeat(i)));
        }

        var restarted = new SyntheticConnection(inst.Client.ToString(), inst.Server.ToString(), serverIsn: 123_456);
        tracker.OnPacket(t, LinkTypes.Ethernet, restarted.ServerSynAck());
        tracker.OnPacket(t, LinkTypes.Ethernet, restarted.ServerSend(GameBytes.IdentityFrame));
        Assert.Equal(new[] { DiscontinuityReason.NewConnection }, f.Of(inst).Discontinuities);
        Assert.Empty(f.Of(world).Discontinuities);
        Assert.EndsWith(Convert.ToHexString(GameBytes.IdentityFrame), Convert.ToHexString(f.Of(inst).Data));
    }

    [Fact]
    public void ProcessHints_LockEveryHintedFlowOnItsFirstHeartbeat()
    {
        var f = new RecordingFactory();
        var tracker = new GameFlowTracker(f);
        var world = World();
        var inst = Instance();
        tracker.SetHints(new[] { new FlowHint(world.Client, world.Server), new FlowHint(inst.Client, inst.Server) });
        tracker.OnPacket(T0, LinkTypes.Ethernet, world.ServerSend(GameBytes.Heartbeat(0)));
        tracker.OnPacket(T0, LinkTypes.Ethernet, inst.ServerSend(GameBytes.Heartbeat(0)));
        Assert.Equal(2, tracker.ActiveFlowCount);
        Assert.All(tracker.ActiveLocks, l => Assert.True(l.ByHint));
    }

    [Fact]
    public void MaxConcurrentFlows_CapsTheNumberOfOpenFlows()
    {
        var f = new RecordingFactory();
        var tracker = new GameFlowTracker(f, new FlowTrackerOptions { MaxConcurrentFlows = 2 });
        var conns = Enumerable.Range(0, 3).Select(i => new SyntheticConnection($"10.0.0.2:{50000 + i}", $"193.202.112.{100 + i}:13328")).ToList();
        for (int i = 0; i < 3; i++)
            foreach (var c in conns) tracker.OnPacket(T0.AddMilliseconds(i * 50), LinkTypes.Ethernet, c.ServerSend(GameBytes.Heartbeat(i)));
        Assert.Equal(2, tracker.ActiveFlowCount);
        Assert.Equal(2, f.Created.Count);
    }

    [Fact]
    public void Recorder_WritesEveryGameFlow_BothDirections_NotOtherTraffic()
    {
        var rec = new List<byte[]>();
        var tracker = new GameFlowTracker(new RecordingFactory()) { Recorder = new ListRecorder(rec) };
        var world = World();
        var inst = Instance();
        var other = new SyntheticConnection("192.168.178.81:50001", "1.2.3.4:9999");
        var expected = new List<byte[]>();
        void Feed(byte[] frame, bool game)
        {
            if (game) expected.Add(frame);
            tracker.OnPacket(T0, LinkTypes.Ethernet, frame);
        }

        Feed(world.ClientSyn(), true);
        Feed(world.ServerSynAck(), true);
        Feed(inst.ClientSyn(), true);
        Feed(inst.ServerSynAck(), true);
        for (int i = 0; i < 4; i++)
        {
            Feed(world.ServerSend(GameBytes.Heartbeat(i)), true);
            Feed(inst.ServerSend(GameBytes.Heartbeat(i)), true);
            Feed(inst.ClientSend(new byte[] { 1, 2, (byte)i }), true);
            Feed(other.ServerSend(new byte[] { 5, 5, 5 }), false);
        }

        Feed(world.ClientSend(new byte[] { 9 }), true);
        // Pre-lock buffers are written at lock time, so compare as multisets.
        Assert.Equal(expected.Count, rec.Count);
        Assert.Equal(expected.Select(Convert.ToHexString).OrderBy(x => x), rec.Select(Convert.ToHexString).OrderBy(x => x));
    }

    private sealed class ListRecorder(List<byte[]> list) : IPacketRecorder
    {
        public void WritePacket(DateTime timestampUtc, int linkType, ReadOnlySpan<byte> frame) => list.Add(frame.ToArray());
    }
}

/// <summary>Replay of a capture with two concurrent game flows.</summary>
public class MultiFlowReplayTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 6, 19, 41, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aion2dps-mf-replay", Guid.NewGuid().ToString("N"));

    public MultiFlowReplayTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static (List<(DateTime Ts, byte[] Frame)> Frames, byte[] World, byte[] Instance) Build()
    {
        var frames = new List<(DateTime, byte[])>();
        var world = new SyntheticConnection("192.168.178.81:53334", "87.232.75.150:13328");
        var inst = new SyntheticConnection("192.168.178.81:64331", "193.202.112.155:13328", serverIsn: 0xFFFF_F000);
        var w = new MemoryStream();
        var n = new MemoryStream();
        var t = T0;
        frames.Add((t, world.ClientSyn()));
        frames.Add((t, world.ServerSynAck()));
        w.Write(GameBytes.IdentityFrame);
        frames.Add((t, world.ServerSend(GameBytes.IdentityFrame)));
        for (int i = 0; i < 200; i++)
        {
            t = T0.AddMilliseconds(50 * (i + 1));
            var hb = GameBytes.Heartbeat(i);
            w.Write(hb);
            frames.Add((t, world.ServerSend(hb)));
            if (i == 40)
            {
                frames.Add((t, inst.ClientSyn()));
                frames.Add((t, inst.ServerSynAck()));
            }

            if (i > 40)
            {
                var p = GameBytes.Concat(GameBytes.Heartbeat(5000 + i), GameBytes.DamageFrame(i, 30 + i % 300));
                n.Write(p);
                frames.Add((t, inst.ServerSend(p)));
            }
        }

        frames.Add((t, inst.ServerSend(default, TcpFlags.Fin | TcpFlags.Ack)));
        frames.Add((t, inst.ClientSend(default, TcpFlags.Fin | TcpFlags.Ack)));
        return (frames, w.ToArray(), n.ToArray());
    }

    private string Write(string name, CaptureFileFormat format, IEnumerable<(DateTime Ts, byte[] Frame)> frames)
    {
        string path = Path.Combine(_dir, name);
        using var wr = PcapFileWriter.Create(path, format);
        foreach (var (ts, f) in frames) wr.WritePacket(ts, LinkTypes.Ethernet, f);
        return path;
    }

    [Theory]
    [InlineData(CaptureFileFormat.Pcap)]
    [InlineData(CaptureFileFormat.PcapNg)]
    public void Replay_FollowsBothConcurrentFlows_AndReportsThem(CaptureFileFormat format)
    {
        var (frames, world, inst) = Build();
        string path = Write("two." + (format == CaptureFileFormat.Pcap ? "pcap" : "pcapng"), format, frames);
        var f = new RecordingFactory();
        var stats = PcapReplaySource.Replay(path, f);

        Assert.Equal(new[] { DiscontinuityReason.ReplayStarted }, f.SessionDiscontinuities);
        Assert.Equal(2, stats.Flows);
        Assert.Equal(2, stats.MaxConcurrentFlows);
        Assert.Equal("87.232.75.150:13328 + 193.202.112.155:13328", stats.Servers);
        Assert.Equal(world, f.Of("87.232.75.150:13328>192.168.178.81:53334").Data);
        Assert.Equal(inst, f.Of("193.202.112.155:13328>192.168.178.81:64331").Data);
        Assert.Equal(world.Length + inst.Length, stats.BytesDelivered);
        Assert.Equal(0, stats.Gaps);
        Assert.Equal(0, stats.DroppedBytes);
        Assert.Equal(2, f.Released.Count); // instance by FIN, world at the end of the file
        Assert.Equal("connection closed (FIN)", stats.FlowDetails.Single(d => d.ServerEndpoint.StartsWith("193.")).EndReason);
        Assert.Equal("end of capture file", stats.FlowDetails.Single(d => d.ServerEndpoint.StartsWith("87.")).EndReason);
        Assert.Single(f.Threads); // every sink call on the replay thread

        // Delivery is interleaved in capture order: after both flows are locked, data calls alternate between flows.
        var dataLog = f.Log.Where(l => l.StartsWith("data ")).Select(l => l.Split(' ')[1]).ToList();
        int switches = dataLog.Zip(dataLog.Skip(1)).Count(p => p.First != p.Second);
        Assert.True(switches > 100, $"only {switches} switches between flows");
    }

    [Fact]
    public void Replay_ResortsOutOfOrderRecords_ByTimestamp()
    {
        var (frames, world, inst) = Build();
        // Swap neighbouring records (as two pcapng interfaces could): file order is no longer time order.
        var shuffled = frames.ToList();
        for (int i = 10; i + 1 < shuffled.Count; i += 7) (shuffled[i], shuffled[i + 1]) = (shuffled[i + 1], shuffled[i]);
        string path = Write("shuffled.pcapng", CaptureFileFormat.PcapNg, shuffled);
        var f = new RecordingFactory();
        var clock = new List<DateTime>();
        var stats = PcapReplaySource.Replay(path, f, null, 0, clock.Add);
        Assert.True(stats.ReorderedPackets > 0);
        Assert.Equal(world, f.Of("87.232.75.150:13328>192.168.178.81:53334").Data);
        Assert.Equal(inst, f.Of("193.202.112.155:13328>192.168.178.81:64331").Data);
        Assert.Equal(frames.Count, clock.Count);
        Assert.True(clock.Zip(clock.Skip(1)).All(p => p.First <= p.Second));
    }

    [Fact]
    public void Replay_WithPlainSink_StillWorks_NewestFlowWins()
    {
        var (frames, world, inst) = Build();
        string path = Write("plain.pcap", CaptureFileFormat.Pcap, frames);
        var sink = new CollectingSink();
        var stats = PcapReplaySource.Replay(path, sink);
        Assert.Equal(2, stats.Flows);
        Assert.True(stats.DroppedBytes > 0);
        Assert.Equal(DiscontinuityReason.ReplayStarted, sink.Discontinuities[0]);
        Assert.Contains(DiscontinuityReason.NewConnection, sink.Discontinuities);
        // The instance stream reaches the single sink completely (it is the newest flow while it is open).
        Assert.Contains(Convert.ToHexString(inst), Convert.ToHexString(sink.Data));
    }

    [Fact]
    public void HexLog_WithTwoStreamKeys_GivesTwoFlows()
    {
        var sw = new StringWriter();
        var a = new MemoryStream();
        var b = new MemoryStream();
        for (int i = 0; i < 10; i++)
        {
            var pa = GameBytes.Concat(GameBytes.Heartbeat(i), GameBytes.DamageFrame(i, 30));
            var pb = GameBytes.Concat(GameBytes.Heartbeat(100 + i), GameBytes.DamageFrame(100 + i, 50));
            a.Write(pa);
            b.Write(pb);
            HexLogReader.WriteLine(sw, T0.AddMilliseconds(i * 50), "Client:62311", pa);
            HexLogReader.WriteLine(sw, T0.AddMilliseconds(i * 50 + 10), "Client:62400", pb);
        }

        string path = Path.Combine(_dir, "two.log");
        File.WriteAllText(path, sw.ToString());
        var f = new RecordingFactory();
        var stats = PcapReplaySource.Replay(path, f);
        Assert.Equal(2, stats.Flows);
        Assert.Equal(2, stats.MaxConcurrentFlows);
        Assert.Equal(a.Length + b.Length, stats.BytesDelivered);
        Assert.Equal(new[] { a.ToArray(), b.ToArray() }.Select(Convert.ToHexString).OrderBy(x => x),
            f.Flows.Values.Select(s => Convert.ToHexString(s.Data)).OrderBy(x => x));
    }
}

/// <summary>The live service's single ordered dispatch (no Npcap needed).</summary>
public class CaptureDispatcherTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 19, 41, 0, DateTimeKind.Utc);

    [Fact]
    public void PacketsFromSeveralReaderThreads_AreDeliveredOnOneThread_InQueueOrder()
    {
        var t0 = DateTime.UtcNow; // the dispatch thread ticks with the wall clock (idle timeouts)
        var f = new RecordingFactory();
        var tracker = new GameFlowTracker(f);
        var sync = new object();
        var dispatcher = new CaptureDispatcher(tracker, sync, 64 * 1024 * 1024);
        dispatcher.Start();
        Assert.Equal(new[] { DiscontinuityReason.CaptureRestarted }, f.SessionDiscontinuities);

        // Two "adapters" (threads), each carrying one game flow.
        var world = new SyntheticConnection("192.168.178.81:53334", "87.232.75.150:13328");
        var inst = new SyntheticConnection("10.8.0.2:64331", "193.202.112.155:13328");
        var expectWorld = new MemoryStream();
        var expectInst = new MemoryStream();
        var worldFrames = new List<byte[]>();
        var instFrames = new List<byte[]>();
        for (int i = 0; i < 500; i++)
        {
            var pw = GameBytes.Concat(GameBytes.Heartbeat(i), GameBytes.DamageFrame(i, 20 + i % 50));
            var pi = GameBytes.Concat(GameBytes.Heartbeat(9000 + i), GameBytes.DamageFrame(9000 + i, 40 + i % 90));
            expectWorld.Write(pw);
            expectInst.Write(pi);
            worldFrames.Add(world.ServerSend(pw));
            instFrames.Add(inst.ServerSend(pi));
        }

        var a = new Thread(() => { for (int i = 0; i < worldFrames.Count; i++) dispatcher.EnqueuePacket(1, t0.AddMilliseconds(i * 10), LinkTypes.Ethernet, worldFrames[i]); });
        var b = new Thread(() => { for (int i = 0; i < instFrames.Count; i++) dispatcher.EnqueuePacket(2, t0.AddMilliseconds(i * 10 + 5), LinkTypes.Ethernet, instFrames[i]); });
        a.Start();
        b.Start();
        a.Join();
        b.Join();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (dispatcher.Processed < 1000 && DateTime.UtcNow < until) Thread.Sleep(10);
        bool unlockRan = false;
        dispatcher.Enqueue(t => unlockRan = t.ActiveFlowCount == 2);
        dispatcher.Complete();

        Assert.True(unlockRan);
        Assert.Equal(1000, dispatcher.Processed);
        Assert.Equal(expectWorld.ToArray(), f.Of(world).Data);
        Assert.Equal(expectInst.ToArray(), f.Of(inst).Data);
        Assert.Single(f.Threads);
        Assert.Equal(dispatcher.ThreadId, f.Threads.Keys.Single());
        Assert.Equal(0, f.ConcurrentCalls);
        Assert.Equal(2, f.Released.Count); // Complete releases every flow
        Assert.False(dispatcher.IsRunning);
    }

    [Fact]
    public void FullQueue_DropsAndCounts_InsteadOfBlocking()
    {
        var tracker = new GameFlowTracker(new RecordingFactory());
        var dispatcher = new CaptureDispatcher(tracker, new object(), maxQueuedBytes: 100);
        // Not started: nothing drains the queue.
        var frame = new byte[60];
        dispatcher.EnqueuePacket(1, T0, LinkTypes.Ethernet, frame);
        dispatcher.EnqueuePacket(1, T0, LinkTypes.Ethernet, frame);
        Assert.Equal(1, dispatcher.Dropped);
    }
}

public class MultiFlowServiceStatusTests
{
    private sealed class ScriptedLocator(GameLocatorResult result) : IGameConnectionLocator
    {
        public GameLocatorResult Locate() => result;
        public bool IsProcessAlive(int processId) => true;
    }

    [Fact]
    public void Detecting_ShowsEveryGameConnectionOfTheProcess()
    {
        var world = new GameConnection(43532, "AION2", IPv4Endpoint.Parse("192.168.178.81:53334"), IPv4Endpoint.Parse("87.232.75.150:13328"));
        var inst = new GameConnection(43532, "AION2", IPv4Endpoint.Parse("192.168.178.81:64331"), IPv4Endpoint.Parse("193.202.112.155:13328"));
        var web = new GameConnection(43532, "AION2", IPv4Endpoint.Parse("192.168.178.81:55000"), IPv4Endpoint.Parse("193.202.112.99:13700"));
        var options = new CaptureServiceOptions
        {
            NpcapProbe = () => new NpcapCheckResult(true, "ok (test)", null, 1),
            Locator = new ScriptedLocator(new GameLocatorResult(new[] { new GameProcessInfo(43532, "AION2") }, new[] { world, inst, web })),
            AdapterRefreshInterval = TimeSpan.FromHours(1),
        };
        using var svc = new NpcapCaptureService(options);
        svc.AdapterOverride = "No Such Adapter (test)"; // never opens a real adapter
        svc.Start(new RecordingFactory());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && svc.Status.ServerEndpoint is null) Thread.Sleep(20);
        Assert.Equal("87.232.75.150:13328 + 193.202.112.155:13328", svc.Status.ServerEndpoint);
        Assert.Equal("192.168.178.81:53334 + 192.168.178.81:64331", svc.Status.LocalEndpoint);
        Assert.Equal(0, svc.OpenFlowCount);
        svc.Stop();
    }

    [Fact]
    public void Filter_CoversTheGamePortAndEveryHintedHost()
    {
        var filter = CaptureFilters.ForServers(new[] { IPv4Endpoint.Parse("87.232.75.150:13328"), IPv4Endpoint.Parse("193.202.112.155:13328") });
        // Both servers use the game port, which the filter covers as a whole (one constant filter, no flush per new flow).
        Assert.Equal("tcp port 13328", filter);
    }

    [Fact]
    public void Locator_KeepsEveryEstablishedGamePortConnection()
    {
        var procs = new[] { new GameProcessInfo(43532, "AION2") };
        var table = new[]
        {
            new TcpConnectionEntry(IPv4Endpoint.Parse("192.168.178.81:53334"), IPv4Endpoint.Parse("87.232.75.150:13328"), TcpState.Established, 43532),
            new TcpConnectionEntry(IPv4Endpoint.Parse("192.168.178.81:64331"), IPv4Endpoint.Parse("193.202.112.155:13328"), TcpState.Established, 43532),
        };
        var ranked = GameProcessLocator.RankCandidates(table, procs);
        Assert.Equal(2, ranked.Count(c => c.IsGamePort));
    }
}
