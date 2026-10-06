using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public class EndedDisplayTests
{
    private static Script KilledBoss(EngineOptions? options = null)
    {
        var s = Script.Standard(options);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(9, Script.Me, Script.Boss, 100);
        s.Hp(10, Script.Boss, 0);
        return s;
    }

    [Fact]
    public void Default_keeps_the_finished_encounter_display()
    {
        var s = KilledBoss();
        Assert.Equal(0, s.Options.EndedDisplaySeconds);
        s.Tick(13);

        var snapshot = s.Snap(100);
        Assert.Equal(MeterState.Ended, snapshot.State);
        Assert.Equal(200, snapshot.TotalDamage);
        Assert.Equal(25, snapshot.PartyDps);
        Assert.NotEmpty(snapshot.Rows);
        Assert.NotNull(snapshot.Target);
        Assert.Single(s.Completed);
    }

    [Fact]
    public void Finished_display_clears_at_configured_boundary_without_changing_history_or_context()
    {
        var s = KilledBoss(new EngineOptions { EndedDisplaySeconds = 15 });
        s.Tick(13);
        var saved = Assert.Single(s.Completed);
        var before = s.Snap(24.999);
        Assert.Equal(MeterState.Ended, before.State);
        Assert.Equal(200, before.TotalDamage);

        var cleared = s.Snap(25);
        Assert.Equal(MeterState.WaitingForCombat, cleared.State);
        Assert.Equal("Waiting for combat", cleared.StatusText);
        Assert.Equal(Script.At(25), cleared.TimeUtc);
        Assert.Equal(before.Mode, cleared.Mode);
        Assert.Equal(before.MapId, cleared.MapId);
        Assert.Equal(before.MapName, cleared.MapName);
        Assert.Equal(before.LocalPlayer, cleared.LocalPlayer);
        Assert.Equal(before.PingMs, cleared.PingMs);
        Assert.Empty(cleared.Rows);
        Assert.Empty(cleared.PvpRows);
        Assert.Empty(cleared.Targets);
        Assert.Empty(cleared.Bosses);
        Assert.Null(cleared.Target);
        Assert.Null(cleared.EncounterId);
        Assert.Null(cleared.EncounterKind);
        Assert.Null(cleared.Outcome);
        Assert.Null(cleared.HpCheckRatio);
        Assert.Null(cleared.OverallHpCheckRatio);
        Assert.Equal(TimeSpan.Zero, cleared.Elapsed);
        Assert.Equal(0, cleared.TotalDamage);
        Assert.Equal(0, cleared.PartyDps);
        Assert.Equal(0, cleared.MinDps);
        Assert.Equal(0, cleared.AvgDps);
        Assert.Equal(0, cleared.MaxDps);
        Assert.Equal(0, cleared.PvpKills);

        var retained = s.Record();
        Assert.Equal(saved.Id, retained.Id);
        Assert.Equal(saved.TotalDamage, retained.TotalDamage);
        Assert.Equal(saved.Hits.Count, retained.Hits.Count);
        Assert.Equal(EncounterOutcome.Kill, retained.Outcome);
        s.Snap(30);
        s.Tick(40);
        Assert.Same(saved, Assert.Single(s.Completed));
    }

    [Fact]
    public void Idle_timeout_delay_starts_at_encounter_end_instead_of_last_damage()
    {
        var s = Script.Standard(new EngineOptions { EndedDisplaySeconds = 15 });
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(10, Script.Me, Script.Boss, 100);
        s.Tick(41);

        Assert.Equal(EncounterOutcome.Timeout, s.Snap(41).Outcome);
        Assert.Equal(MeterState.Ended, s.Snap(55.999).State);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(56).State);
        Assert.Equal(200, s.Record().TotalDamage);
        Assert.Single(s.Completed);
    }

    [Fact]
    public void Active_encounter_is_not_hidden_after_the_display_delay()
    {
        var s = Script.Standard(new EngineOptions { EndedDisplaySeconds = 15, BossIdleTimeoutSeconds = 1000 });
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(20, Script.Me, Script.Boss, 100);
        s.Tick(200);

        var snapshot = s.Snap(200);
        Assert.Equal(MeterState.InCombat, snapshot.State);
        Assert.Equal(200, snapshot.TotalDamage);
        Assert.NotEmpty(snapshot.Rows);
        Assert.Empty(s.Completed);
    }

    [Fact]
    public void New_encounter_after_display_deadline_is_shown_with_preserved_party_and_entity_context()
    {
        var s = KilledBoss(new EngineOptions { EndedDisplaySeconds = 15 });
        s.Roster(10.5, ("Me", 6), ("Ally", 26));
        s.Tick(13);
        var completed = Assert.Single(s.Completed);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(25).State);

        s.Hit(26, Script.Me, Script.Trash, 300);
        s.Hit(26, Script.Ally, Script.Trash, 100, Script.SorSkill);
        var snapshot = s.Snap(27);
        Assert.Equal(MeterState.InCombat, snapshot.State);
        Assert.NotEqual(completed.Id, snapshot.EncounterId);
        Assert.Equal(400, snapshot.TotalDamage);
        Assert.Equal(Script.Trash, snapshot.Target!.EntityId);
        Assert.Equal(FakeGameData.TrashCode, snapshot.Target.NpcCode);
        var ally = Script.Row(snapshot, Script.Ally);
        Assert.Equal("Ally", ally.Name);
        Assert.True(ally.IsPartyMember);
        Assert.Equal(1001u, ally.GearScore);
        Assert.Same(completed, Assert.Single(s.Completed));
    }

    [Fact]
    public void Kill_grace_keeps_display_visible_and_retains_late_killing_blow_before_clearing()
    {
        var s = KilledBoss(new EngineOptions { EndedDisplaySeconds = 0.25 });
        Assert.Equal(MeterState.Ended, s.Snap(11).State);
        Assert.Empty(s.Completed);

        s.Hit(11, Script.Me, Script.Boss, 50);
        var late = s.Snap(11.5);
        Assert.Equal(MeterState.Ended, late.State);
        Assert.Equal(250, late.TotalDamage);
        Assert.Empty(s.Completed);

        s.Tick(12.01);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(12.01).State);
        var saved = Assert.Single(s.Completed);
        Assert.Equal(250, saved.TotalDamage);
        Assert.Contains(saved.Hits, hit => hit.Flags.HasFlag(HitFlags.KillingBlow));
        Assert.Equal(saved.TotalDamage, s.Record().TotalDamage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    public void Disabled_invalid_or_extremely_long_delays_keep_finished_display(double delay)
    {
        var s = KilledBoss(new EngineOptions { EndedDisplaySeconds = delay });
        s.Tick(13);

        var snapshot = s.Snap(100);
        Assert.Equal(MeterState.Ended, snapshot.State);
        Assert.Equal(200, snapshot.TotalDamage);
        Assert.Equal(0, s.Engine.EventErrors);
    }
}
