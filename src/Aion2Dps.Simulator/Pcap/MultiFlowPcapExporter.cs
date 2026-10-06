using System.Net;
using System.Text;
using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator;

/// <summary>Shape of a two-connection capture made by <see cref="MultiFlowPcapExporter"/>.</summary>
public sealed record MultiFlowOptions
{
    public int Seed { get; init; } = 1;
    /// <summary>Stream settings of the instance (combat) flow; null = defaults. Its Seed is replaced by <see cref="Seed"/>.</summary>
    public StreamGeneratorOptions? Stream { get; init; }
    /// <summary>The world connection exists this long before the scenario starts (the instance connection opens later,
    /// when the dungeon is entered).</summary>
    public double WorldLeadSeconds { get; init; } = 3;
    /// <summary>The world connection continues this long after the scenario ends.</summary>
    public double WorldTailSeconds { get; init; } = 2;
    /// <summary>When set, the world connection's data ends this many seconds after the scenario start (e.g. it closes
    /// mid-fight); otherwise it ends <see cref="WorldTailSeconds"/> after the scenario.</summary>
    public double? WorldEndOffsetSeconds { get; init; }
    /// <summary>Seconds between unknown-opcode "noise" frames (movement/chat-like) on the world flow (0 = none).</summary>
    public double NoiseIntervalSeconds { get; init; } = 0.35;
    /// <summary>Send the own-character record (33 36) and the party roster (02 97) on the world flow instead of the
    /// instance flow, so the meter needs BOTH flows to produce correct encounters.</summary>
    public bool IdentityOnWorld { get; init; } = true;
    /// <summary>Delays the world flow's identity records by this many seconds (e.g. the world server re-sends them after
    /// the instance connection opened). Keep it below the scenario's first hit.</summary>
    public double WorldIdentityDelaySeconds { get; init; }
    /// <summary>Close the instance connection with FIN after its last data (leaving the dungeon).</summary>
    public bool CloseInstanceAtEnd { get; init; } = true;
    /// <summary>Close the world connection with FIN after its last data.</summary>
    public bool CloseWorldAtEnd { get; init; }
    public bool IncludeTlsDecoy { get; init; } = true;
    public IPAddress ClientIp { get; init; } = IPAddress.Parse("192.168.178.81");
    public IPAddress WorldServerIp { get; init; } = IPAddress.Parse("87.232.75.150");
    public IPAddress InstanceServerIp { get; init; } = IPAddress.Parse("193.202.112.155");
    public ushort WorldClientPort { get; init; } = 53334;
    public ushort InstanceClientPort { get; init; } = 64331;
}

/// <summary>One server→client chunk of a multi-flow stream, tagged with its flow.</summary>
/// <param name="Flow">0 = world, 1 = instance.</param>
public sealed record FlowChunk(int Flow, StreamChunk Chunk);

/// <summary>The two generated server→client streams of a multi-flow capture.</summary>
public sealed class MultiFlowStream
{
    public const int WorldFlow = 0;
    public const int InstanceFlow = 1;

    public required Scenario Scenario { get; init; }
    public required DateTime StartUtc { get; init; }
    /// <summary>The instance (dungeon) connection: combat, spawns, map load.</summary>
    public required GeneratedStream Instance { get; init; }
    /// <summary>The world connection: heartbeats, identity records, unknown-opcode noise.</summary>
    public required IReadOnlyList<StreamChunk> WorldChunks { get; init; }
    public required byte[] WorldBytes { get; init; }
    /// <summary>Scripted events that travel on the world flow (identity records).</summary>
    public required IReadOnlyList<ScriptedEvent> WorldEvents { get; init; }
    public int NoiseFrames { get; init; }

    /// <summary>Server→client bytes of both flows.</summary>
    public long TotalBytes => Instance.Bytes.LongLength + WorldBytes.LongLength;

    /// <summary>Both flows merged by timestamp (world first on equal timestamps), as a capture would interleave them.</summary>
    public IReadOnlyList<FlowChunk> Interleaved()
    {
        var list = new List<FlowChunk>(WorldChunks.Count + Instance.Chunks.Count);
        int w = 0, i = 0;
        while (w < WorldChunks.Count || i < Instance.Chunks.Count)
        {
            bool takeWorld = i >= Instance.Chunks.Count ||
                             (w < WorldChunks.Count && WorldChunks[w].TimeUtc <= Instance.Chunks[i].TimeUtc);
            list.Add(takeWorld ? new FlowChunk(WorldFlow, WorldChunks[w++]) : new FlowChunk(InstanceFlow, Instance.Chunks[i++]));
        }

        return list;
    }
}

/// <summary>
/// Simulates the real client's connection layout (LIVE-FINDINGS NEW 1): a world connection that is open before and after
/// the fight (heartbeats, the own-character record and party roster, movement/chat-like frames with unknown opcodes) and
/// a concurrent dungeon-instance connection opened later that carries the combat. Writes both as two interleaved TCP
/// flows into one pcap so capture, multi-flow reassembly and decoding can be tested against the scenario ground truth.
/// </summary>
public static class MultiFlowPcapExporter
{
    /// <summary>Opcodes seen in real traffic without a decoder (LIVE-FINDINGS, "frequently seen but undocumented").</summary>
    private static readonly ushort[] NoiseOpcodes = [0x1D37, 0x1C37, 0x1B37, 0x2037, 0x8456];

    private static readonly string[] ChatLines =
    [
        "LFG Fire Temple, need healer", "wts enchant stones", "anyone for the field boss?", "gg", "selling crafting mats",
    ];

    private static bool TravelsOnWorld(GameEvent e) => e is SelfInfoEvent or PartyRosterEvent;

    /// <summary>Generates both streams of <paramref name="scenario"/> starting at <paramref name="startUtc"/>.</summary>
    public static MultiFlowStream Generate(Scenario scenario, DateTime startUtc, MultiFlowOptions? options = null)
    {
        var o = options ?? new MultiFlowOptions();
        startUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        var worldEvents = o.IdentityOnWorld ? scenario.Events.Where(e => TravelsOnWorld(e.Event)).ToList() : new List<ScriptedEvent>();
        var instanceEvents = o.IdentityOnWorld ? scenario.Events.Where(e => !TravelsOnWorld(e.Event)).ToList() : scenario.Events.ToList();

        var instanceScenario = Clone(scenario, scenario.Name + " (instance flow)", instanceEvents, scenario.Duration);
        var streamOptions = (o.Stream ?? new StreamGeneratorOptions()) with { Seed = o.Seed };
        var instance = new StreamGenerator(streamOptions).Generate(instanceScenario, startUtc);

        // World flow: its own timeline, starting WorldLeadSeconds earlier, no ping echoes, no bundles needed.
        var lead = TimeSpan.FromSeconds(Math.Max(0, o.WorldLeadSeconds));
        var worldStart = startUtc - lead;
        var delay = TimeSpan.FromSeconds(Math.Max(0, o.WorldIdentityDelaySeconds));
        var shifted = worldEvents.Select(e => e with { Offset = e.Offset + lead + delay }).ToList();
        var worldDuration = o.WorldEndOffsetSeconds is double endOffset
            ? lead + TimeSpan.FromSeconds(Math.Max(0.5, endOffset))
            : scenario.Duration + lead + TimeSpan.FromSeconds(Math.Max(0, o.WorldTailSeconds));
        if (shifted.Count > 0 && shifted[^1].Offset >= worldDuration)
            throw new ArgumentException("The world flow ends before its last identity record.", nameof(options));
        var worldScenario = Clone(scenario, scenario.Name + " (world flow)", shifted, worldDuration);
        var worldGen = new StreamGenerator(new StreamGeneratorOptions { Seed = o.Seed * 7919 + 13, PingIntervalSeconds = 0, BundleFraction = 0 });
        var worldStream = worldGen.Generate(worldScenario, worldStart);

        var rng = new Random(o.Seed * 31 + 7);
        var noise = new List<StreamChunk>();
        if (o.NoiseIntervalSeconds > 0)
        {
            var end = worldStart + worldScenario.Duration;
            for (var t = worldStart.AddSeconds(0.5 + rng.NextDouble() * 0.1); t < end; t = t.AddSeconds(o.NoiseIntervalSeconds * (0.6 + rng.NextDouble() * 0.8)))
            {
                // Off the generator's millisecond grid; on a tie the merge keeps the whole generator flush first anyway,
                // so a noise frame never lands inside a flush that was split into several segments.
                var at = new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond + 3_000 + rng.Next(1, 1_000), DateTimeKind.Utc);
                noise.Add(new StreamChunk(at, NoiseFrame(rng)));
            }
        }

        var worldChunks = Merge(worldStream.Chunks, noise);
        var bytes = new MemoryStream();
        foreach (var c in worldChunks) bytes.Write(c.Data);
        return new MultiFlowStream
        {
            Scenario = scenario,
            StartUtc = startUtc,
            Instance = instance,
            WorldChunks = worldChunks,
            WorldBytes = bytes.ToArray(),
            WorldEvents = worldEvents,
            NoiseFrames = noise.Count,
        };
    }

    /// <summary>Generates the streams and writes them as one pcap with two concurrent game flows (plus a TLS decoy).</summary>
    public static MultiFlowStream WriteFile(string path, Scenario scenario, DateTime startUtc, MultiFlowOptions? options = null)
    {
        var o = options ?? new MultiFlowOptions();
        var stream = Generate(scenario, startUtc, o);
        using var file = File.Create(path);
        Write(file, stream, o);
        return stream;
    }

    /// <summary>Writes an already generated multi-flow stream as pcap.</summary>
    public static void Write(Stream output, MultiFlowStream stream, MultiFlowOptions? options = null)
    {
        var o = options ?? new MultiFlowOptions();
        var world = new PcapFlowSpec
        {
            Chunks = stream.WorldChunks,
            ServerIp = o.WorldServerIp,
            ClientIp = o.ClientIp,
            ClientPort = o.WorldClientPort,
            ServerInitialSeq = 0x1111_0000u + (uint)o.Seed,
            ClientInitialSeq = 0x2222_0000u,
            ClientPacketIntervalSeconds = 2.0,
            CloseAtUtc = o.CloseWorldAtEnd ? stream.WorldChunks[^1].TimeUtc.AddMilliseconds(5) : null,
        };
        var instance = new PcapFlowSpec
        {
            Chunks = stream.Instance.Chunks,
            ServerIp = o.InstanceServerIp,
            ClientIp = o.ClientIp,
            ClientPort = o.InstanceClientPort,
            ServerInitialSeq = 0xFFFF_8000u - (uint)o.Seed, // wraps around during the fight
            ClientInitialSeq = 0x3333_0000u,
            ClientPacketIntervalSeconds = 1.5,
            CloseAtUtc = o.CloseInstanceAtEnd ? stream.Instance.Chunks[^1].TimeUtc.AddMilliseconds(5) : null,
        };
        PcapExporter.WriteFlows(output, [world, instance], new PcapExportOptions { Seed = o.Seed, IncludeTlsDecoy = o.IncludeTlsDecoy, ClientIp = o.ClientIp });
    }

    private static byte[] NoiseFrame(Random rng)
    {
        ushort op = NoiseOpcodes[rng.Next(NoiseOpcodes.Length)];
        byte[] body;
        if (op == 0x1C37)
        {
            // Chat-like: a short header and UTF-8 text.
            var text = Encoding.UTF8.GetBytes(ChatLines[rng.Next(ChatLines.Length)]);
            body = new byte[6 + text.Length];
            rng.NextBytes(body.AsSpan(0, 6));
            text.CopyTo(body, 6);
        }
        else
        {
            body = new byte[op == 0x8456 ? rng.Next(150, 400) : rng.Next(12, 48)];
            rng.NextBytes(body);
        }

        return Framing.Frame(op, body);
    }

    /// <summary>Stable merge by timestamp: for equal timestamps the generator's chunks come first.</summary>
    private static List<StreamChunk> Merge(IReadOnlyList<StreamChunk> a, IReadOnlyList<StreamChunk> b)
    {
        var list = new List<StreamChunk>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
        {
            if (j >= b.Count || (i < a.Count && a[i].TimeUtc <= b[j].TimeUtc)) list.Add(a[i++]);
            else list.Add(b[j++]);
        }

        return list;
    }

    private static Scenario Clone(Scenario s, string name, IReadOnlyList<ScriptedEvent> events, TimeSpan duration) => new()
    {
        Name = name,
        Description = s.Description,
        Seed = s.Seed,
        Duration = duration,
        Events = events,
        Players = s.Players,
        Npcs = s.Npcs,
        Truth = s.Truth,
    };
}
