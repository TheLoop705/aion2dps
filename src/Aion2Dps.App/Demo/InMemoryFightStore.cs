namespace Aion2Dps.App.Demo;

/// <summary>Thread-safe in-memory <see cref="IFightStore"/> (demo mode and tests).</summary>
public sealed class InMemoryFightStore : IFightStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, EncounterRecord> _records = new();

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    public void Save(EncounterRecord record)
    {
        lock (_gate) _records[record.Id] = record;
    }

    public EncounterRecord? Load(Guid id)
    {
        lock (_gate) return _records.TryGetValue(id, out var r) ? r : null;
    }

    public bool Delete(Guid id)
    {
        lock (_gate) return _records.Remove(id);
    }

    public IReadOnlyList<FightSummary> Query(FightQuery query)
    {
        lock (_gate)
        {
            IEnumerable<EncounterRecord> q = _records.Values;
            if (query.FromUtc is { } from) q = q.Where(r => r.StartUtc >= from);
            if (query.ToUtc is { } to) q = q.Where(r => r.StartUtc <= to);
            if (query.MapId is { } map) q = q.Where(r => r.MapId == map);
            if (query.BossNpcCode is { } boss) q = q.Where(r => r.BossNpcCode == boss);
            if (query.Kind is { } kind) q = q.Where(r => r.Kind == kind);
            if (query.LocalPlayerName is { } name) q = q.Where(r => string.Equals(r.LocalPlayerName, name, StringComparison.OrdinalIgnoreCase));
            if (query.KillsOnly) q = q.Where(r => r.Outcome == EncounterOutcome.Kill);
            return q.OrderByDescending(r => r.StartUtc).Skip(query.Offset).Take(query.Limit).Select(Summarize).ToList();
        }
    }

    public static FightSummary Summarize(EncounterRecord r)
    {
        var local = r.Combatants.FirstOrDefault(c => c.IsLocal);
        return new FightSummary
        {
            Id = r.Id, StartUtc = r.StartUtc, DurationSeconds = r.DurationSeconds, Kind = r.Kind, Outcome = r.Outcome, MapId = r.MapId,
            BossNpcCode = r.BossNpcCode, BossMaxHp = r.BossMaxHp, TotalDamage = r.TotalDamage, PartyDps = r.PartyDps,
            PlayerCount = r.Combatants.Count(c => c.Kind == CombatantKind.Player), LocalPlayerName = r.LocalPlayerName,
            LocalPlayerClass = r.LocalPlayerClass, LocalDamage = local?.Damage ?? 0, LocalDps = local?.Dps ?? 0,
            HpCheckRatio = r.HpCheck?.Ratio, Note = r.Note,
        };
    }

    public IReadOnlyList<string> GetCharacters()
    {
        lock (_gate)
            return _records.Values.Select(r => r.LocalPlayerName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => n!).OrderBy(n => n).ToList();
    }

    public IReadOnlyList<TrendPoint> GetBossTrend(uint bossNpcCode, string characterName)
    {
        lock (_gate)
            return Mine(characterName).Where(r => r.BossNpcCode == bossNpcCode && r.Kind == EncounterKind.Boss)
                .OrderBy(r => r.StartUtc)
                .Select(r => Point(r, characterName))
                .ToList();
    }

    public IReadOnlyList<BossTrendSummary> GetBossSummaries(string characterName)
    {
        lock (_gate)
        {
            return Mine(characterName).Where(r => r.Kind == EncounterKind.Boss && r.BossNpcCode is not null)
                .GroupBy(r => r.BossNpcCode!.Value)
                .Select(g =>
                {
                    var pts = g.OrderBy(r => r.StartUtc).Select(r => Point(r, characterName)).ToList();
                    var kills = pts.Where(p => p.Outcome == EncounterOutcome.Kill).ToList();
                    var sorted = pts.Select(p => p.Dps).OrderBy(d => d).ToList();
                    return new BossTrendSummary
                    {
                        BossNpcCode = g.Key, MapId = g.First().MapId, Fights = pts.Count, Kills = kills.Count,
                        BestDps = kills.Count > 0 ? kills.Max(p => p.Dps) : 0,
                        MedianDps = sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2,
                        LastDps = pts.Count > 0 ? pts[^1].Dps : 0,
                        FastestKillSeconds = kills.Count > 0 ? kills.Min(p => p.DurationSeconds) : 0,
                        LastFoughtUtc = pts.Count > 0 ? pts[^1].StartUtc : default,
                    };
                })
                .OrderByDescending(s => s.LastFoughtUtc)
                .ToList();
        }
    }

    public TrendPoint? GetPersonalBest(uint bossNpcCode, string characterName)
    {
        lock (_gate)
            return Mine(characterName).Where(r => r.BossNpcCode == bossNpcCode && r.Outcome == EncounterOutcome.Kill)
                .Select(r => Point(r, characterName))
                .OrderByDescending(p => p.Dps)
                .FirstOrDefault();
    }

    private IEnumerable<EncounterRecord> Mine(string name) =>
        _records.Values.Where(r => string.Equals(r.LocalPlayerName, name, StringComparison.OrdinalIgnoreCase));

    private static TrendPoint Point(EncounterRecord r, string name)
    {
        var c = r.Combatants.FirstOrDefault(x => x.IsLocal) ?? r.Combatants.FirstOrDefault(x => x.Name == name);
        return new TrendPoint(r.Id, r.StartUtc, c?.Dps ?? 0, c?.Damage ?? 0, r.DurationSeconds, r.Outcome);
    }

    /// <summary>Fills the store with a few weeks of plausible history for demo mode.</summary>
    public void SeedDemoHistory(IGameData gameData, DateTime nowUtc, int seed = 11)
    {
        var rng = new Random(seed);
        var bosses = new (uint Npc, uint Map, double Secs)[]
        {
            (FakeGameData.BossNpc, FakeGameData.DemoMap, 140), (FakeGameData.SecondBossNpc, 600_052, 125),
            (2_490_011, 600_063, 110), (2_490_012, 600_063, 160),
        };
        for (int i = 0; i < 16; i++)
        {
            var b = bosses[i % bosses.Length];
            var start = nowUtc.AddDays(-(16 - i) * 1.6).AddMinutes(rng.Next(0, 600));
            // Players get a little stronger over time.
            double growth = 0.85 + i * 0.012;
            var party = DemoCombatant.Party.Select(p => p with { BaseDps = p.BaseDps * growth * (0.92 + rng.NextDouble() * 0.16) }).ToList();
            bool wipe = i % 5 == 3;
            var fight = DemoFight.CreateBoss(1000 + i, start, party, b.Secs, wipe ? 0.31 : null, b.Npc, b.Map, seed + i);
            Save(fight.BuildRecord(gameData, fight.Duration));
        }
        var training = DemoFight.CreateTraining(2000, nowUtc.AddDays(-2), DemoCombatant.Party[0], TimeSpan.FromSeconds(60), seed);
        Save(training.BuildRecord(gameData, training.Duration, EncounterOutcome.Timeout));
    }

    public void Dispose() { }
}
