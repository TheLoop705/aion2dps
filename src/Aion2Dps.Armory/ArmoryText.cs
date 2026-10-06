using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Aion2Dps.Armory;

/// <summary>Text helpers: markup stripping and number formatting for the armory UI.</summary>
public static partial class ArmoryText
{
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTag();

    [GeneratedRegex(@"<[^>]{0,200}>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t ]{2,}")]
    private static partial Regex Spaces();

    /// <summary>Removes HTML tags (search highlights <c>&lt;strong&gt;</c>, <c>&lt;desc_point&gt;</c>), decodes entities and
    /// trims. Line breaks (\n and &lt;br&gt;) are kept as \n.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s;
        if (t.Contains('<'))
        {
            t = BrTag().Replace(t, "\n");
            t = AnyTag().Replace(t, "");
        }
        if (t.Contains('&')) t = WebUtility.HtmlDecode(t);
        t = t.Replace("\r\n", "\n").Replace('\r', '\n');
        t = Spaces().Replace(t, " ");
        return t.Trim();
    }

    /// <summary>12.3K / 4.56M style abbreviation used across the app.</summary>
    public static string Abbreviate(long? value)
    {
        if (value is null) return "—";
        var v = value.Value;
        var a = Math.Abs((double)v);
        string Fmt(double x, string suffix)
        {
            var digits = x >= 100 ? 0 : x >= 10 ? 1 : 2;
            return (v < 0 ? "-" : "") + Math.Round(x, digits, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture) + suffix;
        }
        if (a >= 1_000_000_000) return Fmt(a / 1_000_000_000, "B");
        if (a >= 1_000_000) return Fmt(a / 1_000_000, "M");
        if (a >= 10_000) return Fmt(a / 1_000, "K");
        return v.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string Exact(long? value) => value is null ? "—" : value.Value.ToString("N0", CultureInfo.InvariantCulture);
}
