using System.Globalization;
using Aion2Dps.Contracts;
using Microsoft.Data.Sqlite;

namespace Aion2Dps.Storage;

/// <summary>
/// Local fight history in a single SQLite file (WAL mode).
/// <para>
/// Thread-safety: every operation opens its own (pooled) connection, so reads run concurrently; writes are additionally
/// serialized by an in-process lock and use <c>BEGIN IMMEDIATE</c> transactions.
/// </para>
/// <para>
/// Storage conventions: ids are lowercase "D" GUID strings; <c>start_utc</c> is UTC ticks (INTEGER); <c>kind</c> and
/// <c>outcome</c> are enum names (TEXT); <c>record</c> is <see cref="EncounterRecordSerializer.Compress"/> output.
/// Timestamps with <see cref="DateTimeKind.Unspecified"/> are treated as UTC; every returned DateTime is UTC.
/// </para>
/// </summary>
public sealed class SqliteFightStore : IFightStore
{
    private const string LogArea = "Storage";

    /// <summary>The schema version this build creates/migrates to.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private volatile bool _disposed;

    /// <summary>Opens (creating directories and the file if needed) and migrates the database at <paramref name="databasePath"/>.</summary>
    public SqliteFightStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();

        using var conn = Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Migrate(conn);
    }

    /// <summary>%APPDATA%/Aion2Dps/history.db</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aion2Dps", "history.db");

    /// <summary>Absolute path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>The schema version recorded in the database (normally <see cref="CurrentSchemaVersion"/>).</summary>
    public int SchemaVersion
    {
        get
        {
            using var conn = Open();
            return ReadSchemaVersion(conn);
        }
    }

    // ───────────────────────────── schema ─────────────────────────────

    private static readonly string[][] Migrations =
    [
        // v1: initial schema
        [
            """
            CREATE TABLE IF NOT EXISTS fights (
                id             TEXT    PRIMARY KEY NOT NULL,
                start_utc      INTEGER NOT NULL,
                duration       REAL    NOT NULL,
                kind           TEXT    NOT NULL,
                outcome        TEXT    NOT NULL,
                map_id         INTEGER NULL,
                boss_npc_code  INTEGER NULL,
                boss_max_hp    INTEGER NULL,
                total_damage   INTEGER NOT NULL,
                party_dps      REAL    NOT NULL,
                player_count   INTEGER NOT NULL,
                local_name     TEXT    NULL,
                local_class    TEXT    NOT NULL,
                local_damage   INTEGER NOT NULL,
                local_dps      REAL    NOT NULL,
                hp_check_ratio REAL    NULL,
                note           TEXT    NULL,
                record_version INTEGER NOT NULL,
                record         BLOB    NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS combatants (
                fight_id     TEXT    NOT NULL REFERENCES fights(id) ON DELETE CASCADE,
                name         TEXT    NOT NULL,
                class        TEXT    NOT NULL,
                kind         TEXT    NOT NULL,
                is_local     INTEGER NOT NULL,
                is_party     INTEGER NOT NULL,
                damage       INTEGER NOT NULL,
                dps          REAL    NOT NULL,
                contribution REAL    NOT NULL
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_fights_boss_start ON fights(boss_npc_code, start_utc);",
            "CREATE INDEX IF NOT EXISTS ix_fights_local_name ON fights(local_name);",
            "CREATE INDEX IF NOT EXISTS ix_fights_start ON fights(start_utc);",
            "CREATE INDEX IF NOT EXISTS ix_combatants_fight ON combatants(fight_id);",
            "CREATE INDEX IF NOT EXISTS ix_combatants_name ON combatants(name, fight_id);",
        ],
    ];

    private void Migrate(SqliteConnection conn)
    {
        lock (_writeLock)
        {
            using var tx = conn.BeginTransaction(deferred: false);
            Exec(conn, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY NOT NULL, applied_utc TEXT NOT NULL);", tx);
            int version = ReadSchemaVersion(conn, tx);
            if (version > Migrations.Length)
            {
                AppLog.Warn(LogArea, $"Database schema v{version} is newer than this build (v{Migrations.Length}); opening anyway.");
            }
            for (int v = version + 1; v <= Migrations.Length; v++)
            {
                foreach (var sql in Migrations[v - 1]) Exec(conn, sql, tx);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO schema_version(version, applied_utc) VALUES ($v, $t);";
                cmd.Parameters.AddWithValue("$v", v);
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
                AppLog.Info(LogArea, $"Migrated fight history to schema v{v}.");
            }
            tx.Commit();
        }
    }

    private static int ReadSchemaVersion(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ───────────────────────────── writes ─────────────────────────────

    public void Save(EncounterRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ThrowIfDisposed();

        // Serialize outside the lock: it is the expensive part for big fights.
        byte[] blob = EncounterRecordSerializer.Compress(record);
        var local = FindLocal(record);
        string id = IdText(record.Id);

        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction(deferred: false);

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO fights (id, start_utc, duration, kind, outcome, map_id, boss_npc_code, boss_max_hp,
                        total_damage, party_dps, player_count, local_name, local_class, local_damage, local_dps,
                        hp_check_ratio, note, record_version, record)
                    VALUES ($id, $start, $dur, $kind, $outcome, $map, $boss, $bossHp, $total, $partyDps, $players,
                        $localName, $localClass, $localDmg, $localDps, $hpRatio, $note, $recVer, $record)
                    ON CONFLICT(id) DO UPDATE SET
                        start_utc = excluded.start_utc, duration = excluded.duration, kind = excluded.kind,
                        outcome = excluded.outcome, map_id = excluded.map_id, boss_npc_code = excluded.boss_npc_code,
                        boss_max_hp = excluded.boss_max_hp, total_damage = excluded.total_damage,
                        party_dps = excluded.party_dps, player_count = excluded.player_count,
                        local_name = excluded.local_name, local_class = excluded.local_class,
                        local_damage = excluded.local_damage, local_dps = excluded.local_dps,
                        hp_check_ratio = excluded.hp_check_ratio, note = excluded.note,
                        record_version = excluded.record_version, record = excluded.record;
                    """;
                var p = cmd.Parameters;
                p.AddWithValue("$id", id);
                p.AddWithValue("$start", ToTicks(record.StartUtc));
                p.AddWithValue("$dur", Finite(record.DurationSeconds));
                p.AddWithValue("$kind", record.Kind.ToString());
                p.AddWithValue("$outcome", record.Outcome.ToString());
                p.AddWithValue("$map", Db(record.MapId));
                p.AddWithValue("$boss", Db(record.BossNpcCode));
                p.AddWithValue("$bossHp", Db(record.BossMaxHp));
                p.AddWithValue("$total", record.TotalDamage);
                p.AddWithValue("$partyDps", Finite(record.PartyDps));
                p.AddWithValue("$players", record.Combatants.Count(c => c is not null && c.Kind == CombatantKind.Player));
                p.AddWithValue("$localName", (object?)(record.LocalPlayerName ?? local?.Name) ?? DBNull.Value);
                p.AddWithValue("$localClass", (record.LocalPlayerClass != CharacterClass.Unknown || local is null
                    ? record.LocalPlayerClass : local.Class).ToString());
                p.AddWithValue("$localDmg", local?.Damage ?? 0L);
                p.AddWithValue("$localDps", Finite(local?.Dps ?? 0));
                p.AddWithValue("$hpRatio", record.HpCheck is { } hc && double.IsFinite(hc.Ratio) ? hc.Ratio : DBNull.Value);
                p.AddWithValue("$note", (object?)record.Note ?? DBNull.Value);
                p.AddWithValue("$recVer", record.SchemaVersion);
                p.AddWithValue("$record", blob);
                cmd.ExecuteNonQuery();
            }

            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM combatants WHERE fight_id = $id;";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }

            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO combatants (fight_id, name, class, kind, is_local, is_party, damage, dps, contribution)
                    VALUES ($id, $name, $class, $kind, $local, $party, $dmg, $dps, $contrib);
                    """;
                ins.Parameters.AddWithValue("$id", id);
                var pName = ins.Parameters.Add("$name", SqliteType.Text);
                var pClass = ins.Parameters.Add("$class", SqliteType.Text);
                var pKind = ins.Parameters.Add("$kind", SqliteType.Text);
                var pLocal = ins.Parameters.Add("$local", SqliteType.Integer);
                var pParty = ins.Parameters.Add("$party", SqliteType.Integer);
                var pDmg = ins.Parameters.Add("$dmg", SqliteType.Integer);
                var pDps = ins.Parameters.Add("$dps", SqliteType.Real);
                var pContrib = ins.Parameters.Add("$contrib", SqliteType.Real);
                foreach (var c in record.Combatants)
                {
                    if (c is null) continue;
                    pName.Value = c.Name ?? "";
                    pClass.Value = c.Class.ToString();
                    pKind.Value = c.Kind.ToString();
                    pLocal.Value = c.IsLocal ? 1 : 0;
                    pParty.Value = c.IsPartyMember ? 1 : 0;
                    pDmg.Value = c.Damage;
                    pDps.Value = Finite(c.Dps);
                    pContrib.Value = Finite(c.Contribution);
                    ins.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    public bool Delete(Guid id)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction(deferred: false);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$id", IdText(id));
            cmd.CommandText = "DELETE FROM combatants WHERE fight_id = $id;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM fights WHERE id = $id;";
            int n = cmd.ExecuteNonQuery();
            tx.Commit();
            return n > 0;
        }
    }

    // ───────────────────────────── reads ─────────────────────────────

    public EncounterRecord? Load(Guid id)
    {
        ThrowIfDisposed();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT record FROM fights WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", IdText(id));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0)) return null;
        try
        {
            using var blob = reader.GetStream(0);
            var record = EncounterRecordSerializer.Decompress(blob);
            if (record is null) AppLog.Warn(LogArea, $"Fight {id}: stored record is empty.");
            return record;
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, $"Fight {id}: stored record is corrupt and cannot be loaded", ex);
            return null;
        }
    }

    /// <summary>Newest first. <see cref="FightQuery.FromUtc"/> and <see cref="FightQuery.ToUtc"/> are inclusive bounds on the start time;
    /// a <see cref="FightQuery.Limit"/> ≤ 0 means no limit.</summary>
    public IReadOnlyList<FightSummary> Query(FightQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (query.FromUtc is { } from) { where.Add("start_utc >= $from"); cmd.Parameters.AddWithValue("$from", ToTicks(from)); }
        if (query.ToUtc is { } to) { where.Add("start_utc <= $to"); cmd.Parameters.AddWithValue("$to", ToTicks(to)); }
        if (query.MapId is { } map) { where.Add("map_id = $map"); cmd.Parameters.AddWithValue("$map", (long)map); }
        if (query.BossNpcCode is { } boss) { where.Add("boss_npc_code = $boss"); cmd.Parameters.AddWithValue("$boss", (long)boss); }
        if (query.Kind is { } kind) { where.Add("kind = $kind"); cmd.Parameters.AddWithValue("$kind", kind.ToString()); }
        if (query.LocalPlayerName is { } name) { where.Add("local_name = $name"); cmd.Parameters.AddWithValue("$name", name); }
        if (query.KillsOnly) { where.Add("outcome = $kill"); cmd.Parameters.AddWithValue("$kill", nameof(EncounterOutcome.Kill)); }

        cmd.CommandText = $"""
            SELECT id, start_utc, duration, kind, outcome, map_id, boss_npc_code, boss_max_hp, total_damage, party_dps,
                   player_count, local_name, local_class, local_damage, local_dps, hp_check_ratio, note
            FROM fights
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY start_utc DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", query.Limit > 0 ? query.Limit : -1);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

        var list = new List<FightSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FightSummary
            {
                Id = Guid.Parse(r.GetString(0)),
                StartUtc = FromTicks(r.GetInt64(1)),
                DurationSeconds = r.GetDouble(2),
                Kind = ParseEnum<EncounterKind>(r.GetString(3)),
                Outcome = ParseEnum<EncounterOutcome>(r.GetString(4)),
                MapId = r.IsDBNull(5) ? null : (uint)r.GetInt64(5),
                BossNpcCode = r.IsDBNull(6) ? null : (uint)r.GetInt64(6),
                BossMaxHp = r.IsDBNull(7) ? null : r.GetInt64(7),
                TotalDamage = r.GetInt64(8),
                PartyDps = r.GetDouble(9),
                PlayerCount = r.GetInt32(10),
                LocalPlayerName = r.IsDBNull(11) ? null : r.GetString(11),
                LocalPlayerClass = ParseEnum<CharacterClass>(r.GetString(12)),
                LocalDamage = r.GetInt64(13),
                LocalDps = r.GetDouble(14),
                HpCheckRatio = r.IsDBNull(15) ? null : r.GetDouble(15),
                Note = r.IsDBNull(16) ? null : r.GetString(16),
            });
        }
        return list;
    }

    /// <summary>Distinct non-empty local character names, most recently played first.</summary>
    public IReadOnlyList<string> GetCharacters()
    {
        ThrowIfDisposed();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT local_name FROM fights
            WHERE local_name IS NOT NULL AND local_name <> ''
            GROUP BY local_name
            ORDER BY MAX(start_utc) DESC, local_name;
            """;
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// One point per fight on the boss in which a friendly combatant named <paramref name="characterName"/> took part
    /// (not only fights where it was the local player), oldest first. Dps/Damage are that combatant's numbers.
    /// </summary>
    public IReadOnlyList<TrendPoint> GetBossTrend(uint bossNpcCode, string characterName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(characterName)) return [];
        var rows = ReadCharacterFights(characterName, bossNpcCode);
        return rows.Select(x => x.Point).ToList();
    }

    /// <summary>
    /// One row per boss the character fought, most recently fought first. Best/Median DPS are over kills when the
    /// boss has at least one kill, otherwise over all attempts; LastDps/MapId come from the most recent fight;
    /// FastestKillSeconds is 0 when there is no kill.
    /// </summary>
    public IReadOnlyList<BossTrendSummary> GetBossSummaries(string characterName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(characterName)) return [];
        var rows = ReadCharacterFights(characterName, bossNpcCode: null);
        return rows
            .GroupBy(x => x.Boss)
            .Select(g =>
            {
                var fights = g.ToList(); // oldest first
                var kills = fights.Where(x => x.Point.Outcome == EncounterOutcome.Kill).ToList();
                var basis = kills.Count > 0 ? kills : fights;
                var last = fights[^1];
                return new BossTrendSummary
                {
                    BossNpcCode = g.Key,
                    MapId = last.MapId,
                    Fights = fights.Count,
                    Kills = kills.Count,
                    BestDps = basis.Max(x => x.Point.Dps),
                    MedianDps = Median(basis.Select(x => x.Point.Dps)),
                    LastDps = last.Point.Dps,
                    FastestKillSeconds = kills.Count > 0 ? kills.Min(x => x.Point.DurationSeconds) : 0,
                    LastFoughtUtc = last.Point.StartUtc,
                };
            })
            .OrderByDescending(s => s.LastFoughtUtc)
            .ThenBy(s => s.BossNpcCode)
            .ToList();
    }

    /// <summary>The kill with the highest DPS of the character on the boss (earliest wins ties), or null.</summary>
    public TrendPoint? GetPersonalBest(uint bossNpcCode, string characterName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(characterName)) return null;
        TrendPoint? best = null;
        foreach (var row in ReadCharacterFights(characterName, bossNpcCode))
        {
            if (row.Point.Outcome != EncounterOutcome.Kill) continue;
            if (best is null || row.Point.Dps > best.Dps) best = row.Point;
        }
        return best;
    }

    /// <summary>Median of the values (mean of the two middle values for an even count); 0 for an empty set.</summary>
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.ToArray();
        if (sorted.Length == 0) return 0;
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private readonly record struct CharacterFight(uint Boss, uint? MapId, TrendPoint Point);

    /// <summary>Fights with a boss in which the named friendly combatant appears, oldest first.</summary>
    private List<CharacterFight> ReadCharacterFights(string characterName, uint? bossNpcCode)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // MAX(c.dps) collapses the (rare) case of two same-named combatants in one fight; SQLite returns the
        // bare columns (damage) from the row that produced the max.
        cmd.CommandText = $"""
            SELECT f.id, f.start_utc, f.duration, f.outcome, f.boss_npc_code, f.map_id, MAX(c.dps) AS dps, c.damage
            FROM fights f
            JOIN combatants c ON c.fight_id = f.id
            WHERE c.name = $name AND c.kind <> $enemy AND f.boss_npc_code IS NOT NULL
                  {(bossNpcCode is null ? "" : "AND f.boss_npc_code = $boss")}
            GROUP BY f.id
            ORDER BY f.start_utc ASC, f.id ASC;
            """;
        cmd.Parameters.AddWithValue("$name", characterName);
        cmd.Parameters.AddWithValue("$enemy", nameof(CombatantKind.EnemyPlayer));
        if (bossNpcCode is { } b) cmd.Parameters.AddWithValue("$boss", (long)b);

        var list = new List<CharacterFight>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var point = new TrendPoint(
                Guid.Parse(r.GetString(0)),
                FromTicks(r.GetInt64(1)),
                r.GetDouble(6),
                r.GetInt64(7),
                r.GetDouble(2),
                ParseEnum<EncounterOutcome>(r.GetString(3)));
            list.Add(new CharacterFight((uint)r.GetInt64(4), r.IsDBNull(5) ? null : (uint)r.GetInt64(5), point));
        }
        return list;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static CombatantRecord? FindLocal(EncounterRecord record)
    {
        CombatantRecord? byName = null;
        foreach (var c in record.Combatants)
        {
            if (c is null) continue;
            if (c.IsLocal) return c;
            if (byName is null && record.LocalPlayerName is { Length: > 0 } n && c.Kind == CombatantKind.Player && c.Name == n) byName = c;
        }
        return byName;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string IdText(Guid id) => id.ToString("D");

    private static long ToTicks(DateTime t) => t.Kind switch
    {
        DateTimeKind.Local => t.ToUniversalTime().Ticks,
        _ => t.Ticks,
    };

    private static DateTime FromTicks(long ticks) => new(ticks, DateTimeKind.Utc);

    private static object Db<T>(T? value) where T : struct => value is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : DBNull.Value;

    private static double Finite(double v) => double.IsFinite(v) ? v : 0;

    private static T ParseEnum<T>(string text) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var v) ? v : default;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Release pooled handles so the file can be moved/deleted after disposal.
            using var conn = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(conn);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, $"Clearing the connection pool failed: {ex.Message}");
        }
    }
}
