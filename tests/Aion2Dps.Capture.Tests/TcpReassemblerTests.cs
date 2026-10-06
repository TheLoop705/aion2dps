using Aion2Dps.Contracts;

namespace Aion2Dps.Capture.Tests;

public class TcpReassemblerTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] Bytes(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

    [Fact]
    public void InOrder_IsDeliveredImmediately()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink);
        r.Push(1000, Bytes(0, 10), T0);
        r.Push(1010, Bytes(10, 5), T0.AddMilliseconds(1));
        Assert.Equal(Bytes(0, 15), sink.Data);
        Assert.Equal(new[] { "D10", "D5" }, sink.Events);
        Assert.Equal(1015u, r.NextSequence);
        Assert.Equal(T0.AddMilliseconds(1), sink.Times[1]);
    }

    [Fact]
    public void OutOfOrder_IsHeldAndDrained()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink);
        r.Push(1000, Bytes(0, 10), T0);
        r.Push(1020, Bytes(20, 10), T0); // hole 1010..1019
        r.Push(1030, Bytes(30, 10), T0);
        Assert.Equal(Bytes(0, 10), sink.Data);
        Assert.Equal(2, r.PendingSegments);
        r.Push(1010, Bytes(10, 10), T0.AddMilliseconds(5));
        Assert.Equal(Bytes(0, 40), sink.Data);
        Assert.Equal(0, r.PendingSegments);
        Assert.Equal(0, r.PendingBytes);
        Assert.Empty(sink.Discontinuities);
    }

    [Fact]
    public void Duplicate_AndRetransmit_AreDropped()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink);
        r.Push(1000, Bytes(0, 10), T0);
        r.Push(1000, Bytes(0, 10), T0); // exact duplicate
        r.Push(1002, Bytes(2, 5), T0);  // fully old subset
        r.Push(1020, Bytes(20, 5), T0);
        r.Push(1020, Bytes(20, 5), T0); // duplicate of a held segment
        r.Push(1010, Bytes(10, 10), T0);
        Assert.Equal(Bytes(0, 25), sink.Data);
        Assert.Equal(20, r.DuplicateBytes);
    }

    [Fact]
    public void PartialOverlap_IsTrimmed()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink);
        r.Push(1000, Bytes(0, 10), T0);
        r.Push(1005, Bytes(5, 10), T0); // 5 old + 5 new
        Assert.Equal(Bytes(0, 15), sink.Data);

        // Held segments that overlap each other and the delivered data.
        r.Push(1020, Bytes(20, 10), T0);  // 1020..1029
        r.Push(1025, Bytes(25, 10), T0);  // 1025..1034 overlaps the previous held one
        r.Push(1015, Bytes(15, 8), T0);   // 1015..1022 fills the hole and overlaps 1020..1022
        Assert.Equal(Bytes(0, 35), sink.Data);
        Assert.Equal(0, r.PendingSegments);
    }

    [Fact]
    public void KeepsLongestSegmentPerSequence()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink);
        r.Push(1000, Bytes(0, 10), T0);
        r.Push(1020, Bytes(20, 4), T0);
        r.Push(1020, Bytes(20, 10), T0); // longer, replaces
        r.Push(1020, Bytes(20, 2), T0);  // shorter, ignored
        r.Push(1010, Bytes(10, 10), T0);
        Assert.Equal(Bytes(0, 30), sink.Data);
    }

    [Fact]
    public void SequenceWraparound_IsHandled()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink);
        uint start = 0xFFFF_FFF0;
        r.Push(start, Bytes(0, 10), T0);               // ..FFFFFFF9
        r.Push(unchecked(start + 20), Bytes(20, 10), T0); // wraps: 0x00000004.. held
        r.Push(unchecked(start + 10), Bytes(10, 10), T0); // FFFFFFFA..0x00000003
        r.Push(unchecked(start + 25), Bytes(25, 10), T0); // overlap after wrap
        r.Push(unchecked(start + 5), Bytes(5, 3), T0);    // old before wrap
        Assert.Equal(Bytes(0, 35), sink.Data);
        Assert.Equal(unchecked(start + 35), r.NextSequence);
        Assert.Empty(sink.Discontinuities);
    }

    [Fact]
    public void GapGiveUp_AfterTimeout_JumpsAndReportsGap()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink, TimeSpan.FromMilliseconds(1500));
        r.Push(1000, Bytes(0, 10), T0);
        r.Push(1020, Bytes(20, 10), T0.AddMilliseconds(100)); // 1010..1019 lost
        r.Push(1030, Bytes(30, 10), T0.AddMilliseconds(800));
        Assert.Equal(Bytes(0, 10), sink.Data);
        r.Tick(T0.AddMilliseconds(1500)); // only 1.4 s old
        Assert.Empty(sink.Discontinuities);
        r.Tick(T0.AddMilliseconds(1700));
        Assert.Equal(new[] { DiscontinuityReason.TcpGap }, sink.Discontinuities);
        Assert.Equal(new[] { "D10", "TcpGap", "D10", "D10" }, sink.Events);
        Assert.Equal(Bytes(0, 10).Concat(Bytes(20, 20)), sink.Data);
        Assert.Equal(1, r.GapCount);
        Assert.Equal(10, r.SkippedBytes);

        // The late retransmit of the lost bytes is now old and dropped.
        r.Push(1010, Bytes(10, 10), T0.AddMilliseconds(1800));
        Assert.Equal(30, sink.Data.Length);
        r.Push(1040, Bytes(40, 5), T0.AddMilliseconds(1900));
        Assert.Equal(35, sink.Data.Length);
    }

    [Fact]
    public void GapGiveUp_WhenPendingExceedsLimit()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink, TimeSpan.FromSeconds(60), maxPendingBytes: 100);
        r.Push(1000, Bytes(0, 10), T0);
        uint seq = 1050; // 40 bytes lost
        for (int i = 0; i < 10; i++)
        {
            r.Push(seq, Bytes(i, 20), T0);
            seq += 20;
        }
        Assert.Contains(DiscontinuityReason.TcpGap, sink.Discontinuities);
        Assert.True(r.PendingBytes <= 100);
        Assert.Equal(10 + 200, sink.Data.Length);
    }

    [Fact]
    public void Flush_DeliversHeldDataAcrossHoles()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink);
        r.Push(1000, Bytes(0, 10), T0);
        r.Push(1020, Bytes(20, 10), T0);
        r.Push(1040, Bytes(40, 10), T0);
        r.Flush(T0);
        Assert.Equal(30, sink.Data.Length);
        Assert.Equal(2, sink.Discontinuities.Count);
    }

    [Fact]
    public void Seed_AllowsEarlierBufferedSegmentsToBeHeld()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink);
        r.Seed(1000);
        r.Push(1010, Bytes(10, 10), T0); // arrives first but is not the start
        r.Push(1000, Bytes(0, 10), T0);
        Assert.Equal(Bytes(0, 20), sink.Data);
    }

    [Fact]
    public void FirstSegmentSeeds_MidStreamStart()
    {
        var sink = new CollectingSink();
        var r = new TcpReassembler(sink);
        r.Push(123_456_789, Bytes(0, 3), T0);
        Assert.Equal(Bytes(0, 3), sink.Data);
        r.Push(123_456_789 - 10, Bytes(0, 5), T0); // before the seed: old
        Assert.Equal(3, sink.Data.Length);
    }
}
