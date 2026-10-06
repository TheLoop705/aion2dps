using Aion2Dps.App.Integration;
using Aion2Dps.Contracts;
using Aion2Dps.Simulator;

namespace Aion2Dps.App.Tests;

/// <summary>ServiceFactory with the real modules: simulator and replay modes end to end (no windows, no live capture).</summary>
public sealed class IntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aion2dps-app-int-" + Guid.NewGuid().ToString("N"));

    public IntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private static void WaitUntil(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until) Thread.Sleep(20);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public void Simulator_mode_runs_the_real_pipeline_into_its_own_history()
    {
        var sim = new SimulationOptions { Speed = 0, Loop = false, GapSeconds = 15, Scenarios = [ScenarioLibrary.BossKill()] };
        using var services = ServiceFactory.CreateSimulated(_dir, sim);
        Assert.False(services.IsDemo);
        Assert.Equal("Simulator", services.ModeLabel);
        Assert.NotNull(services.Diagnostics);
        services.Engine.EncounterCompleted += services.Store.Save;
        services.Capture.Start(services.PipelineInput);
        WaitUntil(() => services.Capture.Status.State == CaptureState.Stopped);

        var fights = services.Store.Query(new FightQuery());
        var fight = Assert.Single(fights);
        Assert.Equal(EncounterOutcome.Kill, fight.Outcome);
        Assert.Equal(ScenarioLibrary.BossKill().Truth.Final!.TotalDamage, fight.TotalDamage);
        Assert.Equal(0, services.Diagnostics!.DecodeErrors);
        Assert.Equal("Kaelwyn", services.Engine.LocalPlayer?.Name);
        Assert.True(File.Exists(Path.Combine(_dir, ServiceFactory.DemoHistoryDatabase)));
        Assert.False(File.Exists(Path.Combine(_dir, ServiceFactory.HistoryDatabase)));
    }

    [Fact]
    public void Replay_mode_uses_the_capture_clock_and_finishes_the_last_encounter()
    {
        var scenario = ScenarioLibrary.BossWipeThenKill();
        string pcap = Path.Combine(_dir, "wipe.pcap");
        var start = new DateTime(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc);
        PcapExporter.WriteFile(pcap, scenario, start);

        using var services = ServiceFactory.CreateReal(new LaunchOptions { Mode = LaunchMode.Replay, ReplayPath = pcap, ReplaySpeed = 0 }, _dir);
        var completed = new List<EncounterRecord>();
        services.Engine.EncounterCompleted += r => { lock (completed) completed.Add(r); };
        var replay = Assert.IsType<ReplayCaptureService>(services.Capture);
        services.Capture.Start(services.PipelineInput);
        WaitUntil(() => replay.Finished);
        Assert.Equal(CaptureState.Stopped, services.Capture.Status.State);

        // The clock is capture time (September), not the wall clock.
        var now = services.Now();
        Assert.InRange(now, start, start.AddMinutes(10));
        services.Engine.Tick(now.AddSeconds(60));
        lock (completed)
        {
            var errors = TruthComparer.CompareAll(scenario, completed, start);
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }

        Assert.StartsWith("Replay", services.ModeLabel);
    }

    [Fact]
    public void Replay_of_a_missing_file_reports_an_error_state()
    {
        using var services = ServiceFactory.CreateReplay(Path.Combine(_dir, "nope.pcapng"), 1, _dir);
        services.Capture.Start(services.PipelineInput);
        Assert.Equal(CaptureState.Error, services.Capture.Status.State);
        Assert.Contains("not found", services.Capture.Status.Message);
    }

    [Fact]
    public void Live_mode_wires_npcap_capture_without_starting_it()
    {
        using var services = ServiceFactory.CreateLive(_dir);
        Assert.Equal("Live", services.ModeLabel);
        Assert.IsType<Aion2Dps.Capture.NpcapCaptureService>(services.Capture);
        Assert.IsType<Aion2Dps.GameData.GameDataStore>(services.GameData);
        Assert.IsType<Aion2Dps.Storage.SqliteFightStore>(services.Store);
        Assert.Null(services.Clock);
    }

    [Fact]
    public void Language_changes_reach_the_real_game_data()
    {
        using var services = ServiceFactory.CreateLive(_dir);
        int raised = 0;
        services.GameData.LanguageChanged += () => raised++;
        string en = services.GameData.GetNpcName(2300171);
        services.GameData.Language = GameLanguage.Korean;
        Assert.Equal(1, raised);
        Assert.NotEqual(en, services.GameData.GetNpcName(2300171));
    }
}
