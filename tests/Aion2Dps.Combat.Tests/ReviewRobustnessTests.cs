using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

/// <summary>Regressions for the robustness/performance review: bystander damage, trash growth, re-attribution cost.</summary>
public class ReviewRobustnessTests
{
    /// <summary>An open-world map (not an instance in <see cref="FakeGameData"/>).</summary>
    private const uint OpenWorld = 100_001;

    private static Script OpenWorldScript(EngineOptions? options = null)
    {
        var s = new Script(options);
        s.Map(0, OpenWorld);
        s.Self(0);
        s.Player(0, Script.Stranger, "Stranger", 26);
        s.SpawnNpc(0, Script.Trash, FakeGameData.TrashCode, 50_000, 50_000);
        s.SpawnNpc(0, Script.Trash2, FakeGameData.TrashCode, 50_000, 50_000);
        return s;
    }

    [Fact]
    public void Bystander_damage_in_the_open_world_does_not_start_a_trash_encounter()
    {
        var s = OpenWorldScript();
        for (double t = 1; t <= 120; t += 0.5) s.Hit(t, Script.Stranger, Script.Trash, 1_000, Script.SorSkill);
        var snap = s.Snap(120);
        Assert.NotEqual(MeterState.InCombat, snap.State);
        Assert.Null(s.Engine.GetCurrentEncounter());
    }

    [Fact]
    public void Bystander_damage_does_not_keep_our_trash_encounter_alive()
    {
        var s = OpenWorldScript();
        s.Hit(1, Script.Me, Script.Trash2, 5_000);
        s.Hit(2, Script.Me, Script.Trash2, 5_000);
        // The stranger keeps fighting next to us for minutes: its own mob is not ours, our mob only adds its damage.
        s.Hit(2.5, Script.Stranger, Script.Trash2, 700, Script.SorSkill);
        for (double t = 3; t <= 300; t += 0.5) s.Hit(t, Script.Stranger, Script.Trash, 1_000, Script.SorSkill);

        var rec = s.Record();
        Assert.Equal(EncounterOutcome.Timeout, rec.Outcome); // ended 10 s after our last hit
        Assert.True(rec.DurationSeconds < 5, $"duration {rec.DurationSeconds}");
        Assert.Equal(10_000, Script.Combatant(rec, Script.Me).Damage);
        Assert.Equal(700, Script.Combatant(rec, Script.Stranger).Damage); // in scope: our target
        Assert.DoesNotContain(rec.Targets, t => t.EntityId == Script.Trash);
    }

    [Fact]
    public void Party_members_and_instances_still_count_without_the_local_player()
    {
        // Open world with a roster: a party member starts and keeps the encounter.
        var s = OpenWorldScript();
        s.Player(0, Script.Ally, "Ally", 26);
        s.Roster(0, ("Me", 6), ("Ally", 26));
        s.Hit(1, Script.Ally, Script.Trash, 1_000, Script.SorSkill);
        for (double t = 2; t <= 30; t += 2) s.Hit(t, Script.Ally, Script.Trash, 1_000, Script.SorSkill);
        Assert.Equal(MeterState.InCombat, s.Snap(30).State);
        Assert.Equal(16_000, Script.Row(s.Snap(30), Script.Ally).Damage);

        // Inside an instance only the party is there: any friendly player counts (also without a roster).
        var inst = Script.Standard();
        inst.Player(0, Script.Stranger, "Stranger", 26);
        for (double t = 1; t <= 30; t += 2) inst.Hit(t, Script.Stranger, Script.Trash, 1_000, Script.SorSkill);
        Assert.Equal(MeterState.InCombat, inst.Snap(30).State);
    }

    [Fact]
    public void Bystander_can_still_engage_a_field_boss()
    {
        var s = OpenWorldScript();
        s.SpawnNpc(0, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Hit(1, Script.Stranger, Script.Boss, 10_000, Script.SorSkill);
        Assert.Equal(EncounterKind.Boss, s.Snap(1).EncounterKind);
    }

    [Fact]
    public void Endless_trash_is_cut_at_the_safety_limit()
    {
        var s = OpenWorldScript();
        double t = 1;
        for (; t <= 1900; t += 5) s.Hit(t, Script.Me, Script.Trash, 10);
        var rec = s.Record();
        Assert.True(rec.DurationSeconds <= CombatCore.MaxTrashDuration.TotalSeconds, $"duration {rec.DurationSeconds}");
        Assert.Equal(MeterState.InCombat, s.Snap(t).State); // a fresh encounter continues
    }

    [Fact]
    public void Partial_rebuild_after_reattribution_matches_a_full_rebuild()
    {
        static (Encounter Enc, List<Hit> Moved) Build()
        {
            var enc = new Encounter(Script.T0, EncounterKind.Boss, _ => { });
            enc.Targets[Script.Boss] = new TargetState(Script.Boss) { IsBoss = true };
            enc.Targets[Script.Trash] = new TargetState(Script.Trash);
            var moved = new List<Hit>();
            var rnd = new Random(7);
            for (int i = 0; i < 400; i++)
            {
                uint actor = (i % 4) switch { 0 => Script.Me, 1 => Script.Ally, _ => CombatEngine.UnknownSummonsEntityId };
                var kind = (i % 9) switch { 7 => HitKind.Incoming, 8 => HitKind.Heal, _ => HitKind.Outgoing };
                var h = new Hit
                {
                    Time = Script.T0.AddMilliseconds(i * 250),
                    Actor = kind == HitKind.Incoming ? Script.Boss : actor,
                    Source = actor,
                    Target = kind == HitKind.Incoming ? Script.Me : (i % 3 == 0 ? Script.Trash : Script.Boss),
                    Skill = Script.GlaSkill,
                    Amount = rnd.Next(1, 50_000),
                    Flags = i % 5 == 0 ? HitFlags.Crit : HitFlags.None,
                    Kind = kind,
                    InScope = i % 3 != 0,
                    ToBoss = i % 3 != 0,
                };
                enc.AddHit(h);
                if (kind == HitKind.Outgoing && actor == CombatEngine.UnknownSummonsEntityId && i % 8 == 2) moved.Add(h);
            }
            enc.AddHit(new Hit { Time = Script.T0, Kind = HitKind.TargetSelfHeal, Target = Script.Boss, Amount = 1234 });
            return (enc, moved);
        }

        var (partial, movedA) = Build();
        var (full, movedB) = Build();
        foreach (var h in movedA) h.Actor = Script.Cleric;
        foreach (var h in movedB) h.Actor = Script.Cleric;
        partial.RebuildLive(new HashSet<uint> { CombatEngine.UnknownSummonsEntityId, Script.Cleric });
        full.RebuildLive();

        Assert.Equal(full.Combatants.Keys.OrderBy(k => k), partial.Combatants.Keys.OrderBy(k => k));
        foreach (var (id, f) in full.Combatants)
        {
            var p = partial.Combatants[id];
            Assert.Equal((f.All.Damage, f.All.Hits, f.All.Crits, f.All.MaxHit, f.All.First, f.All.Last),
                (p.All.Damage, p.All.Hits, p.All.Crits, p.All.MaxHit, p.All.First, p.All.Last));
            Assert.Equal((f.Scoped.Damage, f.Scoped.Hits, f.BossDamage, f.Healing, f.DamageTaken),
                (p.Scoped.Damage, p.Scoped.Hits, p.BossDamage, p.Healing, p.DamageTaken));
            Assert.Equal(f.DamageByBoss.OrderBy(x => x.Key), p.DamageByBoss.OrderBy(x => x.Key));
        }
        foreach (var (id, f) in full.Targets)
            Assert.Equal((f.DamageTaken, f.SelfHealing), (partial.Targets[id].DamageTaken, partial.Targets[id].SelfHealing));
        Assert.True(partial.Combatants[Script.Cleric].All.Damage > 0);
    }
}
