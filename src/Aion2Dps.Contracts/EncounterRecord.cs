namespace Aion2Dps.Contracts;

// Full, serializable description of one encounter. Produced by the combat engine (live: on demand as a deep copy;
// completed: via ICombatEngine.EncounterCompleted), persisted by IFightStore, rendered by the analysis UI.
// Only ids are stored for game data (skills, NPCs, maps); names are resolved through IGameData at display time so the
// display language can change. Player names are stored because they are session data.
// All classes are plain POCOs with public setters so System.Text.Json can round-trip them.

public sealed class EncounterRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public EncounterKind Kind { get; set; }
    public EncounterOutcome Outcome { get; set; }

    /// <summary>First counted hit (UTC).</summary>
    public DateTime StartUtc { get; set; }
    /// <summary>Last counted hit (UTC).</summary>
    public DateTime EndUtc { get; set; }
    /// <summary>Fight clock used for encounter DPS: max(1, last hit − first hit) in seconds.</summary>
    public double DurationSeconds { get; set; }

    public uint? MapId { get; set; }
    public ushort? ServerId { get; set; }
    public string? LocalPlayerName { get; set; }
    public CharacterClass LocalPlayerClass { get; set; }

    /// <summary>The primary target (boss) when <see cref="Kind"/> is Boss/Dummy.</summary>
    public uint? BossNpcCode { get; set; }
    public uint? BossEntityId { get; set; }
    public long? BossMaxHp { get; set; }
    public long? BossHpStart { get; set; }
    public long? BossHpEnd { get; set; }
    /// <summary>Boss HP over time (seconds from StartUtc).</summary>
    public List<HpSample> BossHpTimeline { get; set; } = new();
    /// <summary>How many times the boss returned to full HP before this attempt.</summary>
    public int ResetCount { get; set; }

    /// <summary>Sum of counted damage of all friendly combatants.</summary>
    public long TotalDamage { get; set; }
    /// <summary>TotalDamage / DurationSeconds.</summary>
    public double PartyDps { get; set; }

    public List<CombatantRecord> Combatants { get; set; } = new();
    public List<TargetRecord> Targets { get; set; } = new();

    /// <summary>Every counted hit, heal and incoming hit in time order (the source for ribbons, timelines and drill-downs).</summary>
    public List<HitRecord> Hits { get; set; } = new();

    public HpCheckResult? HpCheck { get; set; }

    /// <summary>True when the capture had TCP gaps or resyncs during the fight (numbers may be incomplete).</summary>
    public bool CaptureGaps { get; set; }

    /// <summary>Free-form notes (e.g. "training run 60 s").</summary>
    public string? Note { get; set; }
}

public sealed class CombatantRecord
{
    public uint EntityId { get; set; }
    public string Name { get; set; } = "";
    public CharacterClass Class { get; set; }
    public CombatantKind Kind { get; set; }
    public ushort? ServerId { get; set; }
    public bool IsLocal { get; set; }
    public bool IsPartyMember { get; set; }
    public uint? Level { get; set; }
    public uint? GearScore { get; set; }
    public ulong? CombatPower { get; set; }

    /// <summary>Counted outgoing damage (direct + DoT + attributed summons) in the encounter's scope.</summary>
    public long Damage { get; set; }
    /// <summary>Damage dealt to the primary boss only.</summary>
    public long BossDamage { get; set; }
    /// <summary>Damage / encounter DurationSeconds (the comparable "whole fight" DPS).</summary>
    public double Dps { get; set; }
    /// <summary>Damage / (own last hit − own first hit), min 1 s.</summary>
    public double ActiveDps { get; set; }
    /// <summary>0..1: share of the boss's max HP removed by this player; falls back to share of party damage.</summary>
    public double Contribution { get; set; }
    /// <summary>0..1: share of party damage.</summary>
    public double DamageShare { get; set; }

    public long Healing { get; set; }
    public long DamageTaken { get; set; }
    public int Deaths { get; set; }

    public DateTime? FirstHitUtc { get; set; }
    public DateTime? LastHitUtc { get; set; }

    public HitQualityStats Quality { get; set; } = new();
    public DefenseStats Defense { get; set; } = new();
    public List<SkillStats> Skills { get; set; } = new();
    /// <summary>Outgoing damage per whole second since the encounter start (index = second).</summary>
    public List<long> DamagePerSecond { get; set; } = new();
    public List<SourceDamage> DamageTakenBySource { get; set; } = new();
    public List<BuffUptime> Buffs { get; set; } = new();

    // PvP (Kind == EnemyPlayer): exchange with the local player.
    public long DamageToLocal { get; set; }
    public long DamageFromLocal { get; set; }
    public bool KilledByLocal { get; set; }
}

/// <summary>Hit-quality counters. DoT ticks are excluded from <see cref="Hits"/> and every rate denominator.</summary>
public sealed class HitQualityStats
{
    /// <summary>Measured direct hits (no DoT ticks, no layout-0 notices, no heals).</summary>
    public int Hits { get; set; }
    public int Crits { get; set; }
    public int Back { get; set; }
    public int Front { get; set; }
    public int Perfect { get; set; }
    public int Double { get; set; }
    public int Smite { get; set; }
    /// <summary>Hits the target parried.</summary>
    public int Parried { get; set; }
    /// <summary>Hits the target dodged (dodge pseudo-skill).</summary>
    public int Dodged { get; set; }
    /// <summary>Hits that carried an extra-hit list.</summary>
    public int MultiHits { get; set; }
    public int ExtraHitCount { get; set; }
    public int DotTicks { get; set; }
    public long DotDamage { get; set; }
    public long MaxHit { get; set; }
    public uint MaxHitSkillId { get; set; }
    /// <summary>Hits whose record carried the mods/direction bytes (layouts 6/7); the denominator for Back/Front/Perfect/Smite/Parry rates.
    /// When 0 those rates are unmeasured (show "—", never 0%).</summary>
    public int QualityMeasuredHits { get; set; }
}

/// <summary>Incoming-damage counters. Null = the protocol does not report it (show "—").</summary>
public sealed class DefenseStats
{
    public int HitsTaken { get; set; }
    public long DamageTaken { get; set; }
    public int CritsTaken { get; set; }
    public int BackHitsTaken { get; set; }
    public int Dodged { get; set; }
    public int Parried { get; set; }
    public int? Blocked { get; set; }
    public int? Endured { get; set; }
    public int? Resisted { get; set; }
    public long HealingReceived { get; set; }
}

public sealed class SkillStats
{
    /// <summary>Group key (<see cref="IGameData.GetSkillGroupKey"/>).</summary>
    public uint SkillId { get; set; }
    /// <summary>A representative exact id seen for this group.</summary>
    public uint SampleSkillId { get; set; }
    public long Damage { get; set; }
    public long Healing { get; set; }
    /// <summary>Direct hits (DoT ticks excluded).</summary>
    public int Hits { get; set; }
    public int Casts { get; set; }
    public int Crits { get; set; }
    public long MinHit { get; set; }
    public long MaxHit { get; set; }
    public int Back { get; set; }
    public int Front { get; set; }
    public int Perfect { get; set; }
    public int Double { get; set; }
    public int Smite { get; set; }
    public int Parried { get; set; }
    public int MultiHits { get; set; }
    public int ExtraHitCount { get; set; }
    public int DotTicks { get; set; }
    public long DotDamage { get; set; }
    public int QualityMeasuredHits { get; set; }
    /// <summary>True when the damage came from a summon/spirit attributed to this combatant.</summary>
    public bool FromSummon { get; set; }
}

public sealed class SourceDamage
{
    /// <summary>Attacker entity (NPC or player) at the time.</summary>
    public uint SourceEntityId { get; set; }
    public uint? SourceNpcCode { get; set; }
    /// <summary>Set when the attacker was a player (PvP).</summary>
    public string? SourcePlayerName { get; set; }
    public uint SkillId { get; set; }
    public long Damage { get; set; }
    public int Hits { get; set; }
    public int Crits { get; set; }
}

public sealed class BuffUptime
{
    public uint BuffId { get; set; }
    /// <summary>buffId / 10.</summary>
    public uint SkillId { get; set; }
    public int Applications { get; set; }
    public double UptimeSeconds { get; set; }
    /// <summary>0..1 of the encounter duration.</summary>
    public double Uptime { get; set; }
    public uint? CasterEntityId { get; set; }
}

public sealed class TargetRecord
{
    public uint EntityId { get; set; }
    public uint? NpcCode { get; set; }
    /// <summary>Set when the target is a player (PvP).</summary>
    public string? PlayerName { get; set; }
    public bool IsBoss { get; set; }
    public bool IsDummy { get; set; }
    public long? MaxHp { get; set; }
    public long? LastHp { get; set; }
    public long DamageTaken { get; set; }
    public long SelfHealing { get; set; }
    public bool Killed { get; set; }
    public DateTime? FirstHitUtc { get; set; }
    public DateTime? LastHitUtc { get; set; }
}

/// <summary>One hit/heal/tick. Compact on purpose: fights can hold tens of thousands.</summary>
public sealed class HitRecord
{
    /// <summary>Seconds since <see cref="EncounterRecord.StartUtc"/>.</summary>
    public float T { get; set; }
    /// <summary>Combatant credited (summon owner for summon hits; the NPC/enemy for incoming hits).</summary>
    public uint Actor { get; set; }
    /// <summary>The entity that actually acted (summon id for summon hits).</summary>
    public uint Source { get; set; }
    public uint Target { get; set; }
    /// <summary>Normalized exact skill id.</summary>
    public uint Skill { get; set; }
    public long Amount { get; set; }
    public HitFlags Flags { get; set; }
    public byte HitTag { get; set; }
    public byte HitIndex { get; set; }
    public byte ExtraHits { get; set; }
}

public readonly record struct HpSample(float T, long Hp);

/// <summary>Self-test: does decoded damage explain the boss's HP loss? (PROTOCOL.md §11.4)</summary>
public sealed class HpCheckResult
{
    public long DecodedDamage { get; set; }
    public long HpLost { get; set; }
    public long BossSelfHealing { get; set; }
    /// <summary>DecodedDamage / (HpLost + BossSelfHealing). 1.0 = perfect.</summary>
    public double Ratio { get; set; }
    /// <summary>|Ratio − 1| ≤ 0.01.</summary>
    public bool Passed { get; set; }
    public string? Note { get; set; }
}
