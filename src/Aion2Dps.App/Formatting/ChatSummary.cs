using System.Text;

namespace Aion2Dps.App.Formatting;

/// <summary>
/// Builds the one-line "copy to chat" summary, e.g. <c>[Boss] 3:45 · Name 554.6K (34.8%) · Name2 484.9K (30.4%)</c>.
/// The line never exceeds <see cref="MaxChatLength"/> characters: players that do not fit are summarised as "+N".
/// </summary>
public static class ChatSummary
{
    public const int MaxChatLength = 255;
    private const int MaxTitleLength = 40;
    private const string Sep = " · ";

    public readonly record struct Entry(string Name, double Dps, double? Contribution);

    /// <summary>Summary of the live (or last ended) snapshot. Returns null when there is nothing to report.</summary>
    public static string? FromSnapshot(MeterSnapshot snapshot, int maxLength = MaxChatLength)
    {
        if (snapshot.Mode == MeterMode.Pvp)
        {
            if (snapshot.PvpRows.Count == 0) return null;
            long dealt = snapshot.PvpRows.Sum(r => r.DamageDealt), taken = snapshot.PvpRows.Sum(r => r.DamageTaken);
            var head = $"[PvP] {Fmt.Duration(snapshot.Elapsed)}{Sep}{snapshot.PvpKills} kill{(snapshot.PvpKills == 1 ? "" : "s")}{Sep}dealt {Fmt.Abbrev1(dealt)}{Sep}taken {Fmt.Abbrev1(taken)}";
            var parts = snapshot.PvpRows.OrderByDescending(r => r.DamageDealt)
                .Select(r => $"{r.Name} ↑{Fmt.Abbrev1(r.DamageDealt)} ↓{Fmt.Abbrev1(r.DamageTaken)}{(r.Killed ? " KO" : "")}");
            return Join(head, parts.ToList(), maxLength);
        }

        var rows = snapshot.Rows.Where(r => r.Kind != CombatantKind.EnemyPlayer && r.Damage > 0).ToList();
        if (rows.Count == 0) return null;
        string title = snapshot.Mode == MeterMode.AllTargets
            ? "All targets"
            : snapshot.Target?.Name is { Length: > 0 } n ? n : "Encounter";
        if (snapshot.EncounterKind == Contracts.EncounterKind.Training) title = "Training";
        var entries = rows.OrderByDescending(r => r.Dps).Select(r => new Entry(r.Name, r.Dps, r.Contribution));
        return Format(title, snapshot.Elapsed, entries, maxLength);
    }

    /// <summary>Summary of a saved encounter (encounter DPS and contribution).</summary>
    public static string? FromRecord(EncounterRecord record, IGameData? gameData, int maxLength = MaxChatLength)
    {
        var combatants = record.Combatants.Where(c => c.Kind == CombatantKind.Player && c.Damage > 0).ToList();
        if (combatants.Count == 0) return null;
        string title = record.Kind switch
        {
            Contracts.EncounterKind.Training => "Training",
            Contracts.EncounterKind.Pvp => "PvP",
            _ when record.BossNpcCode is { } code && gameData is not null => gameData.GetNpcName(code),
            _ => "Encounter",
        };
        return Format(title, TimeSpan.FromSeconds(record.DurationSeconds),
            combatants.OrderByDescending(c => c.Dps).Select(c => new Entry(c.Name, c.Dps, c.Contribution)), maxLength);
    }

    public static string Format(string title, TimeSpan elapsed, IEnumerable<Entry> entries, int maxLength = MaxChatLength)
    {
        title = Clean(title);
        if (title.Length > MaxTitleLength) title = title[..(MaxTitleLength - 1)] + "…";
        string head = $"[{title}] {Fmt.Duration(elapsed)}";
        var parts = entries.Select(e =>
            e.Contribution is { } c && c > 0
                ? $"{Clean(e.Name)} {Fmt.Abbrev1(e.Dps)} ({Fmt.Percent(c)})"
                : $"{Clean(e.Name)} {Fmt.Abbrev1(e.Dps)}").ToList();
        return Join(head, parts, maxLength);
    }

    private static string Join(string head, IReadOnlyList<string> parts, int maxLength)
    {
        if (head.Length > maxLength) return head[..maxLength];
        var sb = new StringBuilder(head);
        int used = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            int remainingAfter = parts.Count - i - 1;
            string more = remainingAfter > 0 ? $"{Sep}+{remainingAfter}" : "";
            // Keep room for a "+N" tail if later parts may be dropped.
            if (sb.Length + Sep.Length + parts[i].Length + more.Length <= maxLength
                || (remainingAfter == 0 && sb.Length + Sep.Length + parts[i].Length <= maxLength))
            {
                sb.Append(Sep).Append(parts[i]);
                used++;
            }
            else break;
        }
        int dropped = parts.Count - used;
        if (dropped > 0)
        {
            string tail = $"{Sep}+{dropped}";
            if (sb.Length + tail.Length <= maxLength) sb.Append(tail);
        }
        return sb.ToString();
    }

    /// <summary>Chat lines are single-line: strip control characters and brackets that could confuse the game's chat parser.</summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
            if (!char.IsControl(ch)) sb.Append(ch);
        return sb.ToString().Trim();
    }
}
