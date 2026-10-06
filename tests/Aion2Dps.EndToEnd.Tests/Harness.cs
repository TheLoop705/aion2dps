using Aion2Dps.Combat;
using Aion2Dps.Contracts;
using Aion2Dps.GameData;
using Aion2Dps.Protocol;
using Aion2Dps.Simulator;

namespace Aion2Dps.EndToEnd.Tests;

/// <summary>Real pipeline: ProtocolPipeline → CombatEngine (real GameDataStore), collecting completed encounters.</summary>
internal sealed class Harness
{
    private static readonly Lazy<GameDataStore> SharedData = new(() => GameDataStore.LoadDefault());

    public Harness(EngineOptions? options = null)
    {
        Engine = new CombatEngine(SharedData.Value, options ?? new EngineOptions { SaveTrashFights = true });
        Engine.EncounterCompleted += r => { lock (Records) Records.Add(r); };
        Pipeline = new ProtocolPipeline(Engine, OpcodeTable.LoadOrDefault());
        Input = new DiscontinuityTap(Pipeline.Input, reason =>
        {
            if (reason == DiscontinuityReason.TcpGap) Engine.NotifyCaptureGap();
        });
    }

    public static GameDataStore GameData => SharedData.Value;

    public CombatEngine Engine { get; }
    public ProtocolPipeline Pipeline { get; }
    public IStreamSink Input { get; }
    public List<EncounterRecord> Records { get; } = new();

    /// <summary>Lets idle timeouts and kill graces expire after the stream ended.</summary>
    public void Drain(DateTime lastUtc)
    {
        for (int s = 1; s <= 60; s++) Engine.Tick(lastUtc.AddSeconds(s));
    }

    /// <summary>Feeds the generated chunks, additionally re-split at random points (same timestamps).</summary>
    public void FeedRechunked(GeneratedStream stream, int seed)
    {
        var rng = new Random(seed);
        Input.OnDiscontinuity(DiscontinuityReason.NewConnection);
        DateTime last = stream.StartUtc;
        foreach (var chunk in stream.Chunks)
        {
            var data = chunk.Data.AsSpan();
            while (data.Length > 0)
            {
                int n = rng.NextDouble() < 0.3 ? data.Length : Math.Min(data.Length, 1 + rng.Next(Math.Max(1, data.Length)));
                Input.OnData(chunk.TimeUtc, data[..n]);
                data = data[n..];
            }

            Engine.Tick(chunk.TimeUtc);
            last = chunk.TimeUtc;
        }

        Drain(last);
    }
}
