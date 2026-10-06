namespace Aion2Dps.Combat;

/// <summary>Builds a full, independent <see cref="EncounterRecord"/> from an encounter's hit list (feature spec §1 metrics).</summary>
internal static class EncounterRecordBuilder
{
    private static readonly TimeSpan CastRearm = TimeSpan.FromSeconds(5);

    private sealed class Builder
    {
        public readonly CombatantRecord R;
        public readonly Dictionary<uint, SkillStats> Skills = new();
        public readonly Dictionary<(uint Source, uint Skill), SourceDamage> Sources = new();
        public readonly Dictionary<(uint Source, uint Skill, byte Tag), DateTime> CastSeen = new();
        public long[] PerSecond;

        public Builder(CombatantRecord r, int seconds)
        {
            R = r;
            PerSecond = new long[seconds];
        }

        private SkillStats Skill(uint key, uint exact)
        {
            if (!Skills.TryGetValue(key, out var s))
            {
                s = new SkillStats { SkillId = key, SampleSkillId = exact };
                Skills[key] = s;
            }
            return s;
        }

        public void AddDamage(Hit h, uint groupKey, int second)
        {
            var q = R.Quality;
            if (h.IsDodge)
            {
                q.Dodged++;
                return;
            }
            long amt = h.Amount;
            R.Damage += amt;
            if (h.ToPrimaryBoss) R.BossDamage += amt;
            if ((uint)second < (uint)PerSecond.Length) PerSecond[second] += amt;
            if (R.FirstHitUtc is null || h.Time < R.FirstHitUtc) R.FirstHitUtc = h.Time;
            if (R.LastHitUtc is null || h.Time > R.LastHitUtc) R.LastHitUtc = h.Time;

            var s = Skill(groupKey, h.Skill);
            s.Damage += amt;
            if ((h.Flags & HitFlags.Summon) != 0) s.FromSummon = true;
            if (h.IsDot)
            {
                q.DotTicks++;
                q.DotDamage += amt;
                s.DotTicks++;
                s.DotDamage += amt;
                return;
            }

            q.Hits++;
            s.Hits++;
            if (h.IsCrit) { q.Crits++; s.Crits++; }
            if (h.QualityMeasured)
            {
                q.QualityMeasuredHits++;
                s.QualityMeasuredHits++;
                if ((h.Flags & HitFlags.Back) != 0) { q.Back++; s.Back++; }
                if ((h.Flags & HitFlags.Front) != 0) { q.Front++; s.Front++; }
                if ((h.Flags & HitFlags.Perfect) != 0) { q.Perfect++; s.Perfect++; }
                if ((h.Flags & HitFlags.Double) != 0) { q.Double++; s.Double++; }
                if ((h.Flags & HitFlags.Smite) != 0) { q.Smite++; s.Smite++; }
                if ((h.Flags & HitFlags.Parry) != 0) { q.Parried++; s.Parried++; }
            }
            if (h.ExtraHits > 0)
            {
                q.MultiHits++;
                q.ExtraHitCount += h.ExtraHits;
                s.MultiHits++;
                s.ExtraHitCount += h.ExtraHits;
            }
            if (amt > q.MaxHit)
            {
                q.MaxHit = amt;
                q.MaxHitSkillId = h.Skill;
            }
            if (amt > s.MaxHit) s.MaxHit = amt;
            if (s.Hits == 1 || amt < s.MinHit) s.MinHit = amt;

            var key = (h.Source, h.Skill, h.HitTag);
            if (!CastSeen.TryGetValue(key, out var last) || h.Time - last > CastRearm) s.Casts++;
            CastSeen[key] = h.Time;
        }

        public void AddTaken(Hit h, uint? npcCode, string? playerName)
        {
            var d = R.Defense;
            if (h.IsDodge)
            {
                d.Dodged++;
                return;
            }
            long amt = h.Amount;
            d.DamageTaken += amt;
            R.DamageTaken += amt;
            if (!h.IsDot)
            {
                d.HitsTaken++;
                if (h.IsCrit) d.CritsTaken++;
                if (h.QualityMeasured && (h.Flags & HitFlags.Back) != 0) d.BackHitsTaken++;
                if (h.QualityMeasured && (h.Flags & HitFlags.Parry) != 0) d.Parried++;
            }
            var key = (h.Source, h.Skill);
            if (!Sources.TryGetValue(key, out var src))
            {
                src = new SourceDamage { SourceEntityId = h.Source, SourceNpcCode = npcCode, SourcePlayerName = playerName, SkillId = h.Skill };
                Sources[key] = src;
            }
            src.Damage += amt;
            if (!h.IsDot)
            {
                src.Hits++;
                if (h.IsCrit) src.Crits++;
            }
        }

        public void AddHeal(Hit h, uint groupKey)
        {
            R.Healing += h.Amount;
            var s = Skill(groupKey, h.Skill);
            s.Healing += h.Amount;
            if ((h.Flags & HitFlags.Summon) != 0) s.FromSummon = true;
        }
    }

    public static EncounterRecord Build(CombatCore core, Encounter enc)
    {
        var gd = core.GameData;
        DateTime start = enc.StartUtc;
        double duration = enc.DurationSeconds;
        var local = core.Entities.Local;

        var rec = new EncounterRecord
        {
            Id = enc.Id,
            Kind = enc.Kind,
            Outcome = enc.Outcome,
            StartUtc = start,
            EndUtc = enc.EndUtc,
            DurationSeconds = duration,
            MapId = enc.MapId,
            ServerId = local is { ServerId: > 0 } ? local.ServerId : null,
            LocalPlayerName = local is { Name.Length: > 0 } ? local.Name : null,
            LocalPlayerClass = local?.Class ?? CharacterClass.Unknown,
            BossNpcCode = enc.BossNpcCode,
            BossEntityId = enc.PrimaryBossId,
            BossMaxHp = enc.BossMaxHp,
            BossHpStart = enc.BossHpStart,
            BossHpEnd = enc.BossHpEnd,
            BossHpTimeline = enc.Timeline.ToSamples(start),
            ResetCount = enc.ResetCount,
            HpCheck = enc.HpCheck.ToResult(),
            CaptureGaps = enc.CaptureGaps,
            Note = enc.Note,
        };

        int seconds = Math.Max(1, (int)Math.Floor((enc.EndUtc - start).TotalSeconds) + 1);
        var builders = new Dictionary<uint, Builder>();
        var groupKeys = new Dictionary<uint, uint>();

        Builder Get(uint id, CombatantKind kind)
        {
            if (builders.TryGetValue(id, out var b)) return b;
            var r = new CombatantRecord { EntityId = id, Kind = kind };
            if (enc.Combatants.TryGetValue(id, out var cs))
            {
                r.Name = cs.Name;
                r.Class = cs.Class;
                r.IsLocal = cs.IsLocal;
                r.IsPartyMember = cs.IsPartyMember;
                r.ServerId = cs.ServerId;
                r.Deaths = cs.Deaths;
                if (cs.IsEnemy && kind == CombatantKind.Player) r.Kind = CombatantKind.EnemyPlayer;
            }
            else if (kind == CombatantKind.UnknownSummons)
            {
                r.Name = "Unknown summons";
            }
            if (enc.Pvp.TryGetValue(id, out var pv))
            {
                r.Kind = CombatantKind.EnemyPlayer;
                if (pv.Name.Length > 0) r.Name = pv.Name;
                if (pv.Class != CharacterClass.Unknown) r.Class = pv.Class;
                r.ServerId ??= pv.ServerId;
                r.KilledByLocal = pv.Killed;
            }
            if (r.Name.Length == 0) r.Name = $"Player {id}";
            if (core.Party.Get(r.Name) is { } m && r.Kind != CombatantKind.EnemyPlayer)
            {
                r.IsPartyMember = true;
                r.Level = m.Level != 0 ? m.Level : null;
                r.GearScore = m.GearScore;
                r.CombatPower = m.CombatPower;
                r.ServerId ??= m.ServerId != 0 ? m.ServerId : null;
            }
            if (r.Level is null && core.Entities.Get(id) is PlayerEntity { Level: { } lvl } pe && pe.DisplayName == r.Name) r.Level = lvl;
            b = new Builder(r, seconds);
            builders[id] = b;
            return b;
        }

        uint GroupKey(uint skill)
        {
            if (!groupKeys.TryGetValue(skill, out var k))
            {
                k = gd.GetSkillGroupKey(skill);
                groupKeys[skill] = k;
            }
            return k;
        }

        static CombatantKind KindOf(uint id) =>
            id == CombatEngine.UnknownSummonsEntityId ? CombatantKind.UnknownSummons : CombatantKind.Player;

        var hits = new List<HitRecord>(enc.Hits.Count);
        foreach (var h in enc.Hits)
        {
            int sec = (int)Math.Floor((h.Time - start).TotalSeconds);
            switch (h.Kind)
            {
                case HitKind.Outgoing:
                    if (!h.InScope) continue;
                    Get(h.Actor, KindOf(h.Actor)).AddDamage(h, GroupKey(h.Skill), sec);
                    break;
                case HitKind.PvpOut:
                {
                    if (h.InScope) Get(h.Actor, CombatantKind.Player).AddDamage(h, GroupKey(h.Skill), sec);
                    var enemy = Get(h.Target, CombatantKind.EnemyPlayer);
                    enemy.R.DamageFromLocal += h.Amount;
                    enemy.AddTaken(h, null, null);
                    break;
                }
                case HitKind.PvpIn:
                {
                    var enemy = Get(h.Actor, CombatantKind.EnemyPlayer);
                    enemy.AddDamage(h, GroupKey(h.Skill), sec);
                    enemy.R.DamageToLocal += h.Amount;
                    string? name = enc.Pvp.TryGetValue(h.Actor, out var pv) ? pv.Name : null;
                    Get(h.Target, CombatantKind.Player).AddTaken(h, null, name);
                    break;
                }
                case HitKind.Incoming:
                {
                    uint? code = enc.SourceNpcCodes.TryGetValue(h.Source, out var c) ? c : null;
                    Get(h.Target, CombatantKind.Player).AddTaken(h, code, null);
                    break;
                }
                case HitKind.Heal:
                    Get(h.Actor, KindOf(h.Actor)).AddHeal(h, GroupKey(h.Skill));
                    if (builders.ContainsKey(h.Target) || enc.Combatants.ContainsKey(h.Target))
                        Get(h.Target, KindOf(h.Target)).R.Defense.HealingReceived += h.Amount;
                    break;
                case HitKind.TargetSelfHeal:
                    break;
            }
            hits.Add(new HitRecord
            {
                T = (float)(h.Time - start).TotalSeconds,
                Actor = h.Actor,
                Source = h.Source,
                Target = h.Target,
                Skill = h.Skill,
                Amount = h.Amount,
                Flags = h.Flags,
                HitTag = h.HitTag,
                HitIndex = (byte)Math.Min(255u, h.HitIndex),
                ExtraHits = h.ExtraHits,
            });
        }
        rec.Hits = hits;

        // Combatants that never appear in a hit (e.g. the local player who only died) still get a row.
        foreach (var c in enc.Combatants.Values)
            if (!builders.ContainsKey(c.Id) && c.Kind != CombatantKind.UnknownSummons) Get(c.Id, c.Kind);
        foreach (var id in enc.Pvp.Keys)
            if (!builders.ContainsKey(id)) Get(id, CombatantKind.EnemyPlayer);

        long total = 0;
        foreach (var b in builders.Values)
            if (b.R.Kind != CombatantKind.EnemyPlayer) total += b.R.Damage;
        rec.TotalDamage = total;
        rec.PartyDps = total / duration;

        bool hpContribution = enc.Kind == EncounterKind.Boss && enc.BossMaxTrusted && enc.BossMaxHp is > 0;
        var combatants = new List<CombatantRecord>(builders.Count);
        foreach (var b in builders.Values)
        {
            var r = b.R;
            r.Dps = r.Damage / duration;
            if (r.FirstHitUtc is { } f && r.LastHitUtc is { } l) r.ActiveDps = r.Damage / Math.Max(1, (l - f).TotalSeconds);
            if (r.Kind != CombatantKind.EnemyPlayer)
            {
                r.DamageShare = total > 0 ? (double)r.Damage / total : 0;
                r.Contribution = hpContribution ? (double)r.BossDamage / enc.BossMaxHp!.Value : r.DamageShare;
                if (r.Kind == CombatantKind.Player) r.Buffs = core.Buffs.Compute(r.EntityId, start, duration);
            }
            if (r.Kind == CombatantKind.EnemyPlayer) r.Damage = r.DamageToLocal;
            r.Skills = b.Skills.Values.OrderByDescending(s => s.Damage + s.Healing).ThenBy(s => s.SkillId).ToList();
            r.DamagePerSecond = b.PerSecond.ToList();
            r.DamageTakenBySource = b.Sources.Values.OrderByDescending(s => s.Damage).ToList();
            combatants.Add(r);
        }
        rec.Combatants = combatants
            .OrderBy(r => r.Kind == CombatantKind.EnemyPlayer ? 1 : 0)
            .ThenByDescending(r => r.Damage)
            .ThenBy(r => r.EntityId)
            .ToList();

        var targets = new List<TargetRecord>(enc.Targets.Count);
        foreach (var t in core.OrderedTargetsAll(enc))
        {
            targets.Add(new TargetRecord
            {
                EntityId = t.Id,
                NpcCode = t.NpcCode,
                PlayerName = t.IsPlayer ? t.PlayerName : null,
                IsBoss = t.IsBoss,
                IsDummy = t.IsDummy,
                MaxHp = t.MaxHp,
                LastHp = t.LastHp,
                DamageTaken = t.DamageTaken,
                SelfHealing = t.SelfHealing,
                Killed = t.Killed,
                FirstHitUtc = t.First,
                LastHitUtc = t.Last,
            });
        }
        rec.Targets = targets;
        return rec;
    }
}
