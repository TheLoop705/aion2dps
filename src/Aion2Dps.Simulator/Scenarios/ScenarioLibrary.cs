using Aion2Dps.Contracts;
using static Aion2Dps.Simulator.SkillBook;

namespace Aion2Dps.Simulator;

/// <summary>
/// Ready-made scenarios with exact ground truth. All are deterministic for a given seed.
/// <list type="bullet">
/// <item><see cref="BossKill"/>: local Gladiator + Cleric, Elementalist (2 spirits), Ranger, Sorcerer (winds) vs Ultimate Berk, ~90 s, kill.</item>
/// <item><see cref="BossWipeThenKill"/>: Enhanced Harcon, a wipe at ~40 % (boss resets to full), then a kill.</item>
/// <item><see cref="TrashPull"/>: 3 players clear 5 non-boss mobs; ends by idle timeout.</item>
/// <item><see cref="PvpSkirmish"/>: the local player trades damage with two enemy players and kills one.</item>
/// <item><see cref="TrainingDummy"/>: the local player hits a Training Scarecrow for 60 s.</item>
/// </list>
/// </summary>
public static class ScenarioLibrary
{
    /// <summary>Server clock origin used for buff expiry times.</summary>
    public static readonly DateTime ServerEpoch = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<string> Names { get; } = ["BossKill", "BossWipeThenKill", "TrashPull", "PvpSkirmish", "TrainingDummy"];

    /// <summary>Scenario by name (case-insensitive).</summary>
    public static Scenario Get(string name, int seed = 1) => name.ToLowerInvariant() switch
    {
        "bosskill" => BossKill(seed),
        "bosswipethenkill" => BossWipeThenKill(seed),
        "trashpull" => TrashPull(seed),
        "pvpskirmish" => PvpSkirmish(seed),
        "trainingdummy" => TrainingDummy(seed),
        _ => throw new ArgumentException($"Unknown scenario '{name}'. Known: {string.Join(", ", Names)}", nameof(name)),
    };

    /// <summary>The demo cycle: BossKill, TrashPull, BossWipeThenKill.</summary>
    public static IReadOnlyList<Scenario> DefaultCycle(int seed = 1) => [BossKill(seed), TrashPull(seed), BossWipeThenKill(seed)];

    public static IReadOnlyList<Scenario> All(int seed = 1) => Names.Select(n => Get(n, seed)).ToList();

    // ───────────────────────────── cast ─────────────────────────────

    public static readonly SimPlayer Kaelwyn = new()
    {
        EntityId = 2210, Name = "Kaelwyn", Class = CharacterClass.Gladiator, IsLocal = true, IsPartyMember = true, PowerScalar = 14500,
        CharacterId = 210034, Level = 45, GearScore = 2480, CombatPower = 61250, GuildName = "Dawnward",
    };

    public static readonly SimPlayer Mireth = new()
    {
        EntityId = 3471, Name = "Mireth", Class = CharacterClass.Cleric, IsPartyMember = true, PowerScalar = 11200,
        CharacterId = 211877, Level = 45, GearScore = 2390, CombatPower = 55410, GuildName = "Dawnward",
    };

    public static readonly SimPlayer Sylvae = new()
    {
        EntityId = 5102, Name = "Sylvae", Class = CharacterClass.Elementalist, IsPartyMember = true, PowerScalar = 13800,
        CharacterId = 214502, Level = 45, GearScore = 2455, CombatPower = 59880,
    };

    public static readonly SimPlayer Torvald = new()
    {
        EntityId = 6688, Name = "Torvald", Class = CharacterClass.Ranger, IsPartyMember = true, PowerScalar = 15200,
        CharacterId = 219341, Level = 45, GearScore = 2510, CombatPower = 63020,
    };

    public static readonly SimPlayer Ishara = new()
    {
        EntityId = 9035, Name = "Ishara", Class = CharacterClass.Sorcerer, IsPartyMember = true, PowerScalar = 12810,
        CharacterId = 220986, Level = 45, GearScore = 2430, CombatPower = 58770,
    };

    public static readonly SimPlayer Vexmora = new()
    {
        EntityId = 12001, Name = "Vexmora", Class = CharacterClass.Assassin, IsEnemy = true, PowerScalar = 13300, Faction = 1,
        CharacterId = 330101, ServerId = 2305, GuildName = "Nightfall",
    };

    public static readonly SimPlayer Draketh = new()
    {
        EntityId = 12044, Name = "Draketh", Class = CharacterClass.Templar, IsEnemy = true, PowerScalar = 11900, Faction = 1,
        CharacterId = 330552, ServerId = 2305, GuildName = "Nightfall",
    };

    private sealed record Move(uint Skill, int Base, int Weight = 1, int Extras = 0, bool Plain = false);

    private static readonly Move[] GladiatorMoves =
    [
        new(KeenStrike, 10500, 3), new(RuptureStrike, 13500, 2), new(WrathfulStrike, 16500, 2),
        new(RuinousBlow, 24000, 1, Extras: 2), new(CrushingWave, 14500, 1, Plain: true),
    ];

    private static readonly Move[] ClericMoves = [new(EarthsRetribution, 7000, 3), new(ThunderAndLightning, 9500, 2), new(JudgmentThunder, 12000, 1)];

    private static readonly Move[] ElementalistMoves = [new(ColdShock, 10000, 3), new(VacuumExplosion, 13000, 2), new(Combustion, 15500, 2, Extras: 2)];

    private static readonly Move[] RangerMoves =
    [
        new(Snipe, 15000, 3), new(RapidFire, 11000, 2, Extras: 3), new(SpiralArrow, 13500, 2), new(Deadshot, 28000, 1, Extras: 4),
    ];

    private static readonly Move[] SorcererMoves = [new(FlameScattershot, 12500, 3), new(Burst, 17000, 2), new(Firestorm, 15000, 2), new(Firebomb, 9000, 1)];

    private static readonly Move[] AssassinMoves = [new(QuickSlice, 3200, 3), new(ThrowShadowblade, 3800, 2), new(BreakingSlice, 4600, 1)];

    private static readonly Move[] TemplarMoves = [new(ViciousStrike, 2600, 1)];

    // ───────────────────────────── helpers ─────────────────────────────

    private static double U(Random r, double a, double b) => a + r.NextDouble() * (b - a);

    private static IReadOnlyList<uint> Extras(long amount, int n)
    {
        if (n <= 0) return Array.Empty<uint>();
        uint each = (uint)Math.Max(1, amount / 10);
        return Enumerable.Repeat(each, n).ToArray();
    }

    /// <summary>One player's rotation on a target between t0 and t1. Returns the time after the last cast.</summary>
    private static double Rotation(ScenarioBuilder b, SimPlayer p, uint target, double t0, double t1, Move[] moves,
        double gcdMin = 1.1, double gcdMax = 1.6, double noticeChance = 0.5, double damageScale = 1.0, Action<double, Move>? afterHit = null)
    {
        var r = b.Rng;
        int totalWeight = moves.Sum(m => m.Weight);
        double t = t0 + U(r, 0, 0.8);
        while (t < t1)
        {
            int pick = r.Next(totalWeight);
            var move = moves.First(m => (pick -= m.Weight) < 0);
            if (r.NextDouble() < noticeChance) b.CastNotice(t, p.EntityId, r.NextDouble() < 0.5 ? p.EntityId : target, move.Skill);
            HitWithStyle(b, t, p.EntityId, target, move, damageScale);
            afterHit?.Invoke(t, move);
            t += U(r, gcdMin, gcdMax);
        }

        return t;
    }

    private static void HitWithStyle(ScenarioBuilder b, double t, uint actor, uint target, Move move, double scale, bool forceCrit = false)
    {
        var r = b.Rng;
        bool crit = forceCrit || r.NextDouble() < 0.25;
        long amount = (long)(move.Base * scale * U(r, 0.85, 1.15) * (crit ? U(r, 1.5, 1.9) : 1.0));
        byte layout = move.Plain || r.NextDouble() < 0.25 ? (byte)4 : (byte)6;
        var dir = HitDirection.None;
        var mods = HitMods.None;
        bool sw10 = false;
        if (layout == 6)
        {
            double d = r.NextDouble();
            dir = d < 0.3 ? HitDirection.Back : d < 0.8 ? HitDirection.Front : HitDirection.None;
            if (r.NextDouble() < 0.08) mods |= HitMods.Perfect;
            if (r.NextDouble() < 0.06) mods |= HitMods.Double;
            sw10 = r.NextDouble() < 0.15;
        }
        else
        {
            sw10 = r.NextDouble() < 0.1;
        }

        int n = move.Extras > 0 ? move.Extras : r.NextDouble() < 0.08 ? r.Next(1, 3) : 0;
        b.Hit(t, actor, target, move.Skill, amount, new HitStyle
        {
            Layout = layout, Crit = crit, Mods = mods, Direction = dir, Switch10 = sw10, ExtraHits = Extras(amount, n),
        });
    }

    private static void SpiritAttacks(ScenarioBuilder b, uint spirit, uint target, uint skill, int baseDamage, double t0, double t1)
    {
        var r = b.Rng;
        for (double t = t0; t < t1; t += U(r, 1.6, 2.2))
        {
            bool crit = r.NextDouble() < 0.25;
            long amount = (long)(baseDamage * U(r, 0.85, 1.15) * (crit ? 1.7 : 1.0));
            int n = r.NextDouble() < 0.3 ? r.Next(1, 4) : 0;
            b.Hit(t, spirit, target, skill, amount, new HitStyle { Layout = 4, Crit = crit, ExtraHits = Extras(amount, n) });
        }
    }

    private static void DotSeries(ScenarioBuilder b, uint actor, uint target, uint skill, double t0, int ticks, double interval, int baseTick, double until, uint stack)
    {
        for (int i = 1; i <= ticks; i++)
        {
            double t = t0 + i * interval;
            if (t >= until) break;
            b.DotTick(t, actor, target, skill, (long)(baseTick * U(b.Rng, 0.9, 1.1)), stack);
        }
    }

    private static void Hot(ScenarioBuilder b, uint healer, uint target, uint skill, long total, double t0, uint stack)
    {
        b.HotAnnounce(t0, healer, target, skill, total, stack);
        long per = total / 4;
        long remaining = total;
        for (int i = 1; i <= 4; i++)
        {
            remaining -= per;
            b.HotTick(t0 + 2 * i, healer, target, skill, per, remaining, stack);
        }
    }

    private static void BossAttacks(ScenarioBuilder b, uint boss, IReadOnlyList<SimPlayer> players, double t0, double t1, int baseDamage,
        double? dodgeAt = null, double? parryAt = null)
    {
        var r = b.Rng;
        uint[] skills = [NpcAttack, NpcCleave, NpcSlam];
        for (double t = t0; t < t1; t += U(r, 2.2, 3.0))
        {
            if (dodgeAt is double da && Math.Abs(t - da) < 1.5) continue;
            if (parryAt is double pa && Math.Abs(t - pa) < 1.5) continue;
            var victim = r.NextDouble() < 0.5 ? players[0] : players[r.Next(players.Count)];
            bool crit = r.NextDouble() < 0.15;
            long amount = (long)(baseDamage * U(r, 0.6, 1.4) * (crit ? 1.5 : 1.0));
            b.Hit(t, boss, victim.EntityId, skills[r.Next(skills.Length)], amount, new HitStyle
            {
                Layout = 6, Crit = crit, Direction = r.NextDouble() < 0.7 ? HitDirection.Front : HitDirection.None,
            });
        }

        if (dodgeAt is double dt) b.Dodge(dt, boss, players[0].EntityId);
        if (parryAt is double pt)
            b.Hit(pt, boss, players[0].EntityId, NpcCleave, baseDamage / 3, new HitStyle { Layout = 6, Mods = HitMods.Parry, Direction = HitDirection.None });
    }

    private static void Heals(ScenarioBuilder b, SimPlayer cleric, IReadOnlyList<SimPlayer> party, double t0, double t1)
    {
        var r = b.Rng;
        for (double t = t0; t < t1; t += U(r, 2.5, 3.5))
        {
            var target = party[r.Next(party.Count)];
            bool crit = r.NextDouble() < 0.2;
            b.Heal(t, cleric.EntityId, target.EntityId, HealingLight, (long)(U(r, 4000, 9000) * (crit ? 1.5 : 1.0)), crit);
        }
    }

    private static void Intro(ScenarioBuilder b, uint mapId, SimPlayer local, IReadOnlyList<SimPlayer> others, uint? dungeon, bool roster = true)
    {
        b.MapLoad(0.10, mapId);
        b.Teleport(0.15);
        b.SelfInfo(0.20, local);
        if (roster) b.Roster(0.25, 0x591C, dungeon, [local, .. others]);
        double t = 0.30;
        foreach (var p in others)
        {
            b.PlayerAppears(t, p);
            b.GlobalIdLink(t, p);
            t += 0.05;
        }
    }

    // ───────────────────────────── scenarios ─────────────────────────────

    /// <summary>
    /// Ultimate Berk (2300171) in Krao Cave: local Gladiator Kaelwyn, Cleric Mireth (heals, HoTs, Divine Aura),
    /// Elementalist Sylvae (Fire + Water spirits, Jointstrike: Curse DoT), Ranger Torvald (Deadshot multi-hits, triggers
    /// the boss self-heal), Sorcerer Ishara (Firebomb DoT, two Bittercold Winds). ~90 s, kill by Kaelwyn.
    /// </summary>
    public static Scenario BossKill(int seed = 1)
    {
        var b = new ScenarioBuilder("BossKill", seed, "Five-player party kills Ultimate Berk in about 90 seconds.");
        SimPlayer[] party = [Kaelwyn, Mireth, Sylvae, Torvald, Ishara];
        foreach (var p in party) b.AddPlayer(p);
        var boss = b.AddNpc(40610, UltimateBerk, isBoss: true);

        Intro(b, KraoCave, Kaelwyn, party[1..], KraoCave);
        b.SpawnNpc(0.50, boss, -9988.6f, 15286.3f, -143.0f);
        b.NpcHpInfo(0.55, boss);
        foreach (var p in party) b.Buff(1.0, p.EntityId, Mireth.EntityId, 17400027, 30000, ServerEpoch);
        b.Buff(1.2, Torvald.EntityId, Torvald.EntityId, 14220010, 20000, ServerEpoch);

        const double start = 3.0, end = 90.0;
        b.BeginEncounter(EncounterKind.Boss, EncounterOutcome.Kill, boss.EntityId, "Kill by Kaelwyn; one boss self-heal tick.");
        PartyFight(b, boss.EntityId, party, start, end, spiritIds: (28343, 22309), windIds: (25323, 25390), auraId: 26307,
            dodgeAt: 25.0, parryAt: 41.0, selfHealAt: 33.0, selfHeal: 122_788);

        // Killing blow by the local player, then HP 0 (inserted by the builder), kill record and death.
        double kt = end + 0.5;
        HitWithStyle(b, kt, Kaelwyn.EntityId, boss.EntityId, new Move(KeenStrike, 12000), 1.0, forceCrit: true);
        b.Kill(kt + 0.05, boss.EntityId, Kaelwyn.EntityId, KeenStrike);
        b.Death(kt + 0.10, boss.EntityId, 3);
        b.EndEncounter();
        return b.Build(idleTail: 4.0);
    }

    /// <summary>
    /// Enhanced Harcon (2300104): attempt 1 takes the boss to roughly 55 % HP, the party dies, the boss resets to full
    /// (same entity id), attempt 2 kills it. Expected encounters: Boss/Wipe then Boss/Kill.
    /// </summary>
    public static Scenario BossWipeThenKill(int seed = 1)
    {
        var b = new ScenarioBuilder("BossWipeThenKill", seed, "Wipe at ~45 % damage, boss resets to full, second attempt kills.");
        SimPlayer[] party = [Kaelwyn, Mireth, Sylvae, Torvald, Ishara];
        foreach (var p in party) b.AddPlayer(p);
        var boss = b.AddNpc(40877, EnhancedHarcon, isBoss: true);

        Intro(b, KraoCaveHard, Kaelwyn, party[1..], KraoCaveHard);
        b.SpawnNpc(0.50, boss, -10120.4f, 15340.9f, -140.0f);
        b.NpcHpInfo(0.55, boss);

        // Attempt 1 → wipe.
        b.BeginEncounter(EncounterKind.Boss, EncounterOutcome.Wipe, boss.EntityId, "Party wipes; boss returns to full HP.");
        PartyFight(b, boss.EntityId, party, 3.0, 37.0, spiritIds: (28343, 22309), windIds: (25323, 0), auraId: 0,
            dodgeAt: 15.0, parryAt: null, selfHealAt: null, selfHeal: 0);
        double t = 38.0;
        foreach (var p in party)
        {
            b.Hit(t, boss.EntityId, p.EntityId, NpcSlam, 250_000, new HitStyle { Layout = 6, Crit = true, Direction = HitDirection.Front });
            b.Death(t + 0.05, p.EntityId, 3);
            t += 0.4;
        }

        b.EndEncounter();
        b.HpReset(46.0, boss.EntityId);

        // Revive and come back.
        b.Teleport(52.0);
        double at = 52.5;
        foreach (var p in party[1..]) { b.PlayerAppears(at, p); at += 0.05; }

        b.BeginEncounter(EncounterKind.Boss, EncounterOutcome.Kill, boss.EntityId, "Second attempt kills; boss self-heal once.");
        PartyFight(b, boss.EntityId, party, 62.0, 137.0, spiritIds: (28401, 22377), windIds: (25455, 25470), auraId: 26350,
            dodgeAt: 80.0, parryAt: 95.0, selfHealAt: 90.0, selfHeal: 98_500);
        double kt = 137.5;
        HitWithStyle(b, kt, Torvald.EntityId, boss.EntityId, new Move(Snipe, 15000), 1.0, forceCrit: true);
        b.Kill(kt + 0.05, boss.EntityId, Torvald.EntityId, Snipe);
        b.Death(kt + 0.10, boss.EntityId, 3);
        b.EndEncounter();
        return b.Build(idleTail: 4.0);
    }

    /// <summary>Full party combat block on one boss between start and end.</summary>
    private static void PartyFight(ScenarioBuilder b, uint boss, SimPlayer[] party, double start, double end,
        (uint Fire, uint Water) spiritIds, (uint First, uint Second) windIds, uint auraId,
        double? dodgeAt, double? parryAt, double? selfHealAt, long selfHeal)
    {
        var r = b.Rng;
        var (glad, cleric, ele, ranger, sorc) = (party[0], party[1], party[2], party[3], party[4]);

        // Gladiator: rotation + periodic Blood Absorption self-heal.
        double nextSelfHeal = start + 8;
        Rotation(b, glad, boss, start, end, GladiatorMoves, afterHit: (t, _) =>
        {
            if (t < nextSelfHeal) return;
            b.SelfHeal(t, glad.EntityId, BloodAbsorption, (long)U(r, 600, 1400));
            nextSelfHeal = t + U(r, 12, 16);
        });

        // Cleric: damage rotation, heals, two HoTs, Divine Aura.
        Rotation(b, cleric, boss, start, end, ClericMoves, gcdMin: 1.8, gcdMax: 2.6);
        Heals(b, cleric, party, start + 2.0, end);
        Hot(b, cleric.EntityId, glad.EntityId, RadiantRecovery, 3_336, start + 12, 0x24);
        if (end - start > 50) Hot(b, cleric.EntityId, ranger.EntityId, RadiantRecovery, 2_800, start + 45, 0x31);
        if (auraId != 0)
        {
            double ta = start + 20;
            b.CastNotice(ta - 0.1, cleric.EntityId, cleric.EntityId, DivineAura);
            b.SpawnSummon(ta, auraId, DivineAuraNpc, cleric.EntityId, anchorIsOwner: false, kindFlags: 0x00, casterName: cleric.Name, hp: 4294);
            for (int i = 1; i <= 6; i++)
                b.Hit(ta + 2 * i, auraId, boss, DivineAura, (long)U(r, 850, 1100), new HitStyle { Layout = 6, Crit = r.NextDouble() < 0.25 });
        }

        // Elementalist: rotation, Jointstrike: Curse DoT, two spirits.
        Rotation(b, ele, boss, start, end, ElementalistMoves, afterHit: null);
        for (double t = start + 4; t < end; t += 18)
        {
            b.Hit(t, ele.EntityId, boss, JointstrikeCurse, (long)U(r, 5500, 7000), new HitStyle { Layout = 6, Crit = r.NextDouble() < 0.25, Direction = HitDirection.Back });
            DotSeries(b, ele.EntityId, boss, JointstrikeCurse, t, 7, 2.0, 1500, end, 0x10);
        }

        double ts = start + 1.0;
        b.CastNotice(ts - 0.1, ele.EntityId, ele.EntityId, SummonFireSpirit);
        b.SpawnSummon(ts, spiritIds.Fire, FireSpirit, ele.EntityId, anchorIsOwner: true, kindFlags: 0x10, hp: 2915);
        b.LinkRecord(ts + 0.05, spiritIds.Fire, spiritIds.Fire, SpiritLink, 110_000);
        SpiritAttacks(b, spiritIds.Fire, boss, FireSpiritAttack, 3600, ts + 1.0, end);
        b.CastNotice(ts + 0.4, ele.EntityId, ele.EntityId, SummonWaterSpirit);
        b.SpawnSummon(ts + 0.5, spiritIds.Water, WaterSpirit, ele.EntityId, anchorIsOwner: true, kindFlags: 0x10, hp: 5215);
        b.LinkRecord(ts + 0.55, spiritIds.Water, spiritIds.Water, SpiritLink, 110_000);
        SpiritAttacks(b, spiritIds.Water, boss, WaterSpiritAttack, 2900, ts + 1.5, end);

        // Ranger: rotation; the boss self-heal is triggered by a Deadshot.
        Rotation(b, ranger, boss, start, end, RangerMoves, gcdMin: 1.0, gcdMax: 1.5);
        if (selfHealAt is double sh)
        {
            HitWithStyle(b, sh, ranger.EntityId, boss, new Move(Deadshot, 30000, Extras: 4), 1.0);
            b.NpcSelfHealTick(sh, boss, ranger.EntityId, BossSelfHealEffect, Deadshot, selfHeal);
        }

        // Sorcerer: rotation, Firebomb DoTs, Bittercold Winds (cast notice → spawn → 02 38 → wind hits).
        Rotation(b, sorc, boss, start, end, SorcererMoves, afterHit: (t, m) =>
        {
            if (m.Skill == Firebomb) DotSeries(b, sorc.EntityId, boss, Firebomb, t, 4, 1.5, 1300, end, 0x22);
        });
        foreach (var (wind, tw) in new[] { (windIds.First, start + 10), (windIds.Second, start + 50) })
        {
            if (wind == 0 || tw + 9 > end) continue;
            b.CastNotice(tw, sorc.EntityId, boss, BittercoldWindCast);
            b.SpawnSummon(tw + 0.1, wind, BittercoldWind, sorc.EntityId, anchorIsOwner: false, kindFlags: 0x00, hp: 3446);
            b.Cast(tw + 0.15, wind, wind, BittercoldWindTick);
            for (int i = 1; i <= 8; i++)
                b.Hit(tw + i, wind, boss, BittercoldWindHit, (long)U(r, 2200, 2800), new HitStyle { Layout = 4, Switch10 = true, Crit = r.NextDouble() < 0.25 });
            b.Despawn(tw + 9.2, wind);
        }

        BossAttacks(b, boss, party, start + 1.5, end, 5000, dodgeAt, parryAt);
    }

    /// <summary>Kaelwyn, Mireth and Torvald clear five Krao mobs one after another (Trash, ends by idle timeout).</summary>
    public static Scenario TrashPull(int seed = 1)
    {
        var b = new ScenarioBuilder("TrashPull", seed, "Three players clear five non-boss mobs.");
        SimPlayer[] party = [Kaelwyn, Mireth, Torvald];
        foreach (var p in party) b.AddPlayer(p);
        Intro(b, KraoCaveTrash, Kaelwyn, party[1..], KraoCaveTrash);

        var mobs = new List<SimNpc>();
        for (int i = 0; i < 5; i++)
        {
            var mob = b.AddNpc((uint)(41001 + i * 7), i % 2 == 0 ? KraoMutant : KraoMagicTestSubject);
            mobs.Add(mob);
            b.SpawnNpc(0.5 + i * 0.05, mob, -9800f + i * 40, 15100f + i * 25, -150f);
        }

        b.BeginEncounter(EncounterKind.Trash, EncounterOutcome.Timeout, null, "Trash fights end by the 10 s idle timeout.");
        var r = b.Rng;
        for (int i = 0; i < mobs.Count; i++)
        {
            uint mob = mobs[i].EntityId;
            double t0 = 3.0 + i * 8.0, t1 = t0 + 7.0;
            Rotation(b, Kaelwyn, mob, t0, t1, GladiatorMoves, damageScale: 0.8);
            Rotation(b, Torvald, mob, t0, t1, RangerMoves, damageScale: 0.8);
            Rotation(b, Mireth, mob, t0, t1, ClericMoves, gcdMin: 1.8, gcdMax: 2.6, damageScale: 0.8);
            Heals(b, Mireth, party, t0 + 1.0, t1);
            for (double t = t0 + 1.2; t < t1; t += U(r, 1.8, 2.6))
                b.Hit(t, mob, party[r.Next(party.Length)].EntityId, NpcAttack, (long)U(r, 900, 2200), new HitStyle { Layout = 6, Direction = HitDirection.Front });
            HitWithStyle(b, t1 + 0.2, Kaelwyn.EntityId, mob, new Move(KeenStrike, 9000), 1.0);
            if (i == 2) b.Kill(t1 + 0.25, mob, Kaelwyn.EntityId, KeenStrike);
            b.Death(t1 + 0.3, mob, 3);
        }

        b.EndEncounter();
        return b.Build(idleTail: 14.0);
    }

    /// <summary>Kaelwyn fights enemy players Vexmora (Assassin) and Draketh (Templar) in the open world and kills Vexmora.</summary>
    public static Scenario PvpSkirmish(int seed = 1)
    {
        var b = new ScenarioBuilder("PvpSkirmish", seed, "Local Gladiator vs two enemy players; one kill.");
        b.AddPlayer(Kaelwyn with { IsPartyMember = false });
        b.AddPlayer(Vexmora);
        b.AddPlayer(Draketh);
        b.MapLoad(0.10, WorldLA);
        b.Teleport(0.15);
        b.SelfInfo(0.20, Kaelwyn);
        b.PlayerAppears(1.0, Vexmora);
        b.PlayerAppears(1.2, Draketh);

        b.BeginEncounter(EncounterKind.Pvp, EncounterOutcome.Timeout, null, "Vexmora dies at ~25 s; Draketh disengages at ~32 s.");
        Rotation(b, Kaelwyn, Vexmora.EntityId, 3.0, 25.0, GladiatorMoves, damageScale: 0.25);
        Rotation(b, Vexmora, Kaelwyn.EntityId, 3.4, 25.0, AssassinMoves, gcdMin: 1.0, gcdMax: 1.4);
        Rotation(b, Draketh, Kaelwyn.EntityId, 8.0, 32.0, TemplarMoves, gcdMin: 1.6, gcdMax: 2.2, noticeChance: 0.2);
        HitWithStyle(b, 25.5, Kaelwyn.EntityId, Vexmora.EntityId, new Move(RuptureStrike, 5200), 1.0, forceCrit: true);
        b.Kill(25.55, Vexmora.EntityId, Kaelwyn.EntityId, RuptureStrike);
        b.Death(25.6, Vexmora.EntityId, 3);
        Rotation(b, Kaelwyn, Draketh.EntityId, 26.5, 31.0, GladiatorMoves, damageScale: 0.25);
        b.SelfHeal(27.0, Kaelwyn.EntityId, BloodAbsorption, 950);
        b.EndEncounter();
        return b.Build(idleTail: 14.0);
    }

    /// <summary>Kaelwyn hits a Training Scarecrow (2400032, dummy) for 60 s.</summary>
    public static Scenario TrainingDummy(int seed = 1)
    {
        var b = new ScenarioBuilder("TrainingDummy", seed, "Local Gladiator on a Training Scarecrow for 60 seconds.");
        b.AddPlayer(Kaelwyn with { IsPartyMember = false });
        var dummy = b.AddNpc(50123, TrainingScarecrow, isBoss: false, isDummy: true, fixedMaxHp: 100_000_000);
        b.MapLoad(0.10, WorldLA);
        b.Teleport(0.15);
        b.SelfInfo(0.20, Kaelwyn);
        b.SpawnNpc(0.5, dummy, 1210.5f, -3320.25f, 88.0f);
        b.NpcHpInfo(0.55, dummy);

        b.BeginEncounter(EncounterKind.Dummy, EncounterOutcome.Timeout, dummy.EntityId, "Dummy never dies; the run ends by idle timeout.");
        double nextSelfHeal = 12;
        Rotation(b, Kaelwyn, dummy.EntityId, 3.0, 63.0, GladiatorMoves, afterHit: (t, _) =>
        {
            if (t < nextSelfHeal) return;
            b.SelfHeal(t, Kaelwyn.EntityId, BloodAbsorption, 1000);
            nextSelfHeal = t + 15;
        });
        b.EndEncounter();
        return b.Build(idleTail: 14.0);
    }
}
