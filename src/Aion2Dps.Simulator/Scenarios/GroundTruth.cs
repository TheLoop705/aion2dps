using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator;

/// <summary>
/// Exact expected results of a scenario, computed from the script. Semantics (PROTOCOL.md §8.2.2, §8.3, §12):
/// <list type="bullet">
/// <item><b>Damage</b> = Σ <c>04 38</c> amounts (layouts 4/6, extra hits already included) + Σ <c>05 38</c> flag-0x0A tick
/// amounts, dealt by the player or its summons to hostile targets. Excludes heal skills, self-targeted records, link-range
/// skills, the dodge pseudo-skill, layout-0 notices and the boss self-heal tick.</item>
/// <item><b>Hits/Crits</b> count direct damage records only (one per record; DoT ticks excluded).</item>
/// <item><b>Healing</b> = heal-skill <c>04 38</c> amounts + HoT (<c>0x0B</c>) heal fields + self-heals (actor == target). No overheal model.</item>
/// <item>Encounter duration = max(1 s, last hit − first hit), over counted damage of the encounter.</item>
/// </list>
/// </summary>
public sealed class ScenarioGroundTruth
{
    public required string ScenarioName { get; init; }
    public uint LocalPlayerId { get; init; }
    public string LocalPlayerName { get; init; } = "";
    public CharacterClass LocalPlayerClass { get; init; }
    public uint? MapId { get; init; }
    public required IReadOnlyList<ExpectedEncounter> Encounters { get; init; }
    /// <summary>Whole-scenario totals per player entity id (all encounters plus anything outside them).</summary>
    public required IReadOnlyDictionary<uint, PlayerTruth> Players { get; init; }

    public long TotalDamage => Players.Values.Where(p => p.Kind == CombatantKind.Player).Sum(p => p.Damage);
    public long TotalHealing => Players.Values.Sum(p => p.Healing);
    /// <summary>The last encounter (the kill in boss scenarios).</summary>
    public ExpectedEncounter? Final => Encounters.Count > 0 ? Encounters[^1] : null;
}

/// <summary>One encounter the combat engine is expected to produce.</summary>
public sealed class ExpectedEncounter
{
    public int Index { get; init; }
    public EncounterKind Kind { get; init; }
    public EncounterOutcome Outcome { get; init; }
    /// <summary>Offset of the first counted hit from the scenario start.</summary>
    public TimeSpan FirstHit { get; internal set; }
    /// <summary>Offset of the last counted hit.</summary>
    public TimeSpan LastHit { get; internal set; }
    /// <summary>Encounter DPS clock: max(1, last − first) seconds.</summary>
    public double DurationSeconds => Math.Max(1.0, (LastHit - FirstHit).TotalSeconds);

    public uint? BossEntityId { get; init; }
    public uint? BossNpcCode { get; init; }
    public long? BossMaxHp { get; internal set; }
    public long? BossHpStart { get; internal set; }
    /// <summary>Last boss HP reading of the encounter (0 on a kill).</summary>
    public long? BossHpEnd { get; internal set; }
    /// <summary>Σ boss self-heal ticks (§8.3) inside the encounter.</summary>
    public long BossSelfHealing { get; internal set; }
    /// <summary>Σ player damage to the boss inside the encounter.</summary>
    public long BossDamageTaken { get; internal set; }
    /// <summary>HP check (§11.4): BossHpStart − BossHpEnd (= BossDamageTaken − BossSelfHealing).</summary>
    public long? BossHpLost => BossHpStart - BossHpEnd;

    /// <summary>Σ damage of friendly players (<see cref="CombatantKind.Player"/>).</summary>
    public long TotalDamage => Players.Values.Where(p => p.Kind == CombatantKind.Player).Sum(p => p.Damage);
    public double PartyDps => TotalDamage / DurationSeconds;

    /// <summary>Per player entity id (friendly players and, in PvP, enemy players).</summary>
    public IReadOnlyDictionary<uint, PlayerTruth> Players => _players;
    internal readonly Dictionary<uint, PlayerTruth> _players = new();

    /// <summary>Entities that took counted damage.</summary>
    public IReadOnlyCollection<uint> TargetIds => _targets;
    internal readonly SortedSet<uint> _targets = new();

    /// <summary>Kills inside the encounter as (target, killer) in order.</summary>
    public IReadOnlyList<(uint Target, uint Killer)> Kills => _kills;
    internal readonly List<(uint, uint)> _kills = new();

    public string Note { get; init; } = "";

    public override string ToString() =>
        $"#{Index} {Kind}/{Outcome} {FirstHit.TotalSeconds:0.00}-{LastHit.TotalSeconds:0.00}s dmg {TotalDamage:N0}" +
        (BossMaxHp is null ? "" : $" bossMax {BossMaxHp:N0}");
}

/// <summary>Expected numbers of one player (in an encounter or for the whole scenario).</summary>
public sealed class PlayerTruth
{
    public uint EntityId { get; init; }
    public string Name { get; init; } = "";
    public CharacterClass Class { get; init; }
    public CombatantKind Kind { get; init; }
    public bool IsLocal { get; init; }
    public bool IsPartyMember { get; init; }
    public ushort ServerId { get; init; }

    public long Damage { get; internal set; }
    /// <summary>Direct <c>04 38</c> damage by the player itself (no DoT, no summons).</summary>
    public long DirectDamage { get; internal set; }
    public long DotDamage { get; internal set; }
    public long SummonDamage { get; internal set; }
    /// <summary>Damage to the encounter's boss (whole-scenario row: to any boss).</summary>
    public long BossDamage { get; internal set; }
    public long Healing { get; internal set; }
    public long SelfHealing { get; internal set; }
    /// <summary>Direct damage records (own + summons); DoT ticks excluded.</summary>
    public int Hits { get; internal set; }
    public int Crits { get; internal set; }
    public int DotTicks { get; internal set; }
    /// <summary>Records carrying an extra-hit list.</summary>
    public int MultiHitRecords { get; internal set; }
    /// <summary>Σ extra hits (n) over all records.</summary>
    public int ExtraHits { get; internal set; }
    public int BackHits { get; internal set; }
    public int FrontHits { get; internal set; }
    public int PerfectHits { get; internal set; }
    public int DoubleHits { get; internal set; }
    public long DamageTaken { get; internal set; }
    public int HitsTaken { get; internal set; }
    public int Dodges { get; internal set; }
    public int Parries { get; internal set; }
    public int Deaths { get; internal set; }

    public double CritRate => Hits == 0 ? 0 : (double)Crits / Hits;

    /// <summary>Per exact skill id (summon skills keyed by the summon's skill id).</summary>
    public IReadOnlyDictionary<uint, SkillTruth> Skills => _skills;
    internal readonly Dictionary<uint, SkillTruth> _skills = new();

    internal SkillTruth Skill(uint id) => _skills.TryGetValue(id, out var s) ? s : _skills[id] = new SkillTruth { SkillId = id };

    internal PlayerTruth CloneEmpty() => new()
    {
        EntityId = EntityId, Name = Name, Class = Class, Kind = Kind, IsLocal = IsLocal, IsPartyMember = IsPartyMember, ServerId = ServerId,
    };

    public override string ToString() => $"{Name} ({Class}) dmg {Damage:N0} heal {Healing:N0} hits {Hits} crits {Crits}";
}

/// <summary>Expected per-skill numbers.</summary>
public sealed class SkillTruth
{
    public uint SkillId { get; init; }
    public long Damage { get; internal set; }
    public long Healing { get; internal set; }
    public int Hits { get; internal set; }
    public int Crits { get; internal set; }
    public int Ticks { get; internal set; }
}
