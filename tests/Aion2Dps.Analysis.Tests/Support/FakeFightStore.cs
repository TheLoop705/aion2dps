using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

/// <summary>Thread-safe in-memory <see cref="IFightStore"/> for the history/trends views.</summary>
internal sealed class FakeFightStore : IFightStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, EncounterRecord> _records = new();
    public int QueryCalls;
    public int LoadCalls;
    public int? LastQueryThreadId;

    public void Save(EncounterRecord record)
    {
        lock (_gate) _records[record.Id] = record;
    }

    public EncounterRecord? Load(Guid id)
    {
        Interlocked.Increment(ref LoadCalls);
        lock (_gate) return _records.GetValueOrDefault(id);
    }

    public bool Delete(Guid id)
    {
        lock (_gate) return _records.Remove(id);
    }

    public IReadOnlyList<FightSummary> Query(FightQuery q)
    {
        Interlocked.Increment(ref QueryCalls);
        LastQueryThreadId = Environment.CurrentManagedThreadId;
        lock (_gate)
        {
            return _records.Values.Select(Summarize)
                .Where(s => q.FromUtc is null || s.StartUtc >= q.FromUtc)
                .Where(s => q.ToUtc is null || s.StartUtc <= q.ToUtc)
                .Where(s => q.MapId is null || s.MapId == q.MapId)
                .Where(s => q.BossNpcCode is null || s.BossNpcCode == q.BossNpcCode)
                .Where(s => q.Kind is null || s.Kind == q.Kind)
                .Where(s => q.LocalPlayerName is null || s.LocalPlayerName == q.LocalPlayerName)
                .Where(s => !q.KillsOnly || s.Outcome == EncounterOutcome.Kill)
                .OrderByDescending(s => s.StartUtc)
                .Skip(q.Offset).Take(q.Limit)
                .ToList();
        }
    }

    public IReadOnlyList<string> GetCharacters()
    {
        lock (_gate) return _records.Values.Select(r => r.LocalPlayerName).OfType<string>().Distinct().OrderBy(x => x).ToList();
    }

    public IReadOnlyList<TrendPoint> GetBossTrend(uint bossNpcCode, string characterName)
    {
        lock (_gate)
        {
            return _records.Values.Where(r => r.BossNpcCode == bossNpcCode && r.LocalPlayerName == characterName)
                .OrderBy(r => r.StartUtc)
                .Select(r =>
                {
                    var me = r.Combatants.FirstOrDefault(c => c.IsLocal);
                    return new TrendPoint(r.Id, r.StartUtc, me?.Dps ?? 0, me?.Damage ?? 0, r.DurationSeconds, r.Outcome);
                }).ToList();
        }
    }

    public IReadOnlyList<BossTrendSummary> GetBossSummaries(string characterName)
    {
        lock (_gate)
        {
            return _records.Values.Where(r => r.BossNpcCode is not null && r.LocalPlayerName == characterName && r.Kind != EncounterKind.Pvp)
                .GroupBy(r => r.BossNpcCode!.Value)
                .Select(g =>
                {
                    var pts = GetBossTrend(g.Key, characterName);
                    var kills = pts.Where(p => p.Outcome == EncounterOutcome.Kill).ToList();
                    return new BossTrendSummary
                    {
                        BossNpcCode = g.Key, MapId = g.First().MapId, Fights = pts.Count, Kills = kills.Count,
                        BestDps = pts.Max(p => p.Dps), MedianDps = SeriesMath.Median(pts.Select(p => p.Dps)), LastDps = pts[^1].Dps,
                        FastestKillSeconds = kills.Count == 0 ? 0 : kills.Min(k => k.DurationSeconds), LastFoughtUtc = pts[^1].StartUtc,
                    };
                }).ToList();
        }
    }

    public TrendPoint? GetPersonalBest(uint bossNpcCode, string characterName) =>
        GetBossTrend(bossNpcCode, characterName).Where(p => p.Outcome == EncounterOutcome.Kill).OrderByDescending(p => p.Dps).FirstOrDefault();

    public void Dispose() { }

    private static FightSummary Summarize(EncounterRecord r)
    {
        var me = r.Combatants.FirstOrDefault(c => c.IsLocal);
        return new FightSummary
        {
            Id = r.Id, StartUtc = r.StartUtc, DurationSeconds = r.DurationSeconds, Kind = r.Kind, Outcome = r.Outcome, MapId = r.MapId,
            BossNpcCode = r.BossNpcCode, BossMaxHp = r.BossMaxHp, TotalDamage = r.TotalDamage, PartyDps = r.PartyDps,
            PlayerCount = r.Combatants.Count(c => c.Kind == CombatantKind.Player), LocalPlayerName = r.LocalPlayerName,
            LocalPlayerClass = r.LocalPlayerClass, LocalDamage = me?.Damage ?? 0, LocalDps = me?.Dps ?? 0, HpCheckRatio = r.HpCheck?.Ratio,
            Note = r.Note,
        };
    }

    /// <summary>~30 fights over 60 days across three instances, a dummy, PvP sessions and two characters.</summary>
    public static FakeFightStore CreateSample(DateTime nowUtc)
    {
        var store = new FakeFightStore();
        var rng = new Random(5);
        (uint map, uint boss, double hpScale)[] bosses =
        [
            (SampleData.MapAshfall, SampleData.BossVorgrath, 1.0), (SampleData.MapAshfall, SampleData.BossIlsa, 0.8),
            (SampleData.MapArchive, SampleData.BossMorrow, 0.6), (SampleData.MapArchive, SampleData.BossCodex, 0.7),
            (SampleData.MapHollow, SampleData.BossVael, 0.5),
        ];
        int seed = 100;
        for (int run = 0; run < 7; run++)
        {
            var runStart = nowUtc.AddDays(-run * 8.5 - rng.NextDouble() * 2).AddHours(-rng.Next(1, 6));
            var (map, _, _) = bosses[run % 3 == 0 ? 0 : run % 3 == 1 ? 2 : 4];
            var inMap = bosses.Where(b => b.map == map).ToList();
            double t = 0;
            foreach (var (m, boss, hp) in inMap)
            {
                bool wipe = rng.NextDouble() < 0.25;
                if (wipe)
                {
                    store.Save(SampleData.BossFight(seed++, 50 + rng.Next(40), EncounterOutcome.Wipe, runStart.AddMinutes(t), boss, m, 5, 0.7 + run * 0.04));
                    t += 6;
                }
                store.Save(SampleData.BossFight(seed++, 70 + rng.Next(70), EncounterOutcome.Kill, runStart.AddMinutes(t), boss, m, 5, (0.75 + run * 0.035) * hp + 0.2));
                t += 9;
            }
        }
        store.Save(SampleData.BossFight(seed++, 60, EncounterOutcome.ManualReset, nowUtc.AddDays(-1.2), SampleData.Scarecrow, SampleData.MapCamp, 1, 1.0,
            kind: EncounterKind.Dummy));
        store.Save(SampleData.BossFight(seed++, 90, EncounterOutcome.Kill, nowUtc.AddDays(-3.1), SampleData.BossMorrow, SampleData.MapArchive, 4, 0.9, "Mirelle"));
        store.Save(SampleData.PvpSession(11, nowUtc.AddHours(-5)));
        store.Save(SampleData.PvpSession(12, nowUtc.AddDays(-12)));
        return store;
    }
}
