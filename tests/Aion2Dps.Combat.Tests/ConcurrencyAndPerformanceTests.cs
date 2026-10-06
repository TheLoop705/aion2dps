using System.Diagnostics;
using Aion2Dps.Contracts;
using Xunit.Abstractions;

namespace Aion2Dps.Combat.Tests;

[CollectionDefinition("Timing", DisableParallelization = true)]
public class TimingCollection { }

[Collection("Timing")]
public class ConcurrencyAndPerformanceTests
{
    private readonly ITestOutputHelper _out;

    public ConcurrencyAndPerformanceTests(ITestOutputHelper output) => _out = output;

    private static readonly uint[] ClassCodes = { 6, 10, 14, 18, 22, 26, 30, 34, 46, 6 };
    private static readonly uint[] ClassSkills = { 11020030, 12020030, 14020030, 13020030, 16020030, 15020030, 17020030, 18020030, 19020030, 11030030 };

    /// <summary>10 players, a boss and 49 adds; returns a generator of damage/notice/HP events for one raid fight.</summary>
    private static (Script S, List<GameEvent> Events, long ExpectedBossDamage) Raid(int count, double seconds)
    {
        var s = Script.Standard(new EngineOptions { CountAddsInBossFight = true });
        for (int p = 0; p < 10; p++) s.Player(0, 1000u + (uint)p, $"P{p}", ClassCodes[p]);
        for (int n = 0; n < 49; n++) s.SpawnNpc(0, 20_000u + (uint)n, FakeGameData.AddCode, 1_000_000, 1_000_000);
        s.SpawnNpc(0, 30_000, FakeGameData.BossCode, 9_000_000_000, 9_000_000_000);

        var events = new List<GameEvent>(count);
        var rng = new Random(42);
        long bossDamage = 0;
        long bossHp = 9_000_000_000;
        for (int i = 0; i < count; i++)
        {
            var t = Script.At(1 + seconds * i / count);
            int p = i % 10;
            uint actor = 1000u + (uint)p;
            uint target = i % 3 == 0 ? 30_000u : 20_000u + (uint)rng.Next(49);
            uint skill = ClassSkills[p] + (uint)(i % 4) * 10;
            if (i % 4 == 3)
            {
                events.Add(new DamageEvent { Time = t, Actor = actor, Target = actor, SkillRaw = skill, SkillId = skill, Layout = 0, PowerScalar = 10_000 + (uint)p });
                continue;
            }
            if (i % 50 == 1)
            {
                events.Add(new EntityStatsEvent { Time = t, Entity = 30_000, Format = 2, CurrentHp = bossHp });
                continue;
            }
            long amount = 1000 + rng.Next(5000);
            if (target == 30_000) { bossDamage += amount; bossHp -= amount; }
            events.Add(new DamageEvent
            {
                Time = t, Actor = actor, Target = target, SkillRaw = skill, SkillId = skill, Layout = 6, Switch = 6,
                DamageType = (byte)(rng.Next(4) == 0 ? 3 : 2), Mods = rng.Next(5) == 0 ? HitMods.Perfect : HitMods.None,
                Direction = (HitDirection)rng.Next(3), HitTag = (byte)i, HitIndex = 1, PowerScalar = 10_000 + (uint)p, Amount = amount,
                EffectId = skill * 100 + 11, EffectValidated = true,
            });
        }
        return (s, events, bossDamage);
    }

    [Fact]
    public void Events_on_one_thread_snapshots_on_another()
    {
        var (s, events, expectedBoss) = Raid(20_000, 20);
        var errors = new List<Exception>();
        using var done = new ManualResetEventSlim();

        var producer = new Thread(() =>
        {
            try { foreach (var e in events) s.Engine.OnEvent(e); }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
            finally { done.Set(); }
        });
        int snapshots = 0;
        var consumer = new Thread(() =>
        {
            try
            {
                // do/while: on a loaded machine the producer can finish before this thread is scheduled.
                do
                {
                    var snap = s.Engine.GetSnapshot(Script.At(10));
                    _ = snap.Rows.Sum(r => r.Damage);
                    if (snapshots % 10 == 0) _ = s.Engine.GetCurrentEncounter()?.Hits.Count;
                    if (snapshots % 7 == 0) s.Engine.CycleTarget(1);
                    if (snapshots % 13 == 0) s.Engine.Mode = snapshots % 2 == 0 ? MeterMode.AllTargets : MeterMode.BossOnly;
                    snapshots++;
                } while (!done.IsSet);
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        });
        producer.Start();
        consumer.Start();
        Assert.True(producer.Join(TimeSpan.FromSeconds(60)));
        Assert.True(consumer.Join(TimeSpan.FromSeconds(60)));

        Assert.Empty(errors);
        Assert.Equal(0, s.Engine.EventErrors);
        Assert.True(snapshots > 0);
        var rec = s.Record();
        Assert.Equal(expectedBoss, rec.Combatants.Sum(c => c.BossDamage));
        Assert.Equal(10, rec.Combatants.Count(c => c.Kind == CombatantKind.Player && c.Damage > 0));
    }

    [Fact]
    public void Sustained_event_rate_and_snapshot_cost()
    {
        const int count = 30_000; // 10 s at 3,000 events/s
        var (s, events, _) = Raid(count, 10);

        // warm-up on a separate engine so JIT cost is not measured
        var (w, warm, _) = Raid(3_000, 1);
        foreach (var e in warm) w.Engine.OnEvent(e);
        _ = w.Engine.GetSnapshot(Script.At(2));

        var sw = Stopwatch.StartNew();
        foreach (var e in events) s.Engine.OnEvent(e);
        sw.Stop();
        _out.WriteLine($"{count} events in {sw.Elapsed.TotalMilliseconds:0.0} ms ({sw.Elapsed.TotalMilliseconds / 10000 * 100:0.00} % of one core over 10 s)");
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"processing took {sw.Elapsed.TotalMilliseconds} ms");

        var snap = s.Engine.GetSnapshot(Script.At(11));
        Assert.Equal(10, snap.Rows.Count(r => r.Kind == CombatantKind.Player && r.Damage > 0));
        Assert.Equal(50, snap.Targets.Count);

        const int n = 500;
        sw.Restart();
        for (int i = 0; i < n; i++) _ = s.Engine.GetSnapshot(Script.At(11));
        sw.Stop();
        double perSnapshot = sw.Elapsed.TotalMilliseconds / n;
        _out.WriteLine($"GetSnapshot: {perSnapshot:0.000} ms");
        Assert.True(perSnapshot < 2.0, $"snapshot took {perSnapshot} ms");
    }
}
