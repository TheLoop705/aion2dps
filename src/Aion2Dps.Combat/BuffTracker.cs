namespace Aion2Dps.Combat;

/// <summary>Buff intervals per (target, buff) from <c>2A 38</c>/<c>2B 38</c> and <c>0E 92</c> (§8.13). Uptime is computed per encounter.</summary>
internal sealed class BuffTracker
{
    private const int MaxIntervals = 50_000;
    private const long MaxPlannedMs = 24L * 3600 * 1000;

    internal sealed class Interval
    {
        public uint Target;
        public uint BuffId;
        public uint Caster;
        public DateTime Start;
        /// <summary>Planned or actual end; null = permanent / unknown until removed.</summary>
        public DateTime? End;
    }

    private readonly Dictionary<(uint Target, uint Buff), Interval> _active = new();
    private readonly List<Interval> _all = new();

    public int Count => _all.Count;

    public void OnApplied(BuffAppliedEvent e, long? serverClockOffsetMs)
    {
        var t = e.Time;
        if (_active.TryGetValue((e.Target, e.BuffId), out var prev) && (prev.End is null || prev.End > t)) prev.End = t;

        DateTime? end = null;
        // Real traffic carries implausible durations/expiries (permanent auras, misaligned optional bytes): anything
        // beyond a day is treated as "until removed" instead of overflowing DateTime arithmetic.
        if (e.DurationMs != uint.MaxValue)
        {
            if (e.DurationMs > 0)
            {
                if (e.DurationMs <= MaxPlannedMs) end = t.AddMilliseconds(e.DurationMs);
            }
            else if (e.ExpiryUnixMs > 0 && serverClockOffsetMs is long off)
            {
                long localMs = (long)Math.Min(e.ExpiryUnixMs, long.MaxValue / 2) - off;
                long nowMs = (long)(t - DateTime.UnixEpoch).TotalMilliseconds;
                if (localMs > nowMs && localMs - nowMs <= MaxPlannedMs) end = t.AddMilliseconds(localMs - nowMs);
            }
        }
        var iv = new Interval { Target = e.Target, BuffId = e.BuffId, Caster = e.Caster, Start = t, End = end };
        _active[(e.Target, e.BuffId)] = iv;
        _all.Add(iv);
        if (_all.Count > MaxIntervals) Prune(t - TimeSpan.FromMinutes(15));
    }

    public void OnRemoved(BuffRemovedEvent e)
    {
        if (_active.Remove((e.Target, e.BuffId), out var iv) && (iv.End is null || iv.End > e.Time)) iv.End = e.Time;
    }

    private void Prune(DateTime before)
    {
        _all.RemoveAll(iv => iv.End is { } end && end < before);
        if (_all.Count > MaxIntervals) _all.RemoveRange(0, _all.Count - MaxIntervals);
    }

    /// <summary>Uptime of every buff on <paramref name="target"/> within [start, start + duration].</summary>
    public List<BuffUptime> Compute(uint target, DateTime start, double durationSeconds)
    {
        var result = new List<BuffUptime>();
        DateTime end = start.AddSeconds(durationSeconds);
        Dictionary<uint, List<Interval>>? byBuff = null;
        foreach (var iv in _all)
        {
            if (iv.Target != target) continue;
            var ivEnd = iv.End ?? end;
            if (iv.Start > end || ivEnd < start) continue;
            byBuff ??= new();
            if (!byBuff.TryGetValue(iv.BuffId, out var list)) byBuff[iv.BuffId] = list = new();
            list.Add(iv);
        }
        if (byBuff == null) return result;

        foreach (var (buffId, list) in byBuff)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            double seconds = 0;
            DateTime? curS = null, curE = null;
            foreach (var iv in list)
            {
                var s = iv.Start < start ? start : iv.Start;
                var e = iv.End ?? end;
                if (e > end) e = end;
                if (e < s) e = s;
                if (curS is null) { curS = s; curE = e; continue; }
                if (s <= curE) { if (e > curE) curE = e; }
                else
                {
                    seconds += (curE!.Value - curS.Value).TotalSeconds;
                    curS = s;
                    curE = e;
                }
            }
            if (curS is not null) seconds += (curE!.Value - curS.Value).TotalSeconds;
            result.Add(new BuffUptime
            {
                BuffId = buffId,
                SkillId = buffId / 10,
                Applications = list.Count,
                UptimeSeconds = seconds,
                Uptime = durationSeconds > 0 ? Math.Clamp(seconds / durationSeconds, 0, 1) : 0,
                CasterEntityId = list[^1].Caster == 0 ? null : list[^1].Caster,
            });
        }
        result.Sort((a, b) => b.UptimeSeconds.CompareTo(a.UptimeSeconds));
        return result;
    }

    public void Clear()
    {
        _active.Clear();
        _all.Clear();
    }
}
