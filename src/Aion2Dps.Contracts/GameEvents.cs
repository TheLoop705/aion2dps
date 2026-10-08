namespace Aion2Dps.Contracts;

// Typed events produced by the protocol layer from decoded frames.
// Field meanings follow docs/research/PROTOCOL.md (section numbers in comments).
// Entity ids are session-scoped varints shared by players, NPCs and summons; they are reissued on zone change.

public abstract record GameEvent
{
    /// <summary>Capture timestamp (UTC).</summary>
    public DateTime Time { get; init; }

    /// <summary>Bundle nesting depth of the frame this came from (0 = top level).</summary>
    public int BundleDepth { get; init; }
}

/// <summary><c>00 36</c> heartbeat, body = server clock (§8.1).</summary>
public sealed record HeartbeatEvent : GameEvent
{
    public ulong ServerUnixMs { get; init; }
}

/// <summary><c>04 38</c> damage / direct heal / cast notice record (§8.2). One event per record (frames may chain records).</summary>
public sealed record DamageEvent : GameEvent
{
    public uint Target { get; init; }
    public uint Actor { get; init; }
    /// <summary>Skill id as read from the wire (u32).</summary>
    public uint SkillRaw { get; init; }
    /// <summary>Normalized skill id (<see cref="SkillIds.Normalize"/>).</summary>
    public uint SkillId { get; init; }
    /// <summary>The full switch varint; <c>Layout = Switch &amp; 0x0F</c>.</summary>
    public uint Switch { get; init; }
    public byte Layout { get; init; }
    public byte HitTag { get; init; }
    /// <summary>2 = normal, 3 = critical, 0 on some notices.</summary>
    public byte DamageType { get; init; }
    public bool IsCritical => DamageType == 3;
    public HitMods Mods { get; init; }
    public HitDirection Direction { get; init; }
    public uint EffectId { get; init; }
    public uint HitIndex { get; init; }
    public uint PowerScalar { get; init; }
    /// <summary>Total amount of the event (already includes <see cref="ExtraHits"/>). Null for layout-0 cast notices.</summary>
    public long? Amount { get; init; }
    /// <summary>Multi-hit breakdown (already included in <see cref="Amount"/>).</summary>
    public IReadOnlyList<uint> ExtraHits { get; init; } = Array.Empty<uint>();
    /// <summary>Target buff effect ids explicitly listed by an absorb/negation block (at most eight).</summary>
    public IReadOnlyList<uint> AbsorbEffects { get; init; } = Array.Empty<uint>();
    /// <summary>True when the effect-id validator (§8.2.3) passed.</summary>
    public bool EffectValidated { get; init; }
    /// <summary>True when this is a layout-0 no-damage cast/companion notice.</summary>
    public bool IsCastNotice => Layout == 0;
}

/// <summary><c>05 38</c> DoT / HoT tick (§8.3).</summary>
public sealed record DotEvent : GameEvent
{
    public uint Target { get; init; }
    /// <summary>0x02 amount present, 0x01 heal present, 0x08 trigger skill present, 0x40 effect skill present.</summary>
    public byte Flags { get; init; }
    public uint Actor { get; init; }
    public uint Stack { get; init; }
    public uint EffectId { get; init; }
    /// <summary>Tick damage for flags 0x0A/0x02; "remaining heal" for HoT flags 0x0B.</summary>
    public long? Amount { get; init; }
    /// <summary>Heal of this tick (flags 0x0B) or HoT total announcement (0x09).</summary>
    public long? Heal { get; init; }
    /// <summary>Exact skill from flag 0x08, overridden by the effect skill when flag 0x40 is present.</summary>
    public uint? SkillId { get; init; }

    public bool IsDamageTick => (Flags & 0x02) != 0 && (Flags & 0x01) == 0;
    public bool IsHealTick => Flags == 0x0B;
    /// <summary>A 9-digit monster effect paired with a player class skill: the target heals itself (boss self-heal trap, §8.3).</summary>
    public bool IsMonsterEffect => EffectId is >= 100_000_000 and <= 999_999_999;
}

/// <summary><c>00 8D</c> entity stats delta (§8.4).</summary>
public sealed record EntityStatsEvent : GameEvent
{
    public uint Entity { get; init; }
    public byte Format { get; init; }
    /// <summary>8-byte stat kind 0.</summary>
    public long? CurrentHp { get; init; }
    public IReadOnlyDictionary<byte, uint> Stats32 { get; init; } = new Dictionary<byte, uint>();
    public IReadOnlyDictionary<byte, long> Stats64 { get; init; } = new Dictionary<byte, long>();
    /// <summary>Frames with format bit 0 are only sent for the local player.</summary>
    public bool HasSelfStats => (Format & 0x01) != 0;
}

/// <summary><c>41 36</c> NPC / summon / skill-entity spawn (§8.5).</summary>
public sealed record SpawnEvent : GameEvent
{
    public uint Entity { get; init; }
    public byte KindByte { get; init; }
    public byte KindFlags { get; init; }
    /// <summary>Inline caster/owner name (gate bit 0).</summary>
    public string? CasterName { get; init; }
    public uint NpcCode { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public long? HpCurrent { get; init; }
    public long? HpMax { get; init; }
    /// <summary>Owner entity from the <c>07 02 06</c> marker (primary owner source).</summary>
    public uint? OwnerId { get; init; }
    /// <summary>Owner candidate from the caster anchor (only meaningful when ≠ Entity).</summary>
    public uint? AnchorId { get; init; }
    /// <summary>Kind byte says summon/spirit/skill entity (0x1F and friends) rather than a monster.</summary>
    public bool IsSummonLike { get; init; }
}

/// <summary><c>33 36</c> own character record (§8.6).</summary>
public sealed record SelfInfoEvent : GameEvent
{
    public uint Entity { get; init; }
    public string Name { get; init; } = "";
    public ushort ServerId { get; init; }
    public uint ClassCode { get; init; }
    public CharacterClass Class { get; init; }
    public uint Level { get; init; }
}

/// <summary><c>45 36</c> another player enters view (§8.7).</summary>
public sealed record PlayerInfoEvent : GameEvent
{
    public uint Entity { get; init; }
    public string Name { get; init; } = "";
    public uint ClassCode { get; init; }
    public CharacterClass Class { get; init; }
    public ushort? ServerId { get; init; }
    public string? GuildName { get; init; }
}

/// <summary><c>04 8D</c> kill / last-blow record (§8.8). Skill 0 and killer 0 = despawn form.</summary>
public sealed record KillEvent : GameEvent
{
    public uint Target { get; init; }
    public uint SkillRaw { get; init; }
    public uint Killer { get; init; }
    public ushort KillerServerId { get; init; }
    public string? KillerName { get; init; }
    public string? KillerGuildName { get; init; }
}

/// <summary><c>42 36</c> death (§8.9). Flag 3 = died in combat.</summary>
public sealed record DeathEvent : GameEvent
{
    public uint Entity { get; init; }
    public uint Flag { get; init; }
}

/// <summary><c>21 36</c> map load (§8.10). Same MapId as the previous load = in-map teleport.</summary>
public sealed record MapLoadEvent : GameEvent
{
    public uint LoadCount { get; init; }
    public uint MapId { get; init; }
}

/// <summary><c>23 36</c> teleport; Entity 0 = the local player.</summary>
public sealed record TeleportEvent : GameEvent
{
    public uint Entity { get; init; }
}

/// <summary><c>02 97</c> own party roster (§8.11). The latest complete roster replaces the party.</summary>
public sealed record PartyRosterEvent : GameEvent
{
    public uint PartyKey { get; init; }
    public uint? DungeonId { get; init; }
    public IReadOnlyList<PartyMember> Members { get; init; } = Array.Empty<PartyMember>();
}

public sealed record PartyMember
{
    public int Slot { get; init; }
    public ulong DbId { get; init; }
    /// <summary>Low 32 bits of DbId (matches the <c>20 36</c> global id).</summary>
    public uint CharacterId { get; init; }
    /// <summary>Top 16 bits of DbId.</summary>
    public ushort ServerId { get; init; }
    public string Name { get; init; } = "";
    public uint ClassCode { get; init; }
    public CharacterClass Class { get; init; }
    public uint Level { get; init; }
    public uint? GearScore { get; init; }
    public ulong? CombatPower { get; init; }
}

/// <summary><c>1B 92</c> HP/MP update (§8.12) — a max-HP source for NPCs already present when capture began.</summary>
public sealed record HpUpdateEvent : GameEvent
{
    public uint Entity { get; init; }
    public long Hp { get; init; }
    public long HpMax { get; init; }
}

/// <summary><c>2A 38</c>/<c>2B 38</c> buff applied (§8.13).</summary>
public sealed record BuffAppliedEvent : GameEvent
{
    public uint Target { get; init; }
    public uint Stack { get; init; }
    public uint BuffId { get; init; }
    /// <summary>0xFFFFFFFF = permanent.</summary>
    public uint DurationMs { get; init; }
    public ulong ExpiryUnixMs { get; init; }
    public uint Caster { get; init; }
    public uint SourceSkill { get; init; }
}

/// <summary><c>0E 92</c> buff removed/refreshed (L).</summary>
public sealed record BuffRemovedEvent : GameEvent
{
    public uint Target { get; init; }
    public uint BuffId { get; init; }
}

/// <summary><c>02 38</c> cast announcement (§8.14) — used for summon linking.</summary>
public sealed record CastEvent : GameEvent
{
    public uint Actor { get; init; }
    public uint SkillRaw { get; init; }
    public uint Target { get; init; }
    /// <summary>Caster position (x, y, z) when the record carries one (kind byte 2: heading f32, then x, y, z f32 after
    /// the target); null otherwise. Used to match an NPC whose spawn was missed against the field-boss list.</summary>
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Z { get; init; }
}

/// <summary>
/// <c>01 91</c> field-boss list (§8.16): <c>u16 0 | map u32 | count u8 | count × slot | 00 00 00</c>. Slot =
/// <c>alive u8 (0/1) | slot varint (map × 100 + place) | [x y z f32 if alive] | [u8, some slots only] | time i64 Unix ms</c>.
/// Sent every few seconds for the region's open-world map, also while the player is elsewhere.
/// </summary>
public sealed record FieldBossListEvent : GameEvent
{
    public uint MapId { get; init; }
    public IReadOnlyList<FieldBossSlot> Slots { get; init; } = Array.Empty<FieldBossSlot>();
}

/// <summary>One field-boss slot of a <see cref="FieldBossListEvent"/>.</summary>
public sealed record FieldBossSlot
{
    /// <summary>map × 100 + place (1-based place of the boss among the map's field bosses in NPC-code order).</summary>
    public uint Slot { get; init; }
    public bool Alive { get; init; }
    /// <summary>Spawn time of a living boss or respawn time of a dead one (Unix ms); 0 when the game sends none.</summary>
    public long TimeUnixMs { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}

/// <summary><c>03 36</c> ping echo (§8.15). Global: client QPC-ms + 16,777,216,000.</summary>
public sealed record PingEvent : GameEvent
{
    public long ClientSentMs { get; init; }
}

/// <summary><c>20 36</c> session id ↔ global character id link (§8.16, L).</summary>
public sealed record GlobalIdLinkEvent : GameEvent
{
    public uint Entity { get; init; }
    public uint CharacterId { get; init; }
}

/// <summary><c>21 8D</c> NPC battle state toggle (L on Global; a hint only).</summary>
public sealed record BattleToggleEvent : GameEvent
{
    public uint Entity { get; init; }
    public bool Engaged { get; init; }
}
