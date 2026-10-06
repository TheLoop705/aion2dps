using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

/// <summary>
/// Deterministic sample encounters: a 5-player boss kill (~20k hits with DoTs, multi-hits, summons, heals, incoming
/// damage, buffs and a death), a wipe with a boss reset, and a PvP session. Names are invented.
/// </summary>
internal static class SampleData
{
    public sealed record Skill(uint Id, string Name, long Base, int Hits, double Weight, bool Dot = false, bool Heal = false, bool Summon = false);

    public sealed record Player(uint Id, string Name, CharacterClass Class, ushort Server, bool Local, double CastsPerSecond,
        double CritRate, uint? SummonId = null, double? DiesAtFraction = null);

    public const uint BossEntity = 90_001;
    public const uint MapAshfall = 600_012, MapArchive = 600_021, MapHollow = 610_031, MapAbyss = 400_010, MapCamp = 100_005;
    public const uint BossVorgrath = 2_300_104, BossIlsa = 2_300_118, BossMorrow = 2_310_201, BossCodex = 2_310_215,
        BossVael = 2_320_330, Scarecrow = 2_900_001;

    public static readonly Dictionary<CharacterClass, Skill[]> SkillsByClass = new()
    {
        [CharacterClass.Gladiator] =
        [
            new(11_020_030, "Ferocious Strike", 9_500, 2, 3), new(11_030_020, "Rending Arc", 7_800, 3, 2.5),
            new(11_040_010, "Ground Breaker", 21_000, 1, 0.8), new(11_050_040, "Bleeding Edge", 6_000, 1, 0.7, Dot: true),
            new(11_060_010, "Whirling Steel", 5_200, 5, 1.2), new(11_070_020, "Furious Lunge", 12_500, 2, 1.0),
            new(11_080_030, "Seismic Wave", 46_000, 1, 0.25),
        ],
        [CharacterClass.Ranger] =
        [
            new(14_010_010, "Swift Shot", 6_200, 2, 3), new(14_020_030, "Piercing Arrow", 13_800, 1, 1.4),
            new(14_030_020, "Arrow Volley", 4_100, 6, 1.2), new(14_040_010, "Venom Tip", 5_000, 1, 0.7, Dot: true),
            new(14_050_020, "Hunter's Mark", 9_900, 2, 1), new(14_060_040, "Storm of Arrows", 7_400, 8, 0.3),
        ],
        [CharacterClass.Sorcerer] =
        [
            new(15_010_030, "Flame Lance", 14_500, 2, 2.2), new(15_020_020, "Frost Shard", 8_800, 3, 2),
            new(15_030_040, "Meteor Fall", 52_000, 1, 0.3), new(15_040_010, "Searing Brand", 7_000, 1, 0.6, Dot: true),
            new(15_050_020, "Arcane Bolt", 10_400, 2, 1.5), new(15_060_030, "Chain Lightning", 6_600, 5, 1),
        ],
        [CharacterClass.Cleric] =
        [
            new(17_010_020, "Holy Smite", 6_900, 2, 2.5), new(17_020_030, "Judgement Light", 11_000, 1, 1),
            new(17_030_010, "Chastise", 4_200, 1, 0.7, Dot: true), new(17_040_020, "Radiant Burst", 5_600, 4, 1),
            new(17_050_010, "Healing Light", 14_000, 1, 1.6, Heal: true), new(17_060_020, "Restoration Wave", 6_500, 3, 0.6, Heal: true),
        ],
        [CharacterClass.Elementalist] =
        [
            new(16_010_020, "Spirit Lash", 7_900, 2, 2.5), new(16_020_030, "Stone Barrage", 4_700, 5, 1.2),
            new(16_030_010, "Cursed Chain", 5_400, 1, 0.8, Dot: true), new(16_040_020, "Wind Blade", 11_200, 2, 1.2),
            new(110_010, "Ember Claw", 6_100, 1, 0, Summon: true), new(110_020, "Blaze Breath", 4_000, 3, 0, Summon: true),
        ],
        [CharacterClass.Assassin] =
        [
            new(13_010_020, "Shadow Fang", 8_800, 2, 2.5), new(13_020_030, "Ambush", 19_000, 1, 0.8),
            new(13_030_010, "Toxic Blade", 5_000, 1, 0.8, Dot: true), new(13_040_020, "Flurry", 4_500, 3, 1.2),
        ],
        [CharacterClass.Templar] =
        [
            new(12_010_020, "Shield Bash", 7_000, 1, 2.5), new(12_020_030, "Righteous Blade", 10_500, 1, 1.4),
            new(12_030_010, "Punishing Strike", 6_000, 2, 1),
        ],
        [CharacterClass.Chanter] =
        [
            new(18_010_020, "Mantra Strike", 6_400, 1, 2.5), new(18_020_030, "Gale Palm", 9_800, 1, 1.2),
            new(18_030_010, "Echoing Blow", 5_200, 2, 1),
        ],
    };

    public static IEnumerable<Skill> AllSkills => SkillsByClass.Values.SelectMany(s => s);

    public static readonly (uint Id, string Name)[] NpcSkills =
        [(2_301_010, "Ashen Cleave"), (2_301_020, "Cinder Rain"), (2_301_030, "Molten Slam")];

    public static readonly (uint Id, string Name)[] BuffSkills =
    [
        (17_070_010, "Blessing of Light"), (17_080_010, "Sacred Ward"), (11_100_010, "Battle Cry"), (14_080_010, "Eagle Eye"),
        (15_070_010, "Arcane Focus"), (16_050_010, "Spirit Bond"),
    ];

    public static readonly Dictionary<uint, (string Name, bool Boss, bool Dummy)> Npcs = new()
    {
        [BossVorgrath] = ("Vorgrath the Ashen", true, false),
        [BossIlsa] = ("Pyre Warden Ilsa", true, false),
        [BossMorrow] = ("Archivist Morrow", true, false),
        [BossCodex] = ("The Drowned Codex", true, false),
        [BossVael] = ("Thornmother Vael", true, false),
        [Scarecrow] = ("Training Scarecrow", false, true),
    };

    public static readonly Dictionary<uint, string> Maps = new()
    {
        [MapAshfall] = "Ashfall Sanctum (Hard)",
        [MapArchive] = "Sunken Archive (Normal)",
        [MapHollow] = "Verdant Hollow (Normal)",
        [MapAbyss] = "Abyssal Front",
        [MapCamp] = "Pathfinder Camp",
    };

    public static List<Player> Party(string localName = "Kaelith") =>
    [
        new(1001, localName, CharacterClass.Gladiator, 1011, true, 3.8, 0.32),
        new(1002, "Morrowyn", CharacterClass.Ranger, 1011, false, 4.2, 0.38),
        new(1003, "Seraphix", CharacterClass.Sorcerer, 1012, false, 3.4, 0.35, DiesAtFraction: 0.62),
        new(1004, "Thalune", CharacterClass.Cleric, 1011, false, 3.6, 0.22),
        new(1005, "Vesperine", CharacterClass.Elementalist, 2021, false, 3.5, 0.30, SummonId: 50_005),
    ];

    // ───────────── Boss fight ─────────────

    public static EncounterRecord BossFight(int seed = 7, double duration = 372, EncounterOutcome outcome = EncounterOutcome.Kill,
        DateTime? startUtc = null, uint bossCode = BossVorgrath, uint mapId = MapAshfall, int playerCount = 5, double scale = 1.0,
        string localName = "Kaelith", EncounterKind kind = EncounterKind.Boss)
    {
        var rng = new Random(seed);
        var start = startUtc ?? new DateTime(2026, 10, 5, 19, 42, 10, DateTimeKind.Utc);
        var players = Party(localName).Take(Math.Max(1, playerCount)).ToList();
        var hits = new List<HitRecord>();
        var measured = new HashSet<HitRecord>(ReferenceEqualityComparer.Instance);
        var ids = players.Select(p => p.Id).ToList();

        foreach (var p in players)
        {
            double end = p.DiesAtFraction is { } f ? duration * f : duration;
            Simulate(rng, p, () => BossEntity, ids, 0.2 + rng.NextDouble() * 0.8, end, scale, hits, measured, incomingToTarget: false);
        }
        if (kind != EncounterKind.Dummy) SimulateBoss(rng, players, duration, scale, hits);
        hits = hits.OrderBy(h => h.T).ToList();

        long bossDamage = hits.Where(IsOutgoingDamage).Sum(h => h.Amount);
        long maxHp = outcome == EncounterOutcome.Kill ? bossDamage : (long)(bossDamage / 0.62);
        if (outcome == EncounterOutcome.Kill)
        {
            var last = hits.Last(IsOutgoingDamage);
            last.Flags |= HitFlags.KillingBlow;
        }

        var record = new EncounterRecord
        {
            Kind = kind,
            Outcome = outcome,
            StartUtc = start,
            MapId = mapId,
            ServerId = 1011,
            LocalPlayerName = localName,
            LocalPlayerClass = CharacterClass.Gladiator,
            BossNpcCode = bossCode,
            BossEntityId = BossEntity,
            BossMaxHp = maxHp,
            BossHpStart = maxHp,
            Hits = hits,
            ResetCount = outcome == EncounterOutcome.Wipe ? 0 : (seed % 3 == 0 ? 1 : 0),
        };
        Finish(record, players, measured, duration);

        // Boss HP timeline, one sample per second (plus a reset after a wipe).
        var perSecond = SeriesMath.PerSecond(hits, IsOutgoingDamage, (int)Math.Ceiling(record.DurationSeconds) + 1);
        long hp = maxHp;
        record.BossHpTimeline.Add(new HpSample(0, maxHp));
        for (int i = 0; i < perSecond.Length; i++)
        {
            hp = Math.Max(0, hp - perSecond[i]);
            record.BossHpTimeline.Add(new HpSample(i + 1, hp));
        }
        if (outcome == EncounterOutcome.Wipe)
        {
            float t = record.BossHpTimeline[^1].T;
            record.BossHpTimeline.Add(new HpSample(t + 2, (long)(maxHp * 0.7)));
            record.BossHpTimeline.Add(new HpSample(t + 4, maxHp));
        }
        record.BossHpEnd = record.BossHpTimeline[^1].Hp;
        long lost = maxHp - (outcome == EncounterOutcome.Wipe ? hp : record.BossHpEnd.Value);
        record.HpCheck = new HpCheckResult
        {
            DecodedDamage = bossDamage,
            HpLost = lost,
            BossSelfHealing = 0,
            Ratio = lost > 0 ? (double)bossDamage / lost : 0,
        };
        record.HpCheck.Passed = Math.Abs(record.HpCheck.Ratio - 1) <= 0.01;
        record.Targets.Add(new TargetRecord
        {
            EntityId = BossEntity, NpcCode = bossCode, IsBoss = kind == EncounterKind.Boss, IsDummy = kind == EncounterKind.Dummy,
            MaxHp = maxHp, LastHp = record.BossHpEnd, DamageTaken = bossDamage, Killed = outcome == EncounterOutcome.Kill,
            FirstHitUtc = record.StartUtc, LastHitUtc = record.EndUtc,
        });
        foreach (var c in record.Combatants)
        {
            c.BossDamage = c.Damage;
            c.Contribution = maxHp > 0 ? (double)c.BossDamage / maxHp : c.DamageShare;
        }
        return record;
    }

    public static EncounterRecord WipeFight(int seed = 21) =>
        BossFight(seed, 150, EncounterOutcome.Wipe, new DateTime(2026, 10, 5, 19, 35, 0, DateTimeKind.Utc));

    // ───────────── PvP ─────────────

    public static EncounterRecord PvpSession(int seed = 11, DateTime? startUtc = null, double duration = 96, string localName = "Kaelith")
    {
        var rng = new Random(seed);
        var local = new Player(1001, localName, CharacterClass.Gladiator, 1011, true, 1.5, 0.3);
        var enemies = new List<Player>
        {
            new(7001, "Nyxara", CharacterClass.Assassin, 2021, false, 1.6, 0.36),
            new(7002, "Bramwell", CharacterClass.Templar, 2021, false, 1.1, 0.2),
            new(7003, "Solenne", CharacterClass.Chanter, 1012, false, 1.2, 0.25),
            new(7004, "Corvath", CharacterClass.Ranger, 2021, false, 1.3, 0.33),
        };
        double[] focus = [0.45, 0.2, 0.2, 0.15];
        var hits = new List<HitRecord>();
        var measured = new HashSet<HitRecord>(ReferenceEqualityComparer.Instance);
        uint PickEnemy()
        {
            double x = rng.NextDouble(), acc = 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                acc += focus[i];
                if (x <= acc) return enemies[i].Id;
            }
            return enemies[^1].Id;
        }
        Simulate(rng, local, PickEnemy, [local.Id], 0.3, duration, 0.33, hits, measured, incomingToTarget: false);
        foreach (var e in enemies)
        {
            double from = rng.NextDouble() * 12, to = duration - rng.NextDouble() * 20;
            Simulate(rng, e, () => local.Id, [e.Id], from, to, 0.28, hits, measured, incomingToTarget: true);
        }
        hits = hits.OrderBy(h => h.T).ToList();
        var record = new EncounterRecord
        {
            Kind = EncounterKind.Pvp,
            Outcome = EncounterOutcome.Timeout,
            StartUtc = startUtc ?? new DateTime(2026, 10, 4, 21, 5, 0, DateTimeKind.Utc),
            MapId = MapAbyss,
            ServerId = 1011,
            LocalPlayerName = localName,
            LocalPlayerClass = CharacterClass.Gladiator,
            Hits = hits,
            Note = "Abyss skirmish",
        };
        Finish(record, [local, .. enemies], measured, duration);
        var localRec = record.Combatants.First(c => c.IsLocal);
        localRec.Deaths = 1;
        foreach (var e in record.Combatants.Where(c => c.Kind == CombatantKind.EnemyPlayer))
        {
            e.DamageToLocal = hits.Where(h => h.Actor == e.EntityId && h.Target == local.Id && (h.Flags & HitFlags.Incoming) != 0).Sum(h => h.Amount);
            e.DamageFromLocal = hits.Where(h => h.Actor == local.Id && h.Target == e.EntityId && (h.Flags & (HitFlags.Heal | HitFlags.Incoming)) == 0).Sum(h => h.Amount);
            e.KilledByLocal = e.EntityId is 7001 or 7003;
            record.Targets.Add(new TargetRecord { EntityId = e.EntityId, PlayerName = e.Name, DamageTaken = e.DamageFromLocal, Killed = e.KilledByLocal });
        }
        return record;
    }

    // ───────────── Simulation ─────────────

    public static bool IsOutgoingDamage(HitRecord h) => (h.Flags & (HitFlags.Incoming | HitFlags.Heal)) == 0 && h.Target == BossEntity;

    private static void Simulate(Random rng, Player p, Func<uint> pickTarget, List<uint> allies, double from, double to, double scale,
        List<HitRecord> hits, HashSet<HitRecord> measured, bool incomingToTarget)
    {
        var skills = SkillsByClass[p.Class];
        var castable = skills.Where(s => s.Weight > 0).ToArray();
        double totalWeight = castable.Sum(s => s.Weight);
        byte tag = (byte)rng.Next(256);
        double t = from;
        double nextSummon = from + 1.5;
        var summonSkills = skills.Where(s => s.Summon).ToArray();
        HitFlags extra = incomingToTarget ? HitFlags.Incoming : HitFlags.None;

        while (t < to)
        {
            // Summon attacks run on their own clock.
            while (p.SummonId is { } sid && summonSkills.Length > 0 && nextSummon < t)
            {
                var ss = summonSkills[rng.Next(summonSkills.Length)];
                byte stag = (byte)rng.Next(256);
                uint target = pickTarget();
                for (int i = 0; i < ss.Hits; i++)
                    AddHit(rng, p, ss, target, (float)(nextSummon + i * 0.15), stag, (byte)(i + 1), scale, hits, measured, HitFlags.Summon | extra, sid);
                nextSummon += 0.8 + rng.NextDouble() * 0.6;
            }

            double pick = rng.NextDouble() * totalWeight;
            var s = castable[^1];
            foreach (var c in castable)
            {
                pick -= c.Weight;
                if (pick <= 0) { s = c; break; }
            }
            tag++;
            if (s.Heal)
            {
                var targets = s.Hits > 1 ? allies.OrderBy(_ => rng.Next()).Take(s.Hits).ToList() : [allies[rng.Next(allies.Count)]];
                foreach (var target in targets)
                {
                    hits.Add(new HitRecord
                    {
                        T = (float)t, Actor = p.Id, Source = p.Id, Target = target, Skill = s.Id, HitTag = tag, HitIndex = 1,
                        Amount = (long)(s.Base * scale * (0.85 + rng.NextDouble() * 0.3) * (rng.NextDouble() < p.CritRate ? 1.5 : 1)),
                        Flags = HitFlags.Heal,
                    });
                }
            }
            else
            {
                uint target = pickTarget();
                for (int i = 0, count = s.Hits >= 2 ? s.Hits + 2 : s.Hits; i < count; i++)
                    AddHit(rng, p, s, target, (float)(t + i * 0.12), tag, (byte)(i + 1), scale, hits, measured, extra, p.Id);
                if (s.Dot)
                {
                    for (int k = 1; k <= 8; k++)
                    {
                        hits.Add(new HitRecord
                        {
                            T = (float)(t + k * 1.0 + 0.05), Actor = p.Id, Source = p.Id, Target = target, Skill = s.Id, HitTag = tag,
                            Amount = (long)(s.Base * 0.2 * scale * (0.9 + rng.NextDouble() * 0.2)), Flags = HitFlags.Dot | extra,
                        });
                    }
                }
            }
            t += 1.0 / p.CastsPerSecond * (0.6 + rng.NextDouble() * 0.8) + (s.Hits - 1) * 0.08;
        }
    }

    private static void AddHit(Random rng, Player p, Skill s, uint target, float t, byte tag, byte index, double scale,
        List<HitRecord> hits, HashSet<HitRecord> measured, HitFlags baseFlags, uint source)
    {
        var flags = baseFlags;
        double amount = s.Base * scale * (0.85 + rng.NextDouble() * 0.3);
        bool isMeasured = rng.NextDouble() < 0.86;
        if (rng.NextDouble() < 0.02)
        {
            flags |= HitFlags.Dodged;
            amount = 0;
        }
        else
        {
            if (rng.NextDouble() < p.CritRate) { flags |= HitFlags.Crit; amount *= 1.55; }
            if (isMeasured)
            {
                double dir = rng.NextDouble();
                if (dir < 0.3) { flags |= HitFlags.Back; amount *= 1.1; }
                else if (dir < 0.75) flags |= HitFlags.Front;
                if (rng.NextDouble() < 0.08) { flags |= HitFlags.Perfect; amount *= 1.15; }
                if (rng.NextDouble() < 0.12) { flags |= HitFlags.Double; amount *= 1.4; }
                if (rng.NextDouble() < 0.05) flags |= HitFlags.Smite;
                if (rng.NextDouble() < 0.035) { flags |= HitFlags.Parry; amount *= 0.5; }
            }
        }
        byte extraHits = 0;
        if (s.Hits >= 3 && rng.NextDouble() < 0.3)
        {
            flags |= HitFlags.MultiHit;
            extraHits = (byte)rng.Next(1, 3);
            amount *= 1 + extraHits * 0.3;
        }
        var h = new HitRecord
        {
            T = t, Actor = p.Id, Source = source, Target = target, Skill = s.Id, Amount = (long)amount, Flags = flags,
            HitTag = tag, HitIndex = index, ExtraHits = extraHits,
        };
        hits.Add(h);
        if (isMeasured && (flags & HitFlags.Dodged) == 0) measured.Add(h);
    }

    private static void SimulateBoss(Random rng, List<Player> players, double duration, double scale, List<HitRecord> hits)
    {
        double t = 1.2;
        byte tag = 0;
        while (t < duration)
        {
            var target = rng.NextDouble() < 0.5 ? players[0] : players[rng.Next(players.Count)];
            if (target.DiesAtFraction is { } f && t > duration * f) target = players[0];
            var (skill, _) = NpcSkills[rng.Next(NpcSkills.Length)];
            var flags = HitFlags.Incoming;
            double amount = (9_000 + rng.NextDouble() * 22_000) * scale;
            double roll = rng.NextDouble();
            if (roll < 0.06) { flags |= HitFlags.Dodged; amount = 0; }
            else if (roll < 0.10) { flags |= HitFlags.Parry; amount *= 0.5; }
            else
            {
                if (rng.NextDouble() < 0.15) { flags |= HitFlags.Crit; amount *= 1.5; }
                if (rng.NextDouble() < 0.2) flags |= HitFlags.Back;
            }
            hits.Add(new HitRecord
            {
                T = (float)t, Actor = BossEntity, Source = BossEntity, Target = target.Id, Skill = skill, Amount = (long)amount,
                Flags = flags, HitTag = tag++, HitIndex = 1,
            });
            t += 0.9 + rng.NextDouble() * 1.1;
        }
    }

    private static uint GroupKey(uint skill) => skill >= 10_000_000 ? SkillIds.BaseId(skill) : skill;

    /// <summary>Fills timing, combatants and totals from the hits.</summary>
    private static void Finish(EncounterRecord r, List<Player> players, HashSet<HitRecord> measured, double plannedDuration)
    {
        float first = r.Hits.Count == 0 ? 0 : r.Hits.Min(h => h.T), last = r.Hits.Count == 0 ? 0 : r.Hits.Max(h => h.T);
        r.DurationSeconds = Math.Max(1, last - first);
        r.EndUtc = r.StartUtc.AddSeconds(r.DurationSeconds);
        int n = (int)Math.Floor(r.Hits.Count == 0 ? 0 : r.Hits.Max(h => h.T)) + 1;

        foreach (var p in players)
        {
            bool enemy = r.Kind == EncounterKind.Pvp && !p.Local;
            var outgoing = r.Hits.Where(h => h.Actor == p.Id && (h.Flags & HitFlags.Heal) == 0).ToList();
            var damage = outgoing.Where(h => h.Amount > 0).ToList();
            var c = new CombatantRecord
            {
                EntityId = p.Id, Name = p.Name, Class = p.Class, Kind = enemy ? CombatantKind.EnemyPlayer : CombatantKind.Player,
                ServerId = p.Server, IsLocal = p.Local, IsPartyMember = !enemy, Level = 45, GearScore = (uint)(2200 + p.Id % 7 * 37),
                CombatPower = (ulong)(480_000 + p.Id % 11 * 12_500),
                Damage = damage.Sum(h => h.Amount),
                Healing = r.Hits.Where(h => h.Actor == p.Id && (h.Flags & HitFlags.Heal) != 0).Sum(h => h.Amount),
                Deaths = p.DiesAtFraction is null ? 0 : 1,
            };
            if (outgoing.Count > 0)
            {
                c.FirstHitUtc = r.StartUtc.AddSeconds(outgoing.Min(h => h.T));
                c.LastHitUtc = r.StartUtc.AddSeconds(outgoing.Max(h => h.T));
            }
            c.DamagePerSecond = SeriesMath.PerSecond(damage, _ => true, n).ToList();
            c.Dps = c.Damage / r.DurationSeconds;
            c.ActiveDps = c.FirstHitUtc is { } f && c.LastHitUtc is { } l ? c.Damage / Math.Max(1, (l - f).TotalSeconds) : 0;

            var rotation = CastGrouper.Build(r.Hits, p.Id);
            var casts = CastGrouper.CountByGroup(rotation, GroupKey);
            foreach (var g in r.Hits.Where(h => h.Actor == p.Id).GroupBy(h => GroupKey(h.Skill)))
            {
                var direct = g.Where(h => (h.Flags & (HitFlags.Dot | HitFlags.Heal)) == 0).ToList();
                var landed = direct.Where(h => h.Amount > 0).ToList();
                var dots = g.Where(h => (h.Flags & HitFlags.Dot) != 0).ToList();
                var q = direct.Where(measured.Contains).ToList();
                var s = new SkillStats
                {
                    SkillId = g.Key, SampleSkillId = g.First().Skill,
                    Damage = g.Where(h => (h.Flags & HitFlags.Heal) == 0).Sum(h => h.Amount),
                    Healing = g.Where(h => (h.Flags & HitFlags.Heal) != 0).Sum(h => h.Amount),
                    Hits = g.Any(h => (h.Flags & HitFlags.Heal) != 0) ? g.Count() : direct.Count,
                    Casts = casts.GetValueOrDefault(g.Key),
                    Crits = direct.Count(h => (h.Flags & HitFlags.Crit) != 0),
                    MinHit = landed.Count == 0 ? 0 : landed.Min(h => h.Amount),
                    MaxHit = landed.Count == 0 ? 0 : landed.Max(h => h.Amount),
                    Back = q.Count(h => (h.Flags & HitFlags.Back) != 0),
                    Front = q.Count(h => (h.Flags & HitFlags.Front) != 0),
                    Perfect = q.Count(h => (h.Flags & HitFlags.Perfect) != 0),
                    Double = q.Count(h => (h.Flags & HitFlags.Double) != 0),
                    Smite = q.Count(h => (h.Flags & HitFlags.Smite) != 0),
                    Parried = q.Count(h => (h.Flags & HitFlags.Parry) != 0),
                    MultiHits = direct.Count(h => (h.Flags & HitFlags.MultiHit) != 0),
                    ExtraHitCount = direct.Sum(h => h.ExtraHits),
                    DotTicks = dots.Count,
                    DotDamage = dots.Sum(h => h.Amount),
                    QualityMeasuredHits = q.Count,
                    FromSummon = g.Any(h => (h.Flags & HitFlags.Summon) != 0),
                };
                c.Skills.Add(s);
            }
            var dmgSkills = c.Skills.Where(s => s.Healing == 0).ToList();
            c.Quality = new HitQualityStats
            {
                Hits = dmgSkills.Sum(s => s.Hits), Crits = dmgSkills.Sum(s => s.Crits), Back = dmgSkills.Sum(s => s.Back),
                Front = dmgSkills.Sum(s => s.Front), Perfect = dmgSkills.Sum(s => s.Perfect), Double = dmgSkills.Sum(s => s.Double),
                Smite = dmgSkills.Sum(s => s.Smite), Parried = dmgSkills.Sum(s => s.Parried),
                Dodged = outgoing.Count(h => (h.Flags & HitFlags.Dodged) != 0),
                MultiHits = dmgSkills.Sum(s => s.MultiHits), ExtraHitCount = dmgSkills.Sum(s => s.ExtraHitCount),
                DotTicks = dmgSkills.Sum(s => s.DotTicks), DotDamage = dmgSkills.Sum(s => s.DotDamage),
                QualityMeasuredHits = dmgSkills.Sum(s => s.QualityMeasuredHits),
            };
            var maxHit = outgoing.Where(h => (h.Flags & HitFlags.Dot) == 0).OrderByDescending(h => h.Amount).FirstOrDefault();
            if (maxHit is not null)
            {
                c.Quality.MaxHit = maxHit.Amount;
                c.Quality.MaxHitSkillId = maxHit.Skill;
            }

            var incoming = r.Hits.Where(h => h.Target == p.Id && (h.Flags & HitFlags.Incoming) != 0).ToList();
            c.Defense = new DefenseStats
            {
                HitsTaken = incoming.Count(h => (h.Flags & HitFlags.Dodged) == 0),
                DamageTaken = incoming.Sum(h => h.Amount),
                CritsTaken = incoming.Count(h => (h.Flags & HitFlags.Crit) != 0),
                BackHitsTaken = incoming.Count(h => (h.Flags & HitFlags.Back) != 0),
                Dodged = incoming.Count(h => (h.Flags & HitFlags.Dodged) != 0),
                Parried = incoming.Count(h => (h.Flags & HitFlags.Parry) != 0),
                Blocked = p.Class is CharacterClass.Gladiator or CharacterClass.Templar ? incoming.Count / 9 : null,
                HealingReceived = r.Hits.Where(h => h.Target == p.Id && (h.Flags & HitFlags.Heal) != 0).Sum(h => h.Amount),
            };
            c.DamageTaken = c.Defense.DamageTaken;
            foreach (var g in incoming.GroupBy(h => (h.Actor, h.Skill)))
            {
                var attacker = players.FirstOrDefault(x => x.Id == g.Key.Actor);
                c.DamageTakenBySource.Add(new SourceDamage
                {
                    SourceEntityId = g.Key.Actor,
                    SourceNpcCode = attacker is null ? r.BossNpcCode : null,
                    SourcePlayerName = attacker?.Name,
                    SkillId = g.Key.Skill,
                    Damage = g.Sum(h => h.Amount),
                    Hits = g.Count(h => (h.Flags & HitFlags.Dodged) == 0),
                    Crits = g.Count(h => (h.Flags & HitFlags.Crit) != 0),
                });
            }

            if (r.Kind != EncounterKind.Pvp) AddBuffs(c, p, r.DurationSeconds, players);
            r.Combatants.Add(c);
        }

        var friendly = r.Combatants.Where(c => c.Kind != CombatantKind.EnemyPlayer).ToList();
        r.TotalDamage = friendly.Sum(c => c.Damage);
        r.PartyDps = r.TotalDamage / r.DurationSeconds;
        foreach (var c in friendly) c.DamageShare = r.TotalDamage > 0 ? (double)c.Damage / r.TotalDamage : 0;
        foreach (var c in r.Combatants.Where(c => c.Kind == CombatantKind.EnemyPlayer))
        {
            long enemyTotal = r.Combatants.Where(x => x.Kind == CombatantKind.EnemyPlayer).Sum(x => x.Damage);
            c.DamageShare = enemyTotal > 0 ? (double)c.Damage / enemyTotal : 0;
            c.Contribution = c.DamageShare;
        }
        foreach (var c in friendly) c.Contribution = c.DamageShare;
    }

    private static void AddBuffs(CombatantRecord c, Player p, double duration, List<Player> party)
    {
        var rng = new Random((int)p.Id);
        var cleric = party.FirstOrDefault(x => x.Class == CharacterClass.Cleric);
        void Add(uint skill, uint? caster, double uptime, int apps)
        {
            uptime = Math.Clamp(uptime + (rng.NextDouble() - 0.5) * 0.08, 0.02, 1);
            c.Buffs.Add(new BuffUptime
            {
                BuffId = skill * 10 + 1, SkillId = skill, Applications = apps, Uptime = uptime, UptimeSeconds = uptime * duration,
                CasterEntityId = caster,
            });
        }
        if (cleric is not null)
        {
            Add(17_070_010, cleric.Id, 0.94, 4);
            Add(17_080_010, cleric.Id, 0.31, 9);
        }
        switch (p.Class)
        {
            case CharacterClass.Gladiator: Add(11_100_010, p.Id, 0.58, 7); break;
            case CharacterClass.Ranger: Add(14_080_010, p.Id, 0.66, 6); break;
            case CharacterClass.Sorcerer: Add(15_070_010, p.Id, 0.41, 5); break;
            case CharacterClass.Elementalist: Add(16_050_010, p.Id, 0.99, 1); break;
        }
    }
}
