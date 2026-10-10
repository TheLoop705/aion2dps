using Aion2Dps.Combat.Timers;

namespace Aion2Dps.App.Demo;

/// <summary>Demo field-boss timers: a few bosses up, a few respawning (with learned intervals), one unannounced.</summary>
public static class DemoTimers
{
    public static FieldBossTimerBook Seed(IGameData gameData, DateTime nowUtc)
    {
        var book = new FieldBossTimerBook(gameData, TimerData.LoadDefault());
        uint map = FakeGameData.FieldBossMap;
        long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        FieldBossSlot Slot(int place, bool alive, DateTime? time) =>
            new() { Slot = map * 100 + (uint)place, Alive = alive, TimeUnixMs = time is DateTime t ? Ms(t) : 0 };

        // Everyone alive first, then the kills a few seconds later: the book learns each killed boss's interval.
        var killedAt = nowUtc.AddMinutes(-20);
        book.OnList(new FieldBossListEvent
        {
            Time = killedAt.AddSeconds(-5), MapId = map,
            Slots = [Slot(1, true, nowUtc.AddHours(-3)), Slot(2, true, nowUtc.AddHours(-1)), Slot(3, true, nowUtc.AddHours(-2)),
                Slot(4, true, nowUtc.AddHours(-9)), Slot(5, true, nowUtc.AddHours(-5)), Slot(6, true, nowUtc.AddHours(-13))],
        });
        book.OnList(new FieldBossListEvent
        {
            Time = killedAt, MapId = map,
            Slots = [Slot(1, true, nowUtc.AddHours(-3)), Slot(2, false, killedAt.AddMinutes(30)), Slot(3, false, killedAt.AddHours(2)),
                Slot(4, false, killedAt.AddHours(4)), Slot(5, true, nowUtc.AddHours(-5)), Slot(6, false, killedAt.AddHours(12))],
        });
        return book;
    }
}
