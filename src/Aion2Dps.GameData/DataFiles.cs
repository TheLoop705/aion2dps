using System.Collections.Frozen;
using System.Text.Json;
using Aion2Dps.Contracts;

namespace Aion2Dps.GameData;

/// <summary>NPC flags from the English table (flags are language independent).</summary>
internal readonly record struct NpcFlags(bool IsBoss, bool IsDummy, uint? DungeonId);

/// <summary>Names of one language (skills, NPCs, dungeons). Immutable after construction.</summary>
internal sealed class LanguageTables
{
    public static readonly LanguageTables Empty = new(
        FrozenDictionary<uint, string>.Empty, FrozenDictionary<uint, string>.Empty, FrozenDictionary<uint, string>.Empty);

    public LanguageTables(FrozenDictionary<uint, string> skills, FrozenDictionary<uint, string> npcs, FrozenDictionary<uint, string> maps)
    {
        Skills = skills;
        Npcs = npcs;
        Maps = maps;
    }

    public FrozenDictionary<uint, string> Skills { get; }
    public FrozenDictionary<uint, string> Npcs { get; }
    public FrozenDictionary<uint, string> Maps { get; }
}

/// <summary>JSON readers for the files under <c>data/</c>. Every reader tolerates a missing or malformed file
/// (logs and returns an empty table) so a damaged install never breaks the meter.</summary>
internal static class DataFiles
{
    private const string Area = "GameData";

    public static string LanguageCode(GameLanguage language) => language switch
    {
        GameLanguage.Korean => "ko",
        GameLanguage.ChineseSimplified => "zh-Hans",
        GameLanguage.ChineseTraditional => "zh-Hant",
        _ => "en",
    };

    private static JsonDocument? Open(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                AppLog.Warn(Area, $"Data file not found: {path}");
                return null;
            }
            return JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex)
        {
            AppLog.Error(Area, $"Failed to read {path}", ex);
            return null;
        }
    }

    private static bool TryKey(string name, out uint id) => uint.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id);

    private static bool TryUInt(JsonElement e, out uint value)
    {
        value = 0;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetUInt32(out value),
            JsonValueKind.String => TryKey(e.GetString() ?? "", out value),
            _ => false,
        };
    }

    /// <summary><c>{ "id": "name" }</c> or <c>{ "id": { "name": "…" } }</c>.</summary>
    public static FrozenDictionary<uint, string> ReadNameMap(string path)
    {
        using var doc = Open(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return FrozenDictionary<uint, string>.Empty;
        var map = new Dictionary<uint, string>(capacity: 10_000);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!TryKey(p.Name, out uint id)) continue;
            string? name = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Object when p.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(name)) map[id] = name;
        }
        return map.ToFrozenDictionary();
    }

    /// <summary>NPC flags from <c>npcs/en.json</c>.</summary>
    public static FrozenDictionary<uint, NpcFlags> ReadNpcFlags(string path)
    {
        using var doc = Open(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return FrozenDictionary<uint, NpcFlags>.Empty;
        var map = new Dictionary<uint, NpcFlags>(capacity: 10_000);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!TryKey(p.Name, out uint id) || p.Value.ValueKind != JsonValueKind.Object) continue;
            bool boss = p.Value.TryGetProperty("isBoss", out var b) && b.ValueKind == JsonValueKind.True;
            bool dummy = p.Value.TryGetProperty("isDummy", out var d) && d.ValueKind == JsonValueKind.True;
            uint? dungeon = p.Value.TryGetProperty("dungeonId", out var dg) && TryUInt(dg, out uint dgId) ? dgId : null;
            map[id] = new NpcFlags(boss, dummy, dungeon);
        }
        return map.ToFrozenDictionary();
    }

    /// <summary>A JSON array of ids (numbers or numeric strings), or an object whose first array property holds them.</summary>
    public static FrozenSet<uint> ReadIdSet(string path, string? arrayProperty = null)
    {
        using var doc = Open(path);
        if (doc is null) return FrozenSet<uint>.Empty;
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (arrayProperty is null || !root.TryGetProperty(arrayProperty, out root)) return FrozenSet<uint>.Empty;
        }
        if (root.ValueKind != JsonValueKind.Array) return FrozenSet<uint>.Empty;
        var set = new HashSet<uint>();
        foreach (var e in root.EnumerateArray())
            if (TryUInt(e, out uint id)) set.Add(id);
        return set.ToFrozenSet();
    }

    /// <summary><c>servers.json</c>: <c>{ "servers": { "1001": { "en": "Siel", "ko": "…" } } }</c> → id → (lang → name).</summary>
    public static FrozenDictionary<ushort, FrozenDictionary<string, string>> ReadServers(string path)
    {
        using var doc = Open(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Object)
            return FrozenDictionary<ushort, FrozenDictionary<string, string>>.Empty;
        var map = new Dictionary<ushort, FrozenDictionary<string, string>>();
        foreach (var p in servers.EnumerateObject())
        {
            if (!ushort.TryParse(p.Name, out ushort id)) continue;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (p.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var l in p.Value.EnumerateObject())
                    if (l.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(l.Value.GetString()))
                        names[l.Name] = l.Value.GetString()!;
            }
            else if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
            {
                names["en"] = p.Value.GetString()!;
            }
            if (names.Count > 0) map[id] = names.ToFrozenDictionary(StringComparer.Ordinal);
        }
        return map.ToFrozenDictionary();
    }

    /// <summary><c>skill_icons.json</c>: <c>{ "1102": "ICON_…" }</c>.</summary>
    public static FrozenDictionary<uint, string> ReadIcons(string path) => ReadNameMap(path);

    /// <summary><c>field_boss_maps.json</c> → map id → (lang → name). Only maps with at least one name are kept.</summary>
    public static FrozenDictionary<uint, FrozenDictionary<string, string>> ReadFieldBossMapNames(string path)
    {
        using var doc = Open(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("maps", out var maps) || maps.ValueKind != JsonValueKind.Object)
            return FrozenDictionary<uint, FrozenDictionary<string, string>>.Empty;
        var result = new Dictionary<uint, FrozenDictionary<string, string>>();
        foreach (var p in maps.EnumerateObject())
        {
            if (!TryKey(p.Name, out uint id) || p.Value.ValueKind != JsonValueKind.Object) continue;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var l in p.Value.EnumerateObject())
                if (l.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(l.Value.GetString()))
                    names[l.Name] = l.Value.GetString()!;
            if (names.Count > 0) result[id] = names.ToFrozenDictionary(StringComparer.Ordinal);
        }
        return result.ToFrozenDictionary();
    }

    /// <summary><c>field_boss_maps.json</c> → map id → NPC-code block (code / 1000) of the map's field bosses.</summary>
    public static FrozenDictionary<uint, uint> ReadFieldBossBlocks(string path)
    {
        using var doc = Open(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("maps", out var maps) || maps.ValueKind != JsonValueKind.Object)
            return FrozenDictionary<uint, uint>.Empty;
        var result = new Dictionary<uint, uint>();
        foreach (var p in maps.EnumerateObject())
        {
            if (!TryKey(p.Name, out uint id) || p.Value.ValueKind != JsonValueKind.Object) continue;
            if (p.Value.TryGetProperty("block", out var b) && TryUInt(b, out uint block) && block > 0) result[id] = block;
        }
        return result.ToFrozenDictionary();
    }

    /// <summary><c>heal_skill_families.json</c>: base-id families and exact ids.</summary>
    public static (FrozenSet<uint> Families, FrozenSet<uint> Exact) ReadHealFamilies(string path)
    {
        using var doc = Open(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return (FrozenSet<uint>.Empty, FrozenSet<uint>.Empty);
        return (ReadIdList(doc.RootElement, "families", "baseId"), ReadIdList(doc.RootElement, "exact", "id"));

        static FrozenSet<uint> ReadIdList(JsonElement root, string property, string idProperty)
        {
            if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return FrozenSet<uint>.Empty;
            var set = new HashSet<uint>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(idProperty, out var idEl) && TryUInt(idEl, out uint id)) set.Add(id);
                else if (TryUInt(e, out uint plain)) set.Add(plain);
            }
            return set.ToFrozenSet();
        }
    }
}
