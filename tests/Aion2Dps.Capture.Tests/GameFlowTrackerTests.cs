using Aion2Dps.Contracts;

namespace Aion2Dps.Capture.Tests;

public class GameFlowTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

    private sealed class Recorder : IPacketRecorder
    {
        public List<byte[]> Frames { get; } = new();
        public void WritePacket(DateTime timestampUtc, int linkType, ReadOnlySpan<byte> frame) => Frames.Add(frame.ToArray());
    }

    [Fact]
    public void LocksGameFlow_AfterThreeHeartbeatsFromPort13328_AndReplaysBufferedIdentity()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var game = new SyntheticConnection("192.168.178.81:62311", "87.232.75.150:13328");
        var expected = new MemoryStream();
        var t = T0;

        void Server(byte[] payload)
        {
            expected.Write(payload);
            tracker.OnPacket(t, LinkTypes.Ethernet, game.ServerSend(payload));
            t = t.AddMilliseconds(50);
        }

        Server(GameBytes.IdentityFrame); // sent at connect, before any heartbeat
        tracker.OnPacket(t, LinkTypes.Ethernet, game.ClientSend(new byte[] { 0xAA, 0xBB, 0xCC })); // encrypted c→s
        Server(GameBytes.Concat(GameBytes.Heartbeat(0), GameBytes.DamageFrame(1)));
        Server(GameBytes.Heartbeat(1));
        Assert.Null(tracker.CurrentLock);
        Assert.Empty(sink.Data);
        Server(GameBytes.Heartbeat(2)); // third hit → lock
        Assert.NotNull(tracker.CurrentLock);
        Assert.Equal(game.Server, tracker.CurrentLock!.Server);
        Assert.Equal(game.Client, tracker.CurrentLock.Client);
        Assert.False(tracker.CurrentLock.ByHint);
        Server(GameBytes.DamageFrame(2, 300));

        Assert.Equal(expected.ToArray(), sink.Data);
        Assert.Equal(GameBytes.IdentityFrame, sink.Data[..GameBytes.IdentityFrame.Length]);
        Assert.Empty(sink.Discontinuities); // the first lock does not emit NewConnection
        Assert.Equal(1, tracker.LockCount);
    }

    [Fact]
    public void IgnoresTlsDecoy_OnTheSameServerPort_AndNonGameFlows()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var tls = new SyntheticConnection("192.168.178.81:62000", "87.232.75.150:13328");
        var web = new SyntheticConnection("192.168.178.81:62001", "20.113.126.57:8080");
        var t = T0;

        // TLS decoy: first payload is a TLS record header, later payloads even contain heartbeat-looking bytes.
        tracker.OnPacket(t, LinkTypes.Ethernet, tls.ServerSend(new byte[] { 0x16, 0x03, 0x03, 0x00, 0x30, 0x02, 0x00 }));
        for (int i = 0; i < 20; i++)
        {
            t = t.AddMilliseconds(50);
            tracker.OnPacket(t, LinkTypes.Ethernet, tls.ServerSend(GameBytes.Concat(new byte[] { 0x17, 0x03, 0x03, 0x00, 0x10 }, GameBytes.Heartbeat(i))));
            // Non-game flow on another port with a few accidental hits (< 12 within 3 s) and lots of other data.
            var junk = new byte[400];
            new Random(i).NextBytes(junk);
            if (i % 4 == 0) GameBytes.Heartbeat(i).CopyTo(junk, 100);
            tracker.OnPacket(t, LinkTypes.Ethernet, web.ServerSend(junk));
        }
        Assert.Null(tracker.CurrentLock);
        Assert.Empty(sink.Data);
        Assert.Equal(1, tracker.TlsFlowsIgnored);
    }

    [Fact]
    public void NonGamePort_NeedsTwelveHitsWithinWindow()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var relay = new SyntheticConnection("127.0.0.1:50001", "127.0.0.1:38600", linkType: LinkTypes.Null);
        var t = T0;
        for (int i = 0; i < 11; i++)
        {
            tracker.OnPacket(t, LinkTypes.Null, relay.ServerSend(GameBytes.Heartbeat(i)));
            t = t.AddMilliseconds(52);
        }
        Assert.Null(tracker.CurrentLock);
        tracker.OnPacket(t, LinkTypes.Null, relay.ServerSend(GameBytes.Heartbeat(11)));
        Assert.NotNull(tracker.CurrentLock);
        Assert.Equal(relay.Server, tracker.CurrentLock!.Server);
        Assert.Equal(12 * 11, sink.Data.Length);
    }

    [Fact]
    public void HitsOutsideTheWindow_DoNotCount()
    {
        var tracker = new GameFlowTracker(new CollectingSink());
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(0)));
        tracker.OnPacket(T0.AddSeconds(4), LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(1)));
        tracker.OnPacket(T0.AddSeconds(8), LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(2)));
        Assert.Null(tracker.CurrentLock);
    }

    [Fact]
    public void HintedFlow_LocksOnFirstHeartbeat_ClientDirectionNeverQualifies()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var game = new SyntheticConnection("192.168.178.81:61283", "87.232.75.150:13328");
        tracker.SetHints(new[] { new FlowHint(game.Client, game.Server) });
        // Heartbeat-looking bytes from the client side must not lock.
        tracker.OnPacket(T0, LinkTypes.Ethernet, game.ClientSend(GameBytes.Heartbeat(0)));
        Assert.Null(tracker.CurrentLock);
        tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(1)));
        Assert.NotNull(tracker.CurrentLock);
        Assert.True(tracker.CurrentLock!.ByHint);
        Assert.Equal(GameBytes.Heartbeat(1), sink.Data);
    }

    [Fact]
    public void Reconnect_ToNewFlow_EmitsNewConnection()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var first = new SyntheticConnection("192.168.178.81:61283", "87.232.75.150:13328");
        var second = new SyntheticConnection("192.168.178.81:61300", "87.232.75.151:13328", serverIsn: 7_000_000);
        var t = T0;
        var expected = new MemoryStream();
        for (int i = 0; i < 5; i++)
        {
            var p = GameBytes.Heartbeat(i);
            expected.Write(p);
            tracker.OnPacket(t, LinkTypes.Ethernet, first.ServerSend(p));
            t = t.AddMilliseconds(50);
        }
        tracker.OnPacket(t, LinkTypes.Ethernet, first.ServerSend(default, TcpFlags.Fin | TcpFlags.Ack));
        Assert.True(tracker.LockedFlowClosed);

        // The new connection starts with a handshake and the identity record.
        tracker.OnPacket(t, LinkTypes.Ethernet, second.ClientSyn());
        tracker.OnPacket(t, LinkTypes.Ethernet, second.ServerSynAck());
        int boundary = (int)expected.Length;
        foreach (var p in new[] { GameBytes.IdentityFrame, GameBytes.Heartbeat(10), GameBytes.Heartbeat(11), GameBytes.Heartbeat(12) })
        {
            t = t.AddMilliseconds(50);
            expected.Write(p);
            tracker.OnPacket(t, LinkTypes.Ethernet, second.ServerSend(p));
        }
        Assert.Equal(second.Server, tracker.CurrentLock!.Server);
        Assert.Equal(2, tracker.LockCount);
        Assert.Equal(new[] { DiscontinuityReason.NewConnection }, sink.Discontinuities);
        Assert.Equal(expected.ToArray(), sink.Data);
        int idx = sink.Events.IndexOf("NewConnection");
        Assert.Equal(boundary, sink.Events.Take(idx).Sum(e => int.Parse(e[1..])));
    }

    [Fact]
    public void SecondQualifyingFlow_IsLockedConcurrently_PlainSinkFollowsTheNewestFlow()
    {
        // LIVE-FINDINGS NEW 1: both game connections are followed. A plain (single-stream) sink can only take one of
        // them: the newest flow owns it (SingleSinkFlowAdapter), the other flow's bytes are counted as dropped.
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var a = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        var b = new SyntheticConnection("10.0.0.2:50001", "193.202.112.200:13328");
        var t = T0;
        for (int i = 0; i < 6; i++)
        {
            tracker.OnPacket(t, LinkTypes.Ethernet, a.ServerSend(GameBytes.Heartbeat(i)));
            tracker.OnPacket(t, LinkTypes.Ethernet, b.ServerSend(GameBytes.Heartbeat(i)));
            t = t.AddMilliseconds(50);
        }
        Assert.Equal(2, tracker.ActiveFlowCount);
        Assert.Equal(2, tracker.LockCount);
        Assert.Equal(2, tracker.MaxConcurrentFlows);
        Assert.Equal(b.Server, tracker.CurrentLock!.Server);
        Assert.Equal(new[] { a.Server, b.Server }, tracker.ActiveLocks.Select(l => l.Server));
        Assert.Equal(new[] { DiscontinuityReason.NewConnection }, sink.Discontinuities);
        var adapter = Assert.IsType<SingleSinkFlowAdapter>(tracker.Factory);
        Assert.Equal(3 * 11, adapter.DroppedBytes); // a's heartbeats 3..5 after b took the sink
        Assert.Equal(12 * 11, tracker.BytesDelivered); // reassembled per flow
        Assert.Equal(9 * 11, sink.Data.Length);        // what the single sink received

        // a goes silent: it stays open until the idle timeout, then b remains.
        t = t.AddSeconds(6);
        for (int i = 0; i < 3; i++)
        {
            tracker.OnPacket(t, LinkTypes.Ethernet, b.ServerSend(GameBytes.Heartbeat(100 + i)));
            t = t.AddMilliseconds(50);
        }
        Assert.Equal(2, tracker.ActiveFlowCount);
        tracker.Tick(t.AddSeconds(91));
        Assert.Equal(0, tracker.ActiveFlowCount);
        Assert.Equal(2, tracker.LockCount);
    }

    [Fact]
    public void Recorder_GetsBothDirections_IncludingPreLockBuffer()
    {
        var rec = new Recorder();
        var tracker = new GameFlowTracker(new CollectingSink()) { Recorder = rec };
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        var other = new SyntheticConnection("10.0.0.2:50001", "1.2.3.4:9999");
        var frames = new List<byte[]>
        {
            game.ClientSyn(), game.ServerSynAck(), game.ServerSend(GameBytes.IdentityFrame), game.ClientSend(new byte[] { 1 }),
            game.ServerSend(GameBytes.Heartbeat(0)), game.ServerSend(GameBytes.Heartbeat(1)), game.ServerSend(GameBytes.Heartbeat(2)),
            game.ClientSend(new byte[] { 2 }),
        };
        foreach (var f in frames)
        {
            tracker.OnPacket(T0, LinkTypes.Ethernet, f);
            tracker.OnPacket(T0, LinkTypes.Ethernet, other.ServerSend(new byte[] { 5, 5, 5 }));
        }
        Assert.Equal(frames.Count, rec.Frames.Count);
        for (int i = 0; i < frames.Count; i++) Assert.Equal(frames[i], rec.Frames[i]);
    }

    [Fact]
    public void OutOfOrderAndRetransmittedSegments_AfterLock_AreReassembled()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        for (int i = 0; i < 3; i++) tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(i)));
        int before = sink.Data.Length;
        var p1 = GameBytes.DamageFrame(1, 100);
        var p2 = GameBytes.DamageFrame(2, 200);
        var p3 = GameBytes.DamageFrame(3, 50);
        var f1 = game.ServerSend(p1);
        var f2 = game.ServerSend(p2);
        var f3 = game.ServerSend(p3);
        tracker.OnPacket(T0, LinkTypes.Ethernet, f3);
        tracker.OnPacket(T0, LinkTypes.Ethernet, f1);
        tracker.OnPacket(T0, LinkTypes.Ethernet, f1); // retransmit
        tracker.OnPacket(T0, LinkTypes.Ethernet, f2);
        Assert.Equal(GameBytes.Concat(p1, p2, p3), sink.Data[before..]);
        Assert.Equal(0, tracker.GapCount);
    }

    [Fact]
    public void FallbackLock_AtEndOfInput_UsesGamePortFlow()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSend(GameBytes.IdentityFrame));
        Assert.Null(tracker.CurrentLock);
        Assert.True(tracker.TryFallbackLock(T0));
        Assert.Equal(GameBytes.IdentityFrame, sink.Data);
        Assert.False(tracker.TryFallbackLock(T0));
    }

    [Fact]
    public void SinkExceptions_DoNotBreakTheTracker()
    {
        var tracker = new GameFlowTracker(new ThrowingSink());
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        for (int i = 0; i < 5; i++) tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(i)));
        Assert.NotNull(tracker.CurrentLock);
        Assert.Equal(55, tracker.BytesDelivered);
    }

    private sealed class ThrowingSink : IStreamSink
    {
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data) => throw new InvalidOperationException("boom");
        public void OnDiscontinuity(DiscontinuityReason reason) => throw new InvalidOperationException("boom");
    }
}
