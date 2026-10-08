using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>One line of the DPS timeline.</summary>
public sealed record TimelineSeries(uint EntityId, string Name, CharacterClass Class, IReadOnlyList<double> Values)
{
    /// <summary>Optional colour override (otherwise the class brush of the theme).</summary>
    public Brush? Color { get; init; }
}

/// <summary>One layer of the cumulative damage chart (running totals per second).</summary>
public sealed record CumulativeSeries(uint EntityId, string Name, CharacterClass Class, IReadOnlyList<long> Values);

/// <summary>Builds chart series from an <see cref="EncounterRecord"/>.</summary>
public static class ChartData
{
    /// <summary>Number of whole seconds a per-second series of the record needs (covers duration and data).</summary>
    public static int SecondsLength(EncounterRecord record)
    {
        int n = (int)Math.Ceiling(Math.Max(0, record.DurationSeconds));
        foreach (var c in record.Combatants) n = Math.Max(n, c.DamagePerSecond.Count);
        return Math.Max(1, n);
    }

    /// <summary>Friendly damage dealers (players and the unknown-summon bucket), biggest first.</summary>
    public static IEnumerable<CombatantRecord> Friendly(EncounterRecord record) =>
        record.Combatants.Where(c => c.Kind != CombatantKind.EnemyPlayer).OrderByDescending(c => c.Damage);

    /// <summary>Enemy players (PvP), most damage exchanged first.</summary>
    public static IEnumerable<CombatantRecord> Enemies(EncounterRecord record) =>
        record.Combatants.Where(c => c.Kind == CombatantKind.EnemyPlayer)
            .OrderByDescending(c => c.DamageFromLocal + c.DamageToLocal);

    public static CombatantRecord? Find(EncounterRecord record, uint entityId) =>
        record.Combatants.FirstOrDefault(c => c.EntityId == entityId);

    public static CombatantRecord? Local(EncounterRecord record) =>
        record.Combatants.FirstOrDefault(c => c.IsLocal);

    /// <summary>Rolling-DPS lines for the given combatants (default: friendly combatants that dealt damage).</summary>
    public static IReadOnlyList<TimelineSeries> TimelineSeries(EncounterRecord record, int windowSeconds = 5,
        Func<CombatantRecord, bool>? include = null)
    {
        int n = SecondsLength(record);
        include ??= c => c.Kind != CombatantKind.EnemyPlayer;
        return record.Combatants
            .Where(c => include(c) && c.Damage > 0)
            .OrderByDescending(c => c.Damage)
            .Select(c => new TimelineSeries(c.EntityId, DisplayName(c), c.Class,
                SeriesMath.RollingDps(c.DamagePerSecond, windowSeconds, n)))
            .ToList();
    }

    /// <summary>Cumulative-damage layers for friendly combatants, biggest damage first (drawn at the bottom).</summary>
    public static IReadOnlyList<CumulativeSeries> CumulativeSeries(EncounterRecord record,
        Func<CombatantRecord, bool>? include = null)
    {
        int n = SecondsLength(record);
        include ??= c => c.Kind != CombatantKind.EnemyPlayer;
        return record.Combatants
            .Where(c => include(c) && c.Damage > 0)
            .OrderByDescending(c => c.Damage)
            .Select(c => new CumulativeSeries(c.EntityId, DisplayName(c), c.Class, SeriesMath.Cumulative(c.DamagePerSecond, n)))
            .ToList();
    }

    /// <summary>Kill time in seconds since start, or null when the boss did not die.</summary>
    public static double? KillTime(EncounterRecord record)
    {
        if (record.Outcome != EncounterOutcome.Kill) return null;
        var killHit = record.Hits.LastOrDefault(h => (h.Flags & HitFlags.KillingBlow) != 0);
        return killHit is not null ? killHit.T : record.DurationSeconds;
    }

    /// <summary>Name for display; the unknown-summon bucket gets a readable label.</summary>
    public static string DisplayName(CombatantRecord c) =>
        c.Kind == CombatantKind.UnknownSummons ? "Unattributed summons"
        : string.IsNullOrWhiteSpace(c.Name) ? "Player " + c.EntityId : c.Name;

    /// <summary>Map display name; unnamed maps read "Dungeon 600123" / "Open world (1010)" instead of a bare id.</summary>
    public static string MapLabel(IGameData gameData, uint mapId) =>
        gameData.GetMapName(mapId) ?? (gameData.IsInstanceMap(mapId) ? $"Dungeon {mapId}" : $"Open world ({mapId})");

    /// <summary>True when several bosses were fought at the same time in this encounter.</summary>
    public static bool IsMultiBoss(EncounterRecord record) => record.Bosses.Count >= 2;

    /// <summary>Display name of one boss of the encounter; "World boss (45.6M HP)" when its spawn (NPC code) was missed.</summary>
    public static string BossName(BossResult boss, IGameData gameData, bool openWorld = false) =>
        boss.NpcCode is { } code ? gameData.GetNpcName(code) : BossLabel.Unnamed(boss.MaxHp, openWorld) ?? $"Target {boss.EntityId}";

    /// <summary>True when the encounter was fought on an overworld map.</summary>
    public static bool IsOpenWorld(EncounterRecord record, IGameData gameData) =>
        record.MapId is { } m && gameData.IsOpenWorldMap(m);

    /// <summary>
    /// Partial view (<see cref="EncounterRecord.PartialView"/>): you and your party first, then other players with direct
    /// hits; players seen only through DoT ticks or heals are returned separately (<paramref name="folded"/>).
    /// Otherwise the friendly list unchanged.
    /// </summary>
    public static List<CombatantRecord> VisibleFriendly(EncounterRecord record, out List<CombatantRecord> folded)
    {
        var all = Friendly(record).ToList();
        folded = new List<CombatantRecord>();
        if (!record.PartialView) return all;
        folded = all.Where(c => c.Kind == CombatantKind.Player && !c.IsLocal && !c.IsPartyMember && c.Quality.Hits == 0).ToList();
        var hidden = folded.ToHashSet();
        return all.Where(c => !hidden.Contains(c)).OrderBy(c => c.IsLocal || c.IsPartyMember ? 0 : 1).ThenByDescending(c => c.Damage).ToList();
    }

    /// <summary>Boss display name (NPC table) — "Silver Blade Rotan + Black Smoke Murute" for a multi-boss fight, in
    /// engagement order — or a fallback by kind.</summary>
    public static string Title(EncounterRecord record, IGameData gameData)
    {
        bool openWorld = IsOpenWorld(record, gameData);
        if (IsMultiBoss(record)) return string.Join(" + ", record.Bosses.Select(b => BossName(b, gameData, openWorld)));
        if (record.BossNpcCode is { } code) return gameData.GetNpcName(code);
        if (record.Kind == EncounterKind.Boss && BossLabel.Unnamed(record.BossMaxHp, openWorld) is { } unnamed) return unnamed;
        return record.Kind switch
        {
            EncounterKind.Pvp => "PvP session",
            EncounterKind.Training => "Training run",
            EncounterKind.Dummy => "Training dummy",
            _ => record.MapId is { } m && gameData.GetMapName(m) is { } name ? name : "Open-world fight",
        };
    }
}
