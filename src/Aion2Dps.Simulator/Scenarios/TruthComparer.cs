using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator;

/// <summary>
/// Compares an <see cref="EncounterRecord"/> produced by the real pipeline (protocol → combat engine) with the exact
/// ground truth of a scenario encounter. Used by the end-to-end tests and by <c>aion2dps-cli selftest</c>.
/// Every returned string is one mismatch; an empty list means the record matches exactly.
/// </summary>
public static class TruthComparer
{
    /// <param name="expected">The scenario's expected encounter.</param>
    /// <param name="record">The record produced by the engine.</param>
    /// <param name="scenarioStartUtc">The UTC time the scenario stream started at (StreamGenerator start).</param>
    /// <param name="checkTimes">Compare StartUtc/EndUtc/DurationSeconds (exact to 1 ms).</param>
    public static IReadOnlyList<string> Compare(ExpectedEncounter expected, EncounterRecord record, DateTime scenarioStartUtc, bool checkTimes = true)
    {
        var errors = new List<string>();
        void Eq<T>(string what, T exp, T act)
        {
            if (!EqualityComparer<T>.Default.Equals(exp, act)) errors.Add($"{what}: expected {exp}, got {act}");
        }

        Eq("Kind", expected.Kind, record.Kind);
        Eq("Outcome", expected.Outcome, record.Outcome);
        if (checkTimes)
        {
            var expStart = scenarioStartUtc + expected.FirstHit;
            var expEnd = scenarioStartUtc + expected.LastHit;
            if (Math.Abs((record.StartUtc - expStart).TotalMilliseconds) > 1) errors.Add($"StartUtc: expected {expStart:HH:mm:ss.fff}, got {record.StartUtc:HH:mm:ss.fff}");
            if (Math.Abs((record.EndUtc - expEnd).TotalMilliseconds) > 1) errors.Add($"EndUtc: expected {expEnd:HH:mm:ss.fff}, got {record.EndUtc:HH:mm:ss.fff}");
            if (Math.Abs(record.DurationSeconds - expected.DurationSeconds) > 0.001) errors.Add($"DurationSeconds: expected {expected.DurationSeconds:0.000}, got {record.DurationSeconds:0.000}");
        }

        Eq("TotalDamage", expected.TotalDamage, record.TotalDamage);
        if (expected.BossEntityId is not null)
        {
            Eq("BossEntityId", expected.BossEntityId, record.BossEntityId);
            Eq("BossNpcCode", expected.BossNpcCode, record.BossNpcCode);
            Eq("BossMaxHp", expected.BossMaxHp, record.BossMaxHp);
        }

        if (expected.Kind == EncounterKind.Boss)
        {
            if (record.HpCheck is not { } hc) errors.Add("HpCheck: missing");
            else
            {
                if (!hc.Passed) errors.Add($"HpCheck: not passed (ratio {hc.Ratio:0.0000}, decoded {hc.DecodedDamage:N0}, lost {hc.HpLost:N0}, self-heal {hc.BossSelfHealing:N0}, {hc.Note})");
                Eq("HpCheck.BossSelfHealing", expected.BossSelfHealing, hc.BossSelfHealing);
                if (expected.BossHpLost is long lost) Eq("HpCheck.HpLost", lost, hc.HpLost);
            }
        }
        else if (record.HpCheck is { Passed: false } hc2)
        {
            errors.Add($"HpCheck: not passed (ratio {hc2.Ratio:0.0000}, {hc2.Note})");
        }

        foreach (var exp in expected.Players.Values)
        {
            bool expectRow = exp.Damage > 0 || exp.Healing > 0;
            var c = record.Combatants.FirstOrDefault(x => x.EntityId == exp.EntityId);
            if (c is null)
            {
                if (expectRow) errors.Add($"{exp.Name}: missing from the record");
                continue;
            }

            string p = exp.Name;
            Eq($"{p}.Name", exp.Name, c.Name);
            Eq($"{p}.Kind", exp.Kind, c.Kind);
            Eq($"{p}.Class", exp.Class, c.Class);
            Eq($"{p}.IsLocal", exp.IsLocal, c.IsLocal);
            Eq($"{p}.Damage", exp.Damage, c.Damage);
            if (exp.Kind == CombatantKind.EnemyPlayer) continue;
            Eq($"{p}.Healing", exp.Healing, c.Healing);
            Eq($"{p}.Hits", exp.Hits, c.Quality.Hits);
            Eq($"{p}.Crits", exp.Crits, c.Quality.Crits);
            Eq($"{p}.DotTicks", exp.DotTicks, c.Quality.DotTicks);
            Eq($"{p}.DotDamage", exp.DotDamage, c.Quality.DotDamage);
            Eq($"{p}.MultiHits", exp.MultiHitRecords, c.Quality.MultiHits);
            Eq($"{p}.ExtraHitCount", exp.ExtraHits, c.Quality.ExtraHitCount);
            Eq($"{p}.Back", exp.BackHits, c.Quality.Back);
            Eq($"{p}.Front", exp.FrontHits, c.Quality.Front);
            Eq($"{p}.Perfect", exp.PerfectHits, c.Quality.Perfect);
            Eq($"{p}.Double", exp.DoubleHits, c.Quality.Double);
            long summon = c.Skills.Where(s => s.FromSummon).Sum(s => s.Damage);
            Eq($"{p}.SummonDamage", exp.SummonDamage, summon);
            if (expected.BossEntityId is not null) Eq($"{p}.BossDamage", exp.BossDamage, c.BossDamage);
            Eq($"{p}.Deaths", exp.Deaths, c.Deaths);
        }

        foreach (var c in record.Combatants)
        {
            if (c.Kind == CombatantKind.UnknownSummons && (c.Damage > 0 || c.Healing > 0))
                errors.Add($"Unknown summons bucket holds {c.Damage:N0} damage (summon owner not resolved)");
            else if (!expected.Players.ContainsKey(c.EntityId) && c.Damage > 0)
                errors.Add($"Unexpected combatant {c.Name} ({c.EntityId}) with {c.Damage:N0} damage");
        }

        return errors;
    }

    /// <summary>Compares the list of completed encounters with the scenario truth (count, then each in order).</summary>
    public static IReadOnlyList<string> CompareAll(Scenario scenario, IReadOnlyList<EncounterRecord> records, DateTime scenarioStartUtc, bool checkTimes = true)
    {
        var errors = new List<string>();
        var expected = scenario.Truth.Encounters;
        if (records.Count != expected.Count)
            errors.Add($"Encounter count: expected {expected.Count} ({string.Join(", ", expected.Select(e => $"{e.Kind}/{e.Outcome}"))}), got {records.Count} ({string.Join(", ", records.Select(r => $"{r.Kind}/{r.Outcome}"))})");
        for (int i = 0; i < Math.Min(records.Count, expected.Count); i++)
            foreach (var e in Compare(expected[i], records[i], scenarioStartUtc, checkTimes))
                errors.Add($"#{i} {e}");
        return errors;
    }
}
