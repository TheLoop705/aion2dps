using Aion2Dps.Contracts;

namespace Aion2Dps.Armory;

// Typed models of the official AION 2 character-info endpoints. Every field the site may omit is nullable; lists are
// never null (empty instead). Strings are already HTML-stripped where the site sends markup.

public enum Faction { Unknown = 0, Elyos = 1, Asmodian = 2 }

/// <summary>Item grade as the site names it (Global and KR use the same English ids).</summary>
public enum ItemGrade { Unknown, Common, Rare, Legend, Unique, Epic, Special, Mythic }

/// <summary>Where a slot is shown in the equipment grid.</summary>
public enum SlotGroup { Weapon, Armor, Accessory, Rune, Arcana, Other }

public enum StatCategory { Core, Pantheon, ItemLevel, Other }

public sealed record ArmoryServer(int ServerId, string Name, string ShortName, Faction Faction, ArmoryRegion Region)
{
    public string DisplayName => Faction == Faction.Unknown ? Name : $"{Name} ({Faction})";
}

public sealed record ArmoryClassInfo(int Id, string Name, string Text);

/// <summary>One entry of <c>/api/gameinfo/pcdata</c>: a class/gender/race combination (pcId).</summary>
public sealed record ArmoryPcData(int PcId, string ClassName, string ClassText, string? Gender, string? Race);

public sealed record CharacterSearchResult
{
    /// <summary>Encrypted character id, URL-decoded (pass it back as-is to the other calls).</summary>
    public required string CharacterId { get; init; }
    public required string Name { get; init; }
    public int Level { get; init; }
    public Faction Faction { get; init; }
    public int PcId { get; init; }
    public CharacterClass Class { get; init; }
    public int ServerId { get; init; }
    public string? ServerName { get; init; }
    public string? ProfileImageUrl { get; init; }
    /// <summary>Global region code of the character ("eu", ...), null on KR/TW.</summary>
    public string? RegionCode { get; init; }
    /// <summary>The region the search ran in.</summary>
    public ArmoryRegion Region { get; init; }
}

public sealed record CharacterSearchPage(IReadOnlyList<CharacterSearchResult> Items, int Page, int PageSize, int Total, int EndPage)
{
    public bool HasMore => Page < EndPage;
    public static CharacterSearchPage Empty { get; } = new([], 1, 0, 0, 0);
}

public sealed record CharacterProfile
{
    public required string CharacterId { get; init; }
    public required string Name { get; init; }
    public int Level { get; init; }
    public string? ClassName { get; init; }
    public CharacterClass Class { get; init; }
    public int PcId { get; init; }
    public Faction Faction { get; init; }
    public string? RaceName { get; init; }
    public string? GenderName { get; init; }
    public int ServerId { get; init; }
    public string? ServerName { get; init; }
    public string? RegionName { get; init; }
    public long? CombatPower { get; init; }
    /// <summary>Item level ("gear score" on the site), from <c>profile.itemLevel</c> or the ItemLevel stat.</summary>
    public int? ItemLevel { get; init; }
    public int? TitleId { get; init; }
    public string? TitleName { get; init; }
    public string? TitleGrade { get; init; }
    public string? ProfileImageUrl { get; init; }
    /// <summary>Legion name. Not sent by the official endpoints as of 2026-10; kept for forward compatibility.</summary>
    public string? LegionName { get; init; }
}

public sealed record CharacterStat(string Type, string Name, long Value, IReadOnlyList<string> Effects, StatCategory Category);

public sealed record CharacterTitle
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? Grade { get; init; }
    /// <summary>Attack / Defense / Etc.</summary>
    public string? Category { get; init; }
    public int TotalCount { get; init; }
    public int OwnedCount { get; init; }
    /// <summary>Collection (owned) effects.</summary>
    public IReadOnlyList<string> Stats { get; init; } = [];
    /// <summary>Effects while equipped.</summary>
    public IReadOnlyList<string> EquipStats { get; init; } = [];
}

public sealed record CharacterTitles(int TotalCount, int OwnedCount, IReadOnlyList<CharacterTitle> Titles)
{
    public static CharacterTitles Empty { get; } = new(0, 0, []);
}

public sealed record CharacterRanking
{
    public string? ContentName { get; init; }
    public int? Rank { get; init; }
    public long? Point { get; init; }
    public string? GradeName { get; init; }
    public int? RankChange { get; init; }
}

public sealed record DaevanionBoard(int Id, string Name, int TotalNodes, int OpenNodes, string? Icon, bool Open)
{
    public double Percent => TotalNodes <= 0 ? 0 : 100.0 * OpenNodes / TotalNodes;
}

/// <summary>Result of <c>/api/character/info</c>.</summary>
public sealed record CharacterInfo
{
    public required CharacterProfile Profile { get; init; }
    public IReadOnlyList<CharacterStat> Stats { get; init; } = [];
    public CharacterTitles Titles { get; init; } = CharacterTitles.Empty;
    public IReadOnlyList<CharacterRanking> Rankings { get; init; } = [];
    public IReadOnlyList<DaevanionBoard> DaevanionBoards { get; init; } = [];
    public ArmoryRegion Region { get; init; }
}

public sealed record EquipmentItem
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int EnchantLevel { get; init; }
    public int ExceedLevel { get; init; }
    public string? GradeName { get; init; }
    public ItemGrade Grade { get; init; }
    public int SlotPos { get; init; }
    public string? SlotName { get; init; }
    public string? Icon { get; init; }
    public SlotGroup Group => ArmorySlots.GroupOf(SlotPos, SlotName);
}

public sealed record PetInfo(long? Id, string? Name, int? Level, string? Icon);

public sealed record WingInfo(long? Id, string? Name, int? EnchantLevel, string? GradeName, ItemGrade Grade, string? Icon);

public sealed record SkillInfo
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int Level { get; init; }
    public int NeedLevel { get; init; }
    /// <summary>Active / Passive / Stigma (as sent by the site).</summary>
    public string? Category { get; init; }
    public bool Acquired { get; init; }
    public bool Equipped { get; init; }
    public string? Icon { get; init; }
}

/// <summary>Result of <c>/api/character/equipment</c>: gear, appearance skins, pet, wings and skills.</summary>
public sealed record CharacterEquipment
{
    public IReadOnlyList<EquipmentItem> Items { get; init; } = [];
    public IReadOnlyList<EquipmentItem> Skins { get; init; } = [];
    public PetInfo? Pet { get; init; }
    public WingInfo? Wing { get; init; }
    public WingInfo? WingSkin { get; init; }
    public IReadOnlyList<SkillInfo> Skills { get; init; } = [];
}

public sealed record ItemStat(string Id, string Name, string Value, string? MinValue, string? Extra, bool Exceed);

public sealed record ItemSubSkill(string Name, int Level);

/// <summary>A socketed manastone (<c>magicStoneStat</c>).</summary>
public sealed record ItemManastone(string? Id, string Name, string Value, string? GradeName, ItemGrade Grade, string? Icon, int SlotPos);

/// <summary>A socketed godstone (<c>godStoneStat</c>).</summary>
public sealed record ItemGodstone(string Name, string Description, string? GradeName, ItemGrade Grade, string? Icon, int SlotPos);

public sealed record ItemSetBonus(int? Degree, IReadOnlyList<string> Descriptions);

/// <summary>Result of <c>/api/character/equipment/item</c> (an equipped item with its rolled stats).</summary>
public sealed record ItemDetail
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? GradeName { get; init; }
    public ItemGrade Grade { get; init; }
    public string? Icon { get; init; }
    public int? Level { get; init; }
    public int EnchantLevel { get; init; }
    public int? MaxEnchantLevel { get; init; }
    public int? MaxExceedLevel { get; init; }
    public int? EquipLevel { get; init; }
    public string? CategoryName { get; init; }
    public string? RaceName { get; init; }
    public string? Type { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> ClassNames { get; init; } = [];
    public int MagicStoneSlotCount { get; init; }
    public int GodStoneSlotCount { get; init; }
    public int SubStatCount { get; init; }
    public string? SoulBindRate { get; init; }
    public bool? Tradable { get; init; }
    public IReadOnlyList<ItemStat> MainStats { get; init; } = [];
    public IReadOnlyList<ItemStat> SubStats { get; init; } = [];
    public IReadOnlyList<ItemSubSkill> SubSkills { get; init; } = [];
    public IReadOnlyList<ItemManastone> Manastones { get; init; } = [];
    public IReadOnlyList<ItemGodstone> Godstones { get; init; } = [];
    public IReadOnlyList<string> Costumes { get; init; } = [];
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> SetItemNames { get; init; } = [];
    public IReadOnlyList<ItemSetBonus> SetBonuses { get; init; } = [];
}

public enum DaevanionNodeType { None, Start, Stat, SkillLevel, Other }

public sealed record DaevanionNode
{
    public int BoardId { get; init; }
    public int NodeId { get; init; }
    public string Name { get; init; } = "";
    public int Row { get; init; }
    public int Col { get; init; }
    public string? Grade { get; init; }
    public DaevanionNodeType Type { get; init; }
    public string? Icon { get; init; }
    public IReadOnlyList<string> Effects { get; init; } = [];
    public bool Open { get; init; }
}

/// <summary>Result of <c>/api/character/daevanion/detail</c> for one board.</summary>
public sealed record DaevanionBoardDetail
{
    public int BoardId { get; init; }
    public IReadOnlyList<DaevanionNode> Nodes { get; init; } = [];
    public IReadOnlyList<string> OpenStatEffects { get; init; } = [];
    public IReadOnlyList<string> OpenSkillEffects { get; init; } = [];
}
