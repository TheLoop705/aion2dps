using System.Diagnostics;
using Aion2Dps.Contracts;
using Microsoft.Data.Sqlite;

namespace Aion2Dps.Storage.Tests;

public sealed class SqliteFightStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aion2dps-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly List<SqliteFightStore> _stores = new();

    private string DbPath => Path.Combine(_dir, "sub", "history.db");

    private SqliteFightStore NewStore(string? path = null)
    {
        var s = new SqliteFightStore(path ?? DbPath);
        _stores.Add(s);
        return s;
    }

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ───────────────────────── setup / schema ─────────────────────────

    [Fact]
    public void Constructor_creates_directory_file_wal_and_schema()
    {
        using var store = NewStore();
        Assert.True(File.Exists(DbPath));
        Assert.Equal(SqliteFightStore.CurrentSchemaVersion, store.SchemaVersion);

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!, ignoreCase: true);
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','index') ORDER BY name;";
        var names = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
        Assert.Contains("fights", names);
        Assert.Contains("combatants", names);
        Assert.Contains("schema_version", names);
        Assert.Contains("ix_fights_boss_start", names);
        Assert.Contains("ix_fights_local_name", names);
        Assert.Contains("ix_fights_start", names);
    }

    [Fact]
    public void Migrates_from_an_empty_existing_file_and_is_idempotent_on_reopen()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllBytes(DbPath, []);
        using (var store = NewStore())
        {
            Assert.Equal(1, store.SchemaVersion);
            store.Save(TestRecords.Fight(TestRecords.T0));
        }
        using var again = NewStore();
        Assert.Equal(1, again.SchemaVersion);
        Assert.Single(again.Query(new FightQuery()));

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void DefaultPath_is_under_appdata()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aion2Dps", "history.db");
        Assert.Equal(expected, SqliteFightStore.DefaultPath());
    }

    // ───────────────────────── round trip ─────────────────────────

    [Fact]
    public void Rich_record_round_trips_exactly()
    {
        using var store = NewStore();
        var rec = TestRecords.Rich();
        store.Save(rec);
        var loaded = store.Load(rec.Id);
        Assert.NotNull(loaded);
        Assert.Equal(EncounterRecordSerializer.ToJson(rec), EncounterRecordSerializer.ToJson(loaded!));
        Assert.Equal(DateTimeKind.Utc, loaded!.StartUtc.Kind);
        Assert.Equal(rec.StartUtc.Ticks, loaded.StartUtc.Ticks);
        Assert.Equal(rec.Hits[17].T, loaded.Hits[17].T);
        Assert.Equal(rec.Hits[17].Flags, loaded.Hits[17].Flags);
        Assert.Null(loaded.Combatants[0].Defense.Endured);
        Assert.Equal(6, loaded.Combatants[0].Defense.Blocked);
    }

    [Fact]
    public void Json_uses_enum_names_and_numeric_flags()
    {
        var json = EncounterRecordSerializer.ToJson(TestRecords.Rich(3));
        Assert.Contains("\"kind\":\"Boss\"", json);
        Assert.Contains("\"outcome\":\"Wipe\"", json);
        Assert.Contains("\"class\":\"Brawler\"", json);
        Assert.Matches("\"flags\":\\d+", json);
    }

    [Fact]
    public void Summary_columns_are_derived_from_record()
    {
        using var store = NewStore();
        var rec = TestRecords.Rich();
        store.Save(rec);
        var s = Assert.Single(store.Query(new FightQuery()));
        Assert.Equal(rec.Id, s.Id);
        Assert.Equal(rec.StartUtc, s.StartUtc);
        Assert.Equal(DateTimeKind.Utc, s.StartUtc.Kind);
        Assert.Equal(rec.DurationSeconds, s.DurationSeconds);
        Assert.Equal(EncounterKind.Boss, s.Kind);
        Assert.Equal(EncounterOutcome.Wipe, s.Outcome);
        Assert.Equal(rec.MapId, s.MapId);
        Assert.Equal(rec.BossNpcCode, s.BossNpcCode);
        Assert.Equal(rec.BossMaxHp, s.BossMaxHp);
        Assert.Equal(rec.TotalDamage, s.TotalDamage);
        Assert.Equal(rec.PartyDps, s.PartyDps);
        Assert.Equal(1, s.PlayerCount); // enemy player and summon bucket excluded
        Assert.Equal("Ärger한글", s.LocalPlayerName);
        Assert.Equal(CharacterClass.Brawler, s.LocalPlayerClass);
        Assert.Equal(60_000_000_000, s.LocalDamage);
        Assert.Equal(320_427_236.3, s.LocalDps);
        Assert.Equal(0.99999964, s.HpCheckRatio);
        Assert.Equal(rec.Note, s.Note);
    }

    [Fact]
    public void Save_is_upsert()
    {
        using var store = NewStore();
        var rec = TestRecords.Fight(TestRecords.T0, others: [("Bob", 500)]);
        store.Save(rec);
        rec.Outcome = EncounterOutcome.Wipe;
        rec.Note = "edited";
        rec.Combatants.RemoveAt(1);
        rec.Combatants[0].Dps = 1234;
        store.Save(rec);

        var s = Assert.Single(store.Query(new FightQuery()));
        Assert.Equal(EncounterOutcome.Wipe, s.Outcome);
        Assert.Equal("edited", s.Note);
        Assert.Equal(1234, s.LocalDps);
        Assert.Equal(1, s.PlayerCount);
        Assert.Empty(store.GetBossTrend(1001, "Bob"));
        Assert.Equal("edited", store.Load(rec.Id)!.Note);
    }

    [Fact]
    public void Load_unknown_id_returns_null()
    {
        using var store = NewStore();
        Assert.Null(store.Load(Guid.NewGuid()));
    }

    [Fact]
    public void Corrupt_blob_returns_null_and_logs()
    {
        var logs = new List<string>();
        AppLog.SetSink((level, area, msg) => { lock (logs) logs.Add($"{level} {area} {msg}"); });
        try
        {
            using var store = NewStore();
            var rec = TestRecords.Fight(TestRecords.T0);
            store.Save(rec);
            using (var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE fights SET record = $b WHERE id = $id;";
                cmd.Parameters.AddWithValue("$b", new byte[] { 1, 2, 3, 4, 5 });
                cmd.Parameters.AddWithValue("$id", rec.Id.ToString("D"));
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }
            Assert.Null(store.Load(rec.Id));
            Assert.Single(store.Query(new FightQuery())); // summaries still work
            lock (logs) Assert.Contains(logs, l => l.Contains("corrupt") && l.Contains(rec.Id.ToString()));
        }
        finally
        {
            AppLog.SetSink(null);
        }
    }

    [Fact]
    public void Delete_removes_fight_and_combatants()
    {
        using var store = NewStore();
        var a = TestRecords.Fight(TestRecords.T0, others: [("Bob", 500)]);
        var b = TestRecords.Fight(TestRecords.T0.AddHours(1));
        store.Save(a);
        store.Save(b);
        Assert.True(store.Delete(a.Id));
        Assert.False(store.Delete(a.Id));
        Assert.Null(store.Load(a.Id));
        Assert.NotNull(store.Load(b.Id));
        Assert.Equal([b.Id], store.Query(new FightQuery()).Select(s => s.Id));
        Assert.Empty(store.GetBossTrend(1001, "Bob"));
    }

    [Fact]
    public void Data_persists_across_reopen()
    {
        var rec = TestRecords.Rich();
        using (var store = NewStore()) store.Save(rec);
        SqliteConnection.ClearAllPools();
        using var reopened = NewStore();
        var loaded = reopened.Load(rec.Id);
        Assert.Equal(EncounterRecordSerializer.ToJson(rec), EncounterRecordSerializer.ToJson(loaded!));
        Assert.Equal(["Ärger한글"], reopened.GetCharacters());
    }

    [Fact]
    public void Disposed_store_throws()
    {
        var store = NewStore();
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Query(new FightQuery()));
        store.Dispose(); // idempotent
    }

    // ───────────────────────── query ─────────────────────────

    [Fact]
    public void Query_honours_every_filter_and_orders_newest_first()
    {
        using var store = NewStore();
        var t = TestRecords.T0;
        var f1 = TestRecords.Fight(t, boss: 1001, map: 500, outcome: EncounterOutcome.Kill, local: "Alice");
        var f2 = TestRecords.Fight(t.AddDays(1), boss: 1001, map: 500, outcome: EncounterOutcome.Wipe, local: "Alice");
        var f3 = TestRecords.Fight(t.AddDays(2), boss: 1002, map: 501, outcome: EncounterOutcome.Kill, local: "Carol");
        var f4 = TestRecords.Fight(t.AddDays(3), boss: null, map: 501, kind: EncounterKind.Trash, outcome: EncounterOutcome.Timeout, local: "Alice");
        var f5 = TestRecords.Fight(t.AddDays(4), boss: 9000, map: 502, kind: EncounterKind.Dummy, outcome: EncounterOutcome.ManualReset, local: "Carol");
        foreach (var f in new[] { f3, f1, f5, f2, f4 }) store.Save(f);

        Guid[] Ids(FightQuery q) => store.Query(q).Select(s => s.Id).ToArray();

        Assert.Equal([f5.Id, f4.Id, f3.Id, f2.Id, f1.Id], Ids(new FightQuery()));
        Assert.Equal([f3.Id, f2.Id], Ids(new FightQuery { FromUtc = t.AddDays(1), ToUtc = t.AddDays(2) })); // inclusive bounds
        Assert.Equal([f5.Id, f4.Id], Ids(new FightQuery { FromUtc = t.AddDays(2).AddTicks(1) }));
        Assert.Equal([f2.Id, f1.Id], Ids(new FightQuery { ToUtc = t.AddDays(1) }));
        Assert.Equal([f4.Id, f3.Id], Ids(new FightQuery { MapId = 501 }));
        Assert.Equal([f2.Id, f1.Id], Ids(new FightQuery { BossNpcCode = 1001 }));
        Assert.Equal([f4.Id], Ids(new FightQuery { Kind = EncounterKind.Trash }));
        Assert.Equal([f5.Id], Ids(new FightQuery { Kind = EncounterKind.Dummy }));
        Assert.Equal([f4.Id, f2.Id, f1.Id], Ids(new FightQuery { LocalPlayerName = "Alice" }));
        Assert.Equal([f3.Id, f1.Id], Ids(new FightQuery { KillsOnly = true }));
        Assert.Equal([f1.Id], Ids(new FightQuery { KillsOnly = true, LocalPlayerName = "Alice", BossNpcCode = 1001, MapId = 500, Kind = EncounterKind.Boss }));
        Assert.Empty(Ids(new FightQuery { LocalPlayerName = "Nobody" }));
    }

    [Fact]
    public void Query_local_time_bounds_are_converted_to_utc()
    {
        using var store = NewStore();
        var f = TestRecords.Fight(TestRecords.T0);
        store.Save(f);
        var local = TestRecords.T0.ToLocalTime();
        Assert.Single(store.Query(new FightQuery { FromUtc = local, ToUtc = local }));
    }

    [Fact]
    public void Query_paging()
    {
        using var store = NewStore();
        var all = Enumerable.Range(0, 25).Select(i => TestRecords.Fight(TestRecords.T0.AddMinutes(i))).ToList();
        foreach (var f in all) store.Save(f);
        var newestFirst = all.AsEnumerable().Reverse().Select(f => f.Id).ToArray();

        Assert.Equal(newestFirst[..10], store.Query(new FightQuery { Limit = 10 }).Select(s => s.Id));
        Assert.Equal(newestFirst[10..20], store.Query(new FightQuery { Limit = 10, Offset = 10 }).Select(s => s.Id));
        Assert.Equal(newestFirst[20..], store.Query(new FightQuery { Limit = 10, Offset = 20 }).Select(s => s.Id));
        Assert.Empty(store.Query(new FightQuery { Limit = 10, Offset = 30 }));
        Assert.Equal(25, store.Query(new FightQuery { Limit = 0 }).Count); // no limit
        Assert.Equal(25, store.Query(new FightQuery()).Count);              // default 500
    }

    // ───────────────────────── characters / trends ─────────────────────────

    [Fact]
    public void GetCharacters_returns_distinct_local_names_most_recent_first()
    {
        using var store = NewStore();
        store.Save(TestRecords.Fight(TestRecords.T0, local: "Alice", others: [("Bob", 1)]));
        store.Save(TestRecords.Fight(TestRecords.T0.AddDays(1), local: "Carol"));
        store.Save(TestRecords.Fight(TestRecords.T0.AddDays(2), local: "Alice"));
        var noName = TestRecords.Fight(TestRecords.T0.AddDays(3), local: "x");
        noName.LocalPlayerName = null;
        noName.Combatants.Clear();
        store.Save(noName);
        Assert.Equal(["Alice", "Carol"], store.GetCharacters());
    }

    [Fact]
    public void GetBossTrend_is_oldest_first_and_matches_any_combatant_by_name()
    {
        using var store = NewStore();
        var t = TestRecords.T0;
        var a = TestRecords.Fight(t.AddDays(2), localDps: 1500, outcome: EncounterOutcome.Wipe, duration: 50, others: [("Bob", 700)]);
        var b = TestRecords.Fight(t, localDps: 1000, others: [("Bob", 600)]);
        var c = TestRecords.Fight(t.AddDays(1), local: "Bob", localDps: 900, others: [("Alice", 1200)]); // Alice as party member
        var other = TestRecords.Fight(t.AddDays(3), boss: 1002, localDps: 9999);
        foreach (var f in new[] { a, b, c, other }) store.Save(f);

        var trend = store.GetBossTrend(1001, "Alice");
        Assert.Equal([b.Id, c.Id, a.Id], trend.Select(p => p.FightId));
        Assert.Equal([1000d, 1200d, 1500d], trend.Select(p => p.Dps));
        Assert.Equal([100_000L, 120_000L, 75_000L], trend.Select(p => p.Damage));
        Assert.Equal(EncounterOutcome.Wipe, trend[2].Outcome);
        Assert.Equal(50, trend[2].DurationSeconds);
        Assert.Equal(b.StartUtc, trend[0].StartUtc);

        Assert.Equal([600d, 900d, 700d], store.GetBossTrend(1001, "Bob").Select(p => p.Dps));
        Assert.Empty(store.GetBossTrend(1001, "Nobody"));
        Assert.Empty(store.GetBossTrend(4242, "Alice"));
    }

    [Fact]
    public void GetBossSummaries_computes_counts_best_median_last_fastest()
    {
        using var store = NewStore();
        var t = TestRecords.T0;
        // Boss 1001: kills at 1000, 3000, 2000, 4000 (median 2500), plus a wipe at 9000 (ignored for best/median).
        store.Save(TestRecords.Fight(t, boss: 1001, map: 500, localDps: 1000, duration: 120));
        store.Save(TestRecords.Fight(t.AddDays(1), boss: 1001, map: 500, localDps: 3000, duration: 90));
        store.Save(TestRecords.Fight(t.AddDays(2), boss: 1001, map: 500, localDps: 9000, duration: 10, outcome: EncounterOutcome.Wipe));
        store.Save(TestRecords.Fight(t.AddDays(3), boss: 1001, map: 500, localDps: 2000, duration: 80));
        store.Save(TestRecords.Fight(t.AddDays(4), boss: 1001, map: 501, localDps: 4000, duration: 100));
        // Boss 1002: wipes only, 100/300/200 -> median 200, last = 200.
        store.Save(TestRecords.Fight(t.AddDays(5), boss: 1002, map: 600, localDps: 100, outcome: EncounterOutcome.Wipe));
        store.Save(TestRecords.Fight(t.AddDays(6), boss: 1002, map: 600, localDps: 300, outcome: EncounterOutcome.Wipe));
        store.Save(TestRecords.Fight(t.AddDays(7), boss: 1002, map: 600, localDps: 200, outcome: EncounterOutcome.Timeout));
        // Trash without a boss and another character's fight: not included.
        store.Save(TestRecords.Fight(t.AddDays(8), boss: null, kind: EncounterKind.Trash, localDps: 50_000));
        store.Save(TestRecords.Fight(t.AddDays(9), boss: 1003, local: "Carol"));

        var sums = store.GetBossSummaries("Alice");
        Assert.Equal([1002u, 1001u], sums.Select(s => s.BossNpcCode)); // most recently fought first

        var b1 = sums[1];
        Assert.Equal(5, b1.Fights);
        Assert.Equal(4, b1.Kills);
        Assert.Equal(4000, b1.BestDps);
        Assert.Equal(2500, b1.MedianDps);
        Assert.Equal(4000, b1.LastDps);
        Assert.Equal(80, b1.FastestKillSeconds);
        Assert.Equal(t.AddDays(4), b1.LastFoughtUtc);
        Assert.Equal(501u, b1.MapId);

        var b2 = sums[0];
        Assert.Equal(3, b2.Fights);
        Assert.Equal(0, b2.Kills);
        Assert.Equal(300, b2.BestDps);
        Assert.Equal(200, b2.MedianDps);
        Assert.Equal(200, b2.LastDps);
        Assert.Equal(0, b2.FastestKillSeconds);
        Assert.Equal(600u, b2.MapId);

        Assert.Empty(store.GetBossSummaries("Nobody"));
    }

    [Theory]
    [InlineData(new double[] { }, 0)]
    [InlineData(new double[] { 5 }, 5)]
    [InlineData(new double[] { 3, 1, 2 }, 2)]
    [InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
    [InlineData(new double[] { 10, 10, 1, 1000, 10 }, 10)]
    public void Median_is_correct(double[] values, double expected) =>
        Assert.Equal(expected, SqliteFightStore.Median(values));

    [Fact]
    public void GetPersonalBest_is_best_kill_dps()
    {
        using var store = NewStore();
        var t = TestRecords.T0;
        store.Save(TestRecords.Fight(t, localDps: 1000));
        var best = TestRecords.Fight(t.AddDays(1), localDps: 3000, duration: 77);
        store.Save(best);
        store.Save(TestRecords.Fight(t.AddDays(2), localDps: 9000, outcome: EncounterOutcome.Wipe));
        store.Save(TestRecords.Fight(t.AddDays(3), localDps: 2000));
        store.Save(TestRecords.Fight(t.AddDays(4), boss: 1002, localDps: 99_000));

        var pb = store.GetPersonalBest(1001, "Alice");
        Assert.NotNull(pb);
        Assert.Equal(best.Id, pb!.FightId);
        Assert.Equal(3000, pb.Dps);
        Assert.Equal(77, pb.DurationSeconds);
        Assert.Equal(EncounterOutcome.Kill, pb.Outcome);

        Assert.Null(store.GetPersonalBest(1001, "Nobody"));
        store.Save(TestRecords.Fight(t.AddDays(5), boss: 1005, outcome: EncounterOutcome.Wipe));
        Assert.Null(store.GetPersonalBest(1005, "Alice"));
    }

    // ───────────────────────── performance / concurrency ─────────────────────────

    [Fact]
    public void Big_record_with_30k_hits_saves_and_loads_under_one_second()
    {
        using var store = NewStore();
        // Warm up JIT / pool so the measurement reflects steady-state cost.
        var warm = TestRecords.Rich(100);
        store.Save(warm);
        store.Load(warm.Id);

        var big = TestRecords.Rich(30_000);
        var sw = Stopwatch.StartNew();
        store.Save(big);
        var loaded = store.Load(big.Id);
        sw.Stop();

        Assert.NotNull(loaded);
        Assert.Equal(30_000, loaded!.Hits.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"save+load took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(EncounterRecordSerializer.ToJson(big), EncounterRecordSerializer.ToJson(loaded));
    }

    [Fact]
    public async Task Concurrent_saves_and_queries_from_several_threads()
    {
        using var store = NewStore();
        const int writers = 6, perWriter = 25;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();

        var readerTasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var list = store.Query(new FightQuery { Limit = 20 });
                    if (list.Count > 0) store.Load(list[0].Id);
                    store.GetCharacters();
                    store.GetBossSummaries("Alice");
                    store.GetBossTrend(1001, "Alice");
                }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
        })).ToArray();

        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                try
                {
                    var f = TestRecords.Fight(TestRecords.T0.AddMinutes(w * 1000 + i), localDps: 100 + i, others: [("Bob", 50)]);
                    store.Save(f);
                    if (i % 5 == 0) store.Save(f); // upsert under contention
                }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);
        stop.Cancel();
        await Task.WhenAll(readerTasks);

        Assert.Empty(errors);
        Assert.Equal(writers * perWriter, store.Query(new FightQuery { Limit = 0 }).Count);
        Assert.Equal(writers * perWriter, store.GetBossTrend(1001, "Alice").Count);
        Assert.Equal(writers * perWriter, store.GetBossTrend(1001, "Bob").Count);
    }

    [Fact]
    public void Two_store_instances_on_the_same_file_see_each_others_writes()
    {
        using var a = NewStore();
        using var b = NewStore();
        var f = TestRecords.Fight(TestRecords.T0);
        a.Save(f);
        Assert.NotNull(b.Load(f.Id));
        Assert.True(b.Delete(f.Id));
        Assert.Empty(a.Query(new FightQuery()));
    }
}
