using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator;

/// <summary>One TCP payload of the server→client stream, as a capture would deliver it.</summary>
/// <param name="TimeUtc">Capture timestamp of the segment.</param>
/// <param name="Data">Payload bytes (at most <see cref="StreamGeneratorOptions.MaxSegmentSize"/>).</param>
public sealed record StreamChunk(DateTime TimeUtc, byte[] Data);

/// <summary>One frame of the generated stream in wire order, with the event it encodes.</summary>
/// <param name="TimeUtc">Timestamp of the chunk(s) carrying it (= the event's scripted time).</param>
/// <param name="Opcode">Opcode in wire order.</param>
/// <param name="Payload">Opcode + body.</param>
/// <param name="BundleDepth">0 = top level, 1 = inside an LZ4 bundle, 2 = nested bundle.</param>
/// <param name="Event">The event as a decoder should report it (Time and BundleDepth set), or null for frames without a typed event.</param>
/// <param name="Source">The scripted event it came from (null for generator-added heartbeats and pings).</param>
public sealed record GeneratedFrame(DateTime TimeUtc, ushort Opcode, byte[] Payload, int BundleDepth, GameEvent? Event, ScriptedEvent? Source);

/// <summary>Knobs of <see cref="StreamGenerator"/>.</summary>
public sealed record StreamGeneratorOptions
{
    public int Seed { get; init; } = 1;
    /// <summary>Target share of combat frames (04 38, 05 38, 00 8D, 2A 38, 0E 92, 02 38) sent inside LZ4 bundles.</summary>
    public double BundleFraction { get; init; } = 0.4;
    /// <summary>Chance per top-level unit of 1..3 <c>00</c> padding bytes in front of it.</summary>
    public double PaddingChance { get; init; } = 0.03;
    /// <summary>Chance per bundle that its inner stream also carries padding bytes.</summary>
    public double InnerPaddingChance { get; init; } = 0.1;
    /// <summary>Chance per bundle to wrap part of it in a second, nested bundle (spec §5 allows depth ≤ 4). Off by default.</summary>
    public double NestedBundleChance { get; init; }
    /// <summary>Chance per bundle to use a literal-only LZ4 block.</summary>
    public double LiteralOnlyBundleChance { get; init; } = 0.1;
    /// <summary>Heartbeats per second (<c>00 36</c>, the real client sees ~19/s).</summary>
    public double HeartbeatHz { get; init; } = 19;
    /// <summary>Seconds between <c>03 36</c> ping echoes (0 = none).</summary>
    public double PingIntervalSeconds { get; init; } = 5;
    /// <summary>Largest TCP payload.</summary>
    public int MaxSegmentSize { get; init; } = 1460;
    /// <summary>Chance per flush to be cut into several segments at random points.</summary>
    public double SplitChance { get; init; } = 0.6;
    /// <summary>Chance per multi-byte length varint to force a segment boundary inside it.</summary>
    public double VarintSplitChance { get; init; } = 0.5;
    /// <summary>Chance per bundle to force a segment boundary inside it.</summary>
    public double BundleSplitChance { get; init; } = 0.3;
}

/// <summary>The output of <see cref="StreamGenerator.Generate"/>.</summary>
public sealed class GeneratedStream
{
    public required Scenario Scenario { get; init; }
    public required DateTime StartUtc { get; init; }
    public required IReadOnlyList<StreamChunk> Chunks { get; init; }
    /// <summary>Every frame in wire order (bundle contents expanded in place, the bundle itself not listed).</summary>
    public required IReadOnlyList<GeneratedFrame> Frames { get; init; }
    /// <summary>The whole stream (all chunks concatenated).</summary>
    public required byte[] Bytes { get; init; }

    public int BundleCount { get; init; }
    public int CombatFrames { get; init; }
    public int BundledCombatFrames { get; init; }
    public int PaddingBytes { get; init; }
    public int SplitsInsideVarint { get; init; }
    public int SplitsInsideBundle { get; init; }
    public double BundledCombatShare => CombatFrames == 0 ? 0 : (double)BundledCombatFrames / CombatFrames;

    /// <summary>Events a decoder should emit, in order (heartbeats and pings included).</summary>
    public IEnumerable<GameEvent> ExpectedEvents => Frames.Where(f => f.Event is not null).Select(f => f.Event!);

    /// <summary>Delivers every chunk synchronously to <paramref name="sink"/> (for tests and offline runs).</summary>
    /// <param name="clock">Optional callback with each chunk's timestamp (e.g. to drive ICombatEngine.Tick).</param>
    public void FeedTo(IStreamSink sink, bool announceNewConnection = true, Action<DateTime>? clock = null)
    {
        if (announceNewConnection) sink.OnDiscontinuity(DiscontinuityReason.NewConnection);
        foreach (var chunk in Chunks)
        {
            sink.OnData(chunk.TimeUtc, chunk.Data);
            clock?.Invoke(chunk.TimeUtc);
        }
    }
}

/// <summary>
/// Turns a scenario into timestamped TCP payloads: every scripted event becomes a frame, heartbeats (~19/s) and ping
/// echoes are added, about 40 % of combat frames travel inside LZ4 bundles, padding appears occasionally, and the bytes
/// of each server tick are cut at random points (including inside length varints and inside bundles).
/// Chunk timestamps equal the scripted event times, so decoded event times match the ground truth exactly.
/// Deterministic for a given seed.
/// </summary>
public sealed class StreamGenerator
{
    private readonly StreamGeneratorOptions _o;

    public StreamGenerator(StreamGeneratorOptions? options = null) => _o = options ?? new StreamGeneratorOptions();

    public StreamGeneratorOptions Options => _o;

    private static bool IsCombat(ushort opcode) => opcode is Opcodes.Damage or Opcodes.DotTick or Opcodes.EntityStats
        or Opcodes.BuffApplied or Opcodes.BuffRemoved or Opcodes.Cast;

    private sealed record Item(ushort Opcode, byte[] Frame, byte[] Payload, GameEvent? Event, ScriptedEvent? Source);

    /// <summary>Heartbeat-only stream of <paramref name="duration"/> (idle time between scenarios).</summary>
    public IReadOnlyList<StreamChunk> GenerateIdle(DateTime startUtc, TimeSpan duration)
    {
        var chunks = new List<StreamChunk>();
        double step = 1.0 / Math.Max(0.1, _o.HeartbeatHz);
        for (double t = 0; t < duration.TotalSeconds; t += step)
        {
            var time = startUtc.AddTicks((long)(t * TimeSpan.TicksPerSecond));
            var hb = new HeartbeatEvent { ServerUnixMs = (ulong)new DateTimeOffset(time).ToUnixTimeMilliseconds() };
            chunks.Add(new StreamChunk(time, PacketEncoders.EncodeFrame(hb)));
        }

        return chunks;
    }

    public GeneratedStream Generate(Scenario scenario, DateTime startUtc)
    {
        startUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        var rng = new Random(_o.Seed);

        // 1. Timeline: scripted events + heartbeats + pings, grouped by identical timestamps (one flush per server tick).
        var timeline = new List<(TimeSpan At, int Order, GameEvent Event, ScriptedEvent? Source)>();
        int order = 0;
        foreach (var se in scenario.Events) timeline.Add((se.Offset, order++, se.Event, se));
        double hbStep = 1.0 / Math.Max(0.1, _o.HeartbeatHz);
        for (double t = 0.012; t < scenario.Duration.TotalSeconds; t += hbStep * (0.9 + rng.NextDouble() * 0.2))
        {
            var at = TimeSpan.FromMilliseconds(Math.Round(t * 1000));
            var time = startUtc + at;
            timeline.Add((at, order++, new HeartbeatEvent { ServerUnixMs = (ulong)new DateTimeOffset(time).ToUnixTimeMilliseconds() }, null));
        }

        if (_o.PingIntervalSeconds > 0)
        {
            long qpcBase = 16_777_216_000 + 9_000_000 + rng.Next(1_000_000);
            for (double t = 1.37; t < scenario.Duration.TotalSeconds; t += _o.PingIntervalSeconds)
            {
                var at = TimeSpan.FromMilliseconds(Math.Round(t * 1000));
                timeline.Add((at, order++, new PingEvent { ClientSentMs = qpcBase + (long)(t * 1000) - 45 }, null));
            }
        }

        timeline.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Order.CompareTo(b.Order));

        var chunks = new List<StreamChunk>();
        var frames = new List<GeneratedFrame>();
        var all = new WireWriter(64 * 1024);
        int bundles = 0, combat = 0, bundledCombat = 0, padding = 0, varintSplits = 0, bundleSplits = 0;

        int i = 0;
        while (i < timeline.Count)
        {
            var at = timeline[i].At;
            var time = startUtc + at;
            var items = new List<Item>();
            for (; i < timeline.Count && timeline[i].At == at; i++)
            {
                var (_, _, e, src) = timeline[i];
                ushort opcode = PacketEncoders.OpcodeOf(e);
                byte[] payload = PacketEncoders.EncodePayload(e);
                items.Add(new Item(opcode, Framing.Frame(payload), payload, e, src));
            }

            // 2. Build the flush: some runs of combat frames go into bundles, steering the share toward the target.
            var flush = new WireWriter(512);
            var varintCuts = new List<int>();      // candidate cut positions inside length varints
            var bundleRanges = new List<(int Start, int End)>();
            int k = 0;
            while (k < items.Count)
            {
                if (rng.NextDouble() < _o.PaddingChance)
                {
                    int n = rng.Next(1, 4);
                    flush.Zeros(n);
                    padding += n;
                }

                var item = items[k];
                bool startBundle = false;
                if (IsCombat(item.Opcode))
                {
                    double share = combat == 0 ? 0 : (double)bundledCombat / combat;
                    double p = share < _o.BundleFraction ? Math.Min(0.95, _o.BundleFraction * 1.6) : _o.BundleFraction * 0.4;
                    startBundle = rng.NextDouble() < p;
                }

                if (!startBundle)
                {
                    if (IsCombat(item.Opcode)) combat++;
                    if (item.Frame[0] >= 0x80) varintCuts.Add(flush.Length + 1);
                    flush.Bytes(item.Frame);
                    frames.Add(new GeneratedFrame(time, item.Opcode, item.Payload, 0, Stamp(item.Event, time, 0), item.Source));
                    k++;
                    continue;
                }

                // A run of consecutive combat frames (1..10) becomes one bundle.
                int runEnd = k;
                int maxRun = rng.Next(1, 11);
                while (runEnd < items.Count && runEnd - k < maxRun && IsCombat(items[runEnd].Opcode)) runEnd++;
                var run = items.GetRange(k, runEnd - k);
                combat += run.Count;
                bundledCombat += run.Count;
                byte[] bundle = BuildBundle(run, time, rng, frames, depth: 1, ref padding);
                bundles++;
                int bundleStart = flush.Length;
                if (bundle[0] >= 0x80) varintCuts.Add(bundleStart + 1);
                flush.Bytes(bundle);
                bundleRanges.Add((bundleStart, flush.Length));
                k = runEnd;
            }

            // 3. Cut the flush into segments.
            byte[] data = flush.ToArray();
            var cuts = new SortedSet<int>();
            foreach (int c in varintCuts)
                if (rng.NextDouble() < _o.VarintSplitChance) cuts.Add(c);
            foreach (var (s, e) in bundleRanges)
                if (e - s > 8 && rng.NextDouble() < _o.BundleSplitChance) cuts.Add(rng.Next(s + 3, e - 1));
            if (data.Length > 1 && rng.NextDouble() < _o.SplitChance)
            {
                int extra = rng.Next(1, 4);
                for (int c = 0; c < extra; c++) cuts.Add(rng.Next(1, data.Length));
            }

            for (int pos = _o.MaxSegmentSize; pos < data.Length; pos += _o.MaxSegmentSize) cuts.Add(pos);
            cuts.RemoveWhere(c => c <= 0 || c >= data.Length);

            // Enforce the MSS after random cuts too.
            var sortedCuts = new List<int>();
            int last = 0;
            foreach (int c in cuts.Append(data.Length))
            {
                while (c - last > _o.MaxSegmentSize) { last += _o.MaxSegmentSize; sortedCuts.Add(last); }
                if (c < data.Length) sortedCuts.Add(c);
                last = c;
            }

            int from = 0;
            foreach (int c in sortedCuts.Append(data.Length))
            {
                if (c <= from) continue;
                if (varintCuts.Contains(c) && c != data.Length) varintSplits++;
                if (c != data.Length && bundleRanges.Any(r => c > r.Start && c < r.End)) bundleSplits++;
                chunks.Add(new StreamChunk(time, data[from..c]));
                from = c;
            }

            all.Bytes(data);
        }

        return new GeneratedStream
        {
            Scenario = scenario,
            StartUtc = startUtc,
            Chunks = chunks,
            Frames = frames,
            Bytes = all.ToArray(),
            BundleCount = bundles,
            CombatFrames = combat,
            BundledCombatFrames = bundledCombat,
            PaddingBytes = padding,
            SplitsInsideVarint = varintSplits,
            SplitsInsideBundle = bundleSplits,
        };
    }

    private byte[] BuildBundle(List<Item> run, DateTime time, Random rng, List<GeneratedFrame> frames, int depth, ref int padding)
    {
        var inner = new WireWriter(256);
        bool pad = rng.NextDouble() < _o.InnerPaddingChance;
        int nestAt = depth < 4 && run.Count >= 2 && rng.NextDouble() < _o.NestedBundleChance ? rng.Next(1, run.Count) : -1;
        for (int j = 0; j < run.Count; j++)
        {
            if (j == nestAt)
            {
                var rest = run.GetRange(j, run.Count - j);
                inner.Bytes(BuildBundle(rest, time, rng, frames, depth + 1, ref padding));
                break;
            }

            if (pad && rng.NextDouble() < 0.3)
            {
                inner.U8(0);
                padding++;
            }

            var item = run[j];
            inner.Bytes(item.Frame);
            frames.Add(new GeneratedFrame(time, item.Opcode, item.Payload, depth, Stamp(item.Event, time, depth), item.Source));
        }

        return BundleBuilder.BuildFrame(inner.WrittenSpan, literalOnly: rng.NextDouble() < _o.LiteralOnlyBundleChance);
    }

    private static GameEvent? Stamp(GameEvent? e, DateTime time, int depth) => e is null ? null : e with { Time = time, BundleDepth = depth };
}
