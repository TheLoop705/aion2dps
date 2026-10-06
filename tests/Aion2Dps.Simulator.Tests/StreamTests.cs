using System.Buffers.Binary;
using System.Collections.Concurrent;
using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator.Tests;

public class StreamGeneratorTests
{
    private static readonly DateTime Start = new(2026, 10, 6, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_seed_same_stream_different_seed_different_chunks()
    {
        var s = ScenarioLibrary.BossKill();
        var a = new StreamGenerator(new StreamGeneratorOptions { Seed = 9 }).Generate(s, Start);
        var b = new StreamGenerator(new StreamGeneratorOptions { Seed = 9 }).Generate(s, Start);
        var c = new StreamGenerator(new StreamGeneratorOptions { Seed = 10 }).Generate(s, Start);
        Assert.Equal(a.Chunks.Count, b.Chunks.Count);
        Assert.True(a.Chunks.Zip(b.Chunks).All(p => p.First.TimeUtc == p.Second.TimeUtc && p.First.Data.AsSpan().SequenceEqual(p.Second.Data)));
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.NotEqual(Convert.ToHexString(a.Bytes), Convert.ToHexString(c.Bytes));
    }

    [Fact]
    public void Concatenated_chunks_equal_the_frame_bytes()
    {
        var s = ScenarioLibrary.BossKill();
        var g = new StreamGenerator().Generate(s, Start);
        var concat = g.Chunks.SelectMany(c => c.Data).ToArray();
        Assert.Equal(g.Bytes, concat);

        // An independent framer (K4os LZ4) recovers exactly the generated frames, in order, with their bundle depth.
        var parsed = ReferenceFramer.Parse(concat);
        Assert.Equal(g.Frames.Count, parsed.Count);
        for (int i = 0; i < parsed.Count; i++)
        {
            Assert.Equal(g.Frames[i].Payload, parsed[i].Payload);
            Assert.Equal(g.Frames[i].BundleDepth, parsed[i].Depth);
        }

        // Every scripted event is in the stream exactly once, in script order.
        var scripted = g.Frames.Where(f => f.Source is not null).Select(f => f.Source!).ToList();
        Assert.Equal(s.Events, scripted);
    }

    [Fact]
    public void Stream_has_bundles_padding_heartbeats_and_hard_splits()
    {
        var s = ScenarioLibrary.BossKill();
        var g = new StreamGenerator().Generate(s, Start);
        Assert.InRange(g.BundledCombatShare, 0.33, 0.47);
        Assert.True(g.BundleCount > 100);
        Assert.True(g.PaddingBytes > 0);
        Assert.True(g.SplitsInsideVarint > 0, "no split inside a length varint");
        Assert.True(g.SplitsInsideBundle > 0, "no split inside a bundle");
        Assert.All(g.Chunks, c => Assert.InRange(c.Data.Length, 1, 1460));
        Assert.True(g.Chunks.Zip(g.Chunks.Skip(1)).All(p => p.First.TimeUtc <= p.Second.TimeUtc));

        int heartbeats = g.Frames.Count(f => f.Opcode == Opcodes.Heartbeat);
        Assert.InRange(heartbeats / s.Duration.TotalSeconds, 17, 21);
        Assert.Contains(g.Frames, f => f.Opcode == Opcodes.Ping);
        Assert.Contains(g.Frames, f => f.BundleDepth == 1 && f.Opcode == Opcodes.Damage);
        Assert.Contains(g.Frames, f => f.BundleDepth == 0 && f.Opcode == Opcodes.Damage);
        Assert.Contains(g.Frames, f => f.Payload.Length + 4 >= 128); // 2-byte length varints occur
    }

    [Fact]
    public void Frame_events_carry_the_scripted_time_and_depth()
    {
        var s = ScenarioLibrary.TrashPull();
        var g = new StreamGenerator(new StreamGeneratorOptions { Seed = 4 }).Generate(s, Start);
        foreach (var f in g.Frames.Where(f => f.Source is not null))
        {
            Assert.Equal(Start + f.Source!.Offset, f.TimeUtc);
            Assert.Equal(f.TimeUtc, f.Event!.Time);
            Assert.Equal(f.BundleDepth, f.Event.BundleDepth);
        }

        // Chunks carrying a frame's last byte share its timestamp: walk the chunks and frames together.
        int frameIndex = 0;
        long consumed = 0;
        var frameEnds = new List<long>();
        long pos = 0;
        foreach (var unit in ReferenceFramerTopLevel(g.Bytes)) frameEnds.Add(pos += unit);
        Assert.Equal(g.Bytes.Length, frameEnds[^1]);
        foreach (var chunk in g.Chunks)
        {
            consumed += chunk.Data.Length;
            while (frameIndex < frameEnds.Count && frameEnds[frameIndex] <= consumed) frameIndex++;
        }

        Assert.Equal(frameEnds.Count, frameIndex);
    }

    /// <summary>Top-level unit sizes (padding bytes count as 1-byte units).</summary>
    private static IEnumerable<int> ReferenceFramerTopLevel(byte[] bytes)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            if (bytes[i] == 0) { i++; yield return 1; continue; }
            uint l = 0;
            int shift = 0, w = 0;
            while (true)
            {
                byte b = bytes[i + w++];
                l |= (uint)(b & 0x7F) << shift;
                if (b < 0x80) break;
                shift += 7;
            }

            int total = (int)l + w - 4;
            i += total;
            yield return total;
        }
    }

    [Fact]
    public void Nested_bundles_when_enabled()
    {
        var g = new StreamGenerator(new StreamGeneratorOptions { NestedBundleChance = 0.5 }).Generate(ScenarioLibrary.TrainingDummy(), Start);
        Assert.Contains(g.Frames, f => f.BundleDepth == 2);
        var parsed = ReferenceFramer.Parse(g.Bytes);
        Assert.Equal(g.Frames.Select(f => f.BundleDepth), parsed.Select(p => p.Depth));
    }

    [Fact]
    public void Expected_events_match_the_script()
    {
        var s = ScenarioLibrary.PvpSkirmish();
        var g = new StreamGenerator().Generate(s, Start);
        var expected = g.ExpectedEvents.ToList();
        Assert.Equal(g.Frames.Count, expected.Count);
        var damage = expected.OfType<DamageEvent>().ToList();
        Assert.Equal(s.Events.Count(e => e.Event is DamageEvent), damage.Count);
        Assert.Equal(s.Truth.Players.Values.Sum(p => p.Damage),
            damage.Where(d => d.Amount is not null && d.Actor != d.Target && d.SkillRaw != SkillIds.Dodge).Sum(d => d.Amount!.Value));
    }

    [Fact]
    public void Feed_to_delivers_every_chunk_in_order()
    {
        var g = new StreamGenerator().Generate(ScenarioLibrary.TrainingDummy(), Start);
        var sink = new ListSink();
        var ticks = new List<DateTime>();
        g.FeedTo(sink, clock: ticks.Add);
        Assert.Equal([DiscontinuityReason.NewConnection], sink.Discontinuities);
        Assert.Equal(g.Bytes, sink.Data.SelectMany(d => d.Bytes).ToArray());
        Assert.Equal(g.Chunks.Select(c => c.TimeUtc), ticks);
    }

    private sealed class ListSink : IStreamSink
    {
        public readonly List<(DateTime Time, byte[] Bytes)> Data = new();
        public readonly List<DiscontinuityReason> Discontinuities = new();
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data) => Data.Add((timeUtc, data.ToArray()));
        public void OnDiscontinuity(DiscontinuityReason reason) => Discontinuities.Add(reason);
    }

    [Fact]
    public void Idle_stream_is_heartbeats_only()
    {
        var chunks = new StreamGenerator().GenerateIdle(Start, TimeSpan.FromSeconds(2));
        Assert.InRange(chunks.Count, 37, 39);
        Assert.All(chunks, c => Assert.Equal(11, c.Data.Length));
        Assert.All(chunks, c => Assert.Equal(new byte[] { 0x0E, 0x00, 0x36 }, c.Data[..3]));
    }
}

public class PcapExporterTests
{
    private sealed record PcapPacket(DateTime Time, byte[] Src, byte[] Dst, ushort SrcPort, ushort DstPort, uint Seq, uint Ack, byte Flags, byte[] Payload);

    /// <summary>Tiny classic-pcap reader: global header, records, Ethernet II / IPv4 / TCP, with checksum verification.</summary>
    private static List<PcapPacket> ReadPcap(byte[] file)
    {
        Assert.Equal(PcapExporter.Magic, BinaryPrimitives.ReadUInt32LittleEndian(file));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)));
        Assert.Equal(65535u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20)));
        var list = new List<PcapPacket>();
        int pos = 24;
        while (pos < file.Length)
        {
            uint sec = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
            uint usec = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
            int incl = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 8));
            int orig = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 12));
            Assert.Equal(incl, orig);
            Assert.InRange(usec, 0u, 999_999u);
            var pkt = file.AsSpan(pos + 16, incl);
            pos += 16 + incl;

            Assert.Equal(0x0800, BinaryPrimitives.ReadUInt16BigEndian(pkt[12..]));
            var ip = pkt[14..];
            Assert.Equal(0x45, ip[0]);
            int ipLen = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
            Assert.Equal(pkt.Length - 14, ipLen);
            Assert.Equal(6, ip[9]);
            Assert.Equal(0, PcapExporter.Checksum(ip[..20], 0)); // header checksum verifies to 0
            var tcp = ip[20..ipLen];
            uint pseudo = 0;
            for (int i = 12; i < 20; i += 2) pseudo += (uint)(ip[i] << 8 | ip[i + 1]);
            pseudo += 6 + (uint)tcp.Length;
            Assert.Equal(0, PcapExporter.Checksum(tcp, pseudo));
            int dataOffset = (tcp[12] >> 4) * 4;
            list.Add(new PcapPacket(
                DateTime.UnixEpoch.AddTicks(sec * TimeSpan.TicksPerSecond + usec * 10),
                ip.Slice(12, 4).ToArray(), ip.Slice(16, 4).ToArray(),
                BinaryPrimitives.ReadUInt16BigEndian(tcp), BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]),
                BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]), BinaryPrimitives.ReadUInt32BigEndian(tcp[8..]), tcp[13],
                tcp[dataOffset..].ToArray()));
        }

        return list;
    }

    [Fact]
    public void Pcap_structure_parses_and_reassembles_to_the_stream()
    {
        var s = ScenarioLibrary.PvpSkirmish();
        var start = new DateTime(2026, 10, 6, 21, 30, 0, DateTimeKind.Utc);
        var g = new StreamGenerator().Generate(s, start);
        using var ms = new MemoryStream();
        PcapExporter.Write(ms, g.Chunks);
        var packets = ReadPcap(ms.ToArray());

        Assert.True(packets.Zip(packets.Skip(1)).All(p => p.First.Time <= p.Second.Time), "packets out of time order");

        var server = packets.Where(p => p.SrcPort == 13328 && p.DstPort == 62311).ToList();
        var client = packets.Where(p => p.SrcPort == 62311 && p.DstPort == 13328).ToList();
        var decoy = packets.Where(p => p.SrcPort == 443 || p.DstPort == 443).ToList();

        // Handshake.
        Assert.Equal(0x02, client[0].Flags);
        Assert.Equal(0x12, server[0].Flags);
        Assert.Equal(client[0].Seq + 1, server[0].Ack);

        // Server data: contiguous sequence numbers, payload = the generated stream, timestamps preserved.
        var data = server.Where(p => p.Payload.Length > 0).ToList();
        uint seq = server[0].Seq + 1;
        foreach (var p in data)
        {
            Assert.Equal(seq, p.Seq);
            seq += (uint)p.Payload.Length;
            Assert.InRange(p.Payload.Length, 1, 1460);
        }

        Assert.Equal(g.Bytes, data.SelectMany(p => p.Payload).ToArray());
        Assert.Equal(g.Chunks.Count, data.Count);
        Assert.Equal(g.Chunks[0].TimeUtc, data[0].Time);

        // Client data packets advance their own seq; the server acks them.
        var clientData = client.Where(p => p.Payload.Length > 0).ToList();
        Assert.True(clientData.Count >= 5);
        uint cseq = client[0].Seq + 1;
        foreach (var p in clientData) { Assert.Equal(cseq, p.Seq); cseq += (uint)p.Payload.Length; }
        Assert.Equal(cseq, data[^1].Ack);

        // TLS decoy flow with application-data records.
        Assert.True(decoy.Count > 5);
        Assert.Contains(decoy, p => p.Payload.Length > 5 && p.Payload[0] == 0x17 && p.Payload[1] == 0x03 && p.Payload[2] == 0x03);
    }

    [Fact]
    public void Write_file_round_trip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"aion2dps-sim-{Guid.NewGuid():N}.pcap");
        try
        {
            var g = PcapExporter.WriteFile(path, ScenarioLibrary.TrainingDummy(), new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc),
                options: new PcapExportOptions { IncludeTlsDecoy = false, ClientPacketIntervalSeconds = 0, IncludeHandshake = false });
            var packets = ReadPcap(File.ReadAllBytes(path));
            Assert.All(packets, p => Assert.Equal(13328, p.SrcPort));
            Assert.Equal(g.Bytes, packets.SelectMany(p => p.Payload).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Oversized_chunks_are_split_into_segments()
    {
        var big = new byte[4000];
        new Random(1).NextBytes(big);
        using var ms = new MemoryStream();
        PcapExporter.Write(ms, [new StreamChunk(DateTime.UtcNow, big)], new PcapExportOptions { IncludeTlsDecoy = false, IncludeHandshake = false, ClientPacketIntervalSeconds = 0 });
        var packets = ReadPcap(ms.ToArray());
        Assert.Equal(3, packets.Count);
        Assert.Equal(big, packets.SelectMany(p => p.Payload).ToArray());
    }
}

public class SimulatedCaptureServiceTests
{
    private sealed class CollectingSink : IStreamSink
    {
        public readonly ConcurrentQueue<(DateTime Time, byte[] Data)> Chunks = new();
        public readonly ConcurrentQueue<DiscontinuityReason> Discontinuities = new();
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data) => Chunks.Enqueue((timeUtc, data.ToArray()));
        public void OnDiscontinuity(DiscontinuityReason reason) => Discontinuities.Enqueue(reason);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 20_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }

        return condition();
    }

    [Fact]
    public void Plays_a_scenario_as_fast_as_possible_and_finishes()
    {
        var scenario = ScenarioLibrary.TrainingDummy();
        var options = new SimulationOptions { Speed = 0, Loop = false, Scenarios = [scenario], Seed = 3, GapSeconds = 2 };
        using var service = new SimulatedCaptureService(options);
        var statuses = new ConcurrentQueue<CaptureStatus>();
        service.StatusChanged += statuses.Enqueue;
        var sink = new CollectingSink();
        var before = DateTime.UtcNow;
        service.Start(sink);

        Assert.True(WaitFor(() => service.Status.State == CaptureState.Stopped), "simulation did not finish");
        Assert.Equal(DiscontinuityReason.NewConnection, Assert.Single(sink.Discontinuities));

        var generator = new StreamGenerator(new StreamGeneratorOptions { Seed = 3 });
        var expected = generator.Generate(scenario, before);
        int idleBytes = generator.GenerateIdle(before, TimeSpan.FromSeconds(2)).Sum(c => c.Data.Length);
        var received = sink.Chunks.SelectMany(c => c.Data).ToArray();
        Assert.Equal(expected.Bytes.Length + idleBytes, received.Length);
        // Same frames (heartbeat bodies differ only by the wall-clock start).
        static IEnumerable<string> NonHeartbeat(byte[] bytes) =>
            ReferenceFramer.Parse(bytes).Where(f => !(f.Payload[0] == 0x00 && f.Payload[1] == 0x36)).Select(f => Convert.ToHexString(f.Payload));
        Assert.Equal(NonHeartbeat(expected.Bytes), NonHeartbeat(received));

        var times = sink.Chunks.Select(c => c.Time).ToList();
        Assert.True(times.Zip(times.Skip(1)).All(p => p.First <= p.Second));
        Assert.InRange(times[0], before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.InRange((times[^1] - times[0]).TotalSeconds, scenario.Duration.TotalSeconds, scenario.Duration.TotalSeconds + 3);

        Assert.Contains(statuses, st => st.State == CaptureState.Capturing && st.AdapterName == "Simulated" && st.ServerEndpoint == "simulated:13328");
        Assert.Equal(received.Length, service.Status.BytesDelivered);
        Assert.Equal(sink.Chunks.Count, service.Status.PacketsSeen);
    }

    [Fact]
    public void Real_time_playback_is_paced_and_stops_cleanly()
    {
        using var service = new SimulatedCaptureService(new SimulationOptions { Speed = 1.0, Scenarios = [ScenarioLibrary.BossKill()] });
        var sink = new CollectingSink();
        int statusEvents = 0;
        service.StatusChanged += _ => Interlocked.Increment(ref statusEvents);
        service.Start(sink);
        service.Start(sink); // second start is ignored
        Assert.Equal(CaptureState.Capturing, service.Status.State);
        Assert.Equal("simulated:13328", service.Status.ServerEndpoint);
        Assert.Equal("Simulated", service.Status.AdapterName);
        Thread.Sleep(1600);
        service.Stop();
        Assert.Equal(CaptureState.Stopped, service.Status.State);

        // ~1.6 s of a real-time stream: heartbeats at ~19/s, nowhere near the whole 94 s scenario.
        int heartbeatsApprox = sink.Chunks.Count;
        Assert.InRange(heartbeatsApprox, 10, 400);
        var first = sink.Chunks.First().Time;
        var last = sink.Chunks.Last().Time;
        Assert.InRange((last - first).TotalSeconds, 0.8, 2.5);
        Assert.InRange(first, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow);
        Assert.True(statusEvents >= 3); // start, ~1/s, stop

        int count = sink.Chunks.Count;
        Thread.Sleep(200);
        Assert.Equal(count, sink.Chunks.Count); // nothing after Stop
        service.Stop();    // idempotent
        service.Dispose(); // safe after stop
    }

    [Fact]
    public void Speed_multiplier_compresses_time()
    {
        using var service = new SimulatedCaptureService(new SimulationOptions { Speed = 20, Scenarios = [ScenarioLibrary.TrainingDummy()], Loop = false });
        var sink = new CollectingSink();
        service.Start(sink);
        Thread.Sleep(1000);
        service.Stop();
        var first = sink.Chunks.First().Time;
        var last = sink.Chunks.Last().Time;
        Assert.InRange((last - first).TotalSeconds, 0.5, 1.6); // wall-clock based timestamps
        Assert.True(sink.Chunks.Count > 150); // ~20 s of scenario in 1 s
    }

    [Fact]
    public void Defaults_and_lifecycle_are_safe()
    {
        var service = new SimulatedCaptureService();
        Assert.Equal(CaptureState.Stopped, service.Status.State);
        Assert.Equal("Simulated", Assert.Single(service.GetAdapters()).Name);
        service.AdapterOverride = "x";
        service.StartRecording("ignored.pcapng");
        service.StopRecording();
        service.Stop();
        service.Dispose();
        service.Dispose();
        Assert.Null(service.Options.Scenarios);
        Assert.True(service.Options.Loop);
        Assert.Equal(1.0, service.Options.Speed);
    }

    [Fact]
    public void Sink_exceptions_do_not_kill_playback()
    {
        using var service = new SimulatedCaptureService(new SimulationOptions { Speed = 0, Loop = false, Scenarios = [ScenarioLibrary.TrainingDummy()], GapSeconds = 0 });
        int calls = 0;
        var sink = new ThrowingSink(() => Interlocked.Increment(ref calls));
        service.Start(sink);
        Assert.True(WaitFor(() => service.Status.State == CaptureState.Stopped));
        Assert.True(calls > 100);
    }

    private sealed class ThrowingSink(Action onCall) : IStreamSink
    {
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
        {
            onCall();
            throw new InvalidOperationException("boom");
        }

        public void OnDiscontinuity(DiscontinuityReason reason) { }
    }
}
