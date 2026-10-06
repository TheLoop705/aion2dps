using System.Windows.Media.Animation;
using Aion2Dps.App.Controls;
using Aion2Dps.App.Formatting;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Overlay;

/// <summary>
/// One PvP opponent: class emblem, name/class/guild, live HP bar, ↑ dealt / ↓ taken and a two-way trade bar,
/// plus a KILLED tag. Reused across refreshes.
/// </summary>
internal sealed class PvpRowView : Grid
{
    private readonly TranslateTransform _translate = new();
    private readonly Border _hover = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly ClassEmblem _emblem = new(18);
    private readonly TextBlock _name = Ui.Text("", ThemeKeys.Text, weight: FontWeights.SemiBold);
    private readonly TextBlock _meta = Ui.Text("", ThemeKeys.TextMuted);
    private readonly Border _killed = Ui.Pill("KILLED", ThemeKeys.Negative, ThemeKeys.Text, 8.5);
    private readonly TextBlock _dealt = Ui.Text("", ThemeKeys.Positive, weight: FontWeights.SemiBold, mono: true);
    private readonly TextBlock _taken = Ui.Text("", ThemeKeys.Negative, weight: FontWeights.SemiBold, mono: true);
    private readonly Grid _hpBar = new() { Height = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly ColumnDefinition _hpFill = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly ColumnDefinition _hpEmpty = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly Border _hpFillBorder = new();
    private readonly Grid _tradeBar = new() { Height = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly ColumnDefinition _tradeDealt = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly ColumnDefinition _tradeTaken = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly Grid _layout = new();
    private RowSize? _size;
    private PvpRow _row = new();

    public PvpRowView()
    {
        RenderTransform = _translate;
        VerticalAlignment = VerticalAlignment.Top;
        Background = Brushes.Transparent;
        _hover.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceAlt);
        MouseEnter += (_, _) => _hover.Opacity = 0.55;
        MouseLeave += (_, _) => _hover.Opacity = 0;
        _dealt.TextAlignment = _taken.TextAlignment = TextAlignment.Right;
        _killed.Margin = new Thickness(6, 0, 0, 0);
        _meta.Margin = new Thickness(6, 0, 0, 0);

        // HP bar: track + fill (star columns, no layout dependency on ActualWidth)
        var hpTrack = new Border { CornerRadius = new CornerRadius(2) };
        hpTrack.SetResourceReference(Border.BackgroundProperty, ThemeKeys.BarTrack);
        Grid.SetColumnSpan(hpTrack, 2);
        _hpBar.ColumnDefinitions.Add(_hpFill);
        _hpBar.ColumnDefinitions.Add(_hpEmpty);
        _hpFillBorder.CornerRadius = new CornerRadius(2);
        _hpBar.Children.Add(hpTrack);
        _hpBar.Children.Add(_hpFillBorder);

        _tradeBar.ColumnDefinitions.Add(_tradeDealt);
        _tradeBar.ColumnDefinitions.Add(_tradeTaken);
        var d = new Border { CornerRadius = new CornerRadius(2, 0, 0, 2), Margin = new Thickness(0, 0, 1, 0) };
        d.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Positive);
        var t = new Border { CornerRadius = new CornerRadius(0, 2, 2, 0) };
        t.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Negative);
        Grid.SetColumn(t, 1);
        _tradeBar.Children.Add(d);
        _tradeBar.Children.Add(t);

        Children.Add(_hover);
        Children.Add(_layout);
        ToolTip = new ToolTip();
        ToolTipOpening += (_, _) => ((ToolTip)ToolTip).Content = BuildTooltip();
    }

    public uint EntityId => _row.EntityId;

    public void MoveTo(double y, bool animate)
    {
        if (!animate || Math.Abs(_translate.Y - y) < 0.5)
        {
            _translate.BeginAnimation(TranslateTransform.YProperty, null);
            _translate.Y = y;
            return;
        }
        var anim = new DoubleAnimation(y, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        _translate.BeginAnimation(TranslateTransform.YProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    public void Configure(RowSize size)
    {
        if (_size == size) return;
        _size = size;
        Height = RowMetrics.PvpHeight(size);
        foreach (UIElement el in new UIElement[] { _emblem, _name, _meta, _killed, _dealt, _taken, _hpBar, _tradeBar })
            if (el is FrameworkElement { Parent: Panel p }) p.Children.Remove(el);
        _layout.Children.Clear();
        _layout.ColumnDefinitions.Clear();
        _layout.RowDefinitions.Clear();
        _layout.Margin = new Thickness(5, 0, 7, 0);
        bool micro = size == RowSize.Micro;
        double font = size switch { RowSize.Normal => 12.5, RowSize.Compact => 11.5, _ => 10.5 };
        foreach (var tb in new[] { _name, _dealt, _taken }) tb.FontSize = font;
        _meta.FontSize = font - 1.5;
        _emblem.Width = _emblem.Height = micro ? 12 : size == RowSize.Normal ? 22 : 18;
        _emblem.Margin = new Thickness(0, 0, 7, 0);

        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(micro ? 48 : 58) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(micro ? 48 : 58) });

        var namePanel = new TrimPanel { VerticalAlignment = VerticalAlignment.Center };
        namePanel.Children.Add(_name);
        if (!micro) namePanel.Children.Add(_meta);
        namePanel.Children.Add(_killed);

        if (micro)
        {
            Place(_emblem, 0, 0);
            Place(namePanel, 0, 1);
            Place(_dealt, 0, 2);
            Place(_taken, 0, 3);
            return;
        }

        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(size == RowSize.Normal ? 12 : 9) });
        Place(_emblem, 0, 0, rowSpan: 2);
        Place(namePanel, 0, 1);
        Place(_dealt, 0, 2);
        Place(_taken, 0, 3);
        _hpBar.Margin = new Thickness(0, 0, 10, size == RowSize.Normal ? 4 : 3);
        _tradeBar.Margin = new Thickness(8, 0, 0, size == RowSize.Normal ? 4 : 3);
        Place(_hpBar, 1, 1);
        Place(_tradeBar, 1, 2, colSpan: 2);
    }

    private void Place(UIElement el, int row, int col, int rowSpan = 1, int colSpan = 1)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        Grid.SetRowSpan(el, rowSpan);
        Grid.SetColumnSpan(el, colSpan);
        _layout.Children.Add(el);
    }

    public void Update(PvpRow r, Brush hpBrush)
    {
        _row = r;
        _emblem.Class = r.Class;
        if (_name.Text != r.Name) _name.Text = r.Name;
        string meta = ClassInfo.ShortName(r.Class) + (string.IsNullOrEmpty(r.GuildName) ? "" : " · " + r.GuildName);
        if (_meta.Text != meta) _meta.Text = meta;
        _killed.Visibility = r.Killed ? Visibility.Visible : Visibility.Collapsed;
        _name.Opacity = r.Killed ? 0.7 : 1;
        _dealt.Text = "↑" + Fmt.Abbrev(r.DamageDealt);
        _taken.Text = "↓" + Fmt.Abbrev(r.DamageTaken);
        double hp = r.Killed ? 0 : Math.Clamp(r.HpFraction ?? 1, 0, 1);
        _hpFill.Width = new GridLength(hp, GridUnitType.Star);
        _hpEmpty.Width = new GridLength(1 - hp, GridUnitType.Star);
        _hpFillBorder.Background = hpBrush;
        _hpFillBorder.Opacity = r.HpFraction is null ? 0.4 : 1;
        double total = r.DamageDealt + r.DamageTaken;
        double share = total > 0 ? r.DamageDealt / total : 0.5;
        _tradeDealt.Width = new GridLength(Math.Max(0.0001, share), GridUnitType.Star);
        _tradeTaken.Width = new GridLength(Math.Max(0.0001, 1 - share), GridUnitType.Star);
    }

    private object BuildTooltip()
    {
        var r = _row;
        var lines = new List<(string, string)>
        {
            ("Class", ClassInfo.ShortName(r.Class)),
            ("Guild", string.IsNullOrEmpty(r.GuildName) ? Fmt.Dash : r.GuildName!),
            ("Damage dealt", Fmt.Exact(r.DamageDealt)),
            ("Damage taken", Fmt.Exact(r.DamageTaken)),
            ("HP", r.Killed ? "killed" : Fmt.Percent(r.HpFraction)),
        };
        var sp = new StackPanel { MinWidth = 180 };
        sp.Children.Add(Ui.Text(r.Name, ThemeKeys.Text, 13, FontWeights.SemiBold));
        var g = Ui.KeyValueGrid(lines.Select(l => l.Item1), out var values, 100);
        foreach (var (k, v) in lines) values[k].Text = v;
        g.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(g);
        return sp;
    }
}
