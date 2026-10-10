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

    /// <summary>Other members of your force / party when the fight ended (its group size after the zone change).</summary>
    public int ForceOthersAtEnd;
    public int PartyOthersAtEnd;

    /// <summary>Bosses (or dummies) of a Boss/Dummy encounter in engagement order. Several = a multi-boss fight.</summary>
    public readonly List<BossState> Bosses = new();
    public readonly Dictionary<uint, BossState> BossById = new();

    public readonly List<Hit> Hits = new(1024);
    public readonly Dictionary<uint, CombatantState> Combatants = new();
    public readonly Dictionary<uint, TargetState> Targets = new();
    public readonly Dictionary<uint, PvpState> Pvp = new();
    /// <summary>NPC code of incoming-damage sources, captured at hit time (entities are cleared on zone change).</summary>
    public readonly Dictionary<uint, uint> SourceNpcCodes = new();
    public int PvpKills;

    public bool CaptureGaps;
    public string? Note;

    public TimeSpan? TrainingDuration;
    public DateTime? TrainingEnd;
    public bool TrainingCompleted;

    public bool IsBoss(uint id) => BossById.ContainsKey(id);

    public BossState? Boss(uint id) => BossById.TryGetValue(id, out var b) ? b : null;

    /// <summary>The primary boss: largest max HP, ties → first engaged.</summary>
    public BossState? Primary
    {
        get
        {
            BossState? best = null;
            foreach (var b in Bosses)
                if (best == null || (b.MaxHp ?? 0) > (best.MaxHp ?? 0)) best = b;
            return best;
        }
    }

    public uint? PrimaryBossId => Primary?.Id;

    /// <summary>"Silver Blade Rotan + Black Smoke Murute": the bosses in engagement order.</summary>
    public string BossTitle(IGameData gd)
    {
        if (Bosses.Count == 0) return "Boss";
        var names = new List<string>(Bosses.Count);
        foreach (var b in Bosses) names.Add(b.NpcCode is uint c ? gd.GetNpcName(c) : UnnamedBoss(gd, b.MaxHp) ?? $"Target {b.Id}");
        return string.Join(" + ", names);
    }

    /// <summary>"World boss (45.6M HP)" for a boss whose spawn (NPC code) was missed; null when its max HP is unknown.</summary>
    public string? UnnamedBoss(IGameData gd, long? maxHp) => BossLabel.Unnamed(maxHp, MapId is uint m && gd.IsOpenWorldMap(m));

    /// <summary>
    /// Contribution of one combatant: damage to the bosses with a trusted max HP / the sum of those max HPs.
    /// Null when no boss has a trusted max HP (callers fall back to the share of party damage). With
    /// <paramref name="anyKnownMax"/> (partial view, where a share of the visible damage means nothing) any known max counts.
    /// </summary>
    public double? HpContribution(CombatantState c, bool anyKnownMax = false)
    {
        if (Kind != EncounterKind.Boss) return null;
        long denom = 0, num = 0;
        foreach (var b in Bosses)
        {
            if (!(b.MaxTrusted || anyKnownMax) || b.MaxHp is not long max || max <= 0) continue;
            denom += max;
            num += c.DamageByBoss.GetValueOrDefault(b.Id);
        }
        return denom > 0 ? (double)num / denom : null;
    }

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

    private void NoteBossHit(Hit h)
    {
        if (h.IsDodge || h.Amount <= 0) return;
        if (Kind == EncounterKind.Boss && h.Time > LastActivity) LastActivity = h.Time;
        if (!h.ToBoss || Boss(h.Target) is not { } boss) return;
        if (boss.FirstHit is null || h.Time < boss.FirstHit) boss.FirstHit = h.Time;
        if (boss.LastHit is null || h.Time > boss.LastHit) boss.LastHit = h.Time;
        if (Combatants.TryGetValue(h.Actor, out var c) && c.IsLocal
            && (boss.LastLocalHit is null || h.Time > boss.LastLocalHit)) boss.LastLocalHit = h.Time;
    }

    private void ApplyLive(Hit h, bool combatantsOnly = false)
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
                if (h.ToBoss && h.Amount > 0)
                {
                    c.BossDamage += h.Amount;
                    c.DamageByBoss[h.Target] = c.DamageByBoss.GetValueOrDefault(h.Target) + h.Amount;
                }
                if (!combatantsOnly) NoteTarget(h);
                NoteBossHit(h);
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
                if (!combatantsOnly) NoteTarget(h);
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
                if (!combatantsOnly && Targets.TryGetValue(h.Target, out var t2)) t2.SelfHealing += h.Amount;
                break;
        }
    }

    /// <summary>Recomputes the live totals from the hit list (after summon re-attribution).</summary>
    public void RebuildLive()
    {
        FirstHit = null;
        LastHit = null;
        if (Kind == EncounterKind.Boss) LastActivity = CreatedUtc;
        foreach (var boss in Bosses)
        {
            boss.FirstHit = null;
            boss.LastHit = null;
            boss.LastLocalHit = null;
        }
        foreach (var c in Combatants.Values) c.ResetLive();
        foreach (var t in Targets.Values)
        {
            t.DamageTaken = 0;
            t.SelfHealing = 0;
            t.First = null;
            t.Last = null;
        }
        foreach (var h in Hits) ApplyLive(h);
        foreach (var boss in Bosses)
            if (boss.FirstHit is { } first) boss.EngagedAt = first;
    }

    /// <summary>
    /// Recomputes the live totals of the given combatants only (summon re-attribution moved hits between them). Target
    /// totals do not depend on the actor, so they are left alone. Same result as <see cref="RebuildLive()"/>.
    /// </summary>
    public void RebuildLive(IReadOnlySet<uint> combatants)
    {
        if (combatants.Count == 0) return;
        foreach (var id in combatants)
            if (Combatants.TryGetValue(id, out var c)) c.ResetLive();
        foreach (var h in Hits)
        {
            if (h.Kind == HitKind.TargetSelfHeal) continue;
            uint owner = h.Kind is HitKind.Incoming or HitKind.PvpIn ? h.Target : h.Actor;
            if (combatants.Contains(owner)) ApplyLive(h, combatantsOnly: true);
        }
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
