using Aion2Dps.Combat.Timers;
using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

/// <summary>Rift / event schedule maths (server clock, DST) and the field-boss timer book (the game's 01 91 lists).</summary>
public class TimersTests
{
    private static readonly TimerData Data = TimerData.Load(RepoFile("data", "timers.json"));
    private static readonly TimeZoneInfo Berlin = Data.ResolveZone("EU");

    private static DateTime Utc(int month, int day, int hour, int minute = 0, int second = 0) =>
        new(2026, month, day, hour, minute, second, DateTimeKind.Utc);

    private static ScheduledEvent Event(string id) => Data.Events.Single(e => e.Id == id);

    private static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }

    // ───────────── data file ─────────────

    [Fact]
    public void Bundled_timer_data_loads()
    {
        Assert.Contains(Data.Events, e => e.Id == "rift" && e.EveryMinutes == 180 && e.OpenMinutes == 10 && e.Favorite);
        Assert.Equal("Europe/Berlin", Data.ServerTimeZones["EU"]);
        Assert.Equal(TimeSpan.FromHours(12), Data.RespawnIntervals[(1110, 21)]);
        Assert.Equal(24, Data.RespawnIntervals.Keys.Count(k => k.Map == 1110));
        Assert.Equal(TimeSpan.FromHours(6), Data.ImportantRespawn);
        Assert.NotEqual(TimeZoneInfo.Utc, Berlin);
    }

    [Fact]
    public void Bad_timer_data_never_throws()
    {
        Assert.Same(TimerData.Empty, TimerData.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        var d = TimerData.Parse("""
            { "events": [ { "id": "x", "name": "X", "start": "25:00" }, { "id": "y", "name": "Y", "start": "01:00", "days": ["Funday"] },
              { "id": "ok", "name": "Ok", "start": "01:30" } ] }
            """);
        Assert.Equal("ok", Assert.Single(d.Events).Id);
        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), d.ResolveZone("Nowhere/Invalid"));
    }

    // ───────────── schedule ─────────────

    [Fact]
    public void Rift_follows_the_server_clock_every_three_hours_from_two()
    {
        var rift = Event("rift");
        // Sat 2026-10-10 12:30 CEST (10:30 UTC): next rift 14:00 CEST = 12:00 UTC.
        var next = TimerSchedule.Current(rift, Berlin, Utc(10, 10, 10, 30))!.Value;
        Assert.Equal(Utc(10, 10, 12), next.StartUtc);
        Assert.False(next.Running);
        Assert.Equal(
            new[] { Utc(10, 10, 12), Utc(10, 10, 15), Utc(10, 10, 18), Utc(10, 10, 21), Utc(10, 11, 0), Utc(10, 11, 3) },
            TimerSchedule.Upcoming(rift, Berlin, Utc(10, 10, 10, 30), 6));
    }

    [Fact]
    public void Rift_entry_window_and_running_hour()
    {
        var rift = Event("rift");
        var open = TimerSchedule.Current(rift, Berlin, Utc(10, 10, 12, 5))!.Value;
        Assert.True(open.Running);
        Assert.True(open.EntryOpen);
        Assert.Equal(Utc(10, 10, 12), open.StartUtc);
        var inside = TimerSchedule.Current(rift, Berlin, Utc(10, 10, 12, 30))!.Value;
        Assert.True(inside.Running);
        Assert.False(inside.EntryOpen);
        Assert.Equal(Utc(10, 10, 13), inside.EndUtc);
        var after = TimerSchedule.Current(rift, Berlin, Utc(10, 10, 13, 0))!.Value;
        Assert.Equal(Utc(10, 10, 15), after.StartUtc);
    }

    [Fact]
    public void Rift_moves_one_hour_in_utc_when_summer_time_ends()
    {
        var rift = Event("rift");
        // Mon 2026-10-26, CET (UTC+1): 02:00 CET = 01:00 UTC.
        Assert.Equal(Utc(10, 26, 1), TimerSchedule.Upcoming(rift, Berlin, Utc(10, 26, 0, 0), 1)[0]);
        // The DST night itself: strictly increasing, no duplicates.
        var night = TimerSchedule.Upcoming(rift, Berlin, Utc(10, 24, 20), 6);
        Assert.Equal(night.Order().Distinct(), night);
    }

    [Fact]
    public void Weekly_siege_boss_matches_the_game_announced_time()
    {
        // The game's own 01 91 list (captured Fri 2026-10-09) announced the Sunday siege bosses for 2026-10-11 19:05 UTC.
        var nahma = Event("nahma");
        Assert.Equal(Utc(10, 11, 19, 5), TimerSchedule.Current(nahma, Berlin, Utc(10, 9, 21, 21))!.Value.StartUtc);
        var siege = Event("siege");
        Assert.Equal(DayOfWeek.Saturday, TimerSchedule.Current(siege, Berlin, Utc(10, 9, 21, 21))!.Value.StartUtc.DayOfWeek);
        Assert.Equal(Utc(10, 10, 19), TimerSchedule.Current(siege, Berlin, Utc(10, 9, 21, 21))!.Value.StartUtc);
    }

    // ───────────── field-boss book ─────────────

    private const uint Map = 1110;

    private static FieldBossListEvent List(DateTime at, params FieldBossSlot[] slots) => new() { Time = at, MapId = Map, Slots = slots };

    private static FieldBossSlot Slot(int place, bool alive, DateTime? time) => new()
    {
        Slot = Map * 100 + (uint)place, Alive = alive,
        TimeUnixMs = time is DateTime t ? new DateTimeOffset(t).ToUnixTimeMilliseconds() : 0,
    };

    [Fact]
    public void A_kill_seen_live_teaches_the_respawn_interval()
    {
        var book = new FieldBossTimerBook(null, Data);
        var t0 = Utc(10, 9, 20, 58, 18);
        book.OnList(List(t0, Slot(21, true, Utc(10, 9, 20, 53, 41)), Slot(11, false, Utc(10, 9, 23, 35, 59))));
        // Real Gartua kill: dead at 20:58:54, respawn 08:58:51 next day.
        book.OnList(List(t0.AddSeconds(36), Slot(21, false, Utc(10, 10, 8, 58, 51)), Slot(11, false, Utc(10, 9, 23, 35, 59))));

        var snap = book.Snapshot();
        var gartua = snap.Single(t => t.Place == 21);
        Assert.False(gartua.Alive);
        Assert.Equal(TimeSpan.FromHours(12), gartua.Interval);
        Assert.True(gartua.IntervalLearned);
        Assert.Equal(FieldBossStatus.Respawning, gartua.StatusAt(Utc(10, 9, 22)));
        Assert.Equal(FieldBossStatus.ProbablyUp, gartua.StatusAt(Utc(10, 10, 9)));
        Assert.True(gartua.IsImportant(Data.ImportantRespawn));

        // First seen dead: no kill observed, so only the bundled default interval (3 h for Linx).
        var linx = snap.Single(t => t.Place == 11);
        Assert.Equal(TimeSpan.FromHours(3), linx.Interval);
        Assert.False(linx.IntervalLearned);
        Assert.False(linx.IsImportant(Data.ImportantRespawn));
        Assert.Equal("Field boss 11", linx.Name);
    }

    [Fact]
    public void Stale_alive_state_does_not_teach_an_interval()
    {
        var book = new FieldBossTimerBook(null, TimerData.Empty);
        var t0 = Utc(10, 9, 12);
        book.OnList(List(t0, Slot(1, true, null)));
        book.OnList(List(t0.AddHours(1), Slot(1, false, t0.AddHours(1.5))));
        Assert.Null(book.Snapshot().Single().Interval);
    }

    [Fact]
    public void Unchanged_lists_do_not_bump_the_version()
    {
        var book = new FieldBossTimerBook();
        int changes = 0;
        book.Changed += () => changes++;
        var t0 = Utc(10, 9, 12);
        book.OnList(List(t0, Slot(1, true, null)));
        book.OnList(List(t0.AddSeconds(5), Slot(1, true, null)));
        Assert.Equal(1, changes);
        Assert.Equal(1, book.Version);
        book.OnList(List(t0.AddSeconds(10), Slot(1, false, t0.AddMinutes(30))));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Book_round_trips_through_json_and_ignores_damage()
    {
        var book = new FieldBossTimerBook(null, Data);
        var t0 = Utc(10, 9, 20);
        book.OnList(List(t0, Slot(2, true, null), Slot(22, false, Utc(10, 10, 9, 16, 50))));
        book.OnList(List(t0.AddSeconds(4), Slot(2, false, t0.AddMinutes(30)), Slot(22, false, Utc(10, 10, 9, 16, 50))));

        var copy = new FieldBossTimerBook(null, Data);
        copy.LoadJson(book.ToJson(), t0.AddHours(1));
        Assert.Equal(book.Snapshot(), copy.Snapshot());
        Assert.Equal(TimeSpan.FromMinutes(30), copy.Snapshot().Single(t => t.Place == 2).Interval);

        var damaged = new FieldBossTimerBook();
        damaged.LoadJson("{ not json", t0);
        damaged.LoadJson("""{ "Slots": [ { "Map": 1110, "Slot": 5, "Seen": "2026-10-09T20:00:00Z" } ] }""", t0);
        Assert.Empty(damaged.Snapshot());

        var stale = new FieldBossTimerBook();
        stale.LoadJson(book.ToJson(), t0.AddDays(30));
        Assert.Empty(stale.Snapshot());
    }

    [Fact]
    public void Live_lists_win_over_the_saved_book()
    {
        var saved = new FieldBossTimerBook();
        var t0 = Utc(10, 9, 20);
        saved.OnList(List(t0, Slot(3, false, t0.AddHours(1))));
        var book = new FieldBossTimerBook();
        book.OnList(List(t0.AddMinutes(90), Slot(3, true, t0.AddHours(1))));
        book.LoadJson(saved.ToJson(), t0.AddMinutes(90));
        Assert.True(book.Snapshot().Single().Alive);
    }

    [Theory]
    [InlineData(29.9, 30)]
    [InlineData(719.95, 720)]
    [InlineData(-1, 0)]
    public void Intervals_round_to_five_minutes(double minutes, double expected) =>
        Assert.Equal(TimeSpan.FromMinutes(expected), FieldBossTimerBook.RoundInterval(TimeSpan.FromMinutes(minutes)));
}
