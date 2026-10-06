using System.Collections.Frozen;
using System.Diagnostics;
using Aion2Dps.Contracts;

namespace Aion2Dps.GameData;

/// <summary>
/// <see cref="IGameData"/> backed by the JSON tables under <c>data/</c> (PROTOCOL.md §15).
/// English tables and the language-independent tables load in the constructor; the other languages load lazily on
/// first use. All lookups are lock-free reads of frozen collections and are safe from any thread. Unknown ids never
/// throw: missing keys fall back to English, then to a readable placeholder.
/// </summary>
public sealed class GameDataStore : IGameData
{
    private const string Area = "GameData";

    /// <summary>NPC codes known to be training dummies even if the table does not flag them (§11.1).</summary>
    private static readonly FrozenSet<uint> ExtraDummyCodes = new uint[] { 2_400_032, 2_400_035, 2_090_773, 2_300_229 }.ToFrozenSet();

    private static readonly string[] DummyNames = ["Training Scarecrow", "Punching Bag"];

    private readonly string _dataDirectory;
    private readonly LanguageState _english;
    private readonly Lazy<LanguageState>[] _languages;
    private readonly FrozenDictionary<uint, NpcFlags> _npcFlags;
    private readonly FrozenDictionary<uint, uint> _groupKeys;
    private readonly FrozenSet<uint> _dotIds;
    private readonly FrozenSet<uint> _healingIds;
    private readonly FrozenSet<uint> _healFamilies;
    private readonly FrozenSet<uint> _healExact;
    private readonly FrozenDictionary<uint, string> _icons;
    private readonly FrozenSet<uint> _openWorldMaps;
    private readonly FrozenDictionary<uint, FrozenDictionary<string, string>> _openWorldMapNames;
    private readonly FrozenDictionary<ushort, FrozenDictionary<string, string>> _servers;

    private readonly Lock _gate = new();
    private volatile LanguageState _current;
    private int _language; // GameLanguage, accessed with Volatile/Interlocked

    /// <summary>Loads the tables from <paramref name="dataDirectory"/> (the folder that contains <c>i18n/</c>).</summary>
    public GameDataStore(string dataDirectory, GameLanguage language = GameLanguage.English)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _dataDirectory = dataDirectory;
        var sw = Stopwatch.StartNew();

        _npcFlags = DataFiles.ReadNpcFlags(I18nPath("npcs", GameLanguage.English));
        _english = BuildLanguage(GameLanguage.English);
        _languages = new Lazy<LanguageState>[4];
        foreach (GameLanguage l in Enum.GetValues<GameLanguage>())
        {
            var lang = l;
            _languages[(int)l] = lang == GameLanguage.English
                ? new Lazy<LanguageState>(_english)
                : new Lazy<LanguageState>(() => BuildLanguage(lang), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        _groupKeys = BuildGroupKeys(_english.Tables.Skills);
        _dotIds = DataFiles.ReadIdSet(DataPath("dot_skill_ids.json"));
        _healingIds = DataFiles.ReadIdSet(DataPath("healing_skill_ids.json"));
        (_healFamilies, _healExact) = DataFiles.ReadHealFamilies(DataPath("heal_skill_families.json"));
        _icons = DataFiles.ReadIcons(DataPath("skill_icons.json"));
        _openWorldMaps = DataFiles.ReadIdSet(DataPath("open_world_maps.json"), "maps");
        _openWorldMapNames = DataFiles.ReadFieldBossMapNames(DataPath("field_boss_maps.json"));
        _servers = DataFiles.ReadServers(DataPath("servers.json"));

        _language = (int)language;
        _current = StateFor(language);
        AppLog.Info(Area, $"Loaded game data from {dataDirectory} in {sw.ElapsedMilliseconds} ms " +
                          $"({_english.Tables.Skills.Count} skills, {_npcFlags.Count} NPCs, {_english.Tables.Maps.Count} maps, {_servers.Count} servers)");
    }

    /// <summary>Loads from <c>AppContext.BaseDirectory/data</c>.</summary>
    public static GameDataStore LoadDefault(GameLanguage language = GameLanguage.English) =>
        new(Path.Combine(AppContext.BaseDirectory, "data"), language);

    /// <summary>The folder the tables were loaded from.</summary>
    public string DataDirectory => _dataDirectory;

    /// <inheritdoc />
    public event Action? LanguageChanged;

    /// <inheritdoc />
    public GameLanguage Language
    {
        get => (GameLanguage)Volatile.Read(ref _language);
        set
        {
            if (!Enum.IsDefined(value)) value = GameLanguage.English;
            var state = StateFor(value); // load before publishing, so readers never see a half-built language
            int old;
            lock (_gate)
            {
                old = _language;
                _current = state;
                Volatile.Write(ref _language, (int)value);
            }
            if (old == (int)value) return;
            try { LanguageChanged?.Invoke(); }
            catch (Exception ex) { AppLog.Error(Area, "LanguageChanged handler failed", ex); }
        }
    }

    /// <summary>Forces every language to load now (otherwise they load on first use).</summary>
    public void PreloadAllLanguages()
    {
        foreach (var l in _languages) _ = l.Value;
    }

    // ---------------------------------------------------------------- skills

    /// <inheritdoc />
    public string GetSkillName(uint skillId)
    {
        var cur = _current;
        uint id = SkillIds.Normalize(skillId);
        var kind = SkillIds.GetKind(id);

        if (TryName(cur, id, out var name)) return name;
        if (id == SkillIds.Dodge) return LocalizedText.DodgeName(cur.Language);

        if (kind is SkillKind.Player or SkillKind.Link)
        {
            uint baseId = SkillIds.BaseId(id);
            if (baseId != id && TryName(cur, baseId, out name)) return name;
        }
        uint tens = id / 10 * 10;
        if (tens != id && TryName(cur, tens, out name)) return name;

        return kind switch
        {
            SkillKind.Npc => LocalizedText.MonsterAttackName(cur.Language),
            SkillKind.Theostone => LocalizedText.TheostoneName(cur.Language),
            SkillKind.Spirit => LocalizedText.SpiritAttackName(cur.Language),
            SkillKind.Link => LocalizedText.SpiritLinkName(cur.Language),
            _ => LocalizedText.UnknownSkillName(id, cur.Language),
        };
    }

    private bool TryName(LanguageState cur, uint id, out string name)
    {
        if (cur.Tables.Skills.TryGetValue(id, out name!)) return true;
        return !ReferenceEquals(cur, _english) && _english.Tables.Skills.TryGetValue(id, out name!);
    }

    /// <inheritdoc />
    /// <remarks>§15.2: the base id when the variant's English name equals the base name (or the variant is unnamed);
    /// otherwise the variant keeps its own group. Variants of one base that share a distinct name (e.g. all
    /// "Basic Attack" ids of a base without a base entry) are merged under their smallest id. Language independent.</remarks>
    public uint GetSkillGroupKey(uint skillId)
    {
        uint id = SkillIds.Normalize(skillId);
        if (_groupKeys.TryGetValue(id, out uint key)) return key;
        var kind = SkillIds.GetKind(id);
        if (kind is SkillKind.Player or SkillKind.Link && id != SkillIds.Dodge)
            return SkillIds.BaseId(id); // unnamed variant → its base
        return id;
    }

    private static FrozenDictionary<uint, uint> BuildGroupKeys(FrozenDictionary<uint, string> en)
    {
        // Smallest id per (base, name) among named player/link skills.
        var canonical = new Dictionary<(uint Base, string Name), uint>();
        foreach (var (id, name) in en)
        {
            if (SkillIds.GetKind(id) is not (SkillKind.Player or SkillKind.Link)) continue;
            var k = (SkillIds.BaseId(id), name);
            if (!canonical.TryGetValue(k, out uint existing) || id < existing) canonical[k] = id;
        }

        var keys = new Dictionary<uint, uint>(en.Count);
        foreach (var (id, name) in en)
        {
            if (SkillIds.GetKind(id) is not (SkillKind.Player or SkillKind.Link)) continue;
            if (id == SkillIds.Dodge) { keys[id] = id; continue; }
            uint baseId = SkillIds.BaseId(id);
            if (en.TryGetValue(baseId, out var baseName) && string.Equals(baseName, name, StringComparison.Ordinal))
                keys[id] = baseId;
            else
                keys[id] = canonical[(baseId, name)];
        }
        return keys.ToFrozenDictionary();
    }

    /// <inheritdoc />
    public string? GetSkillIconKey(uint skillId)
    {
        uint id = SkillIds.Normalize(skillId);
        if (id < 10_000 && _icons.TryGetValue(id, out var direct)) return direct;
        return _icons.TryGetValue(id / 10_000, out var key) ? key : null; // icon key = id / 10000 (§15.2)
    }

    /// <inheritdoc />
    public CharacterClass GetSkillClass(uint skillId) => SkillIds.ClassOf(SkillIds.Normalize(skillId));

    /// <inheritdoc />
    /// <remarks>True for <c>healing_skill_ids.json</c> (exact or base id), the curated families in
    /// <c>heal_skill_families.json</c> (base id) and its exact ids (NPC heals, potions, heal variants of damage families).
    /// Base-id matching only applies to 8-digit class skills.</remarks>
    public bool IsHealSkill(uint skillId)
    {
        uint id = SkillIds.Normalize(skillId);
        if (_healingIds.Contains(id) || _healExact.Contains(id)) return true;
        if (id is >= 10_000_000 and < 20_000_000)
        {
            uint baseId = SkillIds.BaseId(id);
            return _healingIds.Contains(baseId) || _healFamilies.Contains(baseId);
        }
        return false;
    }

    /// <inheritdoc />
    public bool IsDotSkill(uint skillId)
    {
        uint id = SkillIds.Normalize(skillId);
        if (_dotIds.Contains(id)) return true;
        return id is >= 10_000_000 and < 20_000_000 && _dotIds.Contains(SkillIds.BaseId(id));
    }

    // ---------------------------------------------------------------- NPCs, maps, servers

    /// <inheritdoc />
    public NpcInfo? GetNpc(uint npcCode) => _current.Npcs.TryGetValue(npcCode, out var info) ? info : null;

    /// <inheritdoc />
    public string GetNpcName(uint npcCode) =>
        _current.Npcs.TryGetValue(npcCode, out var info) ? info.Name : $"NPC {npcCode}";

    /// <inheritdoc />
    public string? GetMapName(uint mapId)
    {
        var cur = _current;
        if (cur.Tables.Maps.TryGetValue(mapId, out var name)) return name;
        if (_english.Tables.Maps.TryGetValue(mapId, out name)) return name;
        if (_openWorldMapNames.TryGetValue(mapId, out var names)) return Pick(names, cur.Language);
        return null;
    }

    /// <inheritdoc />
    public bool IsInstanceMap(uint mapId) => mapId is >= 600_000 and <= 699_999;

    /// <summary>True for overworld maps listed in <c>open_world_maps.json</c>.</summary>
    public bool IsOpenWorldMap(uint mapId) => _openWorldMaps.Contains(mapId);

    /// <inheritdoc />
    public string? GetServerName(ushort serverId) =>
        _servers.TryGetValue(serverId, out var names) ? Pick(names, Language) : null;

    /// <inheritdoc />
    public string GetClassName(CharacterClass characterClass) => LocalizedText.ClassName(characterClass, Language);

    private static string? Pick(FrozenDictionary<string, string> names, GameLanguage language)
    {
        if (names.TryGetValue(DataFiles.LanguageCode(language), out var n)) return n;
        // Simplified/Traditional Chinese stand in for each other before falling back to English.
        if (language == GameLanguage.ChineseSimplified && names.TryGetValue("zh-Hant", out n)) return n;
        if (language == GameLanguage.ChineseTraditional && names.TryGetValue("zh-Hans", out n)) return n;
        if (names.TryGetValue("en", out n)) return n;
        foreach (var v in names.Values) return v;
        return null;
    }

    // ---------------------------------------------------------------- loading

    private string DataPath(string file) => Path.Combine(_dataDirectory, file);

    private string I18nPath(string table, GameLanguage language) =>
        Path.Combine(_dataDirectory, "i18n", table, DataFiles.LanguageCode(language) + ".json");

    private LanguageState StateFor(GameLanguage language) =>
        (int)language is >= 0 and < 4 ? _languages[(int)language].Value : _english;

    private LanguageState BuildLanguage(GameLanguage language)
    {
        var sw = Stopwatch.StartNew();
        var tables = new LanguageTables(
            DataFiles.ReadNameMap(I18nPath("skills", language)),
            DataFiles.ReadNameMap(I18nPath("npcs", language)),
            DataFiles.ReadNameMap(I18nPath("dungeons", language)));
        var englishNpcNames = language == GameLanguage.English ? tables.Npcs : _english.Tables.Npcs;

        var npcs = new Dictionary<uint, NpcInfo>(_npcFlags.Count + ExtraDummyCodes.Count);
        foreach (var (code, flags) in _npcFlags)
        {
            englishNpcNames.TryGetValue(code, out var enName);
            string name = tables.Npcs.TryGetValue(code, out var n) ? n : enName ?? $"NPC {code}";
            bool dummy = flags.IsDummy || ExtraDummyCodes.Contains(code) || IsDummyName(enName);
            npcs[code] = new NpcInfo(code, name, flags.IsBoss, dummy, flags.DungeonId);
        }
        foreach (uint code in ExtraDummyCodes)
            if (!npcs.ContainsKey(code))
                npcs[code] = new NpcInfo(code, $"NPC {code}", false, true, null);

        AppLog.Debug(Area, $"Language {DataFiles.LanguageCode(language)} loaded in {sw.ElapsedMilliseconds} ms");
        return new LanguageState(language, tables, npcs.ToFrozenDictionary());
    }

    private static bool IsDummyName(string? englishName)
    {
        if (string.IsNullOrEmpty(englishName)) return false;
        foreach (var d in DummyNames)
            if (englishName.Contains(d, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private sealed record LanguageState(GameLanguage Language, LanguageTables Tables, FrozenDictionary<uint, NpcInfo> Npcs);
}
