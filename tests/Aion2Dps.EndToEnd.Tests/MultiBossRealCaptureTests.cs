using Aion2Dps.Capture;
using Aion2Dps.Combat;
using Aion2Dps.Contracts;
using Xunit.Abstractions;

namespace Aion2Dps.EndToEnd.Tests;

/// <summary>
/// Replays the user's real Fire Temple expedition capture (when it exists on this machine) through the real
/// Capture → Protocol → Combat chain and checks the multi-boss segmentation. The capture contains player names, so it
/// is never copied into the repository: the test silently passes when the file is absent (CI, other machines).
/// Override the location with the AION2DPS_EXPEDITION_PCAP environment variable.
/// </summary>
public sealed class MultiBossRealCaptureTests
{
    private const uint Rotan = 2310310;
    private const uint Ignus = 2310311;
    private const uint Murute = 2310312;
    private const uint Kromede = 2310371;

    private readonly ITestOutputHelper _out;

    public MultiBossRealCaptureTests(ITestOutputHelper output) => _out = output;

    private static string? CapturePath()
    {
        var env = Environment.GetEnvironmentVariable("AION2DPS_EXPEDITION_PCAP");
        if (string.IsNullOrWhiteSpace(env)) return RealCaptureReplay.Find(RealCaptureReplay.ExpeditionFile);
        return File.Exists(env) ? env : null;
    }

    [Fact]
    public void Expedition_capture_gives_one_multi_boss_encounter_then_kromede()
    {
        var path = CapturePath();
        if (path is null)
        {
            _out.WriteLine("Expedition capture not present on this machine: skipped.");
            return;
        }

        var h = new Harness();
        DateTime last = default;
        var stats = PcapReplaySource.Replay(path, h.Input, 0, utc =>
        {
            if ((utc - last).TotalMilliseconds < 250) return;
            last = utc;
            h.Engine.Tick(utc);
        });
        h.Drain(stats.LastPacketUtc ?? last);

        var gd = Harness.GameData;
        _out.WriteLine($"{stats.Packets} packets, {stats.Locks} locks, {h.Pipeline.Diagnostics.DecodeErrors} decode errors, {h.Records.Count} encounters");
        foreach (var r in h.Records) Describe(r, gd);

        var bossFights = h.Records.Where(r => r.Kind == EncounterKind.Boss).ToList();
        Assert.Equal(2, bossFights.Count);

        var multi = bossFights[0];
        Assert.Equal(EncounterOutcome.Kill, multi.Outcome);
        Assert.Equal(new[] { Rotan, Murute }, multi.Bosses.Select(b => b.NpcCode ?? 0).ToArray());
        Assert.Equal(Murute, multi.BossNpcCode); // primary = largest max HP
        Assert.Equal(900_000, multi.BossMaxHp);
        var rotan = multi.Bosses[0];
        var murute = multi.Bosses[1];
        Assert.Equal(600_000, rotan.MaxHp);
        Assert.Equal(900_000, murute.MaxHp);
        Assert.True(rotan.Killed && murute.Killed);
        Assert.InRange(rotan.HpCheck!.Ratio, 0.99, 1.01);
        Assert.InRange(murute.HpCheck!.Ratio, 0.99, 1.01);
        Assert.InRange(multi.OverallHpCheck!.Ratio, 0.99, 1.01);
        Assert.DoesNotContain(multi.Bosses, b => b.NpcCode == Ignus);
        Assert.DoesNotContain(multi.Combatants, c => c.Kind == CombatantKind.Player && c.Name.StartsWith("Player ", StringComparison.Ordinal));
        // Contribution over both bosses: the shares add up to (about) 100 % on a full kill of both.
        double sum = multi.Combatants.Sum(c => c.Contribution);
        Assert.InRange(sum, 0.99, 1.02);

        var kromede = bossFights[1];
        Assert.Equal(EncounterOutcome.Kill, kromede.Outcome);
        Assert.Equal(Kromede, kromede.BossNpcCode);
        Assert.Single(kromede.Bosses);
        Assert.InRange(kromede.HpCheck!.Ratio, 0.99, 1.01);
    }

    [Fact]
    public void Boss_fights_only_keeps_the_boss_encounters_identical_and_drops_trash()
    {
        var path = CapturePath();
        if (path is null)
        {
            _out.WriteLine("Expedition capture not present on this machine: skipped.");
            return;
        }

        List<EncounterRecord> Run(bool bossOnly)
        {
            var h = new Harness(new EngineOptions { SaveTrashFights = true, BossFightsOnly = bossOnly });
            DateTime last = default;
            var stats = PcapReplaySource.Replay(path, h.Input, 0, utc =>
            {
                if ((utc - last).TotalMilliseconds < 250) return;
                last = utc;
                h.Engine.Tick(utc);
            });
            h.Drain(stats.LastPacketUtc ?? last);
            return h.Records;
        }

        var all = Run(bossOnly: false);
        var bossOnly = Run(bossOnly: true);
        Assert.Contains(all, r => r.Kind == EncounterKind.Trash);
        Assert.DoesNotContain(bossOnly, r => r.Kind == EncounterKind.Trash);

        var expected = all.Where(r => r.Kind != EncounterKind.Trash).ToList();
        Assert.Equal(expected.Count, bossOnly.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var a = expected[i];
            var b = bossOnly[i];
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Outcome, b.Outcome);
            Assert.Equal(a.StartUtc, b.StartUtc);
            Assert.Equal(a.EndUtc, b.EndUtc);
            Assert.Equal(a.TotalDamage, b.TotalDamage);
            Assert.Equal(a.Hits.Count, b.Hits.Count);
            Assert.Equal(a.HpCheck?.Ratio, b.HpCheck?.Ratio);
            Assert.Equal(a.OverallHpCheck?.Ratio, b.OverallHpCheck?.Ratio);
            Assert.Equal(a.Bosses.Select(x => (x.NpcCode, x.MaxHp, x.Killed, x.HpCheck?.Ratio)),
                b.Bosses.Select(x => (x.NpcCode, x.MaxHp, x.Killed, x.HpCheck?.Ratio)));
            Assert.Equal(a.Combatants.Select(c => (c.Name, c.Damage, c.Healing, c.DamageTaken)),
                b.Combatants.Select(c => (c.Name, c.Damage, c.Healing, c.DamageTaken)));
        }
    }

    private void Describe(EncounterRecord r, IGameData gd)
    {
        _out.WriteLine("");
        _out.WriteLine($"{r.Kind} {r.Outcome} {r.StartUtc:HH:mm:ss.fff} → {r.EndUtc:HH:mm:ss.fff} ({r.DurationSeconds:0.0} s) total {r.TotalDamage:N0} " +
                       $"primary {(r.BossNpcCode is uint b ? gd.GetNpcName(b) : "-")} max {r.BossMaxHp:N0} ratio {r.HpCheck?.Ratio:0.0000} overall {r.OverallHpCheck?.Ratio:0.0000}");
        foreach (var boss in r.Bosses)
        {
            _out.WriteLine($"  boss {(boss.NpcCode is uint c ? gd.GetNpcName(c) : "?")} #{boss.EntityId} primary={boss.IsPrimary} max {boss.MaxHp:N0} " +
                           $"start {boss.HpStart:N0} end {boss.HpEnd:N0} killed={boss.Killed} kill@{boss.KillTimeSeconds:0.0}s engaged@{boss.EngagedSeconds:0.0}s " +
                           $"taken {boss.DamageTaken:N0} check {boss.HpCheck?.Ratio:0.0000} ({boss.HpCheck?.DecodedDamage:N0} vs {boss.HpCheck?.HpLost:N0}; {boss.HpCheck?.Note}) samples {boss.HpTimeline.Count}");
            foreach (var d in boss.DamageByCombatant)
                _out.WriteLine($"     {Name(r, d.EntityId),-16} {d.Damage,10:N0}  {d.Contribution:P1}");
        }
        foreach (var c in r.Combatants)
            _out.WriteLine($"  {c.Name,-16} {c.Kind,-14} {c.Class,-12} dmg {c.Damage,10:N0} bosses {c.AllBossesDamage,10:N0} contrib {c.Contribution:P1} share {c.DamageShare:P1}");
        var sources = r.Hits.Where(x => x.Source != x.Actor && (x.Flags & HitFlags.Incoming) == 0 && (x.Flags & HitFlags.Heal) == 0)
            .GroupBy(x => (x.Source, x.Actor)).Select(g => (g.Key.Source, g.Key.Actor, Dmg: g.Sum(x => x.Amount))).OrderByDescending(x => x.Dmg).Take(15);
        foreach (var (src, actor, dmg) in sources)
        {
            var skills = string.Join(",", r.Hits.Where(x => x.Source == src && x.Actor == actor).Select(x => x.Skill).Distinct().Take(4));
            _out.WriteLine($"    summon #{src} → {(actor == CombatEngine.UnknownSummonsEntityId ? "Unknown summons" : Name(r, actor))}: {dmg:N0} skills {skills}");
        }
        foreach (var boss in r.Bosses)
        {
            // HP rises not explained by recorded self-heals, and the last hits around the kill (overkill diagnostics).
            var tl = boss.HpTimeline;
            for (int i = 1; i < tl.Count; i++)
                if (tl[i].Hp > tl[i - 1].Hp) _out.WriteLine($"    HP up #{boss.EntityId} at {tl[i].T:0.00}s: {tl[i - 1].Hp:N0} → {tl[i].Hp:N0}");
            if (Environment.GetEnvironmentVariable("AION2DPS_WINDOWS") == "1")
            {
                long cum = 0;
                for (int i = 1; i < tl.Count; i++)
                {
                    long dmg = r.Hits.Where(x => x.Target == boss.EntityId && x.Amount > 0 && (x.Flags & (HitFlags.Heal | HitFlags.Incoming)) == 0
                                                 && x.T > tl[i - 1].T && x.T <= tl[i].T).Sum(x => x.Amount);
                    long drop = tl[i - 1].Hp - tl[i].Hp;
                    cum += dmg - drop;
                    if (Math.Abs(dmg - drop) > 300) _out.WriteLine($"    win #{boss.EntityId} {tl[i - 1].T:0.00}-{tl[i].T:0.00}s dmg {dmg:N0} drop {drop:N0} diff {dmg - drop:N0} cum {cum:N0}");
                }
            }
            if (tl.Count >= 3) _out.WriteLine($"    last HP samples #{boss.EntityId}: " + string.Join(" | ", tl.Skip(tl.Count - 4).Select(x => $"{x.T:0.00}s {x.Hp:N0}")));
            if (boss.KillTimeSeconds is double kt)
            {
                var near = r.Hits.Where(x => x.Target == boss.EntityId && x.Amount > 0 && (x.Flags & (HitFlags.Heal | HitFlags.Incoming)) == 0 && x.T >= kt - 1.5 && x.T <= kt + 2.5);
                foreach (var x in near) _out.WriteLine($"      {x.T:0.000}s {Name(r, x.Actor)} skill {x.Skill} {x.Amount:N0} {x.Flags}");
            }
        }
    }

    private static string Name(EncounterRecord r, uint id) => r.Combatants.FirstOrDefault(c => c.EntityId == id)?.Name ?? $"#{id}";
}
