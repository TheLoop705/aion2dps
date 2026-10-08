namespace Aion2Dps.Contracts;

public sealed record NpcInfo(uint Code, string Name, bool IsBoss, bool IsDummy, uint? DungeonId);

/// <summary>
/// Localized game data lookups (skills, NPCs, maps, servers, classes) and skill classification tables.
/// Implementations must be thread-safe for concurrent reads. Missing keys in a language fall back to English;
/// unknown ids never throw.
/// </summary>
public interface IGameData
{
    GameLanguage Language { get; set; }

    /// <summary>Raised after <see cref="Language"/> changes.</summary>
    event Action? LanguageChanged;

    /// <summary>Localized skill name. Lookup order: exact id → base id → id/10*10 → a readable fallback like "Skill 11020030".</summary>
    string GetSkillName(uint skillId);

    /// <summary>Key used to group skill variants in breakdowns (usually <see cref="SkillIds.BaseId"/>, but the exact id when
    /// the variant has its own distinct name).</summary>
    uint GetSkillGroupKey(uint skillId);

    /// <summary>Icon key from <c>skill_icons.json</c> (e.g. "ICON_EL_SKILL_010"), or null.</summary>
    string? GetSkillIconKey(uint skillId);

    /// <summary>Class owning the skill (by prefix, see <see cref="SkillIds.ClassOf"/>).</summary>
    CharacterClass GetSkillClass(uint skillId);

    /// <summary>True when the skill is a heal-family skill (a <c>04 38</c> amount from it is healing, not damage).</summary>
    bool IsHealSkill(uint skillId);

    /// <summary>True when the skill is a known DoT source.</summary>
    bool IsDotSkill(uint skillId);

    NpcInfo? GetNpc(uint npcCode);

    /// <summary>Localized NPC name, or "NPC 2300104" when unknown.</summary>
    string GetNpcName(uint npcCode);

    /// <summary>Localized dungeon/map name, or null when unknown.</summary>
    string? GetMapName(uint mapId);

    /// <summary>Instanced dungeon maps are 600000..699999 (last digit = difficulty).</summary>
    bool IsInstanceMap(uint mapId);

    string? GetServerName(ushort serverId);

    /// <summary>True for overworld maps (<c>open_world_maps.json</c>): only your own direct hits are sent there.</summary>
    bool IsOpenWorldMap(uint mapId) => false;

    /// <summary>NPC-code block (code / 1000) of a map's field bosses (<c>field_boss_maps.json</c>), or null.</summary>
    uint? GetFieldBossBlock(uint mapId) => null;

    /// <summary>
    /// The field boss at <paramref name="place"/> (1-based, the slot's last two digits) of an NPC-code block: the
    /// <paramref name="place"/>-th boss (training dummies excluded) of that block in code order; null when unknown.
    /// </summary>
    uint? GetFieldBossNpcCode(uint block, int place) => null;

    string GetClassName(CharacterClass characterClass);
}
