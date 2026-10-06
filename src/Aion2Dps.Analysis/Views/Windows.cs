using System.Windows;
using System.Windows.Controls;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Resizable window around a <see cref="BreakdownView"/>. Pinning the player and then picking another one opens a
/// <see cref="CompareWindow"/> (set <see cref="OpenCompareOnRequest"/> to false to handle
/// <see cref="BreakdownView.CompareRequested"/> yourself). Position and size are left to the host.
/// </summary>
public sealed class BreakdownWindow : Window
{
    private readonly EncounterRecord _record;
    private readonly IGameData _gameData;

    public BreakdownWindow(EncounterRecord record, uint combatantEntityId, IGameData gameData)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        View = new BreakdownView(record, combatantEntityId, gameData);
        Width = 1000;
        Height = 860;
        MinWidth = 640;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResize;
        ShowActivated = true;
        SetResourceReference(BackgroundProperty, ThemeKeys.WindowBackground);
        Content = View;
        UpdateTitle();
        View.CombatantChanged += _ => UpdateTitle();
        View.CompareRequested += (a, b) =>
        {
            if (!OpenCompareOnRequest) return;
            var compare = new CompareWindow(_record, a, b, _gameData) { Owner = Owner };
            if (WindowState == WindowState.Normal && IsLoaded)
            {
                compare.Left = Left;
                compare.Top = Top;
            }
            compare.Show();
            View.IsPinned = false;
        };
    }

    public BreakdownView View { get; }

    /// <summary>Open a <see cref="CompareWindow"/> when the pinned view asks for a comparison (default true).</summary>
    public bool OpenCompareOnRequest { get; set; } = true;

    private void UpdateTitle()
    {
        string name = View.Combatant is { } c ? ChartData.DisplayName(c) : "Player";
        Title = $"{name} · {ChartData.Title(_record, _gameData)} · breakdown";
    }
}

/// <summary>Two breakdowns side by side; the first one carries the PINNED badge.</summary>
public sealed class CompareWindow : Window
{
    public CompareWindow(EncounterRecord record, uint a, uint b, IGameData gameData)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(gameData);
        Pinned = new BreakdownView(record, a, gameData) { ShowPinButton = false, IsPinned = true };
        Other = new BreakdownView(record, b, gameData) { ShowPinButton = false };
        Width = 1680;
        Height = 900;
        MinWidth = 900;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, ThemeKeys.WindowBackground);
        Content = BuildContent();
        void Retitle() => Title = $"Compare · {NameOf(Pinned)} vs {NameOf(Other)} · {ChartData.Title(record, gameData)}";
        Pinned.CombatantChanged += _ => Retitle();
        Other.CombatantChanged += _ => Retitle();
        // Keep both on the same tab: comparing rotations or hit quality head to head is the point.
        Retitle();
    }

    /// <summary>The pinned (left) breakdown.</summary>
    public BreakdownView Pinned { get; }

    /// <summary>The compared (right) breakdown.</summary>
    public BreakdownView Other { get; }

    /// <summary>Switches both sides to the same tab.</summary>
    public void SelectTab(BreakdownTab tab)
    {
        Pinned.SelectedTab = tab;
        Other.SelectedTab = tab;
    }

    private static string NameOf(BreakdownView v) => v.Combatant is { } c ? ChartData.DisplayName(c) : "?";

    private UIElement BuildContent()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var divider = new Border();
        divider.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Border);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(Other, 2);
        grid.Children.Add(Pinned);
        grid.Children.Add(divider);
        grid.Children.Add(Other);
        return grid;
    }
}
