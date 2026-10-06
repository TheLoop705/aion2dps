using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>Pure data transforms behind the charts (testable without WPF).</summary>
public static class SeriesMath
{
    /// <summary>
    /// Trailing rolling DPS: value[i] = damage in seconds (i − window + 1 … i) / min(window, i + 1).
    /// The divisor shrinks at the start so the first seconds show the real average instead of a ramp.
    /// </summary>
    /// <param name="perSecond">Damage per whole second (index = second).</param>
    /// <param name="windowSeconds">Window length (≥ 1).</param>
    /// <param name="length">Output length (pads with zeros after the data); defaults to the input length.</param>
    public static double[] RollingDps(IReadOnlyList<long> perSecond, int windowSeconds = 5, int? length = null)
    {
        int w = Math.Max(1, windowSeconds);
        int n = Math.Max(0, length ?? perSecond.Count);
        var result = new double[n];
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += At(perSecond, i);
            if (i - w >= 0) sum -= At(perSecond, i - w);
            result[i] = (double)sum / Math.Min(w, i + 1);
        }
        return result;
    }

    /// <summary>Running total: value[i] = damage in seconds 0 … i.</summary>
    public static long[] Cumulative(IReadOnlyList<long> perSecond, int? length = null)
    {
        int n = Math.Max(0, length ?? perSecond.Count);
        var result = new long[n];
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += At(perSecond, i);
            result[i] = sum;
        }
        return result;
    }

    /// <summary>Stacks series bottom-up: result[k][i] = Σ series[0..k][i] (the top edge of layer k).</summary>
    public static long[][] Stack(IReadOnlyList<IReadOnlyList<long>> series, int length)
    {
        var result = new long[series.Count][];
        var running = new long[Math.Max(0, length)];
        for (int k = 0; k < series.Count; k++)
        {
            var top = new long[running.Length];
            for (int i = 0; i < running.Length; i++)
            {
                running[i] += At(series[k], i);
                top[i] = running[i];
            }
            result[k] = top;
        }
        return result;
    }

    /// <summary>Buckets amounts into whole seconds: result[floor(T)] += Amount for hits accepted by <paramref name="filter"/>.</summary>
    public static long[] PerSecond(IEnumerable<HitRecord> hits, Func<HitRecord, bool> filter, int length)
    {
        var result = new long[Math.Max(0, length)];
        foreach (var h in hits)
        {
            if (!filter(h)) continue;
            int i = (int)Math.Floor(h.T);
            if (i < 0) i = 0;
            if (i >= result.Length) continue;
            result[i] += h.Amount;
        }
        return result;
    }

    /// <summary>Median of the values (NaN when empty).</summary>
    public static double Median(IEnumerable<double> values)
    {
        var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    /// <summary>"Nice" axis ticks (steps of 1/2/2.5/5 × 10ⁿ) from 0 to at least <paramref name="max"/>.</summary>
    public static IReadOnlyList<double> NiceTicks(double max, int targetCount, out double niceMax)
    {
        if (double.IsNaN(max) || max <= 0) max = 1;
        targetCount = Math.Max(2, targetCount);
        double rough = max / targetCount;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double norm = rough / mag;
        double step = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10;
        step *= mag;
        niceMax = Math.Ceiling(max / step - 1e-9) * step;
        var ticks = new List<double>();
        for (double v = 0; v <= niceMax + step * 1e-6; v += step) ticks.Add(v);
        return ticks;
    }

    /// <summary>Picks a time-axis step (seconds) so labels stay at least <paramref name="minPixels"/> apart.</summary>
    public static double TimeStep(double durationSeconds, double widthPixels, double minPixels = 64)
    {
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        if (widthPixels <= 0 || durationSeconds <= 0) return 30;
        foreach (var s in steps)
            if (durationSeconds / s * minPixels <= widthPixels) return s;
        return steps[^1];
    }

    /// <summary>
    /// Times where the boss went back to full HP (≥ <paramref name="fullThreshold"/> of max) after having been below
    /// <paramref name="lowThreshold"/> — the wipe/reset rule of PROTOCOL.md (99.5 % after &lt; 90 %).
    /// </summary>
    public static IReadOnlyList<float> DetectResets(IReadOnlyList<HpSample> samples, long maxHp,
        double fullThreshold = 0.995, double lowThreshold = 0.90)
    {
        var result = new List<float>();
        if (maxHp <= 0) return result;
        bool wasLow = false;
        foreach (var s in samples)
        {
            double f = (double)s.Hp / maxHp;
            if (f < lowThreshold) wasLow = true;
            else if (wasLow && f >= fullThreshold)
            {
                result.Add(s.T);
                wasLow = false;
            }
        }
        return result;
    }

    private static long At(IReadOnlyList<long> list, int i) => i >= 0 && i < list.Count ? list[i] : 0;
}
