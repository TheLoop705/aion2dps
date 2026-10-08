using Aion2Dps.App.Demo;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Settings;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

public class FakeEngineTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Waits_then_fights_with_draining_boss_hp()
    {
        var e = new FakeCombatEngine(new FakeGameData(), originUtc: T0);
        Assert.Equal(MeterState.WaitingForCombat, e.GetSnapshot(T0.AddSeconds(1)).State);
        var a = e.GetSnapshot(T0.AddSeconds(20));
        var b = e.GetSnapshot(T0.AddSeconds(60));
        Assert.Equal(MeterState.InCombat, b.State);
        Assert.Equal(5, b.Rows.Count);
        Assert.True(b.Target!.HpFraction < a.Target!.HpFraction);
        Assert.True(b.TotalDamage > a.TotalDamage);
        Assert.Contains(b.Rows, r => r.IsLocal);
        Assert.Equal(1, b.Rows[0].Rank);
        Assert.True(b.Rows.Zip(b.Rows.Skip(1)).All(p => p.First.Damage >= p.Second.Damage));
        Assert.Equal(b.MaxDps, b.Rows.Max(r => r.Dps), 3);
        Assert.True(b.Targets.Count >= 2); // adds spawned
    }

    [Fact]
    public void Is_deterministic()
    {
        var x = new FakeCombatEngine(new FakeGameData(), originUtc: T0).GetSnapshot(T0.AddSeconds(50));
        var y = new FakeCombatEngine(new FakeGameData(), originUtc: T0).GetSnapshot(T0.AddSeconds(50));
        Assert.Equal(x.TotalDamage, y.TotalDamage);
        Assert.Equal(x.Target!.Hp, y.Target!.Hp);
    }

    [Fact]
    public void Completes_a_kill_then_a_wipe_with_full_records()
    {
        var e = new FakeCombatEngine(new FakeGameData(), originUtc: T0);
        var done = new List<EncounterRecord>();
        e.EncounterCompleted += done.Add;
        for (int s = 0; s < 600 && done.Count < 2; s++) e.Tick(T0.AddSeconds(s));
        Assert.Equal(2, done.Count);
        var kill = done[0];
        Assert.Equal(EncounterOutcome.Kill, kill.Outcome);
        Assert.Equal(0, kill.BossHpEnd);
        Assert.Equal(5, kill.Combatants.Count);
        Assert.All(kill.Combatants, c => Assert.NotEmpty(c.Skills));
        Assert.NotEmpty(kill.Hits);
        Assert.True(kill.HpCheck!.Ratio > 0.99 && kill.HpCheck.Ratio < 1.01, $"ratio {kill.HpCheck.Ratio}");
        Assert.InRange(kill.Combatants.Sum(c => c.Damage) - kill.TotalDamage, -5L, 5L);
        Assert.InRange(kill.Combatants.Sum(c => c.Contribution), 0.97, 1.02);
        var wipe = done[1];
        Assert.Equal(EncounterOutcome.Wipe, wipe.Outcome);
        Assert.True(wipe.BossHpEnd > 0);
        Assert.Contains(wipe.Combatants, c => c.Deaths > 0);
    }

    [Fact]
    public void Ended_state_shows_the_outcome()
    {
        var s = PreviewData.EndedKill();
        Assert.Equal(MeterState.Ended, s.State);
        Assert.Equal(EncounterOutcome.Kill, s.Outcome);
    }

    [Fact]
    public void Pvp_mode_produces_opponents_and_kills()
    {
        var s = PreviewData.Pvp(64);
        Assert.Equal(MeterMode.Pvp, s.Mode);
        Assert.True(s.PvpRows.Count >= 4);
        Assert.Equal(1, s.PvpKills);
        Assert.Contains(s.PvpRows, r => r.Killed && r.HpFraction == 0);
    }

    [Fact]
    public void Training_runs_and_reports_a_training_record()
    {
        var e = new FakeCombatEngine(new FakeGameData(), originUtc: T0);
        var done = new List<EncounterRecord>();
        e.EncounterCompleted += done.Add;
        e.GetSnapshot(T0.AddSeconds(2));
        e.StartTraining(TimeSpan.FromSeconds(30));
        var mid = e.GetSnapshot(T0.AddSeconds(17));
        Assert.Equal(EncounterKind.Training, mid.EncounterKind);
        Assert.Single(mid.Rows);
        Assert.True(mid.Target!.IsDummy);
        e.Tick(T0.AddSeconds(40));
        Assert.Contains(done, r => r.Kind == EncounterKind.Training);
    }

    [Fact]
    public void Reset_clears_display_and_cycle_target_wraps()
    {
        var e = new FakeCombatEngine(new FakeGameData(), originUtc: T0);
        var s = e.GetSnapshot(T0.AddSeconds(40));
        uint first = s.Target!.EntityId;
        e.CycleTarget(+1);
        Assert.NotEqual(first, e.GetSnapshot(T0.AddSeconds(40)).Target!.EntityId);
        e.CycleTarget(-1);
        Assert.Equal(first, e.GetSnapshot(T0.AddSeconds(40)).Target!.EntityId);
        e.Reset();
        Assert.Equal(MeterState.WaitingForCombat, e.GetSnapshot(T0.AddSeconds(41)).State);
    }

    [Fact]
    public void Demo_services_are_complete()
    {
        using var s = ServiceFactory.CreateDemo(originUtc: T0);
        Assert.True(s.IsDemo);
        Assert.NotNull(s.Diagnostics);
        Assert.NotEmpty(s.Capture.GetAdapters());
        Assert.NotEmpty(s.Store.Query(new FightQuery()));
        Assert.NotEmpty(s.Store.GetCharacters());
    }

    [Fact]
    public void Fake_game_data_never_throws_on_unknown_ids()
    {
        var gd = new FakeGameData();
        Assert.Equal("Skill 99", gd.GetSkillName(99));
        Assert.Equal("NPC 1", gd.GetNpcName(1));
        Assert.Null(gd.GetMapName(1));
        Assert.Equal("Flame Lance", gd.GetSkillName(FakeGameData.SkillId(CharacterClass.Sorcerer, 0)));
        int raised = 0;
        gd.LanguageChanged += () => raised++;
        gd.Language = GameLanguage.Korean;
        Assert.Equal(1, raised);
    }
}

public class StoreAndPersonalBestTests
{
    [Fact]
    public void Store_queries_trends_and_personal_best()
    {
        var gd = new FakeGameData();
        var store = new InMemoryFightStore();
        store.SeedDemoHistory(gd, new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(17, store.Count);
        Assert.Single(store.GetCharacters(), "Velkaris");
        var kills = store.Query(new FightQuery { KillsOnly = true });
        Assert.All(kills, k => Assert.Equal(EncounterOutcome.Kill, k.Outcome));
        Assert.True(kills.Zip(kills.Skip(1)).All(p => p.First.StartUtc >= p.Second.StartUtc));
        var summaries = store.GetBossSummaries("Velkaris");
        Assert.Equal(4, summaries.Count);
        var pb = store.GetPersonalBest(FakeGameData.BossNpc, "Velkaris");
        Assert.NotNull(pb);
        Assert.Equal(store.GetBossTrend(FakeGameData.BossNpc, "Velkaris").Where(t => t.Outcome == EncounterOutcome.Kill).Max(t => t.Dps), pb!.Dps);
        Assert.True(store.Delete(pb.FightId));
        Assert.Null(store.Load(pb.FightId));
    }

    private static EncounterRecord Kill(double dps, DateTime start)
    {
        var gd = new FakeGameData();
        var f = DemoFight.CreateBoss(1, start, DemoCombatant.Party.Select(p => p with { BaseDps = p.BaseDps * dps }).ToList(), 60, null);
        return f.BuildRecord(gd, f.Duration);
    }

    [Fact]
    public void Personal_best_toasts_first_kill_and_improvements_only()
    {
        var gd = new FakeGameData();
        var store = new InMemoryFightStore();
        var first = Kill(1.0, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var t1 = PersonalBest.Check(store, first, gd);
        Assert.Equal("First kill recorded", t1!.Value.Title);
        store.Save(first);

        var worse = Kill(0.8, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Null(PersonalBest.Check(store, worse, gd));
        store.Save(worse);

        var better = Kill(1.3, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        var t3 = PersonalBest.Check(store, better, gd);
        Assert.Equal("New personal best!", t3!.Value.Title);
        Assert.Contains("Warden of the Ashen Spire", t3.Value.Message);

        better.Outcome = EncounterOutcome.Wipe;
        Assert.Null(PersonalBest.Check(store, better, gd));
    }
}

internal static class CombatEngineIds
{
    public const uint Others = Aion2Dps.Combat.CombatEngine.OthersEntityId;
}

public class OverlayLogicTests
{
    private static MeterSnapshot Snap() => new()
    {
        State = MeterState.InCombat,
        Elapsed = TimeSpan.FromSeconds(100),
        Rows = Enumerable.Range(1, 12).Select(i => new PlayerRow
        {
            EntityId = (uint)i, Name = "P" + i, Damage = 1_000_000 - i * 50_000, Dps = 10_000 - i * 500 + (i == 3 ? 5_000 : 0),
            DamageShare = 0.08, Contribution = 0.05, DamageTaken = i * 1000, Healing = i == 5 ? 9_000 : 0, IsLocal = i == 12,
        }).Append(new PlayerRow { EntityId = 99, Name = "Enemy", Kind = CombatantKind.EnemyPlayer, Damage = 5_000_000, Dps = 99_999 }).ToList(),
    };

    [Fact]
    public void Dps_view_sorts_by_dps_and_bars_are_relative_to_top()
    {
        var rows = OverlayView.ComputeRows(Snap(), new OverlayViewOptions { View = MeterView.Dps, MaxRows = 24 });
        Assert.Equal(12, rows.Count); // enemy players never appear in party rows
        Assert.Equal(3u, rows[0].Row.EntityId);
        Assert.Equal(1.0, rows[0].Bar, 6);
        Assert.True(rows.Zip(rows.Skip(1)).All(p => p.First.Primary >= p.Second.Primary));
        Assert.Equal(Enumerable.Range(1, 12), rows.Select(r => r.Rank));
    }

    [Fact]
    public void Total_view_sorts_by_damage_and_share_mode_uses_damage_share()
    {
        var rows = OverlayView.ComputeRows(Snap(), new OverlayViewOptions { View = MeterView.Total, BarMode = BarMode.ShareOfParty, MaxRows = 24 });
        Assert.Equal(1u, rows[0].Row.EntityId);
        Assert.All(rows, r => Assert.Equal(0.08, r.Bar, 6));
    }

    [Fact]
    public void Partial_view_ranks_you_and_party_first_others_last_and_shows_boss_hp_share()
    {
        var snap = Snap() with
        {
            PartialView = true,
            PartialViewText = "Partial view: only your/party damage is visible",
            Rows = new[]
            {
                new PlayerRow { EntityId = 1, Name = "Stranger", Damage = 900_000, Dps = 9_000, DamageShare = 0.6, Contribution = 0.02, Hits = 40 },
                new PlayerRow { EntityId = CombatEngineIds.Others, Name = "Others (39, visible DoT/heal only)", Damage = 500_000, Dps = 5_000, DamageShare = 0.3, Contribution = 0.011, AggregateCount = 39 },
                new PlayerRow { EntityId = 2, Name = "Mate", Damage = 10_000, Dps = 100, DamageShare = 0.01, Contribution = 0.0002, IsPartyMember = true, Hits = 0 },
                new PlayerRow { EntityId = 3, Name = "You", Damage = 300_000, Dps = 3_000, DamageShare = 0.2, Contribution = 0.042, IsLocal = true, Hits = 1692 },
            },
        };
        var rows = OverlayView.ComputeRows(snap, new OverlayViewOptions { View = MeterView.Total, BarMode = BarMode.ShareOfParty, MaxRows = 24 });
        Assert.Equal(new uint[] { 3, 2, 1, CombatEngineIds.Others }, rows.Select(r => r.Row.EntityId));
        Assert.Equal(0.042, rows[0].Pct!.Value, 6); // share of the boss's max HP, never the 20 % of visible damage
        Assert.Equal(300_000.0 / 900_000, rows[0].Bar, 6); // share-of-party bars fall back to relative bars
    }

    [Fact]
    public void Own_row_never_disappears_when_rows_are_limited()
    {
        var rows = OverlayView.ComputeRows(Snap(), new OverlayViewOptions { View = MeterView.Total, MaxRows = 5 });
        Assert.Equal(5, rows.Count);
        Assert.True(rows[^1].Row.IsLocal);
    }

    [Fact]
    public void Taken_and_heal_views_filter_and_rank()
    {
        var taken = OverlayView.ComputeRows(Snap(), new OverlayViewOptions { View = MeterView.Taken, MaxRows = 24 });
        Assert.Equal(12u, taken[0].Row.EntityId);
        Assert.Equal(12_000 / 100.0, taken[0].Secondary, 6);
        var heal = OverlayView.ComputeRows(Snap(), new OverlayViewOptions { View = MeterView.Heal, MaxRows = 24 });
        Assert.Single(heal);
        Assert.Equal(1.0, heal[0].Pct);
    }

    [Fact]
    public void Pvp_sort_by_threat_puts_killed_last()
    {
        var rows = new[]
        {
            new PvpRow { EntityId = 1, DamageTaken = 900, DamageDealt = 10, Killed = true },
            new PvpRow { EntityId = 2, DamageTaken = 500, DamageDealt = 50 },
            new PvpRow { EntityId = 3, DamageTaken = 700, DamageDealt = 5 },
        };
        Assert.Equal(new uint[] { 3, 2, 1 }, OverlayView.SortPvp(rows, PvpSort.Threat).Select(r => r.EntityId));
        Assert.Equal(new uint[] { 2, 1, 3 }, OverlayView.SortPvp(rows, PvpSort.Damage).Select(r => r.EntityId));
    }

    [Theory]
    [InlineData(CaptureState.NpcapMissing, MeterState.InCombat, OverlayPhase.NpcapMissing)]
    [InlineData(CaptureState.WaitingForGame, MeterState.Idle, OverlayPhase.WaitingForGame)]
    [InlineData(CaptureState.Detecting, MeterState.Idle, OverlayPhase.Detecting)]
    [InlineData(CaptureState.Capturing, MeterState.Idle, OverlayPhase.WaitingForData)]
    [InlineData(CaptureState.Capturing, MeterState.WaitingForCombat, OverlayPhase.WaitingForCombat)]
    [InlineData(CaptureState.Replaying, MeterState.InCombat, OverlayPhase.Live)]
    [InlineData(CaptureState.Capturing, MeterState.Ended, OverlayPhase.Ended)]
    [InlineData(CaptureState.Stopped, MeterState.Idle, OverlayPhase.CaptureStopped)]
    [InlineData(CaptureState.Stopped, MeterState.WaitingForCombat, OverlayPhase.CaptureStopped)]
    [InlineData(CaptureState.Stopped, MeterState.Ended, OverlayPhase.Ended)]
    public void Phase_logic(CaptureState capture, MeterState meter, OverlayPhase expected) =>
        Assert.Equal(expected, OverlayPhaseLogic.Compute(new CaptureStatus { State = capture }, new MeterSnapshot { State = meter }));

    [Fact]
    public void Launch_options_parse()
    {
        Assert.Equal(LaunchMode.Live, LaunchOptions.Parse([]).Mode);
        Assert.Equal(LaunchMode.Demo, LaunchOptions.Parse(["--demo"]).Mode);
        Assert.Equal(LaunchMode.Simulator, LaunchOptions.Parse(["--sim"]).Mode);
        var r = LaunchOptions.Parse(["--replay", "C:\\x.pcapng", "--speed", "0"]);
        Assert.Equal(LaunchMode.Replay, r.Mode);
        Assert.Equal("C:\\x.pcapng", r.ReplayPath);
        Assert.Equal(0, r.ReplaySpeed);
        Assert.Equal(LaunchMode.Live, LaunchOptions.Parse(["--replay"]).Mode);
        Assert.Equal("out", LaunchOptions.Parse(["--render-screens", "out"]).RenderScreensDir);
    }
}
