using Aion2Dps.Contracts;
using Aion2Dps.Simulator;
using Xunit.Abstractions;
using static Aion2Dps.Simulator.SkillBook;

namespace Aion2Dps.EndToEnd.Tests;

/// <summary>
/// A Fire Temple–style split pull built with the simulator's <see cref="ScenarioBuilder"/>: the party splits on two bosses
/// at the same time (Rotan by the Gladiators + Cleric, Murute by the Assassin + Elementalist), the bosses spawn with
/// their base max HP and are rescaled for the party size (stat kind 7), a third boss (Ignus) spawns but is never fought,
/// a Cleric skill entity with no spawn record hits Rotan, and Kromede's Desire follows after a pause. The wire stream
/// (LZ4 bundles, random chunking) goes through the real ProtocolPipeline → CombatEngine.
/// </summary>
public sealed class MultiBossScenarioEndToEndTests
{
    private const uint Map = 600021;
    private const uint RotanCode = 2310310, IgnusCode = 2310311, MuruteCode = 2310312, KromedeCode = 2310371;
    private const uint Rotan = 38364, Ignus = 27348, Murute = 27820, Kromede = 36916;
    private const uint ClericServant = 39063; // skill entity whose 41 36 never arrives
    private const uint ServantSkill = 17150002;

    private static readonly DateTime Start = new(2026, 10, 6, 19, 42, 0, DateTimeKind.Utc);

    private static readonly SimPlayer Local = new()
    {
        EntityId = 4101, Name = "Nightwisp", Class = CharacterClass.Assassin, IsLocal = true, IsPartyMember = true,
        PowerScalar = 14100, CharacterId = 400001,
    };
    private static readonly SimPlayer GladA = new() { EntityId = 4102, Name = "Ironvow", Class = CharacterClass.Gladiator, IsPartyMember = true, PowerScalar = 15300, CharacterId = 400002 };
    private static readonly SimPlayer GladB = new() { EntityId = 4103, Name = "Stoneclad", Class = CharacterClass.Gladiator, IsPartyMember = true, PowerScalar = 14900, CharacterId = 400003 };
    private static readonly SimPlayer Cleric = new() { EntityId = 4104, Name = "Lumenra", Class = CharacterClass.Cleric, IsPartyMember = true, PowerScalar = 11700, CharacterId = 400004 };
    private static readonly SimPlayer Ele = new() { EntityId = 4105, Name = "Emberly", Class = CharacterClass.Elementalist, IsPartyMember = true, PowerScalar = 13600, CharacterId = 400005 };
    private static readonly SimPlayer[] Party = [Local, GladA, GladB, Cleric, Ele];

    private readonly ITestOutputHelper _out;

    public MultiBossScenarioEndToEndTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Builds the scenario. Max HP is derived from the damage dealt, so the first pass (no spawns) learns it and the second
    /// pass adds the spawns with the base max HP plus the party-size rescale records (the builder's RNG is not consumed
    /// by those raw events, so the damage is identical).
    /// </summary>
    private static Scenario Build(int seed)
    {
        var first = Script(seed, null);
        var max = first.Npcs.ToDictionary(n => n.EntityId, n => n.MaxHp);
        return Script(seed, max);
    }

    private static Scenario Script(int seed, IReadOnlyDictionary<uint, long>? max)
    {
        var b = new ScenarioBuilder("MultiBossSplit", seed, "Two bosses at once, then a third one");
        var r = b.Rng;
        foreach (var p in Party) b.AddPlayer(p);
        b.AddNpc(Rotan, RotanCode, isBoss: true);
        b.AddNpc(Murute, MuruteCode, isBoss: true);
        b.AddNpc(Ignus, IgnusCode, isBoss: true, fixedMaxHp: 900_000, trackHp: false);
        b.AddNpc(Kromede, KromedeCode, isBoss: true);
        b.AddSummon(ClericServant, Cleric.EntityId);

        b.MapLoad(0, Map);
        b.SelfInfo(0.1, Local);
        foreach (var p in Party.Skip(1)) b.PlayerAppears(0.2, p);
        b.Roster(0.3, 77, Map, Party);

        if (max != null)
        {
            // Spawns carry the solo/base max HP; stat kind 7 then rescales them for 2..5 players and HP follows.
            foreach (var (id, code, final) in new[] { (Rotan, RotanCode, max[Rotan]), (Ignus, IgnusCode, 900_000L), (Murute, MuruteCode, max[Murute]) })
            {
                long baseMax = final / 5;
                b.Add(1, new SpawnEvent
                {
                    Entity = id, KindByte = 0x0C, KindFlags = 0x22, NpcCode = code, X = 100, Y = 200, Z = 30,
                    HpCurrent = baseMax, HpMax = baseMax, AnchorId = id,
                });
                for (int n = 2; n <= 5; n++)
                {
                    long scaled = n == 5 ? final : baseMax * n;
                    b.Add(1 + 0.2 * n, new EntityStatsEvent { Entity = id, Format = 0x02, Stats64 = new Dictionary<byte, long> { [7] = scaled } });
                    b.Add(1.05 + 0.2 * n, new EntityStatsEvent { Entity = id, Format = 0x02, CurrentHp = scaled });
                }
            }
            b.Add(1, new SpawnEvent
            {
                Entity = Kromede, KindByte = 0x0C, KindFlags = 0x22, NpcCode = KromedeCode, X = 400, Y = 200, Z = 30,
                HpCurrent = max[Kromede], HpMax = max[Kromede], AnchorId = Kromede,
            });
        }

        // ── encounter 0: Rotan (Gladiators + Cleric from t=5) and Murute (Assassin + Elementalist from t=30) at once ──
        b.BeginEncounter(EncounterKind.Boss, EncounterOutcome.Kill, Murute);
        double rotanEnd = 60, muruteEnd = 76;
        Attack(b, GladA, Rotan, 5, rotanEnd, [KeenStrike, RuptureStrike], 9000);
        Attack(b, GladB, Rotan, 5.3, rotanEnd, [KeenStrike, WrathfulStrike], 8500);
        Attack(b, Cleric, Rotan, 5.6, rotanEnd, [ThunderAndLightning, JudgmentThunder], 6000);
        for (double t = 20; t < 50; t += 6.1) b.Hit(t, ClericServant, Rotan, ServantSkill, 1100 + r.Next(300)); // the orphan skill entity
        b.Hit(rotanEnd, GladA.EntityId, Rotan, KeenStrike, 7000);
        b.Kill(rotanEnd + 0.05, Rotan, GladA.EntityId, KeenStrike);

        Attack(b, Local, Murute, 30, muruteEnd, [QuickSlice, ThrowShadowblade, BreakingSlice], 7000);
        Attack(b, Ele, Murute, 30.4, muruteEnd, [ColdShock, VacuumExplosion], 8000);
        Attack(b, GladA, Murute, 62, muruteEnd, [KeenStrike], 9000);
        Attack(b, GladB, Murute, 62.3, muruteEnd, [WrathfulStrike], 8500);
        Attack(b, Cleric, Murute, 62.6, muruteEnd, [JudgmentThunder], 6000);
        b.Hit(muruteEnd, Local.EntityId, Murute, QuickSlice, 6500);
        b.Kill(muruteEnd + 0.05, Murute, Local.EntityId, QuickSlice);
        b.EndEncounter();

        // ── encounter 1: Kromede's Desire, after a pause ──
        b.BeginEncounter(EncounterKind.Boss, EncounterOutcome.Kill, Kromede);
        foreach (var (p, skills, dmg, offset) in new (SimPlayer, uint[], int, double)[]
                 {
                     (Local, [QuickSlice, BreakingSlice], 7000, 0), (GladA, [KeenStrike], 9000, 0.2), (GladB, [WrathfulStrike], 8500, 0.4),
                     (Cleric, [ThunderAndLightning], 6000, 0.6), (Ele, [Combustion], 9500, 0.8),
                 })
            Attack(b, p, Kromede, 130 + offset, 190, skills, dmg);
        b.Hit(190, Ele.EntityId, Kromede, Combustion, 9000);
        b.Kill(190.05, Kromede, Ele.EntityId, Combustion);
        b.EndEncounter();
        return b.Build(idleTail: 45);
    }

    private static void Attack(ScenarioBuilder b, SimPlayer p, uint target, double t0, double t1, uint[] skills, int baseDamage)
    {
        var r = b.Rng;
        for (double t = t0; t < t1 - 0.2; t += 1.1 + r.NextDouble() * 0.5)
        {
            bool crit = r.NextDouble() < 0.3;
            long amount = (long)(baseDamage * (0.85 + r.NextDouble() * 0.3) * (crit ? 1.7 : 1.0));
            b.Hit(t, p.EntityId, target, skills[r.Next(skills.Length)], amount, new HitStyle { Layout = 6, Crit = crit, Direction = HitDirection.Back });
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Split_pull_is_one_multi_boss_encounter_and_the_next_boss_is_separate(int seed)
    {
        var scenario = Build(seed);
        var stream = new StreamGenerator(new StreamGeneratorOptions { Seed = seed * 13 + 1 }).Generate(scenario, Start);
        var h = new Harness();
        h.FeedRechunked(stream, seed);

        Assert.Equal(0, h.Pipeline.Diagnostics.DecodeErrors);
        foreach (var rec in h.Records)
        {
            _out.WriteLine($"{rec.Kind} {rec.Outcome} {rec.DurationSeconds:0.0}s bosses {string.Join(" + ", rec.Bosses.Select(x => Harness.GameData.GetNpcName(x.NpcCode ?? 0)))} " +
                           $"overall {rec.OverallHpCheck?.Ratio:0.0000} total {rec.TotalDamage:N0}");
            foreach (var boss in rec.Bosses) _out.WriteLine($"  {boss.NpcCode} max {boss.MaxHp:N0} killed {boss.Killed} check {boss.HpCheck?.Ratio:0.0000} {boss.HpCheck?.Note}");
        }

        Assert.Equal(2, h.Records.Count);
        var multi = h.Records[0];
        var kromede = h.Records[1];

        // Expected numbers straight from the script's ground-truth tags.
        var damage = scenario.Events.Where(e => e.Truth is { Kind: TruthKind.Damage }).Select(e => (e.EncounterIndex, e.Truth!)).ToList();
        long Expected(int enc, uint player) => damage.Where(x => x.EncounterIndex == enc && x.Item2.Credited == player).Sum(x => x.Item2.Amount);
        long ExpectedOn(uint boss, uint player) => damage.Where(x => x.Item2.Target == boss && x.Item2.Credited == player).Sum(x => x.Item2.Amount);
        long maxRotan = scenario.Npcs.Single(n => n.EntityId == Rotan).MaxHp;
        long maxMurute = scenario.Npcs.Single(n => n.EntityId == Murute).MaxHp;

        // ── the split pull ──
        Assert.Equal(EncounterKind.Boss, multi.Kind);
        Assert.Equal(EncounterOutcome.Kill, multi.Outcome);
        Assert.Equal(new uint?[] { RotanCode, MuruteCode }, multi.Bosses.Select(x => x.NpcCode));
        Assert.DoesNotContain(multi.Bosses, x => x.NpcCode == IgnusCode);
        Assert.Equal(maxMurute > maxRotan ? MuruteCode : RotanCode, multi.BossNpcCode);
        Assert.Equal(maxRotan, multi.Bosses[0].MaxHp);
        Assert.Equal(maxMurute, multi.Bosses[1].MaxHp);
        Assert.All(multi.Bosses, x => Assert.True(x.Killed));
        Assert.All(multi.Bosses, x => Assert.True(x.MaxHpTrusted));
        Assert.All(multi.Bosses, x => Assert.Equal(0, x.Resets)); // the party-size rescale is not a wipe
        Assert.All(multi.Bosses, x => Assert.InRange(x.HpCheck!.Ratio, 0.999, 1.001));
        Assert.InRange(multi.OverallHpCheck!.Ratio, 0.999, 1.001);
        Assert.Equal(maxRotan + maxMurute, multi.TotalDamage);

        foreach (var p in Party)
        {
            var c = multi.Combatants.Single(x => x.EntityId == p.EntityId);
            Assert.Equal(p.Name, c.Name);
            Assert.Equal(Expected(0, p.EntityId), c.Damage);
            Assert.Equal(c.Damage, c.AllBossesDamage);
            Assert.Equal((double)c.AllBossesDamage / (maxRotan + maxMurute), c.Contribution, 9);
            Assert.Equal(ExpectedOn(Rotan, p.EntityId), multi.Bosses[0].DamageByCombatant.SingleOrDefault(x => x.EntityId == p.EntityId)?.Damage ?? 0);
            Assert.Equal(ExpectedOn(Murute, p.EntityId), multi.Bosses[1].DamageByCombatant.SingleOrDefault(x => x.EntityId == p.EntityId)?.Damage ?? 0);
        }
        // The servant's hits went to the Cleric (power-scalar link), never to a player row of their own.
        Assert.DoesNotContain(multi.Combatants, x => x.EntityId == ClericServant || x.Kind == CombatantKind.UnknownSummons);
        Assert.Contains(multi.Hits, x => x.Source == ClericServant && x.Actor == Cleric.EntityId);
        Assert.Equal(1.0, multi.Combatants.Sum(x => x.Contribution), 9);
        // The local Assassin fought Murute: a fair contribution, not "only what it did to Rotan".
        var me = multi.Combatants.Single(x => x.IsLocal);
        Assert.True(me.Contribution > 0.1, $"local contribution {me.Contribution:P1}");

        // The boss list survives the on-disk encoding (additive schema: old records simply have no bosses).
        var stored = Aion2Dps.Storage.EncounterRecordSerializer.FromJson(Aion2Dps.Storage.EncounterRecordSerializer.ToJson(multi))!;
        Assert.Equal(multi.Bosses.Select(x => (x.NpcCode, x.MaxHp, x.Killed, x.HpTimeline.Count, x.DamageByCombatant.Count)),
            stored.Bosses.Select(x => (x.NpcCode, x.MaxHp, x.Killed, x.HpTimeline.Count, x.DamageByCombatant.Count)));
        Assert.Equal(multi.OverallHpCheck!.Ratio, stored.OverallHpCheck!.Ratio);
        Assert.Equal(multi.Combatants.Select(x => x.AllBossesDamage), stored.Combatants.Select(x => x.AllBossesDamage));

        // ── the next boss is its own encounter ──
        Assert.Equal(EncounterOutcome.Kill, kromede.Outcome);
        Assert.Equal(KromedeCode, kromede.BossNpcCode);
        Assert.Equal(KromedeCode, Assert.Single(kromede.Bosses).NpcCode);
        Assert.InRange(kromede.HpCheck!.Ratio, 0.999, 1.001);
        foreach (var p in Party) Assert.Equal(Expected(1, p.EntityId), kromede.Combatants.Single(x => x.EntityId == p.EntityId).Damage);
    }
}
