using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public class EncounterLifecycleTests
{
    private static void BossFight(Script s, double from, double to, long perHit = 10_000)
    {
        for (double t = from; t <= to + 1e-9; t += 1) s.Hit(t, Script.Me, Script.Boss, perHit);
    }

    [Fact]
    public void Status_progression_idle_waiting_combat()
    {
        var s = new Script();
        Assert.Equal(MeterState.Idle, s.Snap(0).State);
        s.Map(0, Script.Map1);
        var w = s.Snap(0);
        Assert.Equal(MeterState.WaitingForCombat, w.State);
        Assert.Contains("Waiting for combat", w.StatusText);
        Assert.Equal("Fire Temple", w.MapName);
        Assert.Null(w.LocalPlayer);
        s.Self(0);
        Assert.Equal("Waiting for combat", s.Snap(0).StatusText);
        Assert.Equal(new LocalPlayerInfo(Script.Me, "Me", 1304, CharacterClass.Gladiator, 30), s.Engine.LocalPlayer);
    }

    [Fact]
    public void Boss_kill_by_hp_zero_with_late_killing_blow()
    {
        var s = Script.Standard();
        BossFight(s, 1, 9);
        s.Hp(9.1, Script.Boss, 0);
        var snap = s.Snap(9.5);
        Assert.Equal(MeterState.Ended, snap.State);
        Assert.Equal(EncounterOutcome.Kill, snap.Outcome);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        Assert.True(snap.Target!.IsDead);
        Assert.Empty(s.Completed); // kill grace still open

        s.Hit(10, Script.Me, Script.Boss, 5000); // within 2 s of death: still counts
        Assert.Equal(95_000, Script.Row(s.Snap(10), Script.Me).Damage);

        s.Tick(11.5);
        var rec = Assert.Single(s.Completed);
        Assert.Equal(EncounterOutcome.Kill, rec.Outcome);
        Assert.Equal(95_000, Script.Combatant(rec, Script.Me).Damage);
        Assert.Equal(9, rec.DurationSeconds, 6);
        Assert.True(rec.Targets.Single(t => t.EntityId == Script.Boss).Killed);
        Assert.Contains(rec.Hits, h => h.Flags.HasFlag(HitFlags.KillingBlow));

        s.Hit(15, Script.Me, Script.Boss, 5000); // corpse: does not reopen
        Assert.Equal(MeterState.Ended, s.Snap(15).State);
        Assert.Single(s.Completed);
    }

    [Fact]
    public void Boss_kill_by_death_flag_3()
    {
        var s = Script.Standard();
        BossFight(s, 1, 7);
        s.Death(7.5, Script.Boss, flag: 1); // "loaded dead" form on a live boss: not a combat death
        Assert.Equal(MeterState.InCombat, s.Snap(7.5).State);
        s.Death(8, Script.Boss);
        Assert.Equal(EncounterOutcome.Kill, s.Snap(8).Outcome);
        s.Tick(20);
        Assert.Equal(EncounterOutcome.Kill, Assert.Single(s.Completed).Outcome);
    }

    [Fact]
    public void Boss_kill_by_kill_record()
    {
        var s = Script.Standard();
        BossFight(s, 1, 7);
        s.Kill(7.2, Script.Boss, Script.Me, "Me");
        Assert.Equal(EncounterOutcome.Kill, s.Snap(7.2).Outcome);
        s.Kill(7.3, Script.Trash, 0, null, skill: 0); // despawn form: ignored
        s.Tick(10);
        Assert.Single(s.Completed);
    }

    [Fact]
    public void Wipe_starts_new_attempt_and_increments_reset_count()
    {
        var s = Script.Standard();
        BossFight(s, 1, 7);
        s.Hp(7.1, Script.Boss, 3_000_000); // 83 %
        s.Hp(12, Script.Boss, 3_600_000);  // back to full → wipe
        var snap = s.Snap(12);
        Assert.Equal(MeterState.Ended, snap.State);
        Assert.Equal(EncounterOutcome.Wipe, snap.Outcome);
        var wipe = Assert.Single(s.Completed);
        Assert.Equal(EncounterOutcome.Wipe, wipe.Outcome);
        Assert.Equal(0, wipe.ResetCount);

        BossFight(s, 20, 30);
        Assert.Equal(MeterState.InCombat, s.Snap(30).State);
        s.Hp(30.5, Script.Boss, 0);
        s.Tick(40);
        Assert.Equal(2, s.Completed.Count);
        Assert.Equal(1, s.Completed[1].ResetCount);
        Assert.Equal(EncounterOutcome.Kill, s.Completed[1].Outcome);
        Assert.NotEqual(s.Completed[0].Id, s.Completed[1].Id);
    }

    [Fact]
    public void Small_hp_dips_are_not_wipes()
    {
        var s = Script.Standard();
        BossFight(s, 1, 5);
        s.Hp(5.1, Script.Boss, 3_500_000); // 97 %
        s.Hp(6, Script.Boss, 3_600_000);
        Assert.Equal(MeterState.InCombat, s.Snap(6).State);
    }

    [Fact]
    public void Idle_timeout_via_tick_for_trash_and_boss()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Trash, 100);
        s.Hit(10, Script.Me, Script.Trash, 100);
        s.Tick(19);
        Assert.Equal(MeterState.InCombat, s.Snap(19).State);
        s.Tick(20.5);
        Assert.Equal(EncounterOutcome.Timeout, s.Snap(20.5).Outcome);

        var b = Script.Standard();
        BossFight(b, 1, 10);
        b.Tick(30);
        Assert.Equal(MeterState.InCombat, b.Snap(30).State); // 30 s boss timeout
        b.Tick(41);
        Assert.Equal(EncounterOutcome.Timeout, b.Snap(41).Outcome);
        Assert.Equal(EncounterOutcome.Timeout, Assert.Single(b.Completed).Outcome);
    }

    [Fact]
    public void Idle_timeout_also_driven_by_event_time()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Trash, 100);
        s.Hit(30, Script.Me, Script.Trash2, 100); // a new encounter: the first one timed out by event time
        var rec = s.Record();
        Assert.Equal(Script.At(30), rec.StartUtc);
        Assert.Equal(100, rec.TotalDamage);
    }

    [Fact]
    public void Zone_change_ends_encounter_and_clears_entities()
    {
        var s = Script.Standard();
        BossFight(s, 1, 8);
        s.Map(9, Script.Map1); // same map: teleport, nothing happens
        Assert.Equal(MeterState.InCombat, s.Snap(9).State);

        s.Map(10, Script.Map2);
        var snap = s.Snap(10);
        Assert.Equal(EncounterOutcome.ZoneChange, snap.Outcome);
        Assert.Equal("Fire Temple (Hard)", snap.MapName);
        Assert.Equal(EncounterOutcome.ZoneChange, Assert.Single(s.Completed).Outcome);
        Assert.Equal("Me", s.Engine.LocalPlayer!.Name); // the local identity survives

        s.Hit(12, Script.Me, Script.Boss, 100); // id 5000 is unknown now
        var after = s.Snap(12);
        Assert.Equal(EncounterKind.Trash, after.EncounterKind);
        Assert.Null(after.Target!.NpcCode);
        Assert.False(after.Target.IsBoss);

        s.Hit(13, Script.Ally, Script.Boss, 100, Script.SorSkill); // the ally's binding is gone too
        Assert.Equal("Player 200", Script.Row(s.Snap(13), Script.Ally).Name);
    }

    [Fact]
    public void Self_info_with_new_name_ends_the_session_context()
    {
        var s = Script.Standard();
        BossFight(s, 1, 8);
        s.Self(9, 150, "Alt");
        Assert.Equal(EncounterOutcome.ZoneChange, s.Snap(9).Outcome);
        Assert.Equal("Alt", s.Engine.LocalPlayer!.Name);
    }

    [Fact]
    public void Local_id_change_keeps_identity_by_name()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Trash, 100);
        s.Self(2, 101, "Me");
        s.Hit(3, 101, Script.Trash, 200);
        var snap = s.Snap(3);
        var me = Assert.Single(snap.Rows, r => r.IsLocal);
        Assert.Equal(101u, me.EntityId);
        Assert.Equal(300, me.Damage);
    }

    [Fact]
    public void Manual_reset_saves_and_clears_display_but_keeps_context()
    {
        var s = Script.Standard();
        BossFight(s, 1, 8);
        s.Engine.Reset();
        Assert.Equal(EncounterOutcome.ManualReset, Assert.Single(s.Completed).Outcome);
        var snap = s.Snap(9);
        Assert.Equal(MeterState.WaitingForCombat, snap.State);
        Assert.Empty(snap.Rows);
        Assert.Equal("Fire Temple", snap.MapName);
        Assert.Null(s.Engine.GetCurrentEncounter());

        s.Hit(10, Script.Me, Script.Boss, 100); // entities kept: still the boss
        Assert.Equal(EncounterKind.Boss, s.Snap(10).EncounterKind);
    }

    [Fact]
    public void Trash_segment_is_dropped_when_a_boss_engages()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Trash, 1000);
        s.Hit(5, Script.Me, Script.Trash, 1000);
        Assert.Equal(EncounterKind.Trash, s.Snap(5).EncounterKind);
        s.Hit(8, Script.Me, Script.Boss, 7000);
        s.Hit(9, Script.Me, Script.Trash, 1000); // add during the boss fight
        s.Hit(10, Script.Me, Script.Boss, 3000);

        var snap = s.Snap(10);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        Assert.Equal(10_000, Script.Row(snap, Script.Me).Damage);    // boss only
        Assert.Equal(TimeSpan.FromSeconds(2), snap.Elapsed);
        Assert.True(snap.Targets[0].IsBoss);

        s.Engine.Mode = MeterMode.AllTargets;
        Assert.Equal(11_000, Script.Row(s.Snap(10), Script.Me).Damage); // everything since the boss pull

        var rec = s.Record();
        Assert.Equal(Script.At(8), rec.StartUtc);
        Assert.Equal(10_000, rec.TotalDamage);
        Assert.Equal(FakeGameData.BossCode, rec.BossNpcCode);
        Assert.Equal(3_600_000, rec.BossMaxHp);
        Assert.Empty(s.Completed);
    }

    [Fact]
    public void Adds_count_in_boss_fight_when_enabled()
    {
        var s = Script.Standard(new EngineOptions { CountAddsInBossFight = true });
        s.Hit(1, Script.Me, Script.Boss, 7000);
        s.Hit(2, Script.Me, Script.Trash, 1000);
        s.Hit(3, Script.Me, Script.Boss, 3000);
        var rec = s.Record();
        var me = Script.Combatant(rec, Script.Me);
        Assert.Equal(11_000, me.Damage);
        Assert.Equal(10_000, me.BossDamage);
        Assert.Equal(11_000, Script.Row(s.Snap(3), Script.Me).Damage);
    }

    [Fact]
    public void Big_hp_pool_is_a_boss_even_without_table_entry()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, 7777, 2999999, 6_800_000, 6_800_000);
        s.Hit(1, Script.Me, 7777, 100);
        Assert.Equal(EncounterKind.Boss, s.Snap(1).EncounterKind);
    }

    [Fact]
    public void Mid_stream_npc_becomes_boss_when_hp_reveals_it()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, 8888, 100);
        Assert.Equal(EncounterKind.Trash, s.Snap(1).EncounterKind);
        s.Send(new HpUpdateEvent { Time = Script.At(1.5), Entity = 8888, Hp = 6_000_000, HpMax = 6_800_000 });
        s.Hit(2, Script.Me, 8888, 100);
        var snap = s.Snap(2);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        Assert.Equal(6_800_000, snap.Target!.MaxHp);
    }

    [Fact]
    public void Dummy_gives_dummy_encounter_and_no_wipe()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Dummy, FakeGameData.DummyCode, 100_000, 100_000);
        s.Hit(1, Script.Me, Script.Dummy, 60_000);
        s.Hp(1.1, Script.Dummy, 40_000);
        s.Hp(2, Script.Dummy, 100_000);
        s.Hit(6, Script.Me, Script.Dummy, 60_000);
        var snap = s.Snap(6);
        Assert.Equal(EncounterKind.Dummy, snap.EncounterKind);
        Assert.Equal(MeterState.InCombat, snap.State);
        Assert.True(snap.Target!.IsDummy);
        s.Tick(40);
        Assert.Equal(EncounterKind.Dummy, Assert.Single(s.Completed).Kind);
    }

    [Fact]
    public void Training_mode_counts_everything_and_ends_on_timer()
    {
        var s = Script.Standard();
        s.Engine.StartTraining(TimeSpan.FromSeconds(30));
        Assert.Equal(MeterMode.AllTargets, s.Engine.Mode);
        Assert.StartsWith("Training ready", s.Snap(1).StatusText);

        s.Hit(5, Script.Me, Script.Boss, 1000);
        s.Hit(10, Script.Me, Script.Trash, 1000);
        s.Hit(34, Script.Me, Script.Boss, 1000);
        var live = s.Snap(20);
        Assert.Equal(EncounterKind.Training, live.EncounterKind);
        Assert.StartsWith("Training", live.StatusText);
        Assert.Equal(3000, Script.Row(live, Script.Me).Damage);

        s.Hit(36, Script.Me, Script.Trash, 1000); // past the 30 s timer
        var rec = Assert.Single(s.Completed);
        Assert.Equal(EncounterKind.Training, rec.Kind);
        Assert.Equal(30, rec.DurationSeconds, 6);
        Assert.Equal(3000, rec.TotalDamage);
        Assert.Equal(100, rec.PartyDps, 6);
        Assert.Contains("Training", rec.Note);
        Assert.Equal(MeterMode.BossOnly, s.Engine.Mode);
        Assert.Equal(EncounterKind.Trash, s.Snap(36).EncounterKind);
    }

    [Fact]
    public void Training_ends_via_tick()
    {
        var s = Script.Standard();
        s.Engine.StartTraining(TimeSpan.FromSeconds(10));
        s.Hit(1, Script.Me, Script.Trash, 500);
        s.Tick(12);
        Assert.Equal("Training complete", s.Snap(12).StatusText);
        Assert.Single(s.Completed);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(6, true)]
    public void Boss_fights_are_reported_when_long_enough(double length, bool reported)
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1 + length, Script.Me, Script.Boss, 100);
        s.Hp(1 + length + 0.1, Script.Boss, 0);
        s.Tick(1 + length + 5);
        Assert.Equal(reported, s.Completed.Count == 1);
    }

    [Theory]
    [InlineData(false, 20, false)]
    [InlineData(true, 20, true)]
    [InlineData(true, 10, false)]
    public void Trash_fights_follow_save_option_and_threshold(bool save, double length, bool reported)
    {
        var s = Script.Standard(new EngineOptions { SaveTrashFights = save });
        for (double t = 1; t <= 1 + length + 1e-9; t += 5) s.Hit(t, Script.Me, Script.Trash, 100);
        s.Tick(1 + length + 11);
        Assert.Equal(reported, s.Completed.Count == 1);
        if (reported) Assert.Equal(EncounterKind.Trash, s.Completed[0].Kind);
    }

    [Fact]
    public void Clear_session_resets_everything()
    {
        var s = Script.Standard();
        BossFight(s, 1, 8);
        s.Engine.ClearSession();
        Assert.Single(s.Completed);
        Assert.Equal(MeterState.Idle, s.Snap(9).State);
        Assert.Null(s.Engine.LocalPlayer);
    }

    [Fact]
    public void Player_deaths_are_counted_once()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        s.Kill(2, Script.Ally, Script.Boss, null, Script.NpcSkill);
        s.Death(2.1, Script.Ally);
        Assert.True(Script.Row(s.Snap(2.1), Script.Ally).IsDead);
        s.Hit(10, Script.Ally, Script.Boss, 100, Script.SorSkill); // resurrected and back in
        Assert.False(Script.Row(s.Snap(10), Script.Ally).IsDead);
        Assert.Equal(1, Script.Combatant(s.Record(), Script.Ally).Deaths);
    }

    [Fact]
    public void Capture_gap_flag_marks_the_encounter()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        Assert.False(s.Record().CaptureGaps);
        s.Engine.NotifyCaptureGap();
        Assert.True(s.Record().CaptureGaps);
    }
}
