namespace Aion2Dps.Contracts;

/// <summary>Playable classes. Values are the game's classId (identity code = 4×classId + faction).</summary>
public enum CharacterClass
{
    Unknown = 0,
    Gladiator = 1,
    Templar = 2,
    Ranger = 3,
    Assassin = 4,
    Elementalist = 5,
    Sorcerer = 6,
    Cleric = 7,
    Chanter = 8,
    Brawler = 11,
}

/// <summary>Language used for game data names (skills, NPCs, maps, classes).</summary>
public enum GameLanguage
{
    English,
    Korean,
    ChineseSimplified,
    ChineseTraditional,
}

/// <summary>Bits of the <c>mods</c> byte in a layout-6/7 damage record (Global meanings, PROTOCOL.md §9.2).</summary>
[Flags]
public enum HitMods : byte
{
    None = 0,
    Unknown01 = 0x01,
    Parry = 0x02,      // inferred (M)
    Perfect = 0x04,    // confirmed
    Double = 0x08,     // confirmed
    Unknown10 = 0x10,
    Smite = 0x20,      // inferred (L-M)
    PowerShard = 0x40, // inferred (L)
    Unknown80 = 0x80,
}

/// <summary>The <c>dir</c> byte of a layout-6/7 damage record.</summary>
public enum HitDirection : byte
{
    None = 0,
    Back = 1,
    Front = 2,
}

public enum SkillKind
{
    Unknown,
    /// <summary>Class skill, 10,000,000..19,999,999.</summary>
    Player,
    /// <summary>Elementalist spirit skill, 100,000..199,999.</summary>
    Spirit,
    /// <summary>Theostone/godstone proc (normalized to 300xxxx1).</summary>
    Theostone,
    /// <summary>NPC / monster skill, 1,000,000..9,999,999 (damage taken).</summary>
    Npc,
    /// <summary>Summon/spirit link record — not damage.</summary>
    Link,
}

/// <summary>What the overlay is counting.</summary>
public enum MeterMode
{
    /// <summary>Only damage to the current boss (falls back to all targets when no boss is engaged).</summary>
    BossOnly,
    /// <summary>All damage to every enemy in the current encounter.</summary>
    AllTargets,
    /// <summary>Player-vs-player view: enemy players you trade damage with.</summary>
    Pvp,
}

public enum MeterState
{
    /// <summary>No game data has been seen yet.</summary>
    Idle,
    /// <summary>Game traffic is flowing, no encounter is active.</summary>
    WaitingForCombat,
    /// <summary>An encounter is active.</summary>
    InCombat,
    /// <summary>The last encounter ended; its final numbers are shown until the next one starts.</summary>
    Ended,
}

public enum EncounterKind
{
    Boss,
    Trash,
    Dummy,
    Pvp,
    Training,
}

public enum EncounterOutcome
{
    InProgress,
    Kill,
    Wipe,
    Timeout,
    ManualReset,
    ZoneChange,
}

public enum CombatantKind
{
    /// <summary>A friendly player (you, a party member, or another player hitting the same enemies).</summary>
    Player,
    /// <summary>An enemy player (PvP).</summary>
    EnemyPlayer,
    /// <summary>Bucket for summon damage whose owner could not be resolved.</summary>
    UnknownSummons,
}

/// <summary>Per-hit flags stored in <see cref="HitRecord"/>.</summary>
[Flags]
public enum HitFlags : ushort
{
    None = 0,
    Crit = 1 << 0,
    Dot = 1 << 1,
    Back = 1 << 2,
    Front = 1 << 3,
    Perfect = 1 << 4,
    Double = 1 << 5,
    Smite = 1 << 6,
    Parry = 1 << 7,
    Heal = 1 << 8,
    MultiHit = 1 << 9,
    Dodged = 1 << 10,
    KillingBlow = 1 << 11,
    /// <summary>The hit is damage <b>taken</b> by a tracked combatant (Actor is an NPC/enemy).</summary>
    Incoming = 1 << 12,
    /// <summary>Dealt by a summon and attributed to its owner.</summary>
    Summon = 1 << 13,
}

public enum CaptureState
{
    Stopped,
    /// <summary>wpcap.dll / Npcap driver not found.</summary>
    NpcapMissing,
    /// <summary>The AION 2 process / game connection was not found yet.</summary>
    WaitingForGame,
    /// <summary>Candidate flows are being checked for the heartbeat signature.</summary>
    Detecting,
    Capturing,
    Replaying,
    Error,
}
