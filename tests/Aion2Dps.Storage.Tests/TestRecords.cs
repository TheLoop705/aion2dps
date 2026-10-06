using Aion2Dps.Contracts;

namespace Aion2Dps.Storage.Tests;

internal static class TestRecords
{
    public static readonly DateTime T0 = new(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>A small fight with the local player plus party members.</summary>
    public static EncounterRecord Fight(
        DateTime start,
        uint? boss = 1001,
        uint? map = 500,
        EncounterKind kind = EncounterKind.Boss,
        EncounterOutcome outcome = EncounterOutcome.Kill,
        string local = "Alice",
        double localDps = 1000,
        double duration = 100,
        params (string Name, double Dps)[] others)
    {
        var rec = new EncounterRecord
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Outcome = outcome,
            StartUtc = start,
            EndUtc = start.AddSeconds(duration),
            DurationSeconds = duration,
            MapId = map,
            BossNpcCode = boss,
            BossMaxHp = boss is null ? null : 10_000_000,
            LocalPlayerName = local,
            LocalPlayerClass = CharacterClass.Sorcerer,
        };
        rec.Combatants.Add(new CombatantRecord
        {
            EntityId = 1, Name = local, Class = CharacterClass.Sorcerer, IsLocal = true, IsPartyMember = true,
            Dps = localDps, Damage = (long)(localDps * duration), Contribution = 0.5,
        });
        uint eid = 2;
        foreach (var (name, dps) in others)
        {
            rec.Combatants.Add(new CombatantRecord
            {
                EntityId = eid++, Name = name, Class = CharacterClass.Templar, IsPartyMember = true,
                Dps = dps, Damage = (long)(dps * duration), Contribution = 0.25,
            });
        }
        rec.TotalDamage = rec.Combatants.Sum(c => c.Damage);
        rec.PartyDps = rec.TotalDamage / duration;
        return rec;
    }

    /// <summary>A record that sets every property of every child type to a non-default value.</summary>
    public static EncounterRecord Rich(int hitCount = 50)
    {
        var start = new DateTime(2026, 9, 2, 18, 30, 15, 123, DateTimeKind.Utc).AddTicks(4567);
        var rec = new EncounterRecord
        {
            SchemaVersion = EncounterRecord.CurrentSchemaVersion,
            Id = Guid.NewGuid(),
            Kind = EncounterKind.Boss,
            Outcome = EncounterOutcome.Wipe,
            StartUtc = start,
            EndUtc = start.AddSeconds(187.25),
            DurationSeconds = 187.25,
            MapId = 600_123,
            ServerId = 2015,
            LocalPlayerName = "Ärger한글",
            LocalPlayerClass = CharacterClass.Brawler,
            BossNpcCode = 2_400_017,
            BossEntityId = 77_123,
            BossMaxHp = 123_456_789_012,
            BossHpStart = 123_456_789_012,
            BossHpEnd = 4_000_000_001,
            BossHpTimeline = [new HpSample(0f, 123_456_789_012), new HpSample(12.5f, 100_000_000_000), new HpSample(187.25f, 4_000_000_001)],
            ResetCount = 2,
            TotalDamage = 119_456_789_011,
            PartyDps = 637_953_478.03,
            HpCheck = new HpCheckResult { DecodedDamage = 119_456_789_011, HpLost = 119_456_789_011, BossSelfHealing = 42, Ratio = 0.99999964, Passed = true, Note = "ok" },
            CaptureGaps = true,
            Note = "rich \"note\" \n with escapes",
        };
        rec.Combatants.Add(new CombatantRecord
        {
            EntityId = 10, Name = "Ärger한글", Class = CharacterClass.Brawler, Kind = CombatantKind.Player, ServerId = 2015,
            IsLocal = true, IsPartyMember = true, Level = 45, GearScore = 3210, CombatPower = 987_654_321_000,
            Damage = 60_000_000_000, BossDamage = 59_000_000_000, Dps = 320_427_236.3, ActiveDps = 330_000_000.1,
            Contribution = 0.4778, DamageShare = 0.5023, Healing = 12345, DamageTaken = 6789, Deaths = 1,
            FirstHitUtc = start, LastHitUtc = start.AddSeconds(180),
            Quality = new HitQualityStats
            {
                Hits = 1000, Crits = 400, Back = 300, Front = 200, Perfect = 50, Double = 60, Smite = 7, Parried = 8, Dodged = 9,
                MultiHits = 100, ExtraHitCount = 250, DotTicks = 77, DotDamage = 1_234_567, MaxHit = 98_765_432, MaxHitSkillId = 11_020_000,
                QualityMeasuredHits = 900,
            },
            Defense = new DefenseStats
            {
                HitsTaken = 12, DamageTaken = 6789, CritsTaken = 3, BackHitsTaken = 2, Dodged = 4, Parried = 5,
                Blocked = 6, Endured = null, Resisted = 7, HealingReceived = 4321,
            },
            Skills =
            [
                new SkillStats
                {
                    SkillId = 11_020_000, SampleSkillId = 11_020_350, Damage = 1_000_000, Healing = 5, Hits = 10, Casts = 9, Crits = 4,
                    MinHit = 1000, MaxHit = 900_000, Back = 1, Front = 2, Perfect = 3, Double = 4, Smite = 5, Parried = 6, MultiHits = 7,
                    ExtraHitCount = 8, DotTicks = 9, DotDamage = 10, QualityMeasuredHits = 11, FromSummon = true,
                },
            ],
            DamagePerSecond = [0, 15, 1_000_000_000_000, 3],
            DamageTakenBySource = [new SourceDamage { SourceEntityId = 77_123, SourceNpcCode = 2_400_017, SourcePlayerName = null, SkillId = 2_000_001, Damage = 6789, Hits = 12, Crits = 3 }],
            Buffs = [new BuffUptime { BuffId = 110_200_001, SkillId = 11_020_000, Applications = 3, UptimeSeconds = 60.5, Uptime = 0.323, CasterEntityId = 10 }],
            DamageToLocal = 0, DamageFromLocal = 0, KilledByLocal = false,
        });
        rec.Combatants.Add(new CombatantRecord
        {
            EntityId = 11, Name = "Bob", Class = CharacterClass.Cleric, Kind = CombatantKind.EnemyPlayer,
            DamageToLocal = 555, DamageFromLocal = 666, KilledByLocal = true,
            DamageTakenBySource = [new SourceDamage { SourceEntityId = 10, SourcePlayerName = "Ärger한글", SkillId = 11_020_000, Damage = 666, Hits = 2 }],
        });
        rec.Combatants.Add(new CombatantRecord { EntityId = 0, Name = "(summons)", Kind = CombatantKind.UnknownSummons, Damage = 3 });
        rec.Targets.Add(new TargetRecord
        {
            EntityId = 77_123, NpcCode = 2_400_017, IsBoss = true, IsDummy = false, MaxHp = 123_456_789_012, LastHp = 4_000_000_001,
            DamageTaken = 119_456_789_011, SelfHealing = 42, Killed = false, FirstHitUtc = start, LastHitUtc = start.AddSeconds(187.25),
        });
        rec.Targets.Add(new TargetRecord { EntityId = 11, PlayerName = "Bob", Killed = true });
        var rng = new Random(1234);
        for (int i = 0; i < hitCount; i++)
        {
            rec.Hits.Add(new HitRecord
            {
                T = i * 0.0137f,
                Actor = 10,
                Source = (uint)(i % 7 == 0 ? 900 : 10),
                Target = 77_123,
                Skill = (uint)(11_020_000 + rng.Next(0, 400)),
                Amount = rng.NextInt64(1, 5_000_000_000),
                Flags = (HitFlags)rng.Next(0, 1 << 14),
                HitTag = (byte)rng.Next(256),
                HitIndex = (byte)rng.Next(256),
                ExtraHits = (byte)rng.Next(4),
            });
        }
        return rec;
    }
}
