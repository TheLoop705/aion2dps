using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Per-player insights computed from a saved encounter: damage downtime, best burst window, healing received by source
/// and the hardest incoming hits. Pure (no WPF); every method tolerates empty or partial records.
/// </summary>
public static class FightInsights
{
    /// <summary>Seconds without damage between a player's first and last damaging second.</summary>
    /// <param name="ActiveSeconds">Whole seconds from the first to the last damaging second (inclusive).</param>
    /// <param name="IdleSeconds">Seconds inside that span with no damage at all.</param>
    /// <param name="LongestGapSeconds">Longest run of consecutive idle seconds.</param>
    /// <param name="LongestGapStart">Fight second the longest gap starts at (null when there is no gap).</param>
    public readonly record struct Downtime(int ActiveSeconds, int IdleSeconds, int LongestGapSeconds, int? LongestGapStart)
    {
        /// <summary>0..1 share of <see cref="ActiveSeconds"/> spent dealing damage.</summary>
        public double Uptime => ActiveSeconds > 0 ? 1 - (double)IdleSeconds / ActiveSeconds : 0;
    }

    public static Downtime ComputeDowntime(IReadOnlyList<long> perSecond)
    {
        int first = -1, last = -1;
        for (int i = 0; i < perSecond.Count; i++)
        {
            if (perSecond[i] <= 0) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return default;
        int idle = 0, run = 0, longest = 0;
        int? longestStart = null;
        for (int i = first; i <= last; i++)
        {
            if (perSecond[i] > 0)
            {
                run = 0;
                continue;
            }
            idle++;
            run++;
            if (run > longest)
            {
                longest = run;
                longestStart = i - run + 1;
            }
        }
        return new Downtime(last - first + 1, idle, longest, longestStart);
    }

    /// <summary>The best <paramref name="windowSeconds"/>-second stretch: its DPS and start second (null when no damage).</summary>
    public static (double Dps, int Start)? BestBurst(IReadOnlyList<long> perSecond, int windowSeconds = 10)
    {
        if (perSecond.Count == 0) return null;
        int w = Math.Clamp(windowSeconds, 1, perSecond.Count);
        long sum = 0, best = -1;
        int bestStart = 0;
        for (int i = 0; i < perSecond.Count; i++)
        {
            sum += perSecond[i];
            if (i >= w) sum -= perSecond[i - w];
            if (i >= w - 1 && sum > best)
            {
                best = sum;
                bestStart = i - w + 1;
            }
        }
        return best > 0 ? ((double)best / w, bestStart) : null;
    }

    /// <summary>One healer/skill pair that healed a player.</summary>
    public sealed record HealSource(uint HealerId, uint SkillId, bool IsSelf, long Amount, int Count, long Max);

    /// <summary>Healing <paramref name="entityId"/> received, grouped by healer and skill, biggest first.</summary>
    public static List<HealSource> HealingReceived(EncounterRecord record, uint entityId)
    {
        var groups = new Dictionary<(uint, uint), (long Amount, int Count, long Max)>();
        foreach (var h in record.Hits)
        {
            if ((h.Flags & HitFlags.Heal) == 0 || (h.Flags & HitFlags.Incoming) != 0 || h.Target != entityId || h.Amount <= 0) continue;
            var key = (h.Actor, h.Skill);
            var g = groups.GetValueOrDefault(key);
            groups[key] = (g.Amount + h.Amount, g.Count + 1, Math.Max(g.Max, h.Amount));
        }
        return groups
            .Select(kv => new HealSource(kv.Key.Item1, kv.Key.Item2, kv.Key.Item1 == entityId, kv.Value.Amount, kv.Value.Count, kv.Value.Max))
            .OrderByDescending(s => s.Amount)
            .ToList();
    }

    /// <summary>Biggest single incoming hit per (attacking entity, skill) for <paramref name="entityId"/>; same key as
    /// <see cref="SourceDamage"/> (SourceEntityId, SkillId).</summary>
    public static Dictionary<(uint Source, uint Skill), long> MaxIncomingHits(EncounterRecord record, uint entityId)
    {
        var max = new Dictionary<(uint, uint), long>();
        foreach (var h in record.Hits)
        {
            if ((h.Flags & HitFlags.Incoming) == 0 || h.Target != entityId) continue;
            var key = (h.Source, h.Skill);
            if (h.Amount > max.GetValueOrDefault(key)) max[key] = h.Amount;
        }
        return max;
    }
}
