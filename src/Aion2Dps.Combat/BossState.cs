namespace Aion2Dps.Combat;

/// <summary>
/// One boss of an encounter. Real traffic: a party can split and fight two bosses at once (Fire Temple: Silver Blade
/// Rotan and Black Smoke Murute), so an encounter holds a list of these, each with its own HP timeline, HP check
/// (§11.4), kill and reset state.
/// </summary>
internal sealed class BossState
{
    public BossState(uint id, int order, DateTime engagedAt)
    {
        Id = id;
        Order = order;
        EngagedAt = engagedAt;
    }

    public uint Id { get; }
    /// <summary>Engagement order inside the encounter (0 = first boss hit).</summary>
    public int Order { get; }
    public DateTime EngagedAt { get; set; }

    public uint? NpcCode;
    public long? MaxHp;
    /// <summary>Max HP came from the game (spawn / stat kind 7 / <c>1B 92</c>) and the boss was near full when engaged.</summary>
    public bool MaxTrusted;
    public long? HpStart;
    public long? HpEnd;

    /// <summary>First / last counted hit on this boss.</summary>
    public DateTime? FirstHit;
    public DateTime? LastHit;
    /// <summary>Last hit by the local player (the overlay shows this boss by default).</summary>
    public DateTime? LastLocalHit;

    public bool Killed;
    public DateTime? KillTime;
    /// <summary>Resets of this boss before the encounter started (same NPC code, this zone visit).</summary>
    public int ResetsBefore;
    /// <summary>Resets (back to full HP) during the encounter.</summary>
    public int Resets;
    /// <summary>The boss reset and was not hit since.</summary>
    public bool InReset;

    public readonly HpTimeline Timeline = new();
    public readonly HpCheckTracker HpCheck = new();

    /// <summary>Alive and engaged (not killed, not sitting in a reset).</summary>
    public bool Active => !Killed && !InReset;
}
