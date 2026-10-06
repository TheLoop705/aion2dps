using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

/// <summary>In-memory <see cref="IGameData"/> with invented names for the sample fights.</summary>
internal sealed class FakeGameData : IGameData
{
    private GameLanguage _language;
    private readonly Dictionary<uint, string> _skills = new();
    private readonly HashSet<uint> _dots = new();
    private readonly HashSet<uint> _heals = new();

    public FakeGameData()
    {
        foreach (var s in SampleData.AllSkills)
        {
            _skills[s.Id] = s.Name;
            if (s.Dot) _dots.Add(s.Id);
            if (s.Heal) _heals.Add(s.Id);
        }
        foreach (var (id, name) in SampleData.NpcSkills) _skills[id] = name;
        foreach (var (id, name) in SampleData.BuffSkills) _skills[id] = name;
    }

    public GameLanguage Language
    {
        get => _language;
        set { _language = value; LanguageChanged?.Invoke(); }
    }

    public event Action? LanguageChanged;

    public string GetSkillName(uint skillId)
    {
        if (_skills.TryGetValue(skillId, out var n)) return n;
        if (_skills.TryGetValue(SkillIds.BaseId(skillId), out n)) return n;
        foreach (var (id, name) in _skills)
            if (SkillIds.BaseId(id) == SkillIds.BaseId(skillId) && skillId >= 10_000_000) return name;
        return "Skill " + skillId;
    }

    public uint GetSkillGroupKey(uint skillId) => skillId >= 10_000_000 ? SkillIds.BaseId(skillId) : skillId;

    public string? GetSkillIconKey(uint skillId) => null;

    public CharacterClass GetSkillClass(uint skillId) =>
        skillId is >= 100_000 and < 200_000 ? CharacterClass.Elementalist : SkillIds.ClassOf(skillId);

    public bool IsHealSkill(uint skillId) => _heals.Contains(skillId);

    public bool IsDotSkill(uint skillId) => _dots.Contains(skillId);

    public NpcInfo? GetNpc(uint npcCode) =>
        SampleData.Npcs.TryGetValue(npcCode, out var n) ? new NpcInfo(npcCode, n.Name, n.Boss, n.Dummy, null) : null;

    public string GetNpcName(uint npcCode) => SampleData.Npcs.TryGetValue(npcCode, out var n) ? n.Name : "NPC " + npcCode;

    public string? GetMapName(uint mapId) => SampleData.Maps.TryGetValue(mapId, out var n) ? n : null;

    public bool IsInstanceMap(uint mapId) => mapId is >= 600_000 and <= 699_999;

    public string? GetServerName(ushort serverId) => serverId switch
    {
        1011 => "Varnhold",
        1012 => "Ostrava",
        2021 => "Kelmora",
        _ => null,
    };

    public string GetClassName(CharacterClass characterClass) => characterClass switch
    {
        CharacterClass.Unknown => "Unknown class",
        _ => characterClass.ToString(),
    };
}
