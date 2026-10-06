using System.Globalization;
using System.Text;

namespace Aion2Dps.Armory;

/// <summary>
/// URL builder for the official AION 2 character-info endpoints, as used by NCSOFT's own pages
/// (<c>https://aion2.plaync.com/en-us/characters/index</c>, script <c>static-aion2/characters/js/index.js</c>).
/// Verified live on 2026-10-06. All endpoints are unauthenticated read-only GETs that return JSON.
/// <list type="table">
/// <item><term>Global servers</term><description><c>https://aion2.plaync.com/{locale}/api/gameinfo/servers?lang=en-US&amp;region=eu</c></description></item>
/// <item><term>Global classes</term><description><c>https://aion2.plaync.com/{locale}/api/gameinfo/classes?lang=en-US&amp;region=eu</c></description></item>
/// <item><term>Global pcdata</term><description><c>https://aion2.plaync.com/{locale}/api/gameinfo/pcdata?lang=en-US&amp;region=eu</c></description></item>
/// <item><term>Global search</term><description><c>https://api-search.plaync.com/aion2global/search/v2/character?keyword=..&amp;serverId=..&amp;page=1&amp;size=40&amp;region=eu&amp;localeInfo=en-US</c></description></item>
/// <item><term>KR search</term><description><c>https://api-search.plaync.com/aion2/search/v2/character?keyword=..&amp;page=1&amp;size=40</c></description></item>
/// <item><term>TW search</term><description><c>https://tw.ncsoft.com/aion2/api/search/character?keyword=..&amp;serverId=..&amp;race=..</c> (400 without server and race)</description></item>
/// <item><term>info</term><description><c>{host}/api/character/info?region=eu&amp;lang=en-US&amp;characterId=..&amp;serverId=..</c></description></item>
/// <item><term>equipment</term><description><c>{host}/api/character/equipment?…same…</c></description></item>
/// <item><term>item</term><description><c>{host}/api/character/equipment/item?id=..&amp;enchantLevel=..&amp;characterId=..&amp;serverId=..&amp;slotPos=..&amp;lang=en-US&amp;region=eu</c></description></item>
/// <item><term>daevanion</term><description><c>{host}/api/character/daevanion/detail?…&amp;boardId=..</c></description></item>
/// <item><term>server list fallback</term><description>the <c>_serverNameMap</c> JSON embedded in the characters index page (the KR/TW
/// <c>gameinfo/servers</c> API answers 404, so for those regions the page is the only source).</description></item>
/// </list>
/// {host} is <c>https://aion2.plaync.com</c> for Global and KR, <c>https://tw.ncsoft.com/aion2</c> for TW.
/// </summary>
public sealed class ArmoryEndpoints
{
    public const string GlobalSite = "https://aion2.plaync.com";
    public const string SearchHost = "https://api-search.plaync.com";
    public const string TaiwanSite = "https://tw.ncsoft.com";

    /// <summary>Language sent to the Global endpoints (en-US, de-DE, fr-FR, es-ES, ja-JP, pt-BR).</summary>
    public string GlobalLanguage { get; }

    public ArmoryEndpoints(string globalLanguage = "en-US")
    {
        GlobalLanguage = string.IsNullOrWhiteSpace(globalLanguage) ? "en-US" : globalLanguage.Trim();
    }

    /// <summary>"/en-us" for en-US, etc.</summary>
    public string GlobalLocalePath => "/" + GlobalLanguage.ToLowerInvariant();

    /// <summary>The <c>lang</c> parameter for a region.</summary>
    public string Lang(ArmoryRegion region) => region switch
    {
        ArmoryRegion.Korea => "ko",
        ArmoryRegion.Taiwan => "zh",
        _ => GlobalLanguage,
    };

    /// <summary>Base for the character endpoints (info / equipment / item / daevanion).</summary>
    public string CharacterApiBase(ArmoryRegion region) => region == ArmoryRegion.Taiwan ? TaiwanSite + "/aion2" : GlobalSite;

    /// <summary>The web page that lists characters (and embeds <c>_serverNameMap</c>).</summary>
    public Uri CharactersPage(ArmoryRegion region) => region switch
    {
        ArmoryRegion.Korea => new Uri(GlobalSite + "/ko-kr/characters/index"),
        ArmoryRegion.Taiwan => new Uri(TaiwanSite + "/aion2/characters/index"),
        _ => new Uri(GlobalSite + GlobalLocalePath + "/characters/index"),
    };

    /// <summary>Profile page of a character on the official site (for an "open in browser" link).</summary>
    public Uri CharacterPage(ArmoryRegion region, int serverId, string characterId)
    {
        var id = Uri.EscapeDataString(characterId);
        var baseUrl = region switch
        {
            ArmoryRegion.Korea => GlobalSite + "/ko-kr/characters",
            ArmoryRegion.Taiwan => TaiwanSite + "/aion2/characters",
            _ => GlobalSite + GlobalLocalePath + "/characters",
        };
        var url = $"{baseUrl}/{serverId.ToString(CultureInfo.InvariantCulture)}/{id}";
        if (region.Code() is { } code) url += "?region=" + code;
        return new Uri(url);
    }

    /// <summary>Server list API (Global only; KR/TW return null → use <see cref="CharactersPage"/>).</summary>
    public Uri? Servers(ArmoryRegion region) => GameInfo(region, "servers");

    public Uri? Classes(ArmoryRegion region) => GameInfo(region, "classes");

    public Uri? PcData(ArmoryRegion region) => GameInfo(region, "pcdata");

    private Uri? GameInfo(ArmoryRegion region, string what)
    {
        if (!region.IsGlobal()) return null;
        return Build(GlobalSite + GlobalLocalePath + "/api/gameinfo/" + what, ("lang", GlobalLanguage), ("region", region.Code()));
    }

    public Uri Search(ArmoryRegion region, string keyword, int? serverId, int page, int size)
    {
        var sid = serverId?.ToString(CultureInfo.InvariantCulture);
        var p = page.ToString(CultureInfo.InvariantCulture);
        var s = size.ToString(CultureInfo.InvariantCulture);
        return region switch
        {
            ArmoryRegion.Korea => Build(SearchHost + "/aion2/search/v2/character",
                ("keyword", keyword), ("serverId", sid), ("page", p), ("size", s)),
            // The TW search answers 400 unless both serverId and race (1 Elyos / 2 Asmodian = first digit of the id) are sent.
            ArmoryRegion.Taiwan => Build(TaiwanSite + "/aion2/api/search/character",
                ("keyword", keyword), ("serverId", sid), ("race", serverId is { } id ? (id / 1000).ToString(CultureInfo.InvariantCulture) : null),
                ("page", p), ("size", s)),
            _ => Build(SearchHost + "/aion2global/search/v2/character",
                ("keyword", keyword), ("serverId", sid), ("page", p), ("size", s), ("region", region.Code()), ("localeInfo", GlobalLanguage)),
        };
    }

    public Uri CharacterInfo(ArmoryRegion region, int serverId, string characterId) =>
        CharacterCall(region, "/api/character/info", serverId, characterId);

    public Uri Equipment(ArmoryRegion region, int serverId, string characterId) =>
        CharacterCall(region, "/api/character/equipment", serverId, characterId);

    public Uri Daevanion(ArmoryRegion region, int serverId, string characterId, int boardId) =>
        CharacterCall(region, "/api/character/daevanion/detail", serverId, characterId,
            ("boardId", boardId.ToString(CultureInfo.InvariantCulture)));

    public Uri ItemDetail(ArmoryRegion region, int serverId, string characterId, long itemId, int enchantLevel, int slotPos) =>
        Build(CharacterApiBase(region) + "/api/character/equipment/item",
            ("id", itemId.ToString(CultureInfo.InvariantCulture)),
            ("enchantLevel", enchantLevel.ToString(CultureInfo.InvariantCulture)),
            ("characterId", characterId),
            ("serverId", serverId.ToString(CultureInfo.InvariantCulture)),
            ("slotPos", slotPos.ToString(CultureInfo.InvariantCulture)),
            ("lang", Lang(region)),
            ("region", region.Code()));

    private Uri CharacterCall(ArmoryRegion region, string path, int serverId, string characterId, params (string, string?)[] extra)
    {
        var args = new List<(string, string?)>
        {
            ("region", region.Code()),
            ("lang", Lang(region)),
            ("characterId", characterId),
            ("serverId", serverId.ToString(CultureInfo.InvariantCulture)),
        };
        args.AddRange(extra);
        return Build(CharacterApiBase(region) + path, [.. args]);
    }

    internal static Uri Build(string baseUrl, params (string Key, string? Value)[] query)
    {
        var sb = new StringBuilder(baseUrl);
        var first = true;
        foreach (var (k, v) in query)
        {
            if (v is null) continue;
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        return new Uri(sb.ToString());
    }
}
