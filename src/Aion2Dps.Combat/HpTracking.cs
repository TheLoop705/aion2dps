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
/// HP self-test (§11.4): decoded player damage to the boss vs HP lost plus boss self-heals, per window between HP readings.
/// Windows spanning a gap ≥ 3 s are ignored; HP frozen ≥ 3 s while being hit is a shield phase and is ignored too.
/// </summary>
internal sealed class HpCheckTracker
{
    private const double GapSeconds = 3.0;

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
    public int ExcludedGaps { get; private set; }
    public int ExcludedShield { get; private set; }

    public void OnDamage(long amount)
    {
        DecodedTotal += amount;
        if (_hasReading) _winDamage += amount;
    }

    public void OnSelfHeal(long amount)
    {
        SelfHealTotal += amount;
        if (_hasReading) _winHeal += amount;
    }

    public void OnReading(DateTime t, long hp)
    {
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

    public HpCheckResult? ToResult()
    {
        if (Ratio is not double ratio) return null;
        var notes = new List<string>();
        if (ExcludedGaps > 0) notes.Add($"{ExcludedGaps} window(s) skipped for HP gaps ≥ 3 s");
        if (ExcludedShield > 0) notes.Add($"{ExcludedShield} shield phase(s) skipped");
        if (DecodedTotal != CountedDamage) notes.Add($"{DecodedTotal - CountedDamage} decoded damage outside measured windows");
        return new HpCheckResult
        {
            DecodedDamage = CountedDamage,
            HpLost = CountedHpLost,
            BossSelfHealing = CountedHeal,
            Ratio = ratio,
            Passed = Math.Abs(ratio - 1.0) <= 0.01,
            Note = notes.Count > 0 ? string.Join("; ", notes) : null,
        };
    }
}
