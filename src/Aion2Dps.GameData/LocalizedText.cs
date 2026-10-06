using Aion2Dps.Contracts;

namespace Aion2Dps.GameData;

/// <summary>Built-in strings that are not in the data tables: class names and readable fallbacks.</summary>
public static class LocalizedText
{
    private static int Index(GameLanguage l) => l switch
    {
        GameLanguage.Korean => 1,
        GameLanguage.ChineseSimplified => 2,
        GameLanguage.ChineseTraditional => 3,
        _ => 0,
    };

    // EN, KO, zh-Hans, zh-Hant (official in-game class names).
    private static readonly Dictionary<CharacterClass, string[]> ClassNames = new()
    {
        [CharacterClass.Gladiator] = ["Gladiator", "검성", "剑圣", "劍聖"],
        [CharacterClass.Templar] = ["Templar", "수호성", "守护圣", "守護聖"],
        [CharacterClass.Ranger] = ["Ranger", "궁성", "弓圣", "弓聖"],
        [CharacterClass.Assassin] = ["Assassin", "살성", "杀圣", "殺聖"],
        [CharacterClass.Elementalist] = ["Spiritmaster", "정령성", "精灵圣", "精靈聖"],
        [CharacterClass.Sorcerer] = ["Sorcerer", "마도성", "魔道圣", "魔道聖"],
        [CharacterClass.Cleric] = ["Cleric", "치유성", "治愈圣", "治癒聖"],
        [CharacterClass.Chanter] = ["Chanter", "호법성", "护法圣", "護法聖"],
        [CharacterClass.Brawler] = ["Brawler", "권성", "拳圣", "拳聖"],
    };

    private static readonly string[] UnknownClass = ["Unknown", "알 수 없음", "未知", "未知"];
    private static readonly string[] Dodge = ["Dodge", "회피", "闪避", "閃避"];
    private static readonly string[] MonsterAttack = ["Monster attack", "몬스터 공격", "怪物攻击", "怪物攻擊"];
    private static readonly string[] Theostone = ["Theostone", "신석", "神石", "神石"];
    private static readonly string[] SpiritAttack = ["Spirit attack", "정령 공격", "精灵攻击", "精靈攻擊"];
    private static readonly string[] SpiritLink = ["Spirit link", "정령 연결", "精灵连结", "精靈連結"];
    private static readonly string[] SkillPrefix = ["Skill", "스킬", "技能", "技能"];

    public static string ClassName(CharacterClass c, GameLanguage l) =>
        ClassNames.TryGetValue(c, out var names) ? names[Index(l)] : UnknownClass[Index(l)];

    public static string DodgeName(GameLanguage l) => Dodge[Index(l)];
    public static string MonsterAttackName(GameLanguage l) => MonsterAttack[Index(l)];
    public static string TheostoneName(GameLanguage l) => Theostone[Index(l)];
    public static string SpiritAttackName(GameLanguage l) => SpiritAttack[Index(l)];
    public static string SpiritLinkName(GameLanguage l) => SpiritLink[Index(l)];
    public static string UnknownSkillName(uint skillId, GameLanguage l) => $"{SkillPrefix[Index(l)]} {skillId}";
}
