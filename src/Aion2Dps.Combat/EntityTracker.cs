namespace Aion2Dps.Combat;

/// <summary>
/// Entity tables (§10.1): players, NPCs and summons by session id, players by name, and the local player (§10.2).
/// Not thread-safe; owned by <see cref="CombatCore"/> under the engine lock.
/// </summary>
internal sealed class EntityTracker
{
    private const int MaxPendingHp = 4096;

    private readonly Dictionary<uint, Entity> _byId = new();
    private readonly Dictionary<string, PlayerEntity> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, (long? Hp, long? Max)> _pendingHp = new();
    private readonly Dictionary<uint, int> _selfStatVotes = new();
    private readonly Dictionary<uint, uint> _characterIdToEntity = new();

    public LocalIdentity? Local { get; private set; }

    public int Count => _byId.Count;

    public IEnumerable<Entity> All => _byId.Values;

    public Entity? Get(uint id) => _byId.TryGetValue(id, out var e) ? e : null;

    public PlayerEntity? LocalEntity =>
        Local?.EntityId is uint id && _byId.TryGetValue(id, out var e) && e is PlayerEntity { IsLocal: true } p ? p : null;

    public uint? LocalId => LocalEntity?.Id;

    public PlayerEntity? FindPlayerByName(string? name) =>
        !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out var p) && _byId.TryGetValue(p.Id, out var cur) && ReferenceEquals(cur, p)
            ? p
            : null;

    public PlayerEntity? FindByCharacterId(uint characterId) =>
        characterId != 0 && _characterIdToEntity.TryGetValue(characterId, out var id) && Get(id) is PlayerEntity p ? p : null;

    // ───────────── binding ─────────────

    private void Bind(Entity e)
    {
        if (_byId.TryGetValue(e.Id, out var old) && !ReferenceEquals(old, e)) Unbind(old);
        _byId[e.Id] = e;
        if (_pendingHp.Remove(e.Id, out var pending))
        {
            // 1B 92 / stat kind 7 max seen before the entity was bound: it beats the spawn's base hp_max (LIVE-FINDINGS NEW 2).
            if (pending.Max is > 0 && e is NpcEntity { MaxSource: MaxHpSource.None or MaxHpSource.HighestSeen or MaxHpSource.Spawn } n)
            {
                n.MaxHp = pending.Max;
                n.MaxSource = MaxHpSource.HpUpdate;
            }
            else if (pending.Max is > 0 && e is PlayerEntity) e.MaxHp = pending.Max;

            if (pending.Hp is long hp && e.Hp is null)
            {
                e.Hp = hp;
                if (e is NpcEntity npc)
                {
                    npc.HighestHp = Math.Max(npc.HighestHp, hp);
                    if (npc.MaxSource == MaxHpSource.None && npc.HighestHp > 0)
                    {
                        npc.MaxHp = npc.HighestHp;
                        npc.MaxSource = MaxHpSource.HighestSeen;
                    }
                }
            }
        }
    }

    private void Unbind(Entity old)
    {
        if (old is PlayerEntity p)
        {
            if (p.Name.Length > 0 && _byName.TryGetValue(p.Name, out var cur) && ReferenceEquals(cur, p)) _byName.Remove(p.Name);
            if (p.IsLocal && Local is { } l && l.EntityId == p.Id) Local = l with { EntityId = null };
        }
    }

    public void SetName(PlayerEntity p, string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (p.Name.Length > 0 && !string.Equals(p.Name, name, StringComparison.Ordinal)
            && _byName.TryGetValue(p.Name, out var cur) && ReferenceEquals(cur, p))
            _byName.Remove(p.Name);
        p.Name = name;
        _byName[name] = p;
    }

    private static CharacterClass ClassOrCode(CharacterClass c, uint code) => c != CharacterClass.Unknown ? c : ClassInfo.FromCode(code);

    /// <summary><c>33 36</c>: the authoritative local player. Returns the entity and whether the character name changed.</summary>
    public (PlayerEntity Player, bool NameChanged, uint? PreviousId) SetLocal(SelfInfoEvent e)
    {
        bool nameChanged = Local is { Authoritative: true } l && l.Name.Length > 0 && e.Name.Length > 0
                           && !string.Equals(l.Name, e.Name, StringComparison.Ordinal);
        var prev = LocalEntity;
        uint? prevId = prev?.Id;

        PlayerEntity p;
        if (Get(e.Entity) is PlayerEntity existing
            && (existing.IsLocal || existing.IsProvisional || existing.Name.Length == 0
                || string.Equals(existing.Name, e.Name, StringComparison.Ordinal)))
            p = existing;
        else
        {
            p = new PlayerEntity(e.Entity);
            Bind(p);
        }

        if (prev != null && !ReferenceEquals(prev, p))
        {
            prev.IsLocal = false;
            // The old id belonged to us; ids are reissued, so drop the stale binding.
            if (_byId.TryGetValue(prev.Id, out var cur) && ReferenceEquals(cur, prev))
            {
                Unbind(prev);
                _byId.Remove(prev.Id);
            }
        }

        p.IsLocal = true;
        p.IsProvisional = false;
        p.IsEnemy = false;
        SetName(p, e.Name);
        var cls = ClassOrCode(e.Class, e.ClassCode);
        if (cls != CharacterClass.Unknown)
        {
            p.Class = cls;
            p.ClassAuthoritative = true;
        }
        if (e.ServerId != 0) p.ServerId = e.ServerId;
        if (e.Level != 0) p.Level = e.Level;
        Local = new LocalIdentity(e.Name, e.ServerId, p.Class, e.Level, e.Entity, Authoritative: true);
        _selfStatVotes.Clear();
        return (p, nameChanged, prevId != p.Id ? prevId : null);
    }

    /// <summary><c>45 36</c>: another player. An id bound to something else (or to another name) is replaced.</summary>
    public PlayerEntity UpsertPlayer(uint id, string? name, CharacterClass cls, ushort? serverId, string? guild)
    {
        var p = Get(id) as PlayerEntity;
        if (p == null
            || (p.Name.Length > 0 && !string.IsNullOrEmpty(name) && !string.Equals(p.Name, name, StringComparison.Ordinal)))
        {
            bool wasLocal = p?.IsLocal == true;
            p = new PlayerEntity(id);
            Bind(p);
            if (wasLocal && Local is { } l) Local = l with { EntityId = null };
        }
        p.IsProvisional = false;
        SetName(p, name);
        if (cls != CharacterClass.Unknown)
        {
            p.Class = cls;
            p.ClassAuthoritative = true;
        }
        if (serverId is > 0) p.ServerId = serverId;
        if (!string.IsNullOrEmpty(guild)) p.GuildName = guild;
        return p;
    }

    public PlayerEntity UpsertPlayer(PlayerInfoEvent e) =>
        UpsertPlayer(e.Entity, e.Name, ClassOrCode(e.Class, e.ClassCode), e.ServerId, e.GuildName);

    public NpcEntity Spawn(SpawnEvent e, NpcInfo? info)
    {
        bool summon = e.IsSummonLike || (e.OwnerId is uint o && o != 0 && o != e.Entity);
        // A re-spawn whose head was not recognised (NpcCode 0) must not erase what the first spawn told us.
        var prev = e.NpcCode == 0 && Get(e.Entity) is NpcEntity pn && pn.IsSummon == summon && pn.NpcCode != 0 ? pn : null;
        NpcEntity n;
        if (summon)
        {
            // A late first spawn supplies the owner of a skill entity already seen attacking. Keep the orphan
            // instance so its unresolved hits can move to that owner. An already spawned entity is still replaced:
            // its id may have been reused, and its old hits must never move to the new summon.
            var s = Get(e.Entity) is SummonEntity { Orphan: true, Dead: false } orphan ? orphan : new SummonEntity(e.Entity);
            s.Orphan = false;
            s.OwnerHint = e.OwnerId is uint ow && ow != 0 && ow != e.Entity ? ow : null;
            s.AnchorHint = e.AnchorId is uint an && an != 0 && an != e.Entity ? an : null;
            s.CasterName = string.IsNullOrEmpty(e.CasterName) ? null : e.CasterName;
            s.ResolvedOwner = null;
            s.OwnerSource = OwnerSource.None;
            s.NextAttempt = default;
            n = s;
        }
        else
        {
            n = new NpcEntity(e.Entity);
        }
        n.NpcCode = e.NpcCode;
        n.Info = info;
        n.SpawnTime = e.Time;
        if (prev != null)
        {
            n.NpcCode = prev.NpcCode;
            n.Info = prev.Info;
            if (e.HpMax is not > 0 && prev.MaxHp is > 0)
            {
                n.MaxHp = prev.MaxHp;
                n.MaxSource = prev.MaxSource;
            }
            if (e.HpCurrent is null)
            {
                n.Hp = prev.Hp;
                n.Dead = prev.Dead;
                n.DeathTime = prev.DeathTime;
            }
            n.HighestHp = prev.HighestHp;
        }
        if (e.HpMax is > 0)
        {
            n.MaxHp = e.HpMax;
            n.MaxSource = MaxHpSource.Spawn;
        }
        if (e.HpCurrent is long hp and >= 0)
        {
            n.Hp = hp;
            n.HighestHp = hp;
            if (hp == 0 && n.MaxHp is > 0)
            {
                n.Dead = true;
                n.DeathTime = e.Time;
            }
        }
        if (n.MaxSource == MaxHpSource.None && n.HighestHp > 0)
        {
            n.MaxHp = n.HighestHp;
            n.MaxSource = MaxHpSource.HighestSeen;
        }
        else if (n.MaxHp is long known && n.HighestHp > known)
        {
            n.MaxHp = n.HighestHp; // spawn hp_max below the current HP (party-scaled boss): trust the HP reading
        }
        if (n.MaxHp is long max && max > 0 && n.Hp is long cur) n.MinFraction = Math.Min(1.0, (double)cur / max);
        Bind(n);
        return n;
    }

    public PlayerEntity CreateProvisionalPlayer(uint id)
    {
        var p = new PlayerEntity(id) { IsProvisional = true };
        Bind(p);
        return p;
    }

    public NpcEntity CreateUnknownNpc(uint id)
    {
        var n = new NpcEntity(id) { Inferred = true };
        Bind(n);
        return n;
    }

    public SummonEntity CreateOrphanSummon(uint id, DateTime t, uint? firstSkill, uint scalar)
    {
        var s = new SummonEntity(id) { Orphan = true, SpawnTime = t, FirstSkill = firstSkill, PowerScalar = scalar };
        Bind(s);
        return s;
    }

    /// <summary>HP for an id not bound yet (applied when it gets bound).</summary>
    public void StorePendingHp(uint id, long? hp, long? max)
    {
        if (_pendingHp.Count >= MaxPendingHp && !_pendingHp.ContainsKey(id)) _pendingHp.Clear();
        _pendingHp.TryGetValue(id, out var cur);
        _pendingHp[id] = (hp ?? cur.Hp, max is > 0 ? max : cur.Max);
    }

    public void LinkCharacterId(uint entity, uint characterId)
    {
        if (characterId == 0) return;
        _characterIdToEntity[characterId] = entity;
        if (Get(entity) is PlayerEntity p) p.CharacterId = characterId;
    }

    /// <summary>§10.2 #3 fallback: the dominant receiver of self-stat frames. Returns true when it became the local player.</summary>
    public bool NoteSelfStats(uint id)
    {
        if (Local is { Authoritative: true }) return false;
        _selfStatVotes.TryGetValue(id, out int v);
        _selfStatVotes[id] = ++v;
        if (v < 5) return false;
        int runnerUp = 0;
        foreach (var (k, n) in _selfStatVotes)
            if (k != id && n > runnerUp) runnerUp = n;
        if (v < 3 * runnerUp) return false;
        if (Local?.EntityId == id) return false;

        var p = Get(id) as PlayerEntity;
        if (p == null)
        {
            if (Get(id) != null) return false; // an NPC/summon: not us
            p = CreateProvisionalPlayer(id);
        }
        if (LocalEntity is { } old && !ReferenceEquals(old, p)) old.IsLocal = false;
        p.IsLocal = true;
        p.IsEnemy = false;
        Local = new LocalIdentity(p.Name, p.ServerId ?? 0, p.Class, p.Level ?? 0, id, Authoritative: false);
        return true;
    }

    /// <summary>The inferred local player got a name (roster or remembered character): keep the identity in sync.</summary>
    public void RenameInferredLocal(PlayerEntity p)
    {
        if (Local is { Authoritative: false } l && l.EntityId == p.Id)
            Local = l with { Name = p.Name, ServerId = p.ServerId ?? l.ServerId, Class = p.Class };
    }

    /// <summary>Non-teleport map load: ids are reissued. Keeps the local identity (keyed by name) and its current binding.</summary>
    public void ClearForZoneChange()
    {
        var local = LocalEntity;
        _byId.Clear();
        _byName.Clear();
        _pendingHp.Clear();
        _selfStatVotes.Clear();
        _characterIdToEntity.Clear();
        if (local != null)
        {
            local.Hp = null;
            local.MaxHp = null;
            local.Dead = false;
            _byId[local.Id] = local;
            if (local.Name.Length > 0) _byName[local.Name] = local;
        }
    }

    public void ClearAll()
    {
        _byId.Clear();
        _byName.Clear();
        _pendingHp.Clear();
        _selfStatVotes.Clear();
        _characterIdToEntity.Clear();
        Local = null;
    }
}
