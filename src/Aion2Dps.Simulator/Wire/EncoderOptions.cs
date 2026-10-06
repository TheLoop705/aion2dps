namespace Aion2Dps.Simulator;

// Wire fields that the Contracts events do not carry (opaque or constant on the wire). The defaults are the values
// observed in real Global frames (PROTOCOL.md §8), so encoding an event with default options gives a realistic frame.

/// <summary>Extra <c>04 38</c> fields (§8.2).</summary>
public sealed record DamageEncodeOptions
{
    public static readonly DamageEncodeOptions Default = new();

    /// <summary>Field #3 "flag" varint (always 0 in every sample).</summary>
    public uint Flag { get; init; }
    /// <summary>Field #13, written when layout == 4 and <c>sw &amp; 0x10</c> (value 1 in all samples).</summary>
    public uint Layout4Extra { get; init; } = 1;
    /// <summary>Write the 2-byte trailer <c>hit_index, 00</c> (present in 32/32 complete samples).</summary>
    public bool Trailer { get; init; } = true;
    /// <summary>The 4 unknown block bytes of layouts 5/7 (unverified on Global).</summary>
    public uint Layout1Bytes { get; init; }
}

/// <summary>Extra <c>41 36</c> fields (§8.5).</summary>
public sealed record SpawnEncodeOptions
{
    public static readonly SpawnEncodeOptions Default = new();

    /// <summary>The 4th float after x, y, z.</summary>
    public float Heading { get; init; }
    /// <summary>The u16 after the floats.</summary>
    public ushort Unknown16 { get; init; } = 0x1700;
    /// <summary>The byte after npc_code: null = 0x40 for summon-like spawns, 0x00 for monsters.</summary>
    public byte? CodeSuffix { get; init; }
    /// <summary>Write the variable tail (stat block, FF×8 run, caster anchor, <c>07 02 06</c> owner block). False = head only.</summary>
    public bool IncludeTail { get; init; } = true;
}

/// <summary>Extra <c>33 36</c> fields (§8.6).</summary>
public sealed record SelfInfoEncodeOptions
{
    public static readonly SelfInfoEncodeOptions Default = new();

    public uint Mask1 { get; init; } = 0x28C1A15F;
    /// <summary>Bit 0 = name present.</summary>
    public byte Mask2 { get; init; } = 0x37;
    /// <summary>Bytes after the level (equipment entries etc.).</summary>
    public byte[] Tail { get; init; } = [];
}

/// <summary>Extra <c>45 36</c> fields (§8.7).</summary>
public sealed record PlayerInfoEncodeOptions
{
    public static readonly PlayerInfoEncodeOptions Default = new();

    public uint Mask1 { get; init; } = 0x01A0B017;
    public byte Mask2 { get; init; } = 0x07;
    /// <summary>Guild id written in the guild run (only when the event has a guild name).</summary>
    public uint GuildId { get; init; } = 0x00010001;
}

/// <summary>Extra <c>04 8D</c> fields (§8.8).</summary>
public sealed record KillEncodeOptions
{
    public static readonly KillEncodeOptions Default = new();

    /// <summary>Trailing bytes after the legion name (stacys real frame: <c>01 00 00 00 00 00 00 01 00</c>).</summary>
    public byte[] Trailer { get; init; } = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00];
}

/// <summary>Extra <c>21 36</c> fields (§8.10). Real frames have a 46-byte body (L = 52).</summary>
public sealed record MapLoadEncodeOptions
{
    public static readonly MapLoadEncodeOptions Default = new();

    public uint InstanceKey { get; init; } = 0x00351A3B;
    public float X { get; init; } = 12729.74f;
    public float Y { get; init; } = -22591.52f;
    public float Z { get; init; } = 5654.0f;
    public float Heading { get; init; } = 82.81f;
    public byte TailValue { get; init; } = 0x4F;
}

/// <summary>Extra <c>02 97</c> fields (§8.11).</summary>
public sealed record RosterEncodeOptions
{
    public static readonly RosterEncodeOptions Default = new();

    public string Title { get; init; } = "Let's go";
    /// <summary>Party size byte; null = member count.</summary>
    public byte? PartySize { get; init; }
    /// <summary>member_count varint; null = member count.</summary>
    public uint? SlotCount { get; init; }
    /// <summary>The 2 bytes after dungeon_id.</summary>
    public byte[] HeadBytes1 { get; init; } = [0x00, 0x03];
    /// <summary>The 3 bytes after the leader dbid.</summary>
    public byte[] HeadBytes2 { get; init; } = [0xFF, 0x01, 0x03];
    /// <summary>Per-member presence mask (0x1E: born-server present, no conqueror level).</summary>
    public byte Presence { get; init; } = 0x1E;
}

/// <summary>Extra <c>2A 38</c> fields (§8.13).</summary>
public sealed record BuffEncodeOptions
{
    public static readonly BuffEncodeOptions Default = new();

    public byte Byte1 { get; init; } = 0x01;
    public byte Byte2 { get; init; } = 0x13;
    public float X { get; init; } = 15317.83f;
    public float Y { get; init; } = 12704.64f;
    public float Z { get; init; } = 645.0f;
}

/// <summary>Extra <c>02 38</c> fields (§8.14).</summary>
public sealed record CastEncodeOptions
{
    public static readonly CastEncodeOptions Default = new();

    public uint Flag { get; init; }
    public byte Sequence { get; init; } = 0x92;
    public byte Kind { get; init; } = 0x02;
    public float X { get; init; } = -177.0f;
    public float Y { get; init; } = 119157.1f;
    public float Z { get; init; } = -108258.1f;
    public float Heading { get; init; } = 21.2f;
    /// <summary>The 5 bytes after the floats.</summary>
    public byte[] Tail { get; init; } = [0xEA, 0x5D, 0x01, 0xDC, 0x6F];
}
