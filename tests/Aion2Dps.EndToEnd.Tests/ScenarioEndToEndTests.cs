using Aion2Dps.Capture;
using Aion2Dps.Contracts;
using Aion2Dps.Simulator;
using Aion2Dps.Storage;
using Xunit.Abstractions;

namespace Aion2Dps.EndToEnd.Tests;

/// <summary>
/// Simulator scenario → wire stream (random chunking, LZ4 bundles, padding) → ProtocolPipeline → CombatEngine with the
/// real GameDataStore; every completed EncounterRecord must match the scenario ground truth exactly. The same scenario
/// also goes through a pcap file (PcapExporter → PcapReplaySource) and a SqliteFightStore round trip.
/// </summary>
public sealed class ScenarioEndToEndTests
{
    private readonly ITestOutputHelper _out;

    public ScenarioEndToEndTests(ITestOutputHelper output) => _out = output;

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in ScenarioLibrary.Names)
            foreach (var seed in new[] { 1, 2, 7 })
                data.Add(name, seed);
        return data;
    }

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in ScenarioLibrary.Names) data.Add(name);
        return data;
    }

    private static readonly DateTime Start = new(2026, 10, 6, 18, 30, 0, DateTimeKind.Utc);

    private void AssertMatches(Scenario scenario, IReadOnlyList<EncounterRecord> records, DateTime start)
    {
        var errors = TruthComparer.CompareAll(scenario, records, start);
        foreach (var e in errors) _out.WriteLine(e);
        Assert.True(errors.Count == 0, $"{scenario.Name}: {errors.Count} mismatches:\n" + string.Join("\n", errors.Take(40)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Stream_through_pipeline_matches_ground_truth(string name, int seed)
    {
        var scenario = ScenarioLibrary.Get(name, seed);
        var stream = new StreamGenerator(new StreamGeneratorOptions { Seed = seed * 31 + 5 }).Generate(scenario, Start);
        var h = new Harness();
        h.FeedRechunked(stream, seed);

        Assert.Equal(0, h.Pipeline.Diagnostics.DecodeErrors);
        Assert.Equal(0, h.Pipeline.Diagnostics.BundleErrors);
        Assert.True(h.Pipeline.Diagnostics.Bundles > 0, "the stream should contain LZ4 bundles");
        AssertMatches(scenario, h.Records, Start);
        Assert.All(h.Records, r => Assert.False(r.CaptureGaps));
        Assert.Equal(scenario.Truth.LocalPlayerName, h.Engine.LocalPlayer?.Name);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Nested_bundles_and_byte_by_byte_delivery_match_ground_truth(string name)
    {
        var scenario = ScenarioLibrary.Get(name, 3);
        var stream = new StreamGenerator(new StreamGeneratorOptions { Seed = 99, NestedBundleChance = 0.3, BundleFraction = 0.7 }).Generate(scenario, Start);
        var h = new Harness();
        h.Input.OnDiscontinuity(DiscontinuityReason.NewConnection);
        foreach (var chunk in stream.Chunks)
        {
            // Byte-by-byte for the first few seconds, then whole chunks.
            if (chunk.TimeUtc < Start.AddSeconds(5))
                for (int i = 0; i < chunk.Data.Length; i++) h.Input.OnData(chunk.TimeUtc, chunk.Data.AsSpan(i, 1));
            else
                h.Input.OnData(chunk.TimeUtc, chunk.Data);
            h.Engine.Tick(chunk.TimeUtc);
        }

        h.Drain(stream.Chunks[^1].TimeUtc);
        AssertMatches(scenario, h.Records, Start);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Pcap_replay_matches_ground_truth_and_survives_storage(string name)
    {
        var scenario = ScenarioLibrary.Get(name, 1);
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string pcap = Path.Combine(dir, name + ".pcap");
            var generated = PcapExporter.WriteFile(pcap, scenario, Start, new StreamGeneratorOptions { Seed = 11 });
            Assert.True(new FileInfo(pcap).Length > 10_000);

            var h = new Harness();
            DateTime last = Start;
            var stats = PcapReplaySource.Replay(pcap, h.Input, speed: 0, clock: t => { last = t; h.Engine.Tick(t); });
            h.Drain(last);

            Assert.Equal(generated.Bytes.LongLength, stats.BytesDelivered);
            Assert.Equal(0, stats.Gaps);
            Assert.Equal(1, stats.Locks);
            Assert.Equal(0, h.Pipeline.Diagnostics.DecodeErrors);
            AssertMatches(scenario, h.Records, Start);

            // Storage round trip: everything that was compared survives save/load.
            string db = Path.Combine(dir, "history.db");
            using (var store = new SqliteFightStore(db))
            {
                foreach (var r in h.Records) store.Save(r);
                var loaded = h.Records.Select(r => store.Load(r.Id)).ToList();
                Assert.All(loaded, Assert.NotNull);
                AssertMatches(scenario, loaded!, Start);
                for (int i = 0; i < loaded.Count; i++)
                    Assert.Equal(EncounterRecordSerializer.ToJson(h.Records[i]), EncounterRecordSerializer.ToJson(loaded[i]!));

                var summaries = store.Query(new FightQuery());
                Assert.Equal(h.Records.Count, summaries.Count);
                foreach (var r in h.Records)
                {
                    var s = summaries.Single(x => x.Id == r.Id);
                    Assert.Equal(r.TotalDamage, s.TotalDamage);
                    Assert.Equal(r.Kind, s.Kind);
                    Assert.Equal(r.Outcome, s.Outcome);
                }

                if (scenario.Truth.Final is { Kind: EncounterKind.Boss, Outcome: EncounterOutcome.Kill, BossNpcCode: uint boss } final)
                {
                    var pb = store.GetPersonalBest(boss, scenario.Truth.LocalPlayerName);
                    Assert.NotNull(pb);
                    var local = final.Players[scenario.Truth.LocalPlayerId];
                    Assert.Equal(local.Damage, pb!.Damage);
                    Assert.Contains(scenario.Truth.LocalPlayerName, store.GetCharacters());
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Default_cycle_back_to_back_produces_every_encounter()
    {
        // The demo cycle as one continuous connection: BossKill, TrashPull, BossWipeThenKill with idle gaps.
        var h = new Harness();
        h.Input.OnDiscontinuity(DiscontinuityReason.NewConnection);
        var t = Start;
        var expected = new List<(Scenario Scenario, DateTime Start)>();
        int seed = 1;
        foreach (var scenario in ScenarioLibrary.DefaultCycle())
        {
            var gen = new StreamGenerator(new StreamGeneratorOptions { Seed = seed++ });
            var stream = gen.Generate(scenario, t);
            expected.Add((scenario, t));
            foreach (var c in stream.Chunks.Concat(gen.GenerateIdle(t + scenario.Duration, TimeSpan.FromSeconds(12))))
            {
                h.Input.OnData(c.TimeUtc, c.Data);
                h.Engine.Tick(c.TimeUtc);
            }

            t += scenario.Duration + TimeSpan.FromSeconds(12);
        }

        h.Drain(t);
        int index = 0;
        foreach (var (scenario, start) in expected)
        {
            var mine = h.Records.Skip(index).Take(scenario.Truth.Encounters.Count).ToList();
            index += scenario.Truth.Encounters.Count;
            AssertMatches(scenario, mine, start);
        }

        Assert.Equal(index, h.Records.Count);
    }

    [Fact]
    public void Tcp_gap_marks_the_encounter_as_incomplete()
    {
        var scenario = ScenarioLibrary.BossKill();
        var stream = new StreamGenerator(new StreamGeneratorOptions { Seed = 4 }).Generate(scenario, Start);
        var h = new Harness();
        h.Input.OnDiscontinuity(DiscontinuityReason.NewConnection);
        var gapAt = Start.AddSeconds(40);
        bool gapSent = false;
        foreach (var c in stream.Chunks)
        {
            if (!gapSent && c.TimeUtc >= gapAt)
            {
                gapSent = true;
                h.Input.OnDiscontinuity(DiscontinuityReason.TcpGap);
            }

            h.Input.OnData(c.TimeUtc, c.Data);
            h.Engine.Tick(c.TimeUtc);
        }

        h.Drain(stream.Chunks[^1].TimeUtc);
        var record = Assert.Single(h.Records);
        Assert.True(record.CaptureGaps);
        Assert.Equal(EncounterOutcome.Kill, record.Outcome);
    }
}
