using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

/// <summary>Deterministic in-memory <see cref="IGameData"/> for engine tests.</summary>
internal sealed class FakeGameData : IGameData
{
    public const uint BossCode = 2300104;
    public const uint BossCode2 = 2300171;
    public const uint TrashCode = 2100001;
    public const uint AddCode = 2100002;
    public const uint DummyCode = 2400032;
    public const uint HealSkill = 17010000;
    public const uint RecuperationSkill = 18050000;
    /// <summary>Open-world field boss (place 2 of block 2400 on <see cref="OpenWorldMap"/>).</summary>
    public const uint FieldBossCode = 2400419;
    public const uint FieldBossCode1 = 2400017;
    public const uint OpenWorldMap = 1110;

    public GameLanguage Language { get; set; }

    public event Action? LanguageChanged
    {
        add { }
        remove { }
    }

    public HashSet<uint> HealSkills { get; } = new() { HealSkill, RecuperationSkill };

    public Dictionary<uint, NpcInfo> Npcs { get; } = new()
    {
        [BossCode] = new NpcInfo(BossCode, "Enhanced Harcon", IsBoss: true, IsDummy: false, DungeonId: 600021),
        [BossCode2] = new NpcInfo(BossCode2, "Ultimate Berk", IsBoss: true, IsDummy: false, DungeonId: 600021),
        [TrashCode] = new NpcInfo(TrashCode, "Cave Rat", IsBoss: false, IsDummy: false, DungeonId: null),
        [AddCode] = new NpcInfo(AddCode, "Harcon Minion", IsBoss: false, IsDummy: false, DungeonId: 600021),
        [DummyCode] = new NpcInfo(DummyCode, "Training Scarecrow", IsBoss: false, IsDummy: true, DungeonId: null),
        [FieldBossCode1] = new NpcInfo(FieldBossCode1, "Melted Danar", IsBoss: true, IsDummy: false, DungeonId: null),
        [FieldBossCode] = new NpcInfo(FieldBossCode, "Special Operations Leader Linx", IsBoss: true, IsDummy: false, DungeonId: null),
    };

    public Dictionary<uint, uint> GroupOverrides { get; } = new();

    public string GetSkillName(uint skillId) => $"Skill {skillId}";

    public uint GetSkillGroupKey(uint skillId) =>
        GroupOverrides.TryGetValue(skillId, out var k) ? k : SkillIds.BaseId(skillId);

    public string? GetSkillIconKey(uint skillId) => null;

    public CharacterClass GetSkillClass(uint skillId) => SkillIds.ClassOf(skillId);

    public bool IsHealSkill(uint skillId) => HealSkills.Contains(skillId) || HealSkills.Contains(SkillIds.BaseId(skillId));

    public bool IsDotSkill(uint skillId) => false;

    public NpcInfo? GetNpc(uint npcCode) => Npcs.TryGetValue(npcCode, out var n) ? n : null;

    public string GetNpcName(uint npcCode) => Npcs.TryGetValue(npcCode, out var n) ? n.Name : $"NPC {npcCode}";

    public string? GetMapName(uint mapId) => mapId switch
    {
        600021 => "Fire Temple",
        600022 => "Fire Temple (Hard)",
        OpenWorldMap => "Altgard",
        _ => null,
    };

    public bool IsInstanceMap(uint mapId) => mapId is >= 600000 and <= 699999;

    public bool IsOpenWorldMap(uint mapId) => mapId is OpenWorldMap or 1010;

    public uint? GetFieldBossBlock(uint mapId) => mapId == OpenWorldMap ? 2400 : null;

    public uint? GetFieldBossNpcCode(uint block, int place) => (block, place) switch
    {
        (2400, 1) => FieldBossCode1,
        (2400, 2) => FieldBossCode,
        _ => null,
    };

    public string? GetServerName(ushort serverId) => serverId == 1304 ? "Kaisinel" : null;

    public string GetClassName(CharacterClass characterClass) => characterClass.ToString();
}
