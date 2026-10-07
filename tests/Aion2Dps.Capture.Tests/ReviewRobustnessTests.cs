using Aion2Dps.Contracts;

namespace Aion2Dps.Capture.Tests;

/// <summary>Regressions for the robustness review of the capture layer.</summary>
public class CaptureReviewRobustnessTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>A heartbeat-shaped triplet followed by 8 bytes that are no plausible server clock (random bulk data).</summary>
    private static byte[] FakeHeartbeat(Random rnd)
    {
        var b = new byte[11];
        rnd.NextBytes(b);
        b[0] = 0x0E;
        b[1] = 0x00;
        b[2] = 0x36;
        b[10] = (byte)(0x80 | b[10]); // top byte of the u64 LE body: far beyond year 2100
        return b;
    }

    [Fact]
    public void BulkTransfer_WithHeartbeatShapedBytes_DoesNotLock_OffTheGamePort()
    {
        // Before: '0E 00 36' anywhere counted, so a fast non-TLS download (~0.125 hits/MB) reached 12 hits in 3 s.
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var download = new SyntheticConnection("192.168.178.81:50123", "151.101.2.10:8080");
        var rnd = new Random(42);
        var t = T0;
        for (int i = 0; i < 200; i++)
        {
            var junk = new byte[1400];
            rnd.NextBytes(junk);
            FakeHeartbeat(rnd).CopyTo(junk, 300);
            FakeHeartbeat(rnd).CopyTo(junk, 900);
            tracker.OnPacket(t, LinkTypes.Ethernet, download.ServerSend(junk));
            t = t.AddMilliseconds(10);
        }

        Assert.Null(tracker.CurrentLock);
        Assert.Equal(0, tracker.LockCount);
    }

    [Fact]
    public void WebAndSmbPorts_NeverLock_WithoutAHint()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var web = new SyntheticConnection("192.168.178.81:50124", "151.101.2.10:80");
        var t = T0;
        for (int i = 0; i < 40; i++)
        {
            tracker.OnPacket(t, LinkTypes.Ethernet, web.ServerSend(GameBytes.Heartbeat(i)));
            t = t.AddMilliseconds(20);
        }
        Assert.Null(tracker.CurrentLock);

        // Named by the game process, the same flow locks.
        tracker.SetHints(new[] { new FlowHint(web.Client, web.Server) });
        tracker.OnPacket(t, LinkTypes.Ethernet, web.ServerSend(GameBytes.Heartbeat(99)));
        Assert.NotNull(tracker.CurrentLock);
    }

    [Fact]
    public void RealHeartbeats_StillLock_OnARelayPort()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var relay = new SyntheticConnection("127.0.0.1:50001", "127.0.0.1:38600", linkType: LinkTypes.Null);
        var t = T0;
        for (int i = 0; i < 12; i++)
        {
            tracker.OnPacket(t, LinkTypes.Null, relay.ServerSend(GameBytes.Heartbeat(i)));
            t = t.AddMilliseconds(52);
        }
        Assert.NotNull(tracker.CurrentLock);
        Assert.Equal(t.AddMilliseconds(-52), tracker.LastPacketOf(tracker.CurrentLock!.Key));
    }

    [Fact]
    public void StrictCount_RequiresAPlausibleServerClock()
    {
        var real = GameBytes.Heartbeat(3);
        var fake = FakeHeartbeat(new Random(1));
        byte[] legacy = { 0x06, 0x00, 0x36 };
        var payload = GameBytes.Concat(new byte[] { 0x55 }, real, fake, legacy, real);
        Assert.Equal(4, GameSignature.CountHeartbeats(payload));
        Assert.Equal(2, GameSignature.CountHeartbeats(payload, requireServerClock: true));
    }
}
