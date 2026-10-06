namespace Aion2Dps.Contracts;

/// <summary>Skill id normalization and classification rules (PROTOCOL.md §8.2.2, §10.4, §15.2).</summary>
public static class SkillIds
{
    /// <summary>"Dodge" pseudo-skill: in a damage record it means the target dodged.</summary>
    public const uint Dodge = 11_000_100;

    /// <summary>Theostone procs arrive as 3,000,000..3,099,999 and are keyed ×10+1 in the data tables.</summary>
    public static uint Normalize(uint raw) => raw is >= 3_000_000 and <= 3_099_999 ? raw * 10 + 1 : raw;

    public static SkillKind GetKind(uint skillId) => skillId switch
    {
        >= 16_990_000 and <= 16_999_999 => SkillKind.Link,
        >= 16_770_000 and <= 16_779_999 => SkillKind.Link,
        >= 10_000_000 and < 20_000_000 => SkillKind.Player,
        >= 30_000_000 and < 31_000_000 => SkillKind.Theostone,
        >= 100_000 and < 200_000 => SkillKind.Spirit,
        >= 1_000_000 and < 10_000_000 => SkillKind.Npc,
        _ => SkillKind.Unknown,
    };

    /// <summary>Strips the level/spec digits: 11011230 → 11010000.</summary>
    public static uint BaseId(uint skillId) => skillId - skillId % 10_000;

    /// <summary>Class owning a player skill, from its prefix (skill / 1,000,000). Sub-id 0 means generic/mob → Unknown.</summary>
    public static CharacterClass ClassOf(uint skillId)
    {
        if (skillId is < 11_000_000 or >= 20_000_000) return CharacterClass.Unknown;
        if ((skillId / 10_000) % 100 == 0) return CharacterClass.Unknown;
        return (skillId / 1_000_000) switch
        {
            11 => CharacterClass.Gladiator,
            12 => CharacterClass.Templar,
            13 => CharacterClass.Assassin,   // note: Ranger/Assassin order is swapped vs classId
            14 => CharacterClass.Ranger,
            15 => CharacterClass.Sorcerer,
            16 => CharacterClass.Elementalist,
            17 => CharacterClass.Cleric,
            18 => CharacterClass.Chanter,
            19 => CharacterClass.Brawler,
            _ => CharacterClass.Unknown,
        };
    }

    /// <summary>Effect validator (§8.2.3 #1): effect/100 and skill share the same 10,000-base.</summary>
    public static bool EffectMatchesSkill(uint effectId, uint skillId)
    {
        if (effectId == 0) return false;
        uint e = effectId / 100;
        return e - e % 10_000 == skillId - skillId % 10_000;
    }
}

/// <summary>Class code helpers and an original class colour palette.</summary>
public static class ClassInfo
{
    /// <summary>Identity/roster class code → class: classId = (code − 1) / 4.</summary>
    public static CharacterClass FromCode(uint classCode)
    {
        if (classCode == 0) return CharacterClass.Unknown;
        uint id = (classCode - 1) / 4;
        return id switch
        {
            1 => CharacterClass.Gladiator,
            2 => CharacterClass.Templar,
            3 => CharacterClass.Ranger,
            4 => CharacterClass.Assassin,
            5 => CharacterClass.Elementalist,
            6 => CharacterClass.Sorcerer,
            7 => CharacterClass.Cleric,
            8 => CharacterClass.Chanter,
            11 => CharacterClass.Brawler,
            _ => CharacterClass.Unknown,
        };
    }

    public static readonly IReadOnlyList<CharacterClass> All =
    [
        CharacterClass.Gladiator, CharacterClass.Templar, CharacterClass.Ranger, CharacterClass.Assassin,
        CharacterClass.Elementalist, CharacterClass.Sorcerer, CharacterClass.Cleric, CharacterClass.Chanter,
        CharacterClass.Brawler,
    ];

    /// <summary>Default class colours (#RRGGBB). Themes may override them.</summary>
    public static string DefaultColor(CharacterClass c) => c switch
    {
        CharacterClass.Gladiator => "#E08A3C",
        CharacterClass.Templar => "#5B8FD6",
        CharacterClass.Ranger => "#73C24A",
        CharacterClass.Assassin => "#A866E0",
        CharacterClass.Elementalist => "#38BFC7",
        CharacterClass.Sorcerer => "#E2505F",
        CharacterClass.Cleric => "#EED36A",
        CharacterClass.Chanter => "#8CCBF2",
        CharacterClass.Brawler => "#E27AA2",
        _ => "#8A8F98",
    };

    public static string ShortName(CharacterClass c) => c switch
    {
        CharacterClass.Gladiator => "GLA",
        CharacterClass.Templar => "TEM",
        CharacterClass.Ranger => "RAN",
        CharacterClass.Assassin => "ASN",
        CharacterClass.Elementalist => "ELE",
        CharacterClass.Sorcerer => "SOR",
        CharacterClass.Cleric => "CLR",
        CharacterClass.Chanter => "CHN",
        CharacterClass.Brawler => "BRW",
        _ => "???",
    };
}
