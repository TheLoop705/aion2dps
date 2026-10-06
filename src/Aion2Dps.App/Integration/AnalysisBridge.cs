using Aion2Dps.Analysis;
using Aion2Dps.App.Controls;
using Aion2Dps.Armory;

namespace Aion2Dps.App.Integration;

/// <summary>
/// The App's only entry points into the analysis (Aion2Dps.Analysis) and character lookup (Aion2Dps.Armory) UI.
/// Popups opened from the overlay go through <see cref="ShowWindow"/> so they never take focus from the game.
/// </summary>
public static class AnalysisBridge
{
    private static readonly Lazy<ArmoryClient> SharedArmoryClient = new(() => new ArmoryClient());

    /// <summary>Opens the breakdown popup for one combatant of an encounter (non-activating, topmost).</summary>
    public static Window? OpenBreakdown(EncounterRecord record, uint entityId, IGameData gameData, Window? owner)
    {
        var c = record.Combatants.FirstOrDefault(x => x.EntityId == entityId);
        if (c is null) return null;
        var view = CreateBreakdownView(record, entityId, gameData);
        var window = ShowWindow($"{ChartData.DisplayName(c)} · {ChartData.Title(record, gameData)}", view, 1000, 860, owner);
        view.CombatantChanged += _ =>
        {
            if (view.Combatant is { } now) window.Title = $"{ChartData.DisplayName(now)} · {ChartData.Title(record, gameData)} — Aion2Dps";
        };
        return window;
    }

    /// <summary>The breakdown content (DPS / accuracy / defense / buffs tabs) for one combatant.</summary>
    public static BreakdownView CreateBreakdownView(EncounterRecord record, uint entityId, IGameData gameData)
    {
        var view = new BreakdownView(record, entityId, gameData);
        // Pin + pick another player = compare (in a non-activating window instead of the library's default).
        view.CompareRequested += (pinned, picked) =>
        {
            OpenCompare(record, pinned, picked, gameData);
            view.IsPinned = false;
        };
        return view;
    }

    /// <summary>Opens two breakdowns side by side (ctrl+click compare).</summary>
    public static Window? OpenCompare(EncounterRecord record, uint entityA, uint entityB, IGameData gameData)
    {
        var a = record.Combatants.FirstOrDefault(x => x.EntityId == entityA);
        var b = record.Combatants.FirstOrDefault(x => x.EntityId == entityB);
        if (a is null || b is null) return null;
        var left = new BreakdownView(record, entityA, gameData) { ShowPinButton = false, IsPinned = true };
        var right = new BreakdownView(record, entityB, gameData) { ShowPinButton = false };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(left);
        var sep = new Border();
        sep.Ref(Border.BackgroundProperty, ThemeKeys.Border);
        Grid.SetColumn(sep, 1);
        grid.Children.Add(sep);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return ShowWindow($"Compare · {ChartData.DisplayName(a)} vs {ChartData.DisplayName(b)} · {ChartData.Title(record, gameData)}", grid, 1600, 880, null);
    }

    /// <summary>Saved fight browser (dashboard History page).</summary>
    public static FrameworkElement CreateHistoryView(IFightStore store, IGameData gameData)
    {
        var view = new HistoryView();
        view.Initialize(store, gameData);
        RefreshOnLanguageChange(view, gameData, () => _ = view.RefreshAsync());
        return view;
    }

    /// <summary>Per-boss personal trends (dashboard Trends page).</summary>
    public static FrameworkElement CreateTrendsView(IFightStore store, IGameData gameData)
    {
        var view = new TrendsView();
        view.Initialize(store, gameData);
        RefreshOnLanguageChange(view, gameData, () => _ = view.RefreshAsync());
        return view;
    }

    /// <summary>Character lookup through the official AION 2 site (dashboard Character page). Network only on use.</summary>
    public static FrameworkElement CreateArmoryView() => new CharacterLookupView(SharedArmoryClient.Value);

    /// <summary>Full report for a saved encounter (PvP sessions get the PvP review).</summary>
    public static FrameworkElement CreateEncounterReport(EncounterRecord record, IGameData gameData) =>
        record.Kind == EncounterKind.Pvp ? new PvpReviewView(record, gameData) : new EncounterReportView(record, gameData);

    /// <summary>Re-reads names while the view is loaded and the game-data language changes.</summary>
    private static void RefreshOnLanguageChange(FrameworkElement view, IGameData gameData, Action refresh)
    {
        void Handler() => view.Dispatcher.BeginInvoke(refresh);
        view.Loaded += (_, _) => { gameData.LanguageChanged -= Handler; gameData.LanguageChanged += Handler; };
        view.Unloaded += (_, _) => gameData.LanguageChanged -= Handler;
    }

    /// <summary>Hosts a view in a themed, non-activating tool window (never steals focus from the game).</summary>
    public static Window ShowWindow(string title, FrameworkElement content, double width, double height, Window? owner)
    {
        var w = new ThemedWindow
        {
            Title = title + " — Aion2Dps",
            Content = content,
            Width = width,
            Height = height,
            ShowActivated = false,
            Topmost = true,
            ShowInTaskbar = true,
            WindowStartupLocation = owner is not null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner is not null && owner.IsVisible && owner is not Overlay.OverlayWindow) w.Owner = owner;
        // Keep it on screen on small monitors.
        var area = SystemParameters.WorkArea;
        w.Width = Math.Min(w.Width, Math.Max(480, area.Width - 40));
        w.Height = Math.Min(w.Height, Math.Max(360, area.Height - 40));
        w.Show();
        return w;
    }
}
