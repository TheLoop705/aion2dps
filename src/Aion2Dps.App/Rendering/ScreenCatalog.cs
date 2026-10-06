using System.Windows.Documents;
using Aion2Dps.App.Dashboard;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;

namespace Aion2Dps.App.Rendering;

/// <summary>
/// Renders the review screenshots (overlay states × themes, dashboard pages, breakdown) offscreen to PNG.
/// Used by <c>Aion2Dps.exe --render-screens &lt;dir&gt;</c> and by the tests. Must run on an STA thread.
/// </summary>
public static class ScreenCatalog
{
    public sealed record OverlayCase(string Name, Func<MeterSnapshot> Snapshot, OverlayStatus Status, OverlayViewOptions Options, (string Title, string Body)? Toast = null);

    public static IReadOnlyList<OverlayCase> OverlayCases()
    {
        var baseOpts = new OverlayViewOptions { Version = "0.1.0", RowSize = RowSize.Compact };
        var capturing = PreviewData.Status();
        return
        [
            new("live-boss", () => PreviewData.LiveBoss(), capturing, baseOpts),
            new("live-normal-columns", () => PreviewData.LiveBoss(), capturing with { PinnedEntityId = 1002 },
                baseOpts with { RowSize = RowSize.Normal, ShowCritRate = true, ShowMaxHit = true }),
            new("low-hp-toast", PreviewData.LowHpBoss, capturing with { TrainingRemaining = null },
                baseOpts, ("New personal best!", "Warden of the Ashen Spire: 612K DPS (+5.2% vs 582K)")),
            new("micro-raid", () => PreviewData.LiveBoss(80, raid: true), capturing, baseOpts with { RowSize = RowSize.Micro }),
            new("pvp", () => PreviewData.Pvp(), capturing, baseOpts with { RowSize = RowSize.Normal }),
            new("pvp-compact", () => PreviewData.Pvp(100), capturing, baseOpts with { PvpSort = PvpSort.Damage }),
            new("ended-kill", PreviewData.EndedKill, capturing, baseOpts),
            new("hp-check-warning", () => PreviewData.LiveBoss() with { HpCheckRatio = 0.87 }, capturing, baseOpts with { View = MeterView.Total }),
            new("taken-view", () => PreviewData.LiveBoss(), capturing, baseOpts with { View = MeterView.Taken }),
            new("training", () => PreviewData.LiveBoss(), capturing with { TrainingRemaining = TimeSpan.FromSeconds(42), Flash = "Training 1:00 started" }, baseOpts),
            new("npcap-missing", PreviewData.WaitingForCombat, PreviewData.Status(CaptureState.NpcapMissing), baseOpts),
            new("waiting-for-game", PreviewData.WaitingForCombat, PreviewData.Status(CaptureState.WaitingForGame), baseOpts),
            new("detecting", PreviewData.WaitingForCombat, PreviewData.Status(CaptureState.Detecting), baseOpts),
            new("waiting-for-combat", PreviewData.WaitingForCombat, capturing, baseOpts),
            new("unlocked-hover", () => PreviewData.LiveBoss(), capturing, baseOpts with { Locked = false }),
        ];
    }

    public static OverlayView BuildOverlay(OverlayCase c)
    {
        var v = new OverlayView { AnimationsEnabled = false, Width = 380 };
        v.Update(c.Snapshot(), c.Status, c.Options);
        if (c.Toast is { } t) v.ShowToast(t.Title, t.Body);
        if (c.Name == "unlocked-hover") v.AdjustBarVisible = true;
        return v;
    }

    /// <summary>Renders one overlay case in a theme; returns the file path.</summary>
    public static string RenderOverlay(OverlayCase c, ThemeDefinition theme, string dir, AppearanceSettings? custom = null)
    {
        var view = new OverlayView { AnimationsEnabled = false, Width = 380 };
        var root = OffscreenRenderer.Themed(view, theme, custom ?? new AppearanceSettings { ThemeId = theme.Id, BarStyle = theme.DefaultBarStyle },
            OffscreenRenderer.SceneBackdrop(), new Thickness(16));
        // Update once the palette is in scope (HP colours are looked up from resources).
        view.Update(c.Snapshot(), c.Status, c.Options with { BarStyle = custom?.BarStyle ?? theme.DefaultBarStyle });
        if (c.Toast is { } t) view.ShowToast(t.Title, t.Body);
        if (c.Name == "unlocked-hover") view.AdjustBarVisible = true;
        return OffscreenRenderer.RenderToPng(root, 412, Path.Combine(dir, $"overlay-{c.Name}-{theme.Id}.png"));
    }

    public static DashboardContext DemoContext()
    {
        var now = PreviewData.Origin.AddSeconds(72);
        var services = ServiceFactory.CreateDemo(originUtc: PreviewData.Origin) with
        {
            Clock = () => now,
            Diagnostics = new FakeDiagnostics(() => PreviewData.Origin.AddMinutes(42), PreviewData.Origin),
        };
        if (services.Capture is FakeCaptureService fc) fc.SetStatus(PreviewData.Capturing with { GameProcessId = 21436, GapCount = 1, LastPacketUtc = now.AddSeconds(-0.4) });
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "aion2dps-preview-settings.json"));
        return new DashboardContext { Services = services, Settings = settings };
    }

    public static string RenderDashboardPage(string page, ThemeDefinition theme, string dir, DashboardContext? ctx = null, bool shell = false)
    {
        ctx ??= DemoContext();
        ctx.Settings.Current.Appearance.ThemeId = theme.Id;
        FrameworkElement content;
        if (shell)
        {
            var view = new DashboardView(ctx);
            content = view;
            var root = OffscreenRenderer.Themed(content, theme, null, null);
            view.Navigate(page);
            view.RefreshNow();
            return OffscreenRenderer.RenderToPng(root, 1180, Path.Combine(dir, $"dashboard-shell-{page.ToLowerInvariant()}-{theme.Id}.png"), height: 780, scale: 1.25);
        }
        var shellView = new DashboardView(ctx);
        var p = shellView.Pages.First(x => x.Key == page);
        // Detach the page from the shell's host so it can be rendered full-length.
        var host = new ContentControl();
        shellView.Navigate(page);
        if (p.Parent is ContentControl cc) cc.Content = null;
        host.Content = p;
        var pageRoot = new Border { Padding = new Thickness(28, 22, 28, 22), Child = host };
        pageRoot.SetResourceReference(Border.BackgroundProperty, AppThemeKeys.DashboardBackground);
        pageRoot.SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.FontFamily);
        var themed = OffscreenRenderer.Themed(pageRoot, theme, null, null);
        p.OnShown();
        p.Refresh();
        return OffscreenRenderer.RenderToPng(themed, 1000, Path.Combine(dir, $"page-{page.ToLowerInvariant()}-{theme.Id}.png"), scale: 1.25);
    }

    public static string RenderBreakdown(ThemeDefinition theme, string dir)
    {
        var record = PreviewData.LiveRecord();
        var gd = new FakeGameData();
        var c = record.Combatants.First(x => x.IsLocal);
        var view = AnalysisBridge.CreateBreakdownView(record, c.EntityId, gd);
        var root = new Border { Child = view };
        root.SetResourceReference(Border.BackgroundProperty, AppThemeKeys.DashboardBackground);
        var themed = OffscreenRenderer.Themed(root, theme);
        return OffscreenRenderer.RenderToPng(themed, 1000, Path.Combine(dir, $"breakdown-{theme.Id}.png"), height: 860, scale: 1.0);
    }

    /// <summary>Renders the full review set. Returns the written files.</summary>
    public static IReadOnlyList<string> RenderAll(string dir, IEnumerable<ThemeDefinition>? themes = null)
    {
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        var themeList = (themes ?? ThemeCatalog.All).ToList();
        var cases = OverlayCases();
        // Every case in the default theme; the key cases in every theme.
        foreach (var c in cases) files.Add(RenderOverlay(c, ThemeCatalog.Obsidian, dir));
        foreach (var t in themeList.Where(t => t.Id != ThemeCatalog.Obsidian.Id))
            foreach (var c in cases.Where(c => c.Name is "live-boss" or "pvp" or "micro-raid" or "npcap-missing"))
                files.Add(RenderOverlay(c, t, dir));

        var ctx = DemoContext();
        foreach (var page in new[] { "Meter", "History", "Trends", "Character", "Appearance", "Settings", "About" })
            files.Add(RenderDashboardPage(page, ThemeCatalog.Obsidian, dir, ctx));
        files.Add(RenderDashboardPage("Meter", ThemeCatalog.Obsidian, dir, ctx, shell: true));
        files.Add(RenderDashboardPage("Appearance", ThemeCatalog.Daybreak, dir, DemoContext(), shell: true));
        files.Add(RenderDashboardPage("Meter", ThemeCatalog.Arcade, dir, DemoContext(), shell: true));
        files.Add(RenderBreakdown(ThemeCatalog.Obsidian, dir));
        files.Add(RenderBreakdown(ThemeCatalog.Glacier, dir));
        return files;
    }
}
