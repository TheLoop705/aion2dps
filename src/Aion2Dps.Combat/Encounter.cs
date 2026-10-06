namespace Aion2Dps.Combat;

/// <summary>Mutable state of one encounter (§12). Everything in the record is rebuilt from <see cref="Hits"/>.</summary>
internal sealed class Encounter
{
    private readonly Action<CombatantState> _identity;

    public Encounter(DateTime createdUtc, EncounterKind kind, Action<CombatantState> identity)
    {
        CreatedUtc = createdUtc;
        Kind = kind;
        LastActivity = createdUtc;
        _identity = identity;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; }
    public EncounterKind Kind;
    public EncounterOutcome Outcome = EncounterOutcome.InProgress;

    public bool Ended;
    public bool Finalized;
    public DateTime? EndedAt;
    /// <summary>Kill grace: killing blows until this time still count, then the encounter is finalized.</summary>
    public DateTime? FinalizeAt;

    /// <summary>First / last counted (in-scope) damage.</summary>
    public DateTime? FirstHit;
    public DateTime? LastHit;
    /// <summary>Last outgoing damage of any kind (idle timeout clock).</summary>
    public DateTime LastActivity;

    public uint? MapId;

    public uint? PrimaryBossId;
    public uint? BossNpcCode;
    public long? BossMaxHp;
    public long? BossHpStart;
    public long? BossHpEnd;
    public bool BossMaxTrusted;
    public readonly HashSet<uint> BossTargets = new();
    public int ResetCount;

    public readonly List<Hit> Hits = new(1024);
    public readonly Dictionary<uint, CombatantState> Combatants = new();
    public readonly Dictionary<uint, TargetState> Targets = new();
    public readonly Dictionary<uint, PvpState> Pvp = new();
    /// <summary>NPC code of incoming-damage sources, captured at hit time (entities are cleared on zone change).</summary>
    public readonly Dictionary<uint, uint> SourceNpcCodes = new();
    public int PvpKills;

    public readonly HpTimeline Timeline = new();
    public readonly HpCheckTracker HpCheck = new();

    public bool CaptureGaps;
    public string? Note;

    public TimeSpan? TrainingDuration;
    public DateTime? TrainingEnd;
    public bool TrainingCompleted;

    public DateTime StartUtc => FirstHit ?? CreatedUtc;
    public DateTime EndUtc => LastHit ?? StartUtc;

    /// <summary>Fight clock: max(1, last hit − first hit) seconds (a completed training run uses its configured length).</summary>
    public double DurationSeconds
    {
        get
        {
            if (Kind == EncounterKind.Training && TrainingCompleted && TrainingDuration is { } d) return Math.Max(1, d.TotalSeconds);
            if (FirstHit is not { } f || LastHit is not { } l) return 1;
            return Math.Max(1, (l - f).TotalSeconds);
        }
    }

    public CombatantState GetCombatant(uint id)
    {
        if (Combatants.TryGetValue(id, out var c)) return c;
        c = new CombatantState(id, id == CombatEngine.UnknownSummonsEntityId ? CombatantKind.UnknownSummons : CombatantKind.Player);
        if (c.Kind == CombatantKind.UnknownSummons) c.Name = "Unknown summons";
        Combatants[id] = c;
        _identity(c);
        return c;
    }

    public void AddHit(Hit h)
    {
        Hits.Add(h);
        ApplyLive(h);
    }

    private void NoteScopedHit(Hit h)
    {
        if (h.Amount <= 0 || h.IsDodge) return;
        if (FirstHit is null || h.Time < FirstHit) FirstHit = h.Time;
        if (LastHit is null || h.Time > LastHit) LastHit = h.Time;
    }

    private void NoteTarget(Hit h)
    {
        if (h.IsDodge || !Targets.TryGetValue(h.Target, out var ts)) return;
        ts.DamageTaken += h.Amount;
        if (ts.First is null || h.Time < ts.First) ts.First = h.Time;
        if (ts.Last is null || h.Time > ts.Last) ts.Last = h.Time;
    }

    private void ApplyLive(Hit h)
    {
        switch (h.Kind)
        {
            case HitKind.Outgoing:
            {
                if (h.IsDodge) return;
                var c = GetCombatant(h.Actor);
                c.All.Add(h);
                if (h.InScope)
                {
                    c.Scoped.Add(h);
                    NoteScopedHit(h);
                }
                if (h.ToPrimaryBoss) c.BossDamage += h.Amount;
                NoteTarget(h);
                break;
            }
            case HitKind.PvpOut:
            {
                if (h.IsDodge) return;
                var c = GetCombatant(h.Actor);
                if (h.InScope)
                {
                    c.All.Add(h);
                    c.Scoped.Add(h);
                    NoteScopedHit(h);
                }
                NoteTarget(h);
                break;
            }
            case HitKind.PvpIn:
                if (h.IsDodge) return;
                GetCombatant(h.Target).DamageTaken += h.Amount;
                if (Kind == EncounterKind.Pvp) NoteScopedHit(h);
                break;
            case HitKind.Incoming:
                if (h.IsDodge) return;
                GetCombatant(h.Target).DamageTaken += h.Amount;
                break;
            case HitKind.Heal:
                GetCombatant(h.Actor).Healing += h.Amount;
                break;
            case HitKind.TargetSelfHeal:
                if (Targets.TryGetValue(h.Target, out var t2)) t2.SelfHealing += h.Amount;
                break;
        }
    }

    /// <summary>Recomputes the live totals from the hit list (after summon re-attribution).</summary>
    public void RebuildLive()
    {
        foreach (var c in Combatants.Values) c.ResetLive();
        foreach (var t in Targets.Values)
        {
            t.DamageTaken = 0;
            t.SelfHealing = 0;
        }
        foreach (var h in Hits) ApplyLive(h);
    }

    /// <summary>The local player's id changed mid-encounter: move its hits and state to the new id.</summary>
    public void RekeyCombatant(uint oldId, uint newId)
    {
        if (oldId == newId) return;
        foreach (var h in Hits)
        {
            if (h.Kind is HitKind.Outgoing or HitKind.PvpOut or HitKind.Heal)
            {
                if (h.Actor == oldId) h.Actor = newId;
                if (h.Source == oldId) h.Source = newId;
            }
            if (h.Kind is HitKind.Incoming or HitKind.PvpIn or HitKind.Heal && h.Target == oldId) h.Target = newId;
        }
        if (Combatants.Remove(oldId, out var old))
        {
            var c = GetCombatant(newId);
            c.Deaths += old.Deaths;
            c.Dead = old.Dead;
        }
        RebuildLive();
    }
}
