using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public class SnapshotAndStatsTests
{
    [Fact]
    public void Contribution_uses_boss_max_hp_when_trusted()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 360_000);
        s.Hit(2, Script.Ally, Script.Boss, 720_000, Script.SorSkill);
        var snap = s.Snap(2);
        Assert.Equal(0.1, Script.Row(snap, Script.Me).Contribution, 6);
        Assert.Equal(0.2, Script.Row(snap, Script.Ally).Contribution, 6);
        Assert.Equal(1.0 / 3, Script.Row(snap, Script.Me).DamageShare, 6);
        Assert.Equal(0.5, Script.Row(snap, Script.Me).RelativeToTop, 6);
        Assert.Equal(1.0, Script.Row(snap, Script.Ally).RelativeToTop, 6);
        Assert.Equal(1, Script.Row(snap, Script.Ally).Rank);
        var rec = s.Record();
        Assert.Equal(0.1, Script.Combatant(rec, Script.Me).Contribution, 6);
        Assert.Equal(1.0 / 3, Script.Combatant(rec, Script.Me).DamageShare, 6);
    }

    [Fact]
    public void Contribution_falls_back_to_damage_share_without_max_hp()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Boss2, FakeGameData.BossCode2, null, null);
        s.Hit(1, Script.Me, Script.Boss2, 100_000);
        s.Hit(2, Script.Ally, Script.Boss2, 300_000, Script.SorSkill);
        var snap = s.Snap(2);
        Assert.Equal(EncounterKind.Boss, snap.EncounterKind);
        Assert.Equal(0.25, Script.Row(snap, Script.Me).Contribution, 6);
        Assert.Equal(0.25, Script.Combatant(s.Record(), Script.Me).Contribution, 6);
    }

    [Fact]
    public void Contribution_falls_back_when_joined_mid_fight()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Boss2, FakeGameData.BossCode2, 3_400_000, 6_800_000); // already at 50 %
        s.Hit(1, Script.Me, Script.Boss2, 100_000);
        s.Hit(2, Script.Ally, Script.Boss2, 100_000, Script.SorSkill);
        Assert.Equal(0.5, Script.Row(s.Snap(2), Script.Me).Contribution, 6);
    }

    [Fact]
    public void Min_avg_max_dps_footer()
    {
        var s = Script.Standard();
        s.Hit(0, Script.Me, Script.Trash, 1);
        s.Hit(10, Script.Me, Script.Trash, 999);       // 1000 over 10 s
        s.Hit(0, Script.Ally, Script.Trash, 1000, Script.SorSkill);
        s.Hit(10, Script.Ally, Script.Trash, 2000, Script.SorSkill); // 3000 over 10 s
        var snap = s.Snap(10);
        Assert.Equal(100, snap.MinDps, 6);
        Assert.Equal(300, snap.MaxDps, 6);
        Assert.Equal(200, snap.AvgDps, 6);
    }

    [Fact]
    public void Hp_check_passes_when_damage_explains_hp_loss_including_self_heals()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Boss2, FakeGameData.BossCode2, 1_000_000, 1_000_000);
        s.Hit(1, Script.Me, Script.Boss2, 100_000);
        s.Hp(1.1, Script.Boss2, 900_000);
        s.Hit(2, Script.Me, Script.Boss2, 100_000);
        s.Hp(2.1, Script.Boss2, 800_000);
        s.Dot(2.5, Script.Me, Script.Boss2, 0x0A, 50_000, effect: 230017101, skill: Script.RanSkill); // boss self-heal
        s.Hp(2.6, Script.Boss2, 850_000);
        s.Hit(3, Script.Me, Script.Boss2, 50_000);
        s.Hp(3.1, Script.Boss2, 800_000);

        var check = s.Record().HpCheck;
        Assert.NotNull(check);
        Assert.Equal(250_000, check!.DecodedDamage);
        Assert.Equal(200_000, check.HpLost);
        Assert.Equal(50_000, check.BossSelfHealing);
        Assert.Equal(1.0, check.Ratio, 6);
        Assert.True(check.Passed);
        Assert.Equal(1.0, s.Snap(3.1).HpCheckRatio!.Value, 6);
    }

    [Fact]
    public void Hp_check_fails_when_damage_is_missing()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Boss2, FakeGameData.BossCode2, 1_000_000, 1_000_000);
        s.Hit(1, Script.Me, Script.Boss2, 100_000);
        s.Hp(1.1, Script.Boss2, 800_000);
        s.Hit(2, Script.Me, Script.Boss2, 100_000);
        s.Hp(2.1, Script.Boss2, 600_000);
        var check = s.Record().HpCheck!;
        Assert.Equal(0.5, check.Ratio, 6);
        Assert.False(check.Passed);
    }

    [Fact]
    public void Hp_check_skips_gaps_and_shield_phases()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Boss2, FakeGameData.BossCode2, 1_000_000, 1_000_000);
        s.Hit(1, Script.Me, Script.Boss2, 100_000);
        s.Hp(1.1, Script.Boss2, 900_000);
        s.Hit(6, Script.Me, Script.Boss2, 100_000);   // 5 s without readings: gap
        s.Hp(6.1, Script.Boss2, 700_000);
        s.Hit(6.5, Script.Me, Script.Boss2, 10_000);  // shield: HP frozen for > 3 s while hit
        s.Hp(7, Script.Boss2, 700_000);
        s.Hit(8, Script.Me, Script.Boss2, 10_000);
        s.Hp(9, Script.Boss2, 700_000);
        s.Hit(10, Script.Me, Script.Boss2, 10_000);
        s.Hp(10.5, Script.Boss2, 700_000);
        s.Hit(11, Script.Me, Script.Boss2, 50_000);
        s.Hp(11.1, Script.Boss2, 650_000);
        var check = s.Record().HpCheck!;
        Assert.True(check.Passed);
        Assert.Equal(150_000, check.DecodedDamage);
        Assert.NotNull(check.Note);
    }

    [Fact]
    public void Boss_hp_timeline_is_sampled()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        for (int i = 1; i <= 20; i++) s.Hp(1 + i * 0.1, Script.Boss, 3_600_000 - i * 1000);
        var tl = s.Record().BossHpTimeline;
        Assert.Equal(3_600_000, tl[0].Hp);
        Assert.Equal(0f, tl[0].T);
        Assert.Equal(3_580_000, tl[^1].Hp);
        Assert.InRange(tl.Count, 8, 12); // ~every 250 ms over 2 s, plus the latest reading
        for (int i = 1; i < tl.Count - 1; i++) Assert.True(tl[i].T - tl[i - 1].T >= 0.249f);
    }

    [Fact]
    public void Snapshot_gear_score_uses_the_latest_roster_and_keeps_missing_scores_unknown()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        Assert.Null(Script.Row(s.Snap(1), Script.Me).GearScore);
        Assert.Null(Script.Row(s.Snap(1), Script.Ally).GearScore);

        s.Send(new PartyRosterEvent
        {
            Time = Script.At(2),
            Members = new[]
            {
                new PartyMember { Name = "Me", GearScore = 2480 },
                new PartyMember { Name = "Ally" },
            },
        });
        var before = s.Snap(2);
        Assert.Equal(2480u, Script.Row(before, Script.Me).GearScore);
        Assert.Null(Script.Row(before, Script.Ally).GearScore);

        s.Send(new PartyRosterEvent
        {
            Time = Script.At(3),
            Members = new[] { new PartyMember { Name = "Me", GearScore = 2510 } },
        });
        var after = s.Snap(3);
        Assert.Equal(2510u, Script.Row(after, Script.Me).GearScore);
        Assert.Null(Script.Row(after, Script.Ally).GearScore);
        Assert.Equal(2480u, Script.Row(before, Script.Me).GearScore);
        Assert.Equal(before.TotalDamage, after.TotalDamage);
    }

    [Fact]
    public void Snapshot_gear_score_appears_after_late_player_info_resolves_an_ambiguous_roster_member()
    {
        var s = Script.Standard();
        s.Roster(0, ("Me", 6), ("First", 18), ("Second", 18));
        s.Hit(1, 950, Script.Boss, 100, 13_010_000);
        var before = Script.Row(s.Snap(1), 950);
        Assert.Equal("Player 950", before.Name);
        Assert.Null(before.GearScore);

        s.Player(2, 950, "Second", 18);
        var after = Script.Row(s.Snap(2), 950);
        Assert.Equal("Second", after.Name);
        Assert.Equal(1002u, after.GearScore);
        Assert.Equal(before.Damage, after.Damage);
        Assert.Null(before.GearScore);
    }

    [Fact]
    public void Snapshot_gear_score_does_not_reuse_a_removed_roster_members_score()
    {
        var s = Script.Standard();
        s.Roster(0, ("Me", 6), ("Ally", 26));
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        Assert.Equal(1001u, Script.Row(s.Snap(1), Script.Ally).GearScore);

        s.Roster(2, ("Me", 6));
        Assert.Null(Script.Row(s.Snap(2), Script.Ally).GearScore);
    }

    [Fact]
    public void Party_only_filter_shows_local_and_roster()
    {
        var s = Script.Standard();
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.Roster(0, ("Me", 6), ("Ally", 26));
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        s.Hit(1, Script.Stranger, Script.Boss, 100, Script.RanSkill);
        Assert.Equal(3, s.Snap(1).Rows.Count);

        s.Options.PartyOnly = true;
        var snap = s.Snap(1);
        Assert.Equal(2, snap.Rows.Count);
        Assert.DoesNotContain(snap.Rows, r => r.EntityId == Script.Stranger);
        Assert.True(Script.Row(snap, Script.Ally).IsPartyMember);
        Assert.Equal(200, snap.TotalDamage);

        var rec = s.Record();
        var ally = Script.Combatant(rec, Script.Ally);
        Assert.True(ally.IsPartyMember);
        Assert.Equal(1001u, ally.GearScore);
        Assert.Equal(500_001UL, ally.CombatPower);
        Assert.Equal(30u, ally.Level);
        Assert.False(Script.Combatant(rec, Script.Stranger).IsPartyMember);
    }

    [Fact]
    public void Party_only_without_roster_shows_only_the_local_player()
    {
        var s = Script.Standard(new EngineOptions { PartyOnly = true });
        s.Hit(1, Script.Me, Script.Boss, 300);
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);   // a stranger: no roster says otherwise

        var snap = s.Snap(1);
        var row = Assert.Single(snap.Rows);
        Assert.Equal(Script.Me, row.EntityId);
        Assert.Equal(300, snap.TotalDamage);
        Assert.Equal(1.0, row.DamageShare, 6);

        s.Options.PartyOnly = false;
        Assert.Equal(2, s.Snap(1).Rows.Count);
    }

    [Fact]
    public void Party_only_shows_everyone_while_neither_roster_nor_local_player_is_known()
    {
        var s = new Script(new EngineOptions { PartyOnly = true });
        s.Map(0, Script.Map1);
        s.Player(0, Script.Ally, "Ally", 26);
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        s.Hit(1, Script.Stranger, Script.Boss, 100, Script.RanSkill);
        Assert.Equal(2, s.Snap(1).Rows.Count);   // filtering now would show an empty meter
    }

    [Fact]
    public void Party_only_picks_up_members_when_the_roster_arrives_mid_fight()
    {
        var s = Script.Standard(new EngineOptions { PartyOnly = true });
        s.Player(0, Script.Stranger, "Stranger", 14);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, Script.Ally, Script.Boss, 100, Script.SorSkill);
        s.Hit(1, Script.Stranger, Script.Boss, 100, Script.RanSkill);
        Assert.Single(s.Snap(1).Rows);

        s.Roster(2, ("Me", 6), ("Ally", 26));
        var snap = s.Snap(2);
        Assert.Equal(new[] { Script.Ally, Script.Me }.OrderBy(x => x), snap.Rows.Select(r => r.EntityId).OrderBy(x => x));
        Assert.Equal(200, snap.TotalDamage);
    }

    [Fact]
    public void Roster_member_never_named_in_world_is_joined_by_unique_class()
    {
        var s = Script.Standard();
        s.Roster(0, ("Me", 6), ("Ally", 26), ("Shadow", 18));
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1, 950, Script.Boss, 100, 13_010_000); // unnamed Assassin
        var row = Script.Row(s.Snap(1), 950);
        Assert.Equal("Shadow", row.Name);
        Assert.True(row.IsPartyMember);
    }

    [Fact]
    public void Target_cycling_moves_through_tracked_targets()
    {
        var s = Script.Standard();
        s.SpawnNpc(0, Script.Add, FakeGameData.AddCode, 80_000, 80_000);
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(2, Script.Me, Script.Trash, 500);
        s.Hit(3, Script.Me, Script.Add, 300);
        var snap = s.Snap(3);
        Assert.Equal(3, snap.Targets.Count);
        Assert.Equal(Script.Boss, snap.Targets[0].EntityId);
        Assert.Equal(Script.Trash, snap.Targets[1].EntityId);
        Assert.Equal(Script.Boss, snap.Target!.EntityId);
        Assert.Equal("Enhanced Harcon", snap.Target.Name);
        Assert.Equal(1.0, snap.Target.HpFraction);

        s.Engine.CycleTarget(1);
        Assert.Equal(Script.Trash, s.Snap(3).Target!.EntityId);
        s.Engine.CycleTarget(1);
        Assert.Equal(Script.Add, s.Snap(3).Target!.EntityId);
        s.Engine.CycleTarget(1);
        Assert.Equal(Script.Boss, s.Snap(3).Target!.EntityId);
        s.Engine.CycleTarget(-1);
        Assert.Equal(Script.Add, s.Snap(3).Target!.EntityId);
    }

    [Fact]
    public void Target_hp_follows_entity_stats()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hp(1.5, Script.Boss, 1_800_000);
        var t = s.Snap(2).Target!;
        Assert.Equal(1_800_000, t.Hp);
        Assert.Equal(0.5, t.HpFraction!.Value, 6);
        Assert.Equal(100, t.DamageTaken);
    }

    [Fact]
    public void Ping_is_smoothed_and_range_checked()
    {
        long clock = 5_000;
        var s = new Script(clientClock: () => clock);
        s.Map(0, Script.Map1);
        Assert.Null(s.Snap(0).PingMs);
        s.Send(new PingEvent { Time = Script.At(1), ClientSentMs = 5_000 + 16_777_216_000 - 40 });
        Assert.Equal(40, s.Snap(1).PingMs!.Value, 6);
        s.Send(new PingEvent { Time = Script.At(2), ClientSentMs = 5_000 + 16_777_216_000 - 80 });
        Assert.Equal(50, s.Snap(2).PingMs!.Value, 6);
        s.Send(new PingEvent { Time = Script.At(3), ClientSentMs = 12345 });          // replayed / foreign clock
        s.Send(new PingEvent { Time = Script.At(4), ClientSentMs = 5_000 + 16_777_216_000 + 10 }); // negative
        Assert.Equal(50, s.Snap(4).PingMs!.Value, 6);
    }

    [Fact]
    public void Buff_uptime_is_clamped_to_the_encounter()
    {
        var s = Script.Standard();
        s.Send(new BuffAppliedEvent { Time = Script.At(-5), Target = Script.Me, BuffId = 181200301, DurationMs = 10_000, Caster = Script.Ally, Stack = 1 });
        s.Send(new BuffAppliedEvent { Time = Script.At(1), Target = Script.Me, BuffId = 110100001, DurationMs = uint.MaxValue, Caster = Script.Me, Stack = 2 });
        s.Hit(0, Script.Me, Script.Boss, 100);
        s.Send(new BuffRemovedEvent { Time = Script.At(11), Target = Script.Me, BuffId = 110100001 });
        s.Hit(20, Script.Me, Script.Boss, 100);
        var buffs = Script.Combatant(s.Record(), Script.Me).Buffs;
        var a = buffs.Single(b => b.BuffId == 181200301);
        Assert.Equal(5, a.UptimeSeconds, 6);   // active −5..5, encounter 0..20
        Assert.Equal(0.25, a.Uptime, 6);
        Assert.Equal(18120030u, a.SkillId);
        Assert.Equal(Script.Ally, a.CasterEntityId);
        var b2 = buffs.Single(b => b.BuffId == 110100001);
        Assert.Equal(10, b2.UptimeSeconds, 6);
        Assert.Equal(1, b2.Applications);
    }

    [Fact]
    public void Record_is_complete_and_independent()
    {
        var s = Script.Standard();
        s.Player(0, Script.Cleric, "Healer", 30);
        s.SpawnSummon(0, 9001, owner: Script.Ally);
        s.Send(new BuffAppliedEvent { Time = Script.At(0.5), Target = Script.Me, BuffId = 181200301, DurationMs = 10_000, Caster = Script.Cleric });
        s.Hit(1, Script.Me, Script.Boss, 1000, layout: 6, dmgType: 3, dir: HitDirection.Back);
        s.Hit(1.5, Script.Ally, Script.Boss, 2000, Script.SorSkill);
        s.Hit(2, 9001, Script.Boss, 657, Script.SorSkill + 1);
        s.Dot(2.5, Script.Me, Script.Boss, 0x0A, 300, effect: 1102003011, skill: Script.GlaSkill);
        s.Hit(3, Script.Boss, Script.Me, 800, Script.NpcSkill);
        s.Hit(3.5, Script.Cleric, Script.Me, 700, FakeGameData.HealSkill);
        s.Hp(4, Script.Boss, 3_600_000 - 3957);
        s.Hit(6, Script.Me, Script.Boss, 1000);
        s.Hp(6.1, Script.Boss, 3_600_000 - 4957);

        var rec = s.Record();
        Assert.Equal(EncounterRecord.CurrentSchemaVersion, rec.SchemaVersion);
        Assert.Equal(EncounterKind.Boss, rec.Kind);
        Assert.Equal(EncounterOutcome.InProgress, rec.Outcome);
        Assert.Equal(Script.Map1, rec.MapId);
        Assert.Equal((ushort)1304, rec.ServerId);
        Assert.Equal("Me", rec.LocalPlayerName);
        Assert.Equal(CharacterClass.Gladiator, rec.LocalPlayerClass);
        Assert.Equal(Script.Boss, rec.BossEntityId);
        Assert.Equal(3_600_000, rec.BossHpStart);
        Assert.Equal(3_600_000 - 4957, rec.BossHpEnd);
        Assert.NotEmpty(rec.BossHpTimeline);
        Assert.NotNull(rec.HpCheck);
        Assert.True(rec.HpCheck!.Passed);
        Assert.Equal(4957, rec.TotalDamage);
        Assert.Equal(5, rec.DurationSeconds, 6);
        Assert.Single(rec.Targets);
        Assert.Equal(7, rec.Hits.Count);
        Assert.True(rec.Hits.Zip(rec.Hits.Skip(1)).All(p => p.First.T <= p.Second.T));

        var me = Script.Combatant(rec, Script.Me);
        Assert.True(me.IsLocal);
        Assert.Equal(2300, me.Damage);
        Assert.Equal(800, me.DamageTaken);
        Assert.Equal(700, me.Defense.HealingReceived);
        Assert.NotEmpty(me.Skills);
        Assert.NotEmpty(me.DamageTakenBySource);
        Assert.NotEmpty(me.Buffs);
        Assert.Equal(6, me.DamagePerSecond.Count);
        Assert.Equal(1000, me.DamagePerSecond[0]);
        Assert.Equal(300, me.DamagePerSecond[1]);
        Assert.Equal(1000, me.DamagePerSecond[5]);
        Assert.Equal(Script.At(1), me.FirstHitUtc);
        Assert.Equal(Script.At(6), me.LastHitUtc);
        Assert.Equal(1, me.Quality.Back);
        Assert.Equal(1, me.Quality.DotTicks);
        Assert.Equal(1000, me.Quality.MaxHit);
        Assert.Equal(Script.GlaSkill, me.Quality.MaxHitSkillId);

        var ally = Script.Combatant(rec, Script.Ally);
        Assert.Equal(2657, ally.Damage);
        Assert.Equal(CharacterClass.Sorcerer, ally.Class);
        Assert.Contains(ally.Skills, x => x.FromSummon);
        Assert.Equal(700, Script.Combatant(rec, Script.Cleric).Healing);

        var again = s.Record();
        Assert.NotSame(rec, again);
        rec.Combatants.Clear();
        Assert.NotEmpty(s.Record().Combatants);
    }

    [Fact]
    public void Snapshot_after_end_shows_final_numbers_until_next_fight()
    {
        var s = Script.Standard();
        s.Hit(1, Script.Me, Script.Boss, 1000);
        s.Hit(11, Script.Me, Script.Boss, 1000);
        s.Hp(11.1, Script.Boss, 0);
        var a = s.Snap(12);
        var b = s.Snap(100);
        Assert.Equal(MeterState.Ended, b.State);
        Assert.Equal(200, Script.Row(a, Script.Me).Dps, 6);
        Assert.Equal(200, Script.Row(b, Script.Me).Dps, 6);
        Assert.Equal(TimeSpan.FromSeconds(10), b.Elapsed);
        Assert.StartsWith("Kill", b.StatusText);
        Assert.NotNull(s.Engine.GetCurrentEncounter());
    }
}
