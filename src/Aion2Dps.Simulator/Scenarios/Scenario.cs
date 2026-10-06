using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator;

/// <summary>What a scripted event contributes to the ground truth.</summary>
public enum TruthKind
{
    None,
    /// <summary>Counted outgoing damage of <see cref="TruthTag.Credited"/> (direct, DoT tick or owned summon).</summary>
    Damage,
    /// <summary>Healing done by <see cref="TruthTag.Credited"/> (heal-skill hit, HoT tick, or self-heal).</summary>
    Heal,
    /// <summary>Damage taken by the player <see cref="TruthTag.Target"/> from an NPC.</summary>
    Incoming,
    /// <summary>The player <see cref="TruthTag.Target"/> dodged (no damage).</summary>
    Dodge,
    /// <summary>The NPC <see cref="TruthTag.Target"/> healed itself (§8.3 boss self-heal trap) — not player damage.</summary>
    NpcSelfHeal,
}

/// <summary>Ground-truth annotation of one scripted event.</summary>
public sealed record TruthTag
{
    public TruthKind Kind { get; init; }
    /// <summary>Player credited with the damage/heal (summon owner for summon hits); 0 when not applicable.</summary>
    public uint Credited { get; init; }
    /// <summary>Wire actor (the summon entity for summon hits).</summary>
    public uint Actor { get; init; }
    public uint Target { get; init; }
    public uint SkillId { get; init; }
    public long Amount { get; init; }
    public bool Crit { get; init; }
    public bool Dot { get; init; }
    public bool Summon { get; init; }
    public int ExtraHits { get; init; }
    public bool Parry { get; init; }
    public HitMods Mods { get; init; }
    public HitDirection Direction { get; init; }
}

/// <summary>One event of a scenario script at an offset from the scenario start.</summary>
public sealed record ScriptedEvent(TimeSpan Offset, GameEvent Event)
{
    /// <summary>Ground-truth meaning (null = not counted by the meter: identity, spawn, notice, link record…).</summary>
    public TruthTag? Truth { get; init; }

    /// <summary>Index of the expected encounter this event belongs to, or -1.</summary>
    public int EncounterIndex { get; init; } = -1;
}

/// <summary>Static description of a player taking part in a scenario.</summary>
public sealed record SimPlayer
{
    public uint EntityId { get; init; }
    public string Name { get; init; } = "";
    public CharacterClass Class { get; init; }
    public ushort ServerId { get; init; } = 1304;
    public uint Level { get; init; } = 45;
    public bool IsLocal { get; init; }
    public bool IsPartyMember { get; init; }
    /// <summary>Hostile to the local player (PvP).</summary>
    public bool IsEnemy { get; init; }
    /// <summary>The power_scalar its damage records carry (§8.2 #11). Summons inherit it.</summary>
    public uint PowerScalar { get; init; } = 12000;
    /// <summary>Global character id (low 32 bits of the roster dbid).</summary>
    public uint CharacterId { get; init; }
    public uint GearScore { get; init; } = 2200;
    public ulong CombatPower { get; init; } = 48000;
    public string? GuildName { get; init; }
    /// <summary>Faction component of the class code (2 = Elyos, 1 = the other side).</summary>
    public uint Faction { get; init; } = 2;
    public uint ClassCode => PacketEncoders.ClassCodeFor(Class, Faction);
}

/// <summary>Static description of an NPC in a scenario.</summary>
public sealed record SimNpc
{
    public uint EntityId { get; init; }
    public uint NpcCode { get; init; }
    public bool IsBoss { get; init; }
    public bool IsDummy { get; init; }
    /// <summary>Final max HP (derived from the damage it takes when it dies, otherwise fixed).</summary>
    public long MaxHp { get; init; }
}

/// <summary>A scripted fight: the ordered event script plus its exact ground truth.</summary>
public sealed class Scenario
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Seed the scenario was generated with.</summary>
    public int Seed { get; init; }
    /// <summary>Length of the scenario including the idle tail after the last hit.</summary>
    public TimeSpan Duration { get; init; }
    /// <summary>All events sorted by offset (stable). Event times are multiples of 50 ms (server ticks).</summary>
    public required IReadOnlyList<ScriptedEvent> Events { get; init; }
    public required IReadOnlyList<SimPlayer> Players { get; init; }
    public required IReadOnlyList<SimNpc> Npcs { get; init; }
    public required ScenarioGroundTruth Truth { get; init; }

    public SimPlayer LocalPlayer => Players.First(p => p.IsLocal);

    public override string ToString() => $"{Name} ({Duration.TotalSeconds:0} s, {Events.Count} events)";
}
