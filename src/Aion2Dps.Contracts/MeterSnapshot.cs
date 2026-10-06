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

    /// <summary>Set once when a fight beat the local player's previous best on that boss (for a toast).</summary>
    public string? PersonalBestMessage { get; init; }
}

public sealed record PlayerRow
{
    public uint EntityId { get; init; }
    public string Name { get; init; } = "";
    public CharacterClass Class { get; init; }
    public CombatantKind Kind { get; init; }
    public bool IsLocal { get; init; }
    public bool IsPartyMember { get; init; }
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

public sealed record LocalPlayerInfo(uint EntityId, string Name, ushort ServerId, CharacterClass Class, uint Level);
