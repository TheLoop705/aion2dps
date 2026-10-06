namespace Aion2Dps.Combat;

// In-world entity bindings (PROTOCOL.md §10.1). Ids are session-scoped and reissued on zone change.

internal enum MaxHpSource : byte
{
    None,
    /// <summary><c>41 36</c> spawn hp_max (§11.2 #1, preferred).</summary>
    Spawn,
    /// <summary><c>1B 92</c> hp_max (§11.2 #2). Overrides the spawn value (the spawn carries the solo/base max).</summary>
    HpUpdate,
    /// <summary><c>00 8D</c> 8-byte stat kind 7 (LIVE-FINDINGS NEW 2): the party-scaled max HP. Highest priority.</summary>
    StatMax,
    /// <summary>Highest current HP seen: a lower bound only (§11.2 #3). Never "trusted" for contribution.</summary>
    HighestSeen,
}

internal enum OwnerSource : byte
{
    None,
    SpawnOwner,
    Anchor,
    CasterName,
    CastLink,
    PowerScalar,
    OnlyElementalist,
    /// <summary>Orphan skill entity using a class skill: the party's only player of that class.</summary>
    OnlyOfClass,
}

internal abstract class Entity
{
    protected Entity(uint id) => Id = id;

    public uint Id { get; }
    public long? Hp;
    public long? MaxHp;
    public bool Dead;
    public DateTime? DeathTime;
}

internal sealed class PlayerEntity : Entity
{
    private readonly int[] _classVotes = new int[12];

    public PlayerEntity(uint id) : base(id) { }

    public string Name = "";
    public CharacterClass Class;
    /// <summary>Class came from an identity/roster record rather than skill votes.</summary>
    public bool ClassAuthoritative;
    public ushort? ServerId;
    public string? GuildName;
    public uint? Level;
    public bool IsLocal;
    /// <summary>Known only from its skills (no <c>33 36</c>/<c>45 36</c>/<c>04 8D</c>/roster binding yet).</summary>
    public bool IsProvisional;
    /// <summary>Traded damage with the local player (PvP).</summary>
    public bool IsEnemy;
    public uint? CharacterId;

    public uint LastScalar;
    public DateTime LastScalarTime;
    public uint PrevScalar;
    public DateTime PrevScalarTime;

    public string DisplayName => Name.Length > 0 ? Name : $"Player {Id}";

    public void NoteScalar(uint scalar, DateTime t)
    {
        if (scalar == 0) return;
        if (scalar != LastScalar)
        {
            PrevScalar = LastScalar;
            PrevScalarTime = LastScalarTime;
        }
        LastScalar = scalar;
        LastScalarTime = t;
    }

    public bool HasRecentScalar(uint scalar, DateTime now, TimeSpan window)
    {
        if (scalar == 0) return false;
        if (LastScalar == scalar && (now - LastScalarTime).Duration() <= window) return true;
        return PrevScalar == scalar && (now - PrevScalarTime).Duration() <= window;
    }

    /// <summary>Majority vote over the class prefixes of skills used (§10.4). Returns true when the class changed.</summary>
    public bool VoteClass(uint skillId)
    {
        if (ClassAuthoritative) return false;
        var c = SkillIds.ClassOf(skillId);
        if (c == CharacterClass.Unknown) return false;
        int i = (int)c;
        if (i <= 0 || i >= _classVotes.Length) return false;
        _classVotes[i]++;
        int best = 0;
        for (int k = 1; k < _classVotes.Length; k++)
            if (_classVotes[k] > _classVotes[best]) best = k;
        var winner = (CharacterClass)best;
        if (winner == Class) return false;
        Class = winner;
        return true;
    }
}

internal class NpcEntity : Entity
{
    public NpcEntity(uint id) : base(id) { }

    public uint NpcCode;
    public NpcInfo? Info;
    public MaxHpSource MaxSource;
    public long HighestHp;
    /// <summary>Lowest HP fraction since the last full-HP state (wipe detection, §11.3).</summary>
    public double MinFraction = 1.0;
    public DateTime SpawnTime;
    /// <summary>Healing recorded for this NPC since its last HP reading (explains an HP rise that is not a reset).</summary>
    public long SelfHealSinceReading;
    /// <summary>Capture time of the last friendly damage this NPC took.</summary>
    public DateTime? LastDamagedAt;
    /// <summary>Created from a hit on an id we never saw spawn (no <c>41 36</c>): may really be a player.</summary>
    public bool Inferred;

    public virtual bool IsSummon => false;
}

/// <summary>Summon / spirit / skill entity. Its damage goes to its owner (§10.3).</summary>
internal sealed class SummonEntity : NpcEntity
{
    public SummonEntity(uint id) : base(id) { }

    public override bool IsSummon => true;

    public uint? OwnerHint;
    public uint? AnchorHint;
    public string? CasterName;
    public uint? FirstSkill;
    public uint PowerScalar;
    /// <summary>True when no <c>41 36</c> was seen (inferred from a spirit skill or a scalar match).</summary>
    public bool Orphan;

    public uint? ResolvedOwner;
    public OwnerSource OwnerSource;
    public DateTime NextAttempt;

    /// <summary>Hits credited to the "Unknown summons" bucket that must move once the owner is learned.</summary>
    public List<Hit>? PendingHits;
    public Encounter? PendingEncounter;
}

internal sealed record LocalIdentity(string Name, ushort ServerId, CharacterClass Class, uint Level, uint? EntityId, bool Authoritative);
