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
/// <param name="Priority"><c>bossPriorities</c> of timers.json; scheduled bosses count as High.</param>
public sealed record FieldBossTimer(uint MapId, uint Slot, uint? NpcCode, string Name, bool Alive, DateTime? TimeUtc, TimeSpan? Interval,
    bool IntervalLearned, DateTime SeenUtc, TimerPriority Priority = TimerPriority.None, DateTime? KilledUtc = null)
{
    /// <summary>Known only from your own kill (no field-boss list slot: a map the list cannot be matched on).</summary>
    public bool FromKillOnly => Slot >= FieldBossTimerBook.KillOnlySlotBase;

    /// <summary>
    /// A scheduled boss (the Abyss siege bosses): the game announces its respawn on a whole 5 minutes (21:05:00.000) rather
    /// than kill time + interval (random seconds), and no respawn interval is known.
    /// </summary>
    public bool IsScheduled => !Alive && Interval is null && TimeUtc is DateTime t
        && t.Ticks % TimeSpan.TicksPerMinute == 0 && t.Minute % 5 == 0;

    /// <summary>Starred by default: a priority boss (High / Top; scheduled bosses are High).</summary>
    public bool IsImportant => Priority >= TimerPriority.High;

    public int Place => FromKillOnly ? 0 : (int)(Slot - MapId * 100);

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
    /// <summary>A kill teaches the interval from the next list showing the respawn time within this long.</summary>
    public static readonly TimeSpan KillLearnWindow = TimeSpan.FromMinutes(10);
    /// <summary>A list still showing the boss alive this soon after your kill is stale (the list lags the kill).</summary>
    public static readonly TimeSpan KillListLag = TimeSpan.FromSeconds(90);
    /// <summary>Synthetic slots of bosses known only from your kills: base | NPC code.</summary>
    public const uint KillOnlySlotBase = 0x8000_0000;
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
                // Your kill marked it dead a moment ago; a list sent before the server registered the death still says alive.
                if (s.Alive && !cur.Alive && cur.KillUtc is DateTime killed && e.Time - killed < KillListLag
                    && (time is null || time <= killed))
                    continue;
                if (!s.Alive && time is DateTime r && cur.KillUtc is DateTime k && e.Time >= k && e.Time - k <= KillLearnWindow && r > k)
                {
                    // Exact: the game's respawn time minus your kill time.
                    var learned = RoundInterval(r - k);
                    if (learned > TimeSpan.Zero) cur.IntervalMinutes = (int)learned.TotalMinutes;
                }
                else if (cur.Alive && !s.Alive && time is DateTime respawn && cur.AliveSeen is DateTime lastAlive
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

    /// <summary>
    /// Your party killed a field boss (a finished encounter): the slot showing that boss turns dead right away with
    /// respawn = kill + interval (learned, else the bundled default), and the next list's respawn time teaches the exact
    /// interval. A boss no list slot shows (maps the list cannot be matched on) gets a kill-only timer when its respawn
    /// interval is known (<c>bossRespawnMinutesByNpc</c>) or it is a priority boss. Returns true when the book changed.
    /// </summary>
    public bool OnKill(uint npcCode, uint? mapId, DateTime killUtc)
    {
        if (npcCode == 0) return false;
        killUtc = DateTime.SpecifyKind(killUtc, DateTimeKind.Utc);
        lock (_gate)
        {
            var slot = _slots.Values.Where(x => !IsKillOnly(x.Slot)).FirstOrDefault(x => ResolveCode(x) == npcCode)
                ?? (_slots.TryGetValue(KillOnlySlotBase | npcCode, out var ko) ? ko : null);
            if (slot is null)
            {
                if (!_data.RespawnByNpc.ContainsKey(npcCode) && !_data.BossPriorities.ContainsKey(npcCode)) return false;
                slot = new Entry { Map = mapId ?? 0, Slot = KillOnlySlotBase | npcCode, NpcCode = npcCode, Alive = true, Seen = killUtc };
                _slots[slot.Slot] = slot;
            }
            // An older kill (a replay, a late record) never rolls a newer state back.
            if (slot.KillUtc is DateTime prev && prev >= killUtc) return false;
            slot.KillUtc = killUtc;
            slot.NpcCode ??= npcCode;
            // The list already announced the respawn of this kill: keep the game's time.
            if (!slot.Alive && slot.Time is DateTime t && t > killUtc && slot.Seen >= killUtc && !IsKillOnly(slot.Slot)) return false;
            var interval = IntervalOf(slot);
            slot.Alive = false;
            slot.Time = interval is TimeSpan i ? killUtc + i : null;
            if (killUtc > slot.Seen) slot.Seen = killUtc;
            _version++;
        }
        RaiseChanged();
        return true;
    }

    /// <summary>Feeds the boss kills of a finished encounter (open world only; dungeon bosses are not field bosses).</summary>
    public bool OnEncounter(EncounterRecord record)
    {
        if (record is null || (record.MapId is uint m && _gameData?.IsInstanceMap(m) == true)) return false;
        var kills = new List<(uint Code, DateTime At)>();
        foreach (var b in record.Bosses)
            if (b.Killed && b.NpcCode is uint code)
                kills.Add((code, b.KillTimeSeconds is double sec ? record.StartUtc.AddSeconds(sec) : record.EndUtc));
        if (kills.Count == 0 && record.Outcome == EncounterOutcome.Kill && record.BossNpcCode is uint only)
            kills.Add((only, record.EndUtc));
        bool any = false;
        foreach (var (code, at) in kills) any |= OnKill(code, record.MapId, at);
        return any;
    }

    private static bool IsKillOnly(uint slot) => slot >= KillOnlySlotBase;

    private uint? ResolveCode(Entry x)
    {
        if (x.NpcCode is uint known) return known;
        if (IsKillOnly(x.Slot)) return null;
        try
        {
            return _gameData?.GetFieldBossBlock(x.Map) is uint block ? _gameData.GetFieldBossNpcCode(block, (int)(x.Slot - x.Map * 100)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private TimeSpan? IntervalOf(Entry x)
    {
        if (x.IntervalMinutes is int m and > 0) return TimeSpan.FromMinutes(m);
        if (!IsKillOnly(x.Slot) && _data.RespawnIntervals.TryGetValue((x.Map, (int)(x.Slot - x.Map * 100)), out var d)) return d;
        return ResolveCode(x) is uint c && _data.RespawnByNpc.TryGetValue(c, out var n) ? n : null;
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
        int place = IsKillOnly(x.Slot) ? 0 : (int)(x.Slot - x.Map * 100);
        uint? code = ResolveCode(x);
        string? name = null;
        try
        {
            if (code is uint c) name = _gameData?.GetNpcName(c);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Timers", $"Field boss name lookup failed: {ex.Message}");
        }
        TimeSpan? interval = IntervalOf(x);
        bool learned = x.IntervalMinutes is > 0;
        var priority = code is uint pc && _data.BossPriorities.TryGetValue(pc, out var p) ? p : TimerPriority.None;
        string fallback = code is uint fc ? $"NPC {fc}" : $"Field boss {place}";
        var timer = new FieldBossTimer(x.Map, x.Slot, code, string.IsNullOrWhiteSpace(name) ? fallback : name!, x.Alive, x.Time,
            interval, learned, x.Seen, priority, x.KillUtc);
        return timer.IsScheduled && priority < TimerPriority.High ? timer with { Priority = TimerPriority.High } : timer;
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
                bool killOnly = IsKillOnly(x.Slot) && x.NpcCode is uint kc && (KillOnlySlotBase | kc) == x.Slot;
                if (!killOnly && (x.Map == 0 || x.Slot <= x.Map * 100 || x.Slot >= x.Map * 100 + 100)) continue;
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
        /// <summary>Your latest kill of this boss (from a finished encounter).</summary>
        public DateTime? KillUtc { get; set; }
        /// <summary>The boss's NPC code when learned from a kill (slots of maps without a code block, kill-only entries).</summary>
        public uint? NpcCode { get; set; }

        public Entry Clone() => (Entry)MemberwiseClone();
    }
}
