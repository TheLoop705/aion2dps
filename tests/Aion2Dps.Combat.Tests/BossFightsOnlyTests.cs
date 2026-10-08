using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

/// <summary><see cref="EngineOptions.BossFightsOnly"/>: trash never starts or replaces a fight on the meter.</summary>
public class BossFightsOnlyTests
{
    private static EngineOptions BossOnly(double endedDisplaySeconds = 60, bool countAdds = false) => new()
    {
        BossFightsOnly = true,
        SaveTrashFights = true,
        EndedDisplaySeconds = endedDisplaySeconds,
        CountAddsInBossFight = countAdds,
    };

    /// <summary>Boss hit at 1 s and 9 s, dead at 10 s (ended 10 s, finalized after the 2 s kill grace).</summary>
    private static Script KilledBoss(EngineOptions options)
    {
        var s = Script.Standard(options);
        s.SpawnNpc(0, Script.Boss2, FakeGameData.BossCode2, 2_000_000, 2_000_000);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(9, Script.Me, Script.Boss, 100);
        s.Hp(10, Script.Boss, 0);
        return s;
    }

    [Fact]
    public void Engine_default_is_off()
    {
        Assert.False(new EngineOptions().BossFightsOnly);
    }

    [Fact]
    public void Trash_never_starts_or_shows_an_encounter()
    {
        var s = Script.Standard(BossOnly());
        s.Hit(1, Script.Me, Script.Trash, 1000);
        s.Hit(2, Script.Ally, Script.Trash, 1000, Script.SorSkill);
        s.Hit(20, Script.Me, Script.Trash2, 1000);

        var snap = s.Snap(20);
        Assert.Equal(MeterState.WaitingForCombat, snap.State);
        Assert.Null(snap.EncounterId);
        Assert.Empty(snap.Rows);
        Assert.Null(s.Engine.GetCurrentEncounter());
        s.Tick(100);
        Assert.Empty(s.Completed); // even with "save trash fights" on
    }

    [Fact]
    public void Boss_starts_the_encounter_and_adds_stay_out_of_scope_by_default()
    {
        var s = Script.Standard(BossOnly());
        s.Hit(1, Script.Me, Script.Trash, 1000);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(1).State);

        s.Hit(2, Script.Me, Script.Boss, 7000);
        s.Hit(3, Script.Me, Script.Trash, 1000);
        s.Hit(4, Script.Me, Script.Boss, 3000);
        var snap = s.Snap(4);
        Assert.Equal(MeterState.InCombat, snap.State);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        Assert.Equal(10_000, Script.Row(snap, Script.Me).Damage);
        Assert.Equal(Script.Boss, snap.Target!.EntityId);
        var me = Script.Combatant(s.Record(), Script.Me);
        Assert.Equal(10_000, me.Damage);
    }

    [Fact]
    public void Adds_during_a_boss_fight_follow_count_adds_option()
    {
        var s = Script.Standard(BossOnly(countAdds: true));
        s.Hit(1, Script.Me, Script.Boss, 7000);
        s.Hit(2, Script.Me, Script.Trash, 1000);
        s.Hit(3, Script.Me, Script.Boss, 3000);
        Assert.Equal(11_000, Script.Row(s.Snap(3), Script.Me).Damage);
        Assert.Equal(10_000, Script.Combatant(s.Record(), Script.Me).BossDamage);
    }

    [Fact]
    public void Unknown_npc_with_a_boss_sized_hp_pool_counts_as_boss()
    {
        var s = Script.Standard(BossOnly());
        s.SpawnNpc(0, 7777, 2999999, 6_800_000, 6_800_000);
        s.Hit(1, Script.Me, 7777, 100);
        Assert.Equal(EncounterKind.Boss, s.Snap(1).EncounterKind);
    }

    [Fact]
    public void Training_dummy_and_training_runs_still_track()
    {
        var s = Script.Standard(BossOnly());
        s.SpawnNpc(0, Script.Dummy, FakeGameData.DummyCode, 1_000_000, 1_000_000);
        s.Hit(1, Script.Me, Script.Dummy, 500);
        Assert.Equal(EncounterKind.Dummy, s.Snap(1).EncounterKind);

        s.Engine.StartTraining(TimeSpan.FromSeconds(10));
        s.Hit(5, Script.Me, Script.Trash, 700);
        var live = s.Snap(6);
        Assert.Equal(EncounterKind.Training, live.EncounterKind);
        Assert.Equal(700, live.TotalDamage);
        s.Tick(16);
        Assert.Contains(s.Completed, r => r.Kind == EncounterKind.Training);
    }

    [Fact]
    public void Pvp_still_starts_an_encounter_and_ignores_trash()
    {
        var s = Script.Standard(BossOnly());
        s.Player(0, Script.Enemy, "Rival", 14);
        s.Hit(1, Script.Me, Script.Enemy, 1000);
        s.Hit(2, Script.Me, Script.Trash, 5000);
        s.Hit(3, Script.Enemy, Script.Me, 500, Script.RanSkill);
        s.Engine.Mode = MeterMode.Pvp;
        var snap = s.Snap(3);
        Assert.Equal(EncounterKind.Pvp, snap.EncounterKind);
        Assert.Equal(1000, Assert.Single(snap.PvpRows).DamageDealt);
        Assert.DoesNotContain(s.Record().Hits, h => h.Target == Script.Trash);
    }

    [Fact]
    public void Finished_boss_fight_lingers_through_trash_for_the_display_time()
    {
        var s = KilledBoss(BossOnly(endedDisplaySeconds: 60));
        var ended = s.Snap(10);
        Assert.Equal(MeterState.Ended, ended.State);

        // The party moves on and pulls trash: the result stays up.
        s.Hit(15, Script.Me, Script.Trash, 5000);
        s.Hit(20, Script.Ally, Script.Trash, 5000, Script.SorSkill);
        s.Hit(40, Script.Me, Script.Trash2, 5000);
        s.Tick(41);
        foreach (double t in new[] { 15.0, 30, 50, 69.999 })
        {
            var snap = s.Snap(t);
            Assert.Equal(MeterState.Ended, snap.State);
            Assert.Equal(ended.EncounterId, snap.EncounterId);
            Assert.Equal(EncounterOutcome.Kill, snap.Outcome);
            Assert.Equal(200, snap.TotalDamage);
            Assert.Equal(Script.Boss, snap.Target!.EntityId);
        }

        Assert.Equal(MeterState.WaitingForCombat, s.Snap(70).State);
        var saved = Assert.Single(s.Completed);
        Assert.Equal(EncounterKind.Boss, saved.Kind);
        Assert.Equal(200, saved.TotalDamage);
    }

    [Fact]
    public void Late_killing_blow_still_counts_when_trash_is_hit_in_the_kill_grace()
    {
        var s = KilledBoss(BossOnly());
        s.Hit(10.5, Script.Me, Script.Trash, 5000);
        s.Hit(11, Script.Me, Script.Boss, 50);
        s.Tick(13);
        var saved = Assert.Single(s.Completed);
        Assert.Equal(250, saved.TotalDamage);
        Assert.Equal(250, s.Snap(13).TotalDamage);
        Assert.Equal(MeterState.Ended, s.Snap(13).State);
    }

    [Fact]
    public void A_new_boss_replaces_the_lingering_result_at_once()
    {
        var s = KilledBoss(BossOnly(endedDisplaySeconds: 60));
        var ended = s.Snap(10);
        s.Hit(20, Script.Me, Script.Trash, 5000);
        s.Hit(25, Script.Me, Script.Boss2, 400);

        var snap = s.Snap(25);
        Assert.Equal(MeterState.InCombat, snap.State);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        Assert.NotEqual(ended.EncounterId, snap.EncounterId);
        Assert.Equal(400, snap.TotalDamage);
        Assert.Equal(Script.Boss2, snap.Target!.EntityId);
    }

    [Fact]
    public void Setting_off_keeps_todays_behaviour_trash_replaces_the_finished_fight()
    {
        var s = KilledBoss(new EngineOptions { EndedDisplaySeconds = 60, SaveTrashFights = true });
        var ended = s.Snap(10);
        s.Hit(15, Script.Me, Script.Trash, 5000);

        var snap = s.Snap(15);
        Assert.Equal(MeterState.InCombat, snap.State);
        Assert.Equal(EncounterKind.Trash, snap.EncounterKind);
        Assert.NotEqual(ended.EncounterId, snap.EncounterId);
        Assert.Equal(5000, snap.TotalDamage);
    }

    [Fact]
    public void Setting_off_trash_alone_starts_an_encounter()
    {
        var s = Script.Standard(new EngineOptions());
        s.Hit(1, Script.Me, Script.Trash, 1000);
        var snap = s.Snap(1);
        Assert.Equal(MeterState.InCombat, snap.State);
        Assert.Equal(EncounterKind.Trash, snap.EncounterKind);
        Assert.Equal(1000, Script.Row(snap, Script.Me).Damage);
    }

    [Fact]
    public void Turning_the_setting_on_hides_a_running_trash_encounter()
    {
        var options = new EngineOptions();
        var s = Script.Standard(options);
        s.Hit(1, Script.Me, Script.Trash, 1000);
        Assert.Equal(MeterState.InCombat, s.Snap(1).State);

        options.BossFightsOnly = true;
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(2).State);
        s.Hit(3, Script.Me, Script.Trash, 1000);
        Assert.Equal(MeterState.WaitingForCombat, s.Snap(3).State);
        s.Hit(4, Script.Me, Script.Boss, 1000);
        var snap = s.Snap(4);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        Assert.Equal(1000, snap.TotalDamage);
    }
}
