using System.Diagnostics;
using System.Text;
using Aion2Dps.Capture;
using Aion2Dps.Contracts;
using Aion2Dps.Protocol;
using Aion2Dps.Simulator;
using Aion2Dps.Storage;

namespace Aion2Dps.Cli;

/// <summary>
/// aion2dps-cli: replay, census, simulate, selftest, live, adapters, locate. Simple positional/flag parsing.
/// Exit codes: 0 = ok, 1 = failure (selftest mismatch, bad file), 2 = usage error / Npcap missing.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        AppLog.MinimumLevel = HasFlag(args, "--verbose") ? LogLevel.Debug : LogLevel.Warn;
        AppLog.SetSink((level, area, message) => Console.Error.WriteLine($"[{level}] {area}: {message}"));
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Usage(0);

        try
        {
            var rest = args.Skip(1).ToArray();
            return args[0].ToLowerInvariant() switch
            {
                "replay" => Replay(rest),
                "census" => Census(rest),
                "simulate" => Simulate(rest),
                "selftest" => SelfTest(rest),
                "live" => Live(rest),
                "adapters" => Adapters(),
                "locate" => Locate(),
                _ => Usage(2, $"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            AppLog.Debug("Cli", ex.ToString());
            return 1;
        }
    }

    private static int Usage(int code, string? error = null)
    {
        if (error is not null) Console.Error.WriteLine(error);
        Console.WriteLine("""
            aion2dps-cli — offline tools for the Aion2Dps meter

              replay <file> [--speed N] [--json out.json]   Decode a .pcap/.pcapng/hex log through the full pipeline and
                                                            print every completed encounter (speed 0 = as fast as possible)
              census <file>                                 Opcode census and decoder error statistics of a capture file
              simulate <scenario> --out file.pcap [--seed N] Write a simulator scenario as a pcap file
                                                            (scenarios: BossKill, BossWipeThenKill, TrashPull, PvpSkirmish,
                                                             TrainingDummy)
              selftest [--seeds N]                          Run every simulator scenario through the full pipeline (stream and
                                                            pcap paths) and compare with the ground truth; exit code 0/1
              live [--seconds N] [--adapter NAME] [--record file.pcapng]
                                                            Live Npcap capture; prints status and the meter every second
              adapters                                      List Npcap capture adapters
              locate                                        Find the AION 2 process and its game connection

            Options: --verbose (debug log to stderr)
            """);
        return code;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static bool HasFlag(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static double DoubleOption(string[] args, string name, double fallback) =>
        double.TryParse(Option(args, name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>First argument that is not an option or an option value.</summary>
    private static string? Positional(string[] args, params string[] valueOptions)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (valueOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase)) i++;
                continue;
            }

            return args[i];
        }

        return null;
    }

    private static CancellationTokenSource CtrlC()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }

    // ───────────────────────────── replay ─────────────────────────────

    private static int Replay(string[] args)
    {
        string? file = Positional(args, "--speed", "--json");
        if (file is null) return Usage(2, "replay: missing <file>.");
        if (!File.Exists(file)) { Console.Error.WriteLine($"File not found: {file}"); return 1; }
        double speed = DoubleOption(args, "--speed", 0);
        string? json = Option(args, "--json");

        int index = 0;
        OfflinePipeline? pipe = null;
        pipe = new OfflinePipeline(onCompleted: r => Printer.Encounter(Console.Out, r, pipe!.GameData, ++index));
        using var cts = CtrlC();
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Replaying {Path.GetFileName(file)} (speed {(speed <= 0 ? "max" : speed + "x")}) …");
        ReplayStatistics stats;
        try
        {
            stats = PcapReplaySource.Replay(file, pipe.Input, speed, pipe.Clock, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelled.");
            return 1;
        }

        pipe.Drain();
        var d = pipe.Protocol.ProtocolDiagnostics;
        Console.WriteLine();
        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:0.00} s: {stats.Format}, {Printer.N(stats.Packets)} packets, {Printer.N(stats.BytesDelivered)} game bytes, " +
                          $"{stats.Locks} flow lock(s), {stats.Gaps} gap(s); {Printer.N(d.Frames)} frames, {Printer.N(d.Bundles)} bundles, " +
                          $"{Printer.N(d.EventsEmitted)} events, {Printer.N(d.DecodeErrors)} decode errors; {pipe.Records.Count} encounter(s).");
        if (stats.LastServer is { } server) Console.WriteLine($"Game server {server}, client {stats.LastClient}");
        if (pipe.Engine.LocalPlayer is { } lp)
            Console.WriteLine($"Local player: {lp.Name} ({pipe.GameData.GetClassName(lp.Class)}, level {lp.Level}, {pipe.GameData.GetServerName(lp.ServerId) ?? "server " + lp.ServerId})");
        if (stats.BytesDelivered == 0) Console.WriteLine("No AION 2 game traffic was found in this file.");

        if (json is not null)
        {
            var parts = pipe.Records.Select(EncounterRecordSerializer.ToJson);
            File.WriteAllText(json, "[\n" + string.Join(",\n", parts) + "\n]\n");
            Console.WriteLine($"Wrote {pipe.Records.Count} encounter record(s) to {Path.GetFullPath(json)}");
        }

        return 0;
    }

    // ───────────────────────────── census ─────────────────────────────

    private static int Census(string[] args)
    {
        string? file = Positional(args);
        if (file is null) return Usage(2, "census: missing <file>.");
        if (!File.Exists(file)) { Console.Error.WriteLine($"File not found: {file}"); return 1; }

        var pipe = new OfflinePipeline();
        var stats = PcapReplaySource.Replay(file, pipe.Input, 0, pipe.Clock);
        pipe.Drain();
        var d = pipe.Protocol.ProtocolDiagnostics;
        var names = pipe.Protocol.Opcodes.ToDictionary()
            .GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => string.Join("/", g.Select(kv => kv.Key)));

        Console.WriteLine($"{Path.GetFileName(file)}: {stats.Format}, {Printer.N(stats.Packets)} packets ({Printer.N(stats.TcpPackets)} TCP), " +
                          $"{Printer.N(stats.BytesDelivered)} game bytes, {stats.Locks} lock(s), {stats.Gaps} gap(s), {stats.TlsFlowsIgnored} TLS flow(s) ignored");
        Console.WriteLine();
        Console.WriteLine($"{"Opcode",-7} {"Name",-18} {"Count",10} {"Bytes",12} {"Decoded",10} {"Failed",8}");
        foreach (var s in d.GetCensus())
        {
            string name = names.TryGetValue(s.Opcode, out var n) ? n : s.Opcode == 0xFFFF ? "Bundle" : "";
            Console.WriteLine($"{s.Hex,-7} {name,-18} {Printer.N(s.Count),10} {Printer.N(s.Bytes),12} {Printer.N(s.Decoded),10} {Printer.N(s.Failed),8}");
        }

        Console.WriteLine();
        Console.WriteLine("Decoder statistics");
        (string, long)[] rows =
        [
            ("Bytes in", d.BytesIn), ("Frames", d.Frames), ("Bundles", d.Bundles), ("Bundle errors", d.BundleErrors),
            ("Bundle inner errors", d.BundleInnerErrors), ("Resyncs", d.Resyncs), ("Resync skipped bytes", d.ResyncSkippedBytes),
            ("Invalid frames", d.InvalidFrames), ("Padding bytes", d.PaddingBytes), ("Extra-byte frames", d.ExtraByteFrames),
            ("TLS records skipped", d.TlsRecordsSkipped), ("Embedded bundles", d.EmbeddedBundles), ("Decode errors", d.DecodeErrors),
            ("Unhandled frames", d.UnhandledFrames), ("Damage records", d.DamageRecords), ("Events emitted", d.EventsEmitted),
            ("Sink errors", d.SinkErrors), ("Engine event errors", pipe.Engine.EventErrors), ("Engine dropped records", pipe.Engine.DroppedRecords),
        ];
        foreach (var (label, value) in rows) Console.WriteLine($"  {label,-24} {Printer.N(value),14}");
        var recent = d.GetRecentErrors();
        if (recent.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Recent decoder errors ({recent.Count}):");
            foreach (var e in recent.TakeLast(20)) Console.WriteLine("  " + e);
        }

        Console.WriteLine();
        Console.WriteLine($"{pipe.Records.Count} completed encounter(s).");
        return 0;
    }

    // ───────────────────────────── simulate ─────────────────────────────

    private static int Simulate(string[] args)
    {
        string? name = Positional(args, "--out", "--seed");
        string? output = Option(args, "--out");
        if (name is null || output is null) return Usage(2, "simulate: usage simulate <scenario> --out file.pcap [--seed N]");
        string? match = ScenarioLibrary.Names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return Usage(2, $"Unknown scenario '{name}'. Known: {string.Join(", ", ScenarioLibrary.Names)}");
        int seed = (int)DoubleOption(args, "--seed", 1);

        var scenario = ScenarioLibrary.Get(match, seed);
        var start = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-scenario.Duration.TotalMinutes - 1), DateTimeKind.Utc);
        start = new DateTime(start.Ticks - start.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var stream = PcapExporter.WriteFile(output, scenario, start, new StreamGeneratorOptions { Seed = seed });
        Console.WriteLine($"Wrote {Path.GetFullPath(output)}: scenario {scenario.Name} (seed {seed}), {scenario.Duration.TotalSeconds:0} s, " +
                          $"{Printer.N(stream.Bytes.LongLength)} game bytes, {stream.Chunks.Count} segments, {stream.BundleCount} bundles");
        foreach (var e in scenario.Truth.Encounters) Console.WriteLine("  expected: " + e);
        return 0;
    }

    // ───────────────────────────── selftest ─────────────────────────────

    private static int SelfTest(string[] args)
    {
        int seeds = Math.Max(1, (int)DoubleOption(args, "--seeds", 2));
        var tmp = Path.Combine(Path.GetTempPath(), "aion2dps-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        int failures = 0, checks = 0;
        var sw = Stopwatch.StartNew();
        Console.WriteLine("Self-test: simulator scenarios → ProtocolPipeline → CombatEngine (real game data) vs ground truth");
        try
        {
            for (int seed = 1; seed <= seeds; seed++)
            {
                foreach (var name in ScenarioLibrary.Names)
                {
                    var scenario = ScenarioLibrary.Get(name, seed);
                    var start = new DateTime(2026, 10, 6, 18, 0, 0, DateTimeKind.Utc);

                    // 1. In-memory stream (random segmentation, LZ4 bundles, padding).
                    var stream = new StreamGenerator(new StreamGeneratorOptions { Seed = seed * 17 }).Generate(scenario, start);
                    var a = new OfflinePipeline();
                    a.Input.OnDiscontinuity(DiscontinuityReason.NewConnection);
                    foreach (var c in stream.Chunks)
                    {
                        a.Input.OnData(c.TimeUtc, c.Data);
                        a.Clock(c.TimeUtc);
                    }

                    a.Drain();
                    failures += Report($"{name,-17} seed {seed} stream", scenario, a, start);
                    checks++;

                    // 2. Pcap file → PcapReplaySource (flow detection, TCP reassembly) → pipeline.
                    string pcap = Path.Combine(tmp, $"{name}-{seed}.pcap");
                    PcapExporter.WriteFile(pcap, scenario, start, new StreamGeneratorOptions { Seed = seed * 17 + 1 });
                    var b = new OfflinePipeline();
                    var stats = PcapReplaySource.Replay(pcap, b.Input, 0, b.Clock);
                    b.Drain();
                    failures += Report($"{name,-17} seed {seed} pcap  ", scenario, b, start, $"{stats.Locks} lock, {stats.Gaps} gaps");
                    checks++;
                }
            }

            // 3. Storage round trip of one full boss kill.
            var kill = ScenarioLibrary.BossKill();
            var s0 = new DateTime(2026, 10, 6, 18, 0, 0, DateTimeKind.Utc);
            var p = new OfflinePipeline();
            new StreamGenerator().Generate(kill, s0).FeedTo(p.Input, clock: p.Clock);
            p.Drain();
            using (var store = new SqliteFightStore(Path.Combine(tmp, "selftest.db")))
            {
                foreach (var r in p.Records) store.Save(r);
                var loaded = p.Records.Select(r => store.Load(r.Id)!).ToList();
                var errors = TruthComparer.CompareAll(kill, loaded, s0);
                bool same = p.Records.Zip(loaded).All(x => EncounterRecordSerializer.ToJson(x.First) == EncounterRecordSerializer.ToJson(x.Second));
                checks++;
                if (errors.Count == 0 && same) Console.WriteLine($"PASS  {"storage round trip",-30} {p.Records.Count} record(s) saved and reloaded identically");
                else
                {
                    failures++;
                    Console.WriteLine($"FAIL  storage round trip ({errors.Count} mismatches, identical json: {same})");
                    foreach (var e in errors.Take(10)) Console.WriteLine("      " + e);
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"Self-test PASSED: {checks} checks in {sw.Elapsed.TotalSeconds:0.0} s"
            : $"Self-test FAILED: {failures} of {checks} checks failed");
        return failures == 0 ? 0 : 1;
    }

    private static int Report(string label, Scenario scenario, OfflinePipeline pipe, DateTime start, string? extra = null)
    {
        var errors = TruthComparer.CompareAll(scenario, pipe.Records, start);
        string encounters = string.Join(", ", pipe.Records.Select(r =>
            $"{r.Kind}/{r.Outcome} {Printer.Abbrev(r.TotalDamage)} in {r.DurationSeconds:0.0}s" + (r.HpCheck is { } hc ? $" HP-check {(hc.Passed ? "ok" : "FAIL")} {hc.Ratio:0.0000}" : "")));
        if (errors.Count == 0)
        {
            Console.WriteLine($"PASS  {label}  {encounters}{(extra is null ? "" : "  [" + extra + "]")}");
            return 0;
        }

        Console.WriteLine($"FAIL  {label}  {errors.Count} mismatches  {encounters}");
        foreach (var e in errors.Take(15)) Console.WriteLine("      " + e);
        return 1;
    }

    // ───────────────────────────── live ─────────────────────────────

    private static int Live(string[] args)
    {
        double seconds = DoubleOption(args, "--seconds", 0);
        var npcap = NpcapAvailability.Check();
        if (!npcap.IsAvailable)
        {
            Console.WriteLine($"Npcap is not available: {npcap.Reason}");
            Console.WriteLine($"Install Npcap from {NpcapAvailability.DownloadUrl} (keep \"WinPcap API-compatible mode\" enabled), then run this again.");
            return 2;
        }

        int index = 0;
        OfflinePipeline? pipe = null;
        pipe = new OfflinePipeline(new EngineOptions(), r => Printer.Encounter(Console.Out, r, pipe!.GameData, ++index));
        using var capture = new NpcapCaptureService();
        capture.AdapterOverride = Option(args, "--adapter");
        using var cts = CtrlC();
        capture.Start(pipe.Input);
        if (Option(args, "--record") is { } rec) capture.StartRecording(rec);
        Console.WriteLine($"Live capture started{(seconds > 0 ? $" for {seconds:0} s" : "")}; press Ctrl+C to stop.");
        var sw = Stopwatch.StartNew();
        while (!cts.IsCancellationRequested && (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds))
        {
            if (cts.Token.WaitHandle.WaitOne(1000)) break;
            var now = DateTime.UtcNow;
            pipe.Engine.Tick(now);
            var s = capture.Status;
            var snap = pipe.Engine.GetSnapshot(now);
            var d = pipe.Protocol.Diagnostics;
            string status = $"[{sw.Elapsed:mm\\:ss}] {s.State}: {s.Message}" +
                            (s.ServerEndpoint is null ? "" : $" server {s.ServerEndpoint}") +
                            (s.AdapterDescription is null ? "" : $" via {s.AdapterDescription}") +
                            $" · pkts {Printer.N(s.PacketsSeen)} bytes {Printer.N(s.BytesDelivered)} gaps {s.GapCount} · frames {Printer.N(d.Frames)} events {Printer.N(d.EventsEmitted)} errors {Printer.N(d.DecodeErrors)}";
            Console.WriteLine(status);
            string meter = $"         {snap.State} · {snap.StatusText}" + (snap.Target is { } t ? $" · {t.Name} {(t.HpFraction is double f ? Printer.Pct(f) : "")}" : "") +
                           (snap.State is MeterState.InCombat or MeterState.Ended ? $" · {Printer.Duration(snap.Elapsed.TotalSeconds)} · party {Printer.Abbrev(snap.PartyDps)} DPS" : "") +
                           (snap.PingMs is double ping ? $" · ping {ping:0} ms" : "");
            Console.WriteLine(meter);
            foreach (var row in snap.Rows.Take(8))
                Console.WriteLine($"           {row.Rank,2}. {(row.IsLocal ? "*" : " ")}{row.Name,-16} {Printer.Abbrev(row.Dps),9} DPS {Printer.N(row.Damage),14} {Printer.Pct(row.DamageShare),7}");
        }

        capture.Stop();
        Console.WriteLine($"Stopped. {pipe.Records.Count} completed encounter(s).");
        return 0;
    }

    // ───────────────────────────── adapters / locate ─────────────────────────────

    private static int Adapters()
    {
        var npcap = NpcapAvailability.Check();
        if (!npcap.IsAvailable)
        {
            Console.WriteLine($"Npcap is not available: {npcap.Reason}");
            Console.WriteLine($"Install it from {NpcapAvailability.DownloadUrl}.");
            return 2;
        }

        Console.WriteLine($"Npcap: {npcap.WpcapPath} ({npcap.DeviceCount} devices)");
        foreach (var a in AdapterSelector.ListAdapters())
            Console.WriteLine($"  {a.Description}{(a.IsLoopback ? " [loopback]" : "")}\n      {a.Name}\n      IPv4: {(a.IPv4Addresses.Count == 0 ? "—" : string.Join(", ", a.IPv4Addresses))}");
        return 0;
    }

    private static int Locate()
    {
        var result = new GameProcessLocator().Locate();
        if (!result.ProcessFound)
        {
            Console.WriteLine("AION 2 is not running (no AION2 / AION2-Win64-Shipping process found).");
            return 0;
        }

        Console.WriteLine("Game processes:");
        foreach (var p in result.Processes) Console.WriteLine($"  {p.ProcessName} (pid {p.ProcessId})");
        if (result.Candidates.Count == 0)
        {
            Console.WriteLine("No established game connection found (still loading, or the character select screen).");
            return 0;
        }

        Console.WriteLine("Connections (best first):");
        foreach (var c in result.Candidates)
            Console.WriteLine($"  {c.ProcessName}({c.ProcessId}) {c.Local} -> {c.Remote}{(c.IsGamePort ? "  [game port 13328]" : "")}{(c.IsLoopback ? "  [loopback relay]" : "")}");
        var best = result.Best!;
        Console.WriteLine($"Best: server {best.Remote}, local {best.Local}");
        var npcap = NpcapAvailability.Check();
        if (npcap.IsAvailable)
        {
            var sel = AdapterSelector.Select(AdapterSelector.ListAdapters(), best.Local.IPAddress, null);
            Console.WriteLine($"Adapter: {sel.Adapter?.Description ?? "none"} ({sel.Reason})");
        }
        else Console.WriteLine($"Adapter: n/a (Npcap not available: {npcap.Reason})");
        return 0;
    }
}
