using System.Globalization;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Number, duration and percentage formatting shared by every analysis view.
/// Big numbers are abbreviated (12.3K, 4.56M); exact values belong in tooltips (<see cref="Exact"/>).
/// Unmeasured values are shown as <see cref="Dash"/>, never as 0 %.
/// </summary>
public static class Fmt
{
    /// <summary>Placeholder for values the protocol did not report.</summary>
    public const string Dash = "—";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Abbreviates: 999 → "999", 12,345 → "12.3K", 4,560,000 → "4.56M", 1.2e9 → "1.20B".</summary>
    public static string Abbrev(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return Dash;
        double a = Math.Abs(value);
        string sign = value < 0 && a >= 0.5 ? "-" : "";
        if (a < 999.5) return sign + Math.Round(a, MidpointRounding.AwayFromZero).ToString("0", Inv);
        if (a < 999_950) return sign + (a / 1e3).ToString("0.0", Inv) + "K";
        if (a < 999_995_000) return sign + (a / 1e6).ToString("0.00", Inv) + "M";
        return sign + (a / 1e9).ToString("0.00", Inv) + "B";
    }

    /// <summary>Compact axis label without trailing zeros: 200,000,000 → "200M", 1,500,000 → "1.5M", 50,000 → "50K".</summary>
    public static string Axis(double value)
    {
        string s = Abbrev(value);
        if (s.Length < 2 || !char.IsLetter(s[^1]) || !s.Contains('.')) return s;
        string num = s[..^1].TrimEnd('0').TrimEnd('.');
        return num + s[^1];
    }

    /// <summary>"1 fight", "3 fights".</summary>
    public static string Count(long n, string singular, string? plural = null) =>
        n.ToString(Inv) + " " + (n == 1 ? singular : plural ?? singular + "s");

    /// <summary>Ratio with two decimals and a multiplication sign: 0.71 → "0.71×".</summary>
    public static string Ratio(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? Dash : value.ToString("0.00", Inv) + "×";

    /// <summary>Seconds with one decimal: "12.5 s".</summary>
    public static string Seconds(double value) => value.ToString("0.0", Inv) + " s";

    /// <summary>Abbreviated damage/heal amount.</summary>
    public static string Number(long value) => Abbrev(value);

    /// <summary>Abbreviated DPS value.</summary>
    public static string Dps(double dps) => Abbrev(dps);

    /// <summary>Exact value with thousands separators: 12,345,678.</summary>
    public static string Exact(long value) => value.ToString("N0", Inv);

    /// <summary>Exact value with thousands separators, rounded to an integer.</summary>
    public static string Exact(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? Dash : Math.Round(value).ToString("N0", Inv);

    /// <summary>Fight clock: 65.4 → "1:05", 3725 → "1:02:05". Negative/NaN → "0:00".</summary>
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        long total = (long)Math.Floor(seconds + 1e-6);
        long h = total / 3600, m = total / 60 % 60, s = total % 60;
        return h > 0
            ? h.ToString(Inv) + ":" + m.ToString("00", Inv) + ":" + s.ToString("00", Inv)
            : (total / 60).ToString(Inv) + ":" + s.ToString("00", Inv);
    }

    /// <summary>Clock with tenths for hover tooltips: 42.37 → "0:42.3".</summary>
    public static string PreciseTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        double tenths = Math.Floor(seconds * 10 + 1e-6) / 10;
        long whole = (long)Math.Floor(tenths);
        int frac = (int)Math.Round((tenths - whole) * 10);
        return Duration(whole) + "." + frac.ToString(Inv);
    }

    /// <summary>0..1 ratio → "12.3%". Null/NaN/∞ → "—".</summary>
    public static string Percent(double? ratio, int decimals = 1)
    {
        if (ratio is not { } r || double.IsNaN(r) || double.IsInfinity(r)) return Dash;
        return (r * 100).ToString("F" + Math.Clamp(decimals, 0, 4), Inv) + "%";
    }

    /// <summary>part / whole as a percentage, or "—" when nothing was measured (whole ≤ 0).</summary>
    public static string Rate(long part, long whole, int decimals = 1) => Percent(RateValue(part, whole), decimals);

    /// <summary>part / whole, or null when nothing was measured (whole ≤ 0).</summary>
    public static double? RateValue(long part, long whole) => whole <= 0 ? null : (double)part / whole;

    /// <summary>Relative time for history lists: "just now", "12m ago", "3h ago", "5d ago", then a date.</summary>
    public static string TimeAgo(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return ((int)d.TotalMinutes).ToString(Inv) + "m ago";
        if (d.TotalHours < 24) return ((int)d.TotalHours).ToString(Inv) + "h ago";
        if (d.TotalDays < 30) return ((int)d.TotalDays).ToString(Inv) + "d ago";
        return utc.ToLocalTime().ToString("d MMM yyyy", Inv);
    }

    /// <summary>Local date and time, e.g. "Tue 6 Oct 2026, 21:14".</summary>
    public static string LocalDateTime(DateTime utc) =>
        ToLocal(utc).ToString("ddd d MMM yyyy, HH:mm", Inv);

    /// <summary>Short local date, e.g. "6 Oct".</summary>
    public static string ShortDate(DateTime utc) => ToLocal(utc).ToString("d MMM", Inv);

    private static DateTime ToLocal(DateTime utc) =>
        utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    /// <summary>Human label for an encounter kind.</summary>
    public static string KindLabel(EncounterKind kind) => kind switch
    {
        EncounterKind.Boss => "Boss fight",
        EncounterKind.Trash => "Trash",
        EncounterKind.Dummy => "Training dummy",
        EncounterKind.Pvp => "PvP session",
        EncounterKind.Training => "Training run",
        _ => kind.ToString(),
    };

    /// <summary>Short badge text for an outcome.</summary>
    public static string OutcomeLabel(EncounterOutcome outcome) => outcome switch
    {
        EncounterOutcome.Kill => "KILL",
        EncounterOutcome.Wipe => "WIPE",
        EncounterOutcome.Timeout => "TIMEOUT",
        EncounterOutcome.ManualReset => "RESET",
        EncounterOutcome.ZoneChange => "LEFT",
        EncounterOutcome.InProgress => "LIVE",
        _ => outcome.ToString().ToUpperInvariant(),
    };

    /// <summary>ThemeKeys brush key that colours an outcome badge.</summary>
    public static string OutcomeBrushKey(EncounterOutcome outcome) => outcome switch
    {
        EncounterOutcome.Kill => ThemeKeys.Positive,
        EncounterOutcome.Wipe => ThemeKeys.Negative,
        EncounterOutcome.Timeout => ThemeKeys.Warning,
        EncounterOutcome.InProgress => ThemeKeys.Accent,
        _ => ThemeKeys.TextMuted,
    };
}
