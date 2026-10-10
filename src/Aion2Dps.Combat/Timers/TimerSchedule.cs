using System.Globalization;
using System.Text.Json;

namespace Aion2Dps.Combat.Timers;

/// <summary>
/// A recurring server-time event of <c>timers.json</c>: either every <see cref="EveryMinutes"/> from <see cref="Start"/>
/// (rifts, hourly events; the grid restarts each server day at <see cref="Start"/>) or on fixed <see cref="Days"/> at
/// <see cref="Start"/> (sieges, weekly reset).
/// </summary>
public sealed record ScheduledEvent
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Server-time of day of the first occurrence.</summary>
    public TimeSpan Start { get; init; }
    /// <summary>Repeat interval within the day (0 = once per listed day).</summary>
    public int EveryMinutes { get; init; }
    /// <summary>Days of week (empty = every day).</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];
    /// <summary>Entry window after the start (rift portals stay open 10 min); 0 = none.</summary>
    public int OpenMinutes { get; init; }
    /// <summary>How long the event runs after its start; 0 = an instant (resets).</summary>
    public int DurationMinutes { get; init; }
    /// <summary>Alert for this event by default.</summary>
    public bool Favorite { get; init; }
    public string? Note { get; init; }
}

/// <summary>One occurrence of a <see cref="ScheduledEvent"/> relative to a moment.</summary>
/// <param name="StartUtc">The occurrence that is running now, or else the next one.</param>
/// <param name="Running">True while inside its duration.</param>
/// <param name="EntryOpen">True while inside its entry window.</param>
public readonly record struct EventOccurrence(ScheduledEvent Event, DateTime StartUtc, bool Running, bool EntryOpen)
{
    public DateTime EndUtc => StartUtc.AddMinutes(Event.DurationMinutes);
}

/// <summary>Bundled timer data: scheduled events, server clocks and default field-boss respawn intervals.</summary>
public sealed class TimerData
{
    public const string FileName = "timers.json";
    public const string DefaultTimeZone = "Europe/Berlin";

    public IReadOnlyList<ScheduledEvent> Events { get; init; } = [];
    /// <summary>Region code (servers.json) → IANA/Windows time zone id.</summary>
    public IReadOnlyDictionary<string, string> ServerTimeZones { get; init; } = new Dictionary<string, string>();
    /// <summary>(map, place) → respawn interval after a kill.</summary>
    public IReadOnlyDictionary<(uint Map, int Place), TimeSpan> RespawnIntervals { get; init; } = new Dictionary<(uint, int), TimeSpan>();
    /// <summary>Field bosses with at least this respawn interval are the "important" ones (alerts by default).</summary>
    public TimeSpan ImportantRespawn { get; init; } = TimeSpan.FromHours(6);

    public static TimerData Empty { get; } = new();

    public static TimerData LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, "data", FileName));

    /// <summary>Never throws: a missing or damaged file gives <see cref="Empty"/> (entries with errors are skipped).</summary>
    public static TimerData Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Timers", $"Could not read {path}: {ex.Message}");
            return Empty;
        }
    }

    public static TimerData Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var events = new List<ScheduledEvent>();
        if (root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
            foreach (var e in evs.EnumerateArray())
                if (ParseEvent(e) is { } ev) events.Add(ev);

        var zones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("serverTimeZones", out var z) && z.ValueKind == JsonValueKind.Object)
            foreach (var p in z.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString())) zones[p.Name] = p.Value.GetString()!;

        var respawn = new Dictionary<(uint, int), TimeSpan>();
        if (root.TryGetProperty("fieldBossRespawnMinutes", out var r) && r.ValueKind == JsonValueKind.Object)
            foreach (var map in r.EnumerateObject())
            {
                if (!uint.TryParse(map.Name, NumberStyles.None, CultureInfo.InvariantCulture, out uint mapId) || map.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var place in map.Value.EnumerateObject())
                    if (int.TryParse(place.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int pl) && pl is >= 1 and <= 99
                        && place.Value.ValueKind == JsonValueKind.Number && place.Value.TryGetInt32(out int min) && min > 0)
                        respawn[(mapId, pl)] = TimeSpan.FromMinutes(min);
            }

        var important = root.TryGetProperty("importantRespawnMinutes", out var im) && im.ValueKind == JsonValueKind.Number && im.TryGetInt32(out int imin) && imin > 0
            ? TimeSpan.FromMinutes(imin)
            : TimeSpan.FromHours(6);
        return new TimerData { Events = events, ServerTimeZones = zones, RespawnIntervals = respawn, ImportantRespawn = important };
    }

    private static ScheduledEvent? ParseEvent(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? id = Str(e, "id"), name = Str(e, "name"), start = Str(e, "start");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || start is null
            || !TimeSpan.TryParseExact(start, @"hh\:mm", CultureInfo.InvariantCulture, out var at) || at >= TimeSpan.FromDays(1))
            return null;
        var days = new List<DayOfWeek>();
        if (e.TryGetProperty("days", out var d) && d.ValueKind == JsonValueKind.Array)
            foreach (var x in d.EnumerateArray())
                if (x.GetString() is { } s && ParseDay(s) is DayOfWeek day) days.Add(day);
                else return null;
        int every = Int(e, "everyMinutes");
        if (every < 0 || every > 1440) return null;
        return new ScheduledEvent
        {
            Id = id, Name = name, Start = at, EveryMinutes = every, Days = days,
            OpenMinutes = Math.Max(0, Int(e, "openMinutes")), DurationMinutes = Math.Max(0, Int(e, "durationMinutes")),
            Favorite = e.TryGetProperty("favorite", out var f) && f.ValueKind == JsonValueKind.True, Note = Str(e, "note"),
        };

        static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
    }

    private static DayOfWeek? ParseDay(string s)
    {
        foreach (DayOfWeek d in Enum.GetValues<DayOfWeek>())
            if (d.ToString().StartsWith(s, StringComparison.OrdinalIgnoreCase) && s.Length >= 2) return d;
        return null;
    }

    /// <summary>The time zone for a region code or zone id, falling back to <see cref="DefaultTimeZone"/>, then UTC.</summary>
    public TimeZoneInfo ResolveZone(string? regionOrZone)
    {
        string id = regionOrZone is { Length: > 0 } && ServerTimeZones.TryGetValue(regionOrZone, out var z) ? z : regionOrZone ?? DefaultTimeZone;
        if (TryZone(id) is { } tz) return tz;
        return TryZone(DefaultTimeZone) ?? TimeZoneInfo.Utc;

        static TimeZoneInfo? TryZone(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception) { return null; }
        }
    }
}

/// <summary>Occurrence maths for <see cref="ScheduledEvent"/>s in a server time zone (DST-aware, pure).</summary>
public static class TimerSchedule
{
    /// <summary>
    /// The occurrence running at <paramref name="nowUtc"/> (inside its duration) or else the next one; null when the
    /// event never occurs (bad data).
    /// </summary>
    public static EventOccurrence? Current(ScheduledEvent ev, TimeZoneInfo zone, DateTime nowUtc)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var dur = TimeSpan.FromMinutes(ev.DurationMinutes);
        // Look one day back (an occurrence may still be running) and 8 days ahead (weekly events).
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        for (int dayOffset = -1; dayOffset <= 8; dayOffset++)
        {
            var day = localToday.AddDays(dayOffset);
            if (ev.Days.Count > 0 && !ev.Days.Contains(day.DayOfWeek)) continue;
            foreach (var local in Starts(ev, day))
            {
                var startUtc = ToUtc(local, zone);
                if (startUtc > nowUtc || startUtc + dur > nowUtc)
                {
                    bool running = startUtc <= nowUtc && nowUtc < startUtc + dur;
                    bool open = startUtc <= nowUtc && nowUtc < startUtc.AddMinutes(ev.OpenMinutes);
                    return new EventOccurrence(ev, startUtc, running, open);
                }
            }
        }
        return null;
    }

    /// <summary>The next <paramref name="count"/> starts strictly after <paramref name="fromUtc"/>.</summary>
    public static IReadOnlyList<DateTime> Upcoming(ScheduledEvent ev, TimeZoneInfo zone, DateTime fromUtc, int count)
    {
        var result = new List<DateTime>(count);
        var localDay = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), zone).Date;
        for (int dayOffset = 0; dayOffset <= 15 && result.Count < count; dayOffset++)
        {
            var day = localDay.AddDays(dayOffset);
            if (ev.Days.Count > 0 && !ev.Days.Contains(day.DayOfWeek)) continue;
            foreach (var local in Starts(ev, day))
            {
                var utc = ToUtc(local, zone);
                if (utc <= fromUtc) continue;
                result.Add(utc);
                if (result.Count == count) break;
            }
        }
        return result;
    }

    /// <summary>A server day's starts in order: the grid through <see cref="ScheduledEvent.Start"/> every
    /// <see cref="ScheduledEvent.EveryMinutes"/> (02:00 + 3 h → 02, 05, …, 23; also covers the 00:00..02:00 part).</summary>
    private static IEnumerable<DateTime> Starts(ScheduledEvent ev, DateTime day)
    {
        var first = day + ev.Start;
        if (ev.EveryMinutes <= 0)
        {
            yield return first;
            yield break;
        }
        var step = TimeSpan.FromMinutes(ev.EveryMinutes);
        var t = first - TimeSpan.FromTicks((first - day).Ticks / step.Ticks * step.Ticks);
        for (; t < day.AddDays(1); t += step) yield return t;
    }

    /// <summary>Server local time → UTC; a time skipped by DST moves forward, an ambiguous one takes the first.</summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
