using System.Windows.Documents;
using Aion2Dps.Analysis;
using Aion2Dps.App.Dashboard;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Aion2Dps.Simulator;

namespace Aion2Dps.App.Rendering;

/// <summary>
/// <c>Aion2Dps.exe --render-screens &lt;dir&gt;</c>: builds the REAL simulated pipeline (wire simulator → ProtocolPipeline →
/// CombatEngine with the real game data → SqliteFightStore in a temporary folder), plays simulator scenarios at full speed
/// (several past fights for history and trends, then a boss fight paused mid-fight and played to the kill) and renders
/// the overlay, breakdown, report, history, trends and dashboard pages offscreen to PNG. No window is ever shown.
/// Must run on an STA thread.
/// </summary>
public static class RealScreens
{
    /// <summary>Capture facade for the offscreen run: data is fed synchronously, the status reads like the simulator's.</summary>
    private sealed class OfflineFeedCapture : ICaptureService
    {
        public CaptureStatus Status { get; set; } = new()
        {
            State = CaptureState.Capturing,
            Message = "Simulating game traffic (offscreen render)",
            AdapterName = SimulatedCaptureService.AdapterName,
            AdapterDescription = "Simulated AION 2 traffic",
            ServerEndpoint = SimulatedCaptureService.ServerEndpoint,
            LocalEndpoint = SimulatedCaptureService.LocalEndpoint,
        };

        public event Action<CaptureStatus>? StatusChanged { add { } remove { } }
        public string? AdapterOverride { get; set; }
        public IReadOnlyList<AdapterInfo> GetAdapters() => [new AdapterInfo(SimulatedCaptureService.AdapterName, "Simulated AION 2 traffic", ["127.0.0.1"], true)];
        public void Start(IStreamSink sink) { }
        public void Stop() { }
        public void StartRecording(string pcapngPath) { }
        public void StopRecording() { }
        public void Dispose() { }
    }

    public static IReadOnlyList<string> RenderAll(string dir)
    {
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        string dataDir = Path.Combine(Path.GetTempPath(), "aion2dps-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var real = ServiceFactory.CreateSimulated(dataDir);
        var capture = new OfflineFeedCapture();
        DateTime clock = DateTime.UtcNow;
        var services = real with { Capture = capture, Clock = () => clock, ModeLabel = "Simulator" };
        var engine = services.Engine;
        var saved = new List<EncounterRecord>();
        engine.EncounterCompleted += r =>
        {
            services.Store.Save(r);
            saved.Add(r);
        };

        try
        {
            var now = DateTime.UtcNow;
            now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            long bytes = 0, chunks = 0;

            void Feed(GeneratedStream stream, DateTime? until, ref int index)
            {
                for (; index < stream.Chunks.Count; index++)
                {
                    var c = stream.Chunks[index];
                    if (until is { } u && c.TimeUtc > u) break;
                    services.PipelineInput.OnData(c.TimeUtc, c.Data);
                    clock = c.TimeUtc;
                    engine.Tick(c.TimeUtc);
                    bytes += c.Data.Length;
                    chunks++;
                }

                capture.Status = capture.Status with { PacketsSeen = chunks, BytesDelivered = bytes, LastPacketUtc = clock };
            }

            void Drain()
            {
                var last = clock;
                for (int s = 1; s <= 40; s++) engine.Tick(clock = last.AddSeconds(s));
            }

            void Play(Scenario scenario, DateTime start, int seed)
            {
                var stream = new StreamGenerator(new StreamGeneratorOptions { Seed = seed }).Generate(scenario, start);
                services.PipelineInput.OnDiscontinuity(DiscontinuityReason.NewConnection);
                int i = 0;
                Feed(stream, null, ref i);
                Drain();
            }

            // 1. History: past fights, oldest first (the engine needs chronological capture time).
            int seedBase = 100;
            var past = new List<(DateTime Start, Scenario Scenario)>();
            foreach (int daysAgo in new[] { 13, 10, 8, 6, 4, 2 })
                past.Add((now.AddDays(-daysAgo).AddHours(-3), ScenarioLibrary.BossKill(seed: daysAgo)));
            past.Add((now.AddDays(-5).AddHours(-2), ScenarioLibrary.BossWipeThenKill(seed: 2)));
            past.Add((now.AddDays(-1).AddHours(-5), ScenarioLibrary.BossWipeThenKill(seed: 3)));
            past.Add((now.AddHours(-20), ScenarioLibrary.TrainingDummy(seed: 1)));
            foreach (var (start, scenario) in past.OrderBy(p => p.Start))
                Play(scenario, start, seedBase++);

            // 2. PvP skirmish paused at 20 s for the PvP overlay.
            var pvp = ScenarioLibrary.PvpSkirmish(seed: 1);
            var pvpStart = now.AddHours(-6);
            var pvpStream = new StreamGenerator(new StreamGeneratorOptions { Seed = seedBase++ }).Generate(pvp, pvpStart);
            services.PipelineInput.OnDiscontinuity(DiscontinuityReason.NewConnection);
            int pi = 0;
            Feed(pvpStream, pvpStart.AddSeconds(20), ref pi);
            engine.Mode = MeterMode.Pvp;
            var pvpSnapshot = engine.GetSnapshot(clock);
            files.Add(RenderOverlay("sim-pvp-midfight", pvpSnapshot, capture.Status, new OverlayViewOptions { RowSize = RowSize.Normal }, dir));
            engine.Mode = MeterMode.BossOnly;
            Feed(pvpStream, null, ref pi);
            Drain();

            // 3. The "live" boss fight: mid-fight overlay, then the kill.
            var boss = ScenarioLibrary.BossKill(seed: 1);
            var bossStart = now.AddMinutes(-3);
            var bossStream = new StreamGenerator(new StreamGeneratorOptions { Seed = 1 }).Generate(boss, bossStart);
            services.PipelineInput.OnDiscontinuity(DiscontinuityReason.NewConnection);
            int bi = 0;
            Feed(bossStream, bossStart.AddSeconds(46), ref bi);
            var mid = engine.GetSnapshot(clock);
            var opts = new OverlayViewOptions { RowSize = RowSize.Compact, Version = Infrastructure.AppPaths.Version };
            files.Add(RenderOverlay("sim-boss-midfight", mid, capture.Status, opts, dir));
            files.Add(RenderOverlay("sim-boss-midfight-gearscore", mid, capture.Status, opts with { ShowGearScore = true }, dir));
            files.Add(RenderOverlay("sim-boss-midfight-normal", mid, capture.Status, opts with { RowSize = RowSize.Normal, ShowCritRate = true, ShowMaxHit = true }, dir, ThemeCatalog.Glacier));
            files.Add(RenderOverlay("sim-boss-midfight-total", mid, capture.Status, opts with { View = MeterView.Total }, dir, ThemeCatalog.Daybreak));
            var midRecord = engine.GetCurrentEncounter();
            if (midRecord is not null) files.Add(RenderBreakdown("sim-breakdown-midfight", midRecord, services.GameData, BreakdownTab.Dps, dir));

            Feed(bossStream, null, ref bi);
            var ended = engine.GetSnapshot(clock);
            files.Add(RenderOverlay("sim-boss-ended", ended, capture.Status, opts, dir));
            Drain();
            var last = saved.LastOrDefault(r => r.Kind == EncounterKind.Boss) ?? throw new InvalidOperationException("The simulated boss kill was not completed.");
            files.Add(RenderBreakdown("sim-breakdown-dps", last, services.GameData, BreakdownTab.Dps, dir));
            files.Add(RenderBreakdown("sim-breakdown-accuracy", last, services.GameData, BreakdownTab.Accuracy, dir));
            files.Add(RenderBreakdown("sim-breakdown-defense", last, services.GameData, BreakdownTab.Defense, dir));
            files.Add(RenderBreakdown("sim-breakdown-buffs", last, services.GameData, BreakdownTab.Buffs, dir));
            var elementalist = last.Combatants.FirstOrDefault(c => c.Class == CharacterClass.Elementalist);
            if (elementalist is not null) files.Add(RenderBreakdown("sim-breakdown-summoner", last, services.GameData, BreakdownTab.Dps, dir, elementalist.EntityId));
            files.Add(RenderElement("sim-report-kill", AnalysisBridge.CreateEncounterReport(last, services.GameData), 1000, null, dir));
            var pvpRecord = saved.LastOrDefault(r => r.Kind == EncounterKind.Pvp);
            if (pvpRecord is not null) files.Add(RenderElement("sim-report-pvp", AnalysisBridge.CreateEncounterReport(pvpRecord, services.GameData), 1000, null, dir));
            var local = last.Combatants.First(c => c.IsLocal);
            var other = last.Combatants.Where(c => !c.IsLocal && c.Kind == CombatantKind.Player).OrderByDescending(c => c.Damage).First();
            var compare = new Grid();
            compare.ColumnDefinitions.Add(new ColumnDefinition());
            compare.ColumnDefinitions.Add(new ColumnDefinition());
            compare.Children.Add(new BreakdownView(last, local.EntityId, services.GameData) { ShowPinButton = false, IsPinned = true });
            var right = new BreakdownView(last, other.EntityId, services.GameData) { ShowPinButton = false };
            Grid.SetColumn(right, 1);
            compare.Children.Add(right);
            files.Add(RenderElement("sim-compare", compare, 1600, 880, dir));

            // 4. Dashboard pages over the real store/diagnostics.
            var settings = new SettingsStore(Path.Combine(dataDir, "settings.json"));
            var ctx = new DashboardContext { Services = services, Settings = settings };
            foreach (var page in new[] { "Meter", "History", "Trends", "Character", "Settings", "About" })
                files.Add(RenderPage(page, ctx, dir));
            files.Add(RenderShell("Meter", ctx, dir, 1080, 720));
            files.Add(RenderShell("History", ctx, dir, 1080, 720));
            files.Add(RenderShell("History", ctx, dir, 1600, 960));
            files.Add(RenderShell("Trends", ctx, dir, 1080, 720));

            // 5. Language switch propagates to names (overlay rendered in Korean).
            services.GameData.Language = GameLanguage.Korean;
            files.Add(RenderOverlay("sim-boss-ended-korean", engine.GetSnapshot(clock), capture.Status, opts, dir));
            services.GameData.Language = GameLanguage.English;

            File.WriteAllText(Path.Combine(dir, "sim-summary.txt"), Summary(saved, services));
            files.Add(Path.Combine(dir, "sim-summary.txt"));
            return files;
        }
        finally
        {
            services.Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch { /* temp */ }
        }
    }

    private static string Summary(IReadOnlyList<EncounterRecord> saved, AppServices services)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in saved)
            sb.AppendLine($"{r.StartUtc:yyyy-MM-dd HH:mm} {r.Kind}/{r.Outcome} {(r.BossNpcCode is uint b ? services.GameData.GetNpcName(b) : "-")} " +
                          $"{r.DurationSeconds:0.0}s total {r.TotalDamage:N0} hp-check {(r.HpCheck is { } h ? (h.Passed ? "ok" : "FAIL") : "n/a")}");
        var d = services.Diagnostics!;
        sb.AppendLine($"frames {d.Frames:N0} bundles {d.Bundles:N0} events {d.EventsEmitted:N0} decode errors {d.DecodeErrors:N0}");
        return sb.ToString();
    }

    private static string RenderOverlay(string name, MeterSnapshot snapshot, CaptureStatus status, OverlayViewOptions options, string dir, ThemeDefinition? theme = null)
    {
        theme ??= ThemeCatalog.Obsidian;
        var c = new ScreenCatalog.OverlayCase(name, () => snapshot, new OverlayStatus { Capture = status }, options);
        return ScreenCatalog.RenderOverlay(c, theme, dir);
    }

    private static string RenderBreakdown(string name, EncounterRecord record, IGameData gameData, BreakdownTab tab, string dir, uint? entity = null)
    {
        var view = AnalysisBridge.CreateBreakdownView(record, entity ?? record.Combatants.First(c => c.IsLocal).EntityId, gameData);
        view.SelectedTab = tab;
        return RenderElement(name, view, 1000, 860, dir);
    }

    private static string RenderElement(string name, FrameworkElement element, double width, double? height, string dir, ThemeDefinition? theme = null)
    {
        var root = new Border { Child = element };
        root.SetResourceReference(Border.BackgroundProperty, AppThemeKeys.DashboardBackground);
        root.SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.FontFamily);
        var themed = OffscreenRenderer.Themed(root, theme ?? ThemeCatalog.Obsidian);
        Pump(themed, width, height);
        return OffscreenRenderer.RenderToPng(themed, width, Path.Combine(dir, name + ".png"), height, scale: 1.0);
    }

    private static string RenderPage(string page, DashboardContext ctx, string dir)
    {
        var shell = new DashboardView(ctx);
        var p = shell.Pages.First(x => x.Key == page);
        shell.Navigate(page);
        if (p.Parent is ContentControl cc) cc.Content = null;
        var host = new ContentControl { Content = p };
        var pageRoot = new Border { Padding = new Thickness(28, 22, 28, 22), Child = host };
        pageRoot.SetResourceReference(Border.BackgroundProperty, AppThemeKeys.DashboardBackground);
        pageRoot.SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.FontFamily);
        var themed = OffscreenRenderer.Themed(pageRoot, ThemeCatalog.Obsidian);
        p.OnShown();
        p.Refresh();
        Pump(themed, 1240, null);
        SelectFirstFight(themed);
        Pump(themed, 1240, null);
        p.Refresh();
        return OffscreenRenderer.RenderToPng(themed, 1240, Path.Combine(dir, $"sim-page-{page.ToLowerInvariant()}.png"), scale: 1.0);
    }

    private static string RenderShell(string page, DashboardContext ctx, string dir, double width, double height)
    {
        var view = new DashboardView(ctx);
        var themed = OffscreenRenderer.Themed(view, ThemeCatalog.Obsidian);
        view.Navigate(page);
        view.RefreshNow();
        Pump(themed, width, height);
        SelectFirstFight(themed);
        Pump(themed, width, height);
        view.RefreshNow();
        return OffscreenRenderer.RenderToPng(themed, width, Path.Combine(dir, $"sim-dashboard-{page.ToLowerInvariant()}-{width:0}x{height:0}.png"), height, scale: 1.0);
    }

    /// <summary>Opens the newest boss kill in a HistoryView so its detail panel is part of the screenshot.</summary>
    private static void SelectFirstFight(DependencyObject root)
    {
        var history = FindChild<HistoryView>(root);
        if (history is null) return;
        var fight = history.Fights.FirstOrDefault(f => f.Kind == EncounterKind.Boss && f.Outcome == EncounterOutcome.Kill) ?? history.Fights.FirstOrDefault();
        if (fight is not null) WaitFor(history.SelectFightAsync(fight.Id));
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) return t;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindChild<T>(child) is { } found) return found;
        int n = root is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int i = 0; i < n; i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>Lays the element out and lets async view work (store queries on the thread pool) finish.</summary>
    private static void Pump(FrameworkElement element, double width, double? height)
    {
        for (int round = 0; round < 6; round++)
        {
            element.Measure(new Size(width, height ?? double.PositiveInfinity));
            element.Arrange(new Rect(0, 0, width, height ?? Math.Ceiling(element.DesiredSize.Height)));
            element.UpdateLayout();
            DoEvents(TimeSpan.FromMilliseconds(150));
        }
    }

    private static void WaitFor(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until) DoEvents(TimeSpan.FromMilliseconds(50));
    }

    private static void DoEvents(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
