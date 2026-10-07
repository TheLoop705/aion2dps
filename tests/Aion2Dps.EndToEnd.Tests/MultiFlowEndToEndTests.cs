using Aion2Dps.Capture;
using Aion2Dps.Combat;
using Aion2Dps.Contracts;
using Aion2Dps.Protocol;
using Aion2Dps.Simulator;
using Xunit.Abstractions;

namespace Aion2Dps.EndToEnd.Tests;

/// <summary>Real multi-flow pipeline: MultiFlowProtocolPipeline (frame decoder per flow) → one CombatEngine.</summary>
internal sealed class MultiFlowHarness
{
    public MultiFlowHarness(EngineOptions? options = null)
    {
        Engine = new CombatEngine(Harness.GameData, options ?? new EngineOptions { SaveTrashFights = true });
        Engine.EncounterCompleted += r => { lock (Records) Records.Add(r); };
        Pipeline = new MultiFlowProtocolPipeline(Engine, OpcodeTable.LoadOrDefault(), onDiscontinuity: (flow, reason) =>
        {
            Discontinuities.Add((flow, reason));
            if (reason == DiscontinuityReason.TcpGap) Engine.NotifyCaptureGap();
        });
    }

    public CombatEngine Engine { get; }
    public MultiFlowProtocolPipeline Pipeline { get; }
    public IStreamSink Input => Pipeline.Input;
    public List<EncounterRecord> Records { get; } = new();
    public List<(string? Flow, DiscontinuityReason Reason)> Discontinuities { get; } = new();

    public void Drain(DateTime lastUtc)
    {
        for (int s = 1; s <= 60; s++) Engine.Tick(lastUtc.AddSeconds(s));
    }
}

/// <summary>
/// LIVE-FINDINGS NEW 1: the client keeps a world connection and a dungeon-instance connection open at the same time.
/// The simulator puts heartbeats, the own-character record, the party roster and unknown-opcode noise on the world flow
/// and the combat on a second, concurrent instance flow; the decoded encounters must still match the ground truth
/// exactly, which needs BOTH flows (the negative controls show that either flow alone, or a single-stream sink, fails).
/// </summary>
public sealed class MultiFlowEndToEndTests
{
    private static readonly DateTime Start = new(2026, 10, 6, 19, 43, 0, DateTimeKind.Utc);
    private readonly ITestOutputHelper _out;

    public MultiFlowEndToEndTests(ITestOutputHelper output) => _out = output;

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in ScenarioLibrary.Names)
            foreach (var seed in new[] { 1, 5 })
                data.Add(name, seed);
        return data;
    }

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in ScenarioLibrary.Names) data.Add(name);
        return data;
    }

    private void AssertMatches(Scenario scenario, IReadOnlyList<EncounterRecord> records)
    {
        var errors = TruthComparer.CompareAll(scenario, records, Start);
        foreach (var e in errors) _out.WriteLine(e);
        Assert.True(errors.Count == 0, $"{scenario.Name}: {errors.Count} mismatches:\n" + string.Join("\n", errors.Take(40)));
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-mf-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Two_concurrent_flows_in_a_pcap_match_ground_truth(string name, int seed)
    {
        var scenario = ScenarioLibrary.Get(name, seed);
        string dir = TempDir();
        try
        {
            string pcap = Path.Combine(dir, name + ".pcap");
            var generated = MultiFlowPcapExporter.WriteFile(pcap, scenario, Start, new MultiFlowOptions { Seed = seed * 13 + 1 });
            Assert.True(generated.NoiseFrames > 10);
            Assert.NotEmpty(generated.WorldEvents);

            var h = new MultiFlowHarness();
            DateTime last = Start;
            var stats = PcapReplaySource.Replay(pcap, h.Input, speed: 0, clock: t => { last = t; h.Engine.Tick(t); });
            h.Drain(last);

            Assert.Equal(2, stats.Flows);
            Assert.Equal(2, stats.MaxConcurrentFlows);
            Assert.Equal("87.232.75.150:13328 + 193.202.112.155:13328", stats.Servers);
            Assert.Equal(generated.TotalBytes, stats.BytesDelivered);
            Assert.Equal(0, stats.Gaps);
            Assert.Equal(0, stats.DroppedBytes);
            Assert.Equal(1, stats.TlsFlowsIgnored);
            Assert.Equal(0, h.Pipeline.Diagnostics.DecodeErrors);
            Assert.Equal(0, h.Pipeline.Diagnostics.BundleErrors);

            var flows = h.Pipeline.GetFlowStats();
            Assert.Equal(2, flows.Count);
            var world = flows.Single(f => f.FlowKey.StartsWith("87.232.75.150:13328>", StringComparison.Ordinal));
            var instance = flows.Single(f => f.FlowKey.StartsWith("193.202.112.155:13328>", StringComparison.Ordinal));
            Assert.Equal(generated.WorldBytes.LongLength, world.BytesIn);
            Assert.Equal(generated.Instance.Bytes.LongLength, instance.BytesIn);
            Assert.True(world.Events > 0, "world flow events");
            Assert.True(instance.Events > 0, "instance flow events");
            Assert.All(flows, f => Assert.Equal(0, f.DecodeErrors));
            Assert.All(flows, f => Assert.False(f.IsOpen)); // instance by FIN, world at the end of the file

            AssertMatches(scenario, h.Records);
            Assert.All(h.Records, r => Assert.False(r.CaptureGaps));
            Assert.Equal(scenario.Truth.LocalPlayerName, h.Engine.LocalPlayer?.Name); // only the world flow carries it
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Two_flows_interleaved_chunk_by_chunk_into_flow_sinks_match_ground_truth(string name, int seed)
    {
        var scenario = ScenarioLibrary.Get(name, seed);
        var stream = MultiFlowPcapExporter.Generate(scenario, Start, new MultiFlowOptions
        {
            Seed = seed * 7 + 3,
            Stream = new StreamGeneratorOptions { NestedBundleChance = 0.2, BundleFraction = 0.6 },
        });
        var h = new MultiFlowHarness();
        var factory = (IStreamSinkFactory)h.Input;
        var sinks = new[] { factory.CreateFlowSink("world"), factory.CreateFlowSink("instance") };
        var rng = new Random(seed);
        DateTime last = Start;
        foreach (var (flow, chunk) in stream.Interleaved())
        {
            // Re-split each chunk at random points; the other flow's bytes interleave between the parts' flows.
            var data = chunk.Data.AsSpan();
            while (data.Length > 0)
            {
                int n = rng.NextDouble() < 0.4 ? data.Length : 1 + rng.Next(data.Length);
                sinks[flow].OnData(chunk.TimeUtc, data[..n]);
                data = data[n..];
            }

            h.Engine.Tick(chunk.TimeUtc);
            last = chunk.TimeUtc;
        }

        factory.ReleaseFlowSink("instance");
        h.Drain(last);
        Assert.Equal(0, h.Pipeline.Diagnostics.DecodeErrors);
        Assert.Equal(2, h.Pipeline.MaxConcurrentFlows);
        AssertMatches(scenario, h.Records);
    }

    [Fact]
    public void A_gap_on_the_instance_flow_marks_the_encounter_and_leaves_the_world_flow_alone()
    {
        var scenario = ScenarioLibrary.BossKill();
        var stream = MultiFlowPcapExporter.Generate(scenario, Start, new MultiFlowOptions { Seed = 4 });
        var h = new MultiFlowHarness();
        var factory = (IStreamSinkFactory)h.Input;
        var world = factory.CreateFlowSink("world");
        var instance = factory.CreateFlowSink("instance");
        bool gapSent = false;
        DateTime last = Start;
        foreach (var (flow, chunk) in stream.Interleaved())
        {
            if (flow == MultiFlowStream.InstanceFlow && !gapSent && chunk.TimeUtc >= Start.AddSeconds(40))
            {
                gapSent = true;
                instance.OnDiscontinuity(DiscontinuityReason.TcpGap);
            }

            (flow == MultiFlowStream.WorldFlow ? world : instance).OnData(chunk.TimeUtc, chunk.Data);
            h.Engine.Tick(chunk.TimeUtc);
            last = chunk.TimeUtc;
        }

        h.Drain(last);
        var record = Assert.Single(h.Records);
        Assert.True(record.CaptureGaps);
        Assert.Equal(EncounterOutcome.Kill, record.Outcome);
        Assert.Equal(new[] { ("instance", DiscontinuityReason.TcpGap) }, h.Discontinuities.Select(d => (d.Flow!, d.Reason)));
        var stats = h.Pipeline.GetFlowStats();
        Assert.Equal(0, stats.Single(s => s.FlowKey == "world").TcpGaps);
        Assert.Equal(0, stats.Single(s => s.FlowKey == "world").Resyncs);
        Assert.Equal(1, stats.Single(s => s.FlowKey == "instance").TcpGaps);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Negative_control_either_flow_alone_does_not_match(string name)
    {
        // Proves the tests above need both flows: identity is only on the world flow, combat only on the instance flow.
        var scenario = ScenarioLibrary.Get(name, 1);
        var stream = MultiFlowPcapExporter.Generate(scenario, Start, new MultiFlowOptions { Seed = 9 });

        var instanceOnly = new MultiFlowHarness();
        stream.Instance.FeedTo(instanceOnly.Input, clock: instanceOnly.Engine.Tick);
        instanceOnly.Drain(stream.Instance.Chunks[^1].TimeUtc);
        Assert.Null(instanceOnly.Engine.LocalPlayer);
        Assert.NotEmpty(TruthComparer.CompareAll(scenario, instanceOnly.Records, Start));

        var worldOnly = new MultiFlowHarness();
        worldOnly.Input.OnDiscontinuity(DiscontinuityReason.NewConnection);
        foreach (var c in stream.WorldChunks) worldOnly.Input.OnData(c.TimeUtc, c.Data);
        Assert.Equal(scenario.Truth.LocalPlayerName, worldOnly.Engine.LocalPlayer?.Name);
        Assert.Empty(worldOnly.Records);
    }

    [Fact]
    public void Before_the_fix_a_single_stream_sink_loses_the_world_flow()
    {
        // The pre-multi-flow composition (one ProtocolPipeline behind a plain sink) can follow only one connection: the
        // newest (instance) flow owns it, so the world flow's identity records never reach the engine.
        var scenario = ScenarioLibrary.BossKill();
        string dir = TempDir();
        try
        {
            string pcap = Path.Combine(dir, "legacy.pcap");
            // The world server sends the character record 1 s after the instance connection opened.
            MultiFlowPcapExporter.WriteFile(pcap, scenario, Start, new MultiFlowOptions { Seed = 2, WorldIdentityDelaySeconds = 1.0 });
            var legacy = new Harness();
            DateTime last = Start;
            var stats = PcapReplaySource.Replay(pcap, legacy.Input, 0, t => { last = t; legacy.Engine.Tick(t); });
            legacy.Drain(last);
            Assert.True(stats.DroppedBytes > 0);
            Assert.NotEqual(scenario.Truth.LocalPlayerName, legacy.Engine.LocalPlayer?.Name);
            Assert.NotEmpty(TruthComparer.CompareAll(scenario, legacy.Records, Start));

            var fixedHarness = new MultiFlowHarness();
            last = Start;
            PcapReplaySource.Replay(pcap, fixedHarness.Input, 0, t => { last = t; fixedHarness.Engine.Tick(t); });
            fixedHarness.Drain(last);
            AssertMatches(scenario, fixedHarness.Records);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void World_flow_that_ends_mid_fight_is_released_without_disturbing_the_instance_flow()
    {
        var scenario = ScenarioLibrary.BossWipeThenKill();
        string dir = TempDir();
        try
        {
            string pcap = Path.Combine(dir, "world-closes.pcap");
            // The world connection closes 5 s into the scenario (FIN), the fight on the instance flow continues.
            var shortWorld = new MultiFlowOptions { Seed = 3, WorldEndOffsetSeconds = 5, CloseWorldAtEnd = true };
            var gen = MultiFlowPcapExporter.Generate(scenario, Start, shortWorld);
            var worldEnd = gen.WorldChunks[^1].TimeUtc;
            using (var file = File.Create(pcap)) MultiFlowPcapExporter.Write(file, gen, shortWorld);

            var h = new MultiFlowHarness();
            DateTime last = Start;
            var stats = PcapReplaySource.Replay(pcap, h.Input, 0, t => { last = t; h.Engine.Tick(t); });
            h.Drain(last);
            Assert.Equal(2, stats.Flows);
            Assert.All(stats.FlowDetails, f => Assert.Equal("connection closed (FIN)", f.EndReason));
            Assert.Equal(gen.TotalBytes, stats.BytesDelivered);
            AssertMatches(scenario, h.Records);
            Assert.True(worldEnd < Start.AddSeconds(6) && Start + scenario.Duration > Start.AddSeconds(30));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }
}
