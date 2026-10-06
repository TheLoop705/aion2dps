namespace Aion2Dps.Combat;

/// <summary>Boss HP over time. Records a sample at most every 250 ms; the latest reading in between is kept as pending.</summary>
internal sealed class HpTimeline
{
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(250);
    private readonly List<(DateTime T, long Hp)> _samples = new();
    private (DateTime T, long Hp)? _pending;

    public int Count => _samples.Count + (_pending is null ? 0 : 1);

    public void Add(DateTime t, long hp)
    {
        if (_samples.Count == 0 || t - _samples[^1].T >= MinSpacing)
        {
            _samples.Add((t, hp));
            _pending = null;
        }
        else
        {
            _pending = (t, hp);
        }
    }

    public List<HpSample> ToSamples(DateTime origin)
    {
        var list = new List<HpSample>(Count);
        foreach (var (t, hp) in _samples) list.Add(new HpSample((float)(t - origin).TotalSeconds, hp));
        if (_pending is { } p) list.Add(new HpSample((float)(p.T - origin).TotalSeconds, p.Hp));
        return list;
    }
}

/// <summary>
/// HP self-test (§11.4): decoded player damage to the boss vs HP lost plus healing received, per window between HP readings.
/// Windows spanning a gap ≥ 3 s are ignored; HP frozen ≥ 3 s while being hit is a shield phase and is ignored too.
/// </summary>
internal sealed class HpCheckTracker
{
    private const double GapSeconds = 3.0;

    private enum MeasurementKind : byte { Damage, Heal, Reading, Invalidate }
    private readonly record struct Measurement(MeasurementKind Kind, DateTime Time, long Amount, Hit? Hit = null, long? MaxHp = null);
    // Preserve the original processing order: ownership may be learned after an HP window already closed.
    private readonly List<Measurement> _measurements = new();
    private bool _replaying;

    private bool _hasReading;
    private DateTime _lastT;
    private long _lastHp;
    private long _winDamage;
    private long _winHeal;
    private DateTime? _frozenStart;
    private long _frozenDamage;

    public long DecodedTotal { get; private set; }
    public long SelfHealTotal { get; private set; }
    public long CountedDamage { get; private set; }
    public long CountedHpLost { get; private set; }
    public long CountedHeal { get; private set; }
    public int Windows { get; private set; }
    /// <summary>Damage beyond the remaining HP in the killing window (excluded from the ratio).</summary>
    public long Overkill { get; private set; }
    public int ExcludedGaps { get; private set; }
    public int ExcludedShield { get; private set; }

    public void OnDamage(Hit hit)
    {
        if (!_replaying) _measurements.Add(new Measurement(MeasurementKind.Damage, default, hit.Amount, hit));
        long amount = hit.Amount;
        DecodedTotal += amount;
        if (_hasReading) _winDamage += amount;
    }

    public void OnSelfHeal(long amount, long? trustedMaxHp = null)
    {
        if (!_replaying) _measurements.Add(new Measurement(MeasurementKind.Heal, default, amount, MaxHp: trustedMaxHp));
        if (_hasReading && trustedMaxHp is long max && max > 0 && _lastHp <= max)
        {
            // NPC heal packets include overheal (Draupnir's Naga Priest casts 225,000 at nearly full HP).
            // Only the room before this heal can restore HP. Use the preceding reading and ordered damage/heals,
            // rather than deriving healing from the next HP reading, so the check remains independent.
            long room = Math.Max(0, max - _lastHp + _winDamage - _winHeal);
            amount = Math.Min(amount, room);
        }
        SelfHealTotal += amount;
        if (_hasReading) _winHeal += amount;
    }

    /// <summary>
    /// Drops the current window: the next reading starts a new one. Used when the HP jumps for a reason that is not
    /// damage or a recorded self-heal (party-size max-HP rescale, boss reset).
    /// </summary>
    public void Invalidate()
    {
        if (!_replaying) _measurements.Add(new Measurement(MeasurementKind.Invalidate, default, 0));
        if (!_hasReading) return;
        _hasReading = false;
        _winDamage = 0;
        _winHeal = 0;
        DropFrozen();
        Invalidations++;
    }

    public int Invalidations { get; private set; }

    public void OnReading(DateTime t, long hp)
    {
        if (!_replaying) _measurements.Add(new Measurement(MeasurementKind.Reading, t, hp));
        if (!_hasReading)
        {
            _hasReading = true;
            _lastT = t;
            _lastHp = hp;
            _winDamage = 0;
            _winHeal = 0;
            return;
        }

        double gap = (t - _lastT).TotalSeconds;
        long delta = _lastHp - hp;
        if (gap >= GapSeconds)
        {
            ExcludedGaps++;
            DropFrozen();
        }
        else if (delta == 0 && _winDamage > 0 && _winHeal == 0)
        {
            _frozenStart ??= _lastT;
            _frozenDamage += _winDamage;
        }
        else
        {
            CloseFrozen(t);
            if (hp == 0 && delta >= 0 && _winDamage > delta + _winHeal)
            {
                // Killing window: several hits landed on the last HP at once. The excess is overkill, not a decoder
                // error (real Fire Temple capture: 7,132 on Black Smoke Murute), so it is reported but not counted.
                long over = _winDamage - (delta + _winHeal);
                Overkill += over;
                _winDamage -= over;
            }
            CountedDamage += _winDamage;
            CountedHpLost += delta;
            CountedHeal += _winHeal;
            Windows++;
        }
        _winDamage = 0;
        _winHeal = 0;
        _lastT = t;
        _lastHp = hp;
    }

    /// <summary>Recalculates the measured windows after a provisional friendly summon is identified as hostile.</summary>
    public void RemoveDamage(IReadOnlySet<Hit> discarded)
    {
        if (_measurements.RemoveAll(m => m.Hit is { } hit && discarded.Contains(hit)) == 0) return;
        _hasReading = false;
        _lastT = default;
        _lastHp = _winDamage = _winHeal = 0;
        DropFrozen();
        DecodedTotal = SelfHealTotal = CountedDamage = CountedHpLost = CountedHeal = Overkill = 0;
        Windows = ExcludedGaps = ExcludedShield = Invalidations = 0;
        _replaying = true;
        try
        {
            foreach (var m in _measurements)
                switch (m.Kind)
                {
                    case MeasurementKind.Damage: OnDamage(m.Hit!); break;
                    case MeasurementKind.Heal: OnSelfHeal(m.Amount, m.MaxHp); break;
                    case MeasurementKind.Reading: OnReading(m.Time, m.Amount); break;
                    case MeasurementKind.Invalidate: Invalidate(); break;
                }
        }
        finally { _replaying = false; }
    }

    private void CloseFrozen(DateTime t)
    {
        if (_frozenStart is not { } fs) return;
        if ((t - fs).TotalSeconds >= GapSeconds) ExcludedShield++;
        else CountedDamage += _frozenDamage;
        DropFrozen();
    }

    private void DropFrozen()
    {
        _frozenStart = null;
        _frozenDamage = 0;
    }

    public double? Ratio
    {
        get
        {
            long denom = CountedHpLost + CountedHeal;
            if (Windows == 0 || denom <= 0) return null;
            return (double)CountedDamage / denom;
        }
    }

    public HpCheckResult? ToResult() => Combine(new[] { this });

    /// <summary>Live ratio summed over several trackers (multi-boss encounters).</summary>
    public static double? CombinedRatio(IEnumerable<HpCheckTracker> trackers)
    {
        long damage = 0, lost = 0, heal = 0;
        int windows = 0;
        foreach (var t in trackers)
        {
            damage += t.CountedDamage;
            lost += t.CountedHpLost;
            heal += t.CountedHeal;
            windows += t.Windows;
        }
        long denom = lost + heal;
        if (windows == 0 || denom <= 0) return null;
        return (double)damage / denom;
    }

    /// <summary>One result summed over several trackers (one per boss); null when no window was measured.</summary>
    public static HpCheckResult? Combine(IReadOnlyCollection<HpCheckTracker> trackers)
    {
        if (CombinedRatio(trackers) is not double ratio) return null;
        long damage = 0, lost = 0, heal = 0, decoded = 0, overkill = 0;
        int gaps = 0, shields = 0, invalidations = 0;
        foreach (var t in trackers)
        {
            damage += t.CountedDamage;
            lost += t.CountedHpLost;
            heal += t.CountedHeal;
            decoded += t.DecodedTotal;
            gaps += t.ExcludedGaps;
            shields += t.ExcludedShield;
            invalidations += t.Invalidations;
            overkill += t.Overkill;
        }
        var notes = new List<string>();
        if (gaps > 0) notes.Add($"{gaps} window(s) skipped for HP gaps ≥ 3 s");
        if (shields > 0) notes.Add($"{shields} shield phase(s) skipped");
        if (invalidations > 0) notes.Add($"{invalidations} window(s) skipped for max-HP rescales / resets");
        if (overkill > 0) notes.Add($"{overkill} overkill on the killing blow excluded");
        if (decoded - overkill != damage) notes.Add($"{decoded - overkill - damage} decoded damage outside measured windows");
        return new HpCheckResult
        {
            DecodedDamage = damage,
            HpLost = lost,
            BossSelfHealing = heal,
            Ratio = ratio,
            Passed = Math.Abs(ratio - 1.0) <= 0.01,
            Overkill = overkill,
            Note = notes.Count > 0 ? string.Join("; ", notes) : null,
        };
    }
}
