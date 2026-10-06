using System.Diagnostics;
using Aion2Dps.Contracts;
using Xunit.Abstractions;

namespace Aion2Dps.Protocol.Tests;

[CollectionDefinition("Throughput", DisableParallelization = true)]
public class ThroughputCollection;

/// <summary>Timing tests run alone so other tests do not compete for the CPU.</summary>
[Collection("Throughput")]
public class ThroughputTests(ITestOutputHelper output)
{
    private sealed class CountingSink : IGameEventSink
    {
        public long Count;
        public long Damage;

        public void OnEvent(GameEvent gameEvent)
        {
            Count++;
            if (gameEvent is DamageEvent { Amount: long a }) Damage += a;
        }
    }

    private static byte[] SyntheticCombatStream(int targetBytes)
    {
        var rng = new Random(11);
        var ms = new MemoryStream(targetBytes + 70_000);
        var bundleInner = new List<byte[]>();
        while (ms.Length < targetBytes)
        {
            int kind = rng.Next(10);
            byte[] frame = kind switch
            {
                < 5 => W.DamageFrame((uint)rng.Next(1, 60000), (uint)rng.Next(1, 60000), (uint)rng.Next(1, 200000)),
                5 => W.Frame("0538D490020AC522108BAF3360A804E046F600"),
                6 => W.Frame("008DCE8D0102010008DD010000000000"),
                7 => W.Heartbeat((ulong)ms.Length),
                8 => W.Frame("0438A2BC023600B43683C7D500EE0280000118A1815301000000FA60E9EF0104FD17FD17FD17FD170100"),
                _ => W.Frame("2a38f81b011335ade5cc0ae02e00000000000065a020f4a0010000ec1b0c5e7d1401004f576fc60aa7724600c02144"),
            };

            if (rng.Next(4) == 0)
            {
                bundleInner.Add(frame);
                if (bundleInner.Count >= 30)
                {
                    ms.Write(W.Bundle(W.Concat(bundleInner.ToArray())));
                    bundleInner.Clear();
                }
            }
            else
            {
                ms.Write(frame);
            }
        }

        return ms.ToArray();
    }

    [Fact]
    public void Pipeline_throughput_is_at_least_20_MB_per_second()
    {
        var wire = SyntheticCombatStream(8 * 1024 * 1024);

        // Warm-up (JIT).
        var warm = new ProtocolPipeline(new CountingSink());
        Run.Feed(warm.Input, wire[..(256 * 1024)], 1460);

        double best = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var sink = new CountingSink();
            var pipeline = new ProtocolPipeline(sink);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < wire.Length; i += 1460)
                pipeline.Input.OnData(W.T0, wire.AsSpan(i, Math.Min(1460, wire.Length - i)));
            sw.Stop();

            double mbps = wire.Length / 1_048_576.0 / sw.Elapsed.TotalSeconds;
            best = Math.Max(best, mbps);
            output.WriteLine($"attempt {attempt}: {wire.Length:N0} bytes in {sw.Elapsed.TotalMilliseconds:F1} ms = {mbps:F1} MB/s, {sink.Count:N0} events");
            Assert.Equal(0, pipeline.Diagnostics.DecodeErrors);
            Assert.Equal(0, pipeline.Diagnostics.Resyncs);
            Assert.True(pipeline.Diagnostics.Bundles > 0);
            if (best >= 40) break;
        }

        Assert.True(best >= 20, $"throughput {best:F1} MB/s < 20 MB/s");
    }

    [Fact]
    public void Frame_decoder_alone_is_allocation_light()
    {
        var wire = SyntheticCombatStream(2 * 1024 * 1024);
        var dec = new FrameDecoder(new NullFrameSink());
        Run.Feed(dec, wire[..65536], 1460); // warm-up and buffer growth
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 65536; i < wire.Length; i += 1460)
            dec.OnData(W.T0, wire.AsSpan(i, Math.Min(1460, wire.Length - i)));
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"allocated {allocated:N0} bytes for {wire.Length:N0} input bytes; framer alone {(wire.Length - 65536) / 1_048_576.0 / sw.Elapsed.TotalSeconds:F1} MB/s");
        Assert.True(allocated < wire.Length / 10, $"framer allocated {allocated:N0} bytes");
    }

    private sealed class NullFrameSink : IFrameSink
    {
        public void OnFrame(in Frame frame) { }
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void Error_ring_keeps_the_most_recent_entries_in_order()
    {
        var d = new ProtocolDiagnostics(recentErrorCapacity: 3);
        for (int i = 0; i < 5; i++) d.RecordError(W.T0, $"e{i}");
        var errors = d.GetRecentErrors();
        Assert.Equal(3, errors.Count);
        Assert.EndsWith("e2", errors[0]);
        Assert.EndsWith("e4", errors[2]);
    }

    [Fact]
    public void Counters_are_thread_safe()
    {
        var d = new ProtocolDiagnostics();
        Parallel.For(0, 8, worker =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                d.RecordFrame(0x0438, 10, W.T0, delivered: true);
                d.RecordDecoded(0x0438);
                d.IncrementEvents();
                if (i % 100 == 0) d.RecordError(W.T0, "x");
                if (i % 1000 == 0) _ = d.GetCensus();
            }
        });

        var stat = d.GetStat(0x0438);
        Assert.Equal(80_000, stat.Count);
        Assert.Equal(800_000, stat.Bytes);
        Assert.Equal(80_000, stat.Decoded);
        Assert.Equal(80_000, d.Frames);
        Assert.Equal(80_000, d.EventsEmitted);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var (_, pipe) = Run.Wire(W.Concat(W.Heartbeat(), W.Frame(FrameDecoderTests.RealBundle)));
        var d = pipe.ProtocolDiagnostics;
        Assert.True(d.Frames > 0);
        d.RecordError(W.T0, "x");
        d.Reset();
        Assert.Equal(0, d.Frames);
        Assert.Equal(0, d.BytesIn);
        Assert.Equal(0, d.Bundles);
        Assert.Null(d.LastFrameUtc);
        Assert.Empty(d.GetCensus());
        Assert.Empty(d.GetRecentErrors());
    }

    [Fact]
    public void Pipeline_exposes_shared_diagnostics()
    {
        var events = new EventCollector();
        var p = new ProtocolPipeline(events);
        Assert.Same(p.FrameDecoder, p.Input);
        Assert.Same(p.ProtocolDiagnostics, p.Diagnostics);
        Assert.Same(p.ProtocolDiagnostics, p.FrameDecoder.Diagnostics);
        Assert.Same(p.ProtocolDiagnostics, p.PacketDecoder.Diagnostics);
        Assert.Same(OpcodeTable.Default, p.Opcodes);
        var wire = W.Concat(W.Heartbeat(), W.Frame("0438" + "b1ea01 34 00 f30a e0b7f800 cd 02 8bd32761 01000000 ac52 d330 01 01 59".Replace(" ", "")));
        p.Input.OnData(W.T0, wire);
        Assert.Equal(wire.Length, p.Diagnostics.BytesIn);
        Assert.Equal(2, p.Diagnostics.Frames);
        Assert.Equal(2, p.Diagnostics.EventsEmitted);
        Assert.Equal(W.T0, p.Diagnostics.LastFrameUtc);
    }

    [Fact]
    public void Failed_frames_are_recorded_with_hex()
    {
        var (_, diag) = Run.Body(Opcodes.EntityStats, "CE8D0104");
        var error = Assert.Single(diag.GetRecentErrors());
        Assert.Contains("00 8D", error);
        Assert.Contains("CE8D0104", error);
        Assert.Equal(1, diag.GetStat(Opcodes.EntityStats).Failed);
    }
}
