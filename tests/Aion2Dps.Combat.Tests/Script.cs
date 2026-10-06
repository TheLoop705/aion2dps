using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

/// <summary>Event-driven test scenario builder with synthetic capture timestamps (seconds from <see cref="T0"/>).</summary>
internal sealed class Script
{
    public static readonly DateTime T0 = new(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);

    // Well-known ids
    public const uint Me = 100;
    public const uint Ally = 200;
    public const uint Ele = 300;
    public const uint Cleric = 400;
    public const uint Stranger = 600;
    public const uint Enemy = 800;
    public const uint Boss = 5000;
    public const uint Boss2 = 5001;
    public const uint Trash = 6001;
    public const uint Trash2 = 6002;
    public const uint Add = 6100;
    public const uint Dummy = 7000;

    // Skills
    public const uint GlaSkill = 11020030;      // Keen Strike (Gladiator)
    public const uint GlaSkill2 = 11030030;
    public const uint SorSkill = 15280240;      // Bittercold Wind cast (Sorcerer)
    public const uint RanSkill = 14010243;      // Deadshot (Ranger)
    public const uint NpcSkill = 1500001;       // monster attack
    public const uint SpiritSkill = 100011;     // spirit basic attack
    public const uint SelfHealSkill = 11730007; // Blood Absorption

    public const uint Map1 = 600021;
    public const uint Map2 = 600022;

    public readonly FakeGameData Data = new();
    public readonly EngineOptions Options;
    public readonly CombatEngine Engine;
    public readonly List<EncounterRecord> Completed = new();

    public Script(EngineOptions? options = null, Func<long>? clientClock = null)
    {
        Options = options ?? new EngineOptions();
        Engine = clientClock == null ? new CombatEngine(Data, Options) : new CombatEngine(Data, Options, clientClock);
        Engine.EncounterCompleted += r => { lock (Completed) Completed.Add(r); };
    }

    /// <summary>Map, local player (Gladiator), an ally (Sorcerer), the boss and a trash mob.</summary>
    public static Script Standard(EngineOptions? options = null)
    {
        var s = new Script(options);
        s.Map(0, Map1);
        s.Self(0);
        s.Player(0, Ally, "Ally", 26);
        s.SpawnNpc(0, Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.SpawnNpc(0, Trash, FakeGameData.TrashCode, 50_000, 50_000);
        return s;
    }

    public static DateTime At(double seconds) => T0.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    public void Send(GameEvent e) => Engine.OnEvent(e);

    public void Map(double t, uint mapId) => Send(new MapLoadEvent { Time = At(t), LoadCount = 1, MapId = mapId });

    public void Self(double t, uint id = Me, string name = "Me", uint classCode = 6, ushort server = 1304, uint level = 30) =>
        Send(new SelfInfoEvent { Time = At(t), Entity = id, Name = name, ServerId = server, ClassCode = classCode, Class = ClassInfo.FromCode(classCode), Level = level });

    public void Player(double t, uint id, string name, uint classCode, string? guild = null) =>
        Send(new PlayerInfoEvent { Time = At(t), Entity = id, Name = name, ClassCode = classCode, Class = ClassInfo.FromCode(classCode), GuildName = guild });

    public void SpawnNpc(double t, uint id, uint code, long? hp, long? max) =>
        Send(new SpawnEvent { Time = At(t), Entity = id, KindByte = 0x0C, KindFlags = 0x22, NpcCode = code, HpCurrent = hp, HpMax = max });

    public void SpawnSummon(double t, uint id, uint? owner = null, uint? anchor = null, string? casterName = null, uint code = 2920129) =>
        Send(new SpawnEvent
        {
            Time = At(t), Entity = id, KindByte = 0x1F, KindFlags = 0x10, NpcCode = code, HpCurrent = 1000, HpMax = 1000,
            OwnerId = owner, AnchorId = anchor, CasterName = casterName, IsSummonLike = true,
        });

    public void Hit(double t, uint actor, uint target, long? amount, uint skill = GlaSkill, byte layout = 4, byte dmgType = 2,
        HitMods mods = HitMods.None, HitDirection dir = HitDirection.None, byte hitTag = 1, uint power = 10170,
        uint[]? extra = null, uint hitIndex = 1)
    {
        Send(new DamageEvent
        {
            Time = At(t), Target = target, Actor = actor, SkillRaw = skill, SkillId = SkillIds.Normalize(skill),
            Switch = (uint)(layout | (extra is { Length: > 0 } ? 0x20 : 0)), Layout = layout, HitTag = hitTag,
            DamageType = dmgType, Mods = mods, Direction = dir, EffectId = skill * 100 + 11, HitIndex = hitIndex,
            PowerScalar = power, Amount = layout == 0 ? null : amount, ExtraHits = extra ?? Array.Empty<uint>(),
            EffectValidated = true,
        });
    }

    public void Notice(double t, uint actor, uint target, uint skill, uint power = 10170) =>
        Hit(t, actor, target, null, skill, layout: 0, dmgType: 2, power: power);

    public void Hp(double t, uint id, long hp) =>
        Send(new EntityStatsEvent { Time = At(t), Entity = id, Format = 0x02, CurrentHp = hp });

    public void Dot(double t, uint actor, uint target, byte flags, long? amount, long? heal = null, uint effect = 0, uint? skill = null) =>
        Send(new DotEvent { Time = At(t), Actor = actor, Target = target, Flags = flags, Amount = amount, Heal = heal, EffectId = effect, SkillId = skill, Stack = 1 });

    public void Death(double t, uint id, uint flag = 3) => Send(new DeathEvent { Time = At(t), Entity = id, Flag = flag });

    public void Kill(double t, uint target, uint killer, string? killerName, uint skill = GlaSkill) =>
        Send(new KillEvent { Time = At(t), Target = target, Killer = killer, KillerName = killerName, SkillRaw = skill, KillerServerId = 1304 });

    public void Roster(double t, params (string Name, uint ClassCode)[] members) =>
        Send(new PartyRosterEvent
        {
            Time = At(t), PartyKey = 1,
            Members = members.Select((m, i) => new PartyMember
            {
                Slot = i + 1, Name = m.Name, ClassCode = m.ClassCode, Class = ClassInfo.FromCode(m.ClassCode), Level = 30,
                ServerId = 1304, GearScore = 1000u + (uint)i, CombatPower = 500_000UL + (ulong)i,
            }).ToList(),
        });

    public MeterSnapshot Snap(double t) => Engine.GetSnapshot(At(t));

    public void Tick(double t) => Engine.Tick(At(t));

    public EncounterRecord Record() => Engine.GetCurrentEncounter() ?? throw new InvalidOperationException("no encounter");

    public static PlayerRow Row(MeterSnapshot s, uint id) => s.Rows.Single(r => r.EntityId == id);

    public static CombatantRecord Combatant(EncounterRecord r, uint id) => r.Combatants.Single(c => c.EntityId == id);
}
