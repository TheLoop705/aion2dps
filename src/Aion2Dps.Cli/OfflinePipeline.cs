using Aion2Dps.Combat;
using Aion2Dps.Contracts;
using Aion2Dps.GameData;
using Aion2Dps.Protocol;

namespace Aion2Dps.Cli;

/// <summary>The real decoding chain used by every CLI command: capture/replay bytes → ProtocolPipeline → CombatEngine.</summary>
internal sealed class OfflinePipeline
{
    private static GameDataStore? _sharedData;

    public OfflinePipeline(EngineOptions? options = null, Action<EncounterRecord>? onCompleted = null)
    {
        GameData = _sharedData ??= GameDataStore.LoadDefault();
        Engine = new CombatEngine(GameData, options ?? new EngineOptions { SaveTrashFights = true });
        Engine.EncounterCompleted += r =>
        {
            lock (Records) Records.Add(r);
            onCompleted?.Invoke(r);
        };
        Protocol = new ProtocolPipeline(Engine, OpcodeTable.LoadOrDefault());
        Input = new DiscontinuityTap(Protocol.Input, reason =>
        {
            if (reason == DiscontinuityReason.TcpGap) Engine.NotifyCaptureGap();
        });
    }

    public GameDataStore GameData { get; }
    public CombatEngine Engine { get; }
    public ProtocolPipeline Protocol { get; }
    public IStreamSink Input { get; }
    public List<EncounterRecord> Records { get; } = new();
    public DateTime LastClockUtc { get; private set; }

    /// <summary>Clock callback for replays: drives the engine's idle timeouts with capture time.</summary>
    public void Clock(DateTime utc)
    {
        if (utc <= LastClockUtc) return;
        bool tick = (utc - LastClockUtc).TotalMilliseconds >= 250;
        LastClockUtc = utc;
        if (tick) Engine.Tick(utc);
    }

    /// <summary>Lets idle timeouts and kill graces expire after the data ended.</summary>
    public void Drain()
    {
        var last = LastClockUtc == default ? DateTime.UtcNow : LastClockUtc;
        for (int s = 1; s <= 60; s++) Engine.Tick(last.AddSeconds(s));
    }
}
