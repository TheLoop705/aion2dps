namespace Aion2Dps.App.Formatting;

/// <summary>
/// Number/time formatting used everywhere in the UI. Always culture-invariant so the overlay looks the same on every
/// system locale ("12.3K", "1,002,252").
/// </summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The dash shown for unmeasured values (never "0 %").</summary>
    public const string Dash = "—";

    /// <summary>
    /// Abbreviates to three significant digits: 999 → "999", 1234 → "1.23K", 12345 → "12.3K", 123456 → "123K",
    /// 4_560_000 → "4.56M", 1_230_000_000 → "1.23B". Values that round up across a unit boundary move to the next unit
    /// (999_999 → "1.00M").
    /// </summary>
    public static string Abbrev(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return Dash;
        if (value < 0) return "-" + Abbrev(-value);
        if (value < 1000)
        {
            double r = Math.Round(value, MidpointRounding.AwayFromZero);
            return r >= 1000 ? "1.00K" : r.ToString("0", Inv);
        }

        ReadOnlySpan<(double Div, string Suffix)> units = [(1e3, "K"), (1e6, "M"), (1e9, "B"), (1e12, "T")];
        for (int i = 0; i < units.Length; i++)
        {
            double scaled = value / units[i].Div;
            string text = scaled switch
            {
                < 10 => Math.Round(scaled, 2, MidpointRounding.AwayFromZero).ToString("0.00", Inv),
                < 100 => Math.Round(scaled, 1, MidpointRounding.AwayFromZero).ToString("0.0", Inv),
                _ => Math.Round(scaled, 0, MidpointRounding.AwayFromZero).ToString("0", Inv),
            };
            bool overflow = double.Parse(text, Inv) >= 1000;
            if (overflow && i < units.Length - 1) continue;
            return text + units[i].Suffix;
        }
        return value.ToString("0", Inv);
    }

    /// <summary>Abbreviation with one decimal (chat lines): 554_612 → "554.6K", 1_234_567 → "1.2M", 950 → "950".</summary>
    public static string Abbrev1(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return Dash;
        if (value < 0) return "-" + Abbrev1(-value);
        if (value < 1000) return Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Inv);
        ReadOnlySpan<(double Div, string Suffix)> units = [(1e3, "K"), (1e6, "M"), (1e9, "B"), (1e12, "T")];
        for (int i = 0; i < units.Length; i++)
        {
            double r = Math.Round(value / units[i].Div, 1, MidpointRounding.AwayFromZero);
            if (r >= 1000 && i < units.Length - 1) continue;
            return r.ToString("0.0", Inv) + units[i].Suffix;
        }
        return value.ToString("0", Inv);
    }

    /// <summary>Exact grouped integer, e.g. "1,002,252".</summary>
    public static string Exact(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? Dash : Math.Round(value, MidpointRounding.AwayFromZero).ToString("N0", Inv);

    public static string Exact(long value) => value.ToString("N0", Inv);

    /// <summary>0..1 → "34.8%". Null → dash.</summary>
    public static string Percent(double? fraction, int decimals = 1)
    {
        if (fraction is not { } f || double.IsNaN(f) || double.IsInfinity(f)) return Dash;
        return (f * 100).ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), Inv) + "%";
    }

    /// <summary>A rate with a denominator: dash when nothing was measured (never "0%").</summary>
    public static string Rate(int numerator, int denominator, int decimals = 1) =>
        denominator <= 0 ? Dash : Percent((double)numerator / denominator, decimals);

    /// <summary>"3:45", "0:07", "1:02:03".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        int totalSeconds = (int)Math.Floor(span.TotalSeconds);
        int h = totalSeconds / 3600, m = totalSeconds / 60 % 60, s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    public static string Duration(double seconds) => Duration(TimeSpan.FromSeconds(double.IsFinite(seconds) ? seconds : 0));

    /// <summary>"38 ms" or dash.</summary>
    public static string Ping(double? ms) => ms is { } v && double.IsFinite(v) ? $"{Math.Round(v).ToString("0", Inv)} ms" : Dash;

    /// <summary>Bytes as "12.3 MB".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(Inv) + " B";
        double v = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        int i = -1;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0", Inv) + " " + units[i];
    }

    /// <summary>"HP 1,002,252 · 19.2%" style text for the header HP bar.</summary>
    public static string HpText(long? hp, long? maxHp, double? fraction)
    {
        if (hp is null && fraction is null) return Dash;
        string hpPart = hp is { } h ? Exact(h) + " HP" : "";
        string pct = Percent(fraction ?? (hp is { } h2 && maxHp is > 0 ? (double)h2 / maxHp.Value : null));
        return hpPart.Length == 0 ? pct : $"{hpPart} · {pct}";
    }

    /// <summary>Relative "time ago" label for history lists.</summary>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} d ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd", Inv);
    }
}
