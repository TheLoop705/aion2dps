using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

/// <summary>
/// Multi-boss encounters (real Fire Temple run: the party split on Silver Blade Rotan and Black Smoke Murute at the same
/// time), sequential bosses, per-boss HP checks, max-HP rescales and per-boss wipes.
/// </summary>
public sealed class MultiBossTests
{
    private const uint Rotan = Script.Boss;   // smaller boss (600k), engaged first
    private const uint Murute = Script.Boss2; // bigger boss (900k), engaged second
    private const uint Assassin = 900;

    private static Script Arena()
    {
        var s = new Script();
        s.Map(0, Script.Map1);
        s.Self(0);                                   // Me: Gladiator
        s.Player(0, Script.Ally, "Ally", 26);        // Sorcerer
        s.Player(0, Assassin, "Shade", 18);          // Assassin
        s.SpawnNpc(0, Rotan, FakeGameData.BossCode, 600_000, 600_000);
        s.SpawnNpc(0, Murute, FakeGameData.BossCode2, 900_000, 900_000);
        return s;
    }

    /// <summary>Hits a boss and sends the matching HP reading 50 ms later.</summary>
    private sealed class Fight(Script s)
    {
        private readonly Dictionary<uint, long> _hp = new() { [Rotan] = 600_000, [Murute] = 900_000 };

        public void Hit(double t, uint actor, uint boss, long amount, uint skill = Script.GlaSkill, uint power = 10170)
        {
            s.Hit(t, actor, boss, amount, skill, power: power);
            _hp[boss] = Math.Max(0, _hp[boss] - amount);
            s.Hp(t + 0.05, boss, _hp[boss]);
        }

        public long Hp(uint boss) => _hp[boss];

        public void Set(uint boss, long hp) => _hp[boss] = hp;
    }

    [Fact]
    public void Overlapping_bosses_share_one_encounter_with_a_boss_list()
    {
        var s = Arena();
        var f = new Fight(s);
        // Group A (Me + Ally) on Rotan from t=1; group B (Shade) pulls Murute at t=10 while Rotan is still being fought.
        for (int i = 0; i < 30; i++)
        {
            f.Hit(1 + i, Script.Me, Rotan, 10_000);
            f.Hit(1.2 + i, Script.Ally, Rotan, 10_000, Script.SorSkill);
        }
        for (int i = 0; i < 45; i++) f.Hit(10 + i * 0.5, Assassin, Murute, 20_000, 13_010_000);
        s.Tick(40);

        var snap = s.Snap(40);
        Assert.Equal(MeterState.Ended, snap.State); // both dead → kill
        var rec = Assert.Single(s.Completed);
        Assert.Equal(EncounterOutcome.Kill, rec.Outcome);
        Assert.Equal(2, rec.Bosses.Count);
        Assert.Equal(new[] { Rotan, Murute }, rec.Bosses.Select(b => b.EntityId));
        Assert.Equal(new uint?[] { FakeGameData.BossCode, FakeGameData.BossCode2 }, rec.Bosses.Select(b => b.NpcCode));

        // Primary = largest max HP (Murute), mirrored by the compatibility fields.
        Assert.Equal(FakeGameData.BossCode2, rec.BossNpcCode);
        Assert.Equal(Murute, rec.BossEntityId);
        Assert.Equal(900_000, rec.BossMaxHp);
        Assert.True(rec.Bosses[1].IsPrimary);
        Assert.False(rec.Bosses[0].IsPrimary);
        Assert.True(rec.Bosses.All(b => b.Killed));
        Assert.Equal(1_500_000, rec.TotalDamage);

        // Contribution = damage to the encounter's bosses / sum of their max HP (1.5M).
        var shade = Script.Combatant(rec, Assassin);
        Assert.Equal(900_000, shade.AllBossesDamage);
        Assert.Equal(900_000, shade.BossDamage); // primary only
        Assert.Equal(0.6, shade.Contribution, 6);
        var me = Script.Combatant(rec, Script.Me);
        Assert.Equal(0, me.BossDamage);
        Assert.Equal(300_000, me.AllBossesDamage);
        Assert.Equal(0.2, me.Contribution, 6);
        Assert.Equal(1.0, rec.Combatants.Sum(c => c.Contribution), 6);

        // Per-boss contribution.
        var rotanShares = rec.Bosses[0].DamageByCombatant;
        Assert.Equal(2, rotanShares.Count);
        Assert.Equal(0.5, rotanShares.Single(x => x.EntityId == Script.Me).Contribution!.Value, 6);
        Assert.Equal(1.0, rec.Bosses[1].DamageByCombatant.Single().Contribution!.Value, 6);

        // HP check per boss and overall.
        Assert.Equal(1.0, rec.Bosses[0].HpCheck!.Ratio, 6);
        Assert.Equal(1.0, rec.Bosses[1].HpCheck!.Ratio, 6);
        Assert.Equal(1.0, rec.HpCheck!.Ratio, 6);
        Assert.Equal(1.0, rec.OverallHpCheck!.Ratio, 6);
        Assert.True(rec.Bosses[0].HpTimeline.Count > 10 && rec.Bosses[1].HpTimeline.Count > 10);
        Assert.Equal(rec.Bosses[1].HpTimeline, rec.BossHpTimeline);
        Assert.InRange(rec.Bosses[1].EngagedSeconds, 8.9, 9.1);
        Assert.NotNull(rec.Bosses[0].KillTimeSeconds);
    }

    [Fact]
    public void Live_snapshot_lists_engaged_bosses_and_shows_the_one_the_local_player_hits()
    {
        var s = Arena();
        var f = new Fight(s);
        f.Hit(1, Script.Me, Rotan, 60_000);
        f.Hit(2, Assassin, Murute, 90_000, 13_010_000);
        f.Hit(3, Script.Ally, Rotan, 60_000, Script.SorSkill);

        var snap = s.Snap(3.5);
        Assert.Equal(MeterState.InCombat, snap.State);
        Assert.Equal(2, snap.Bosses.Count);
        Assert.Equal(Murute, snap.Bosses[0].EntityId);   // primary first
        Assert.True(snap.Bosses[0].IsPrimaryBoss);
        Assert.Equal(Rotan, snap.Bosses[1].EntityId);
        Assert.Equal(0.8, snap.Bosses[1].HpFraction!.Value, 6);
        Assert.Equal(0.9, snap.Bosses[0].HpFraction!.Value, 6);
        Assert.Equal(new[] { Murute, Rotan }, snap.Targets.Take(2).Select(t => t.EntityId));
        Assert.All(snap.Targets.Take(2), t => Assert.True(t.IsEncounterBoss));
        Assert.Equal(Rotan, snap.Target!.EntityId); // the boss the local player hit most recently
        Assert.Equal("Enhanced Harcon + Ultimate Berk", snap.StatusText);

        f.Hit(4, Script.Me, Murute, 10_000);
        Assert.Equal(Murute, s.Snap(4.5).Target!.EntityId);
        s.Engine.CycleTarget(1);
        Assert.Equal(Rotan, s.Snap(4.6).Target!.EntityId);

        // Live contribution over both bosses: Me 60k + 10k of 1.5M.
        Assert.Equal(70_000 / 1_500_000.0, Script.Row(s.Snap(5), Script.Me).Contribution, 6);
        Assert.Equal(1.0, s.Snap(5).OverallHpCheckRatio!.Value, 6);
        Assert.Equal(1.0, s.Snap(5).Bosses[1].HpCheckRatio!.Value, 6);
    }

    [Fact]
    public void Sequential_bosses_separated_by_a_kill_get_one_encounter_each()
    {
        var s = Arena();
        var f = new Fight(s);
        for (int i = 0; i < 6; i++) f.Hit(1 + i, Script.Me, Rotan, 100_000);
        // Rotan dead at t≈6; the party walks to Murute.
        for (int i = 0; i < 9; i++) f.Hit(30 + i, Script.Me, Murute, 100_000);
        s.Tick(60);

        Assert.Equal(2, s.Completed.Count);
        var first = s.Completed[0];
        var second = s.Completed[1];
        Assert.Equal(EncounterOutcome.Kill, first.Outcome);
        Assert.Equal(FakeGameData.BossCode, first.BossNpcCode);
        Assert.Equal(Rotan, Assert.Single(first.Bosses).EntityId);
        Assert.Equal(EncounterOutcome.Kill, second.Outcome);
        Assert.Equal(FakeGameData.BossCode2, second.BossNpcCode);
        Assert.Equal(Murute, Assert.Single(second.Bosses).EntityId);
        Assert.Equal(1.0, Script.Combatant(first, Script.Me).Contribution, 6);
        Assert.Equal(1.0, Script.Combatant(second, Script.Me).Contribution, 6);
    }

    [Fact]
    public void A_boss_left_alone_before_the_next_boss_is_pulled_is_a_separate_encounter()
    {
        var s = Arena();
        var f = new Fight(s);
        for (int i = 0; i < 6; i++) f.Hit(1 + i, Script.Me, Rotan, 10_000);
        // No hit on Rotan for 20 s (longer than the join window, shorter than the 30 s boss idle timeout).
        for (int i = 0; i < 6; i++) f.Hit(26 + i, Script.Me, Murute, 10_000);
        s.Tick(80);

        Assert.Equal(2, s.Completed.Count);
        Assert.Equal(EncounterOutcome.Timeout, s.Completed[0].Outcome);
        Assert.Equal(Rotan, Assert.Single(s.Completed[0].Bosses).EntityId);
        Assert.Equal(Murute, Assert.Single(s.Completed[1].Bosses).EntityId);
    }

    [Fact]
    public void A_boss_pulled_during_the_kill_grace_of_another_joins_its_encounter()
    {
        var s = Arena();
        var f = new Fight(s);
        for (int i = 0; i < 6; i++) f.Hit(1 + i, Script.Me, Rotan, 100_000);      // Rotan dies at t≈6.05
        for (int i = 0; i < 9; i++) f.Hit(7 + i, Assassin, Murute, 100_000, 13_010_000); // 0.95 s later
        s.Tick(40);

        var rec = Assert.Single(s.Completed);
        Assert.Equal(EncounterOutcome.Kill, rec.Outcome);
        Assert.Equal(new[] { Rotan, Murute }, rec.Bosses.Select(b => b.EntityId));
    }

    [Fact]
    public void Max_hp_rescale_with_hp_raised_to_match_is_never_a_wipe()
    {
        var s = Arena();
        // Mid-fight rescale (a fifth player joins): Rotan at 50 % of 120k, then max 240k and HP raised to match.
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 120_000, 120_000);
        s.Hit(1, Script.Me, Rotan, 30_000);
        s.Hp(1.05, Rotan, 90_000);
        s.Hit(2, Script.Me, Rotan, 30_000);
        s.Hp(2.05, Rotan, 60_000);
        s.Send(new EntityStatsEvent { Time = Script.At(3), Entity = Rotan, Format = 2, Stats64 = new Dictionary<byte, long> { [7] = 240_000 } });
        s.Hp(3.05, Rotan, 240_000);
        s.Hit(4, Script.Me, Rotan, 40_000);
        s.Hp(4.05, Rotan, 200_000);

        // Same again, but the raised HP arrives before the kind-7 record.
        s.Hp(5, Rotan, 360_000);
        s.Send(new EntityStatsEvent { Time = Script.At(5.05), Entity = Rotan, Format = 2, Stats64 = new Dictionary<byte, long> { [7] = 360_000 } });
        s.Hit(6, Script.Me, Rotan, 60_000);
        s.Hp(6.05, Rotan, 300_000);

        var snap = s.Snap(7);
        Assert.Equal(MeterState.InCombat, snap.State);
        Assert.Empty(s.Completed);
        var rec = s.Record();
        Assert.Equal(EncounterOutcome.InProgress, rec.Outcome);
        var boss = Assert.Single(rec.Bosses);
        Assert.Equal(0, boss.Resets);
        Assert.Equal(360_000, boss.MaxHp);
        Assert.Equal(1.0, boss.HpCheck!.Ratio, 6);
        Assert.Contains("rescale", boss.HpCheck.Note);
    }

    [Fact]
    public void Hp_update_max_overrides_the_spawn_base_max_for_any_npc()
    {
        var s = Arena();
        s.SpawnNpc(0, Script.Trash, FakeGameData.TrashCode, 19_950, 19_950);
        s.Send(new HpUpdateEvent { Time = Script.At(0.5), Entity = Script.Trash, Hp = 99_750, HpMax = 99_750 });
        s.Hit(1, Script.Me, Script.Trash, 9_975);
        s.Send(new HpUpdateEvent { Time = Script.At(1.05), Entity = Script.Trash, Hp = 89_775, HpMax = 99_750 });
        var t = s.Snap(2).Targets.Single(x => x.EntityId == Script.Trash);
        Assert.Equal(99_750, t.MaxHp);
        Assert.Equal(0.9, t.HpFraction!.Value, 6);

        // Stat kind 7 beats a later 1B 92 max.
        s.Send(new EntityStatsEvent { Time = Script.At(3), Entity = Script.Trash, Format = 2, Stats64 = new Dictionary<byte, long> { [7] = 120_000 } });
        s.Send(new HpUpdateEvent { Time = Script.At(3.5), Entity = Script.Trash, Hp = 89_775, HpMax = 99_750 });
        Assert.Equal(120_000, s.Snap(4).Targets.Single(x => x.EntityId == Script.Trash).MaxHp);
    }

    [Fact]
    public void Wipe_is_detected_per_boss()
    {
        var s = Arena();
        var f = new Fight(s);
        f.Hit(1, Script.Me, Rotan, 300_000);
        f.Hit(2, Assassin, Murute, 300_000, 13_010_000);
        f.Hit(3, Script.Me, Rotan, 100_000);
        // Rotan resets to full while Murute is still being fought: not the end of the encounter.
        s.Hp(4, Rotan, 600_000);
        f.Hit(7, Assassin, Murute, 100_000, 13_010_000);
        Assert.Equal(MeterState.InCombat, s.Snap(7.5).State);
        Assert.Empty(s.Completed);
        var live = s.Record();
        Assert.Equal(1, live.Bosses.Single(b => b.EntityId == Rotan).Resets);
        Assert.Equal(0, live.Bosses.Single(b => b.EntityId == Murute).Resets);

        // Then Murute resets too → the encounter is a wipe.
        s.Hp(8, Murute, 900_000);
        var rec = Assert.Single(s.Completed);
        Assert.Equal(EncounterOutcome.Wipe, rec.Outcome);
        Assert.All(rec.Bosses, b => Assert.Equal(1, b.Resets));
        Assert.All(rec.Bosses, b => Assert.False(b.Killed));
        // The HP check ignores the reset jump.
        Assert.Equal(1.0, rec.Bosses[0].HpCheck!.Ratio, 6);
        Assert.Equal(1.0, rec.Bosses[1].HpCheck!.Ratio, 6);

        // Re-pulling both: the reset count is carried per boss.
        f = new Fight(s);
        f.Hit(20, Script.Me, Murute, 10_000);
        Assert.Equal(1, s.Record().Bosses.Single().ResetCountBefore);
    }

    [Fact]
    public void Reset_boss_that_is_pulled_again_rejoins_and_the_fight_can_still_be_a_kill()
    {
        var s = Arena();
        var f = new Fight(s);
        f.Hit(1, Script.Me, Rotan, 300_000);
        f.Hit(1.5, Assassin, Murute, 450_000, 13_010_000);
        f.Hit(2, Script.Me, Rotan, 100_000);
        s.Hp(3, Rotan, 600_000); // Rotan resets while Murute is still alive
        f.Set(Rotan, 600_000);
        for (int i = 0; i < 6; i++) f.Hit(3.5 + i, Script.Me, Rotan, 100_000); // pulled again, dies at t≈8.55
        f.Hit(4, Assassin, Murute, 450_000, 13_010_000);                        // Murute dies at t≈4.05
        Assert.Empty(s.Completed);
        s.Tick(30);
        var rec = Assert.Single(s.Completed);
        Assert.Equal(EncounterOutcome.Kill, rec.Outcome);
        Assert.Equal(1, rec.Bosses.Single(b => b.EntityId == Rotan).Resets);
        Assert.True(rec.Bosses.All(b => b.Killed));
    }

    [Fact]
    public void Killing_window_overkill_is_excluded_from_the_hp_check()
    {
        var s = Arena();
        s.Hit(1, Script.Me, Rotan, 590_000);
        s.Hp(1.05, Rotan, 10_000);
        s.Hp(3.5, Rotan, 10_000); // periodic readings (no gap ≥ 3 s)
        s.Hp(6, Rotan, 10_000);
        s.Hit(7, Script.Me, Rotan, 8_000);
        s.Hit(7, Assassin, Rotan, 9_000, 13_010_000); // both land on the last 10,000
        s.Hp(7.05, Rotan, 0);
        s.Tick(15);
        var rec = Assert.Single(s.Completed);
        Assert.Equal(1.0, rec.HpCheck!.Ratio, 6);
        Assert.Equal(7_000, rec.HpCheck.Overkill);
        Assert.Equal(607_000, rec.TotalDamage); // the damage itself is still counted
    }

    // ───────────── unresolved actors and damage-only players ─────────────

    [Fact]
    public void Unknown_actor_in_a_fully_bound_instance_party_goes_to_unknown_summons_then_to_its_owner()
    {
        var s = Arena();
        s.Roster(0, ("Me", 6), ("Ally", 26), ("Shade", 18), ("Healer", 30));
        s.Player(0, Script.Cleric, "Healer", 30);
        var f = new Fight(s);
        f.Hit(1, Script.Me, Rotan, 10_000);
        // #39063-like entity: no 41 36, no owner, Cleric skill, a power scalar nobody else uses.
        f.Hit(2, 39_063, Rotan, 6_682, 17_150_002, power: 23_456);
        var rec = s.Record();
        Assert.DoesNotContain(rec.Combatants, c => c.EntityId == 39_063);
        Assert.DoesNotContain(rec.Combatants, c => c.Name.StartsWith("Player ", StringComparison.Ordinal));
        // Linked right away: the party's only Cleric owns it (only-of-class linking for orphan skill entities).
        Assert.Equal(6_682, Script.Combatant(rec, Script.Cleric).Damage);
        Assert.True(rec.Hits.Single(h => h.Source == 39_063).Flags.HasFlag(HitFlags.Summon));
    }

    [Fact]
    public void Unknown_actor_sharing_the_scalar_of_two_players_of_its_class_is_an_unknown_summon()
    {
        var s = Arena();
        s.Player(0, 950, "Shade2", 18); // a second Assassin
        var f = new Fight(s);
        f.Hit(1, Assassin, Rotan, 1_000, 13_010_000);
        s.Hit(1.5, 950, Rotan, 1_000, 13_020_000); // same default scalar 10170
        s.Hit(2, 4242, Rotan, 777, 13_150_002);    // unknown, Assassin skill, scalar 10170: ambiguous owner
        var rec = s.Record();
        Assert.DoesNotContain(rec.Combatants, c => c.EntityId == 4242);
        Assert.Equal(777, rec.Combatants.Single(c => c.Kind == CombatantKind.UnknownSummons).Damage);
    }

    [Fact]
    public void Player_known_only_from_damage_is_named_by_the_last_unbound_roster_member()
    {
        var s = Arena();
        s.Roster(0, ("Me", 6), ("Ally", 26), ("Shade", 18), ("Mystic", 34)); // Chanter never seen in world
        var f = new Fight(s);
        f.Hit(1, Script.Me, Rotan, 1_000);
        f.Hit(1.5, 777, Rotan, 2_000, 17_020_000, power: 15_555); // a Cleric by its skills
        s.Roster(2, ("Me", 6), ("Ally", 26), ("Shade", 18), ("Mystic", 34));
        var c = Script.Combatant(s.Record(), 777);
        Assert.Equal(CombatantKind.Player, c.Kind);
        // Class from skills (Cleric) does not match the last unbound member (Chanter) → not named.
        Assert.Equal("Player 777", c.Name);

        // A damage-only player whose class is unknown (theostone hits only) is the last unbound member.
        var s2 = Arena();
        s2.Roster(0, ("Me", 6), ("Ally", 26), ("Shade", 18), ("Mystic", 34));
        new Fight(s2).Hit(1, 778, Rotan, 2_000, 3_000_005, power: 16_666);
        Assert.Equal("Player 778", Script.Combatant(s2.Record(), 778).Name);
        s2.Roster(2, ("Me", 6), ("Ally", 26), ("Shade", 18), ("Mystic", 34)); // the roster is re-sent every few seconds
        var named = Script.Combatant(s2.Record(), 778);
        Assert.Equal("Mystic", named.Name);
        Assert.True(named.IsPartyMember);
    }

    [Fact]
    public void Player_known_only_from_damage_is_named_from_a_kill_record()
    {
        var s = Arena();
        var f = new Fight(s);
        f.Hit(1, 779, Script.Boss, 5_000, 12_010_000); // unnamed Templar
        Assert.Equal("Player 779", Script.Combatant(s.Record(), 779).Name);
        s.SpawnNpc(2, Script.Trash, FakeGameData.TrashCode, 1_000, 1_000);
        s.Hit(3, 779, Script.Trash, 1_000, 12_010_000);
        s.Kill(3.1, Script.Trash, 779, "Bulwark", 12_010_000);
        Assert.Equal("Bulwark", Script.Combatant(s.Record(), 779).Name);
    }
}
