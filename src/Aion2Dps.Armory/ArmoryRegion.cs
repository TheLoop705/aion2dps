namespace Aion2Dps.Armory;

/// <summary>
/// The character-info "regions" of the official AION 2 web sites. The first five are the Global (Steam / NC America /
/// NC Japan) service on <c>aion2.plaync.com/en-us</c>, which partitions characters by a <c>region</c> query parameter.
/// Korea and Taiwan are separate services with their own server lists.
/// </summary>
public enum ArmoryRegion
{
    /// <summary>Global, region code <c>eu</c> (server ids 1301-1399 Elyos / 2301-2399 Asmodian).</summary>
    Europe,
    /// <summary>Global, region code <c>nae</c>.</summary>
    NorthAmericaEast,
    /// <summary>Global, region code <c>naw</c>.</summary>
    NorthAmericaWest,
    /// <summary>Global, region code <c>la</c>.</summary>
    SouthAmerica,
    /// <summary>Global, region code <c>as</c>.</summary>
    Asia,
    /// <summary>Korean service (aion2.plaync.com/ko-kr). Names and stats come back in Korean.</summary>
    Korea,
    /// <summary>Taiwanese service (tw.ncsoft.com/aion2). Names come back in Traditional Chinese.</summary>
    Taiwan,
}

public static class ArmoryRegionExtensions
{
    /// <summary>All regions in display order.</summary>
    public static IReadOnlyList<ArmoryRegion> All { get; } =
    [
        ArmoryRegion.Europe, ArmoryRegion.NorthAmericaEast, ArmoryRegion.NorthAmericaWest,
        ArmoryRegion.SouthAmerica, ArmoryRegion.Asia, ArmoryRegion.Korea, ArmoryRegion.Taiwan,
    ];

    /// <summary>True for the five Global (Steam) regions.</summary>
    public static bool IsGlobal(this ArmoryRegion region) => region <= ArmoryRegion.Asia;

    /// <summary>The <c>region</c> query value the Global site uses ("eu", "nae", ...), or null for KR/TW.</summary>
    public static string? Code(this ArmoryRegion region) => region switch
    {
        ArmoryRegion.Europe => "eu",
        ArmoryRegion.NorthAmericaEast => "nae",
        ArmoryRegion.NorthAmericaWest => "naw",
        ArmoryRegion.SouthAmerica => "la",
        ArmoryRegion.Asia => "as",
        _ => null,
    };

    public static string DisplayName(this ArmoryRegion region) => region switch
    {
        ArmoryRegion.Europe => "Global - Europe",
        ArmoryRegion.NorthAmericaEast => "Global - North America East",
        ArmoryRegion.NorthAmericaWest => "Global - North America West",
        ArmoryRegion.SouthAmerica => "Global - South America",
        ArmoryRegion.Asia => "Global - Asia",
        ArmoryRegion.Korea => "Korea",
        ArmoryRegion.Taiwan => "Taiwan",
        _ => region.ToString(),
    };

    /// <summary>Parses a Global region code ("eu", "NAE", ...). Returns null when unknown.</summary>
    public static ArmoryRegion? FromCode(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "eu" => ArmoryRegion.Europe,
        "nae" => ArmoryRegion.NorthAmericaEast,
        "naw" => ArmoryRegion.NorthAmericaWest,
        "la" => ArmoryRegion.SouthAmerica,
        "as" => ArmoryRegion.Asia,
        "kr" => ArmoryRegion.Korea,
        "tw" => ArmoryRegion.Taiwan,
        _ => null,
    };

    /// <summary>
    /// Best guess of the Global region from a game server id (the third digit encodes the region on Global:
    /// x1xx = NA-East, x2xx = NA-West, x3xx = EU, x4xx = South America, x5xx = Asia; first digit 1 = Elyos, 2 = Asmodian),
    /// as seen in the live Global server list (October 2026). KR/TW ids (10xx/20xx) return null.
    /// </summary>
    public static ArmoryRegion? GuessGlobalRegionFromServerId(int serverId)
    {
        if (serverId < 1100 || serverId > 2599) return null;
        return (serverId / 100 % 10) switch
        {
            1 => ArmoryRegion.NorthAmericaEast,
            2 => ArmoryRegion.NorthAmericaWest,
            3 => ArmoryRegion.Europe,
            4 => ArmoryRegion.SouthAmerica,
            5 => ArmoryRegion.Asia,
            _ => null,
        };
    }
}
