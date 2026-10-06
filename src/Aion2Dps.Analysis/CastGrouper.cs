using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>One cast reconstructed from its hits (all targets and sub-hits of the same use of a skill).</summary>
public sealed class CastInfo
{
    public uint Actor { get; init; }
    /// <summary>Exact (normalized) skill id of the first hit.</summary>
    public uint Skill { get; init; }
    public byte HitTag { get; init; }
    /// <summary>Seconds since the encounter start of the first hit (the landing time on the ribbon axis).</summary>
    public float StartT { get; internal set; }
    /// <summary>Seconds of the last hit that joined the cast.</summary>
    public float EndT { get; internal set; }
    /// <summary>Damage (or healing, when <see cref="IsHeal"/>) of all hits in the cast.</summary>
    public long Amount { get; internal set; }
    /// <summary>Hit records in the cast (all targets, extra hits are already part of each record's amount).</summary>
    public int Hits { get; internal set; }
    public int Crits { get; internal set; }
    public long MaxHit { get; internal set; }
    public bool FromSummon { get; internal set; }
    public bool IsHeal { get; internal set; }
    /// <summary>At least one hit was dodged/parried by the target.</summary>
    public int Avoided { get; internal set; }
    public bool Crit => Crits > 0;

    /// <summary>At least half of the cast's hits were critical (the ribbon rings these casts).</summary>
    public bool MostlyCrit => Crits > 0 && Crits * 2 >= Hits;
}

/// <summary>A DoT tick shown as a small mark under the ribbon.</summary>
public readonly record struct DotTickMark(float T, uint Skill, long Amount);

/// <summary>A combatant's rotation: casts in cast order plus DoT ticks.</summary>
public sealed class RotationData
{
    public static readonly RotationData Empty = new([], []);

    public RotationData(IReadOnlyList<CastInfo> casts, IReadOnlyList<DotTickMark> dotTicks)
    {
        Casts = casts;
        DotTicks = dotTicks;
    }

    public IReadOnlyList<CastInfo> Casts { get; }
    public IReadOnlyList<DotTickMark> DotTicks { get; }
    public long TotalDamage => Casts.Where(c => !c.IsHeal).Sum(c => c.Amount) + DotTicks.Sum(d => d.Amount);
}

/// <summary>
/// Reconstructs casts from <see cref="HitRecord"/>s. Hits of one actor with the same (skill, hit tag) that land within
/// <see cref="DefaultWindowSeconds"/> of the cast's first hit form one cast (the hit tag is a per-cast sequence byte,
/// PROTOCOL.md §8.2). DoT ticks are returned separately; incoming hits are ignored.
/// </summary>
public static class CastGrouper
{
    public const double DefaultWindowSeconds = 1.5;

    public static RotationData Build(IEnumerable<HitRecord> hits, uint actor,
        double windowSeconds = DefaultWindowSeconds, bool includeHeals = true, bool includeIncoming = false)
    {
        var casts = new List<CastInfo>();
        var dots = new List<DotTickMark>();
        var open = new Dictionary<(uint Skill, byte Tag), CastInfo>();

        // Records are stored in time order; a stable sort keeps that guarantee for hand-made inputs.
        foreach (var h in hits.Where(h => h.Actor == actor).OrderBy(h => h.T))
        {
            if (!includeIncoming && (h.Flags & HitFlags.Incoming) != 0) continue;
            if ((h.Flags & HitFlags.Dot) != 0)
            {
                dots.Add(new DotTickMark(h.T, h.Skill, h.Amount));
                continue;
            }
            bool heal = (h.Flags & HitFlags.Heal) != 0;
            if (heal && !includeHeals) continue;

            var key = (h.Skill, h.HitTag);
            if (!open.TryGetValue(key, out var cast) || h.T - cast.StartT > windowSeconds || cast.IsHeal != heal)
            {
                cast = new CastInfo { Actor = actor, Skill = h.Skill, HitTag = h.HitTag, StartT = h.T, EndT = h.T, IsHeal = heal };
                open[key] = cast;
                casts.Add(cast);
            }
            cast.EndT = Math.Max(cast.EndT, h.T);
            cast.Amount += h.Amount;
            cast.Hits++;
            if ((h.Flags & HitFlags.Crit) != 0) cast.Crits++;
            if ((h.Flags & (HitFlags.Dodged | HitFlags.Parry)) != 0) cast.Avoided++;
            if ((h.Flags & HitFlags.Summon) != 0) cast.FromSummon = true;
            if (h.Amount > cast.MaxHit) cast.MaxHit = h.Amount;
        }
        return new RotationData(casts, dots);
    }

    /// <summary>Counts casts per skill group (for tables when <see cref="SkillStats.Casts"/> is not filled in).</summary>
    public static Dictionary<uint, int> CountByGroup(RotationData rotation, Func<uint, uint> groupKey)
    {
        var result = new Dictionary<uint, int>();
        foreach (var c in rotation.Casts)
        {
            uint k = groupKey(c.Skill);
            result[k] = result.TryGetValue(k, out var n) ? n + 1 : 1;
        }
        return result;
    }
}
