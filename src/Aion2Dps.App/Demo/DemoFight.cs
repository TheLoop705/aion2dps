namespace Aion2Dps.App.Demo;

/// <summary>A demo combatant (party member) with a damage profile.</summary>
internal sealed record DemoCombatant(uint EntityId, string Name, CharacterClass Class, bool IsLocal, double BaseDps, double CritRate,
    double JoinSeconds, uint Level, uint GearScore, ulong CombatPower)
{
    public static readonly DemoCombatant[] Party =
    [
        new(1001, "Velkaris", CharacterClass.Sorcerer, true, 560_000, 0.42, 0.0, 45, 3120, 812_400),
        new(1002, "Thorne", CharacterClass.Gladiator, false, 505_000, 0.37, 0.4, 45, 3080, 798_100),
        new(1003, "Ysolde", CharacterClass.Ranger, false, 455_000, 0.46, 0.8, 45, 3010, 771_900),
        new(1004, "Brannoc", CharacterClass.Templar, false, 215_000, 0.24, 0.2, 45, 3155, 805_300),
        new(1005, "Mireth", CharacterClass.Cleric, false, 92_000, 0.18, 1.6, 45, 2990, 742_800),
    ];

    public static readonly DemoCombatant[] Raid =
    [
        .. Party,
        new(1006, "Asharen", CharacterClass.Assassin, false, 480_000, 0.44, 0.6, 45, 3050, 780_000),
        new(1007, "Kiriel", CharacterClass.Elementalist, false, 410_000, 0.33, 1.0, 45, 2970, 751_000),
        new(1008, "Dorran", CharacterClass.Chanter, false, 160_000, 0.27, 0.9, 45, 2940, 733_000),
        new(1009, "Junova", CharacterClass.Brawler, false, 430_000, 0.39, 2.4, 45, 3000, 766_000),
        new(1010, "Fennick", CharacterClass.Ranger, false, 350_000, 0.41, 6.0, 44, 2880, 702_000),
    ];
}

internal sealed record DemoHit(float T, int Player, uint Skill, long Amount, HitFlags Flags, uint Target, byte ExtraHits);

/// <summary>
/// A deterministic, pre-generated demo fight: per-second damage/heal/taken for each combatant, boss HP, adds, deaths and
/// every hit. Live snapshots read it at any elapsed time; <see cref="BuildRecord"/> produces a realistic EncounterRecord.
/// </summary>
internal sealed class DemoFight
{
    public const uint BossEntity = 50_001;
    public const uint DummyEntity = 50_100;
    private const double AddShare = 0.17;

    public required int Index { get; init; }
    public required EncounterKind Kind { get; init; }
    public required EncounterOutcome PlannedOutcome { get; init; }
    public required uint NpcCode { get; init; }
    public required uint TargetEntity { get; init; }
    public required long? MaxHp { get; init; }
    public required DateTime StartUtc { get; init; }
    public required uint MapId { get; init; }
    public required IReadOnlyList<DemoCombatant> Players { get; init; }
    public double Duration { get; private set; }
    public IReadOnlyList<DemoAdd> Adds { get; private set; } = [];
    public IReadOnlyList<DemoHit> Hits => _hits;

    private long[,] _boss = new long[0, 0];
    private long[,] _adds = new long[0, 0];
    private long[,] _heal = new long[0, 0];
    private long[,] _taken = new long[0, 0];
    private double[] _deathAt = [];
    private readonly List<DemoHit> _hits = new();
    private int _seconds;

    internal sealed record DemoAdd(uint EntityId, uint NpcCode, double SpawnAt, long MaxHp);

    public static DemoFight CreateBoss(int index, DateTime startUtc, IReadOnlyList<DemoCombatant> players, double targetSeconds,
        double? wipeAtFraction, uint npcCode = FakeGameData.BossNpc, uint mapId = FakeGameData.DemoMap, int seed = 7)
    {
        double sum = players.Sum(p => p.BaseDps);
        long maxHp = (long)(Math.Round(sum * (1 - AddShare * 0.25) * targetSeconds / 100_000) * 100_000);
        var f = new DemoFight
        {
            Index = index, Kind = EncounterKind.Boss, PlannedOutcome = wipeAtFraction is null ? EncounterOutcome.Kill : EncounterOutcome.Wipe,
            NpcCode = npcCode, TargetEntity = BossEntity + (uint)index * 10, MaxHp = maxHp, StartUtc = startUtc, MapId = mapId, Players = players,
        };
        f.Generate(new Random(seed * 1000 + index * 7919), (int)Math.Ceiling(targetSeconds * 1.8) + 12, wipeAtFraction, withAdds: true);
        return f;
    }

    public static DemoFight CreateTraining(int index, DateTime startUtc, DemoCombatant player, TimeSpan duration, int seed = 7)
    {
        var f = new DemoFight
        {
            Index = index, Kind = EncounterKind.Training, PlannedOutcome = EncounterOutcome.Timeout, NpcCode = FakeGameData.DummyNpc,
            TargetEntity = DummyEntity, MaxHp = null, StartUtc = startUtc, MapId = FakeGameData.DemoMap, Players = [player with { JoinSeconds = 0 }],
        };
        f.Generate(new Random(seed * 31 + index), (int)Math.Ceiling(duration.TotalSeconds) + 1, null, withAdds: false);
        f.Duration = duration.TotalSeconds;
        return f;
    }

    private void Generate(Random rng, int maxSeconds, double? wipeAt, bool withAdds)
    {
        int n = Players.Count;
        _seconds = maxSeconds;
        _boss = new long[n, maxSeconds];
        _adds = new long[n, maxSeconds];
        _heal = new long[n, maxSeconds];
        _taken = new long[n, maxSeconds];
        _deathAt = Enumerable.Repeat(double.PositiveInfinity, n).ToArray();
        double sum = Players.Sum(p => p.BaseDps);
        if (withAdds)
        {
            long addHp = (long)(sum * AddShare * 16);
            Adds = [new DemoAdd(TargetEntity + 1, FakeGameData.AddNpc, 20, addHp), new DemoAdd(TargetEntity + 2, FakeGameData.AddNpc, 24, addHp)];
        }
        var addHpLeft = Adds.Select(a => a.MaxHp).ToArray();

        long hpLeft = MaxHp ?? long.MaxValue;
        double end = maxSeconds;
        bool wiping = false;
        for (int t = 0; t < maxSeconds; t++)
        {
            bool addsAlive = false;
            for (int a = 0; a < Adds.Count; a++) addsAlive |= t >= Adds[a].SpawnAt && addHpLeft[a] > 0;
            long bossThisSecond = 0;
            for (int p = 0; p < n; p++)
            {
                var pl = Players[p];
                if (t + 1 <= pl.JoinSeconds || t >= _deathAt[p]) continue;
                double since = t - pl.JoinSeconds;
                double ramp = Math.Min(1, 0.6 + 0.14 * since);
                double burst = t % 30 < 7 ? 1.32 : t % 30 > 24 ? 0.82 : 1;
                double variance = 0.72 + rng.NextDouble() * 0.56;
                long dmg = (long)(pl.BaseDps * ramp * burst * variance);
                long addPart = addsAlive ? (long)(dmg * AddShare) : 0;
                _boss[p, t] = dmg - addPart;
                _adds[p, t] = addPart;
                bossThisSecond += dmg - addPart;
                if (pl.Class == CharacterClass.Cleric) _heal[p, t] = (long)(38_000 + rng.NextDouble() * 52_000);
                if (pl.Class == CharacterClass.Chanter) _heal[p, t] = (long)(9_000 + rng.NextDouble() * 14_000);
                double takenRate = pl.Class == CharacterClass.Templar ? 62_000 : pl.Class == CharacterClass.Gladiator ? 21_000 : 7_500;
                if (Kind != EncounterKind.Training && rng.NextDouble() < 0.7) _taken[p, t] = (long)(takenRate * (0.4 + rng.NextDouble() * 1.2));
                GenerateHits(rng, p, t, dmg - addPart, addPart);
            }
            for (int a = 0; a < Adds.Count; a++)
                if (t >= Adds[a].SpawnAt && addHpLeft[a] > 0)
                {
                    long hit = 0;
                    for (int p = 0; p < n; p++) hit += _adds[p, t];
                    addHpLeft[a] = Math.Max(0, addHpLeft[a] - hit / Math.Max(1, Adds.Count(x => t >= x.SpawnAt)));
                }

            if (MaxHp is not { } max) continue;
            if (!wiping && hpLeft - bossThisSecond <= 0)
            {
                // Kill inside this second.
                double frac = bossThisSecond > 0 ? (double)hpLeft / bossThisSecond : 1;
                end = t + Math.Clamp(frac, 0.05, 1);
                hpLeft = 0;
                break;
            }
            hpLeft -= bossThisSecond;
            if (wipeAt is { } w && !wiping && (double)hpLeft / max <= w)
            {
                wiping = true;
                // Deaths roll in over a few seconds, healer last.
                var order = Enumerable.Range(0, n).OrderBy(i => Players[i].Class == CharacterClass.Cleric ? 1 : 0).ThenBy(_ => rng.Next()).ToList();
                for (int k = 0; k < order.Count; k++) _deathAt[order[k]] = t + 1.2 + k * 1.6;
            }
            if (wiping && _deathAt.All(d => d <= t + 1))
            {
                end = _deathAt.Max() + 0.4;
                break;
            }
        }
        Duration = Math.Min(end, maxSeconds);
        _hits.RemoveAll(h => h.T > Duration);
        _hits.Sort((a, b) => a.T.CompareTo(b.T));
    }

    private void GenerateHits(Random rng, int p, int t, long bossDamage, long addDamage)
    {
        var pl = Players[p];
        // Skill weights: a few heavy hitters, a basic attack filler.
        double[] weights = [0.24, 0.19, 0.15, 0.12, 0.1, 0.08, 0.05, 0.07];
        bool hasDot = pl.Class is CharacterClass.Assassin or CharacterClass.Sorcerer;
        int count = 2 + rng.Next(3);
        long remaining = bossDamage;
        for (int i = 0; i < count && remaining > 0; i++)
        {
            int skill = Pick(rng, weights);
            long amount = i == count - 1 ? remaining : (long)(remaining * (0.25 + rng.NextDouble() * 0.4));
            remaining -= amount;
            var flags = HitFlags.None;
            bool dot = hasDot && skill == 4 && rng.NextDouble() < 0.6;
            if (dot) flags |= HitFlags.Dot;
            else
            {
                if (rng.NextDouble() < pl.CritRate) flags |= HitFlags.Crit;
                if (rng.NextDouble() < 0.22) flags |= HitFlags.Back;
                else if (rng.NextDouble() < 0.3) flags |= HitFlags.Front;
                if (rng.NextDouble() < 0.08) flags |= HitFlags.Perfect;
                if (rng.NextDouble() < 0.05) flags |= HitFlags.Double;
                if (rng.NextDouble() < 0.03) flags |= HitFlags.Parry;
            }
            byte extra = 0;
            if (!dot && rng.NextDouble() < 0.12) { flags |= HitFlags.MultiHit; extra = (byte)(1 + rng.Next(3)); }
            _hits.Add(new DemoHit((float)(t + (i + rng.NextDouble()) / count), p, FakeGameData.SkillId(pl.Class, skill), amount, flags, TargetEntity, extra));
        }
        if (addDamage > 0 && Adds.Count > 0)
            _hits.Add(new DemoHit((float)(t + rng.NextDouble()), p, FakeGameData.SkillId(pl.Class, Pick(rng, weights)), addDamage, HitFlags.None,
                Adds[rng.Next(Adds.Count)].EntityId, 0));
        if (_heal.Length > 0 && pl.Class is CharacterClass.Cleric or CharacterClass.Chanter && _heal[p, t] > 0)
            _hits.Add(new DemoHit((float)(t + rng.NextDouble()), p, FakeGameData.SkillId(pl.Class, 5), _heal[p, t], HitFlags.Heal,
                Players[rng.Next(Players.Count)].EntityId, 0));
        if (_taken[p, t] > 0)
            _hits.Add(new DemoHit((float)(t + rng.NextDouble()), p, 2_490_100u + (uint)rng.Next(4), _taken[p, t], HitFlags.Incoming, pl.EntityId, 0));
    }

    private static int Pick(Random rng, double[] weights)
    {
        double r = rng.NextDouble() * weights.Sum();
        for (int i = 0; i < weights.Length; i++)
        {
            r -= weights[i];
            if (r <= 0) return i;
        }
        return weights.Length - 1;
    }

    // ───────────────────────── Queries at an elapsed time ─────────────────────────

    private static double Cum(long[,] a, int p, double e)
    {
        int full = (int)Math.Floor(e);
        int len = a.GetLength(1);
        double sum = 0;
        for (int t = 0; t < Math.Min(full, len); t++) sum += a[p, t];
        if (full < len) sum += a[p, full] * (e - full);
        return sum;
    }

    public double BossDamage(int p, double e) => Cum(_boss, p, Math.Min(e, Duration));
    public double AddDamage(int p, double e) => Cum(_adds, p, Math.Min(e, Duration));
    public double Healing(int p, double e) => Cum(_heal, p, Math.Min(e, Duration));
    public double Taken(int p, double e) => Cum(_taken, p, Math.Min(e, Duration));
    public bool IsDead(int p, double e) => _deathAt[p] <= e;
    public bool AnyDeaths => _deathAt.Any(double.IsFinite);

    public double TotalBossDamage(double e)
    {
        double s = 0;
        for (int p = 0; p < Players.Count; p++) s += BossDamage(p, e);
        return s;
    }

    public long? BossHp(double e) => MaxHp is { } max ? Math.Max(0, (long)(max - TotalBossDamage(e))) : null;

    public EncounterOutcome OutcomeAt(double e) => e >= Duration ? PlannedOutcome : EncounterOutcome.InProgress;

    public (long? Hp, long Max, bool Spawned, double Damage) AddState(int index, double e)
    {
        var add = Adds[index];
        if (e < add.SpawnAt) return (null, add.MaxHp, false, 0);
        double dmg = 0;
        foreach (var h in _hits)
        {
            if (h.T > e) break;
            if (h.Target == add.EntityId) dmg += h.Amount;
        }
        return (Math.Max(0, (long)(add.MaxHp - dmg)), add.MaxHp, true, dmg);
    }

    // ───────────────────────── Record ─────────────────────────

    /// <summary>Builds a full EncounterRecord for the fight up to <paramref name="elapsed"/> (the whole fight when it ended).</summary>
    public EncounterRecord BuildRecord(IGameData gameData, double elapsed, EncounterOutcome? outcomeOverride = null)
    {
        double e = Math.Min(elapsed, Duration);
        double dur = Math.Max(1, e);
        var rec = new EncounterRecord
        {
            Id = DeterministicGuid(StartUtc, Index),
            Kind = Kind,
            Outcome = outcomeOverride ?? OutcomeAt(elapsed),
            StartUtc = StartUtc,
            EndUtc = StartUtc.AddSeconds(e),
            DurationSeconds = dur,
            MapId = MapId,
            ServerId = FakeGameData.DemoServer,
            LocalPlayerName = Players.FirstOrDefault(p => p.IsLocal)?.Name,
            LocalPlayerClass = Players.FirstOrDefault(p => p.IsLocal)?.Class ?? CharacterClass.Unknown,
            BossNpcCode = NpcCode,
            BossEntityId = TargetEntity,
            BossMaxHp = MaxHp,
            BossHpStart = MaxHp,
            BossHpEnd = BossHp(e),
            ResetCount = PlannedOutcome == EncounterOutcome.Kill && Index > 0 ? Index % 2 : 0,
            Note = Kind == EncounterKind.Training ? $"training run {Math.Round(Duration)} s" : null,
        };
        if (MaxHp is not null)
            for (int t = 0; t <= (int)e; t++) rec.BossHpTimeline.Add(new HpSample(t, BossHp(t) ?? 0));

        double partyDamage = 0;
        for (int p = 0; p < Players.Count; p++) partyDamage += BossDamage(p, e);
        rec.TotalDamage = (long)partyDamage;
        rec.PartyDps = partyDamage / dur;

        for (int p = 0; p < Players.Count; p++)
        {
            var pl = Players[p];
            var c = new CombatantRecord
            {
                EntityId = pl.EntityId, Name = pl.Name, Class = pl.Class, Kind = CombatantKind.Player, ServerId = FakeGameData.DemoServer,
                IsLocal = pl.IsLocal, IsPartyMember = true, Level = pl.Level, GearScore = pl.GearScore, CombatPower = pl.CombatPower,
                Damage = (long)BossDamage(p, e), BossDamage = (long)BossDamage(p, e),
                Healing = (long)Healing(p, e), DamageTaken = (long)Taken(p, e), Deaths = IsDead(p, e) ? 1 : 0,
                FirstHitUtc = StartUtc.AddSeconds(pl.JoinSeconds),
            };
            c.Dps = c.Damage / dur;
            double lastHit = Math.Min(e, double.IsFinite(_deathAt[p]) ? _deathAt[p] : e);
            c.LastHitUtc = StartUtc.AddSeconds(lastHit);
            c.ActiveDps = c.Damage / Math.Max(1, lastHit - pl.JoinSeconds);
            c.DamageShare = partyDamage > 0 ? c.Damage / partyDamage : 0;
            c.Contribution = MaxHp is { } max ? (double)c.BossDamage / max : c.DamageShare;
            for (int t = 0; t < (int)Math.Ceiling(e); t++) c.DamagePerSecond.Add(_boss[p, Math.Min(t, _seconds - 1)]);

            var skills = new Dictionary<uint, SkillStats>();
            var taken = new Dictionary<uint, SourceDamage>();
            foreach (var h in _hits)
            {
                if (h.T > e) break;
                if (h.Player != p) continue;
                if ((h.Flags & HitFlags.Incoming) != 0)
                {
                    if (!taken.TryGetValue(h.Skill, out var sd))
                        taken[h.Skill] = sd = new SourceDamage { SourceEntityId = TargetEntity, SourceNpcCode = NpcCode, SkillId = h.Skill };
                    sd.Damage += h.Amount;
                    sd.Hits++;
                    c.Defense.HitsTaken++;
                    continue;
                }
                if (h.Target != TargetEntity && (h.Flags & HitFlags.Heal) == 0) continue; // add damage not in boss scope
                uint key = gameData.GetSkillGroupKey(h.Skill);
                if (!skills.TryGetValue(key, out var s))
                    skills[key] = s = new SkillStats { SkillId = key, SampleSkillId = h.Skill, MinHit = long.MaxValue };
                if ((h.Flags & HitFlags.Heal) != 0) { s.Healing += h.Amount; s.Casts++; continue; }
                s.Damage += h.Amount;
                if ((h.Flags & HitFlags.Dot) != 0)
                {
                    s.DotTicks++;
                    s.DotDamage += h.Amount;
                    c.Quality.DotTicks++;
                    c.Quality.DotDamage += h.Amount;
                    continue;
                }
                s.Hits++;
                s.Casts++;
                s.QualityMeasuredHits++;
                s.MinHit = Math.Min(s.MinHit, h.Amount);
                s.MaxHit = Math.Max(s.MaxHit, h.Amount);
                c.Quality.Hits++;
                c.Quality.QualityMeasuredHits++;
                if ((h.Flags & HitFlags.Crit) != 0) { s.Crits++; c.Quality.Crits++; }
                if ((h.Flags & HitFlags.Back) != 0) { s.Back++; c.Quality.Back++; }
                if ((h.Flags & HitFlags.Front) != 0) { s.Front++; c.Quality.Front++; }
                if ((h.Flags & HitFlags.Perfect) != 0) { s.Perfect++; c.Quality.Perfect++; }
                if ((h.Flags & HitFlags.Double) != 0) { s.Double++; c.Quality.Double++; }
                if ((h.Flags & HitFlags.Parry) != 0) { s.Parried++; c.Quality.Parried++; }
                if ((h.Flags & HitFlags.MultiHit) != 0) { s.MultiHits++; s.ExtraHitCount += h.ExtraHits; c.Quality.MultiHits++; c.Quality.ExtraHitCount += h.ExtraHits; }
                if (h.Amount > c.Quality.MaxHit) { c.Quality.MaxHit = h.Amount; c.Quality.MaxHitSkillId = h.Skill; }
            }
            foreach (var s in skills.Values) if (s.MinHit == long.MaxValue) s.MinHit = 0;
            c.Skills = skills.Values.OrderByDescending(s => s.Damage).ThenByDescending(s => s.Healing).ToList();
            c.DamageTakenBySource = taken.Values.OrderByDescending(t => t.Damage).ToList();
            c.Defense.DamageTaken = c.DamageTaken;
            c.Defense.Dodged = c.Defense.HitsTaken / 9;
            c.Defense.Parried = pl.Class is CharacterClass.Templar or CharacterClass.Gladiator ? c.Defense.HitsTaken / 7 : 0;
            c.Defense.Blocked = pl.Class is CharacterClass.Templar or CharacterClass.Gladiator ? c.Defense.HitsTaken / 5 : null;
            c.Defense.HealingReceived = (long)(c.DamageTaken * 0.8);
            c.Buffs.Add(new BuffUptime { BuffId = FakeGameData.SkillId(pl.Class, 6) * 10, SkillId = FakeGameData.SkillId(pl.Class, 6), Applications = 1 + (int)(e / 30), UptimeSeconds = e * 0.62, Uptime = 0.62 });
            rec.Combatants.Add(c);
        }
        rec.Combatants = rec.Combatants.OrderByDescending(c => c.Damage).ToList();

        rec.Targets.Add(new TargetRecord
        {
            EntityId = TargetEntity, NpcCode = NpcCode, IsBoss = Kind == EncounterKind.Boss, IsDummy = Kind == EncounterKind.Training,
            MaxHp = MaxHp, LastHp = BossHp(e), DamageTaken = (long)partyDamage, Killed = rec.Outcome == EncounterOutcome.Kill,
            FirstHitUtc = StartUtc, LastHitUtc = rec.EndUtc,
        });
        for (int a = 0; a < Adds.Count; a++)
        {
            var st = AddState(a, e);
            if (!st.Spawned) continue;
            rec.Targets.Add(new TargetRecord
            {
                EntityId = Adds[a].EntityId, NpcCode = Adds[a].NpcCode, MaxHp = st.Max, LastHp = st.Hp, DamageTaken = (long)st.Damage,
                Killed = st.Hp == 0, FirstHitUtc = StartUtc.AddSeconds(Adds[a].SpawnAt),
            });
        }

        var idByIndex = Players.Select(p => p.EntityId).ToArray();
        foreach (var h in _hits)
        {
            if (h.T > e) break;
            bool incoming = (h.Flags & HitFlags.Incoming) != 0;
            rec.Hits.Add(new HitRecord
            {
                T = h.T,
                Actor = incoming ? TargetEntity : idByIndex[h.Player],
                Source = incoming ? TargetEntity : idByIndex[h.Player],
                Target = incoming ? idByIndex[h.Player] : h.Target,
                Skill = h.Skill,
                Amount = h.Amount,
                Flags = h.Flags,
                ExtraHits = h.ExtraHits,
            });
        }
        if (MaxHp is { } maxHp)
        {
            long lost = maxHp - (BossHp(e) ?? maxHp);
            rec.HpCheck = new HpCheckResult
            {
                DecodedDamage = (long)partyDamage,
                HpLost = lost,
                Ratio = lost > 0 ? partyDamage / lost : 1,
                Passed = true,
            };
        }
        return rec;
    }

    public static Guid DeterministicGuid(DateTime start, int index)
    {
        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b, start.Ticks);
        BitConverter.TryWriteBytes(b[8..], (long)index * 2654435761L ^ 0x5A17_D3B0_0000_0000L);
        return new Guid(b);
    }
}
