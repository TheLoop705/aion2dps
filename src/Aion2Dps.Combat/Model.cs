namespace Aion2Dps.Combat;

internal enum HitKind : byte
{
    /// <summary>Friendly player / owned summon → hostile NPC (DPS).</summary>
    Outgoing,
    /// <summary>NPC → tracked friendly player (damage taken).</summary>
    Incoming,
    /// <summary>Local player → enemy player.</summary>
    PvpOut,
    /// <summary>Enemy player → local player.</summary>
    PvpIn,
    /// <summary>Direct heal, HoT tick or self-heal by a friendly player.</summary>
    Heal,
    /// <summary>Healing received by an NPC target (including the boss self-heal trap, §8.3).</summary>
    TargetSelfHeal,
}

internal enum Side : byte
{
    None,
    Friendly,
    Hostile,
    Enemy,
    /// <summary>A summon owned by a player (or not resolved yet), as a target.</summary>
    OwnedSummon,
}

/// <summary>Who an acting entity's record is credited to.</summary>
internal readonly record struct Attribution(Side Side, uint Credited, bool ViaSummon, SummonEntity? Summon);

/// <summary>Internal mutable hit (the source of every statistic; the record is rebuilt from these).</summary>
internal sealed class Hit
{
    public DateTime Time;
    /// <summary>Credited combatant (owner for summon hits, NPC for incoming hits).</summary>
    public uint Actor;
    /// <summary>The entity that actually acted.</summary>
    public uint Source;
    public uint Target;
    public uint Skill;
    public long Amount;
    public HitFlags Flags;
    public HitKind Kind;
    public byte HitTag;
    public uint HitIndex;
    public byte ExtraHits;
    public bool QualityMeasured;
    public bool InScope;
    /// <summary>The target was one of the encounter's bosses when the hit landed.</summary>
    public bool ToBoss;

    public bool IsDot => (Flags & HitFlags.Dot) != 0;
    public bool IsDodge => (Flags & HitFlags.Dodged) != 0;
    public bool IsCrit => (Flags & HitFlags.Crit) != 0;
}

/// <summary>Cheap running totals for the live overlay.</summary>
internal sealed class LiveAccumulator
{
    public long Damage;
    public int Hits;
    public int Crits;
    public long MaxHit;
    public DateTime? First;
    public DateTime? Last;

    public void Add(Hit h)
    {
        if (h.IsDodge || h.Amount <= 0) return;
        Damage += h.Amount;
        if (First is null || h.Time < First) First = h.Time;
        if (Last is null || h.Time > Last) Last = h.Time;
        if (h.IsDot) return;
        Hits++;
        if (h.IsCrit) Crits++;
        if (h.Amount > MaxHit) MaxHit = h.Amount;
    }

    public void Reset()
    {
        Damage = 0;
        Hits = 0;
        Crits = 0;
        MaxHit = 0;
        First = null;
        Last = null;
    }
}

internal sealed class CombatantState
{
    public CombatantState(uint id, CombatantKind kind)
    {
        Id = id;
        Kind = kind;
    }

    public uint Id { get; }
    public CombatantKind Kind;
    public string Name = "";
    public CharacterClass Class;
    public bool IsLocal;
    public bool IsPartyMember;
    public bool IsEnemy;
    public ushort? ServerId;

    /// <summary>All outgoing damage (every target) — the "All targets" view.</summary>
    public readonly LiveAccumulator All = new();
    /// <summary>Damage in the encounter's scope (boss only in a boss fight) — the record and the "Boss only" view.</summary>
    public readonly LiveAccumulator Scoped = new();
    /// <summary>Damage to the encounter's bosses (all of them).</summary>
    public long BossDamage;
    /// <summary>Damage per boss entity (multi-boss contribution).</summary>
    public readonly Dictionary<uint, long> DamageByBoss = new();
    public long Healing;
    public long DamageTaken;
    public int Deaths;
    public bool Dead;
    /// <summary>Every death in this encounter: when and (from the kill record) which attack of which entity.</summary>
    public readonly List<(DateTime Time, uint KillerSkill, uint Killer)> DeathLog = new();

    public void ResetLive()
    {
        All.Reset();
        Scoped.Reset();
        BossDamage = 0;
        DamageByBoss.Clear();
        Healing = 0;
        DamageTaken = 0;
    }
}

internal sealed class TargetState
{
    public TargetState(uint id) => Id = id;

    public uint Id { get; }
    public uint? NpcCode;
    public string? PlayerName;
    public bool IsPlayer;
    public bool IsBoss;
    public bool IsDummy;
    public long? LastHp;
    public long? MaxHp;
    public long DamageTaken;
    public long SelfHealing;
    public bool Killed;
    public DateTime? First;
    public DateTime? Last;
}

internal sealed class PvpState
{
    public PvpState(uint enemyId) => EnemyId = enemyId;

    public uint EnemyId { get; }
    public string Name = "";
    public CharacterClass Class;
    public ushort? ServerId;
    public string? GuildName;
    public long DamageDealt;
    public long DamageTaken;
    public bool Killed;
    public DateTime LastActivity;
    public DateTime? LastLocalHit;
}
