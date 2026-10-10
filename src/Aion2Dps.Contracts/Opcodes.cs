namespace Aion2Dps.Contracts;

/// <summary>
/// AION 2 server→client opcodes in <b>wire byte order</b>: the bytes <c>04 38</c> are written <c>0x0438</c>.
/// These are the compiled-in defaults (Global/Steam, post-June-2026, see docs/research/PROTOCOL.md §7).
/// The protocol layer may override them from <c>data/protocol/opcodes.json</c> after a game patch.
/// </summary>
public static class Opcodes
{
    public const ushort Heartbeat = 0x0036;
    public const ushort Bundle = 0xFFFF;
    public const ushort Damage = 0x0438;
    public const ushort DotTick = 0x0538;
    public const ushort EntityStats = 0x008D;
    public const ushort Spawn = 0x4136;
    public const ushort SelfInfo = 0x3336;
    public const ushort PlayerInfo = 0x4536;
    public const ushort Kill = 0x048D;
    public const ushort Death = 0x4236;
    public const ushort MapLoad = 0x2136;
    public const ushort Teleport = 0x2336;
    public const ushort PartyRoster = 0x0297;
    public const ushort OtherPartyRoster = 0x0197;
    public const ushort HpUpdate = 0x1B92;
    /// <summary>[real] HP/MP of the other members of your force (several parties joined for a field/world boss); the same
    /// layout as <see cref="HpUpdate"/>, which covers only your own party.</summary>
    public const ushort ForceHpUpdate = 0x2B96;
    public const ushort BuffApplied = 0x2A38;
    public const ushort BuffApplied2 = 0x2B38;
    public const ushort BuffRemoved = 0x0E92;
    public const ushort Cast = 0x0238;
    public const ushort Ping = 0x0336;
    public const ushort GlobalIdLink = 0x2036;
    public const ushort PartyScope = 0x0638;
    public const ushort BattleToggle = 0x218D;
    public const ushort FieldBossList = 0x0191;

    /// <summary>Formats an opcode as wire bytes, e.g. <c>0x0438</c> → <c>"04 38"</c>.</summary>
    public static string Format(ushort opcode) => $"{opcode >> 8:X2} {opcode & 0xFF:X2}";

    /// <summary>Parses <c>"04 38"</c>, <c>"0438"</c> or <c>"0x0438"</c> (all wire order).</summary>
    public static bool TryParse(string text, out ushort opcode)
    {
        var s = text.Replace(" ", "", StringComparison.Ordinal);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out opcode);
    }
}
