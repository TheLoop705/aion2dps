namespace Aion2Dps.App.Demo;

/// <summary>
/// Demo <see cref="ICombatEngine"/>: an endless, animated 5-player boss scenario (kill, wipe, kill …) with boss HP
/// draining, adds, deaths, healing and damage taken; a PvP skirmish when <see cref="Mode"/> is Pvp; training runs.
/// Fully deterministic for a given seed and origin, driven only by the times passed to <see cref="GetSnapshot"/>/<see cref="Tick"/>.
/// Thread-safe. <see cref="EncounterCompleted"/> is raised on the thread that advanced time.
/// </summary>
public sealed class FakeCombatEngine : ICombatEngine
{
    private enum Phase { Waiting, Fighting, Ended }

    private readonly object _gate = new();
    private readonly IGameData _gameData;
    private readonly int _seed;
    private readonly IReadOnlyList<DemoCombatant> _players;
    private DateTime? _origin;
    private Phase _phase = Phase.Waiting;
    private DateTime _phaseStart;
    private DemoFight? _fight;
    private EncounterOutcome? _endedOutcome;
    private double _endedAt;
    private int _fightIndex;
    private int _scenarioStep;
    private int _targetIndex;
    private MeterMode _mode = MeterMode.BossOnly;
    private DateTime _pvpOrigin;

    public const double WaitSeconds = 5;
    public const double EndedSeconds = 10;

    /// <param name="raid">Use the 10-player roster instead of the 5-player party.</param>
    public FakeCombatEngine(IGameData gameData, int seed = 7, DateTime? originUtc = null, bool raid = false)
    {
        _gameData = gameData;
        _seed = seed;
        _players = raid ? DemoCombatant.Raid : DemoCombatant.Party;
        if (originUtc is { } o) Start(o);
    }

    public EngineOptions Options { get; } = new();

    public MeterMode Mode
    {
        get { lock (_gate) return _mode; }
        set
        {
            lock (_gate)
            {
                if (value == MeterMode.Pvp && _mode != MeterMode.Pvp) _pvpOrigin = _lastNow;
                _mode = value;
            }
        }
    }

    public LocalPlayerInfo? LocalPlayer
    {
        get
        {
            var p = _players.First(x => x.IsLocal);
            return new LocalPlayerInfo(p.EntityId, p.Name, FakeGameData.DemoServer, p.Class, p.Level);
        }
    }

    public event Action<EncounterRecord>? EncounterCompleted;

    private DateTime _lastNow;

    private void Start(DateTime now)
    {
        _origin = now;
        _phase = Phase.Waiting;
        _phaseStart = now;
        _pvpOrigin = now;
        _lastNow = now;
    }

    // ───────────────────────── Scenario ─────────────────────────

    private DemoFight NextFight(DateTime start)
    {
        int step = _scenarioStep++ % 3;
        int index = _fightIndex++;
        return step switch
        {
            0 => DemoFight.CreateBoss(index, start, _players, 130, null, seed: _seed),
            1 => DemoFight.CreateBoss(index, start, _players, 150, 0.38, seed: _seed),
            _ => DemoFight.CreateBoss(index, start, _players, 120, null, FakeGameData.SecondBossNpc, 600_052, _seed),
        };
    }

    /// <summary>Advances the state machine to <paramref name="now"/>; returns records to publish (outside the lock).</summary>
    private List<EncounterRecord> AdvanceLocked(DateTime now)
    {
        var completed = new List<EncounterRecord>();
        if (_origin is null) Start(now);
        if (now > _lastNow) _lastNow = now;
        for (int guard = 0; guard < 1000; guard++)
        {
            double inPhase = (now - _phaseStart).TotalSeconds;
            switch (_phase)
            {
                case Phase.Waiting when inPhase >= WaitSeconds:
                    var start = _phaseStart.AddSeconds(WaitSeconds);
                    _fight = NextFight(start);
                    _phase = Phase.Fighting;
                    _phaseStart = start;
                    _targetIndex = 0;
                    _endedOutcome = null;
                    continue;
                case Phase.Fighting when _fight is not null && inPhase >= _fight.Duration:
                    _endedOutcome = _fight.PlannedOutcome;
                    _endedAt = _fight.Duration;
                    completed.Add(_fight.BuildRecord(_gameData, _fight.Duration));
                    _phase = Phase.Ended;
                    _phaseStart = _phaseStart.AddSeconds(_fight.Duration);
                    continue;
                case Phase.Ended when inPhase >= EndedSeconds:
                    _phase = Phase.Waiting;
                    _phaseStart = _phaseStart.AddSeconds(EndedSeconds);
                    if (_fight?.Kind == EncounterKind.Training) _mode = MeterMode.BossOnly;
                    continue;
            }
            break;
        }
        return completed;
    }

    private void Publish(List<EncounterRecord> records)
    {
        foreach (var r in records)
        {
            try { EncounterCompleted?.Invoke(r); }
            catch (Exception ex) { AppLog.Error("Demo", "EncounterCompleted handler failed", ex); }
        }
    }

    public void OnEvent(GameEvent gameEvent) { /* demo engine ignores real events */ }

    public void Tick(DateTime nowUtc)
    {
        List<EncounterRecord> done;
        lock (_gate) done = AdvanceLocked(nowUtc);
        Publish(done);
    }

    public void Reset()
    {
        List<EncounterRecord> done = new();
        lock (_gate)
        {
            var now = _lastNow == default ? DateTime.UtcNow : _lastNow;
            if (_phase == Phase.Fighting && _fight is not null)
            {
                double e = (now - _phaseStart).TotalSeconds;
                if (e >= Options.MinBossFightSeconds) done.Add(_fight.BuildRecord(_gameData, e, EncounterOutcome.ManualReset));
            }
            _fight = null;
            _phase = Phase.Waiting;
            _phaseStart = now;
            _pvpOrigin = now;
            _endedOutcome = null;
        }
        Publish(done);
    }

    public void ClearSession()
    {
        lock (_gate)
        {
            _fight = null;
            _phase = Phase.Waiting;
            _phaseStart = _lastNow == default ? DateTime.UtcNow : _lastNow;
        }
    }

    public void CycleTarget(int direction)
    {
        lock (_gate)
        {
            int count = TargetsLocked(ElapsedLocked(_lastNow)).Count;
            if (count == 0) return;
            _targetIndex = ((_targetIndex + direction) % count + count) % count;
        }
    }

    public void StartTraining(TimeSpan duration)
    {
        List<EncounterRecord> done = new();
        lock (_gate)
        {
            var now = _lastNow == default ? DateTime.UtcNow : _lastNow;
            if (_phase == Phase.Fighting && _fight is not null)
            {
                double e = (now - _phaseStart).TotalSeconds;
                if (e >= Options.MinBossFightSeconds) done.Add(_fight.BuildRecord(_gameData, e, EncounterOutcome.ManualReset));
            }
            _fight = DemoFight.CreateTraining(_fightIndex++, now, _players.First(p => p.IsLocal), duration, _seed);
            _phase = Phase.Fighting;
            _phaseStart = now;
            _targetIndex = 0;
            _mode = MeterMode.AllTargets;
        }
        Publish(done);
    }

    public EncounterRecord? GetCurrentEncounter()
    {
        lock (_gate)
        {
            if (_fight is null) return null;
            double e = ElapsedLocked(_lastNow);
            return _fight.BuildRecord(_gameData, e, _phase == Phase.Ended ? _endedOutcome : null);
        }
    }

    private double ElapsedLocked(DateTime now) => _phase switch
    {
        Phase.Fighting => Math.Max(0, (now - _phaseStart).TotalSeconds),
        Phase.Ended => _endedAt,
        _ => 0,
    };

    // ───────────────────────── Snapshot ─────────────────────────

    public MeterSnapshot GetSnapshot(DateTime nowUtc)
    {
        List<EncounterRecord> done;
        MeterSnapshot snap;
        lock (_gate)
        {
            done = AdvanceLocked(nowUtc);
            snap = BuildSnapshotLocked(nowUtc);
        }
        Publish(done);
        return snap;
    }

    private MeterSnapshot BuildSnapshotLocked(DateTime now)
    {
        double t = (now - _origin!.Value).TotalSeconds;
        double ping = 34 + 7 * Math.Sin(t / 6.3) + 3 * Math.Sin(t * 1.7);
        var local = LocalPlayer;
        var baseSnap = new MeterSnapshot
        {
            TimeUtc = now,
            Mode = _mode,
            PingMs = Math.Round(ping, 1),
            LocalPlayer = local,
            MapId = _fight?.MapId ?? FakeGameData.DemoMap,
            MapName = _gameData.GetMapName(_fight?.MapId ?? FakeGameData.DemoMap),
        };

        if (_mode == MeterMode.Pvp) return BuildPvp(baseSnap, now);

        bool finishedDisplayExpired = _phase == Phase.Ended && double.IsFinite(Options.EndedDisplaySeconds)
            && Options.EndedDisplaySeconds > 0 && (now - _phaseStart).TotalSeconds >= Options.EndedDisplaySeconds;
        if (_phase == Phase.Waiting || _fight is null || finishedDisplayExpired)
            return baseSnap with { State = MeterState.WaitingForCombat, StatusText = "Waiting for combat" };

        var f = _fight;
        double e = ElapsedLocked(now);
        bool ended = _phase == Phase.Ended;
        bool allTargets = _mode == MeterMode.AllTargets;
        var rows = new List<PlayerRow>();
        double party = 0;
        for (int p = 0; p < f.Players.Count; p++) party += f.BossDamage(p, e) + (allTargets ? f.AddDamage(p, e) : 0);
        for (int p = 0; p < f.Players.Count; p++)
        {
            var pl = f.Players[p];
            if (e < pl.JoinSeconds) continue;
            double boss = f.BossDamage(p, e), dmg = boss + (allTargets ? f.AddDamage(p, e) : 0);
            double clock = Options.LivePlayerClock ? Math.Max(1, e - pl.JoinSeconds) : Math.Max(1, e);
            int hits = 0, crits = 0;
            long maxHit = 0;
            foreach (var h in f.Hits)
            {
                if (h.T > e) break;
                if (h.Player != p || (h.Flags & (HitFlags.Incoming | HitFlags.Heal | HitFlags.Dot)) != 0) continue;
                if (!allTargets && h.Target != f.TargetEntity) continue;
                hits++;
                if ((h.Flags & HitFlags.Crit) != 0) crits++;
                if (h.Amount > maxHit) maxHit = h.Amount;
            }
            rows.Add(new PlayerRow
            {
                EntityId = pl.EntityId, Name = pl.Name, Class = pl.Class, Kind = CombatantKind.Player, IsLocal = pl.IsLocal, IsPartyMember = true,
                GearScore = pl.GearScore,
                Damage = (long)dmg, Dps = dmg / clock,
                Contribution = f.MaxHp is { } max ? boss / max : party > 0 ? dmg / party : 0,
                DamageShare = party > 0 ? dmg / party : 0,
                CritRate = hits > 0 ? (double)crits / hits : 0, Hits = hits, MaxHit = maxHit,
                Healing = (long)f.Healing(p, e), DamageTaken = (long)f.Taken(p, e), IsDead = f.IsDead(p, e),
            });
        }
        rows = rows.OrderByDescending(r => r.Damage).ToList();
        double top = rows.Count > 0 ? rows[0].Damage : 0;
        rows = rows.Select((r, i) => r with { Rank = i + 1, RelativeToTop = top > 0 ? r.Damage / top : 0 }).ToList();
        var dpsList = rows.Where(r => r.Damage > 0).Select(r => r.Dps).ToList();

        var targets = TargetsLocked(e);
        var target = targets.Count == 0 ? null : targets[Math.Clamp(_targetIndex, 0, targets.Count - 1)];
        return baseSnap with
        {
            State = ended ? MeterState.Ended : MeterState.InCombat,
            StatusText = ended ? "Encounter ended" : "In combat",
            EncounterId = DemoFight.DeterministicGuid(f.StartUtc, f.Index),
            EncounterKind = f.Kind,
            Outcome = ended ? _endedOutcome : EncounterOutcome.InProgress,
            Elapsed = TimeSpan.FromSeconds(e),
            Target = target,
            Targets = targets,
            Rows = rows,
            TotalDamage = (long)party,
            PartyDps = party / Math.Max(1, e),
            MinDps = dpsList.Count > 0 ? dpsList.Min() : 0,
            AvgDps = dpsList.Count > 0 ? dpsList.Average() : 0,
            MaxDps = dpsList.Count > 0 ? dpsList.Max() : 0,
            HpCheckRatio = f.MaxHp is null || e < 3 ? null : 0.997,
            Context = f.Kind switch
            {
                EncounterKind.Boss => FightContext.DungeonBoss,
                EncounterKind.Training => FightContext.Training,
                EncounterKind.Dummy => FightContext.TrainingDummy,
                _ => FightContext.Dungeon,
            },
            Scope = _players.Count > 5 ? GroupScope.Force : GroupScope.Party,
            GroupSize = _players.Count,
        };
    }

    private List<TargetInfo> TargetsLocked(double e)
    {
        var list = new List<TargetInfo>();
        var f = _fight;
        if (f is null) return list;
        var npc = _gameData.GetNpc(f.NpcCode);
        long? hp = f.BossHp(e);
        list.Add(new TargetInfo
        {
            EntityId = f.TargetEntity, NpcCode = f.NpcCode, Name = npc?.Name ?? _gameData.GetNpcName(f.NpcCode),
            IsBoss = f.Kind == EncounterKind.Boss, IsDummy = f.Kind == EncounterKind.Training,
            Hp = hp, MaxHp = f.MaxHp, HpFraction = f.MaxHp is { } m && hp is { } h ? (double)h / m : null,
            DamageTaken = (long)f.TotalBossDamage(e), IsDead = hp == 0,
        });
        for (int a = 0; a < f.Adds.Count; a++)
        {
            var st = f.AddState(a, e);
            if (!st.Spawned) continue;
            list.Add(new TargetInfo
            {
                EntityId = f.Adds[a].EntityId, NpcCode = f.Adds[a].NpcCode, Name = _gameData.GetNpcName(f.Adds[a].NpcCode) + $" {a + 1}",
                Hp = st.Hp, MaxHp = st.Max, HpFraction = st.Hp is { } h2 ? (double)h2 / st.Max : null, DamageTaken = (long)st.Damage, IsDead = st.Hp == 0,
            });
        }
        return list;
    }

    // ───────────────────────── PvP ─────────────────────────

    private sealed record Enemy(uint Id, string Name, CharacterClass Class, string? Guild, double Join, double DealtRate, double TakenRate, double? KillAt, ushort Server);

    private static readonly Enemy[] Enemies =
    [
        new(70_001, "Draven", CharacterClass.Gladiator, "Crimson Oath", 0, 48_000, 61_000, null, 2306),
        new(70_002, "Isolde", CharacterClass.Cleric, "Crimson Oath", 2, 9_000, 22_000, null, 2306),
        new(70_003, "Korrin", CharacterClass.Ranger, null, 6, 39_000, 52_000, 96, 2307),
        new(70_004, "Maelis", CharacterClass.Assassin, "Duskmantle", 11, 57_000, 74_000, 41, 2306),
        new(70_005, "Tessaly", CharacterClass.Sorcerer, "Duskmantle", 25, 44_000, 30_000, null, 2307),
    ];

    private MeterSnapshot BuildPvp(MeterSnapshot baseSnap, DateTime now)
    {
        const double cycle = 150;
        double t = Math.Max(0, (now - _pvpOrigin).TotalSeconds) % cycle;
        var rows = new List<PvpRow>();
        int kills = 0;
        foreach (var en in Enemies)
        {
            if (t < en.Join) continue;
            double active = t - en.Join;
            bool killed = en.KillAt is { } k && t >= k;
            if (killed) kills++;
            double until = killed ? en.KillAt!.Value - en.Join : active;
            double wobble = 1 + 0.15 * Math.Sin(active / 3.1 + en.Id);
            rows.Add(new PvpRow
            {
                EntityId = en.Id, Name = en.Name, Class = en.Class, GuildName = en.Guild, ServerId = en.Server,
                DamageDealt = (long)(en.TakenRate * until * wobble),
                DamageTaken = (long)(en.DealtRate * until * (2 - wobble)),
                HpFraction = killed ? 0 : Math.Clamp(0.55 + 0.4 * Math.Sin(active / 5.0 + en.Id % 7), 0.05, 1),
                Killed = killed,
                LastActivityUtc = killed ? now.AddSeconds(-(t - en.KillAt!.Value)) : now.AddSeconds(-(en.Id % 3)),
            });
        }
        return baseSnap with
        {
            State = rows.Count > 0 ? MeterState.InCombat : MeterState.WaitingForCombat,
            StatusText = "PvP",
            EncounterKind = EncounterKind.Pvp,
            Elapsed = TimeSpan.FromSeconds(t),
            PvpRows = rows,
            PvpKills = kills,
        };
    }
}
