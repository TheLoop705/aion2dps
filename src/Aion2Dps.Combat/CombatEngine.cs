namespace Aion2Dps.Combat;

/// <summary>
/// Turns decoded game events into encounters, a live <see cref="MeterSnapshot"/> and completed <see cref="EncounterRecord"/>s.
/// Thread-safe through one lock: <see cref="OnEvent"/> runs on the capture thread, the rest on the UI thread.
/// <see cref="EncounterCompleted"/> is raised outside the lock, on whichever thread ended the encounter
/// (normally the capture thread; <see cref="Tick"/>, <see cref="Reset"/>, <see cref="StartTraining"/> and
/// <see cref="ClearSession"/> can end one too).
/// </summary>
public sealed class CombatEngine : ICombatEngine
{
    /// <summary><see cref="CombatantRecord.EntityId"/> / <see cref="PlayerRow.EntityId"/> of the "Unknown summons" bucket.</summary>
    public const uint UnknownSummonsEntityId = uint.MaxValue;

    /// <summary><see cref="PlayerRow.EntityId"/> of the partial-view aggregate row (players seen only through DoT ticks
    /// and heals, <see cref="PlayerRow.AggregateCount"/> &gt; 0).</summary>
    public const uint OthersEntityId = uint.MaxValue - 1;

    private readonly object _gate = new();
    private readonly CombatCore _core;

    public CombatEngine(IGameData gameData, EngineOptions? options = null)
        : this(gameData, options, null)
    {
    }

    /// <summary>Test hook: <paramref name="clientClockMs"/> replaces the QPC millisecond clock used for ping.</summary>
    internal CombatEngine(IGameData gameData, EngineOptions? options, Func<long>? clientClockMs)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        _core = new CombatCore(gameData, options ?? new EngineOptions(), clientClockMs);
    }

    public EngineOptions Options => _core.Options;

    public MeterMode Mode
    {
        get { lock (_gate) return _core.Mode; }
        set { lock (_gate) _core.Mode = value; }
    }

    public LocalPlayerInfo? LocalPlayer
    {
        get { lock (_gate) return _core.LocalPlayerInfo; }
    }

    /// <summary>Events that threw inside the engine (counted, logged, never rethrown).</summary>
    public long EventErrors
    {
        get { lock (_gate) return _core.EventErrors; }
    }

    /// <summary>Damage records dropped by the amount sanity cap (99,999,999).</summary>
    public long DroppedRecords
    {
        get { lock (_gate) return _core.DroppedRecords; }
    }

    public event Action<EncounterRecord>? EncounterCompleted;

    /// <summary>
    /// Raised (outside the lock, on the capture thread) when the game names the local character with its own
    /// <c>33 36</c> record. Hosts persist it and pass it back as <see cref="EngineOptions.KnownLocalCharacter"/> after a
    /// restart, so a meter started mid-session can name the inferred local player.
    /// </summary>
    public event Action<KnownCharacter>? LocalCharacterIdentified;

    public void OnEvent(GameEvent gameEvent)
    {
        if (gameEvent is null) return;
        List<EncounterRecord>? done;
        KnownCharacter? known;
        lock (_gate)
        {
            try
            {
                _core.Handle(gameEvent);
            }
            catch (Exception ex)
            {
                _core.EventErrors++;
                AppLog.Warn("Combat", $"Event {gameEvent.GetType().Name} failed: {ex.Message}");
            }
            done = _core.TakeCompleted();
            known = _core.PendingKnownCharacter;
            _core.PendingKnownCharacter = null;
        }
        Raise(done);
        if (known != null) RaiseKnown(known);
    }

    private void RaiseKnown(KnownCharacter known)
    {
        try
        {
            LocalCharacterIdentified?.Invoke(known);
        }
        catch (Exception ex)
        {
            AppLog.Error("Combat", "LocalCharacterIdentified handler failed", ex);
        }
    }

    public MeterSnapshot GetSnapshot(DateTime nowUtc)
    {
        lock (_gate)
        {
            try
            {
                return SnapshotBuilder.Build(_core, nowUtc);
            }
            catch (Exception ex)
            {
                AppLog.Error("Combat", "Snapshot failed", ex);
                return MeterSnapshot.Empty with { TimeUtc = nowUtc, Mode = _core.Mode, StatusText = "Meter error" };
            }
        }
    }

    public EncounterRecord? GetCurrentEncounter()
    {
        lock (_gate)
        {
            var enc = _core.Current;
            if (enc == null) return null;
            _core.RefreshIdentities(enc);
            return EncounterRecordBuilder.Build(_core, enc);
        }
    }

    public void Tick(DateTime nowUtc) => Run(() => _core.CheckTimers(nowUtc));

    public void Reset() => Run(_core.Reset);

    public void CycleTarget(int direction) => Run(() => _core.CycleTarget(direction));

    public void StartTraining(TimeSpan duration) => Run(() => _core.StartTraining(duration));

    public void ClearSession() => Run(_core.ClearSession);

    /// <summary>Marks the active encounter as having TCP gaps / resyncs (its numbers may be incomplete).</summary>
    public void NotifyCaptureGap() => Run(_core.NotifyCaptureGap);

    private void Run(Action action)
    {
        List<EncounterRecord>? done;
        lock (_gate)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("Combat", "Engine command failed", ex);
            }
            done = _core.TakeCompleted();
        }
        Raise(done);
    }

    private void Raise(List<EncounterRecord>? done)
    {
        if (done == null) return;
        var handler = EncounterCompleted;
        if (handler == null) return;
        foreach (var record in done)
        {
            try
            {
                handler(record);
            }
            catch (Exception ex)
            {
                AppLog.Error("Combat", "EncounterCompleted handler failed", ex);
            }
        }
    }
}
