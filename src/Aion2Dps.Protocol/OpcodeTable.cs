using System.Collections.Frozen;
using System.Text.Json;
using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

/// <summary>
/// The opcode table the decoders dispatch by (PROTOCOL.md §7). Values are in wire byte order (<c>04 38</c> = 0x0438),
/// like <see cref="Opcodes"/>. Defaults come from <see cref="Opcodes"/>; <see cref="LoadOrDefault(string)"/> overrides
/// them from <c>data/protocol/opcodes.json</c> so a game patch that moves an opcode is a data fix.
/// Immutable; thread-safe.
/// </summary>
public sealed class OpcodeTable
{
    /// <summary>JSON keys, in the order of <see cref="ToDictionary"/>.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "heartbeat", "damage", "dotTick", "entityStats", "spawn", "selfInfo", "playerInfo", "kill", "death", "mapLoad",
        "teleport", "partyRoster", "otherPartyRoster", "hpUpdate", "buffApplied", "buffApplied2", "buffRemoved", "cast",
        "ping", "globalIdLink", "partyScope", "battleToggle", "fieldBossList",
    ];

    /// <summary>
    /// Opcodes observed on the Global client (an EU traffic census, PROTOCOL.md §4.4/§7, 333 entries). Used only to
    /// judge resync candidates ("this looks like a real frame"), never for decoding. Includes the Draupnir transition
    /// opcodes observed in October 2026. Extend it through the optional
    /// <c>"syncOpcodes"</c> array in opcodes.json.
    /// </summary>
    public static readonly IReadOnlyList<ushort> CensusOpcodes =
    [
        0x0036, 0x0039, 0x0044, 0x0051, 0x0056, 0x0060, 0x0061, 0x008D, 0x0090, 0x0092, 0x00E2, 0x0138, 0x0140, 0x0143,
        0x0151, 0x0160, 0x0161, 0x018C, 0x0191, 0x0195, 0x0197, 0x0238, 0x0240, 0x0243, 0x0244, 0x0261, 0x028D, 0x0290,
        0x0297, 0x02E0, 0x0336, 0x0338, 0x0343, 0x0344, 0x0361, 0x038A, 0x038D, 0x0390, 0x0438, 0x0461, 0x048D, 0x04E0,
        0x0538, 0x0540, 0x0551, 0x0561, 0x058A, 0x058D, 0x0590, 0x05E0, 0x0638, 0x0690, 0x0740, 0x0761, 0x078D, 0x0795,
        0x0856, 0x0861, 0x088D, 0x0895, 0x0938, 0x0961, 0x098D, 0x0990, 0x0994, 0x0A40, 0x0B61, 0x0B90, 0x0B94, 0x0BE3,
        0x0C38, 0x0C61, 0x0C90, 0x0C91, 0x0D90, 0x0E38, 0x0E92, 0x0F38, 0x0F57, 0x0F8D, 0x108D, 0x1136, 0x1138, 0x1156, 0x118D,
        0x1257, 0x1338, 0x1356, 0x1357, 0x13E2, 0x1438, 0x1460, 0x1538, 0x1556, 0x1557, 0x158D, 0x15E2, 0x1638, 0x1656,
        0x1692, 0x1738, 0x1756, 0x1838, 0x1938, 0x1956, 0x1A37, 0x1A92, 0x1B37, 0x1B56, 0x1B92, 0x1C37, 0x1C38, 0x1C56,
        0x1C8D, 0x1C92, 0x1D37, 0x1D56, 0x1E37, 0x1E56, 0x1F37, 0x1F56, 0x1F97, 0x2036, 0x2037, 0x2092, 0x2136, 0x2137, 0x2138,
        0x2156, 0x218D, 0x2193, 0x2237, 0x2292, 0x2336, 0x2337, 0x2356, 0x23E2, 0x23E3, 0x2437, 0x24E2, 0x24E3, 0x2537, 0x25E2,
        0x2637, 0x2656, 0x268D, 0x26E2, 0x2837, 0x2937, 0x29E2, 0x2A37, 0x2A38, 0x2AE3, 0x2B37, 0x2B38, 0x2C37, 0x2C38,
        0x2C8D, 0x2D37, 0x2D38, 0x2D57, 0x2D92, 0x2E37, 0x2E57, 0x2E92, 0x2EE2, 0x2F37, 0x2F92, 0x2FE2, 0x3037, 0x3093,
        0x3137, 0x3138, 0x318D, 0x3192, 0x3193, 0x3237, 0x3238, 0x3292, 0x3336, 0x3337, 0x3356, 0x338A, 0x338D, 0x3392,
        0x33E2, 0x3436, 0x3437, 0x3438, 0x348D, 0x3536, 0x3537, 0x3538, 0x358D, 0x3592, 0x35E3, 0x3637, 0x3638, 0x368D,
        0x3736, 0x3737, 0x378D, 0x3792, 0x37E2, 0x3836, 0x3837, 0x3936, 0x3937, 0x398A, 0x3A36, 0x3A37, 0x3AE3, 0x3B36,
        0x3B37, 0x3B38, 0x3B8D, 0x3BE3, 0x3C37, 0x3D38, 0x3DE3, 0x3E36, 0x3E37, 0x3E38, 0x3E8D, 0x3EE3, 0x3F37, 0x3F56,
        0x3F8D, 0x4038, 0x4136, 0x4137, 0x4156, 0x418D, 0x41E2, 0x4236, 0x4237, 0x42E3, 0x4337, 0x4356, 0x43E2, 0x43E3,
        0x4437, 0x448D, 0x4536, 0x4537, 0x458A, 0x4636, 0x4637, 0x468D, 0x4736, 0x4738, 0x4838, 0x4856, 0x48E2, 0x4936,
        0x4937, 0x4938, 0x49E3, 0x4A36, 0x4AE2, 0x4B36, 0x4B56, 0x4B8D, 0x4C37, 0x4D56, 0x4D8D, 0x4E36, 0x4E8D, 0x4EE3,
        0x51E2, 0x528D, 0x538D, 0x5636, 0x56E2, 0x5736, 0x578D, 0x57E2, 0x588D, 0x58E2, 0x5AE2, 0x5B8D, 0x5C38, 0x5C8D,
        0x5CE2, 0x62E2, 0x63E2, 0x6D8D, 0x7256, 0x728D, 0x7356, 0x748D, 0x74E2, 0x7556, 0x7656, 0x76E2, 0x77E2, 0x7956,
        0x7C56, 0x7D56, 0x7DE2, 0x7E8D, 0x7EE2, 0x8256, 0x82E2, 0x8356, 0x838D, 0x8456, 0x8A56, 0x8C8D, 0x8CE2, 0x8DE2,
        0x8E56, 0x918D, 0x928D, 0x938D, 0x93E2, 0x95E2, 0x96E2, 0xA5FF, 0xA6FF, 0xA856, 0xA9FF, 0xAB56, 0xAC56, 0xACFF,
        0xAD56, 0xADFF, 0xAF8A, 0xB08A, 0xB18A, 0xB656, 0xB88A, 0xFFFF,
    ];

    private static readonly ushort[] DefaultValues =
    [
        Opcodes.Heartbeat, Opcodes.Damage, Opcodes.DotTick, Opcodes.EntityStats, Opcodes.Spawn, Opcodes.SelfInfo,
        Opcodes.PlayerInfo, Opcodes.Kill, Opcodes.Death, Opcodes.MapLoad, Opcodes.Teleport, Opcodes.PartyRoster,
        Opcodes.OtherPartyRoster, Opcodes.HpUpdate, Opcodes.BuffApplied, Opcodes.BuffApplied2, Opcodes.BuffRemoved,
        Opcodes.Cast, Opcodes.Ping, Opcodes.GlobalIdLink, Opcodes.PartyScope, Opcodes.BattleToggle, Opcodes.FieldBossList,
    ];

    private readonly ushort[] _values = (ushort[])DefaultValues.Clone();
    private readonly FrozenSet<ushort> _known;

    public ushort Heartbeat => _values[0];
    public ushort Damage => _values[1];
    public ushort DotTick => _values[2];
    public ushort EntityStats => _values[3];
    public ushort Spawn => _values[4];
    public ushort SelfInfo => _values[5];
    public ushort PlayerInfo => _values[6];
    public ushort Kill => _values[7];
    public ushort Death => _values[8];
    public ushort MapLoad => _values[9];
    public ushort Teleport => _values[10];
    public ushort PartyRoster => _values[11];
    public ushort OtherPartyRoster => _values[12];
    public ushort HpUpdate => _values[13];
    public ushort BuffApplied => _values[14];
    public ushort BuffApplied2 => _values[15];
    public ushort BuffRemoved => _values[16];
    public ushort Cast => _values[17];
    public ushort Ping => _values[18];
    public ushort GlobalIdLink => _values[19];
    public ushort PartyScope => _values[20];
    public ushort BattleToggle => _values[21];
    public ushort FieldBossList => _values[22];

    /// <summary>The bundle opcode is fixed (<c>FF FF</c>, §5).</summary>
    public ushort Bundle => Opcodes.Bundle;

    /// <summary>Where the table came from ("defaults" or the file path).</summary>
    public string Source { get; }

    /// <summary>Extra opcodes (from the file's "syncOpcodes") added to the resync census.</summary>
    public IReadOnlyList<ushort> ExtraKnownOpcodes { get; }

    private OpcodeTable(IReadOnlyDictionary<string, ushort>? overrides, IEnumerable<ushort>? extraKnown, string source)
    {
        Source = source;
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides) Apply(key, value);
        }

        var extra = extraKnown?.ToArray() ?? Array.Empty<ushort>();
        ExtraKnownOpcodes = extra;
        var known = new HashSet<ushort>(CensusOpcodes);
        known.UnionWith(ToDictionary().Values);
        known.UnionWith(extra);
        known.Add(Opcodes.Bundle);
        _known = known.ToFrozenSet();
    }

    /// <summary>The compiled-in defaults (<see cref="Opcodes"/>).</summary>
    public static OpcodeTable Default { get; } = new(null, null, "defaults");

    /// <summary>Default location: <c>&lt;AppContext.BaseDirectory&gt;/data/protocol/opcodes.json</c>.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "protocol", "opcodes.json");

    /// <summary>Loads <see cref="DefaultPath"/>, falling back to <see cref="Default"/>.</summary>
    public static OpcodeTable LoadOrDefault() => LoadOrDefault(DefaultPath);

    /// <summary>
    /// Loads the opcode table from a JSON file (keys: see <see cref="Keys"/>, values like <c>"04 38"</c>). Keys that are
    /// missing or invalid keep their defaults; unknown keys are ignored. A missing or unreadable file gives
    /// <see cref="Default"/> (a warning is logged). Never throws.
    /// </summary>
    public static OpcodeTable LoadOrDefault(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                AppLog.Warn("Protocol", $"Opcode table not found at '{path}', using compiled-in defaults.");
                return Default;
            }

            return TryParse(File.ReadAllText(path), out var table, path) ? table! : Default;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Protocol", $"Opcode table '{path}' could not be read ({ex.Message}), using compiled-in defaults.");
            return Default;
        }
    }

    /// <summary>Parses opcodes.json content. Returns false (and logs) on malformed JSON.</summary>
    public static bool TryParse(string json, out OpcodeTable? table, string source = "json")
    {
        table = null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                AppLog.Warn("Protocol", $"Opcode table '{source}' is not a JSON object, using defaults.");
                return false;
            }

            var overrides = new Dictionary<string, ushort>(StringComparer.Ordinal);
            var extra = new List<ushort>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("syncOpcodes") && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (TryReadOpcode(item, out ushort op)) extra.Add(op);
                    }

                    continue;
                }

                if (IndexOfKey(prop.Name) < 0) continue;
                if (TryReadOpcode(prop.Value, out ushort value)) overrides[prop.Name] = value;
                else AppLog.Warn("Protocol", $"Opcode table '{source}': invalid value for '{prop.Name}', keeping the default.");
            }

            table = new OpcodeTable(overrides, extra, source);
            return true;
        }
        catch (JsonException ex)
        {
            AppLog.Warn("Protocol", $"Opcode table '{source}' is not valid JSON ({ex.Message}), using defaults.");
            return false;
        }
    }

    /// <summary>Creates a table with some opcodes replaced (keys as in <see cref="Keys"/>).</summary>
    public OpcodeTable With(IReadOnlyDictionary<string, ushort> overrides)
    {
        var merged = new Dictionary<string, ushort>(ToDictionary(), StringComparer.Ordinal);
        foreach (var (k, v) in overrides) merged[k] = v;
        return new OpcodeTable(merged, ExtraKnownOpcodes, Source + "+overrides");
    }

    /// <summary>True for opcodes that a real frame plausibly carries (table, census, extras, bundle). Used by resync.</summary>
    public bool IsKnown(ushort opcode) => _known.Contains(opcode);

    /// <summary>All named opcodes by JSON key.</summary>
    public IReadOnlyDictionary<string, ushort> ToDictionary()
    {
        var d = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (int i = 0; i < Keys.Count; i++) d[Keys[i]] = _values[i];
        return d;
    }

    private static bool TryReadOpcode(JsonElement e, out ushort op)
    {
        op = 0;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return Opcodes.TryParse(e.GetString() ?? "", out op);
            case JsonValueKind.Number:
                if (e.TryGetInt32(out int n) && n is >= 0 and <= 0xFFFF)
                {
                    op = (ushort)n;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private void Apply(string key, ushort value)
    {
        int index = IndexOfKey(key);
        if (index >= 0) _values[index] = value;
    }

    private static int IndexOfKey(string key)
    {
        for (int i = 0; i < Keys.Count; i++)
        {
            if (string.Equals(Keys[i], key, StringComparison.Ordinal)) return i;
        }

        return -1;
    }
}
