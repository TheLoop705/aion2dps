namespace Aion2Dps.Contracts;

/// <summary>Immutable live view for the overlay, produced by <see cref="ICombatEngine.GetSnapshot"/> (~4×/s).</summary>
public sealed record MeterSnapshot
{
    public static readonly MeterSnapshot Empty = new();

    public DateTime TimeUtc { get; init; }
    public MeterState State { get; init; }
    public MeterMode Mode { get; init; }
    /// <summary>Short human-readable status for the header when not in combat (e.g. "Waiting for combat").</summary>
    public string StatusText { get; init; } = "";

    public Guid? EncounterId { get; init; }
    public EncounterKind? EncounterKind { get; init; }
    public EncounterOutcome? Outcome { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>The currently displayed target (boss or cycled target).</summary>
    public TargetInfo? Target { get; init; }
    /// <summary>All tracked targets of the encounter (for cycling and multiple HP bars), boss first.</summary>
    public IReadOnlyList<TargetInfo> Targets { get; init; } = Array.Empty<TargetInfo>();
    /// <summary>The encounter's engaged bosses (primary first, then in engagement order). Several entries = a multi-boss
    /// fight (e.g. two bosses pulled by two halves of the party); the overlay shows a compact HP bar for each.</summary>
    public IReadOnlyList<TargetInfo> Bosses { get; init; } = Array.Empty<TargetInfo>();

    /// <summary>Friendly combatants ranked by damage (or damage taken / healing, depending on view).</summary>
    public IReadOnlyList<PlayerRow> Rows { get; init; } = Array.Empty<PlayerRow>();
    public long TotalDamage { get; init; }
    public double PartyDps { get; init; }
    public double MinDps { get; init; }
    public double AvgDps { get; init; }
    public double MaxDps { get; init; }

    /// <summary>PvP rows (Mode == Pvp).</summary>
    public IReadOnlyList<PvpRow> PvpRows { get; init; } = Array.Empty<PvpRow>();
    public int PvpKills { get; init; }

    public uint? MapId { get; init; }
    public string? MapName { get; init; }
    public LocalPlayerInfo? LocalPlayer { get; init; }
    public double? PingMs { get; init; }

    /// <summary>Live HP-check ratio for the current boss (diagnostic; null when unavailable).</summary>
    public double? HpCheckRatio { get; init; }
    /// <summary>Live HP-check ratio summed over all of the encounter's bosses (null when unavailable).</summary>
    public double? OverallHpCheckRatio { get; init; }

    /// <summary>Set once when a fight beat the local player's previous best on that boss (for a toast).</summary>
    public string? PersonalBestMessage { get; init; }

    /// <summary>
    /// Only part of the fight is visible (open-world boss, or HP loss far above the decoded damage; see
    /// <see cref="EncounterRecord.PartialView"/>). Rows then carry Contribution = damage / boss max HP, the local player
    /// and party come first, and players seen only through DoT ticks or heals are folded into one aggregate row
    /// (<see cref="PlayerRow.AggregateCount"/>) or hidden in party-only mode. Do not show the HP check as a mismatch.
    /// </summary>
    public bool PartialView { get; init; }
    /// <summary>Short explanation for the header/footer when <see cref="PartialView"/> is set.</summary>
    public string? PartialViewText { get; init; }

    /// <summary>What kind of fight this is (field boss, dungeon boss, PvP, training dummy, …).</summary>
    public FightContext Context { get; init; }
    /// <summary>Whose rows are ranked: you, your party, your force or everyone hitting the same enemies.</summary>
    public GroupScope Scope { get; init; }
    /// <summary>Players in <see cref="Scope"/> including you (party or force size), 0 when unknown or not a group.</summary>
    public int GroupSize { get; init; }
}

public sealed record PlayerRow
{
    public uint EntityId { get; init; }
    public string Name { get; init; } = "";
    public CharacterClass Class { get; init; }
    /// <summary>Latest gear score reported by the party roster; null when unavailable.</summary>
    public uint? GearScore { get; init; }
    public CombatantKind Kind { get; init; }
    public bool IsLocal { get; init; }
    public bool IsPartyMember { get; init; }
    /// <summary>A member of your force (several parties joined) who is not in your own party.</summary>
    public bool IsForceMember { get; init; }
    public int Rank { get; init; }
    public long Damage { get; init; }
    /// <summary>Live DPS (per-player clock: damage / (now − own first hit), see EngineOptions.LivePlayerClock).</summary>
    public double Dps { get; init; }
    /// <summary>0..1 share of boss max HP removed (fallback: share of party damage).</summary>
    public double Contribution { get; init; }
    /// <summary>0..1 share of party damage.</summary>
    public double DamageShare { get; init; }
    /// <summary>0..1 bar fill relative to the top row.</summary>
    public double RelativeToTop { get; init; }
    public double CritRate { get; init; }
    public int Hits { get; init; }
    public long MaxHit { get; init; }
    public long Healing { get; init; }
    public long DamageTaken { get; init; }
    public bool IsDead { get; init; }
    /// <summary>&gt; 0 for the partial-view aggregate row ("Others (visible DoT/heal only)"): how many players it folds.</summary>
    public int AggregateCount { get; init; }
}

public sealed record TargetInfo
{
    public uint EntityId { get; init; }
    public uint? NpcCode { get; init; }
    public string Name { get; init; } = "";
    public bool IsBoss { get; init; }
    public bool IsDummy { get; init; }
    public bool IsPlayer { get; init; }
    public long? Hp { get; init; }
    public long? MaxHp { get; init; }
    /// <summary>0..1, null when max HP is unknown.</summary>
    public double? HpFraction { get; init; }
    public long DamageTaken { get; init; }
    public bool IsDead { get; init; }
    /// <summary>One of the encounter's bosses (counts for contribution and the HP check).</summary>
    public bool IsEncounterBoss { get; init; }
    /// <summary>The encounter's primary boss (largest max HP, ties → first engaged).</summary>
    public bool IsPrimaryBoss { get; init; }
    /// <summary>Live HP-check ratio of this boss (null when unavailable or not a boss).</summary>
    public double? HpCheckRatio { get; init; }
}

public sealed record PvpRow
{
    public uint EntityId { get; init; }
    public string Name { get; init; } = "";
    public CharacterClass Class { get; init; }
    public ushort? ServerId { get; init; }
    public string? GuildName { get; init; }
    /// <summary>Damage the local player dealt to this enemy.</summary>
    public long DamageDealt { get; init; }
    /// <summary>Damage this enemy dealt to the local player.</summary>
    public long DamageTaken { get; init; }
    public double? HpFraction { get; init; }
    public bool Killed { get; init; }
    public DateTime LastActivityUtc { get; init; }
}

public sealed record LocalPlayerInfo(uint EntityId, string Name, ushort ServerId, CharacterClass Class, uint Level)
{
    /// <summary>True when the local player was inferred (self-stat frames, meter started mid-session) rather than named by
    /// its own <c>33 36</c> record; <see cref="Name"/> may then come from the party roster or a remembered character.</summary>
    public bool Inferred { get; init; }
}
