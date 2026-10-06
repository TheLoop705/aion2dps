using Aion2Dps.Contracts;

namespace Aion2Dps.Capture.Tests;

public class LiveHardeningTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void HeartbeatsSplitAtEveryByteBoundary_LockAndReplayTheCompleteStream(int split)
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        for (int i = 0; i < 3; i++)
        {
            var heartbeat = GameBytes.Heartbeat(i);
            tracker.OnPacket(T0.AddMilliseconds(i * 52), LinkTypes.Ethernet, game.ServerSend(heartbeat.AsSpan(0, split)));
            tracker.OnPacket(T0.AddMilliseconds(i * 52 + 1), LinkTypes.Ethernet, game.ServerSend(heartbeat.AsSpan(split)));
        }

        Assert.NotNull(tracker.CurrentLock);
        Assert.Equal(GameBytes.Concat(GameBytes.Heartbeat(0), GameBytes.Heartbeat(1), GameBytes.Heartbeat(2)), sink.Data);
        Assert.Equal(0, tracker.GapCount);
    }

    [Fact]
    public void HintedHeartbeat_SplitAndReorderedAfterSyn_StillLocks()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var game = new SyntheticConnection("127.0.0.1:50001", "127.0.0.1:38600");
        tracker.SetHints([new FlowHint(game.Client, game.Server)]);
        tracker.OnPacket(T0, LinkTypes.Ethernet, game.ClientSyn());
        tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSynAck());
        var heartbeat = GameBytes.Heartbeat(0);
        var first = game.ServerSend(heartbeat.AsSpan(0, 2));
        var second = game.ServerSend(heartbeat.AsSpan(2, 3));
        var third = game.ServerSend(heartbeat.AsSpan(5));
        tracker.OnPacket(T0.AddMilliseconds(1), LinkTypes.Ethernet, third);
        tracker.OnPacket(T0.AddMilliseconds(2), LinkTypes.Ethernet, second);
        Assert.Null(tracker.CurrentLock);
        tracker.OnPacket(T0.AddMilliseconds(3), LinkTypes.Ethernet, first);
        Assert.NotNull(tracker.CurrentLock);
        Assert.Equal(heartbeat, sink.Data);
        Assert.Equal(0, tracker.GapCount);
    }

    [Fact]
    public void RepeatedAndOverlappingHeartbeatBytes_DoNotInflateTheSignatureScore()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        var heartbeat = GameBytes.Heartbeat(0);
        uint firstSequence = game.ServerSeq;
        var first = game.ServerSend(heartbeat);
        for (int i = 0; i < 20; i++)
        {
            tracker.OnPacket(T0.AddMilliseconds(i), LinkTypes.Ethernet, first);
            tracker.OnPacket(T0.AddMilliseconds(i), LinkTypes.Ethernet, game.ServerSegmentAt(firstSequence + 2, heartbeat.AsSpan(2)));
        }

        Assert.Null(tracker.CurrentLock);
        tracker.OnPacket(T0.AddMilliseconds(52), LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(1)));
        Assert.Null(tracker.CurrentLock);
        tracker.OnPacket(T0.AddMilliseconds(104), LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(2)));
        Assert.NotNull(tracker.CurrentLock);
        Assert.Equal(GameBytes.Concat(heartbeat, GameBytes.Heartbeat(1), GameBytes.Heartbeat(2)), sink.Data);
    }

    [Fact]
    public void FreshSynOnATlsCandidate_ForgetsTheOldClassification()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var old = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        tracker.OnPacket(T0, LinkTypes.Ethernet, old.ClientSyn());
        tracker.OnPacket(T0, LinkTypes.Ethernet, old.ServerSynAck());
        tracker.OnPacket(T0, LinkTypes.Ethernet, old.ServerSend(new byte[] { 0x16, 0x03, 0x03, 0, 1 }));
        var fresh = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328", serverIsn: 200_000, clientIsn: 9000);
        tracker.OnPacket(T0.AddSeconds(1), LinkTypes.Ethernet, fresh.ClientSyn());
        tracker.OnPacket(T0.AddSeconds(1), LinkTypes.Ethernet, fresh.ServerSynAck());
        for (int i = 0; i < 3; i++) tracker.OnPacket(T0.AddSeconds(1), LinkTypes.Ethernet, fresh.ServerSend(GameBytes.Heartbeat(i)));
        Assert.NotNull(tracker.CurrentLock);
        Assert.Equal(33, sink.Data.Length);
        Assert.Equal(1, tracker.TlsFlowsIgnored);
    }

    [Fact]
    public void FreshSyn_ForgetsOldHeartbeatScoresAndBufferedIdentity()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var old = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        tracker.OnPacket(T0, LinkTypes.Ethernet, old.ServerSend(GameBytes.Heartbeat(0)));
        tracker.OnPacket(T0, LinkTypes.Ethernet, old.ServerSend(GameBytes.Heartbeat(1)));
        var fresh = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328", serverIsn: 200_000, clientIsn: 9000);
        tracker.OnPacket(T0, LinkTypes.Ethernet, fresh.ClientSyn());
        tracker.OnPacket(T0, LinkTypes.Ethernet, fresh.ServerSynAck());
        tracker.OnPacket(T0, LinkTypes.Ethernet, fresh.ServerSend(GameBytes.IdentityFrame));
        tracker.OnPacket(T0, LinkTypes.Ethernet, fresh.ServerSend(GameBytes.Heartbeat(10)));
        Assert.Null(tracker.CurrentLock);
        tracker.OnPacket(T0, LinkTypes.Ethernet, fresh.ServerSend(GameBytes.Heartbeat(11)));
        tracker.OnPacket(T0, LinkTypes.Ethernet, fresh.ServerSend(GameBytes.Heartbeat(12)));
        Assert.Equal(GameBytes.Concat(GameBytes.IdentityFrame, GameBytes.Heartbeat(10), GameBytes.Heartbeat(11), GameBytes.Heartbeat(12)), sink.Data);
    }

    [Fact]
    public void SynPayload_BeforeAndAfterLock_DeliversItsFirstByte()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        tracker.SetHints([new FlowHint(game.Client, game.Server)]);
        var heartbeat = GameBytes.Heartbeat(0);
        tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSegmentAt(100_000, heartbeat, TcpFlags.Syn | TcpFlags.Ack));
        Assert.Equal(heartbeat, sink.Data);
        var identity = GameBytes.IdentityFrame;
        tracker.OnPacket(T0.AddSeconds(1), LinkTypes.Ethernet, game.ServerSegmentAt(300_000, identity, TcpFlags.Syn | TcpFlags.Ack));
        tracker.OnPacket(T0.AddSeconds(1), LinkTypes.Ethernet, game.ServerSegmentAt(300_001 + (uint)identity.Length, GameBytes.Heartbeat(1)));
        Assert.Equal(GameBytes.Concat(heartbeat, identity, GameBytes.Heartbeat(1)), sink.Data);
        Assert.Equal(new[] { DiscontinuityReason.NewConnection }, sink.Discontinuities);
        Assert.Equal(0, tracker.GapCount);
    }

    [Fact]
    public void PrelockBuffer_EnforcesTheTotalLimitEvenWhenOnlyOneCandidateExists()
    {
        var tracker = new GameFlowTracker(new CollectingSink(), new FlowTrackerOptions
        {
            MaxBufferedBytesPerFlow = 128,
            MaxTotalBufferedBytes = 64,
        });
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        for (int i = 0; i < 20; i++)
        {
            tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSend(new byte[] { 1, 2, 3, 4 }));
            Assert.InRange(tracker.BufferedBytes, 0, 64);
        }
        tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSend(new byte[200]));
        Assert.InRange(tracker.BufferedBytes, 0, 64);
    }

    [Fact]
    public void PrelockBuffer_HasAPacketLimitIndependentOfItsByteLimit()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink, new FlowTrackerOptions { MaxBufferedPacketsPerFlow = 2 });
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        for (byte i = 1; i <= 5; i++) tracker.OnPacket(T0, LinkTypes.Ethernet, game.ServerSend(new byte[] { i }));
        Assert.True(tracker.TryFallbackLock(T0));
        Assert.Equal(new byte[] { 4, 5 }, sink.Data);
    }

    [Fact]
    public void TinyOutOfOrderSegments_CannotExceedTheSegmentCountBound()
    {
        var sink = new CollectingSink();
        var reassembler = new TcpReassembler(sink, TimeSpan.FromHours(1), maxPendingBytes: 1_000_000, maxPendingSegments: 4);
        reassembler.Push(1000, new byte[] { 0 }, T0);
        for (uint i = 0; i < 20; i++)
        {
            reassembler.Push(1020 + i * 10, new byte[] { (byte)i }, T0);
            Assert.InRange(reassembler.PendingSegments, 0, 4);
        }
        Assert.True(reassembler.GapCount > 0);
        reassembler.Flush(T0);
        Assert.Equal(21, sink.Data.Length);
    }

    [Fact]
    public void OlderDuplicateCaptureTimes_DoNotMoveTheFlowClockBackwards()
    {
        var tracker = new GameFlowTracker(new CollectingSink());
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        var frames = Enumerable.Range(0, 3).Select(i => game.ServerSend(GameBytes.Heartbeat(i))).ToArray();
        foreach (var frame in frames) tracker.OnPacket(T0, LinkTypes.Ethernet, frame);
        tracker.OnPacket(T0.AddSeconds(10), LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(4)));
        tracker.OnPacket(T0.AddSeconds(1), LinkTypes.Ethernet, frames[0]);
        Assert.Equal(T0.AddSeconds(10), tracker.LastPacketOf(tracker.CurrentLock!.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamingSignatureScanner_MatchesTheWholePayloadCount(bool strict)
    {
        var junk = new byte[50_000];
        new Random(42).NextBytes(junk);
        var bytes = GameBytes.Concat(junk, GameBytes.Heartbeat(0), new byte[] { 0x06, 0, 0x36 },
            new byte[] { 0x0E, 0, 0x36, 0, 0, 0, 0, 0, 0, 0, 0 }, GameBytes.Heartbeat(1), junk);
        foreach (int chunk in new[] { 1, 2, 17, 512 })
        {
            var scanner = new HeartbeatSignatureScanner();
            int count = 0;
            for (int offset = 0; offset < bytes.Length; offset += chunk)
                count += scanner.Push(bytes.AsSpan(offset, Math.Min(chunk, bytes.Length - offset)), strict);
            Assert.Equal(GameSignature.CountHeartbeats(bytes, strict), count);
        }
    }
}

public class DispatchHardeningTests
{
    private sealed class BlockingDataSink : IStreamSink
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public readonly CollectingSink Collected = new();
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
        {
            Entered.Set();
            Release.Wait();
            Collected.OnData(timeUtc, data);
        }
        public void OnDiscontinuity(DiscontinuityReason reason) => Collected.OnDiscontinuity(reason);
    }

    [Fact]
    public async Task Completion_WaitsForTheSinkAndEveryQueuedPacket()
    {
        var sink = new BlockingDataSink();
        var tracker = new GameFlowTracker(sink);
        var dispatcher = new CaptureDispatcher(tracker, new object(), maxQueuedBytes: 1024 * 1024);
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        var expected = GameBytes.Concat(GameBytes.Heartbeat(0), GameBytes.Heartbeat(1), GameBytes.Heartbeat(2), GameBytes.Heartbeat(3));
        dispatcher.Start();
        for (int i = 0; i < 4; i++) dispatcher.EnqueuePacket(1, DateTime.UtcNow, LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(i)));
        Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5)));
        var completion = Task.Run(dispatcher.Complete);
        try
        {
            Assert.NotSame(completion, await Task.WhenAny(completion, Task.Delay(50)));
            Assert.True(dispatcher.IsRunning);
        }
        finally { sink.Release.Set(); }
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, sink.Collected.Data);
        Assert.Equal(0, dispatcher.QueuedBytes);
        Assert.False(dispatcher.IsRunning);
        Assert.Equal(0, tracker.ActiveFlowCount);
    }

    [Fact]
    public void QueueLimit_AccountsForTheActualArrayPoolAllocation()
    {
        var dispatcher = new CaptureDispatcher(new GameFlowTracker(new CollectingSink()), new object(), maxQueuedBytes: 96);
        dispatcher.EnqueuePacket(1, DateTime.UtcNow, LinkTypes.Ethernet, new byte[65]); // ArrayPool rents 128 bytes.
        Assert.Equal(1, dispatcher.Dropped);
        Assert.Equal(0, dispatcher.QueuedBytes);
        dispatcher.Start();
        dispatcher.Complete();
    }

    [Fact]
    public void ConcurrentReaders_CannotOversubscribeTheQueueBudget()
    {
        var dispatcher = new CaptureDispatcher(new GameFlowTracker(new CollectingSink()), new object(), maxQueuedBytes: 256);
        Parallel.For(0, 2000, _ => dispatcher.EnqueuePacket(1, DateTime.UtcNow, LinkTypes.Ethernet, new byte[60]));
        Assert.Equal(256, dispatcher.QueuedBytes);
        Assert.Equal(4, dispatcher.QueuedPackets);
        Assert.Equal(1996, dispatcher.Dropped);
        dispatcher.Start();
        dispatcher.Complete();
        Assert.Equal(4, dispatcher.Processed);
        Assert.Equal(0, dispatcher.QueuedBytes);
        Assert.Equal(0, dispatcher.QueuedPackets);
    }

    [Fact]
    public void EmptyFrames_CannotBypassThePacketCountBound()
    {
        var dispatcher = new CaptureDispatcher(new GameFlowTracker(new CollectingSink()), new object(),
            maxQueuedBytes: 1_000_000, maxQueuedPackets: 4);
        for (int i = 0; i < 20; i++) dispatcher.EnqueuePacket(1, DateTime.UtcNow, LinkTypes.Ethernet, Array.Empty<byte>());
        Assert.Equal(4, dispatcher.QueuedPackets);
        Assert.Equal(16, dispatcher.Dropped);
        dispatcher.Start();
        dispatcher.Complete();
        Assert.Equal(4, dispatcher.Processed);
        Assert.Equal(0, dispatcher.QueuedBytes);
    }

    [Fact]
    public void DispatchBacklog_DrainsQueuedRetransmitsBeforeGivingUpAGap()
    {
        var sink = new CollectingSink();
        var tracker = new GameFlowTracker(sink);
        var dispatcher = new CaptureDispatcher(tracker, new object(), maxQueuedBytes: 1024 * 1024);
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        var t0 = DateTime.UtcNow.AddSeconds(-5);
        for (int i = 0; i < 3; i++) dispatcher.EnqueuePacket(1, t0, LinkTypes.Ethernet, game.ServerSend(GameBytes.Heartbeat(i)));
        var missingPayload = new byte[] { 1, 2, 3, 4, 5 };
        var laterPayload = new byte[] { 6, 7, 8, 9, 10 };
        var missing = game.ServerSend(missingPayload);
        var later = game.ServerSend(laterPayload);
        dispatcher.EnqueuePacket(1, t0.AddMilliseconds(1), LinkTypes.Ethernet, later);
        dispatcher.Enqueue(_ => Thread.Sleep(350)); // slow downstream work, with the missing bytes already queued.
        dispatcher.EnqueuePacket(1, t0.AddMilliseconds(2), LinkTypes.Ethernet, missing);
        dispatcher.Start();
        dispatcher.Complete();
        Assert.Equal(0, tracker.GapCount);
        Assert.Equal(GameBytes.Concat(GameBytes.Heartbeat(0), GameBytes.Heartbeat(1), GameBytes.Heartbeat(2), missingPayload, laterPayload), sink.Data);
    }
}

public class CaptureShutdownHardeningTests
{
    private sealed class EmptyLocator : IGameConnectionLocator
    {
        public GameLocatorResult Locate() => GameLocatorResult.Empty;
        public bool IsProcessAlive(int processId) => false;
    }

    [Fact]
    public void DisposedService_CannotCreateOrAttachANewRecording()
    {
        var service = new NpcapCaptureService();
        string directory = Path.Combine(Path.GetTempPath(), "aion2dps-shutdown-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "disposed.pcapng");
        service.Dispose();
        Assert.Throws<ObjectDisposedException>(() => service.StartRecording(path));
        service.Dispose();
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void TimedOutStop_RetainsTheRunAndDoesNotReportFalseCompletionOrRestartIt()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int probes = 0;
        var service = new NpcapCaptureService(new CaptureServiceOptions
        {
            StopTimeout = TimeSpan.FromMilliseconds(30),
            Locator = new EmptyLocator(),
            NpcapProbe = () =>
            {
                Interlocked.Increment(ref probes);
                entered.Set();
                release.Wait();
                return new NpcapCheckResult(false, "test probe complete", null, 0);
            },
        });
        try
        {
            service.Start(new CollectingSink());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<TimeoutException>(service.Stop);
            Assert.True(service.IsRunning);
            Assert.NotEqual(CaptureState.Stopped, service.Status.State);
            string rejectedDirectory = Path.Combine(Path.GetTempPath(), "aion2dps-shutdown-tests", Guid.NewGuid().ToString("N"));
            Assert.Throws<InvalidOperationException>(() => service.StartRecording(Path.Combine(rejectedDirectory, "stopping.pcapng")));
            Assert.False(Directory.Exists(rejectedDirectory));
            var secondSink = new CollectingSink();
            service.Start(secondSink);
            Assert.Empty(secondSink.Discontinuities);
            Assert.Equal(1, Volatile.Read(ref probes));
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !service.IsRunning, TimeSpan.FromSeconds(5)));
            Assert.Equal(CaptureState.Stopped, service.Status.State);
            service.Stop();
        }
        finally { release.Set(); service.Dispose(); }
    }

    [Fact]
    public void DisposeAfterStopTimeout_KeepsTheRecorderOpenUntilTheRunFinishes()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var service = new NpcapCaptureService(new CaptureServiceOptions
        {
            StopTimeout = TimeSpan.FromMilliseconds(30),
            Locator = new EmptyLocator(),
            NpcapProbe = () =>
            {
                entered.Set();
                release.Wait();
                return new NpcapCheckResult(false, "test probe complete", null, 0);
            },
        });
        string directory = Path.Combine(Path.GetTempPath(), "aion2dps-shutdown-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "tail.pcapng");
        try
        {
            service.StartRecording(path);
            service.Start(new CollectingSink());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            service.Dispose();
            Assert.True(service.IsRunning);
            Assert.Throws<IOException>(() =>
            {
                using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            Assert.Throws<ObjectDisposedException>(() => service.Start(new CollectingSink()));
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !service.IsRunning, TimeSpan.FromSeconds(5)));
            Assert.Null(service.Status.RecordingPath);
            using var reader = new PcapFileReader(path);
            Assert.Empty(reader.ReadAll());
        }
        finally
        {
            release.Set();
            service.Dispose();
            Assert.True(SpinWait.SpinUntil(() => !service.IsRunning, TimeSpan.FromSeconds(5)));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
