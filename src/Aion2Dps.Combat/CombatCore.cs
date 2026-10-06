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
    private static readonly TimeSpan PvpDeathCredit = TimeSpan.FromSeconds(10);

    private readonly Dictionary<ulong, int> _resetCounts = new();
    private readonly List<EncounterRecord> _completed = new();

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
            }
        }
        if (enc.Ended && !enc.Finalized && enc.FinalizeAt is { } fa && now > fa) Finalize(enc);
    }

    // ═════════════════════════════ identity events ═════════════════════════════

    private void OnSelfInfo(SelfInfoEvent e)
    {
        var (p, nameChanged, prevId) = Entities.SetLocal(e);
        if (nameChanged)
        {
            // §12: a 33 36 with a different name = another character → new session context.
            EndActive(EncounterOutcome.ZoneChange, e.Time);
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
    }

    private static CharacterClass MemberClass(PartyMember m) =>
        m.Class != CharacterClass.Unknown ? m.Class : ClassInfo.FromCode(m.ClassCode);

    private static void ApplyMember(PlayerEntity p, PartyMember m)
    {
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
        var n = Entities.Spawn(e, info);
        if (n is SummonEntity s) Summons.OnSpawn(s, e.Time);
    }

    private void OnEntityStats(EntityStatsEvent e)
    {
        if (e.HasSelfStats && Entities.NoteSelfStats(e.Entity)) RefreshIdentities(Current);
        // Real traffic: 8-byte stat kind 7 is the NPC's max HP. Instanced bosses spawn with their base hp_max and are
        // then rescaled for the party size (120,000 → 240,000 → … → 600,000) before current HP is set to match.
        if (!e.HasSelfStats && e.Stats64.TryGetValue(MaxHpStatKind, out long maxHp) && maxHp > 0) OnNpcMaxHp(e.Entity, maxHp);
        if (e.CurrentHp is long hp) OnHp(e.Entity, hp, null, e.Time);
    }

    private const byte MaxHpStatKind = 7;

    private void OnNpcMaxHp(uint id, long max)
    {
        if (Entities.Get(id) is not NpcEntity n || n.IsSummon) return;
        if (n.MaxHp == max && n.MaxSource == MaxHpSource.HpUpdate) return;
        bool raised = n.MaxHp is not long old || max > old;
        n.MaxHp = max;
        n.MaxSource = MaxHpSource.HpUpdate;
        // A rescale is neither damage nor a reset: restart the wipe reference so the following
        // "current HP = new max" reading is not mistaken for a boss returning to full HP.
        if (raised) n.MinFraction = 1.0;
        var enc = Current;
        if (enc is { Finalized: false } && enc.Targets.TryGetValue(n.Id, out var ts)) ts.MaxHp = max;
        if (enc is { Finalized: false, Ended: false } && enc.PrimaryBossId == n.Id)
        {
            enc.BossMaxHp = max;
            enc.BossMaxTrusted = true;
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
                break;
            case NpcEntity n:
                if (max is > 0 && n.MaxSource != MaxHpSource.Spawn)
                {
                    n.MaxHp = max;
                    n.MaxSource = MaxHpSource.HpUpdate;
                }
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
        n.Hp = hp;
        if (hp > n.HighestHp) n.HighestHp = hp;
        if (n.MaxSource is MaxHpSource.None or MaxHpSource.HighestSeen && n.HighestHp > 0)
        {
            n.MaxHp = n.HighestHp;
            n.MaxSource = MaxHpSource.HighestSeen;
        }
        else if (n.MaxHp is long known && n.HighestHp > known)
        {
            // Real traffic: party-scaled bosses spawn with their base hp_max (e.g. 120,000) while 00 8D reports the
            // scaled HP (600,000). The highest HP ever seen is a lower bound of the true maximum.
            n.MaxHp = n.HighestHp;
        }

        var enc = Current;
        if (enc is { Finalized: false } && enc.Targets.TryGetValue(n.Id, out var ts))
        {
            ts.LastHp = hp;
            ts.MaxHp = n.MaxHp;
        }

        // §11.3 wipe: back to ≥ 99.5 % after having been < 90 %.
        if (n.MaxHp is long max && max > 0 && IsBossLike(n) && !IsDummy(n))
        {
            double f = (double)hp / max;
            if (f >= 0.995 && n.MinFraction < 0.90)
            {
                n.MinFraction = f;
                n.Dead = false;
                n.DeathTime = null;
                OnWipe(n, t);
                return;
            }
            if (f < n.MinFraction) n.MinFraction = f;
        }

        if (enc is { Finalized: false } && enc.PrimaryBossId == n.Id && (!enc.Ended || enc.Outcome == EncounterOutcome.Kill))
        {
            if (n.MaxHp is long m && (enc.BossMaxHp is null || enc.BossMaxHp < m || !enc.BossMaxTrusted)) enc.BossMaxHp = m;
            enc.Timeline.Add(t, hp);
            enc.HpCheck.OnReading(t, hp);
            enc.BossHpEnd = hp;
        }

        if (hp == 0) MarkNpcDead(n, t);
    }

    private void OnWipe(NpcEntity n, DateTime t)
    {
        var key = BossKey(n);
        _resetCounts[key] = _resetCounts.GetValueOrDefault(key) + 1;
        if (Current is { Ended: false } enc && enc.PrimaryBossId == n.Id) End(enc, EncounterOutcome.Wipe, t);
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
        if (!enc.Ended && enc.Kind is EncounterKind.Boss or EncounterKind.Dummy && enc.BossTargets.Contains(n.Id))
        {
            foreach (var id in enc.BossTargets)
                if (Entities.Get(id) is { Dead: false }) return;
            End(enc, EncounterOutcome.Kill, t);
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
        if (amount < 0 || amount > MaxAmount)
        {
            DroppedRecords++;
            return;
        }
        bool dodge = skill == SkillIds.Dodge;
        if (!dodge && amount == 0) return;

        var actor = ResolveActorEntity(e.Actor, skill, e.PowerScalar, t, allowCreate: !dodge);
        if (actor == null) return;
        NoteActor(actor, skill, e.PowerScalar, t);
        var src = Attribute(actor, skill, t);

        if (e.Actor == e.Target)
        {
            if (dodge) return;
            if (actor is PlayerEntity && src.Side == Side.Friendly)
                RecordHeal(src, e.Actor, e.Target, skill, amount, t, e.HitTag, e.HitIndex, hot: false);
            else if (actor is NpcEntity { IsSummon: false })
                RecordTargetSelfHeal(e.Target, skill, amount, t);
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
            var healer = ResolveActorEntity(e.Actor, hotSkill, 0, t, allowCreate: true);
            if (healer == null) return;
            var hsrc = Attribute(healer, hotSkill, t);
            if (hsrc.Side != Side.Friendly) return;
            var tgt = Entities.Get(e.Target);
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
            RecordTargetSelfHeal(e.Target, cls, amount, t);
            return;
        }

        uint skill = e.SkillId ?? e.EffectId / 100;
        if (SkillIds.GetKind(skill) == SkillKind.Link) return;
        var actor = ResolveActorEntity(e.Actor, skill, 0, t, allowCreate: true);
        if (actor == null) return;
        if (actor is PlayerEntity pa && pa.VoteClass(skill)) RefreshCombatantIfPresent(pa.Id);
        var src = Attribute(actor, skill, t);
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
    }

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
            RecordPvp(hit, victim, qualifiesStart);
        }
    }

    private Entity? ResolveActorEntity(uint id, uint skill, uint scalar, DateTime t, bool allowCreate)
    {
        var e = Entities.Get(id);
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
                s.FirstSkill ??= skill;
                if (s.PowerScalar == 0 && scalar != 0) s.PowerScalar = scalar;
                break;
        }
    }

    private void RefreshCombatantIfPresent(uint id)
    {
        if (Current is { Finalized: false } enc && enc.Combatants.TryGetValue(id, out var c)) RefreshIdentity(c);
    }

    private Attribution Attribute(Entity actor, uint skill, DateTime t)
    {
        switch (actor)
        {
            case PlayerEntity p:
                return new Attribution(p.IsEnemy ? Side.Enemy : Side.Friendly, p.Id, false, null);
            case SummonEntity s:
            {
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
                return Summons.ResolveRoot(s, t).Kind == RootKind.Npc ? Side.Hostile : Side.OwnedSummon;
            case NpcEntity:
                return Side.Hostile;
            default:
                return Side.None;
        }
    }

    // ═════════════════════════════ recording ═════════════════════════════

    private void RecordOutgoing(Hit hit, Attribution src, Entity target, bool qualifiesStart)
    {
        var tn = target as NpcEntity;
        bool bossLike = tn != null && IsBossLike(tn);
        bool dummy = tn != null && IsDummy(tn);
        var enc = Current;

        // Killing blows that land just after the death still count (feature spec §1.1).
        if (enc is { Ended: true, Finalized: false, FinalizeAt: { } fa } && hit.Time <= fa && target.Dead
            && enc.Targets.ContainsKey(target.Id))
        {
            AddOutgoing(enc, hit, src, target, bossLike, dummy);
            return;
        }

        bool deadTooLong = target.Dead && (target.DeathTime is not { } dt || hit.Time > dt + KillGrace);
        if (enc is null or { Ended: true })
        {
            if (!qualifiesStart || target.Dead) return; // damage to a corpse never reopens
            enc = StartEncounter(hit.Time, tn, bossLike, dummy, pvp: false);
        }
        else
        {
            if (deadTooLong) return;
            if (qualifiesStart && (bossLike || dummy) && enc.Kind is EncounterKind.Trash or EncounterKind.Pvp)
            {
                // §12 boss mode: a boss engages → drop the trash segment.
                if (enc.Kind == EncounterKind.Trash) Drop(enc);
                else End(enc, EncounterOutcome.Timeout, enc.EndUtc);
                enc = StartEncounter(hit.Time, tn, bossLike, dummy, pvp: false);
            }
        }
        AddOutgoing(enc, hit, src, target, bossLike, dummy);
    }

    private void AddOutgoing(Encounter enc, Hit hit, Attribution src, Entity target, bool bossLike, bool dummy)
    {
        bool inScope;
        if (enc.Kind is EncounterKind.Boss or EncounterKind.Dummy)
        {
            if ((bossLike || dummy) && enc.BossTargets.Add(target.Id) && enc.PrimaryBossId is null && target is NpcEntity pn)
                SetPrimaryBoss(enc, pn, hit.Time);
            inScope = enc.BossTargets.Contains(target.Id)
                      || (Options.CountAddsInBossFight && !PrimaryDeadPastGrace(enc, hit.Time));
        }
        else
        {
            inScope = true;
        }

        hit.Kind = HitKind.Outgoing;
        hit.InScope = inScope;
        hit.ToPrimaryBoss = enc.PrimaryBossId == target.Id;
        EnsureTarget(enc, target, bossLike && enc.BossTargets.Contains(target.Id), dummy);
        var c = enc.GetCombatant(hit.Actor);
        c.Dead = false;
        enc.AddHit(hit);
        if (!hit.IsDodge && hit.Amount > 0)
        {
            if (hit.Time > enc.LastActivity) enc.LastActivity = hit.Time;
            if (hit.ToPrimaryBoss) enc.HpCheck.OnDamage(hit.Amount);
        }
        if (src.ViaSummon && src.Summon != null && hit.Actor == CombatEngine.UnknownSummonsEntityId)
            Summons.AddPending(src.Summon, hit, enc);
    }

    private bool PrimaryDeadPastGrace(Encounter enc, DateTime t) =>
        enc.PrimaryBossId is uint id && Entities.Get(id) is { Dead: true, DeathTime: { } dt } && t > dt + KillGrace;

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
        if (target.Dead) ts.Killed = true;
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
        c.Dead = false;
        enc.AddHit(hit);
    }

    private void RecordTargetSelfHeal(uint targetId, uint skill, long amount, DateTime t)
    {
        var enc = Current;
        if (enc is not { Finalized: false }) return;
        if (enc.Ended && enc.Outcome != EncounterOutcome.Kill) return;
        if (!enc.Targets.ContainsKey(targetId) && enc.PrimaryBossId != targetId) return;
        enc.AddHit(new Hit
        {
            Time = t,
            Actor = targetId,
            Source = targetId,
            Target = targetId,
            Skill = skill,
            Amount = amount,
            Flags = HitFlags.Heal,
            Kind = HitKind.TargetSelfHeal,
        });
        if (enc.PrimaryBossId == targetId) enc.HpCheck.OnSelfHeal(amount);
    }

    private void RecordPvp(Hit hit, PlayerEntity victim, bool qualifiesStart)
    {
        var local = Entities.LocalEntity;
        if (local == null) return;
        uint a = hit.Actor, b = victim.Id;
        if (a == b) return;
        bool outgoing = a == local.Id;
        bool incoming = b == local.Id;
        if (!outgoing && !incoming) return;
        var enemy = outgoing ? victim : Entities.Get(a) as PlayerEntity;
        if (enemy == null || enemy.IsLocal || Party.IsMember(enemy.Name)) return;

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
        lc.Dead = false;
        enc.AddHit(hit);
        if (!hit.IsDodge && hit.Amount > 0 && hit.Time > enc.LastActivity) enc.LastActivity = hit.Time;
    }

    private void ApplyReattribution(HashSet<Encounter>? changed)
    {
        if (changed == null) return;
        foreach (var enc in changed)
        {
            enc.RebuildLive();
            RefreshIdentities(enc);
        }
    }

    // ═════════════════════════════ lifecycle ═════════════════════════════

    private Encounter StartEncounter(DateTime t, NpcEntity? target, bool bossLike, bool dummy, bool pvp)
    {
        if (Current is { Ended: true, Finalized: false } pending) Finalize(pending);

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
        if (kind is EncounterKind.Boss or EncounterKind.Dummy && target != null)
        {
            enc.BossTargets.Add(target.Id);
            SetPrimaryBoss(enc, target, t);
        }
        Current = enc;
        SelectedTargetId = null;
        TransientStatus = null;
        if (Entities.LocalEntity is { } local) enc.GetCombatant(local.Id);
        return enc;
    }

    private void SetPrimaryBoss(Encounter enc, NpcEntity n, DateTime t)
    {
        enc.PrimaryBossId = n.Id;
        enc.BossNpcCode = n.NpcCode != 0 ? n.NpcCode : null;
        enc.BossMaxHp = n.MaxHp;
        enc.BossHpStart = n.Hp;
        enc.BossHpEnd = n.Hp;
        enc.BossMaxTrusted = n.MaxSource is MaxHpSource.Spawn or MaxHpSource.HpUpdate
                             && (n.Hp is not long hp || n.MaxHp is not long max || hp >= 0.9 * max);
        enc.ResetCount = _resetCounts.GetValueOrDefault(BossKey(n));
        if (n.Hp is long cur)
        {
            enc.Timeline.Add(t, cur);
            enc.HpCheck.OnReading(t - TimeSpan.FromTicks(1), cur);
        }
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

    /// <summary>Targets of an encounter in display order: bosses first (primary first), then by damage taken. At most 50.</summary>
    public List<TargetState> OrderedTargetsAll(Encounter enc) => OrderedTargets(enc, int.MaxValue);

    public List<TargetState> OrderedTargets(Encounter enc, int max = 50)
    {
        var list = new List<TargetState>(enc.Targets.Values);
        uint? primary = enc.PrimaryBossId;
        list.Sort((a, b) =>
        {
            int pa = a.Id == primary ? 0 : a.IsBoss || a.IsDummy ? 1 : 2;
            int pb = b.Id == primary ? 0 : b.IsBoss || b.IsDummy ? 1 : 2;
            if (pa != pb) return pa.CompareTo(pb);
            int c = b.DamageTaken.CompareTo(a.DamageTaken);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    public void CycleTarget(int direction)
    {
        if (Current is not { } enc || direction == 0) return;
        var list = OrderedTargets(enc);
        if (list.Count == 0) return;
        int idx = SelectedTargetId is uint sel ? list.FindIndex(x => x.Id == sel) : 0;
        if (idx < 0) idx = 0;
        int step = Math.Sign(direction);
        idx = ((idx + step) % list.Count + list.Count) % list.Count;
        SelectedTargetId = list[idx].Id;
    }
}
