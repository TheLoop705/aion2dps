using Aion2Dps.Contracts;
using Aion2Dps.Protocol;
using Xunit.Abstractions;

namespace Aion2Dps.EndToEnd.Tests;

/// <summary>A fact that runs only when the named real capture exists (default paths or <c>AION2DPS_REAL_CAPTURES</c>).</summary>
public sealed class RealCaptureFactAttribute : FactAttribute
{
    public RealCaptureFactAttribute(string fileName)
    {
        if (RealCaptureReplay.Find(fileName) is null)
            Skip = $"Real capture {fileName} not found (set {RealCaptureReplay.EnvVar} to a folder or file list to run this test).";
    }
}

/// <summary>
/// Protocol-level acceptance checks against the user's real Global captures (never committed). Every server→client
/// port-13328 flow is decoded (LIVE-FINDINGS NEW 1). Asserts: no bundle errors, decode failures below 0.05 % of frames,
/// at least 99.9 % of <c>04 38</c> records effect-validated, and every engaged boss's HP check within 1 %.
/// </summary>
public class RealCaptureProtocolTests(ITestOutputHelper output)
{
    private sealed class Collector : IGameEventSink
    {
        public readonly List<GameEvent> Events = new();
        public void OnEvent(GameEvent e) => Events.Add(e);
    }

    private sealed record BossCheck(uint Entity, uint NpcCode, string Name, long? SpawnMaxHp, long? ScaledMaxHp, long StartHp, long EndHp,
        long Decoded, long SelfHeal, double Ratio);

    private sealed record Run(RealCaptureReplay.Result Replay, List<GameEvent> Events, List<BossCheck> Bosses);

    private static readonly Dictionary<string, Run> Cache = new();

    private static Run Load(string file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var cached)) return cached;
            var sink = new Collector();
            var replay = RealCaptureReplay.Run(RealCaptureReplay.Find(file)!, sink);
            var run = new Run(replay, sink.Events, BossHpChecks(sink.Events));
            Cache[file] = run;
            return run;
        }
    }

    /// <summary>
    /// HP check per boss (PROTOCOL.md §11.4), from decoded events only: window = from the last <c>00 8D</c> HP reading
    /// before the first damage to the boss up to its last reading. Expected = HP lost + boss self-heal ticks; decoded =
    /// direct damage (layouts with an amount, actor ≠ boss) + damage ticks with a player effect. The killing blow's
    /// overkill is capped at the HP it found. Only bosses that lost at least 5 % of their max HP are checked.
    /// </summary>
    private static List<BossCheck> BossHpChecks(List<GameEvent> events)
    {
        var data = Harness.GameData;
        var spawns = new Dictionary<uint, SpawnEvent>();
        foreach (var s in events.OfType<SpawnEvent>())
        {
            if (data.GetNpc(s.NpcCode) is { IsBoss: true }) spawns[s.Entity] = s;
        }

        var result = new List<BossCheck>();
        foreach (var (id, spawn) in spawns)
        {
            var hp = new List<(DateTime T, long V)>();
            long? scaled = null;
            foreach (var st in events.OfType<EntityStatsEvent>().Where(e => e.Entity == id))
            {
                if (st.CurrentHp is long v) hp.Add((st.Time, v));
                if (st.Stats64.TryGetValue(7, out long m)) scaled = m;
            }

            var damage = new List<(DateTime T, long A)>();
            var heals = new List<(DateTime T, long A)>();
            foreach (var e in events)
            {
                switch (e)
                {
                    case DamageEvent d when d.Target == id && d.Actor != id && d.Amount is long a && a > 0:
                        damage.Add((d.Time, a));
                        break;
                    case DotEvent t when t.Target == id && t.IsDamageTick && t.Amount is long a && a > 0 && t.Actor != id:
                        if (t.IsMonsterEffect) heals.Add((t.Time, a));
                        else damage.Add((t.Time, a));
                        break;
                }
            }

            if (hp.Count < 2 || damage.Count == 0) continue;
            var firstHit = damage.Min(x => x.T);
            int startIdx = hp.FindLastIndex(x => x.T < firstHit);
            if (startIdx < 0) startIdx = 0;
            var start = hp[startIdx];
            var end = hp[^1];
            long max = scaled ?? spawn.HpMax ?? start.V;
            if (start.V - end.V < max / 20) continue;

            long decoded = damage.Where(x => x.T > start.T && x.T <= end.T).Sum(x => x.A);
            long selfHeal = heals.Where(x => x.T > start.T && x.T <= end.T).Sum(x => x.A);
            if (end.V == 0 && hp.Count >= 2)
            {
                var beforeKill = hp.Count >= 2 ? hp[^2] : start;
                long lastInterval = damage.Where(x => x.T > beforeKill.T && x.T <= end.T).Sum(x => x.A);
                decoded -= Math.Max(0, lastInterval - beforeKill.V);
            }

            long expected = start.V - end.V + selfHeal;
            result.Add(new BossCheck(id, spawn.NpcCode, data.GetNpcName(spawn.NpcCode), spawn.HpMax, scaled, start.V, end.V, decoded, selfHeal,
                expected == 0 ? 0 : (double)decoded / expected));
        }

        return result;
    }

    private void Report(string file, Run run)
    {
        var d = run.Replay.Diagnostics;
        var dmg = run.Events.OfType<DamageEvent>().ToList();
        output.WriteLine($"{file}: {run.Replay.Packets:N0} packets, {run.Replay.Flows.Count} flows, {d.Frames:N0} frames, {d.Bundles:N0} bundles " +
                         $"({d.BundleErrors} errors), {d.DecodeErrors} decode errors, {d.Resyncs} resyncs, {run.Replay.Gaps} gaps, " +
                         $"{dmg.Count:N0} damage records ({dmg.Count(x => x.EffectValidated):N0} validated), {d.DamagePlaceholders} placeholders, " +
                         $"{d.DotTriggerTicks} trigger ticks, {d.DamageTrailingBytes} trailing");
        foreach (var f in run.Replay.Flows) output.WriteLine($"  flow {f.Server} -> {f.Client}: {f.Bytes:N0} bytes, {f.Gaps} gaps");
        foreach (var b in run.Bosses)
            output.WriteLine($"  boss {b.Name} ({b.NpcCode}, #{b.Entity}): spawn max {b.SpawnMaxHp:N0}, 00 8D kind 7 max {b.ScaledMaxHp:N0}, " +
                             $"HP {b.StartHp:N0} -> {b.EndHp:N0}, decoded {b.Decoded:N0}, self-heal {b.SelfHeal:N0}, ratio {b.Ratio:0.0000}");
        foreach (var e in d.GetRecentErrors().Take(20)) output.WriteLine("  error: " + e);
    }

    private void AssertAcceptance(string file)
    {
        var run = Load(file);
        Report(file, run);
        var d = run.Replay.Diagnostics;
        Assert.True(d.Frames > 1000, $"only {d.Frames} frames decoded");
        Assert.Equal(0, d.BundleErrors);
        Assert.True(d.DecodeErrors < d.Frames * 0.0005, $"{d.DecodeErrors} decode errors in {d.Frames} frames");

        var dmg = run.Events.OfType<DamageEvent>().ToList();
        Assert.NotEmpty(dmg);
        double validated = (double)dmg.Count(x => x.EffectValidated) / dmg.Count;
        Assert.True(validated >= 0.999, $"only {validated:P3} of 04 38 records effect-validated");

        foreach (var b in run.Bosses)
            Assert.True(Math.Abs(b.Ratio - 1) <= 0.01, $"HP check of {b.Name} (#{b.Entity}) is {b.Ratio:0.0000}");
    }

    [RealCaptureFact(RealCaptureReplay.ExpeditionFile)]
    public void Expedition_capture_meets_the_protocol_acceptance_criteria()
    {
        AssertAcceptance(RealCaptureReplay.ExpeditionFile);
        var run = Load(RealCaptureReplay.ExpeditionFile);

        // The instance flow and the world flow are both decoded (several concurrent connections, LIVE-FINDINGS NEW 1).
        Assert.True(run.Replay.Flows.Count(f => f.Bytes > 100_000) >= 2);

        // The three killed bosses are HP-checked, with their party-scaled max HP from 00 8D kind 7 (LIVE-FINDINGS NEW 2).
        var bosses = run.Bosses.ToDictionary(b => b.NpcCode);
        Assert.Equal(600_000, bosses[2310310].ScaledMaxHp);   // Silver Blade Rotan
        Assert.Equal(120_000, bosses[2310310].SpawnMaxHp);
        Assert.Equal(900_000, bosses[2310312].ScaledMaxHp);   // Black Smoke Murute
        Assert.Equal(180_000, bosses[2310312].SpawnMaxHp);
        Assert.Null(bosses[2310371].ScaledMaxHp);              // Kromede's Desire is not scaled: the spawn max is real
        Assert.Equal(1_800_000, bosses[2310371].SpawnMaxHp);
        foreach (var code in new uint[] { 2310310, 2310312, 2310371 }) Assert.Equal(0, bosses[code].EndHp);
        Assert.Equal(600_000, bosses[2310310].StartHp);
        Assert.Equal(900_000, bosses[2310312].StartHp);
    }

    [RealCaptureFact(RealCaptureReplay.LiveFile)]
    public void Live_recording_meets_the_protocol_acceptance_criteria() => AssertAcceptance(RealCaptureReplay.LiveFile);

    [RealCaptureFact(RealCaptureReplay.ExpeditionFile)]
    public void Expedition_capture_has_no_unexplained_damage_bytes()
    {
        var run = Load(RealCaptureReplay.ExpeditionFile);
        var d = run.Replay.Diagnostics;
        Assert.Equal(0, d.DamageTrailingBytes);
        Assert.Equal(0, d.DecodeErrors);
        // The real-traffic findings are all exercised: max-HP placeholders, dodge/immunity trigger ticks, evades.
        Assert.True(d.DamagePlaceholders > 0);
        Assert.True(d.DotTriggerTicks > 0);
        Assert.Contains(run.Events.OfType<DamageEvent>(), e => e.Layout == 2 && e.DamageType == 1);
        // 2A 38 / 2B 38 decode with plausible fields (the old 2B 38 reading was misaligned).
        var buffs = run.Events.OfType<BuffAppliedEvent>().ToList();
        Assert.True(buffs.Count > 4000);
        Assert.All(buffs, b => Assert.InRange(b.ExpiryUnixMs, 1_700_000_000_000UL, 4_102_444_800_000UL));
    }
}
