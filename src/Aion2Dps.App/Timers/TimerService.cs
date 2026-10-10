using Aion2Dps.App.Settings;
using Aion2Dps.Combat.Timers;

namespace Aion2Dps.App.Timers;

/// <summary>One line of the timers view: a scheduled event or a field boss, with its next relevant moment.</summary>
/// <param name="Key">Star key ("event:rift", "boss:111021").</param>
/// <param name="Group">"Events" or the boss's map name.</param>
/// <param name="Status">Short state ("Open", "Up", "Respawning", "Probably up", "Unannounced", "Next").</param>
/// <param name="AtUtc">Next start / respawn (null when unknown or already up).</param>
/// <param name="Live">Running / open / alive right now.</param>
public sealed record TimerRow(string Key, string Group, string Name, string Status, DateTime? AtUtc, bool Live, bool Starred, string? Detail,
    TimeSpan? Interval = null, TimerPriority Priority = TimerPriority.None);

/// <summary>A tray notification to show.</summary>
public sealed record TimerAlert(string Title, string Text);

/// <summary>
/// The timers feature without WPF: rows for the dashboard / overlay from the scheduled events (<c>timers.json</c>, in the
/// server's clock) and the field-boss book (the game's own respawn times), star defaults, alerts before starred timers
/// (each occurrence once) and saving the boss book next to the fight history.
/// </summary>
public sealed class TimerService
{
    public const string BookFileName = "boss-timers.json";

    private readonly FieldBossTimerBook _book;
    private readonly Func<TimerSettings> _settings;
    private readonly IGameData _gameData;
    private readonly string? _bookPath;
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);
    private int _savedVersion = -1;

    public TimerService(FieldBossTimerBook book, IGameData gameData, Func<TimerSettings> settings, string? bookPath)
    {
        _book = book;
        _gameData = gameData;
        _settings = settings;
        _bookPath = bookPath;
    }

    public FieldBossTimerBook Book => _book;
    public TimerData Data => _book.Data;

    public TimeZoneInfo ServerZone => Data.ResolveZone(_settings().ServerRegion);

    public static string EventKey(ScheduledEvent e) => "event:" + e.Id;
    public static string BossKey(FieldBossTimer t) => "boss:" + t.Slot;

    public bool IsStarred(ScheduledEvent e) => _settings().Stars.TryGetValue(EventKey(e), out bool s) ? s : e.Favorite;

    public bool IsStarred(FieldBossTimer t) =>
        _settings().Stars.TryGetValue(BossKey(t), out bool s) ? s : t.IsImportant;

    /// <summary>Loads the saved boss book (call once at start-up).</summary>
    public void Load(DateTime nowUtc)
    {
        if (_bookPath is null) return;
        _book.Load(_bookPath, nowUtc);
        _savedVersion = _book.Version;
    }

    /// <summary>Saves the boss book when it changed since the last save.</summary>
    public void SaveIfChanged()
    {
        if (_bookPath is null) return;
        int v = _book.Version;
        if (v == _savedVersion) return;
        _book.Save(_bookPath);
        _savedVersion = v;
    }

    public IReadOnlyList<TimerRow> EventRows(DateTime nowUtc)
    {
        var zone = ServerZone;
        var rows = new List<TimerRow>();
        foreach (var e in Data.Events)
        {
            if (TimerSchedule.Current(e, zone, nowUtc) is not EventOccurrence o) continue;
            string status = o.EntryOpen ? "Open" : o.Running ? "Running" : "Next";
            DateTime? at = o.Running ? (o.EntryOpen ? o.StartUtc.AddMinutes(e.OpenMinutes) : o.EndUtc) : o.StartUtc;
            rows.Add(new TimerRow(EventKey(e), "Events", e.Name, status, at, o.Running, IsStarred(e), e.Note, Priority: e.Priority));
        }
        return rows.OrderBy(r => r.Live ? 0 : 1).ThenBy(r => r.AtUtc ?? DateTime.MaxValue).ToList();
    }

    public IReadOnlyList<TimerRow> BossRows(DateTime nowUtc)
    {
        var rows = new List<TimerRow>();
        foreach (var t in _book.Snapshot())
        {
            string group = _gameData.GetMapName(t.MapId) ?? $"Map {t.MapId}";
            var st = t.StatusAt(nowUtc);
            string status = st switch
            {
                FieldBossStatus.Up => "Up",
                FieldBossStatus.Respawning => "Respawning",
                FieldBossStatus.ProbablyUp => "Probably up",
                _ => "Unannounced",
            };
            string? detail = t.Interval is TimeSpan i ? $"respawn {FormatInterval(i)}{(t.IntervalLearned ? "" : " (est.)")}" : null;
            if (st == FieldBossStatus.Up && t.TimeUtc is DateTime since) detail = Join(detail, "up since " + LocalClock(since));
            rows.Add(new TimerRow(BossKey(t), group, t.Name, status, st == FieldBossStatus.Respawning ? t.RespawnUtc : null,
                st is FieldBossStatus.Up or FieldBossStatus.ProbablyUp, IsStarred(t), detail, t.Interval, t.Priority));
        }
        return rows;
    }

    /// <summary>The next starred event or respawn (for the slim overlay bar); live entries count only when an entry window is open.</summary>
    public TimerRow? NextStarred(DateTime nowUtc) =>
        EventRows(nowUtc).Where(r => r.Starred && (r.Status == "Open" || !r.Live) && r.AtUtc is not null)
            .Concat(BossRows(nowUtc).Where(r => r.Starred && r.AtUtc is not null))
            .OrderBy(r => r.Status == "Open" ? 0 : 1).ThenBy(r => r.AtUtc).FirstOrDefault();

    /// <summary>
    /// Alerts due at <paramref name="nowUtc"/>: starred events and respawns within the lead time, and (optionally) a starred
    /// boss whose respawn time arrived. Each occurrence alerts once.
    /// </summary>
    public IReadOnlyList<TimerAlert> CollectAlerts(DateTime nowUtc)
    {
        var s = _settings();
        if (!s.AlertsEnabled) return [];
        var lead = TimeSpan.FromMinutes(s.AlertMinutesBefore);
        var result = new List<TimerAlert>();
        var zone = ServerZone;
        foreach (var e in Data.Events)
        {
            if (!IsStarred(e) || TimerSchedule.Current(e, zone, nowUtc) is not EventOccurrence o) continue;
            var left = o.StartUtc - nowUtc;
            if (left > lead || o.Running && nowUtc - o.StartUtc > TimeSpan.FromMinutes(1)) continue;
            if (!_alerted.Add($"{EventKey(e)}@{o.StartUtc:O}")) continue;
            result.Add(left > TimeSpan.FromSeconds(30)
                ? new TimerAlert(e.Name, $"Starts in {FormatLeft(left)} ({LocalClock(o.StartUtc)})")
                : new TimerAlert(e.Name, e.OpenMinutes > 0 ? $"Open now for {e.OpenMinutes} min" : "Starting now"));
        }
        foreach (var t in _book.Snapshot())
        {
            if (!IsStarred(t) || t.RespawnUtc is not DateTime r) continue;
            var left = r - nowUtc;
            string name = t.Name;
            if (left > TimeSpan.Zero && left <= lead && _alerted.Add($"{BossKey(t)}@{r:O}:soon"))
                result.Add(new TimerAlert(name, $"Respawns in {FormatLeft(left)} ({LocalClock(r)})"));
            else if (s.AlertOnSpawn && left <= TimeSpan.Zero && left > TimeSpan.FromMinutes(-2) && _alerted.Add($"{BossKey(t)}@{r:O}:up"))
                result.Add(new TimerAlert(name, "Respawned now"));
        }
        if (_alerted.Count > 4096) _alerted.Clear();
        return result;
    }

    public void SetStar(string key, bool starred, bool defaultValue)
    {
        var stars = _settings().Stars;
        if (starred == defaultValue) stars.Remove(key);
        else stars[key] = starred;
    }

    public static string FormatLeft(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return left.TotalHours >= 1
            ? $"{(int)left.TotalHours}h {left.Minutes:00}m"
            : left.TotalMinutes >= 1 ? $"{left.Minutes}m {left.Seconds:00}s" : $"{left.Seconds}s";
    }

    public static string FormatInterval(TimeSpan i) =>
        i.TotalHours >= 1 ? (i.Minutes == 0 ? $"{(int)i.TotalHours} h" : $"{(int)i.TotalHours} h {i.Minutes} min") : $"{(int)i.TotalMinutes} min";

    /// <summary>A UTC moment as the PC's local wall clock (weekday added when not today).</summary>
    public static string LocalClock(DateTime utc)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return local.Date == DateTime.Now.Date ? local.ToString("HH:mm") : local.ToString("ddd HH:mm");
    }

    private static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + " · " + b;
}
