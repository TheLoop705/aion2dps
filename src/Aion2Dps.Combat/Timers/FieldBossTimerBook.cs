using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion2Dps.Combat.Timers;

/// <summary>What the meter knows about a field boss right now.</summary>
public enum FieldBossStatus
{
    /// <summary>The game's latest list says the boss is alive.</summary>
    Up,
    /// <summary>Dead; <see cref="FieldBossTimer.RespawnUtc"/> is the game's own respawn time.</summary>
    Respawning,
    /// <summary>The respawn time passed since the last list: most likely up again (not confirmed).</summary>
    ProbablyUp,
    /// <summary>Dead without a respawn time (scheduled bosses the game has not announced yet).</summary>
    Unknown,
}

/// <summary>One field-boss slot of the timer book (an immutable copy).</summary>
/// <param name="MapId">Open-world map of the <c>01 91</c> list.</param>
/// <param name="Slot">map × 100 + place.</param>
/// <param name="Name">Boss name (or "Field boss N" when the map's NPC block is unknown).</param>
/// <param name="Alive">Alive in the latest list.</param>
/// <param name="TimeUtc">Spawn time of a living boss, respawn time of a dead one; null when the game sent none.</param>
/// <param name="Interval">Respawn interval after a kill: learned live (kill seen → respawn time) or the bundled default.</param>
/// <param name="IntervalLearned">True when <paramref name="Interval"/> was measured from this game's own list.</param>
/// <param name="SeenUtc">Capture time of the latest list carrying this slot.</param>
public sealed record FieldBossTimer(uint MapId, uint Slot, uint? NpcCode, string Name, bool Alive, DateTime? TimeUtc, TimeSpan? Interval,
    bool IntervalLearned, DateTime SeenUtc)
{
    /// <summary>A long-respawn ("big") boss, or a scheduled one (no interval, announced respawn time).</summary>
    public bool IsImportant(TimeSpan threshold) => Interval is TimeSpan i ? i >= threshold : !Alive && TimeUtc is not null;

    public int Place => (int)(Slot - MapId * 100);

    public DateTime? RespawnUtc => Alive ? null : TimeUtc;

    public FieldBossStatus StatusAt(DateTime nowUtc) => Alive
        ? FieldBossStatus.Up
        : TimeUtc is not DateTime r ? FieldBossStatus.Unknown
        : r <= nowUtc ? FieldBossStatus.ProbablyUp
        : FieldBossStatus.Respawning;

    /// <summary>Time left until the respawn (zero or negative once due); null when not respawning on a known time.</summary>
    public TimeSpan? RemainingAt(DateTime nowUtc) => RespawnUtc is DateTime r ? r - nowUtc : null;
}

/// <summary>
/// Field-boss timers from the game's own <c>01 91</c> field-boss lists: every slot of the region's open-world maps with
/// "alive since" or "respawns at" (server Unix ms). The list keeps arriving every few seconds while logged in, also from
/// other zones. A kill seen live (alive → dead within <see cref="LearnWindow"/>) teaches the slot's respawn interval
/// (30 min, 2 h, 12 h, …), which marks the big bosses. Thread-safe; feed it as an <see cref="IGameEventSink"/>.
/// The book persists to JSON so timers survive a restart (respawn times are absolute).
/// </summary>
public sealed class FieldBossTimerBook : IGameEventSink
{
    /// <summary>A kill counts for interval learning only when the slot was seen alive this recently.</summary>
    public static readonly TimeSpan LearnWindow = TimeSpan.FromMinutes(3);
    /// <summary>Entries not refreshed for this long (and long past their respawn) are dropped on load.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    private readonly object _gate = new();
    private readonly IGameData? _gameData;
    private readonly TimerData _data;
    private readonly Dictionary<uint, Entry> _slots = new();
    private int _version;

    public FieldBossTimerBook(IGameData? gameData = null, TimerData? data = null)
    {
        _gameData = gameData;
        _data = data ?? TimerData.Empty;
    }

    public TimerData Data => _data;

    /// <summary>Raised (on the feeding thread, outside the lock) after a list changed a slot's state or time.</summary>
    public event Action? Changed;

    /// <summary>Bumped on every change (cheap "do I need to save/redraw" check).</summary>
    public int Version
    {
        get { lock (_gate) return _version; }
    }

    public void OnEvent(GameEvent gameEvent)
    {
        if (gameEvent is FieldBossListEvent list) OnList(list);
    }

    public void OnList(FieldBossListEvent e)
    {
        if (e.MapId == 0 || e.Slots.Count == 0) return;
        bool changed = false;
        lock (_gate)
        {
            foreach (var s in e.Slots)
            {
                DateTime? time = s.TimeUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(s.TimeUnixMs).UtcDateTime : null;
                if (!_slots.TryGetValue(s.Slot, out var cur))
                {
                    _slots[s.Slot] = new Entry { Map = e.MapId, Slot = s.Slot, Alive = s.Alive, Time = time, Seen = e.Time, AliveSeen = s.Alive ? e.Time : null };
                    changed = true;
                    continue;
                }
                if (cur.Alive && !s.Alive && time is DateTime respawn && cur.AliveSeen is DateTime lastAlive
                    && e.Time - lastAlive <= LearnWindow && e.Time >= lastAlive)
                {
                    var learned = RoundInterval(respawn - e.Time);
                    if (learned > TimeSpan.Zero) cur.IntervalMinutes = (int)learned.TotalMinutes;
                }
                if (cur.Alive != s.Alive || cur.Time != time || cur.Map != e.MapId) changed = true;
                cur.Map = e.MapId;
                cur.Alive = s.Alive;
                cur.Time = time;
                cur.Seen = e.Time;
                if (s.Alive) cur.AliveSeen = e.Time;
            }
            if (changed) _version++;
        }
        if (changed) RaiseChanged();
    }

    /// <summary>Respawn intervals round to whole 5 minutes (the list arrives a few seconds after the kill).</summary>
    public static TimeSpan RoundInterval(TimeSpan raw) => raw <= TimeSpan.Zero
        ? TimeSpan.Zero
        : TimeSpan.FromMinutes(Math.Round(raw.TotalMinutes / 5) * 5);

    /// <summary>Every known slot, maps in id order, slots in place order.</summary>
    public IReadOnlyList<FieldBossTimer> Snapshot()
    {
        List<Entry> copy;
        lock (_gate) copy = _slots.Values.Select(x => x.Clone()).ToList();
        return copy.OrderBy(x => x.Map).ThenBy(x => x.Slot).Select(ToTimer).ToList();
    }

    private FieldBossTimer ToTimer(Entry x)
    {
        int place = (int)(x.Slot - x.Map * 100);
        uint? code = null;
        string? name = null;
        try
        {
            if (_gameData?.GetFieldBossBlock(x.Map) is uint block && _gameData.GetFieldBossNpcCode(block, place) is uint c)
            {
                code = c;
                name = _gameData.GetNpcName(c);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Timers", $"Field boss name lookup failed: {ex.Message}");
        }
        TimeSpan? interval = x.IntervalMinutes is int m and > 0 ? TimeSpan.FromMinutes(m) : null;
        bool learned = interval is not null;
        if (interval is null && _data.RespawnIntervals.TryGetValue((x.Map, place), out var d)) interval = d;
        return new FieldBossTimer(x.Map, x.Slot, code, string.IsNullOrWhiteSpace(name) ? $"Field boss {place}" : name!, x.Alive, x.Time,
            interval, learned, x.Seen);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _slots.Clear();
            _version++;
        }
        RaiseChanged();
    }

    // ───────────────────────────── persistence ─────────────────────────────

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson()
    {
        List<Entry> copy;
        lock (_gate) copy = _slots.Values.OrderBy(x => x.Slot).Select(x => x.Clone()).ToList();
        return JsonSerializer.Serialize(new Stored { Slots = copy }, Json);
    }

    /// <summary>Merges a saved book (entries already filled by live lists win). Bad input is ignored.</summary>
    public void LoadJson(string json, DateTime nowUtc)
    {
        Stored? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Stored>(json, Json);
        }
        catch (JsonException ex)
        {
            AppLog.Warn("Timers", $"Saved field boss timers are damaged: {ex.Message}");
            return;
        }
        if (stored?.Slots is null) return;
        lock (_gate)
        {
            foreach (var x in stored.Slots)
            {
                if (x.Map == 0 || x.Slot <= x.Map * 100 || x.Slot >= x.Map * 100 + 100) continue;
                if (nowUtc - x.Seen > StaleAfter && (x.Time is not DateTime t || nowUtc - t > StaleAfter)) continue;
                if (x.IntervalMinutes is < 0 or > 60 * 24 * 14) x.IntervalMinutes = null;
                if (_slots.TryGetValue(x.Slot, out var cur))
                {
                    cur.IntervalMinutes ??= x.IntervalMinutes;
                    continue;
                }
                _slots[x.Slot] = x;
            }
            _version++;
        }
        RaiseChanged();
    }

    public void Load(string path, DateTime nowUtc)
    {
        try
        {
            if (File.Exists(path)) LoadJson(File.ReadAllText(path), nowUtc);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Timers", $"Could not read the field boss timers: {ex.Message}");
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson());
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Timers", $"Could not save the field boss timers: {ex.Message}");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("Timers", "Changed handler failed", ex);
        }
    }

    private sealed class Stored
    {
        public int Format { get; set; } = 1;
        public List<Entry> Slots { get; set; } = new();
    }

    private sealed class Entry
    {
        public uint Map { get; set; }
        public uint Slot { get; set; }
        public bool Alive { get; set; }
        public DateTime? Time { get; set; }
        public DateTime Seen { get; set; }
        public DateTime? AliveSeen { get; set; }
        public int? IntervalMinutes { get; set; }

        public Entry Clone() => (Entry)MemberwiseClone();
    }
}
