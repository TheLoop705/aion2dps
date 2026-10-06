namespace Aion2Dps.Combat;

/// <summary>Builds the live overlay view from the running totals (no hit-list scans: cheap enough for 4×/s).</summary>
internal static class SnapshotBuilder
{
    public static MeterSnapshot Build(CombatCore core, DateTime now)
    {
        var gd = core.GameData;
        var opts = core.Options;
        uint? mapId = core.MapId;
        string? mapName = mapId is uint m ? gd.GetMapName(m) : null;
        var local = core.LocalPlayerInfo;
        double? ping = core.Ping.Smoothed;

        if (!core.SawTraffic)
        {
            return new MeterSnapshot
            {
                TimeUtc = now, State = MeterState.Idle, Mode = core.Mode, StatusText = "Waiting for game data",
                MapId = mapId, MapName = mapName, LocalPlayer = local, PingMs = ping,
            };
        }

        var enc = core.Current;
        if (enc == null)
        {
            string status = core.TrainingArmed
                ? $"Training ready ({core.TrainingDuration.TotalSeconds:0} s): hit a target to start"
                : core.TransientStatus ?? (local == null ? "Waiting for combat (character not detected yet)" : "Waiting for combat");
            return new MeterSnapshot
            {
                TimeUtc = now, State = MeterState.WaitingForCombat, Mode = core.Mode, StatusText = status,
                MapId = mapId, MapName = mapName, LocalPlayer = local, PingMs = ping,
            };
        }

        bool ended = enc.Ended;
        double encDuration = enc.DurationSeconds;
        DateTime start = enc.StartUtc;
        DateTime clockNow = now;
        if (enc.LastHit is { } lh && clockNow < lh) clockNow = lh;
        if (clockNow < start) clockNow = start;
        if (enc.Kind == EncounterKind.Training && enc.TrainingEnd is { } te && clockNow > te) clockNow = te;
        double liveElapsed = Math.Max(1, (clockNow - start).TotalSeconds);
        bool useAll = core.Mode == MeterMode.AllTargets;
        // Party only: local player + roster members. Without a roster (solo) only the local player is shown; if the
        // local player is not known yet either, filtering would empty the meter, so everyone stays visible.
        bool rosterKnown = core.Party.HasRoster;
        bool partyFilter = opts.PartyOnly && (rosterKnown || local != null);

        // ── rows ──
        var candidates = new List<(CombatantState C, LiveAccumulator A)>(enc.Combatants.Count);
        long total = 0;
        foreach (var c in enc.Combatants.Values)
        {
            if (c.IsEnemy || c.Kind == CombatantKind.EnemyPlayer) continue;
            if (partyFilter && !(c.IsLocal || (rosterKnown && c.IsPartyMember))) continue;
            var acc = useAll ? c.All : c.Scoped;
            if (acc.Damage == 0 && c.Healing == 0 && c.DamageTaken == 0 && c.Deaths == 0 && !c.IsLocal) continue;
            candidates.Add((c, acc));
            total += acc.Damage;
        }
        candidates.Sort((a, b) =>
        {
            int r = b.A.Damage.CompareTo(a.A.Damage);
            return r != 0 ? r : a.C.Id.CompareTo(b.C.Id);
        });

        long top = candidates.Count > 0 ? candidates[0].A.Damage : 0;
        var rows = new List<PlayerRow>(candidates.Count);
        double minDps = double.MaxValue, maxDps = 0, sumDps = 0;
        int dpsCount = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var (c, a) = candidates[i];
            double dps;
            if (ended) dps = a.Damage / encDuration;
            else
            {
                DateTime from = opts.LivePlayerClock && a.First is { } f ? f : start;
                dps = a.Damage / Math.Max(1, (clockNow - from).TotalSeconds);
            }
            double share = total > 0 ? (double)a.Damage / total : 0;
            rows.Add(new PlayerRow
            {
                EntityId = c.Id,
                Name = c.Name,
                Class = c.Class,
                GearScore = c.Kind == CombatantKind.Player ? core.Party.Get(c.Name)?.GearScore : null,
                Kind = c.Kind,
                IsLocal = c.IsLocal,
                IsPartyMember = c.IsPartyMember,
                Rank = i + 1,
                Damage = a.Damage,
                Dps = dps,
                Contribution = enc.HpContribution(c) ?? share,
                DamageShare = share,
                RelativeToTop = top > 0 ? (double)a.Damage / top : 0,
                CritRate = a.Hits > 0 ? (double)a.Crits / a.Hits : 0,
                Hits = a.Hits,
                MaxHit = a.MaxHit,
                Healing = c.Healing,
                DamageTaken = c.DamageTaken,
                IsDead = c.Dead,
            });
            if (c.Kind == CombatantKind.Player && a.Damage > 0)
            {
                minDps = Math.Min(minDps, dps);
                maxDps = Math.Max(maxDps, dps);
                sumDps += dps;
                dpsCount++;
            }
        }

        // ── targets ──
        var ordered = core.OrderedTargets(enc);
        var targets = new List<TargetInfo>(ordered.Count);
        var bosses = new List<TargetInfo>(enc.Bosses.Count);
        TargetInfo? selected = null;
        uint? wanted = core.SelectedTargetId ?? core.DefaultTargetId(enc);
        uint? primaryId = enc.PrimaryBossId;
        foreach (var t in ordered)
        {
            var info = ToTargetInfo(gd, t, enc.Boss(t.Id), primaryId);
            targets.Add(info);
            if (info.IsEncounterBoss) bosses.Add(info);
            if (wanted == t.Id) selected = info;
        }
        selected ??= targets.Count > 0 ? targets[0] : null;

        // ── PvP ──
        var pvp = new List<PvpRow>(enc.Pvp.Count);
        foreach (var pv in enc.Pvp.Values)
        {
            double? hpFrac = null;
            if (core.Entities.Get(pv.EnemyId) is PlayerEntity { Hp: { } hp, MaxHp: { } mx } && mx > 0) hpFrac = Math.Clamp((double)hp / mx, 0, 1);
            if (pv.Killed) hpFrac = 0;
            pvp.Add(new PvpRow
            {
                EntityId = pv.EnemyId,
                Name = pv.Name.Length > 0 ? pv.Name : $"Player {pv.EnemyId}",
                Class = pv.Class,
                ServerId = pv.ServerId,
                GuildName = pv.GuildName,
                DamageDealt = pv.DamageDealt,
                DamageTaken = pv.DamageTaken,
                HpFraction = hpFrac,
                Killed = pv.Killed,
                LastActivityUtc = pv.LastActivity,
            });
        }
        pvp.Sort((a, b) => (b.DamageDealt + b.DamageTaken).CompareTo(a.DamageDealt + a.DamageTaken));

        return new MeterSnapshot
        {
            TimeUtc = now,
            State = ended ? MeterState.Ended : MeterState.InCombat,
            Mode = core.Mode,
            StatusText = StatusText(core, enc, gd, ended, clockNow),
            EncounterId = enc.Id,
            EncounterKind = enc.Kind,
            Outcome = enc.Outcome,
            Elapsed = TimeSpan.FromSeconds(ended ? encDuration : Math.Max(0, (clockNow - start).TotalSeconds)),
            Target = selected,
            Targets = targets,
            Rows = rows,
            TotalDamage = total,
            PartyDps = total / (ended ? encDuration : liveElapsed),
            MinDps = dpsCount > 0 ? minDps : 0,
            AvgDps = dpsCount > 0 ? sumDps / dpsCount : 0,
            MaxDps = maxDps,
            PvpRows = pvp,
            PvpKills = enc.PvpKills,
            MapId = mapId,
            MapName = mapName,
            LocalPlayer = local,
            PingMs = ping,
            HpCheckRatio = enc.Primary?.HpCheck.Ratio,
            OverallHpCheckRatio = enc.Bosses.Count > 0 ? HpCheckTracker.CombinedRatio(enc.Bosses.Select(b => b.HpCheck)) : null,
            Bosses = bosses,
        };
    }

    private static TargetInfo ToTargetInfo(IGameData gd, TargetState t, BossState? boss, uint? primaryId)
    {
        string name = t.IsPlayer
            ? t.PlayerName ?? $"Player {t.Id}"
            : t.NpcCode is uint code ? gd.GetNpcName(code) : $"Target {t.Id}";
        long? hp = t.Killed ? 0 : t.LastHp;
        return new TargetInfo
        {
            EntityId = t.Id,
            NpcCode = t.NpcCode,
            Name = name,
            IsBoss = t.IsBoss,
            IsDummy = t.IsDummy,
            IsPlayer = t.IsPlayer,
            Hp = hp,
            MaxHp = t.MaxHp,
            HpFraction = hp is long h && t.MaxHp is long m && m > 0 ? Math.Clamp((double)h / m, 0, 1) : null,
            DamageTaken = t.DamageTaken,
            IsDead = t.Killed,
            IsEncounterBoss = boss != null,
            IsPrimaryBoss = boss != null && primaryId == t.Id,
            HpCheckRatio = boss?.HpCheck.Ratio,
        };
    }

    private static string StatusText(CombatCore core, Encounter enc, IGameData gd, bool ended, DateTime clockNow)
    {
        string boss = enc.BossTitle(gd);
        if (!ended)
        {
            return enc.Kind switch
            {
                EncounterKind.Boss => boss,
                EncounterKind.Dummy => boss,
                EncounterKind.Training when enc.TrainingDuration is { } d =>
                    $"Training {Clock((clockNow - enc.StartUtc).TotalSeconds)} / {Clock(d.TotalSeconds)}",
                EncounterKind.Pvp => $"PvP · {enc.Pvp.Count} active · {enc.PvpKills} kills",
                _ => "In combat",
            };
        }
        if (enc.Kind == EncounterKind.Training) return enc.TrainingCompleted ? "Training complete" : "Training stopped";
        return enc.Outcome switch
        {
            EncounterOutcome.Kill => enc.Kind is EncounterKind.Boss or EncounterKind.Dummy ? $"Kill: {boss}" : "Kill",
            EncounterOutcome.Wipe => $"Wipe: {boss}",
            EncounterOutcome.Timeout => "Combat ended",
            EncounterOutcome.ManualReset => "Reset",
            EncounterOutcome.ZoneChange => "Zone changed",
            _ => "Combat ended",
        };
    }

    private static string Clock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }
}
