using System.Text.Json;
using System.Text.RegularExpressions;
using Aion2Dps.Contracts;

namespace Aion2Dps.Armory;

/// <summary>
/// Parses the JSON of the official character-info endpoints into the typed models. Tolerant: unknown members are ignored,
/// missing members become null/empty. Throws <see cref="ArmoryException"/> (<see cref="ArmoryErrorKind.EndpointChanged"/>)
/// only when the payload is not JSON or lacks the part that makes it useful (e.g. info without a profile).
/// </summary>
public static partial class ArmoryParser
{
    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static JsonDocument Parse(string json, string what)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArmoryException(ArmoryErrorKind.EndpointChanged, $"The official site returned an empty {what} response.");
        var t = json.AsSpan().TrimStart();
        if (t.Length > 0 && t[0] == '<')
            throw new ArmoryException(ArmoryErrorKind.EndpointChanged,
                $"The official site returned a web page instead of {what} data. The site may have changed.");
        try
        {
            return JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException ex)
        {
            throw new ArmoryException(ArmoryErrorKind.EndpointChanged, $"The official site returned unreadable {what} data.", ex);
        }
    }

    private static ArmoryException Changed(string what) =>
        new(ArmoryErrorKind.EndpointChanged, $"The official site's {what} data has an unexpected shape. The site may have changed.");

    // ───────────────────────────── servers / classes ─────────────────────────────

    /// <summary><c>{"serverList":[{raceId, serverId, serverName, serverShortName}]}</c>.</summary>
    public static IReadOnlyList<ArmoryServer> ParseServers(string json, ArmoryRegion region)
    {
        using var doc = Parse(json, "server list");
        var root = doc.RootElement;
        JsonElement list;
        if (root.ValueKind == JsonValueKind.Array) list = root;
        else if (root.Prop("serverList") is { ValueKind: JsonValueKind.Array } l) list = l;
        else throw Changed("server list");
        return ReadServers(list, region, filterByRegion: false);
    }

    [GeneratedRegex(@"_serverNameMap\s*=\s*(\[.*?\])\s*;", RegexOptions.Singleline)]
    private static partial Regex ServerMapRegex();

    /// <summary>
    /// Reads the <c>var _serverNameMap = [...]</c> array embedded in the official characters index page. On the Global page every
    /// entry carries a <c>region</c> ("eu", "nae", ...) and only the requested region is returned; KR/TW entries have none.
    /// </summary>
    public static IReadOnlyList<ArmoryServer> ParseServerMapFromHtml(string html, ArmoryRegion region)
    {
        var m = ServerMapRegex().Match(html ?? "");
        if (!m.Success) throw Changed("server list page");
        using var doc = Parse(m.Groups[1].Value, "server list");
        return ReadServers(doc.RootElement, region, filterByRegion: region.IsGlobal());
    }

    private static List<ArmoryServer> ReadServers(JsonElement array, ArmoryRegion region, bool filterByRegion)
    {
        var result = new List<ArmoryServer>();
        if (array.ValueKind != JsonValueKind.Array) return result;
        foreach (var s in array.EnumerateArray())
        {
            var id = s.Int("serverId");
            if (id is null or <= 0) continue;
            if (filterByRegion)
            {
                var code = s.Str("region");
                if (code is not null && !string.Equals(code, region.Code(), StringComparison.OrdinalIgnoreCase)) continue;
            }
            var name = ArmoryText.Clean(s.Str("serverName"));
            if (name.Length == 0) name = $"Server {id}";
            var shortName = ArmoryText.Clean(s.Str("serverShortName"));
            result.Add(new ArmoryServer(id.Value, name, shortName.Length == 0 ? name : shortName, ArmorySlots.ParseFaction(s.Int("raceId")), region));
        }
        result.Sort((a, b) => a.Faction != b.Faction ? a.Faction.CompareTo(b.Faction) : a.ServerId.CompareTo(b.ServerId));
        return result;
    }

    /// <summary><c>{"classList":[{id, name, text}]}</c>.</summary>
    public static IReadOnlyList<ArmoryClassInfo> ParseClasses(string json)
    {
        using var doc = Parse(json, "class list");
        var list = new List<ArmoryClassInfo>();
        foreach (var c in doc.RootElement.Arr("classList"))
        {
            var id = c.Int("id");
            if (id is null) continue;
            var name = ArmoryText.Clean(c.Str("name"));
            var text = ArmoryText.Clean(c.Str("text"));
            list.Add(new ArmoryClassInfo(id.Value, name, text.Length == 0 ? name : text));
        }
        return list;
    }

    /// <summary><c>{"pcDataList":[{id, className, classText, genderName, raceName}]}</c>.</summary>
    public static IReadOnlyList<ArmoryPcData> ParsePcData(string json)
    {
        using var doc = Parse(json, "class data");
        var list = new List<ArmoryPcData>();
        foreach (var c in doc.RootElement.Arr("pcDataList"))
        {
            var id = c.Int("id");
            if (id is null) continue;
            list.Add(new ArmoryPcData(id.Value, ArmoryText.Clean(c.Str("className")), ArmoryText.Clean(c.Str("classText")),
                c.Str("genderName"), c.Str("raceName")));
        }
        return list;
    }

    // ───────────────────────────── search ─────────────────────────────

    /// <summary><c>{"list":[{characterId, name, race, pcId, level, serverId, serverName, profileImageUrl, region}], "pagination":{page,size,total,endPage}}</c>.</summary>
    public static CharacterSearchPage ParseSearch(string json, ArmoryRegion region)
    {
        using var doc = Parse(json, "search");
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || (root.Prop("list") is null && root.Prop("pagination") is null))
            throw Changed("search");
        var items = new List<CharacterSearchResult>();
        foreach (var x in root.Arr("list"))
        {
            var rawId = x.Str("characterId");
            var serverId = x.Int("serverId");
            if (string.IsNullOrWhiteSpace(rawId) || serverId is null) continue;
            var pcId = x.Int("pcId") ?? 0;
            var img = x.Str("profileImageUrl");
            if (img is not null && img.StartsWith('/')) img = "https://profileimg.plaync.com" + img;
            items.Add(new CharacterSearchResult
            {
                CharacterId = SafeUnescape(rawId),
                Name = ArmoryText.Clean(x.Str("name")),
                Level = x.Int("level") ?? 0,
                Faction = ArmorySlots.ParseFaction(x.Int("race")),
                PcId = pcId,
                Class = ArmorySlots.ClassFromPcId(pcId),
                ServerId = serverId.Value,
                ServerName = ArmoryText.Clean(x.Str("serverName")) is { Length: > 0 } sn ? sn : null,
                ProfileImageUrl = img,
                RegionCode = x.Str("region"),
                Region = region,
            });
        }
        var pg = root.Obj("pagination");
        var page = pg.Int("page") ?? 1;
        var size = pg.Int("size") ?? items.Count;
        var total = pg.Int("total") ?? items.Count;
        var endPage = pg.Int("endPage") ?? page;
        return new CharacterSearchPage(items, page, size, total, endPage);
    }

    /// <summary>The search returns ids URL-encoded ("...%3D"); the other endpoints want the decoded form.</summary>
    internal static string SafeUnescape(string s)
    {
        if (!s.Contains('%')) return s;
        try { return Uri.UnescapeDataString(s); } catch { return s; }
    }

    // ───────────────────────────── character info ─────────────────────────────

    /// <summary><c>{profile, stat:{statList}, title:{titleList}, ranking:{rankingList}, daevanion:{boardList}}</c>.</summary>
    public static CharacterInfo ParseCharacterInfo(string json, ArmoryRegion region)
    {
        using var doc = Parse(json, "character");
        var root = doc.RootElement;
        if (root.Obj("profile") is not { } p) throw Changed("character");

        var stats = new List<CharacterStat>();
        int? itemLevelStat = null;
        foreach (var s in root.Obj("stat").Arr("statList"))
        {
            var type = s.Str("type") ?? "";
            var value = s.Long("value") ?? 0;
            var cat = ArmorySlots.StatCategoryOf(type);
            var name = ArmoryText.Clean(s.Str("name"));
            if (cat == StatCategory.ItemLevel)
            {
                itemLevelStat = (int)value;
                name = "Item Level"; // the Global site sends this one in Korean
            }
            stats.Add(new CharacterStat(type, name.Length == 0 ? type : name, value, s.Strings("statSecondList"), cat));
        }

        var pcId = p.Int("pcId") ?? 0;
        var className = ArmoryText.Clean(p.Str("className"));
        var cls = ArmorySlots.ClassFromPcId(pcId);
        if (cls == CharacterClass.Unknown) cls = ArmorySlots.ClassFromName(className);
        var itemLevel = p.Int("itemLevel") is { } il and > 0 ? il : itemLevelStat;

        var profile = new CharacterProfile
        {
            CharacterId = p.Str("characterId") ?? "",
            Name = ArmoryText.Clean(p.Str("characterName")),
            Level = p.Int("characterLevel") ?? 0,
            ClassName = className.Length == 0 ? null : className,
            Class = cls,
            PcId = pcId,
            Faction = ArmorySlots.ParseFaction(p.Int("raceId")),
            RaceName = NullIfEmpty(p.Str("raceName")),
            GenderName = NullIfEmpty(p.Str("genderName")),
            ServerId = p.Int("serverId") ?? 0,
            ServerName = NullIfEmpty(p.Str("serverName")),
            RegionName = NullIfEmpty(p.Str("regionName")),
            CombatPower = p.Long("combatPower"),
            ItemLevel = itemLevel,
            TitleId = p.Int("titleId"),
            TitleName = NullIfEmpty(p.Str("titleName")),
            TitleGrade = NullIfEmpty(p.Str("titleGrade")),
            ProfileImageUrl = NullIfEmpty(p.Str("profileImage")),
            LegionName = NullIfEmpty(p.Str("guildName") ?? p.Str("legionName")),
        };

        var titles = CharacterTitles.Empty;
        if (root.Obj("title") is { } t)
        {
            var list = new List<CharacterTitle>();
            foreach (var x in t.Arr("titleList"))
            {
                list.Add(new CharacterTitle
                {
                    Id = x.Int("id") ?? 0,
                    Name = ArmoryText.Clean(x.Str("name")),
                    Grade = NullIfEmpty(x.Str("grade")),
                    Category = NullIfEmpty(x.Str("equipCategory")),
                    TotalCount = x.Int("totalCount") ?? 0,
                    OwnedCount = x.Int("ownedCount") ?? 0,
                    Stats = x.Strings("statList"),
                    EquipStats = x.Strings("equipStatList"),
                });
            }
            titles = new CharacterTitles(t.Int("totalCount") ?? 0, t.Int("ownedCount") ?? list.Count, list);
        }

        var rankings = new List<CharacterRanking>();
        foreach (var r in root.Obj("ranking").Arr("rankingList"))
        {
            rankings.Add(new CharacterRanking
            {
                ContentName = NullIfEmpty(r.Str("rankingContentsName")),
                Rank = r.Int("rank"),
                Point = r.Long("point"),
                GradeName = NullIfEmpty(r.Str("gradeName")),
                RankChange = r.Int("rankChange"),
            });
        }

        var boards = new List<DaevanionBoard>();
        foreach (var b in root.Obj("daevanion").Arr("boardList"))
        {
            var id = b.Int("id");
            if (id is null) continue;
            boards.Add(new DaevanionBoard(id.Value, ArmoryText.Clean(b.Str("name")), b.Int("totalNodeCount") ?? 0,
                b.Int("openNodeCount") ?? 0, NullIfEmpty(b.Str("icon")), (b.Int("open") ?? 0) != 0));
        }

        return new CharacterInfo { Profile = profile, Stats = stats, Titles = titles, Rankings = rankings, DaevanionBoards = boards, Region = region };
    }

    // ───────────────────────────── equipment ─────────────────────────────

    /// <summary><c>{equipment:{equipmentList, skinList}, petwing:{pet, wing, wingSkin}, skill:{skillList}}</c>.</summary>
    public static CharacterEquipment ParseEquipment(string json)
    {
        using var doc = Parse(json, "equipment");
        var root = doc.RootElement;
        var eq = root.Obj("equipment");
        if (eq is null && root.Prop("petwing") is null && root.Prop("skill") is null) throw Changed("equipment");

        static List<EquipmentItem> Items(JsonElement? parent, string name)
        {
            var list = new List<EquipmentItem>();
            foreach (var x in parent.Arr(name))
            {
                var id = x.Long("id");
                if (id is null) continue;
                var grade = x.Str("grade");
                list.Add(new EquipmentItem
                {
                    Id = id.Value,
                    Name = ArmoryText.Clean(x.Str("name")),
                    EnchantLevel = x.Int("enchantLevel") ?? 0,
                    ExceedLevel = x.Int("exceedLevel") ?? 0,
                    GradeName = NullIfEmpty(grade),
                    Grade = ArmorySlots.ParseGrade(grade),
                    SlotPos = x.Int("slotPos") ?? 0,
                    SlotName = NullIfEmpty(x.Str("slotPosName")),
                    Icon = NullIfEmpty(x.Str("icon")),
                });
            }
            list.Sort((a, b) => ArmorySlots.SortKey(a.SlotPos).CompareTo(ArmorySlots.SortKey(b.SlotPos)));
            return list;
        }

        var pw = root.Obj("petwing");
        PetInfo? pet = null;
        if (pw?.Obj("pet") is { } pe && (pe.Long("id") is not null || pe.Str("name") is not null))
            pet = new PetInfo(pe.Long("id"), NullIfEmpty(ArmoryText.Clean(pe.Str("name"))), pe.Int("level"), NullIfEmpty(pe.Str("icon")));

        static WingInfo? Wing(JsonElement? w)
        {
            if (w is not { } x || (x.Long("id") is null && x.Str("name") is null)) return null;
            var g = x.Str("grade");
            return new WingInfo(x.Long("id"), NullIfEmpty(ArmoryText.Clean(x.Str("name"))), x.Int("enchantLevel"), NullIfEmpty(g),
                ArmorySlots.ParseGrade(g), NullIfEmpty(x.Str("icon")));
        }

        var skills = new List<SkillInfo>();
        foreach (var s in root.Obj("skill").Arr("skillList"))
        {
            var id = s.Long("id");
            if (id is null) continue;
            skills.Add(new SkillInfo
            {
                Id = id.Value,
                Name = ArmoryText.Clean(s.Str("name")),
                Level = s.Int("skillLevel") ?? 0,
                NeedLevel = s.Int("needLevel") ?? 0,
                Category = NullIfEmpty(s.Str("category")),
                Acquired = (s.Int("acquired") ?? 0) != 0,
                Equipped = (s.Int("equip") ?? 0) != 0,
                Icon = NullIfEmpty(s.Str("icon")),
            });
        }

        return new CharacterEquipment
        {
            Items = Items(eq, "equipmentList"),
            Skins = Items(eq, "skinList"),
            Pet = pet,
            Wing = Wing(pw?.Obj("wing")),
            WingSkin = Wing(pw?.Obj("wingSkin")),
            Skills = skills,
        };
    }

    // ───────────────────────────── item detail ─────────────────────────────

    /// <summary>Item detail; the payload may be the item itself or wrapped in <c>{"data": {...}}</c>.</summary>
    public static ItemDetail ParseItemDetail(string json)
    {
        using var doc = Parse(json, "item");
        var x = doc.RootElement;
        if (x.Obj("data") is { } inner && inner.Prop("id") is not null) x = inner;
        if (x.ValueKind != JsonValueKind.Object || x.Long("id") is not { } id) throw Changed("item");

        static List<ItemStat> Stats(JsonElement parent, string name)
        {
            var list = new List<ItemStat>();
            foreach (var s in parent.Arr(name))
            {
                var n = ArmoryText.Clean(s.Str("name"));
                if (n.Length == 0) continue;
                list.Add(new ItemStat(s.Str("id") ?? "", n, s.Str("value") ?? "", NullIfEmpty(s.Str("minValue")),
                    NullIfEmpty(s.Str("extra")), s.Bool("exceed") ?? false));
            }
            return list;
        }

        var subSkills = new List<ItemSubSkill>();
        foreach (var s in x.Arr("subSkills"))
        {
            var n = ArmoryText.Clean(s.Str("name"));
            if (n.Length > 0) subSkills.Add(new ItemSubSkill(n, s.Int("level") ?? 0));
        }

        var stones = new List<ItemManastone>();
        foreach (var s in x.Arr("magicStoneStat"))
        {
            var g = s.Str("grade");
            stones.Add(new ItemManastone(s.Str("id"), ArmoryText.Clean(s.Str("name")), s.Str("value") ?? "", NullIfEmpty(g),
                ArmorySlots.ParseGrade(g), NullIfEmpty(s.Str("icon")), s.Int("slotPos") ?? stones.Count));
        }

        var godstones = new List<ItemGodstone>();
        foreach (var s in x.Arr("godStoneStat"))
        {
            var g = s.Str("grade");
            godstones.Add(new ItemGodstone(ArmoryText.Clean(s.Str("name")), ArmoryText.Clean(s.Str("desc")), NullIfEmpty(g),
                ArmorySlots.ParseGrade(g), NullIfEmpty(s.Str("icon")), s.Int("slotPos") ?? godstones.Count));
        }

        // set info: "set" or "SetItem" → {items:[{name}], bonuses:[{degree, descriptions[]}]}
        var set = x.Obj("set") ?? x.Obj("SetItem");
        var setNames = new List<string>();
        var setBonuses = new List<ItemSetBonus>();
        if (set is { } st)
        {
            foreach (var i in st.Arr("items"))
                if (ArmoryText.Clean(i.Str("name")) is { Length: > 0 } n) setNames.Add(n);
            foreach (var b in st.Arr("bonuses"))
            {
                var d = b.Strings("descriptions");
                if (d.Count > 0) setBonuses.Add(new ItemSetBonus(b.Int("degree"), d));
            }
        }

        var grade = x.Str("grade");
        return new ItemDetail
        {
            Id = id,
            Name = ArmoryText.Clean(x.Str("name")),
            GradeName = NullIfEmpty(x.Str("gradeName") ?? grade),
            Grade = ArmorySlots.ParseGrade(grade),
            Icon = NullIfEmpty(x.Str("icon")),
            Level = x.Int("level"),
            EnchantLevel = x.Int("enchantLevel") ?? 0,
            MaxEnchantLevel = x.Int("maxEnchantLevel"),
            MaxExceedLevel = x.Int("maxExceedEnchantLevel"),
            EquipLevel = x.Int("equipLevel"),
            CategoryName = NullIfEmpty(x.Str("categoryName")),
            RaceName = NullIfEmpty(x.Str("raceName")),
            Type = NullIfEmpty(x.Str("type")),
            Description = NullIfEmpty(ArmoryText.Clean(x.Str("desc"))),
            ClassNames = x.Strings("classNames"),
            MagicStoneSlotCount = x.Int("magicStoneSlotCount") ?? 0,
            GodStoneSlotCount = x.Int("godStoneSlotCount") ?? 0,
            SubStatCount = x.Int("subStatCount") ?? 0,
            SoulBindRate = NullIfEmpty(x.Str("soulBindRate")),
            Tradable = x.Bool("tradable"),
            MainStats = Stats(x, "mainStats"),
            SubStats = Stats(x, "subStats"),
            SubSkills = subSkills,
            Manastones = stones,
            Godstones = godstones,
            Costumes = x.Strings("costumes"),
            Sources = x.Strings("sources"),
            SetItemNames = setNames,
            SetBonuses = setBonuses,
        };
    }

    // ───────────────────────────── daevanion ─────────────────────────────

    /// <summary><c>{nodeList:[{boardId,nodeId,name,row,col,grade,type,icon,effectList,open}], openStatEffectList, openSkillEffectList}</c>.</summary>
    public static DaevanionBoardDetail ParseDaevanion(string json, int boardId)
    {
        using var doc = Parse(json, "daevanion");
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || (root.Prop("nodeList") is null && root.Prop("openStatEffectList") is null))
            throw Changed("daevanion");
        var nodes = new List<DaevanionNode>();
        foreach (var n in root.Arr("nodeList"))
        {
            var type = n.Str("type") switch
            {
                "None" or null or "" => DaevanionNodeType.None,
                "Start" => DaevanionNodeType.Start,
                "Stat" => DaevanionNodeType.Stat,
                "SkillLevel" => DaevanionNodeType.SkillLevel,
                _ => DaevanionNodeType.Other,
            };
            nodes.Add(new DaevanionNode
            {
                BoardId = n.Int("boardId") ?? boardId,
                NodeId = n.Int("nodeId") ?? 0,
                Name = ArmoryText.Clean(n.Str("name")),
                Row = n.Int("row") ?? 0,
                Col = n.Int("col") ?? 0,
                Grade = NullIfEmpty(n.Str("grade")),
                Type = type,
                Icon = NullIfEmpty(n.Str("icon")),
                Effects = n.Strings("effectList"),
                Open = (n.Int("open") ?? 0) != 0,
            });
        }
        nodes.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));
        return new DaevanionBoardDetail
        {
            BoardId = boardId,
            Nodes = nodes,
            OpenStatEffects = root.Strings("openStatEffectList"),
            OpenSkillEffects = root.Strings("openSkillEffectList"),
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
