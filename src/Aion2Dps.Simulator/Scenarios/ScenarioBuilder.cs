using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator;

/// <summary>How a damage record is shaped on the wire.</summary>
public sealed record HitStyle
{
    public static readonly HitStyle Plain = new() { Layout = 4 };

    /// <summary>4 = amount only, 6 = amount + mods/dir.</summary>
    public byte Layout { get; init; } = 6;
    public bool Crit { get; init; }
    public HitMods Mods { get; init; }
    public HitDirection Direction { get; init; }
    /// <summary>Breakdown of the amount into extra hits (already included in the amount). Empty = none.</summary>
    public IReadOnlyList<uint> ExtraHits { get; init; } = Array.Empty<uint>();
    /// <summary>Set switch bit 0x10 (layout 4: one extra varint; layout 6: mods 0x80 is set as in the real samples).</summary>
    public bool Switch10 { get; init; }
    public uint HitIndex { get; init; } = 1;
}

/// <summary>
/// Builds a <see cref="Scenario"/>: the script is added in any order, then <see cref="Build"/> sorts it, inserts the
/// <c>00 8D</c> HP updates of tracked NPCs (exactly max − Σdamage + Σself-heals), derives the max HP of NPCs that die
/// from the damage they take, materialises spawns with that max HP, and computes the ground truth.
/// All times are seconds from the scenario start, rounded to 50 ms server ticks.
/// </summary>
public sealed class ScenarioBuilder
{
    public const double TickSeconds = 0.05;

    private readonly List<Pending> _events = new();
    private readonly Dictionary<uint, SimPlayer> _players = new();
    private readonly Dictionary<uint, NpcState> _npcs = new();
    private readonly Dictionary<uint, uint> _summonOwner = new();
    private readonly List<EncounterSpec> _encounters = new();
    private int _openEncounter = -1;
    private uint _stack = 1;
    private uint _mapId;

    public ScenarioBuilder(string name, int seed, string description = "")
    {
        Name = name;
        Seed = seed;
        Description = description;
        Rng = new Random(seed);
    }

    public string Name { get; }
    public int Seed { get; }
    public string Description { get; }
    /// <summary>Deterministic randomness for scripting (seeded).</summary>
    public Random Rng { get; }

    public IReadOnlyDictionary<uint, SimPlayer> Players => _players;

    private sealed class Pending
    {
        public required double Time;
        public required int Order;
        public GameEvent? Event;
        public Func<long, GameEvent>? Deferred;  // materialised with the NPC's final max HP
        public uint DeferredNpc;
        public TruthTag? Truth;
        public int Encounter = -1;
        public uint ResetNpc;                    // HP reset marker (no wire event)
    }

    private sealed class NpcState
    {
        public required SimNpc Npc;
        public long? FixedMaxHp;
        public bool TrackHp = true;
        public uint Scalar = 10000;
    }

    private sealed record EncounterSpec(EncounterKind Kind, EncounterOutcome Outcome, uint? Boss, string Note);

    // ───────────────────────────── registry ─────────────────────────────

    public SimPlayer AddPlayer(SimPlayer player)
    {
        _players[player.EntityId] = player;
        return player;
    }

    /// <summary>Registers an NPC. <paramref name="fixedMaxHp"/> null = max HP derived from the damage it takes after its
    /// last reset (so it reaches exactly 0 on its last hit).</summary>
    public SimNpc AddNpc(uint entity, uint npcCode, bool isBoss = false, bool isDummy = false, long? fixedMaxHp = null, bool trackHp = true)
    {
        var npc = new SimNpc { EntityId = entity, NpcCode = npcCode, IsBoss = isBoss, IsDummy = isDummy, MaxHp = fixedMaxHp ?? 0 };
        _npcs[entity] = new NpcState { Npc = npc, FixedMaxHp = fixedMaxHp, TrackHp = trackHp };
        return npc;
    }

    public void AddSummon(uint summon, uint owner) => _summonOwner[summon] = owner;

    private uint OwnerOf(uint actor) => _summonOwner.TryGetValue(actor, out var o) ? o : actor;

    private uint ScalarOf(uint actor)
    {
        uint owner = OwnerOf(actor);
        if (_players.TryGetValue(owner, out var p)) return p.PowerScalar;
        return _npcs.TryGetValue(actor, out var n) ? n.Scalar : 10000;
    }

    private static double Q(double seconds) => Math.Round(seconds / TickSeconds) * TickSeconds;

    // ───────────────────────────── encounters ─────────────────────────────

    /// <summary>Opens an expected encounter: counted events added until <see cref="EndEncounter"/> belong to it.</summary>
    public int BeginEncounter(EncounterKind kind, EncounterOutcome outcome, uint? bossEntity = null, string note = "")
    {
        _encounters.Add(new EncounterSpec(kind, outcome, bossEntity, note));
        _openEncounter = _encounters.Count - 1;
        return _openEncounter;
    }

    public void EndEncounter() => _openEncounter = -1;

    // ───────────────────────────── raw ─────────────────────────────

    /// <summary>Adds any event at <paramref name="t"/> seconds.</summary>
    public void Add(double t, GameEvent e, TruthTag? truth = null) =>
        _events.Add(new Pending { Time = Q(t), Order = _events.Count, Event = e, Truth = truth, Encounter = _openEncounter });

    private void AddDeferred(double t, uint npc, Func<long, GameEvent> make) =>
        _events.Add(new Pending { Time = Q(t), Order = _events.Count, Deferred = make, DeferredNpc = npc, Encounter = _openEncounter });

    // ───────────────────────────── world / identity ─────────────────────────────

    public void MapLoad(double t, uint mapId, uint loadCount = 1)
    {
        _mapId = mapId;
        Add(t, new MapLoadEvent { LoadCount = loadCount, MapId = mapId });
    }

    public void SelfInfo(double t, SimPlayer p) => Add(t, new SelfInfoEvent
    {
        Entity = p.EntityId, Name = p.Name, ServerId = p.ServerId, ClassCode = p.ClassCode, Class = p.Class, Level = p.Level,
    });

    public void PlayerAppears(double t, SimPlayer p) => Add(t, new PlayerInfoEvent
    {
        Entity = p.EntityId, Name = p.Name, ClassCode = p.ClassCode, Class = p.Class,
        ServerId = p.GuildName is null ? null : p.ServerId, GuildName = p.GuildName,
    });

    public void Roster(double t, uint partyKey, uint? dungeonId, IReadOnlyList<SimPlayer> members)
    {
        var list = members.Select((p, i) => new PartyMember
        {
            Slot = i + 1,
            DbId = ((ulong)p.ServerId << 48) | p.CharacterId,
            CharacterId = p.CharacterId,
            ServerId = p.ServerId,
            Name = p.Name,
            ClassCode = p.ClassCode,
            Class = p.Class,
            Level = p.Level,
            GearScore = p.GearScore,
            CombatPower = p.CombatPower,
        }).ToList();
        Add(t, new PartyRosterEvent { PartyKey = partyKey, DungeonId = dungeonId, Members = list });
    }

    public void GlobalIdLink(double t, SimPlayer p) => Add(t, new GlobalIdLinkEvent { Entity = p.EntityId, CharacterId = p.CharacterId });

    /// <summary><c>23 36</c> with entity 0 = the local player was teleported.</summary>
    public void Teleport(double t, uint entity = 0) => Add(t, new TeleportEvent { Entity = entity });

    /// <summary>Monster spawn (kind 0x0C, flags 0x22, anchor = itself). HP fields are filled with the final max HP.</summary>
    public void SpawnNpc(double t, SimNpc npc, float x, float y, float z)
    {
        AddDeferred(t, npc.EntityId, max => new SpawnEvent
        {
            Entity = npc.EntityId, KindByte = 0x0C, KindFlags = 0x22, NpcCode = npc.NpcCode, X = x, Y = y, Z = z,
            HpCurrent = max, HpMax = max, AnchorId = npc.EntityId, IsSummonLike = false,
        });
    }

    /// <summary><c>1B 92</c> HP/MP of a tracked NPC (full HP), filled with the final max HP.</summary>
    public void NpcHpInfo(double t, SimNpc npc) =>
        AddDeferred(t, npc.EntityId, max => new HpUpdateEvent { Entity = npc.EntityId, Hp = max, HpMax = max });

    /// <summary>
    /// Summon / spirit / skill-entity spawn (kind 0x1F) owned by <paramref name="owner"/> via <c>07 02 06</c>.
    /// Spirits put the owner in the caster anchor too (<paramref name="anchorIsOwner"/>); winds and auras anchor to themselves.
    /// </summary>
    public void SpawnSummon(double t, uint entity, uint npcCode, uint owner, bool anchorIsOwner, byte kindFlags = 0x10,
        string? casterName = null, long hp = 5215, float x = 15312.5f, float y = 12704.2f, float z = 645.1f)
    {
        AddSummon(entity, owner);
        Add(t, new SpawnEvent
        {
            Entity = entity, KindByte = 0x1F, KindFlags = kindFlags, CasterName = casterName, NpcCode = npcCode,
            X = x, Y = y, Z = z, HpCurrent = hp, HpMax = hp, OwnerId = owner, AnchorId = anchorIsOwner ? owner : entity,
            IsSummonLike = true,
        });
    }

    /// <summary><c>2A 38</c> buff and, after the duration, its <c>0E 92</c> removal.</summary>
    public void Buff(double t, uint target, uint caster, uint skill, uint durationMs, DateTime serverEpochStart)
    {
        uint buffId = skill * 10 + 1;
        uint stack = _stack++;
        ulong expiry = (ulong)(new DateTimeOffset(serverEpochStart).ToUnixTimeMilliseconds() + (long)(Q(t) * 1000) + durationMs);
        Add(t, new BuffAppliedEvent
        {
            Target = target, Stack = stack, BuffId = buffId, DurationMs = durationMs, ExpiryUnixMs = expiry, Caster = caster, SourceSkill = skill,
        });
        Add(t + durationMs / 1000.0, new BuffRemovedEvent { Target = target, BuffId = buffId });
    }

    // ───────────────────────────── combat ─────────────────────────────

    private DamageEvent MakeDamage(uint actor, uint target, uint skill, byte layout, uint swFlags, byte damageType,
        HitMods mods, HitDirection dir, uint hitIndex, long? amount, IReadOnlyList<uint> extras, uint effectSuffix = 11)
    {
        uint sw = layout | swFlags;
        if (extras.Count > 0) sw |= 0x20;
        return new DamageEvent
        {
            Target = target,
            Actor = actor,
            SkillRaw = skill,
            SkillId = SkillIds.Normalize(skill),
            Switch = sw,
            Layout = layout,
            HitTag = (byte)Rng.Next(256),
            DamageType = damageType,
            Mods = (layout & 0x02) != 0 ? mods : HitMods.None,
            Direction = (layout & 0x02) != 0 ? dir : HitDirection.None,
            EffectId = PacketEncoders.EffectFor(skill, effectSuffix),
            HitIndex = hitIndex,
            PowerScalar = ScalarOf(actor),
            Amount = (layout & 0x04) != 0 ? amount : null,
            ExtraHits = extras,
            EffectValidated = true,
        };
    }

    private bool IsHostile(uint credited, uint target)
    {
        if (_npcs.ContainsKey(target)) return _players.ContainsKey(credited);
        if (_players.TryGetValue(target, out var tp) && _players.TryGetValue(credited, out var cp)) return tp.IsEnemy != cp.IsEnemy;
        return false;
    }

    /// <summary>A damage record (layout 4/6) from a player, a summon or an NPC.</summary>
    public void Hit(double t, uint actor, uint target, uint skill, long amount, HitStyle? style = null)
    {
        style ??= HitStyle.Plain;
        var mods = style.Mods | (style.Switch10 && style.Layout == 6 ? (HitMods)0x80 : HitMods.None);
        var e = MakeDamage(actor, target, skill, style.Layout, style.Switch10 ? 0x10u : 0, style.Crit ? (byte)3 : (byte)2,
            mods, style.Direction, style.HitIndex, amount, style.ExtraHits);

        uint credited = OwnerOf(actor);
        TruthTag? truth = null;
        var kind = SkillIds.GetKind(skill);
        bool excluded = actor == target || kind == SkillKind.Link || skill == SkillIds.Dodge || SkillBook.IsHealSkill(skill) || amount <= 0;
        if (!excluded && _npcs.ContainsKey(actor) && _players.ContainsKey(target))
        {
            truth = new TruthTag
            {
                Kind = TruthKind.Incoming, Actor = actor, Target = target, SkillId = skill, Amount = amount, Crit = style.Crit,
                Parry = (style.Mods & HitMods.Parry) != 0, Mods = e.Mods, Direction = e.Direction,
            };
        }
        else if (!excluded && IsHostile(credited, target))
        {
            truth = new TruthTag
            {
                Kind = TruthKind.Damage, Credited = credited, Actor = actor, Target = target, SkillId = e.SkillId, Amount = amount,
                Crit = style.Crit, Summon = credited != actor, ExtraHits = style.ExtraHits.Count, Mods = e.Mods, Direction = e.Direction,
            };
        }

        Add(t, e, truth);
    }

    /// <summary>Layout-0 no-damage cast/companion notice (§8.2 layout 0): not a hit.</summary>
    public void CastNotice(double t, uint actor, uint target, uint skill, uint hitIndex = 2) =>
        Add(t, MakeDamage(actor, target, skill, 0, 0, 2, HitMods.None, HitDirection.None, hitIndex, null, Array.Empty<uint>(), 12));

    /// <summary><c>02 38</c> cast announcement.</summary>
    public void Cast(double t, uint actor, uint target, uint skill) => Add(t, new CastEvent { Actor = actor, SkillRaw = skill, Target = target });

    /// <summary>A link-range record (e.g. the spirit's own spawn self-heal): neither damage nor healing.</summary>
    public void LinkRecord(double t, uint actor, uint target, uint skill, long amount) =>
        Add(t, MakeDamage(actor, target, skill, 4, 0, 2, HitMods.None, HitDirection.None, 1, amount, Array.Empty<uint>()));

    /// <summary><c>05 38</c> flags 0x0A damage tick.</summary>
    public void DotTick(double t, uint actor, uint target, uint skill, long amount, uint? stack = null)
    {
        uint credited = OwnerOf(actor);
        var e = new DotEvent
        {
            Target = target, Flags = 0x0A, Actor = actor, Stack = stack ?? 0x10, EffectId = PacketEncoders.EffectFor(skill),
            Amount = amount, SkillId = skill,
        };
        TruthTag? truth = IsHostile(credited, target) && actor != target && amount > 0
            ? new TruthTag { Kind = TruthKind.Damage, Credited = credited, Actor = actor, Target = target, SkillId = skill, Amount = amount, Dot = true, Summon = credited != actor }
            : null;
        Add(t, e, truth);
    }

    /// <summary>Direct heal (layout 4, heal-family skill) on a friendly target.</summary>
    public void Heal(double t, uint healer, uint target, uint skill, long amount, bool crit = false)
    {
        var e = MakeDamage(healer, target, skill, 4, 0, crit ? (byte)3 : (byte)2, HitMods.None, HitDirection.None, 1, amount, Array.Empty<uint>());
        Add(t, e, new TruthTag { Kind = TruthKind.Heal, Credited = healer, Actor = healer, Target = target, SkillId = skill, Amount = amount, Crit = crit });
    }

    /// <summary>Self-heal: layout-4 record with actor == target (e.g. Blood Absorption).</summary>
    public void SelfHeal(double t, uint player, uint skill, long amount)
    {
        var e = MakeDamage(player, player, skill, 4, 0, 2, HitMods.None, HitDirection.None, 1, amount, Array.Empty<uint>());
        Add(t, e, new TruthTag { Kind = TruthKind.Heal, Credited = player, Actor = player, Target = player, SkillId = skill, Amount = amount });
    }

    /// <summary>HoT announcement (flags 0x09: heal = total). Not a heal by itself.</summary>
    public void HotAnnounce(double t, uint healer, uint target, uint skill, long total, uint stack) =>
        Add(t, new DotEvent { Target = target, Flags = 0x09, Actor = healer, Stack = stack, EffectId = PacketEncoders.EffectFor(skill), Heal = total, SkillId = skill });

    /// <summary>HoT tick (flags 0x0B): <paramref name="heal"/> is healed now, <paramref name="remaining"/> is still to come.</summary>
    public void HotTick(double t, uint healer, uint target, uint skill, long heal, long remaining, uint stack) =>
        Add(t, new DotEvent { Target = target, Flags = 0x0B, Actor = healer, Stack = stack, EffectId = PacketEncoders.EffectFor(skill), Amount = remaining, Heal = heal, SkillId = skill },
            new TruthTag { Kind = TruthKind.Heal, Credited = healer, Actor = healer, Target = target, SkillId = skill, Amount = heal, Dot = true });

    /// <summary>
    /// The boss self-heal trap (§8.3): a 0x0A tick whose effect is a 9-digit monster effect while the skill is the
    /// triggering player's class skill. The NPC heals itself by <paramref name="amount"/>; it is not player damage.
    /// </summary>
    public void NpcSelfHealTick(double t, uint npc, uint triggeringPlayer, uint monsterEffect, uint playerSkill, long amount) =>
        Add(t, new DotEvent { Target = npc, Flags = 0x0A, Actor = triggeringPlayer, Stack = 0x17, EffectId = monsterEffect, Amount = amount, SkillId = playerSkill },
            new TruthTag { Kind = TruthKind.NpcSelfHeal, Actor = triggeringPlayer, Target = npc, SkillId = playerSkill, Amount = amount });

    /// <summary>Dodge: the NPC's attack carries the dodge pseudo-skill 11000100, amount 0 (§8.2.2).</summary>
    public void Dodge(double t, uint npc, uint player)
    {
        var e = MakeDamage(npc, player, SkillIds.Dodge, 4, 0, 2, HitMods.None, HitDirection.None, 1, 0, Array.Empty<uint>());
        Add(t, e, new TruthTag { Kind = TruthKind.Dodge, Actor = npc, Target = player, SkillId = SkillIds.Dodge });
    }

    /// <summary><c>04 8D</c> kill record (killer = player).</summary>
    public void Kill(double t, uint target, uint killer, uint skill)
    {
        _players.TryGetValue(killer, out var k);
        Add(t, new KillEvent
        {
            Target = target, SkillRaw = skill, Killer = killer, KillerServerId = k?.ServerId ?? 0,
            KillerName = k?.Name ?? "", KillerGuildName = k?.GuildName ?? "",
        });
    }

    /// <summary><c>04 8D</c> despawn form (skill 0, killer 0), e.g. a summon expiring.</summary>
    public void Despawn(double t, uint entity) => Add(t, new KillEvent { Target = entity, KillerName = "", KillerGuildName = "" });

    /// <summary><c>42 36</c> death; flag 3 = died in combat.</summary>
    public void Death(double t, uint entity, uint flag = 3) => Add(t, new DeathEvent { Entity = entity, Flag = flag });

    /// <summary>The NPC returns to full HP (wipe reset): an <c>00 8D</c> with the max HP is inserted.</summary>
    public void HpReset(double t, uint npc) =>
        _events.Add(new Pending { Time = Q(t), Order = _events.Count, ResetNpc = npc, Encounter = _openEncounter });

    // ───────────────────────────── build ─────────────────────────────

    /// <summary>Finalises the script. <paramref name="idleTail"/> = seconds of quiet after the last event.</summary>
    public Scenario Build(double idleTail)
    {
        var sorted = _events.OrderBy(p => p.Time).ThenBy(p => p.Order).ToList();

        // 1. Max HP per NPC: fixed, or the net loss after its last reset.
        var maxHp = new Dictionary<uint, long>();
        var lossSinceReset = new Dictionary<uint, long>();
        foreach (var (id, _) in _npcs) lossSinceReset[id] = 0;
        foreach (var p in sorted)
        {
            if (p.ResetNpc != 0) { lossSinceReset[p.ResetNpc] = 0; continue; }
            if (NpcDelta(p) is var (npc, delta)) lossSinceReset[npc] += delta;
        }

        foreach (var (id, st) in _npcs)
        {
            long max = st.FixedMaxHp ?? lossSinceReset[id];
            if (max <= 0) throw new InvalidOperationException($"NPC {id} has no damage to derive its max HP from.");
            maxHp[id] = max;
        }

        // 2. Materialise, insert HP updates after each tick's run of HP-affecting events, validate HP never drops to 0 early.
        var final = new List<ScriptedEvent>(sorted.Count * 2);
        var loss = _npcs.Keys.ToDictionary(id => id, _ => 0L);
        var lastDamageIndex = new Dictionary<uint, int>();
        for (int i = 0; i < sorted.Count; i++)
            if (NpcDelta(sorted[i]) is var (npc, d) && d > 0) lastDamageIndex[npc] = i;

        var dirty = new SortedSet<uint>();
        for (int i = 0; i < sorted.Count; i++)
        {
            var p = sorted[i];
            var offset = TimeSpan.FromMilliseconds(Math.Round(p.Time * 1000));
            if (p.ResetNpc != 0)
            {
                loss[p.ResetNpc] = 0;
                if (_npcs[p.ResetNpc].TrackHp) dirty.Add(p.ResetNpc);
            }
            else
            {
                var e = p.Event ?? p.Deferred!(maxHp[p.DeferredNpc]);
                final.Add(new ScriptedEvent(offset, e) { Truth = p.Truth, EncounterIndex = p.Encounter });
                if (NpcDelta(p) is var (npc, delta))
                {
                    loss[npc] += delta;
                    long hp = maxHp[npc] - loss[npc];
                    bool dies = _npcs[npc].FixedMaxHp is null;
                    if (dies && hp <= 0 && i != lastDamageIndex.GetValueOrDefault(npc, -1))
                        throw new InvalidOperationException($"{Name}: NPC {npc} reached {hp} HP before its last hit at {p.Time:0.00}s.");
                    if (_npcs[npc].TrackHp) dirty.Add(npc);
                }
            }

            bool runContinues = i + 1 < sorted.Count && sorted[i + 1].Time == p.Time && (NpcDelta(sorted[i + 1]) is not null || sorted[i + 1].ResetNpc != 0);
            if (dirty.Count > 0 && !runContinues)
            {
                foreach (uint npc in dirty)
                    final.Add(new ScriptedEvent(offset, new EntityStatsEvent { Entity = npc, Format = 0x02, CurrentHp = maxHp[npc] - loss[npc] }));
                dirty.Clear();
            }
        }

        var npcs = _npcs.Values.Select(s => s.Npc with { MaxHp = maxHp[s.Npc.EntityId] }).ToList();
        double end = (sorted.Count > 0 ? sorted[^1].Time : 0) + idleTail;
        var truth = ComputeTruth(final, maxHp);
        return new Scenario
        {
            Name = Name,
            Description = Description,
            Seed = Seed,
            Duration = TimeSpan.FromMilliseconds(Math.Round(Q(end) * 1000)),
            Events = final,
            Players = _players.Values.ToList(),
            Npcs = npcs,
            Truth = truth,
        };
    }

    /// <summary>(npc, +loss) of an event that changes a tracked NPC's HP.</summary>
    private (uint, long)? NpcDelta(Pending p)
    {
        if (p.Truth is not { } t || !_npcs.ContainsKey(t.Target)) return null;
        return t.Kind switch
        {
            TruthKind.Damage => (t.Target, t.Amount),
            TruthKind.NpcSelfHeal => (t.Target, -t.Amount),
            _ => null,
        };
    }

    private ScenarioGroundTruth ComputeTruth(List<ScriptedEvent> events, Dictionary<uint, long> maxHp)
    {
        var local = _players.Values.FirstOrDefault(p => p.IsLocal);
        PlayerTruth NewTruth(SimPlayer p) => new()
        {
            EntityId = p.EntityId, Name = p.Name, Class = p.Class, Kind = p.IsEnemy ? CombatantKind.EnemyPlayer : CombatantKind.Player,
            IsLocal = p.IsLocal, IsPartyMember = p.IsPartyMember, ServerId = p.ServerId,
        };

        var whole = _players.Values.ToDictionary(p => p.EntityId, NewTruth);
        var encounters = _encounters.Select((s, i) => new ExpectedEncounter
        {
            Index = i, Kind = s.Kind, Outcome = s.Outcome, BossEntityId = s.Boss,
            BossNpcCode = s.Boss is uint b && _npcs.TryGetValue(b, out var n) ? n.Npc.NpcCode : null,
            BossMaxHp = s.Boss is uint b2 && maxHp.TryGetValue(b2, out var m) ? m : null,
            Note = s.Note,
        }).ToList();

        var firstHit = new TimeSpan?[encounters.Count];
        var lastHit = new TimeSpan?[encounters.Count];
        var runHp = maxHp.ToDictionary(k => k.Key, v => v.Value);

        foreach (var se in events)
        {
            var enc = se.EncounterIndex >= 0 ? encounters[se.EncounterIndex] : null;

            if (se.Event is EntityStatsEvent { CurrentHp: long hp } st && runHp.ContainsKey(st.Entity))
            {
                runHp[st.Entity] = hp;
                continue;
            }

            if (se.Event is KillEvent { Killer: not 0 } k && enc is not null) enc._kills.Add((k.Target, k.Killer));
            if (se.Event is DeathEvent { Flag: 3 } d && _players.ContainsKey(d.Entity))
            {
                whole[d.Entity].Deaths++;
                if (enc is not null) Row(enc, d.Entity).Deaths++;
            }

            if (se.Truth is not { } t) continue;
            if (enc is not null && t.Kind == TruthKind.Damage)
            {
                firstHit[enc.Index] ??= se.Offset;
                lastHit[enc.Index] = se.Offset;
                if (enc.BossEntityId == t.Target && enc.BossHpStart is null) enc.BossHpStart = runHp[t.Target];
            }

            if (runHp.ContainsKey(t.Target) && t.Kind is TruthKind.Damage or TruthKind.NpcSelfHeal)
            {
                runHp[t.Target] += t.Kind == TruthKind.Damage ? -t.Amount : t.Amount;
                if (enc is not null && enc.BossEntityId == t.Target && enc.BossHpStart is not null) enc.BossHpEnd = runHp[t.Target];
            }

            Apply(whole, t, isBossTarget: _npcs.TryGetValue(t.Target, out var tn) && tn.Npc.IsBoss);
            if (enc is not null)
            {
                Apply(enc._players, t, isBossTarget: enc.BossEntityId == t.Target);
                if (t.Kind == TruthKind.Damage) enc._targets.Add(t.Target);
                if (enc.BossEntityId == t.Target && t.Kind == TruthKind.NpcSelfHeal) enc.BossSelfHealing += t.Amount;
                if (enc.BossEntityId == t.Target && t.Kind == TruthKind.Damage) enc.BossDamageTaken += t.Amount;
            }
        }

        for (int i = 0; i < encounters.Count; i++)
        {
            encounters[i].FirstHit = firstHit[i] ?? TimeSpan.Zero;
            encounters[i].LastHit = lastHit[i] ?? TimeSpan.Zero;
        }

        return new ScenarioGroundTruth
        {
            ScenarioName = Name,
            LocalPlayerId = local?.EntityId ?? 0,
            LocalPlayerName = local?.Name ?? "",
            LocalPlayerClass = local?.Class ?? CharacterClass.Unknown,
            MapId = _mapId == 0 ? null : _mapId,
            Encounters = encounters,
            Players = whole,
        };

        PlayerTruth Row(ExpectedEncounter enc, uint id) =>
            enc._players.TryGetValue(id, out var r) ? r : enc._players[id] = whole[id].CloneEmpty();

        void Apply(Dictionary<uint, PlayerTruth> rows, TruthTag t, bool isBossTarget)
        {
            PlayerTruth? Get(uint id)
            {
                if (!_players.ContainsKey(id)) return null;
                if (rows.TryGetValue(id, out var r)) return r;
                return rows[id] = whole[id].CloneEmpty();
            }

            switch (t.Kind)
            {
                case TruthKind.Damage:
                {
                    var r = Get(t.Credited)!;
                    var s = r.Skill(t.SkillId);
                    r.Damage += t.Amount;
                    s.Damage += t.Amount;
                    if (isBossTarget) r.BossDamage += t.Amount;
                    if (t.Dot) { r.DotDamage += t.Amount; r.DotTicks++; s.Ticks++; }
                    else
                    {
                        if (t.Summon) r.SummonDamage += t.Amount; else r.DirectDamage += t.Amount;
                        r.Hits++;
                        s.Hits++;
                        if (t.Crit) { r.Crits++; s.Crits++; }
                        if (t.ExtraHits > 0) { r.MultiHitRecords++; r.ExtraHits += t.ExtraHits; }
                        if (t.Direction == HitDirection.Back) r.BackHits++;
                        if (t.Direction == HitDirection.Front) r.FrontHits++;
                        if ((t.Mods & HitMods.Perfect) != 0) r.PerfectHits++;
                        if ((t.Mods & HitMods.Double) != 0) r.DoubleHits++;
                    }

                    if (Get(t.Target) is { } victim)
                    {
                        victim.DamageTaken += t.Amount;
                        if (!t.Dot) victim.HitsTaken++;
                    }

                    break;
                }
                case TruthKind.Heal:
                {
                    var r = Get(t.Credited)!;
                    r.Healing += t.Amount;
                    r.Skill(t.SkillId).Healing += t.Amount;
                    if (t.Target == t.Credited) r.SelfHealing += t.Amount;
                    break;
                }
                case TruthKind.Incoming:
                {
                    var r = Get(t.Target)!;
                    r.DamageTaken += t.Amount;
                    r.HitsTaken++;
                    if (t.Parry) r.Parries++;
                    break;
                }
                case TruthKind.Dodge:
                    Get(t.Target)!.Dodges++;
                    break;
            }
        }
    }
}
