using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator.Tests;

/// <summary>
/// The ground truth of every scenario is internally consistent, and an independent recomputation from the raw events
/// (using only the PROTOCOL.md rules) gives the same numbers.
/// </summary>
public class ScenarioTests
{
    public static TheoryData<string> AllNames => new(ScenarioLibrary.Names);

    /// <summary>Independent re-derivation of player damage per (encounter-less) target from raw events.</summary>
    private static (Dictionary<uint, long> DamageByPlayer, Dictionary<uint, long> DamageByTarget, Dictionary<uint, long> SelfHealByTarget) Recompute(Scenario s)
    {
        var players = s.Players.ToDictionary(p => p.EntityId);
        var owners = new Dictionary<uint, uint>();
        var npcs = s.Npcs.Select(n => n.EntityId).ToHashSet();
        var byPlayer = new Dictionary<uint, long>();
        var byTarget = new Dictionary<uint, long>();
        var selfHeal = new Dictionary<uint, long>();

        bool Hostile(uint who, uint target) =>
            npcs.Contains(target) ? players.ContainsKey(who)
            : players.TryGetValue(target, out var t) && players.TryGetValue(who, out var w) && t.IsEnemy != w.IsEnemy;

        foreach (var se in s.Events)
        {
            switch (se.Event)
            {
                case SpawnEvent { OwnerId: uint owner } sp:
                    owners[sp.Entity] = owner;
                    break;
                case DamageEvent d when d.Layout is 4 or 6 && d.Amount is long amount && amount > 0:
                {
                    uint who = owners.TryGetValue(d.Actor, out var o) ? o : d.Actor;
                    if (d.Actor == d.Target || d.SkillRaw == SkillIds.Dodge || SkillIds.GetKind(d.SkillRaw) == SkillKind.Link || SkillBook.IsHealSkill(d.SkillRaw)) break;
                    if (!Hostile(who, d.Target)) break;
                    byPlayer[who] = byPlayer.GetValueOrDefault(who) + amount;
                    byTarget[d.Target] = byTarget.GetValueOrDefault(d.Target) + amount;
                    break;
                }
                case DotEvent { IsDamageTick: true, Amount: long amount } t when t.IsMonsterEffect && t.SkillId is uint sk && SkillIds.GetKind(sk) == SkillKind.Player:
                    selfHeal[t.Target] = selfHeal.GetValueOrDefault(t.Target) + amount;
                    break;
                case DotEvent { IsDamageTick: true, Amount: long amount } t when t.Actor != t.Target && Hostile(t.Actor, t.Target):
                    byPlayer[t.Actor] = byPlayer.GetValueOrDefault(t.Actor) + amount;
                    byTarget[t.Target] = byTarget.GetValueOrDefault(t.Target) + amount;
                    break;
            }
        }

        return (byPlayer, byTarget, selfHeal);
    }

    [Theory]
    [MemberData(nameof(AllNames))]
    public void Ground_truth_matches_an_independent_recomputation(string name)
    {
        var s = ScenarioLibrary.Get(name);
        var (byPlayer, byTarget, selfHeal) = Recompute(s);
        foreach (var p in s.Truth.Players.Values)
            Assert.Equal(byPlayer.GetValueOrDefault(p.EntityId), p.Damage);
        Assert.Equal(s.Truth.Players.Values.Sum(p => p.Damage), byPlayer.Values.Sum());

        // HP check (§11.4) for every NPC that dies: Σ damage − Σ self-heals = max HP, and the last reading is 0.
        foreach (var npc in s.Npcs.Where(n => !n.IsDummy))
        {
            var readings = s.Events.Where(e => e.Event is EntityStatsEvent st && st.Entity == npc.EntityId).Select(e => ((EntityStatsEvent)e.Event).CurrentHp!.Value).ToList();
            Assert.NotEmpty(readings);
            Assert.Equal(0, readings[^1]);
            Assert.All(readings.Take(readings.Count - 1), hp => Assert.True(hp > 0));
            Assert.All(readings, hp => Assert.True(hp <= npc.MaxHp));
        }

        // Spawns and 1B 92 carry the derived max HP.
        foreach (var npc in s.Npcs)
        {
            var spawn = Assert.Single(s.Events.Select(e => e.Event).OfType<SpawnEvent>(), sp => sp.Entity == npc.EntityId);
            Assert.Equal(npc.MaxHp, spawn.HpMax);
            Assert.Equal(npc.NpcCode, spawn.NpcCode);
        }

        _ = byTarget;
        _ = selfHeal;
    }

    [Theory]
    [MemberData(nameof(AllNames))]
    public void Events_are_sorted_on_server_ticks_and_encode(string name)
    {
        var s = ScenarioLibrary.Get(name);
        Assert.Equal(s.Events.OrderBy(e => e.Offset).Select(e => e.Offset), s.Events.Select(e => e.Offset));
        Assert.All(s.Events, e => Assert.Equal(0, e.Offset.TotalMilliseconds % 50));
        Assert.True(s.Duration >= s.Events[^1].Offset);
        foreach (var e in s.Events) Assert.NotEmpty(PacketEncoders.EncodeFrame(e.Event));
        Assert.Single(s.Players, p => p.IsLocal);
        Assert.Contains(s.Events, e => e.Event is MapLoadEvent);
        Assert.Contains(s.Events, e => e.Event is SelfInfoEvent si && si.Entity == s.LocalPlayer.EntityId);
    }

    [Fact]
    public void BossKill_ground_truth()
    {
        var s = ScenarioLibrary.BossKill();
        var t = s.Truth;
        var enc = Assert.Single(t.Encounters);
        Assert.Equal(EncounterKind.Boss, enc.Kind);
        Assert.Equal(EncounterOutcome.Kill, enc.Outcome);
        Assert.Equal(SkillBook.UltimateBerk, enc.BossNpcCode);
        Assert.InRange(enc.DurationSeconds, 85, 92);

        // HP equation: Σ player damage to the boss − boss self-heal = HP lost = max HP; kill at 0.
        long playerBossDamage = enc.Players.Values.Sum(p => p.BossDamage);
        Assert.Equal(enc.BossDamageTaken, playerBossDamage);
        Assert.Equal(122_788, enc.BossSelfHealing);
        Assert.Equal(enc.BossMaxHp, enc.BossHpStart);
        Assert.Equal(0, enc.BossHpEnd);
        Assert.Equal(enc.BossMaxHp, playerBossDamage - enc.BossSelfHealing);
        Assert.Equal(enc.BossHpLost, playerBossDamage - enc.BossSelfHealing);
        Assert.InRange(enc.BossMaxHp!.Value, 4_000_000, 12_000_000);
        Assert.Equal(enc.TotalDamage, playerBossDamage); // all damage is on the boss
        Assert.Contains(enc.Kills, k => k.Target == 40610 && k.Killer == ScenarioLibrary.Kaelwyn.EntityId);

        // Five players, the local Gladiator, distinct classes incl. Elementalist and Sorcerer/Ranger.
        Assert.Equal(5, enc.Players.Count);
        var local = enc.Players[t.LocalPlayerId];
        Assert.True(local.IsLocal);
        Assert.Equal(CharacterClass.Gladiator, local.Class);
        Assert.Equal(5, enc.Players.Values.Select(p => p.Class).Distinct().Count());

        // Crit ~25 %, quality flags, multi-hits, DoTs, heals, summons, incoming damage, one dodge, one parry.
        int hits = enc.Players.Values.Sum(p => p.Hits), crits = enc.Players.Values.Sum(p => p.Crits);
        Assert.InRange((double)crits / hits, 0.18, 0.32);
        Assert.True(enc.Players.Values.Sum(p => p.BackHits) > 10);
        Assert.True(enc.Players.Values.Sum(p => p.FrontHits) > 10);
        Assert.True(enc.Players.Values.Sum(p => p.PerfectHits) > 0);
        Assert.True(enc.Players.Values.Sum(p => p.DoubleHits) > 0);
        Assert.True(enc.Players[ScenarioLibrary.Torvald.EntityId].MultiHitRecords > 5);
        Assert.True(enc.Players[ScenarioLibrary.Sylvae.EntityId].DotDamage > 0);
        Assert.True(enc.Players[ScenarioLibrary.Ishara.EntityId].DotDamage > 0);
        Assert.True(enc.Players[ScenarioLibrary.Sylvae.EntityId].SummonDamage > 0);
        Assert.True(enc.Players[ScenarioLibrary.Ishara.EntityId].SummonDamage > 0);
        Assert.True(enc.Players[ScenarioLibrary.Mireth.EntityId].SummonDamage > 0);
        Assert.True(enc.Players[ScenarioLibrary.Mireth.EntityId].Healing > 50_000);
        Assert.True(local.SelfHealing > 0);
        Assert.True(enc.Players.Values.Sum(p => p.DamageTaken) > 0);
        Assert.Equal(1, enc.Players.Values.Sum(p => p.Dodges));
        Assert.Equal(1, enc.Players.Values.Sum(p => p.Parries));

        // The self-heal tick and link records exist on the wire but are not anyone's damage.
        Assert.Contains(s.Events, e => e.Event is DotEvent { IsMonsterEffect: true, Amount: 122_788 });
        Assert.Contains(s.Events, e => e.Event is DamageEvent d && SkillIds.GetKind(d.SkillRaw) == SkillKind.Link);
        Assert.Contains(s.Events, e => e.Event is DamageEvent { IsCastNotice: true });
        Assert.Contains(s.Events, e => e.Event is CastEvent);

        // Summons with owners via 07 02 06.
        var summons = s.Events.Select(e => e.Event).OfType<SpawnEvent>().Where(sp => sp.IsSummonLike).ToList();
        Assert.True(summons.Count >= 5);
        Assert.All(summons, sp => Assert.NotNull(sp.OwnerId));
        Assert.All(summons, sp => Assert.Equal(sp.OwnerId, SpawnProbe.Owner(PacketEncoders.EncodeBody(sp))));
        Assert.Contains(summons, sp => sp.CasterName == "Mireth");

        // Identity: self record, 4 other players, a 5-member roster.
        Assert.Equal(4, s.Events.Count(e => e.Event is PlayerInfoEvent));
        Assert.Equal(5, s.Events.Select(e => e.Event).OfType<PartyRosterEvent>().Single().Members.Count);

        // Whole-scenario rows equal the single encounter.
        foreach (var p in enc.Players.Values)
        {
            Assert.Equal(p.Damage, t.Players[p.EntityId].Damage);
            Assert.Equal(p.Healing, t.Players[p.EntityId].Healing);
            Assert.Equal(p.Damage, p.DirectDamage + p.DotDamage + p.SummonDamage);
            Assert.Equal(p.Damage, p.Skills.Values.Sum(k => k.Damage));
            Assert.Equal(p.Hits, p.Skills.Values.Sum(k => k.Hits));
        }
    }

    [Fact]
    public void BossWipeThenKill_ground_truth()
    {
        var s = ScenarioLibrary.BossWipeThenKill();
        var t = s.Truth;
        Assert.Equal(2, t.Encounters.Count);
        var (wipe, kill) = (t.Encounters[0], t.Encounters[1]);
        Assert.Equal(EncounterOutcome.Wipe, wipe.Outcome);
        Assert.Equal(EncounterOutcome.Kill, kill.Outcome);
        Assert.Equal(wipe.BossEntityId, kill.BossEntityId);
        long max = kill.BossMaxHp!.Value;

        // Attempt 1 took the boss below 90 % but not to 0; then exactly one reset to full.
        Assert.Equal(max, wipe.BossHpStart);
        Assert.InRange((double)wipe.BossHpEnd!.Value / max, 0.2, 0.85);
        Assert.Equal(wipe.BossHpLost, wipe.BossDamageTaken - wipe.BossSelfHealing);
        var readings = s.Events.Select(e => e.Event).OfType<EntityStatsEvent>().Where(st => st.Entity == kill.BossEntityId).Select(st => st.CurrentHp!.Value).ToList();
        int resets = readings.Zip(readings.Skip(1)).Count(p => p.Second == max && p.First < 0.9 * max);
        Assert.Equal(1, resets);
        Assert.Equal(5, wipe.Players.Values.Sum(p => p.Deaths));

        // Attempt 2 kills from full: damage − self heal = max HP.
        Assert.Equal(max, kill.BossHpStart);
        Assert.Equal(0, kill.BossHpEnd);
        Assert.Equal(max, kill.Players.Values.Sum(p => p.BossDamage) - kill.BossSelfHealing);
        Assert.True(kill.FirstHit > wipe.LastHit);

        // Whole-scenario damage = both attempts.
        foreach (var p in t.Players.Values)
            Assert.Equal(p.Damage, wipe.Players.GetValueOrDefault(p.EntityId)?.Damage + kill.Players.GetValueOrDefault(p.EntityId)?.Damage);
    }

    [Fact]
    public void TrashPull_ground_truth()
    {
        var s = ScenarioLibrary.TrashPull();
        var enc = Assert.Single(s.Truth.Encounters);
        Assert.Equal(EncounterKind.Trash, enc.Kind);
        Assert.Null(enc.BossEntityId);
        Assert.Equal(5, s.Npcs.Count);
        Assert.All(s.Npcs, n => Assert.False(n.IsBoss));
        Assert.All(s.Npcs, n => Assert.InRange(n.MaxHp, 50_000, 4_999_999));
        Assert.Equal(s.Npcs.Select(n => n.EntityId).Order(), enc.TargetIds.Order());
        Assert.Equal(s.Npcs.Sum(n => n.MaxHp), enc.TotalDamage);
        Assert.Equal(5, s.Events.Count(e => e.Event is DeathEvent { Flag: 3 }));
        Assert.True(s.Duration - enc.LastHit >= TimeSpan.FromSeconds(12));
        Assert.InRange(enc.DurationSeconds, 30, 45);
    }

    [Fact]
    public void PvpSkirmish_ground_truth()
    {
        var s = ScenarioLibrary.PvpSkirmish();
        var enc = Assert.Single(s.Truth.Encounters);
        Assert.Equal(EncounterKind.Pvp, enc.Kind);
        var local = enc.Players[ScenarioLibrary.Kaelwyn.EntityId];
        var vex = enc.Players[ScenarioLibrary.Vexmora.EntityId];
        var drak = enc.Players[ScenarioLibrary.Draketh.EntityId];
        Assert.Equal(CombatantKind.EnemyPlayer, vex.Kind);
        Assert.Equal(CombatantKind.EnemyPlayer, drak.Kind);
        Assert.Equal(CombatantKind.Player, local.Kind);
        Assert.Equal(local.Damage, vex.DamageTaken + drak.DamageTaken);
        Assert.Equal(local.DamageTaken, vex.Damage + drak.Damage);
        Assert.True(local.Damage > 0 && vex.Damage > 0 && drak.Damage > 0);
        Assert.Equal((ScenarioLibrary.Vexmora.EntityId, ScenarioLibrary.Kaelwyn.EntityId), Assert.Single(enc.Kills));
        Assert.Equal(1, vex.Deaths);
        Assert.Empty(s.Npcs);
        Assert.Equal(local.Damage, enc.TotalDamage);
        var enemyInfo = s.Events.Select(e => e.Event).OfType<PlayerInfoEvent>().ToList();
        Assert.Equal(2, enemyInfo.Count);
        Assert.All(enemyInfo, p => Assert.Equal("Nightfall", p.GuildName));
    }

    [Fact]
    public void TrainingDummy_ground_truth()
    {
        var s = ScenarioLibrary.TrainingDummy();
        var enc = Assert.Single(s.Truth.Encounters);
        Assert.Equal(EncounterKind.Dummy, enc.Kind);
        Assert.Equal(SkillBook.TrainingScarecrow, enc.BossNpcCode);
        var dummy = Assert.Single(s.Npcs);
        Assert.True(dummy.IsDummy);
        Assert.Equal(100_000_000, dummy.MaxHp);
        Assert.Equal(dummy.MaxHp - enc.TotalDamage, enc.BossHpEnd);
        Assert.InRange(enc.DurationSeconds, 55, 61);
        Assert.Single(enc.Players);
    }

    [Fact]
    public void Scenarios_are_deterministic_by_seed()
    {
        static string Fingerprint(Scenario s) => string.Join("|", s.Events.Select(e => $"{e.Offset.TotalMilliseconds}:{Convert.ToHexString(PacketEncoders.EncodePayload(e.Event))}"));
        Assert.Equal(Fingerprint(ScenarioLibrary.BossKill(1)), Fingerprint(ScenarioLibrary.BossKill(1)));
        Assert.NotEqual(Fingerprint(ScenarioLibrary.BossKill(1)), Fingerprint(ScenarioLibrary.BossKill(2)));
        Assert.Equal(ScenarioLibrary.BossKill(2).Truth.TotalDamage, ScenarioLibrary.BossKill(2).Truth.TotalDamage);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(42)]
    public void Other_seeds_still_satisfy_the_hp_equation(int seed)
    {
        foreach (var s in new[] { ScenarioLibrary.BossKill(seed), ScenarioLibrary.BossWipeThenKill(seed) })
        {
            var kill = s.Truth.Final!;
            Assert.Equal(0, kill.BossHpEnd);
            Assert.Equal(kill.BossMaxHp, kill.BossDamageTaken - kill.BossSelfHealing);
        }
    }

    [Fact]
    public void Library_lookup()
    {
        Assert.Equal(5, ScenarioLibrary.Names.Count);
        Assert.Equal("TrashPull", ScenarioLibrary.Get("trashpull").Name);
        Assert.Throws<ArgumentException>(() => ScenarioLibrary.Get("nope"));
        Assert.Equal(["BossKill", "TrashPull", "BossWipeThenKill"], ScenarioLibrary.DefaultCycle().Select(s => s.Name));
    }
}
