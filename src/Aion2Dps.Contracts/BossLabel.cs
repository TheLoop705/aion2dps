using System.Globalization;

namespace Aion2Dps.Contracts;

/// <summary>Display fallbacks for a boss whose NPC code is unknown (its spawn record was missed).</summary>
public static class BossLabel
{
    /// <summary>"World boss (45.6M HP)" (open world), "Boss (45.6M HP)" (elsewhere), or null when the max HP is unknown too.</summary>
    public static string? Unnamed(long? maxHp, bool openWorld)
    {
        if (maxHp is not long max || max <= 0) return null;
        return $"{(openWorld ? "World boss" : "Boss")} ({Hp(max)} HP)";
    }

    /// <summary>12.3K, 4.56M, 45.6M, 1.20B.</summary>
    public static string Hp(long v)
    {
        var inv = CultureInfo.InvariantCulture;
        double a = Math.Abs((double)v);
        if (a >= 1e9) return (v / 1e9).ToString("0.00", inv) + "B";
        if (a >= 1e8) return (v / 1e6).ToString("0", inv) + "M";
        if (a >= 1e7) return (v / 1e6).ToString("0.0", inv) + "M";
        if (a >= 1e6) return (v / 1e6).ToString("0.00", inv) + "M";
        if (a >= 1e4) return (v / 1e3).ToString("0.0", inv) + "K";
        return v.ToString(inv);
    }

    /// <summary>"Open world (101007)" for an overworld map id without a name, else "map 320060".</summary>
    public static string UnnamedMap(uint mapId, bool openWorld) =>
        openWorld ? $"Open world ({mapId})" : $"map {mapId}";
}
