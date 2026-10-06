using Aion2Dps.App.Demo;
using Aion2Dps.Capture;
using Aion2Dps.Combat;
using Aion2Dps.GameData;
using Aion2Dps.Protocol;
using Aion2Dps.Simulator;
using Aion2Dps.Storage;

namespace Aion2Dps.App.Integration;

/// <summary>
/// Everything the UI needs, built by <see cref="ServiceFactory"/>. The App only talks to these Contracts interfaces.
/// </summary>
/// <param name="GameData">Localized lookups.</param>
/// <param name="Engine">Combat engine (also the end of the protocol pipeline).</param>
/// <param name="Capture">Capture service; the App calls <c>Capture.Start(PipelineInput)</c> at start-up.</param>
/// <param name="PipelineInput">Head of the protocol pipeline (FrameDecoder → PacketDecoder → Engine).</param>
/// <param name="Diagnostics">Protocol counters/census for the dashboard (optional).</param>
/// <param name="Store">Fight history.</param>
public sealed record AppServices(
    IGameData GameData,
    ICombatEngine Engine,
    ICaptureService Capture,
    IStreamSink PipelineInput,
    IProtocolDiagnostics? Diagnostics,
    IFightStore Store) : IDisposable
{
    /// <summary>Clock passed to GetSnapshot/Tick. Null = DateTime.UtcNow (live). Replays set the replay clock here.</summary>
    public Func<DateTime>? Clock { get; init; }

    /// <summary>Short label shown in the dashboard/footer (e.g. "Live", "Demo", "Replay: file.pcapng").</summary>
    public string ModeLabel { get; init; } = "Live";

    /// <summary>True for the self-contained demo services.</summary>
    public bool IsDemo { get; init; }

    /// <summary>Optional extra start-up step run after <c>Capture.Start</c> (e.g. kick off a replay task).</summary>
    public Action? AfterStart { get; init; }

    /// <summary>Extra objects disposed with the services (replay sources, simulator, …).</summary>
    public IReadOnlyList<IDisposable> Extra { get; init; } = [];

    public DateTime Now() => Clock?.Invoke() ?? DateTime.UtcNow;

    public void Dispose()
    {
        try { Capture.Stop(); } catch (Exception ex) { AppLog.Warn("App", $"Capture stop failed: {ex.Message}"); }
        foreach (var d in new IDisposable[] { Capture, Store }.Concat(Extra))
        {
            try { d.Dispose(); }
            catch (Exception ex) { AppLog.Warn("App", $"Dispose failed: {ex.Message}"); }
        }
    }
}

/// <summary>
/// Composition root for the services behind the UI. <see cref="CreateDemo"/> is the self-contained fake set (<c>--demo</c>);
/// <see cref="CreateReal"/> wires the real modules (live capture, simulator or replay).
/// </summary>
public static class ServiceFactory
{
    /// <summary>Fully working demo: animated 5-player boss fights, PvP data, seeded history. No capture, no network.</summary>
    public static AppServices CreateDemo(bool raid = false, DateTime? originUtc = null)
    {
        var gameData = new FakeGameData();
        var engine = new FakeCombatEngine(gameData, seed: 7, originUtc: originUtc, raid: raid);
        var capture = new FakeCaptureService();
        var store = new InMemoryFightStore();
        store.SeedDemoHistory(gameData, originUtc ?? DateTime.UtcNow);
        return new AppServices(gameData, engine, capture, new NullStreamSink(), new FakeDiagnostics(), store)
        {
            ModeLabel = "Demo",
            IsDemo = true,
        };
    }

    /// <summary>File name of the real fight history inside the data directory.</summary>
    public const string HistoryDatabase = "history.db";
    /// <summary>Separate history for the simulator (--sim) so demo fights never mix with real ones.</summary>
    public const string DemoHistoryDatabase = "demo-history.db";
    /// <summary>Separate history for replayed files (--replay) so re-running a file does not duplicate real fights.</summary>
    public const string ReplayHistoryDatabase = "replay-history.db";

    /// <summary>
    /// The real pipeline: GameDataStore → ProtocolPipeline (FrameDecoder → PacketDecoder) → CombatEngine, SqliteFightStore,
    /// and by <see cref="LaunchOptions.Mode"/>: live Npcap capture (default; reports NpcapMissing in the UI when Npcap
    /// is not installed), the wire simulator (<c>--sim</c>) or a pcap/pcapng replay (<c>--replay</c>).
    /// </summary>
    /// <param name="dataDirectory">%APPDATA%/Aion2Dps (fight databases live here).</param>
    public static AppServices CreateReal(LaunchOptions options, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Mode switch
        {
            LaunchMode.Simulator => CreateSimulated(dataDirectory),
            LaunchMode.Replay when !string.IsNullOrWhiteSpace(options.ReplayPath) => CreateReplay(options.ReplayPath!, options.ReplaySpeed, dataDirectory),
            _ => CreateLive(dataDirectory),
        };
    }

    /// <summary>Live capture of the game connection through Npcap.</summary>
    public static AppServices CreateLive(string dataDirectory, string? gameDataDirectory = null)
    {
        var core = Core(gameDataDirectory);
        var store = OpenStore(Path.Combine(dataDirectory, HistoryDatabase));
        var capture = new NpcapCaptureService();
        return new AppServices(core.GameData, core.Engine, capture, core.Input, core.Pipeline.Diagnostics, store)
        {
            ModeLabel = "Live",
        };
    }

    /// <summary>
    /// The real protocol pipeline + combat engine + game data fed by the wire simulator (encoded scenarios, LZ4 bundles,
    /// random segmentation) in real time, with its own demo history database.
    /// </summary>
    public static AppServices CreateSimulated(string dataDirectory, SimulationOptions? simulation = null, string? gameDataDirectory = null)
    {
        var core = Core(gameDataDirectory, new EngineOptions());
        var store = OpenStore(Path.Combine(dataDirectory, DemoHistoryDatabase));
        var capture = new SimulatedCaptureService(simulation ?? new SimulationOptions { Speed = 1, Loop = true });
        return new AppServices(core.GameData, core.Engine, capture, core.Input, core.Pipeline.Diagnostics, store)
        {
            ModeLabel = "Simulator",
        };
    }

    /// <summary>Replays a .pcap/.pcapng/hex-log file through the real pipeline; the UI clock is the capture clock.</summary>
    public static AppServices CreateReplay(string path, double speed, string dataDirectory, string? gameDataDirectory = null)
    {
        var core = Core(gameDataDirectory);
        var store = OpenStore(Path.Combine(dataDirectory, ReplayHistoryDatabase));
        var capture = new ReplayCaptureService(path, speed);
        return new AppServices(core.GameData, core.Engine, capture, core.Input, core.Pipeline.Diagnostics, store)
        {
            ModeLabel = "Replay: " + Path.GetFileName(path),
            Clock = capture.Now,
        };
    }

    /// <summary>The shared decoding chain. Capture gaps mark the active encounter; a replay restart clears the session.</summary>
    private static (GameDataStore GameData, CombatEngine Engine, ProtocolPipeline Pipeline, IStreamSink Input) Core(string? gameDataDirectory, EngineOptions? options = null)
    {
        var gameData = gameDataDirectory is null ? GameDataStore.LoadDefault() : new GameDataStore(gameDataDirectory);
        var engine = new CombatEngine(gameData, options ?? new EngineOptions());
        var pipeline = new ProtocolPipeline(engine, OpcodeTable.LoadOrDefault());
        var input = new DiscontinuityTap(pipeline.Input, reason =>
        {
            switch (reason)
            {
                case DiscontinuityReason.TcpGap:
                    engine.NotifyCaptureGap();
                    break;
                case DiscontinuityReason.ReplayStarted:
                    engine.ClearSession();
                    break;
            }
        });
        return (gameData, engine, pipeline, input);
    }

    private static IFightStore OpenStore(string path)
    {
        try
        {
            return new SqliteFightStore(path);
        }
        catch (Exception ex)
        {
            // History must never keep the meter from running: fall back to an empty session-only store.
            AppLog.Error("App", $"Could not open the fight history at {path}; fights of this session are kept in memory only", ex);
            return new InMemoryFightStore();
        }
    }
}
