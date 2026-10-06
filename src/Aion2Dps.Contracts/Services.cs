namespace Aion2Dps.Contracts;

// ───────────────────────────── Combat engine ─────────────────────────────

public sealed class EngineOptions
{
    /// <summary>End a trash encounter after this long without outgoing damage.</summary>
    public double IdleTimeoutSeconds { get; set; } = 10;
    /// <summary>End a boss encounter after this long without outgoing damage while the boss is alive.</summary>
    public double BossIdleTimeoutSeconds { get; set; } = 30;
    /// <summary>Completed boss fights shorter than this are not reported via EncounterCompleted.</summary>
    public double MinBossFightSeconds { get; set; } = 5;
    /// <summary>Completed trash fights shorter than this are not reported.</summary>
    public double MinTrashFightSeconds { get; set; } = 15;
    /// <summary>Report trash encounters at all (otherwise only boss/dummy/PvP fights are saved).</summary>
    public bool SaveTrashFights { get; set; } = false;
    /// <summary>In a boss encounter, also count damage to non-boss adds between the first and last boss hit.</summary>
    public bool CountAddsInBossFight { get; set; } = false;
    /// <summary>Live per-player DPS uses each player's own first hit as the clock start (late joiners not penalised).
    /// When false, the shared encounter clock is used.</summary>
    public bool LivePlayerClock { get; set; } = true;
    /// <summary>Max HP at or above this marks an unknown NPC as a boss.</summary>
    public long BossHpThreshold { get; set; } = 5_000_000;
    /// <summary>Show only the local player + party roster members (when a roster is known). False = everyone hitting the same targets.</summary>
    public bool PartyOnly { get; set; } = false;
    /// <summary>Hide an ended encounter's overlay totals after this delay from its end time, once finalized.
    /// Zero (default), negative and non-finite values keep the final display. Encounter records remain available.</summary>
    public double EndedDisplaySeconds { get; set; } = 0;
}

/// <summary>
/// Turns game events into encounters. Thread-safe: <see cref="IGameEventSink.OnEvent"/> is called on the capture thread,
/// the other members from the UI thread.
/// </summary>
public interface ICombatEngine : IGameEventSink
{
    EngineOptions Options { get; }
    MeterMode Mode { get; set; }
    LocalPlayerInfo? LocalPlayer { get; }

    /// <summary>Live overlay state at <paramref name="nowUtc"/> (pass DateTime.UtcNow when live, the replay clock otherwise).</summary>
    MeterSnapshot GetSnapshot(DateTime nowUtc);

    /// <summary>Deep copy of the active encounter, or the last ended one retained until the next fight or reset,
    /// even after its overlay display clears; null when no encounter is retained.</summary>
    EncounterRecord? GetCurrentEncounter();

    /// <summary>Drives idle timeouts when no events arrive. Call ~1×/s with the capture clock.</summary>
    void Tick(DateTime nowUtc);

    /// <summary>Manual reset: ends the active encounter (outcome ManualReset) and clears the display; keeps map/party context.</summary>
    void Reset();

    /// <summary>Cycle the displayed target among tracked targets (+1 / −1).</summary>
    void CycleTarget(int direction);

    /// <summary>Starts a training run of the given length: everything hit counts, it ends automatically, Kind = Training.</summary>
    void StartTraining(TimeSpan duration);

    /// <summary>Clears all session state (entities, party, encounters) e.g. after a capture restart.</summary>
    void ClearSession();

    /// <summary>Raised (on the capture thread) when an encounter ends and qualifies for saving.</summary>
    event Action<EncounterRecord>? EncounterCompleted;
}

// ───────────────────────────── Capture ─────────────────────────────

public sealed record AdapterInfo(string Name, string Description, IReadOnlyList<string> IPv4Addresses, bool IsLoopback);

public sealed record CaptureStatus
{
    public CaptureState State { get; init; }
    public string Message { get; init; } = "";
    public string? AdapterName { get; init; }
    public string? AdapterDescription { get; init; }
    public string? LocalEndpoint { get; init; }
    public string? ServerEndpoint { get; init; }
    public int? GameProcessId { get; init; }
    public long PacketsSeen { get; init; }
    public long BytesDelivered { get; init; }
    public long GapCount { get; init; }
    public DateTime? LastPacketUtc { get; init; }
    public string? RecordingPath { get; init; }
}

/// <summary>Live capture of the AION 2 game connection (Npcap). Delivers server→client bytes to the sink.</summary>
public interface ICaptureService : IDisposable
{
    CaptureStatus Status { get; }

    /// <summary>Raised on any thread when <see cref="Status"/> changes meaningfully (state, endpoint, adapter, ~1×/s counters).</summary>
    event Action<CaptureStatus>? StatusChanged;

    /// <summary>Lists capture adapters (empty when Npcap is missing).</summary>
    IReadOnlyList<AdapterInfo> GetAdapters();

    /// <summary>Adapter name to force; null = automatic (by the game's connection).</summary>
    string? AdapterOverride { get; set; }

    /// <summary>Starts capturing in the background (non-blocking). Retries detection until the game connection is found.</summary>
    void Start(IStreamSink sink);

    void Stop();

    /// <summary>Writes every packet of the locked game flow (both directions) to a pcapng file until stopped.</summary>
    void StartRecording(string pcapngPath);

    void StopRecording();
}

/// <summary>Replays a .pcap/.pcapng file through the same detection and reassembly as live capture.</summary>
public interface IReplaySource
{
    /// <param name="speed">1 = real time, 0 = as fast as possible.</param>
    /// <param name="clock">Receives the capture timestamp of every delivered packet (drive ICombatEngine.Tick with it).</param>
    Task ReplayAsync(string path, IStreamSink sink, double speed, Action<DateTime>? clock, CancellationToken cancellationToken);
}

// ───────────────────────────── Storage ─────────────────────────────

public sealed record FightSummary
{
    public Guid Id { get; init; }
    public DateTime StartUtc { get; init; }
    public double DurationSeconds { get; init; }
    public EncounterKind Kind { get; init; }
    public EncounterOutcome Outcome { get; init; }
    public uint? MapId { get; init; }
    public uint? BossNpcCode { get; init; }
    public long? BossMaxHp { get; init; }
    public long TotalDamage { get; init; }
    public double PartyDps { get; init; }
    public int PlayerCount { get; init; }
    public string? LocalPlayerName { get; init; }
    public CharacterClass LocalPlayerClass { get; init; }
    public long LocalDamage { get; init; }
    public double LocalDps { get; init; }
    public double? HpCheckRatio { get; init; }
    public string? Note { get; init; }
}

public sealed record FightQuery
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public uint? MapId { get; init; }
    public uint? BossNpcCode { get; init; }
    public EncounterKind? Kind { get; init; }
    public string? LocalPlayerName { get; init; }
    public bool KillsOnly { get; init; }
    public int Limit { get; init; } = 500;
    public int Offset { get; init; }
}

public sealed record TrendPoint(Guid FightId, DateTime StartUtc, double Dps, long Damage, double DurationSeconds, EncounterOutcome Outcome);

public sealed record BossTrendSummary
{
    public uint BossNpcCode { get; init; }
    public uint? MapId { get; init; }
    public int Fights { get; init; }
    public int Kills { get; init; }
    public double BestDps { get; init; }
    public double MedianDps { get; init; }
    public double LastDps { get; init; }
    public double FastestKillSeconds { get; init; }
    public DateTime LastFoughtUtc { get; init; }
}

/// <summary>Local fight history (SQLite). Thread-safe.</summary>
public interface IFightStore : IDisposable
{
    void Save(EncounterRecord record);
    EncounterRecord? Load(Guid id);
    bool Delete(Guid id);
    IReadOnlyList<FightSummary> Query(FightQuery query);
    /// <summary>Distinct local character names that have saved fights.</summary>
    IReadOnlyList<string> GetCharacters();
    /// <summary>Per-fight DPS points of <paramref name="characterName"/> on one boss, oldest first.</summary>
    IReadOnlyList<TrendPoint> GetBossTrend(uint bossNpcCode, string characterName);
    /// <summary>One row per boss the character fought.</summary>
    IReadOnlyList<BossTrendSummary> GetBossSummaries(string characterName);
    /// <summary>Best kill DPS of the character on the boss, or null.</summary>
    TrendPoint? GetPersonalBest(uint bossNpcCode, string characterName);
}
