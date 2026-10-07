namespace Aion2Dps.Combat;

/// <summary>
/// The single-threaded heart of the engine: entity tables, classification of every event (§8.2.2, §8.3), encounter
/// lifecycle (§12, feature spec §1.7). <see cref="CombatEngine"/> serialises access with one lock.
/// </summary>
internal sealed class CombatCore
{
    public const long MaxAmount = 99_999_999;
    public const long MaxDotAmount = 100_000_000;
    public static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);
    /// <summary>
    /// A new boss engaged while another boss of the active encounter was hit within this window joins that encounter
    /// (bosses fought at the same time, e.g. a party split on Rotan and Murute). Otherwise the fights are sequential and
    /// the new boss gets an encounter of its own.
    /// </summary>
    public static readonly TimeSpan MultiBossJoinWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PvpDeathCredit = TimeSpan.FromSeconds(10);
    /// <summary>Safety net: a trash encounter longer than this ends with <see cref="EncounterOutcome.Timeout"/> (the next
    /// hit starts a new one), so a long stay in a busy zone never grows one encounter without bound.</summary>
    public static readonly TimeSpan MaxTrashDuration = TimeSpan.FromMinutes(30);
    /// <summary>Safety net: a trash encounter with more hits than this ends with <see cref="EncounterOutcome.Timeout"/>.</summary>
    public const int MaxTrashHits = 200_000;

    private readonly Dictionary<ulong, int> _resetCounts = new();
    private readonly List<EncounterRecord> _completed = new();
    private readonly ShieldedIncomingTracker _shieldedIncoming = new();
    /// <summary>
    /// A killed encounter still inside its kill grace while a newer encounter became current (an add was hit right
    /// after the boss died). Late killing blows on its dead targets still go there; it is finalized when the grace ends.
    /// </summary>
    private Encounter? _grace;

    public CombatCore(IGameData gameData, EngineOptions options, Func<long>? clientMs = null)
    {
        GameData = gameData;
        Options = options;
        Summons = new SummonResolver(Entities, Party);
        Ping = new PingTracker(clientMs);
    }

    public IGameData GameData { get; }
    public EngineOptions Options { get; }
    public EntityTracker Entities { get; } = new();
    public PartyTracker Party { get; } = new();
    public SummonResolver Summons { get; }
    public BuffTracker Buffs { get; } = new();
    public PingTracker Ping { get; }

    /// <summary>The latest encounter (active, or ended and still displayed). Null when the display was cleared.</summary>
    public Encounter? Current { get; private set; }
    public MeterMode Mode { get; set; } = MeterMode.BossOnly;
    public bool SawTraffic { get; private set; }
    public DateTime? LastEventTime { get; private set; }
    public uint? MapId { get; private set; }
    public long? ServerClockOffsetMs { get; private set; }
    public uint? SelectedTargetId { get; set; }
    public bool TrainingArmed { get; private set; }
    public TimeSpan TrainingDuration { get; private set; }
    public string? TransientStatus { get; private set; }
    public long EventErrors { get; set; }
    public long DroppedRecords { get; private set; }

    public bool IsActive => Current is { Ended: false };

    public LocalPlayerInfo? LocalPlayerInfo =>
        Entities.Local is { Authoritative: true } l
            ? new LocalPlayerInfo(l.EntityId ?? 0, l.Name, l.ServerId, l.Class, l.Level)
            : null;

    public List<EncounterRecord>? TakeCompleted()
    {
        if (_completed.Count == 0) return null;
        var list = new List<EncounterRecord>(_completed);
        _completed.Clear();
        return list;
    }

    // ═════════════════════════════ dispatch ═════════════════════════════

    public void Handle(GameEvent ev)
    {
        SawTraffic = true;
        var t = ev.Time;
        _shieldedIncoming.BeginEvent(t);
        if (LastEventTime is null || t > LastEventTime) LastEventTime = t;
        CheckTimers(t);

        switch (ev)
        {
            case DamageEvent e: OnDamage(e); break;
            case DotEvent e: OnDot(e); break;
            case EntityStatsEvent e: OnEntityStats(e); break;
            case HpUpdateEvent e: OnHp(e.Entity, e.Hp, e.HpMax, e.Time); break;
            case SpawnEvent e: OnSpawn(e); break;
            case SelfInfoEvent e: OnSelfInfo(e); break;
            case PlayerInfoEvent e: OnPlayerInfo(e); break;
            case KillEvent e: OnKill(e); break;
            case DeathEvent e: OnDeath(e); break;
            case MapLoadEvent e: OnMapLoad(e); break;
            case PartyRosterEvent e: OnRoster(e); break;
            case BuffAppliedEvent e: OnBuffApplied(e); break;
            case BuffRemovedEvent e: Buffs.OnRemoved(e); break;
            case CastEvent e:
                Summons.OnCast(e.Actor, SkillIds.Normalize(e.SkillRaw), e.Time, 0);
                ApplyReattribution(Summons.Reattribute(t, force: false));
                break;
            case PingEvent e: Ping.OnPing(e.ClientSentMs); break;
            case HeartbeatEvent e:
                if (e.ServerUnixMs > 0) ServerClockOffsetMs = (long)e.ServerUnixMs - UnixMs(e.Time);
                break;
            case GlobalIdLinkEvent e:
                Entities.LinkCharacterId(e.Entity, e.CharacterId);
                JoinRoster();
                PlayersChanged(t);
                break;
            // TeleportEvent (same-map teleport) and BattleToggleEvent (L, hint only) need no action.
        }
    }

    private static long UnixMs(DateTime t) => (long)(t - DateTime.UnixEpoch).TotalMilliseconds;

    /// <summary>Idle timeouts, training timer and the kill grace, by capture time.</summary>
    public void CheckTimers(DateTime now)
    {
        if (_grace is { } g && (g.Finalized || g.FinalizeAt is not { } gf || now > gf)) FinalizeGrace();
        var enc = Current;
        if (enc == null) return;
        if (!enc.Ended)
        {
            if (enc.Kind == EncounterKind.Training)
            {
                if (enc.TrainingEnd is { } te && now >= te)
                {
                    enc.TrainingCompleted = true;
                    End(enc, EncounterOutcome.Timeout, te);
                }
            }
            else
            {
                double timeout = enc.Kind is EncounterKind.Boss or EncounterKind.Dummy
                    ? Options.BossIdleTimeoutSeconds
                    : Options.IdleTimeoutSeconds;
                if ((now - enc.LastActivity).TotalSeconds > timeout) End(enc, EncounterOutcome.Timeout, now);
                else if (TrashTooLong(enc)) End(enc, EncounterOutcome.Timeout, enc.LastActivity);
            }
        }
        if (enc.Ended && !enc.Finalized && enc.FinalizeAt is { } fa && now > fa) Finalize(enc);
    }

    // ═════════════════════════════ identity events ═════════════════════════════

    private void OnSelfInfo(SelfInfoEvent e)
    {
        // §12: a 33 36 with a different name = another character → new session context. The previous character's
        // encounter is finalized first, while Entities.Local still describes the character who fought it.
        bool switching = Entities.Local is { Authoritative: true } cur && cur.Name.Length > 0 && e.Name.Length > 0
                         && !string.Equals(cur.Name, e.Name, StringComparison.Ordinal);
        if (switching) EndActive(EncounterOutcome.ZoneChange, e.Time);
        var (p, nameChanged, prevId) = Entities.SetLocal(e);
        if (nameChanged)
        {
            ClearZoneState();
        }
        else if (prevId is uint old && Current is { Finalized: false } enc)
        {
            enc.RekeyCombatant(old, p.Id);
        }
        JoinRoster();
        PlayersChanged(e.Time);
    }

    private void OnPlayerInfo(PlayerInfoEvent e)
    {
        Entities.UpsertPlayer(e);
        JoinRoster();
        PlayersChanged(e.Time);
    }

    private void OnRoster(PartyRosterEvent e)
    {
        Party.Replace(e);
        JoinRoster();
        PlayersChanged(e.Time);
    }

    private void PlayersChanged(DateTime t)
    {
        ApplyReattribution(Summons.Reattribute(t, force: true));
        RefreshIdentities(Current);
    }

    /// <summary>§13: join roster names to entities (by name, by global character id, then by unique class).</summary>
    private void JoinRoster()
    {
        if (!Party.HasRoster) return;
        List<PartyMember>? unbound = null;
        foreach (var m in Party.Members)
        {
            var p = Entities.FindPlayerByName(m.Name);
            if (p == null && Entities.FindByCharacterId(m.CharacterId) is { Name.Length: 0 } byId)
            {
                Entities.SetName(byId, m.Name);
                byId.IsProvisional = false;
                p = byId;
            }
            if (p != null) ApplyMember(p, m);
            else (unbound ??= new()).Add(m);
        }
        if (unbound == null) return;

        foreach (var group in unbound.GroupBy(MemberClass))
        {
            if (group.Key == CharacterClass.Unknown || group.Count() != 1) continue;
            PlayerEntity? candidate = null;
            int n = 0;
            foreach (var e in Entities.All)
            {
                if (e is PlayerEntity { IsLocal: false, IsEnemy: false } pp && pp.Name.Length == 0 && pp.Class == group.Key)
                {
                    candidate = pp;
                    n++;
                }
            }
            if (n != 1 || candidate == null) continue;
            var member = group.First();
            Entities.SetName(candidate, member.Name);
            candidate.IsProvisional = false;
            ApplyMember(candidate, member);
        }

        // Last resort: exactly one roster member is still unbound and exactly one unnamed player is known only from its
        // damage (class unknown or matching) → that is them.
        PartyMember? last = null;
        foreach (var m in unbound)
        {
            if (Entities.FindPlayerByName(m.Name) != null) continue;
            if (last != null) return;
            last = m;
        }
        if (last == null) return;
        var lastClass = MemberClass(last);
        PlayerEntity? only = null;
        foreach (var e in Entities.All)
        {
            if (e is not PlayerEntity { IsLocal: false, IsEnemy: false } pp || pp.Name.Length > 0) continue;
            if (only != null) return;
            only = pp;
        }
        if (only == null || (only.Class != CharacterClass.Unknown && lastClass != CharacterClass.Unknown && only.Class != lastClass)) return;
        Entities.SetName(only, last.Name);
        only.IsProvisional = false;
        ApplyMember(only, last);
    }

    private static CharacterClass MemberClass(PartyMember m) =>
        m.Class != CharacterClass.Unknown ? m.Class : ClassInfo.FromCode(m.ClassCode);

    private static void ApplyMember(PlayerEntity p, PartyMember m)
    {
        // A party member is never an enemy, even when an earlier hit (charm, boss mechanic) flagged them before the
        // roster arrived.
        p.IsEnemy = false;
        var cls = MemberClass(m);
        if (!p.ClassAuthoritative && cls != CharacterClass.Unknown)
        {
            p.Class = cls;
            p.ClassAuthoritative = true;
        }
        if (m.ServerId != 0) p.ServerId ??= m.ServerId;
        if (m.Level != 0) p.Level = m.Level;
        if (m.CharacterId != 0) p.CharacterId ??= m.CharacterId;
    }

    private void RefreshIdentity(CombatantState c)
    {
        if (c.Kind == CombatantKind.UnknownSummons) return;
        if (Entities.Get(c.Id) is not PlayerEntity p)
        {
            if (c.Name.Length == 0) c.Name = $"Player {c.Id}";
            return;
        }
        c.Name = p.DisplayName;
        c.Class = p.Class;
        c.IsLocal = p.IsLocal;
        c.IsEnemy = p.IsEnemy;
        c.IsPartyMember = Party.IsMember(p.Name);
        c.ServerId = p.ServerId ?? Party.Get(p.Name)?.ServerId;
    }

    public void RefreshIdentities(Encounter? enc)
    {
        if (enc is not { Finalized: false }) return;
        foreach (var c in enc.Combatants.Values) RefreshIdentity(c);
        foreach (var pv in enc.Pvp.Values)
            if (Entities.Get(pv.EnemyId) is PlayerEntity p) CopyIdentity(pv, p);
    }

    private static void CopyIdentity(PvpState pv, PlayerEntity p)
    {
        pv.Name = p.DisplayName;
        pv.Class = p.Class;
        pv.ServerId = p.ServerId;
        pv.GuildName = p.GuildName;
    }

    // ═════════════════════════════ world events ═════════════════════════════

    private void OnSpawn(SpawnEvent e)
    {
        NpcInfo? info = e.NpcCode != 0 ? GameData.GetNpc(e.NpcCode) : null;
        bool firstNpcBinding = Entities.Get(e.Entity) is NpcEntity { Inferred: true, IsSummon: false };
        if (Entities.Get(e.Entity) is PlayerEntity { IsProvisional: true, IsLocal: false } provisional)
        {
            DiscardPvpState(e.Entity);
            // An unseen summon owner may have been guessed to be a player. Its first NPC spawn corrects that
            // guess. Known players keep their historical damage when their id is later reused for an NPC.
            if (provisional.Name.Length == 0 && !provisional.ClassAuthoritative) DiscardOutgoingActorState(e.Entity);
        }
        var n = Entities.Spawn(e, info);
        if (firstNpcBinding && !n.IsSummon)
        {
            RefreshNpcBinding(Current, n);
            RefreshNpcBinding(_grace, n);
        }
        if (n is SummonEntity s) Summons.OnSpawn(s, e.Time);
        ApplyReattribution(Summons.Reattribute(e.Time, force: true));
    }

    /// <summary>A first authoritative spawn fills the identity of a target initially inferred from mid-stream hits.
    /// An already spawned entity is excluded by the caller because its id may have been reused.</summary>
    private static void RefreshNpcBinding(Encounter? enc, NpcEntity n)
    {
        if (enc is not { Finalized: false }) return;
        if (enc.Targets.TryGetValue(n.Id, out var target))
        {
            target.NpcCode = n.NpcCode != 0 ? n.NpcCode : null;
            target.MaxHp = n.MaxHp;
        }
        if (enc.Boss(n.Id) is not { } boss) return;
        boss.NpcCode = n.NpcCode != 0 ? n.NpcCode : null;
        if (boss.MaxHp != n.MaxHp) boss.HpCheck.Invalidate();
        boss.MaxHp = n.MaxHp;
        boss.MaxTrusted = n.MaxSource is MaxHpSource.Spawn or MaxHpSource.HpUpdate or MaxHpSource.StatMax
                          && (n.Hp is not long hp || n.MaxHp is not long max || hp >= 0.9 * max);
    }

    private void OnEntityStats(EntityStatsEvent e)
    {
        if (e.HasSelfStats && Entities.NoteSelfStats(e.Entity)) RefreshIdentities(Current);
        // Real traffic: 8-byte stat kind 7 is the NPC's max HP. Instanced bosses spawn with their base hp_max and are
        // then rescaled for the party size (120,000 → 240,000 → … → 600,000) before current HP is set to match.
        if (!e.HasSelfStats && e.Stats64.TryGetValue(MaxHpStatKind, out long maxHp) && maxHp > 0)
        {
            switch (Entities.Get(e.Entity))
            {
                case NpcEntity n: UpdateNpcMax(n, maxHp, MaxHpSource.StatMax); break;
                case null: Entities.StorePendingHp(e.Entity, null, maxHp); break;
            }
        }
        if (e.CurrentHp is long hp) OnHp(e.Entity, hp, null, e.Time);
    }

    private const byte MaxHpStatKind = 7;

    /// <summary>
    /// Max HP from the game (LIVE-FINDINGS NEW 2): stat kind 7 &gt; <c>1B 92</c> max &gt; spawn hp_max &gt; highest HP seen.
    /// A raise (party-size rescale) is neither damage nor a reset: the wipe reference restarts so the following
    /// "current HP = new max" reading is not mistaken for a boss returning to full HP, and the HP check drops the window.
    /// </summary>
    private void UpdateNpcMax(NpcEntity n, long max, MaxHpSource source)
    {
        if (n.IsSummon || max <= 0) return;
        if (source == MaxHpSource.HpUpdate && n.MaxSource == MaxHpSource.StatMax) return;
        if (n.MaxHp == max && n.MaxSource == source) return;
        bool raised = n.MaxHp is not long old || max > old;
        n.MaxHp = max;
        n.MaxSource = source;
        if (raised) n.MinFraction = 1.0;
        var enc = Current;
        if (enc is not { Finalized: false }) return;
        if (enc.Targets.TryGetValue(n.Id, out var ts)) ts.MaxHp = max;
        if (enc.Boss(n.Id) is { } b && !enc.Ended)
        {
            b.MaxHp = max;
            b.MaxTrusted = true;
            if (raised) b.HpCheck.Invalidate();
        }
    }

    private void OnHp(uint id, long hp, long? max, DateTime t)
    {
        if (hp < 0) return;
        switch (Entities.Get(id))
        {
            case null:
                Entities.StorePendingHp(id, hp, max);
                break;
            case PlayerEntity p:
                p.Hp = hp;
                if (max is > 0) p.MaxHp = max;
                if (hp > 0 && p.Dead)
                {
                    // Revived (real traffic: HP 0 → 6,543 with no respawn record). A later death is a new death.
                    p.Dead = false;
                    p.DeathTime = null;
                    if (Current is { Finalized: false } enc && enc.Combatants.TryGetValue(p.Id, out var pc)) pc.Dead = false;
                }
                break;
            case NpcEntity n:
                if (max is long mx and > 0) UpdateNpcMax(n, mx, MaxHpSource.HpUpdate);
                ApplyNpcHp(n, hp, t);
                break;
        }
    }

    public bool IsBossLike(NpcEntity n)
    {
        if (n.IsSummon) return false;
        if (n.Info is { IsBoss: true }) return true;
        long max = Math.Max(n.MaxHp ?? 0, n.HighestHp);
        return max >= Options.BossHpThreshold;
    }

    public static bool IsDummy(NpcEntity n) => !n.IsSummon && n.Info is { IsDummy: true };

    private static ulong BossKey(NpcEntity n) => n.NpcCode != 0 ? n.NpcCode : (1UL << 32) | n.Id;

    private void ApplyNpcHp(NpcEntity n, long hp, DateTime t)
    {
        long? prevHp = n.Hp;
        long healed = n.SelfHealSinceReading;
        n.SelfHealSinceReading = 0;
        n.Hp = hp;
        if (hp > n.HighestHp) n.HighestHp = hp;
        // A reading above a max HP that came from the game = the max was raised (party-size rescale, possibly before
        // the kind-7 record arrives). Never a wipe, never damage.
        bool rescaled = false;
        if (n.MaxSource is MaxHpSource.None or MaxHpSource.HighestSeen && n.HighestHp > 0)
        {
            if (n.MaxHp is long lower && hp > lower) n.MinFraction = 1.0;
            n.MaxHp = n.HighestHp;
            n.MaxSource = MaxHpSource.HighestSeen;
        }
        else if (n.MaxHp is long known && hp > known)
        {
            // Real traffic: party-scaled bosses spawn with their base hp_max (e.g. 120,000) while 00 8D reports the
            // scaled HP (600,000). The highest HP ever seen is a lower bound of the true maximum.
            n.MaxHp = n.HighestHp;
            n.MinFraction = 1.0;
            rescaled = true;
        }

        var enc = Current;
        if (enc is { Finalized: false } && enc.Targets.TryGetValue(n.Id, out var ts))
        {
            ts.LastHp = hp;
            ts.MaxHp = n.MaxHp;
        }

        // §11.3 wipe: back to ≥ 99.5 % after having been < 90 % (per boss).
        if (!rescaled && n.MaxHp is long max && max > 0 && IsBossLike(n) && !IsDummy(n))
        {
            double f = (double)hp / max;
            if (f >= 0.995 && n.MinFraction < 0.90 && !RiseIsNotAReset(n, prevHp, hp, healed, max, t))
            {
                n.MinFraction = f;
                n.Dead = false;
                n.DeathTime = null;
                OnWipe(n, hp, t);
                return;
            }
            if (f < n.MinFraction) n.MinFraction = f;
        }

        if (enc is { Finalized: false } && enc.Boss(n.Id) is { } b && (!enc.Ended || enc.Outcome == EncounterOutcome.Kill))
        {
            if (n.MaxHp is long m) b.MaxHp = m;
            if (rescaled) b.HpCheck.Invalidate();
            b.Timeline.Add(t, hp);
            b.HpCheck.OnReading(t, hp);
            b.HpEnd = hp;
        }

        if (hp == 0) MarkNpcDead(n, t);
    }

    /// <summary>Damage must have stopped this long before a boss whose max HP is only the highest HP seen counts as reset.</summary>
    private static readonly TimeSpan WipeQuietPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A boss back near full HP is not a reset when the rise is explained by its own recorded self-heals, or (max HP
    /// only known as the highest HP seen, e.g. meter started mid-fight) when players were still hitting it. The
    /// reference fraction restarts so a later reading near full is not mistaken for a reset either.
    /// </summary>
    private static bool RiseIsNotAReset(NpcEntity n, long? prevHp, long hp, long healed, long max, DateTime t)
    {
        bool explained = healed > 0 && prevHp is long ph && hp > ph && hp - ph <= healed + Math.Max(1000, max / 100);
        bool stillFought = n.MaxSource == MaxHpSource.HighestSeen && n.LastDamagedAt is { } ld && t - ld < WipeQuietPeriod;
        if (!explained && !stillFought) return false;
        n.MinFraction = (double)hp / max;
        return true;
    }

    private void OnWipe(NpcEntity n, long hp, DateTime t)
    {
        var key = BossKey(n);
        _resetCounts[key] = _resetCounts.GetValueOrDefault(key) + 1;
        if (Current is not { Ended: false } enc || enc.Boss(n.Id) is not { } b) return;
        // Per boss: this boss reset. The encounter wipes once none of its bosses is still being fought.
        b.Resets++;
        b.InReset = true;
        b.Killed = false;
        b.KillTime = null;
        b.HpCheck.Invalidate();
        b.Timeline.Add(t, hp);
        b.HpEnd = hp;
        if (enc.Targets.TryGetValue(n.Id, out var ts))
        {
            ts.Killed = false;
            ts.LastHp = hp;
        }
        foreach (var other in enc.Bosses)
            if (other.Active) return;
        End(enc, EncounterOutcome.Wipe, t);
    }

    private void MarkNpcDead(NpcEntity n, DateTime t)
    {
        if (n.Dead) return;
        n.Dead = true;
        n.DeathTime = t;
        var enc = Current;
        if (enc is not { Finalized: false }) return;
        if (enc.Targets.TryGetValue(n.Id, out var ts))
        {
            ts.Killed = true;
            ts.LastHp = 0;
            MarkKillingBlow(enc, n.Id);
        }
        if (enc.Boss(n.Id) is { Killed: false } b && (!enc.Ended || enc.Outcome == EncounterOutcome.Kill))
        {
            b.Killed = true;
            b.KillTime = t;
            b.InReset = false;
            b.HpEnd = 0;
        }
        if (!enc.Ended && enc.Kind is EncounterKind.Boss or EncounterKind.Dummy && enc.IsBoss(n.Id))
        {
            // Multi-boss: the encounter is a kill once every boss is dead (a boss sitting in a reset does not block it).
            bool anyKilled = false;
            foreach (var other in enc.Bosses)
            {
                if (other.Active) return;
                anyKilled |= other.Killed;
            }
            End(enc, anyKilled ? EncounterOutcome.Kill : EncounterOutcome.Wipe, t);
        }
    }

    private static void MarkKillingBlow(Encounter enc, uint target)
    {
        var hits = enc.Hits;
        for (int i = hits.Count - 1, n = 0; i >= 0 && n < 512; i--, n++)
        {
            var h = hits[i];
            if (h.Target == target && h.Kind is HitKind.Outgoing or HitKind.PvpOut && !h.IsDodge && h.Amount > 0)
            {
                h.Flags |= HitFlags.KillingBlow;
                return;
            }
        }
    }

    private void OnDeath(DeathEvent e)
    {
        // §8.9: flag 3 = died in combat. Flag 1 ("loaded already dead") is not trusted against live HP data.
        if (e.Flag != 3) return;
        switch (Entities.Get(e.Entity))
        {
            case NpcEntity n: MarkNpcDead(n, e.Time); break;
            case PlayerEntity p: OnPlayerDeath(p, e.Time, killedByLocal: false); break;
        }
    }

    private void OnKill(KillEvent e)
    {
        bool despawn = e.Killer == 0 && e.SkillRaw == 0;
        if (despawn) return;

        if (e.Killer != 0 && !string.IsNullOrEmpty(e.KillerName))
        {
            switch (Entities.Get(e.Killer))
            {
                case SummonEntity s:
                    if (s.ResolvedOwner is null) s.CasterName ??= e.KillerName;
                    break;
                case null:
                case PlayerEntity:
                    if (Entities.Get(e.Killer) is not PlayerEntity { IsLocal: true })
                        Entities.UpsertPlayer(e.Killer, e.KillerName, CharacterClass.Unknown,
                            e.KillerServerId != 0 ? e.KillerServerId : null, e.KillerGuildName);
                    break;
            }
            JoinRoster();
            PlayersChanged(e.Time);
        }

        switch (Entities.Get(e.Target))
        {
            case NpcEntity n:
                MarkNpcDead(n, e.Time);
                break;
            case PlayerEntity p:
                OnPlayerDeath(p, e.Time, IsLocalSide(e.Killer, e.KillerName, e.Time));
                break;
        }
    }

    private bool IsLocalSide(uint killer, string? killerName, DateTime t)
    {
        var local = Entities.LocalEntity;
        if (local == null) return false;
        if (killer != 0 && killer == local.Id) return true;
        if (!string.IsNullOrEmpty(killerName) && local.Name.Length > 0 && string.Equals(killerName, local.Name, StringComparison.Ordinal)) return true;
        return Entities.Get(killer) is SummonEntity s && Summons.ResolveRoot(s, t) is { Kind: RootKind.Player } r && r.Id == local.Id;
    }

    private void OnPlayerDeath(PlayerEntity p, DateTime t, bool killedByLocal)
    {
        var enc = Current;
        if (enc is { Finalized: false })
        {
            if (enc.Combatants.TryGetValue(p.Id, out var c) || p.IsLocal || Party.IsMember(p.Name))
            {
                c ??= enc.GetCombatant(p.Id);
                if (!c.Dead)
                {
                    c.Dead = true;
                    c.Deaths++;
                }
            }
            if (enc.Pvp.TryGetValue(p.Id, out var pv) && !pv.Killed)
            {
                bool recent = pv.LastLocalHit is { } lh && t - lh <= PvpDeathCredit;
                if (killedByLocal || recent)
                {
                    pv.Killed = true;
                    pv.LastActivity = t;
                    enc.PvpKills++;
                    MarkKillingBlow(enc, p.Id);
                    if (enc.Targets.TryGetValue(p.Id, out var ts)) ts.Killed = true;
                }
            }
        }
        p.Dead = true;
        p.DeathTime = t;
    }

    private void OnMapLoad(MapLoadEvent e)
    {
        if (MapId == e.MapId) return; // same map = in-map teleport
        MapId = e.MapId;
        EndActive(EncounterOutcome.ZoneChange, e.Time);
        ClearZoneState();
    }

    private void ClearZoneState()
    {
        _shieldedIncoming.Clear();
        Entities.ClearForZoneChange();
        Summons.Clear();
        Buffs.Clear();
        _resetCounts.Clear();
        SelectedTargetId = null;
    }

    private void OnBuffApplied(BuffAppliedEvent e)
    {
        var target = Entities.Get(e.Target);
        if (target is null or PlayerEntity { IsEnemy: false }) Buffs.OnApplied(e, ServerClockOffsetMs);
    }

    // ═════════════════════════════ classification ═════════════════════════════

    private void OnDamage(DamageEvent e)
    {
        var t = e.Time;
        uint skill = e.SkillId != 0 ? e.SkillId : SkillIds.Normalize(e.SkillRaw);
        if (e.IsCastNotice)
        {
            // Layout 0: no damage; "who cast what, when" for summon linking (§8.2).
            Summons.OnCast(e.Actor, skill, t, e.PowerScalar);
            ApplyReattribution(Summons.Reattribute(t, force: false));
            return;
        }
        if (SkillIds.GetKind(skill) == SkillKind.Link) return;
        long amount = e.Amount ?? 0;
        if (!e.EffectValidated && IsConfirmedNpcIncoming(e.Actor, e.Target)) _shieldedIncoming.OnCompanion(e);
        if (amount < 0 || amount > MaxAmount || (amount > 0 && !e.EffectValidated))
        {
            // A record whose effect id does not belong to its skill is misaligned (§8.2.3): its "amount" is some other
            // field (a power scalar, an absorb id...). Never count it.
            DroppedRecords++;
            return;
        }
        bool dodge = skill == SkillIds.Dodge;
        if (!dodge && amount == 0) return;

        // Self-targeted records (potions, self-heals) never re-classify their actor.
        var actor = ResolveActorEntity(e.Actor, skill, e.PowerScalar, t, allowCreate: !dodge, rebind: e.Actor != e.Target);
        if (actor == null) return;
        NoteActor(actor, skill, e.PowerScalar, t);
        var src = Attribute(actor, skill, t);

        if (e.Actor == e.Target)
        {
            if (dodge) return;
            if (actor is PlayerEntity && src.Side == Side.Friendly)
                RecordHeal(src, e.Actor, e.Target, skill, amount, t, e.HitTag, e.HitIndex, hot: false);
            else if (actor is NpcEntity { IsSummon: false })
                RecordTargetHeal(e.Target, e.Target, skill, amount, t);
            return;
        }

        bool healSkill = !dodge && GameData.IsHealSkill(skill);
        if (healSkill && Entities.Get(e.Target) == null)
        {
            // A heal on someone we have not seen yet is still a heal, never damage to an "unknown NPC".
            if (src.Side == Side.Friendly) RecordHeal(src, e.Actor, e.Target, skill, amount, t, e.HitTag, e.HitIndex, hot: false);
            return;
        }
        var target = ResolveTargetEntity(e.Target, src.Side);
        if (target == null) return;
        var tside = TargetSide(target, t);

        if (healSkill && src.Side == Side.Hostile && target is NpcEntity { IsSummon: false })
        {
            // A known NPC healing skill may heal another NPC (Naga Priest -> Lakshmi in Draupnir).
            RecordTargetHeal(e.Actor, e.Target, skill, amount, t);
            return;
        }

        if (healSkill && tside != Side.Hostile)
        {
            if (src.Side == Side.Friendly && tside == Side.Friendly)
                RecordHeal(src, e.Actor, e.Target, skill, amount, t, e.HitTag, e.HitIndex, hot: false);
            return;
        }

        bool measured = (e.Layout & 0x02) != 0;
        var flags = HitFlags.None;
        if (!dodge && e.IsCritical) flags |= HitFlags.Crit;
        if (measured && !dodge)
        {
            if (e.Direction == HitDirection.Back) flags |= HitFlags.Back;
            else if (e.Direction == HitDirection.Front) flags |= HitFlags.Front;
            if ((e.Mods & HitMods.Perfect) != 0) flags |= HitFlags.Perfect;
            if ((e.Mods & HitMods.Double) != 0) flags |= HitFlags.Double;
            if ((e.Mods & HitMods.Smite) != 0) flags |= HitFlags.Smite;
            if ((e.Mods & HitMods.Parry) != 0) flags |= HitFlags.Parry;
        }
        int extra = e.ExtraHits.Count;
        if (extra > 0 && !dodge) flags |= HitFlags.MultiHit;
        if (dodge) flags |= HitFlags.Dodged;
        if (src.ViaSummon) flags |= HitFlags.Summon;

        var hit = new Hit
        {
            Time = t,
            Actor = src.Credited,
            Source = e.Actor,
            Target = e.Target,
            Skill = skill,
            Amount = dodge ? 0 : amount,
            Flags = flags,
            HitTag = e.HitTag,
            HitIndex = e.HitIndex,
            ExtraHits = (byte)Math.Min(255, extra),
            QualityMeasured = measured && !dodge,
        };
        Route(hit, src, target, tside, qualifiesStart: !dodge && amount > 0);
    }

    private void OnDot(DotEvent e)
    {
        var t = e.Time;
        if (e.IsHealTick)
        {
            // 0x0B: the heal of this tick is the Heal field; Amount is what is still to come (§8.3).
            long heal = e.Heal ?? 0;
            if (heal <= 0 || heal > MaxDotAmount) return;
            uint hotSkill = e.SkillId ?? e.EffectId / 100;
            var healer = ResolveActorEntity(e.Actor, hotSkill, 0, t, allowCreate: true,
                rebind: e.SkillId != null && e.Actor != e.Target);
            if (healer == null) return;
            var hsrc = Attribute(healer, hotSkill, t);
            var tgt = Entities.Get(e.Target);
            if (hsrc.Side == Side.Hostile && tgt is NpcEntity { IsSummon: false })
            {
                RecordTargetHeal(e.Actor, e.Target, hotSkill, heal, t, hot: true);
                return;
            }
            if (hsrc.Side != Side.Friendly) return;
            if (tgt is NpcEntity { IsSummon: false }) return;
            RecordHeal(hsrc, e.Actor, e.Target, hotSkill, heal, t, 0, 0, hot: true);
            return;
        }
        if (!e.IsDamageTick) return; // 0x09 announcement, 0x08 status tick
        long amount = e.Amount ?? 0;
        if (amount <= 0 || amount > MaxDotAmount || e.Actor == e.Target) return;

        // Boss self-heal trap: 9-digit monster effect + player class skill = the target heals itself.
        if (e.IsMonsterEffect && e.SkillId is uint cls && SkillIds.GetKind(cls) == SkillKind.Player)
        {
            RecordTargetHeal(e.Target, e.Target, cls, amount, t);
            return;
        }

        uint skill = e.SkillId ?? e.EffectId / 100;
        if (SkillIds.GetKind(skill) == SkillKind.Link) return;
        var actor = ResolveActorEntity(e.Actor, skill, 0, t, allowCreate: true,
            rebind: e.SkillId != null && e.Actor != e.Target);
        if (actor == null) return;
        if (actor is PlayerEntity pa && pa.VoteClass(skill)) RefreshCombatantIfPresent(pa.Id);
        // A skill id derived from the effect id (no source skill on the tick) is not trusted for the summon rule.
        var src = Attribute(actor, skill, t, skillTrusted: e.SkillId != null);
        var target = ResolveTargetEntity(e.Target, src.Side);
        if (target == null) return;
        var tside = TargetSide(target, t);

        var hit = new Hit
        {
            Time = t,
            Actor = src.Credited,
            Source = e.Actor,
            Target = e.Target,
            Skill = skill,
            Amount = amount,
            Flags = HitFlags.Dot | (src.ViaSummon ? HitFlags.Summon : HitFlags.None),
        };
        Route(hit, src, target, tside, qualifiesStart: false);
        if (hit.Kind == HitKind.Incoming && hit.Flags.HasFlag(HitFlags.Incoming)
            && IsConfirmedNpcIncoming(e.Actor, e.Target) && Current is { } enc)
            _shieldedIncoming.OnTick(e, hit, enc);
    }

    private bool IsConfirmedNpcIncoming(uint actor, uint target) =>
        Entities.Get(actor) is NpcEntity { IsSummon: false, Inferred: false, NpcCode: not 0 }
        && Entities.Get(target) is PlayerEntity { IsEnemy: false };

    private void Route(Hit hit, Attribution src, Entity target, Side tside, bool qualifiesStart)
    {
        if (src.Side == Side.Friendly && tside == Side.Hostile)
        {
            RecordOutgoing(hit, src, target, qualifiesStart);
        }
        else if (src.Side == Side.Hostile && tside == Side.Friendly && target is PlayerEntity tp)
        {
            RecordIncoming(hit, tp);
        }
        else if (src.Side is Side.Friendly or Side.Enemy && src.Credited != CombatEngine.UnknownSummonsEntityId
                 && target is PlayerEntity victim && tside is Side.Friendly or Side.Enemy)
        {
            RecordPvp(hit, src, victim, qualifiesStart);
        }
    }

    private Entity? ResolveActorEntity(uint id, uint skill, uint scalar, DateTime t, bool allowCreate, bool rebind = false)
    {
        var e = Entities.Get(id);
        if (rebind && allowCreate && e != null) e = RebindIfMisclassified(e, skill);
        if (e != null || !allowCreate || id == 0) return e;
        switch (SkillIds.GetKind(skill))
        {
            case SkillKind.Player:
            {
                var cls = SkillIds.ClassOf(skill);
                if (cls == CharacterClass.Unknown) return null; // generic id: can't tell who acted
                // A skill entity whose spawn we missed carries its owner's power scalar (§10.3 #5).
                if (scalar != 0 && Summons.FindUniqueScalar(scalar, t, cls, id) is { } owner && !owner.IsProvisional)
                {
                    var s = Entities.CreateOrphanSummon(id, t, skill, scalar);
                    s.ResolvedOwner = owner.Id;
                    s.OwnerSource = OwnerSource.PowerScalar;
                    return s;
                }
                // Neither a known player nor a summon with an owner (real capture: #39063, 6,682 on Rotan). When it is
                // evidently a skill entity, it goes to the unknown-summons bucket (cast-variant / scalar / only-of-class
                // linking re-attributes it later); it never becomes a player row.
                if (LooksLikeSkillEntity(id, cls, scalar, t)) return Entities.CreateOrphanSummon(id, t, skill, scalar);
                return Entities.CreateProvisionalPlayer(id);
            }
            case SkillKind.Theostone:
                return Entities.CreateProvisionalPlayer(id);
            case SkillKind.Spirit:
                return Entities.CreateOrphanSummon(id, t, skill, scalar);
            case SkillKind.Npc:
                return Entities.CreateUnknownNpc(id);
            default:
                return null;
        }
    }

    /// <summary>
    /// Fixes a binding that was only guessed from a hit: an "unknown NPC" (never spawned) that acts with a class skill
    /// is a player whose <c>45 36</c> we missed; a nameless provisional player that acts with a monster skill is an NPC
    /// (e.g. a boss whose id was taken for a summon owner). Records built on the wrong guess are discarded.
    /// Returns the entity to use, or null when the caller should create a fresh binding.
    /// </summary>
    private Entity? RebindIfMisclassified(Entity e, uint skill)
    {
        var kind = SkillIds.GetKind(skill);
        switch (e)
        {
            case NpcEntity { Inferred: true, IsSummon: false, NpcCode: 0 } n
                when kind == SkillKind.Player && SkillIds.ClassOf(skill) != CharacterClass.Unknown
                     && !(Current is { Finalized: false } c && c.IsBoss(n.Id)):
                DiscardTargetState(n.Id);
                return null;
            case PlayerEntity { IsProvisional: true, IsLocal: false, Name.Length: 0, ClassAuthoritative: false } p when kind == SkillKind.Npc:
                DiscardPvpState(p.Id);
                return Entities.CreateUnknownNpc(p.Id);
            default:
                return e;
        }
    }

    /// <summary>Drops the outgoing hits recorded against <paramref name="id"/> in the live encounter (it was not an NPC).</summary>
    private void DiscardTargetState(uint id)
    {
        if (Current is not { Finalized: false } enc || !enc.Targets.Remove(id)) return;
        enc.Hits.RemoveAll(h => h.Target == id && h.Kind == HitKind.Outgoing);
        enc.RebuildLive();
    }

    /// <summary>Drops the PvP state built on <paramref name="id"/> (it was not a player).</summary>
    private void DiscardPvpState(uint id)
    {
        if (Current is not { Finalized: false } enc) return;
        bool changed = enc.Pvp.Remove(id);
        changed |= enc.Hits.RemoveAll(h => (h.Kind == HitKind.PvpOut && h.Target == id) || (h.Kind == HitKind.PvpIn && h.Actor == id)) > 0;
        if (enc.Targets.TryGetValue(id, out var ts) && ts.IsPlayer) changed |= enc.Targets.Remove(id);
        if (enc.Combatants.TryGetValue(id, out var c) && c.IsEnemy) changed |= enc.Combatants.Remove(id);
        if (changed) enc.RebuildLive();
    }

    private void DiscardOutgoingActorState(uint id)
    {
        DiscardOutgoingActorState(Current, id);
        DiscardOutgoingActorState(_grace, id);
    }

    private static void DiscardOutgoingActorState(Encounter? enc, uint id)
    {
        if (enc is not { Finalized: false }) return;
        var discarded = enc.Hits.Where(h => h.Kind == HitKind.Outgoing && h.Actor == id).ToHashSet();
        if (discarded.Count == 0) return;
        enc.Hits.RemoveAll(discarded.Contains);
        enc.RebuildLive();
        foreach (var boss in enc.Bosses) boss.HpCheck.RemoveDamage(discarded);
        if (enc.Combatants.TryGetValue(id, out var c) && c.All.Damage == 0 && c.DamageTaken == 0 && c.Healing == 0 && c.Deaths == 0)
            enc.Combatants.Remove(id);
    }

    /// <summary>
    /// An unknown actor using player skills is a skill entity (summon, spirit, totem…) rather than a player whose
    /// <c>45 36</c> we missed when (a) it carries the power scalar of a known player of the skill's class (§10.3 #5: summons inherit their
    /// owner's scalar, but the match was ambiguous or of another class), or (b) we are in an instance whose whole party
    /// roster is already bound to entities (nobody else can be there).
    /// </summary>
    private bool LooksLikeSkillEntity(uint id, CharacterClass cls, uint scalar, DateTime t)
    {
        if (scalar != 0 && Summons.AnyPlayerWithScalar(scalar, t, id, cls)) return true;
        if (MapId is not uint map || !GameData.IsInstanceMap(map) || !Party.HasRoster) return false;
        foreach (var m in Party.Members)
            if (Entities.FindPlayerByName(m.Name) == null) return false;
        return true;
    }

    private void NoteActor(Entity actor, uint skill, uint scalar, DateTime t)
    {
        switch (actor)
        {
            case PlayerEntity p:
                p.NoteScalar(scalar, t);
                if (p.VoteClass(skill))
                {
                    RefreshCombatantIfPresent(p.Id);
                    if (p.Name.Length == 0) JoinRoster();
                }
                break;
            case SummonEntity s:
                if (s.FirstSkill is null)
                {
                    s.FirstSkill = skill;
                    s.NextAttempt = default; // owner rules that depend on the skill (inline name, cast link) can run now
                }
                if (s.PowerScalar == 0 && scalar != 0) s.PowerScalar = scalar;
                break;
        }
    }

    private void RefreshCombatantIfPresent(uint id)
    {
        if (Current is { Finalized: false } enc && enc.Combatants.TryGetValue(id, out var c)) RefreshIdentity(c);
    }

    private Attribution Attribute(Entity actor, uint skill, DateTime t, bool skillTrusted = true)
    {
        switch (actor)
        {
            case PlayerEntity p:
                return new Attribution(p.IsEnemy ? Side.Enemy : Side.Friendly, p.Id, false, null);
            case SummonEntity s:
            {
                if (skillTrusted && SkillIds.GetKind(skill) == SkillKind.Npc)
                {
                    // A summon acting with a monster skill (boss mechanic entity, mind-controlled spirit) is hostile
                    // whatever its owner: its hits are damage taken, never a player's DPS or PvP.
                    var npcRoot = Summons.ResolveRoot(s, t);
                    return new Attribution(Side.Hostile, npcRoot.Kind == RootKind.Npc ? npcRoot.Id : s.Id, true, s);
                }
                bool hadPending = s.PendingHits is { Count: > 0 };
                var root = Summons.ResolveRoot(s, t);
                switch (root.Kind)
                {
                    case RootKind.Player:
                        if (hadPending) ApplyReattribution(Summons.Reattribute(t, force: false));
                        var owner = Entities.Get(root.Id) as PlayerEntity;
                        return new Attribution(owner is { IsEnemy: true } ? Side.Enemy : Side.Friendly, root.Id, true, s);
                    case RootKind.Npc:
                        return new Attribution(Side.Hostile, root.Id, true, s);
                    default:
                        return SkillIds.GetKind(skill) == SkillKind.Npc
                            ? new Attribution(Side.Hostile, s.Id, true, s)
                            : new Attribution(Side.Friendly, CombatEngine.UnknownSummonsEntityId, true, s);
                }
            }
            case NpcEntity n:
                return new Attribution(Side.Hostile, n.Id, false, null);
            default:
                return new Attribution(Side.None, 0, false, null);
        }
    }

    private Entity? ResolveTargetEntity(uint id, Side actorSide)
    {
        var e = Entities.Get(id);
        if (e != null || id == 0) return e;
        // A player hitting something we have never seen: mid-stream start, an NPC (§18 #3).
        return actorSide is Side.Friendly or Side.Enemy ? Entities.CreateUnknownNpc(id) : null;
    }

    private Side TargetSide(Entity target, DateTime t)
    {
        switch (target)
        {
            case PlayerEntity p:
                return p.IsEnemy ? Side.Enemy : Side.Friendly;
            case SummonEntity s:
                if (s.FirstSkill is uint fs && SkillIds.GetKind(fs) == SkillKind.Npc) return Side.Hostile;
                return Summons.ResolveRoot(s, t).Kind == RootKind.Npc ? Side.Hostile : Side.OwnedSummon;
            case NpcEntity:
                return Side.Hostile;
            default:
                return Side.None;
        }
    }

    // ═════════════════════════════ recording ═════════════════════════════

    /// <summary>
    /// True when hits of <paramref name="actorId"/> may start an encounter and keep it alive: the local player, a party
    /// member, the unknown-summons bucket, or anyone while the local player is unknown (mid-stream start) or inside an
    /// instance (only the party is there). Other players nearby (open world, towns) only add to targets already in scope,
    /// otherwise their damage would start trash encounters and keep them alive for as long as the local player stays.
    /// </summary>
    private bool IsCoreActor(uint actorId)
    {
        if (Entities.LocalId is not uint me || actorId == me || actorId == CombatEngine.UnknownSummonsEntityId) return true;
        if (MapId is uint map && GameData.IsInstanceMap(map)) return true;
        return Entities.Get(actorId) is PlayerEntity { Name.Length: > 0 } p && Party.IsMember(p.Name);
    }

    private static bool TrashTooLong(Encounter enc) =>
        enc.Kind == EncounterKind.Trash && (enc.Hits.Count > MaxTrashHits || enc.LastActivity - enc.CreatedUtc > MaxTrashDuration);

    private void RecordOutgoing(Hit hit, Attribution src, Entity target, bool qualifiesStart)
    {
        var tn = target as NpcEntity;
        bool bossLike = tn != null && IsBossLike(tn);
        bool dummy = tn != null && IsDummy(tn);
        var enc = Current;
        bool core = IsCoreActor(hit.Actor);
        // A bystander may still engage a real boss (world/field bosses): those encounters end with the boss.
        bool bystanderBoss = bossLike && !dummy && !TrainingArmed;

        // Killing blows that land just after the death still count (feature spec §1.1), also when a newer encounter
        // (an add hit right after the kill) has become current meanwhile.
        if (target.Dead && (InKillGrace(enc, hit.Time) ?? InKillGrace(_grace, hit.Time)) is { } graceEnc
            && graceEnc.Targets.ContainsKey(target.Id))
        {
            AddOutgoing(graceEnc, hit, src, target, bossLike, dummy, core);
            return;
        }

        bool deadTooLong = target.Dead && (target.DeathTime is not { } dt || hit.Time > dt + KillGrace);
        bool newBoss = qualifiesStart && bossLike && !dummy && tn != null && !target.Dead;
        if (enc is { Ended: true, Finalized: false, Outcome: EncounterOutcome.Kill, Kind: EncounterKind.Boss, FinalizeAt: { } grace }
            && newBoss && hit.Time <= grace && !enc.IsBoss(target.Id))
        {
            // Another boss engaged during the kill grace of the last one: both were pulled together (multi-boss fight).
            Reopen(enc);
            AddBoss(enc, tn!, hit.Time);
        }
        else if (enc is null or { Ended: true })
        {
            if (!qualifiesStart || target.Dead) return; // damage to a corpse never reopens
            if (!core && !bystanderBoss) return;
            enc = StartEncounter(hit.Time, tn, bossLike, dummy, pvp: false);
        }
        else
        {
            if (deadTooLong) return;
            if (TrashTooLong(enc))
            {
                End(enc, EncounterOutcome.Timeout, enc.LastActivity);
                if (!qualifiesStart || target.Dead || (!core && !bystanderBoss)) return;
                enc = StartEncounter(hit.Time, tn, bossLike, dummy, pvp: false);
            }
            else if (!core && enc.Kind != EncounterKind.Boss && !enc.Targets.ContainsKey(target.Id) && !bystanderBoss)
            {
                return; // a bystander's own fight: not part of ours
            }
            else if (qualifiesStart && (bossLike || dummy) && (core || bystanderBoss) && !target.Dead
                     && enc.Kind is EncounterKind.Trash or EncounterKind.Pvp)
            {
                // §12 boss mode: a boss engages → drop the trash segment.
                if (enc.Kind == EncounterKind.Trash) Drop(enc);
                else End(enc, EncounterOutcome.Timeout, enc.EndUtc);
                enc = StartEncounter(hit.Time, tn, bossLike, dummy, pvp: false);
            }
            else if (newBoss && enc.Kind == EncounterKind.Boss && !enc.IsBoss(target.Id) && !JoinsBossFight(enc, hit.Time))
            {
                // Sequential bosses: the previous boss was left alone (no hit within the join window) → one encounter
                // per boss, so per-boss history and trends stay clean.
                End(enc, EncounterOutcome.Timeout, enc.EndUtc);
                enc = StartEncounter(hit.Time, tn, bossLike, dummy, pvp: false);
            }
        }
        AddOutgoing(enc, hit, src, target, bossLike, dummy, core);
    }

    private static Encounter? InKillGrace(Encounter? enc, DateTime t) =>
        enc is { Ended: true, Finalized: false, FinalizeAt: { } fa } && t <= fa ? enc : null;

    private void FinalizeGrace()
    {
        var g = _grace;
        _grace = null;
        if (g is { Finalized: false }) Finalize(g);
    }

    /// <summary>True when a boss of <paramref name="enc"/> is still being fought (hit within the join window).</summary>
    private static bool JoinsBossFight(Encounter enc, DateTime t)
    {
        foreach (var b in enc.Bosses)
        {
            if (!b.Active || b.LastHit is not { } last) continue;
            if (t - last <= MultiBossJoinWindow) return true;
        }
        return false;
    }

    private static void Reopen(Encounter enc)
    {
        enc.Ended = false;
        enc.Outcome = EncounterOutcome.InProgress;
        enc.EndedAt = null;
        enc.FinalizeAt = null;
    }

    private void AddOutgoing(Encounter enc, Hit hit, Attribution src, Entity target, bool bossLike, bool dummy, bool core)
    {
        bool inScope;
        if (enc.Kind is EncounterKind.Boss or EncounterKind.Dummy)
        {
            if ((bossLike || dummy) && target is NpcEntity pn && !enc.IsBoss(target.Id)
                && (enc.Bosses.Count == 0 || enc.Kind == EncounterKind.Dummy || JoinsBossFight(enc, hit.Time)))
                AddBoss(enc, pn, hit.Time);
            inScope = enc.IsBoss(target.Id)
                      || (Options.CountAddsInBossFight && !AllBossesDeadPastGrace(enc, hit.Time));
        }
        else
        {
            inScope = true;
        }

        var boss = enc.Boss(target.Id);
        hit.Kind = HitKind.Outgoing;
        hit.InScope = inScope;
        hit.ToBoss = boss != null;
        EnsureTarget(enc, target, bossLike && boss != null, dummy);
        var c = enc.GetCombatant(hit.Actor);
        NoteDirectAction(c, hit.Source, hit.Time, hit.IsDot);
        enc.AddHit(hit);
        if (!hit.IsDodge && hit.Amount > 0)
        {
            if (target is NpcEntity damaged && (damaged.LastDamagedAt is not { } ld || hit.Time > ld)) damaged.LastDamagedAt = hit.Time;
            // Only our side keeps an encounter alive (a boss fight ends with the boss anyway).
            if ((core || enc.Kind == EncounterKind.Boss) && hit.Time > enc.LastActivity) enc.LastActivity = hit.Time;
            if (boss != null)
            {
                boss.HpCheck.OnDamage(hit);
                boss.InReset = false;
                if (boss.FirstHit is null || hit.Time < boss.FirstHit) boss.FirstHit = hit.Time;
                if (boss.LastHit is null || hit.Time > boss.LastHit) boss.LastHit = hit.Time;
                if (Entities.LocalId is uint me && hit.Actor == me) boss.LastLocalHit = hit.Time;
            }
        }
        if (src.ViaSummon && src.Summon != null && hit.Actor == CombatEngine.UnknownSummonsEntityId)
            Summons.AddPending(src.Summon, hit, enc);
    }

    private static bool AllBossesDeadPastGrace(Encounter enc, DateTime t)
    {
        if (enc.Bosses.Count == 0) return false;
        foreach (var b in enc.Bosses)
            if (!b.Killed || b.KillTime is not { } kt || t <= kt + KillGrace) return false;
        return true;
    }

    private void EnsureTarget(Encounter enc, Entity target, bool isBoss, bool isDummy)
    {
        if (!enc.Targets.TryGetValue(target.Id, out var ts))
        {
            ts = new TargetState(target.Id);
            enc.Targets[target.Id] = ts;
        }
        switch (target)
        {
            case NpcEntity n:
                ts.NpcCode = n.NpcCode != 0 ? n.NpcCode : null;
                ts.IsBoss |= isBoss;
                ts.IsDummy |= isDummy;
                break;
            case PlayerEntity p:
                ts.IsPlayer = true;
                ts.PlayerName = p.DisplayName;
                break;
        }
        ts.LastHp = target.Hp;
        ts.MaxHp = target.MaxHp;
        // Only a death inside this encounter (or its kill grace) marks the target killed; a player killed in an
        // earlier fight is not shown dead in every later one.
        if (target.Dead && (target is NpcEntity || target.DeathTime is not { } dt || dt >= enc.CreatedUtc)) ts.Killed = true;
    }

    private void RecordIncoming(Hit hit, PlayerEntity target)
    {
        var enc = Current;
        if (enc is not { Ended: false }) return;
        if (!(target.IsLocal || Party.IsMember(target.Name) || enc.Combatants.ContainsKey(target.Id))) return;
        hit.Kind = HitKind.Incoming;
        hit.Flags |= HitFlags.Incoming;
        if (Entities.Get(hit.Source) is NpcEntity { NpcCode: not 0 } src) enc.SourceNpcCodes[hit.Source] = src.NpcCode;
        if (hit.Source != hit.Actor && Entities.Get(hit.Actor) is NpcEntity { NpcCode: not 0 } root) enc.SourceNpcCodes[hit.Actor] = root.NpcCode;
        enc.GetCombatant(target.Id);
        enc.AddHit(hit);
    }

    private void RecordHeal(Attribution src, uint sourceId, uint targetId, uint skill, long amount, DateTime t, byte hitTag, uint hitIndex, bool hot)
    {
        var enc = Current;
        if (enc is not { Ended: false }) return;
        if (src.Credited == CombatEngine.UnknownSummonsEntityId) return;
        if (amount <= 0 || amount > MaxAmount) return;
        var healer = Entities.Get(src.Credited) as PlayerEntity;
        bool relevant = healer is { IsLocal: true } || (healer != null && Party.IsMember(healer.Name))
                        || enc.Combatants.ContainsKey(src.Credited) || enc.Combatants.ContainsKey(targetId);
        if (!relevant) return;
        var hit = new Hit
        {
            Time = t,
            Actor = src.Credited,
            Source = sourceId,
            Target = targetId,
            Skill = skill,
            Amount = amount,
            Flags = HitFlags.Heal | (hot ? HitFlags.Dot : HitFlags.None) | (src.ViaSummon ? HitFlags.Summon : HitFlags.None),
            Kind = HitKind.Heal,
            HitTag = hitTag,
            HitIndex = hitIndex,
        };
        var c = enc.GetCombatant(src.Credited);
        NoteDirectAction(c, sourceId, t, hot);
        enc.AddHit(hit);
    }

    private void RecordTargetHeal(uint sourceId, uint targetId, uint skill, long amount, DateTime t, bool hot = false)
    {
        var healedNpc = Entities.Get(targetId) as NpcEntity;
        if (healedNpc is { IsSummon: false }) healedNpc.SelfHealSinceReading += amount;
        var enc = Current;
        if (enc is not { Finalized: false }) return;
        if (enc.Ended && enc.Outcome != EncounterOutcome.Kill) return;
        if (!enc.Targets.ContainsKey(targetId) && !enc.IsBoss(targetId)) return;
        enc.AddHit(new Hit
        {
            Time = t,
            Actor = targetId,
            Source = sourceId,
            Target = targetId,
            Skill = skill,
            Amount = amount,
            Flags = HitFlags.Heal | (hot ? HitFlags.Dot : HitFlags.None),
            Kind = HitKind.TargetSelfHeal,
        });
        long? trustedMax = healedNpc is { MaxSource: MaxHpSource.Spawn or MaxHpSource.HpUpdate or MaxHpSource.StatMax }
            ? healedNpc.MaxHp : null;
        enc.Boss(targetId)?.HpCheck.OnSelfHeal(amount, trustedMax);
    }

    private void RecordPvp(Hit hit, Attribution src, PlayerEntity victim, bool qualifiesStart)
    {
        var local = Entities.LocalEntity;
        if (local == null) return;
        uint a = hit.Actor, b = victim.Id;
        bool playerSkill = IsPlayerAttackSkill(hit.Skill);
        if (a == b)
        {
            // A monster-skill hit credited to its own victim is damage taken, never discarded.
            if (!playerSkill) RecordIncoming(hit, victim);
            return;
        }
        bool outgoing = a == local.Id;
        bool incoming = b == local.Id;
        var enemy = outgoing ? victim : Entities.Get(a) as PlayerEntity;
        if (enemy == null || !(outgoing || incoming) || enemy.IsLocal || Party.IsMember(enemy.Name)
            || !(enemy.IsEnemy || CanTurnEnemy(hit, src, enemy, local)))
        {
            // Not PvP. Friendly fire from a mechanic (charm, bomb credited to a player) is still damage taken.
            if (!outgoing && !playerSkill) RecordIncoming(hit, victim);
            return;
        }

        if (!enemy.IsEnemy)
        {
            enemy.IsEnemy = true;
            if (Current is { Finalized: false } cur && cur.Combatants.TryGetValue(enemy.Id, out var ec)) ec.IsEnemy = true;
        }

        var enc = Current;
        if (enc is null or { Ended: true })
        {
            if (!qualifiesStart) return;
            enc = StartEncounter(hit.Time, null, false, false, pvp: true);
        }

        if (!enc.Pvp.TryGetValue(enemy.Id, out var pv))
        {
            pv = new PvpState(enemy.Id);
            enc.Pvp[enemy.Id] = pv;
        }
        CopyIdentity(pv, enemy);
        pv.LastActivity = hit.Time;

        hit.Kind = outgoing ? HitKind.PvpOut : HitKind.PvpIn;
        hit.InScope = outgoing && enc.Kind == EncounterKind.Pvp;
        if (outgoing)
        {
            if (!hit.IsDodge)
            {
                pv.DamageDealt += hit.Amount;
                pv.LastLocalHit = hit.Time;
            }
            EnsureTarget(enc, enemy, false, false);
        }
        else
        {
            hit.Flags |= HitFlags.Incoming;
            if (!hit.IsDodge) pv.DamageTaken += hit.Amount;
        }
        var lc = enc.GetCombatant(local.Id);
        if (outgoing) NoteDirectAction(lc, hit.Source, hit.Time, hit.IsDot);
        enc.AddHit(hit);
        if (!hit.IsDodge && hit.Amount > 0 && hit.Time > enc.LastActivity) enc.LastActivity = hit.Time;
    }

    private static bool IsPlayerAttackSkill(uint skill) =>
        SkillIds.GetKind(skill) is SkillKind.Player or SkillKind.Theostone or SkillKind.Spirit;

    /// <summary>Lingering DoTs, HoTs and pets can act after their owner died. Only the player's own direct action
    /// establishes a revive when no positive HP reading arrived.</summary>
    private void NoteDirectAction(CombatantState c, uint sourceId, DateTime t, bool tick)
    {
        if (tick || sourceId != c.Id) return;
        if (Entities.Get(c.Id) is PlayerEntity p)
        {
            if (p.DeathTime is { } death && t <= death) return;
            p.Dead = false;
            p.DeathTime = null;
        }
        c.Dead = false;
    }

    /// <summary>
    /// Whether a hit between the local player and another (non-party) player makes that player a PvP enemy. Only a
    /// player's own attack can (never a monster skill, never a summon owned only by its inline name, which boss
    /// entities carry for their target). A different faction (server race digit) always can; otherwise a player who
    /// already fought this encounter's NPCs with us is an ally hit by a charm / mind-control mechanic.
    /// </summary>
    private bool CanTurnEnemy(Hit hit, Attribution src, PlayerEntity enemy, PlayerEntity local)
    {
        if (!IsPlayerAttackSkill(hit.Skill)) return false;
        if (src.Summon is { OwnerSource: OwnerSource.CasterName }) return false;
        if (enemy.ServerId is ushort es and >= 1000 && local.ServerId is ushort ls and >= 1000 && es / 1000 != ls / 1000) return true;
        if (Current is { Finalized: false } cur && cur.Combatants.TryGetValue(enemy.Id, out var c) && c.All.Damage > 0) return false;
        return true;
    }

    private void ApplyReattribution(Dictionary<Encounter, HashSet<uint>>? changed)
    {
        if (changed == null) return;
        foreach (var (enc, actors) in changed)
        {
            // Only the old and new actors of the moved hits change: no full replay of a long encounter's hit list.
            enc.RebuildLive(actors);
            RefreshIdentities(enc);
        }
    }

    // ═════════════════════════════ lifecycle ═════════════════════════════

    private Encounter StartEncounter(DateTime t, NpcEntity? target, bool bossLike, bool dummy, bool pvp)
    {
        if (Current is { Ended: true, Finalized: false } pending)
        {
            // A kill still in its grace stays open for late killing blows instead of being cut short.
            if (pending.FinalizeAt is { } pf && t <= pf)
            {
                FinalizeGrace();
                _grace = pending;
            }
            else
            {
                Finalize(pending);
            }
        }

        EncounterKind kind = pvp ? EncounterKind.Pvp
            : TrainingArmed ? EncounterKind.Training
            : dummy ? EncounterKind.Dummy
            : bossLike ? EncounterKind.Boss
            : EncounterKind.Trash;
        var enc = new Encounter(t, kind, RefreshIdentity) { MapId = MapId };
        if (kind == EncounterKind.Training)
        {
            TrainingArmed = false;
            enc.TrainingDuration = TrainingDuration;
            enc.TrainingEnd = t + TrainingDuration;
            enc.Note = $"Training run {TrainingDuration.TotalSeconds:0} s";
        }
        if (kind is EncounterKind.Boss or EncounterKind.Dummy && target != null) AddBoss(enc, target, t);
        Current = enc;
        SelectedTargetId = null;
        TransientStatus = null;
        if (Entities.LocalEntity is { } local) enc.GetCombatant(local.Id);
        return enc;
    }

    private BossState AddBoss(Encounter enc, NpcEntity n, DateTime t)
    {
        var b = new BossState(n.Id, enc.Bosses.Count, t)
        {
            NpcCode = n.NpcCode != 0 ? n.NpcCode : null,
            MaxHp = n.MaxHp,
            HpStart = n.Hp,
            HpEnd = n.Hp,
            MaxTrusted = n.MaxSource is MaxHpSource.Spawn or MaxHpSource.HpUpdate or MaxHpSource.StatMax
                         && (n.Hp is not long hp || n.MaxHp is not long max || hp >= 0.9 * max),
            ResetsBefore = _resetCounts.GetValueOrDefault(BossKey(n)),
        };
        enc.Bosses.Add(b);
        enc.BossById[n.Id] = b;
        if (n.Hp is long cur)
        {
            b.Timeline.Add(t, cur);
            b.HpCheck.OnReading(t - TimeSpan.FromTicks(1), cur);
        }
        return b;
    }

    private void Drop(Encounter enc)
    {
        enc.Ended = true;
        enc.Finalized = true;
        enc.Outcome = EncounterOutcome.ManualReset;
        if (ReferenceEquals(Current, enc)) Current = null;
    }

    private void End(Encounter enc, EncounterOutcome outcome, DateTime t)
    {
        if (enc.Ended) return;
        enc.Ended = true;
        enc.Outcome = outcome;
        enc.EndedAt = t;
        if (enc.Kind == EncounterKind.Training) Mode = MeterMode.BossOnly; // ending training restores Boss-only
        if (outcome == EncounterOutcome.Kill) enc.FinalizeAt = t + KillGrace;
        else Finalize(enc);
    }

    private void EndActive(EncounterOutcome outcome, DateTime t)
    {
        FinalizeGrace();
        var enc = Current;
        if (enc == null) return;
        if (!enc.Ended) End(enc, outcome, t);
        if (!enc.Finalized) Finalize(enc);
    }

    private void Finalize(Encounter enc)
    {
        if (enc.Finalized) return;
        RefreshIdentities(enc);
        enc.Finalized = true;
        if (Qualifies(enc)) _completed.Add(EncounterRecordBuilder.Build(this, enc));
        else if (enc.Kind == EncounterKind.Training) TransientStatus = "Training run did no damage";
    }

    public bool Qualifies(Encounter enc)
    {
        long total = 0;
        foreach (var c in enc.Combatants.Values)
            if (!c.IsEnemy) total += c.Scoped.Damage;
        if (enc.Kind == EncounterKind.Pvp)
            foreach (var pv in enc.Pvp.Values) total += pv.DamageTaken;
        if (total <= 0) return false;
        double d = enc.DurationSeconds;
        return enc.Kind switch
        {
            EncounterKind.Training => true,
            EncounterKind.Boss or EncounterKind.Dummy or EncounterKind.Pvp => d >= Options.MinBossFightSeconds,
            EncounterKind.Trash => Options.SaveTrashFights && d >= Options.MinTrashFightSeconds,
            _ => false,
        };
    }

    // ═════════════════════════════ UI commands ═════════════════════════════

    public void Reset()
    {
        _shieldedIncoming.Clear();
        FinalizeGrace();
        var t = LastEventTime ?? Current?.LastActivity ?? DateTime.UtcNow;
        if (Current is { } enc)
        {
            if (!enc.Ended) End(enc, EncounterOutcome.ManualReset, enc.Kind == EncounterKind.Training ? t : enc.EndUtc);
            if (!enc.Finalized) Finalize(enc);
        }
        Current = null;
        SelectedTargetId = null;
        TrainingArmed = false;
        TransientStatus = null;
    }

    public void StartTraining(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1)) duration = TimeSpan.FromSeconds(1);
        Reset();
        TrainingArmed = true;
        TrainingDuration = duration;
        Mode = MeterMode.AllTargets; // everything hit counts
    }

    public void ClearSession()
    {
        _shieldedIncoming.Clear();
        FinalizeGrace();
        if (Current is { } enc)
        {
            if (!enc.Ended) End(enc, EncounterOutcome.ManualReset, enc.EndUtc);
            if (!enc.Finalized) Finalize(enc);
        }
        Current = null;
        Entities.ClearAll();
        Party.Clear();
        Summons.Clear();
        Buffs.Clear();
        Ping.Reset();
        _resetCounts.Clear();
        MapId = null;
        ServerClockOffsetMs = null;
        SelectedTargetId = null;
        TrainingArmed = false;
        TransientStatus = null;
        SawTraffic = false;
        LastEventTime = null;
    }

    public void NotifyCaptureGap()
    {
        if (Current is { Finalized: false } enc) enc.CaptureGaps = true;
    }

    /// <summary>Targets of an encounter in display order: the encounter's bosses first (primary first, then in engagement
    /// order), then other bosses/dummies, then by damage taken. At most 50.</summary>
    public List<TargetState> OrderedTargetsAll(Encounter enc) => OrderedTargets(enc, int.MaxValue);

    public List<TargetState> OrderedTargets(Encounter enc, int max = 50)
    {
        var list = new List<TargetState>(enc.Targets.Values);
        uint? primary = enc.PrimaryBossId;
        int Rank(TargetState t) => t.Id == primary ? 0 : enc.IsBoss(t.Id) ? 1 : t.IsBoss || t.IsDummy ? 2 : 3;
        list.Sort((a, b) =>
        {
            int pa = Rank(a), pb = Rank(b);
            if (pa != pb) return pa.CompareTo(pb);
            if (pa == 1) return enc.BossById[a.Id].Order.CompareTo(enc.BossById[b.Id].Order);
            int c = b.DamageTaken.CompareTo(a.DamageTaken);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    /// <summary>
    /// The target the overlay shows when the user did not cycle: in a multi-boss fight the boss the local player hit
    /// most recently, else the primary boss, else the first target.
    /// </summary>
    public uint? DefaultTargetId(Encounter enc)
    {
        BossState? recent = null;
        foreach (var b in enc.Bosses)
            if (b.LastLocalHit is { } t && enc.Targets.ContainsKey(b.Id) && (recent?.LastLocalHit is not { } r || t > r)) recent = b;
        if (recent != null) return recent.Id;
        if (enc.PrimaryBossId is uint p && enc.Targets.ContainsKey(p)) return p;
        return null;
    }

    public void CycleTarget(int direction)
    {
        if (Current is not { } enc || direction == 0) return;
        var list = OrderedTargets(enc);
        if (list.Count == 0) return;
        uint? current = SelectedTargetId ?? DefaultTargetId(enc);
        int idx = current is uint sel ? list.FindIndex(x => x.Id == sel) : 0;
        if (idx < 0) idx = 0;
        int step = Math.Sign(direction);
        idx = ((idx + step) % list.Count + list.Count) % list.Count;
        SelectedTargetId = list[idx].Id;
    }
}
