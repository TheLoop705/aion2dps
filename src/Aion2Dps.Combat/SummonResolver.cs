namespace Aion2Dps.Combat;

internal enum RootKind : byte { Player, Npc, Unresolved }

internal readonly record struct RootResult(RootKind Kind, uint Id);

/// <summary>
/// Summon → owner resolution (§10.3), in order: spawn owner marker, caster anchor (≠ self), inline caster name,
/// cast-variant link, power-scalar match, only Elementalist for spirit skills; else unresolved ("Unknown summons").
/// Chains are followed up to 8 hops. Hits credited to the bucket are re-attributed once the owner is learned.
/// </summary>
internal sealed class SummonResolver
{
    public const int MaxHops = 8;
    private const int MaxCasts = 8192;
    private static readonly TimeSpan CastHistory = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CastBefore = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CastAfter = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ScalarWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly EntityTracker _entities;
    private readonly PartyTracker _party;
    private readonly Queue<(DateTime Time, uint Actor, uint Skill)> _casts = new();
    private readonly HashSet<SummonEntity> _pending = new();

    public SummonResolver(EntityTracker entities, PartyTracker party)
    {
        _entities = entities;
        _party = party;
    }

    public int PendingCount => _pending.Count;

    /// <summary>A layout-0 notice or <c>02 38</c> cast. From a summon it gives the summon's first skill; from a player it is a link candidate.</summary>
    public void OnCast(uint actor, uint skill, DateTime t, uint scalar)
    {
        if (skill == 0) return;
        var e = _entities.Get(actor);
        if (e is SummonEntity s)
        {
            s.FirstSkill ??= skill;
            if (s.PowerScalar == 0 && scalar != 0) s.PowerScalar = scalar;
            return;
        }
        if (e is NpcEntity) return;
        if (e is PlayerEntity p && scalar != 0) p.NoteScalar(scalar, t);
        _casts.Enqueue((t, actor, skill));
        while (_casts.Count > 0 && (_casts.Count > MaxCasts || t - _casts.Peek().Time > CastHistory)) _casts.Dequeue();
    }

    public void OnSpawn(SummonEntity s, DateTime t) => TryResolve(s, t, force: true);

    /// <summary>Walks the owner chain to a player or NPC.</summary>
    public RootResult ResolveRoot(SummonEntity s, DateTime t, bool force = false)
    {
        Entity cur = s;
        for (int hop = 0; hop <= MaxHops; hop++)
        {
            switch (cur)
            {
                case PlayerEntity p:
                    return new RootResult(RootKind.Player, p.Id);
                case SummonEntity ss:
                {
                    if (ss.ResolvedOwner is null) TryResolve(ss, t, force);
                    if (ss.ResolvedOwner is not uint owner) return new RootResult(RootKind.Unresolved, 0);
                    var next = _entities.Get(owner);
                    if (next == null)
                    {
                        // Owner id not bound yet (mid-stream start). Monster skills → an NPC owner; otherwise a player.
                        if (ss.FirstSkill is uint fs && SkillIds.GetKind(fs) == SkillKind.Npc) return new RootResult(RootKind.Npc, owner);
                        next = _entities.CreateProvisionalPlayer(owner);
                    }
                    cur = next;
                    break;
                }
                case NpcEntity n:
                    return new RootResult(RootKind.Npc, n.Id);
                default:
                    return new RootResult(RootKind.Unresolved, 0);
            }
        }
        return new RootResult(RootKind.Unresolved, 0);
    }

    public bool TryResolve(SummonEntity s, DateTime t, bool force = false)
    {
        if (s.ResolvedOwner is not null) return true;
        if (!force && t < s.NextAttempt) return false;

        if (s.OwnerHint is uint o && o != s.Id && o is >= 1 and <= 9_999_999) return Set(s, o, OwnerSource.SpawnOwner);
        if (s.AnchorHint is uint a && a != s.Id && a != 0) return Set(s, a, OwnerSource.Anchor);
        if (_entities.FindPlayerByName(s.CasterName) is { } byName) return Set(s, byName.Id, OwnerSource.CasterName);
        if (s.FirstSkill is uint fs && FindCastLink(s, fs) is uint caster) return Set(s, caster, OwnerSource.CastLink);
        if (s.PowerScalar != 0 && FindUniqueScalar(s.PowerScalar, t, CharacterClass.Unknown, s.Id) is { } byScalar)
            return Set(s, byScalar.Id, OwnerSource.PowerScalar);
        if (s.FirstSkill is uint fs2 && SkillIds.GetKind(fs2) == SkillKind.Spirit && FindOnlyElementalist() is { } ele)
            return Set(s, ele.Id, OwnerSource.OnlyElementalist);

        s.NextAttempt = t + RetryDelay;
        return false;
    }

    private static bool Set(SummonEntity s, uint owner, OwnerSource source)
    {
        s.ResolvedOwner = owner;
        s.OwnerSource = source;
        return true;
    }

    private uint? FindCastLink(SummonEntity s, uint firstSkill)
    {
        uint key = firstSkill / 10;
        DateTime from = s.SpawnTime - CastBefore, to = s.SpawnTime + CastAfter;
        uint? best = null;
        TimeSpan bestDt = TimeSpan.MaxValue;
        bool ambiguous = false;
        foreach (var (time, actor, skill) in _casts)
        {
            if (time < from || time > to || actor == s.Id || skill / 10 != key) continue;
            var e = _entities.Get(actor);
            if (e is NpcEntity) continue;
            if (e is null && SkillIds.GetKind(skill) != SkillKind.Player) continue;
            var dt = (time - s.SpawnTime).Duration();
            if (dt < bestDt)
            {
                ambiguous = best is uint b && b != actor && dt == bestDt;
                best = actor;
                bestDt = dt;
            }
            else if (dt == bestDt && best != actor)
            {
                ambiguous = true;
            }
        }
        return ambiguous ? null : best;
    }

    /// <summary>The single player whose recent power scalar equals <paramref name="scalar"/> (optionally of one class).</summary>
    public PlayerEntity? FindUniqueScalar(uint scalar, DateTime t, CharacterClass cls, uint exclude)
    {
        PlayerEntity? found = null;
        foreach (var e in _entities.All)
        {
            if (e is not PlayerEntity p || p.Id == exclude || p.IsEnemy) continue;
            if (cls != CharacterClass.Unknown && p.Class != cls) continue;
            if (!p.HasRecentScalar(scalar, t, ScalarWindow)) continue;
            if (found != null) return null;
            found = p;
        }
        return found;
    }

    private PlayerEntity? FindOnlyElementalist()
    {
        if (_party.HasRoster)
        {
            PlayerEntity? only = null;
            int count = 0;
            foreach (var m in _party.Members)
            {
                if (m.Class != CharacterClass.Elementalist && ClassInfo.FromCode(m.ClassCode) != CharacterClass.Elementalist) continue;
                count++;
                only = _entities.FindPlayerByName(m.Name);
            }
            if (count == 1 && only != null) return only;
            if (count > 1) return null;
        }
        PlayerEntity? found = null;
        foreach (var e in _entities.All)
        {
            if (e is not PlayerEntity { IsEnemy: false, Class: CharacterClass.Elementalist } p) continue;
            if (found != null) return null;
            found = p;
        }
        return found;
    }

    // ───────────── re-attribution ─────────────

    public void AddPending(SummonEntity s, Hit h, Encounter enc)
    {
        if (!ReferenceEquals(s.PendingEncounter, enc))
        {
            s.PendingHits = new List<Hit>();
            s.PendingEncounter = enc;
        }
        (s.PendingHits ??= new List<Hit>()).Add(h);
        _pending.Add(s);
    }

    /// <summary>Moves pending bucket hits of summons whose owner chain now ends in a player. Returns the encounters that changed.</summary>
    public HashSet<Encounter>? Reattribute(DateTime t, bool force)
    {
        if (_pending.Count == 0) return null;
        HashSet<Encounter>? changed = null;
        List<SummonEntity>? done = null;
        foreach (var s in _pending)
        {
            if (s.PendingEncounter is not { Finalized: false } enc || s.PendingHits is not { Count: > 0 } hits
                || !ReferenceEquals(_entities.Get(s.Id), s))
            {
                (done ??= new()).Add(s);
                continue;
            }
            var root = ResolveRoot(s, t, force);
            if (root.Kind == RootKind.Unresolved) continue;
            if (root.Kind == RootKind.Player)
            {
                foreach (var h in hits) h.Actor = root.Id;
                (changed ??= new()).Add(enc);
            }
            (done ??= new()).Add(s);
        }
        if (done != null)
        {
            foreach (var s in done)
            {
                s.PendingHits = null;
                s.PendingEncounter = null;
                _pending.Remove(s);
            }
        }
        return changed;
    }

    public void Clear()
    {
        _casts.Clear();
        foreach (var s in _pending)
        {
            s.PendingHits = null;
            s.PendingEncounter = null;
        }
        _pending.Clear();
    }
}
