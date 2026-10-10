namespace Aion2Dps.App.Demo;

/// <summary>Self-contained <see cref="IGameData"/> for demo mode: invented skill/NPC/map names, never throws.</summary>
public sealed class FakeGameData : IGameData
{
    private GameLanguage _language = GameLanguage.English;

    public const uint BossNpc = 2_490_001;
    public const uint AddNpc = 2_490_002;
    public const uint DummyNpc = 2_000_000;
    public const uint SecondBossNpc = 2_490_010;
    public const uint DemoMap = 600_041;
    public const ushort DemoServer = 2305;

    private static readonly Dictionary<CharacterClass, string[]> ClassSkills = new()
    {
        [CharacterClass.Gladiator] = ["Cleaving Arc", "Rending Strike", "Earthsplitter", "Whirling Edge", "Crushing Blow", "Bloodlust Swing", "Seismic Slam", "Basic Attack"],
        [CharacterClass.Templar] = ["Judgement", "Shield Bash", "Radiant Sweep", "Punishing Strike", "Holy Lance", "Bulwark Charge", "Divine Smite", "Basic Attack"],
        [CharacterClass.Ranger] = ["Piercing Volley", "Hunter's Mark", "Gale Shot", "Rain of Arrows", "Deadeye", "Snare Trap", "Twin Shot", "Basic Attack"],
        [CharacterClass.Assassin] = ["Shadow Fang", "Ambush", "Venom Edge", "Silent Rend", "Phantom Dance", "Throat Cut", "Night Lunge", "Basic Attack"],
        [CharacterClass.Elementalist] = ["Spirit Lash", "Tempest Orb", "Stone Wake", "Flare Spirit", "Frost Tether", "Gust Barrage", "Spirit Pact", "Basic Attack"],
        [CharacterClass.Sorcerer] = ["Flame Lance", "Glacial Spike", "Arcane Torrent", "Inferno", "Frostbite Nova", "Meteor Fall", "Mana Burst", "Basic Attack"],
        [CharacterClass.Cleric] = ["Smite", "Chastise", "Hallowed Bolt", "Thunderous Verdict", "Purging Light", "Healing Light", "Renewal", "Basic Attack"],
        [CharacterClass.Chanter] = ["Mantra Strike", "Resonant Blow", "Echo Staff", "Rhythm of Battle", "Pulse Wave", "Ward Chant", "Rising Hymn", "Basic Attack"],
        [CharacterClass.Brawler] = ["Iron Jab", "Rising Knee", "Thunder Palm", "Hundred Fists", "Shockwave Kick", "Grapple Throw", "Dragon Uppercut", "Basic Attack"],
    };

    private static readonly Dictionary<uint, (string Name, bool Boss, bool Dummy)> Npcs = new()
    {
        [BossNpc] = ("Warden of the Ashen Spire", true, false),
        [AddNpc] = ("Ashen Thrall", false, false),
        [DummyNpc] = ("Training Dummy", false, true),
        [SecondBossNpc] = ("Veyra, the Hollow Queen", true, false),
        [2_490_011] = ("Gloomfang Matriarch", true, false),
        [2_490_012] = ("Ironbound Colossus", true, false),
        // Demo field bosses of Verdant Reach (block 2491, see DemoTimers)
        [2_491_001] = ("Mossback Grazer", true, false),
        [2_491_002] = ("Thornwing Harrier", true, false),
        [2_491_003] = ("Captain Orrevan", true, false),
        [2_491_004] = ("Bloomrot Behemoth", true, false),
        [2_491_005] = ("Stormcaller Ysmir", true, false),
        [2_491_006] = ("The Verdant Tyrant", true, false),
    };

    /// <summary>Open-world map of the demo field bosses and their NPC-code block.</summary>
    public const uint FieldBossMap = 400_010;
    public const uint FieldBossBlock = 2_491;

    private static readonly Dictionary<uint, string> Maps = new()
    {
        [DemoMap] = "Ashen Spire Sanctum",
        [600_052] = "Hollow Court",
        [600_063] = "Gloomfang Warrens",
        [400_010] = "Verdant Reach",
    };

    public GameLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            LanguageChanged?.Invoke();
        }
    }

    public event Action? LanguageChanged;

    /// <summary>Skill id for the n-th (0-based) demo skill of a class.</summary>
    public static uint SkillId(CharacterClass c, int index)
    {
        int prefix = c switch
        {
            CharacterClass.Gladiator => 11, CharacterClass.Templar => 12, CharacterClass.Assassin => 13, CharacterClass.Ranger => 14,
            CharacterClass.Sorcerer => 15, CharacterClass.Elementalist => 16, CharacterClass.Cleric => 17, CharacterClass.Chanter => 18,
            CharacterClass.Brawler => 19, _ => 10,
        };
        return (uint)(prefix * 1_000_000 + (index + 1) * 10_000 + 1);
    }

    public string GetSkillName(uint skillId)
    {
        var cls = SkillIds.ClassOf(skillId);
        int idx = (int)(skillId / 10_000 % 100) - 1;
        if (ClassSkills.TryGetValue(cls, out var names) && idx >= 0 && idx < names.Length) return Localize(names[idx]);
        return $"Skill {skillId}";
    }

    public uint GetSkillGroupKey(uint skillId) => SkillIds.BaseId(skillId);
    public string? GetSkillIconKey(uint skillId) => null;
    public CharacterClass GetSkillClass(uint skillId) => SkillIds.ClassOf(skillId);

    public bool IsHealSkill(uint skillId)
    {
        var name = GetSkillName(skillId);
        return name.Contains("Healing", StringComparison.Ordinal) || name.Contains("Renewal", StringComparison.Ordinal);
    }

    public bool IsDotSkill(uint skillId) => GetSkillName(skillId) is "Venom Edge" or "Inferno" or "Frostbite Nova";

    public NpcInfo? GetNpc(uint npcCode) =>
        Npcs.TryGetValue(npcCode, out var n) ? new NpcInfo(npcCode, Localize(n.Name), n.Boss, n.Dummy, DemoMap) : null;

    public string GetNpcName(uint npcCode) => Npcs.TryGetValue(npcCode, out var n) ? Localize(n.Name) : $"NPC {npcCode}";

    public string? GetMapName(uint mapId) => Maps.TryGetValue(mapId, out var m) ? Localize(m) : null;

    public bool IsInstanceMap(uint mapId) => mapId is >= 600_000 and <= 699_999;

    public uint? GetFieldBossBlock(uint mapId) => mapId == FieldBossMap ? FieldBossBlock : null;

    public uint? GetFieldBossNpcCode(uint block, int place) =>
        block == FieldBossBlock && Npcs.ContainsKey(block * 1000 + (uint)place) ? block * 1000 + (uint)place : null;

    public string? GetServerName(ushort serverId) => serverId switch
    {
        DemoServer => "Siel",
        2306 => "Israphel",
        2307 => "Nezekan",
        _ => null,
    };

    public string GetClassName(CharacterClass characterClass) => _language switch
    {
        GameLanguage.Korean => characterClass switch
        {
            CharacterClass.Gladiator => "검성", CharacterClass.Templar => "수호성", CharacterClass.Ranger => "궁성",
            CharacterClass.Assassin => "살성", CharacterClass.Elementalist => "정령성", CharacterClass.Sorcerer => "마도성",
            CharacterClass.Cleric => "치유성", CharacterClass.Chanter => "호법성", CharacterClass.Brawler => "권성", _ => "알 수 없음",
        },
        _ => characterClass == CharacterClass.Unknown ? "Unknown" : characterClass.ToString(),
    };

    // Demo names are English only; other languages get a marker so the language switch is visibly wired.
    private string Localize(string english) => _language switch
    {
        GameLanguage.Korean => english + " (KO)",
        GameLanguage.ChineseSimplified => english + " (简)",
        GameLanguage.ChineseTraditional => english + " (繁)",
        _ => english,
    };
}
