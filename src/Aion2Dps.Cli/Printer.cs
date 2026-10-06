using System.Globalization;
using Aion2Dps.Contracts;

namespace Aion2Dps.Cli;

/// <summary>Console formatting helpers (culture-invariant).</summary>
internal static class Printer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string N(long v) => v.ToString("N0", Inv);
    public static string N(double v, string fmt = "N0") => v.ToString(fmt, Inv);

    public static string Abbrev(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1e9) return (v / 1e9).ToString("0.00", Inv) + "B";
        if (a >= 1e6) return (v / 1e6).ToString("0.00", Inv) + "M";
        if (a >= 1e4) return (v / 1e3).ToString("0.0", Inv) + "K";
        return v.ToString("0", Inv);
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", Inv) : t.ToString(@"m\:ss\.f", Inv);
    }

    public static string Pct(double ratio) => (ratio * 100).ToString("0.0", Inv) + "%";

    /// <summary>One line per game flow of a replay (capture side) plus the protocol counters of each flow.</summary>
    public static void Flows(TextWriter w, Aion2Dps.Capture.ReplayStatistics stats, Aion2Dps.Protocol.MultiFlowProtocolPipeline protocol)
    {
        if (stats.FlowDetails.Count == 0) return;
        w.WriteLine($"Game flows ({stats.FlowDetails.Count}, at most {stats.MaxConcurrentFlows} open at the same time):");
        foreach (var f in stats.FlowDetails)
        {
            w.WriteLine($"  {f.ServerEndpoint,-22} -> {f.LocalEndpoint,-22} {f.LockedAtUtc:HH:mm:ss.fff} → {(f.LastPacketUtc is { } l ? l.ToString("HH:mm:ss.fff", Inv) : "—")}  " +
                        $"{N(f.Packets),8} pkts {N(f.BytesDelivered),12} bytes {f.Gaps,3} gaps{(f.EndReason is null ? "" : "  ended: " + f.EndReason)}");
        }

        if (stats.DroppedBytes > 0) w.WriteLine($"  {N(stats.DroppedBytes)} bytes of other flows were dropped by a single-stream sink.");
        FlowCounters(w, protocol);
    }

    /// <summary>Per-flow protocol counters (frames, events, decode errors) of the multi-flow pipeline.</summary>
    public static void FlowCounters(TextWriter w, Aion2Dps.Protocol.MultiFlowProtocolPipeline protocol)
    {
        var flows = protocol.GetFlowStats().Where(f => f.BytesIn > 0 || f.FlowKey != Aion2Dps.Protocol.MultiFlowProtocolPipeline.DefaultFlowKey).ToList();
        if (flows.Count == 0) return;
        w.WriteLine("Protocol per flow:");
        foreach (var f in flows)
            w.WriteLine($"  #{f.CreatedOrder} {f.FlowKey,-46} {N(f.BytesIn),12} bytes {N(f.Frames),9} frames {N(f.Bundles),6} bundles {N(f.Events),9} events " +
                        $"{N(f.DecodeErrors),5} decode errors {f.Resyncs,3} resyncs {f.TcpGaps,3} gaps{(f.IsOpen ? "  (open)" : "")}");
    }

    /// <summary>Multi-line summary of a completed encounter.</summary>
    public static void Encounter(TextWriter w, EncounterRecord r, IGameData gd, int index)
    {
        string target = r.Bosses.Count > 1
            ? string.Join(" + ", r.Bosses.OrderBy(b => b.EngagedSeconds).Select(b => b.NpcCode is uint bc ? gd.GetNpcName(bc) : $"#{b.EntityId}"))
            : r.BossNpcCode is uint code ? gd.GetNpcName(code) : r.Kind == EncounterKind.Pvp ? "enemy players" : "multiple targets";
        string map = r.MapId is uint m ? gd.GetMapName(m) ?? $"map {m}" : "unknown map";
        w.WriteLine();
        w.WriteLine($"Encounter #{index}  {r.Kind} · {target} · {r.Outcome} · {Duration(r.DurationSeconds)} · {map}");
        w.WriteLine($"  {r.StartUtc:yyyy-MM-dd HH:mm:ss.fff} → {r.EndUtc:HH:mm:ss.fff} UTC   total {N(r.TotalDamage)}   party DPS {Abbrev(r.PartyDps)}" +
                    (r.ResetCount > 0 ? $"   resets {r.ResetCount}" : "") + (r.CaptureGaps ? "   [capture gaps]" : ""));
        if (r.BossMaxHp is long max)
            w.WriteLine($"  boss max HP {N(max)}   HP start {(r.BossHpStart is long s ? N(s) : "—")}   HP end {(r.BossHpEnd is long e ? N(e) : "—")}");
        if (r.HpCheck is { } hc)
            w.WriteLine($"  HP check: {(hc.Passed ? "PASSED" : "FAILED")}  ratio {N(hc.Ratio, "0.0000")}  decoded {N(hc.DecodedDamage)}  HP lost {N(hc.HpLost)}  boss self-heal {N(hc.BossSelfHealing)}" +
                        (string.IsNullOrEmpty(hc.Note) ? "" : $"  ({hc.Note})"));
        else
            w.WriteLine("  HP check: n/a");
        if (r.Bosses.Count > 1)
        {
            foreach (var b in r.Bosses.OrderBy(b => b.EngagedSeconds))
            {
                string name = b.NpcCode is uint bc ? gd.GetNpcName(bc) : $"#{b.EntityId}";
                string check = b.HpCheck is { } bh
                    ? $"{(bh.Passed ? "PASSED" : "FAILED")} {N(bh.Ratio, "0.0000")} ({N(bh.DecodedDamage)} / {N(bh.HpLost)}{(bh.Overkill > 0 ? $", {N(bh.Overkill)} overkill" : "")})"
                    : "n/a";
                w.WriteLine($"    {(b.IsPrimary ? "*" : " ")}{name,-28} max {(b.MaxHp is long bm ? N(bm) : "—"),11}  engaged {b.EngagedSeconds,6:0.0}s  " +
                            $"{(b.Killed && b.KillTimeSeconds is double kt ? $"killed {kt,6:0.0}s" : "not killed    ")}  taken {N(b.DamageTaken),11}  HP check {check}");
            }
            if (r.OverallHpCheck is { } oh)
                w.WriteLine($"  Overall HP check: {(oh.Passed ? "PASSED" : "FAILED")}  ratio {N(oh.Ratio, "0.0000")}  decoded {N(oh.DecodedDamage)}  HP lost {N(oh.HpLost)}");
        }

        w.WriteLine($"  {"Player",-18} {"Class",-13} {"Damage",14} {"DPS",10} {"Share",7} {"Crit",7} {"Hits",6} {"Heal",11} {"Taken",11} {"Deaths",6}");
        foreach (var c in r.Combatants.OrderBy(c => c.Kind == CombatantKind.EnemyPlayer).ThenByDescending(c => c.Damage))
        {
            string name = (c.IsLocal ? "*" : " ") + c.Name;
            if (name.Length > 18) name = name[..18];
            string crit = c.Quality.Hits > 0 ? Pct((double)c.Quality.Crits / c.Quality.Hits) : "—";
            string kind = c.Kind == CombatantKind.EnemyPlayer ? " (enemy)" : "";
            w.WriteLine($"  {name,-18} {gd.GetClassName(c.Class),-13} {N(c.Damage),14} {Abbrev(c.Dps),10} {Pct(c.DamageShare),7} {crit,7} {c.Quality.Hits,6} {N(c.Healing),11} {N(c.DamageTaken),11} {c.Deaths,6}{kind}");
        }
    }
}
