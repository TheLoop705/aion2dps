using System.Windows.Media.Animation;
using Aion2Dps.App.Controls;
using Aion2Dps.App.Formatting;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Overlay;

/// <summary>What one row shows for the current view (computed by <see cref="OverlayView"/>).</summary>
internal readonly record struct RowDisplay(PlayerRow Row, int Rank, double Primary, double Secondary, double? Pct, double Bar);

/// <summary>
/// One party row: bar fill behind, rank, class emblem, name (+ "YOU"), optional crit %/max hit, primary and secondary
/// values and the percentage column. Instances are reused across refreshes and only changed text is touched.
/// </summary>
internal sealed class PlayerRowView : Grid
{
    private readonly Border _hover = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly Border _bar = new() { HorizontalAlignment = HorizontalAlignment.Stretch, IsHitTestVisible = false, RenderTransformOrigin = new Point(0, 0.5) };
    private readonly ScaleTransform _barScale = new(0, 1);
    private readonly Border _localStripe = new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly Border _pinOutline = new() { BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Grid _content = new();
    private readonly TextBlock _rank = Ui.Text("", ThemeKeys.TextMuted, mono: true);
    private readonly ClassEmblem _emblem = new(15);
    private readonly Border _strip = new() { Width = 3, Margin = new Thickness(0, 2, 5, 2), CornerRadius = new CornerRadius(1) };
    private readonly TextBlock _name = Ui.Text("", ThemeKeys.Text);
    private readonly Border _you = Ui.Pill("YOU", ThemeKeys.Accent, ThemeKeys.AccentText, 8);
    private readonly TextBlock _dead = Ui.Icon("", ThemeKeys.Negative, 9);
    private readonly TextBlock _crit = Ui.Text("", ThemeKeys.Crit, mono: true);
    private readonly TextBlock _max = Ui.Text("", ThemeKeys.TextMuted, mono: true);
    private readonly TextBlock _gearScore = Ui.Text("", ThemeKeys.TextMuted, mono: true);
    private readonly TextBlock _primary = Ui.Text("", ThemeKeys.Text, weight: FontWeights.SemiBold, mono: true);
    private readonly TextBlock _secondary = Ui.Text("", ThemeKeys.TextMuted, mono: true);
    private readonly TextBlock _pct = Ui.Text("", ThemeKeys.TextMuted, mono: true);
    private readonly TranslateTransform _translate = new();

    private OverlayViewOptions? _options;
    private RowDisplay _display;
    private CharacterClass _class = (CharacterClass)(-1);
    private double _barTarget = -1;

    public PlayerRowView()
    {
        RenderTransform = _translate;
        VerticalAlignment = VerticalAlignment.Top;
        Background = Brushes.Transparent; // hit-testable for clicks and tooltips
        Cursor = Cursors.Hand;
        _bar.RenderTransform = _barScale;
        _hover.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceAlt);
        _localStripe.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Accent);
        _pinOutline.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Accent);
        _bar.SetResourceReference(Border.CornerRadiusProperty, ThemeKeys.BarCornerRadius);
        _you.Margin = new Thickness(5, 0, 0, 0);
        _dead.Margin = new Thickness(4, 0, 0, 0);
        _dead.ToolTip = "Dead";
        foreach (var t in new[] { _crit, _max, _gearScore, _primary, _secondary, _pct, _rank })
            t.TextAlignment = TextAlignment.Right;
        _rank.TextAlignment = TextAlignment.Center;

        Children.Add(_hover);
        Children.Add(_bar);
        Children.Add(_localStripe);
        Children.Add(_content);
        Children.Add(_pinOutline);

        MouseEnter += (_, _) => _hover.Opacity = 0.55;
        MouseLeave += (_, _) => _hover.Opacity = 0;
        ToolTip = new ToolTip();
        ToolTipService.SetInitialShowDelay(this, 350);
        ToolTipOpening += (_, _) => ((ToolTip)ToolTip).Content = BuildTooltip();
    }

    public uint EntityId => _display.Row.EntityId;
    public PlayerRow Row => _display.Row;
    public double Y => _translate.Y;

    public void MoveTo(double y, bool animate)
    {
        if (!animate || Math.Abs(_translate.Y - y) < 0.5)
        {
            _translate.BeginAnimation(TranslateTransform.YProperty, null);
            _translate.Y = y;
            return;
        }
        var anim = new DoubleAnimation(y, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) => { _translate.BeginAnimation(TranslateTransform.YProperty, null); _translate.Y = y; };
        _translate.BeginAnimation(TranslateTransform.YProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    public void SetPinned(bool pinned) => _pinOutline.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;

    public void Configure(OverlayViewOptions options)
    {
        if (_options is { } o && o.RowSize == options.RowSize && o.BarStyle == options.BarStyle && o.ShowCritRate == options.ShowCritRate
            && o.ShowMaxHit == options.ShowMaxHit && o.ShowGearScore == options.ShowGearScore && o.ShowTotal == options.ShowTotal && o.ShowContribution == options.ShowContribution
            && o.ShowRank == options.ShowRank && o.ShowClassEmblem == options.ShowClassEmblem && o.View == options.View)
        {
            _options = options;
            return;
        }
        _options = options;
        var m = RowMetrics.For(options.RowSize);
        bool micro = options.RowSize == RowSize.Micro;
        Height = m.Height;

        // Bar style
        switch (options.BarStyle)
        {
            case BarStyle.Slim:
                _bar.Height = micro ? 1.5 : 2.5;
                _bar.VerticalAlignment = VerticalAlignment.Bottom;
                _bar.Margin = new Thickness(0, 0, 0, 1);
                _bar.Opacity = 0.95;
                _bar.OpacityMask = null;
                break;
            case BarStyle.Solid:
                _bar.Height = double.NaN;
                _bar.VerticalAlignment = VerticalAlignment.Stretch;
                _bar.Margin = new Thickness(0, 1, 0, 1);
                _bar.Opacity = 0.42;
                _bar.OpacityMask = null;
                break;
            default:
                _bar.Height = double.NaN;
                _bar.VerticalAlignment = VerticalAlignment.Stretch;
                _bar.Margin = new Thickness(0, 1, 0, 1);
                _bar.Opacity = 1;
                var mask = new LinearGradientBrush(Color.FromArgb(0xA0, 0, 0, 0), Color.FromArgb(0x30, 0, 0, 0), 0);
                mask.Freeze();
                _bar.OpacityMask = mask;
                break;
        }

        _content.Children.Clear();
        _content.ColumnDefinitions.Clear();
        _content.Margin = new Thickness(micro ? 2 : 3, 0, micro ? 4 : 6, 0);

        void Col(double width, UIElement? element)
        {
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
            if (element is null) return;
            Grid.SetColumn(element, _content.ColumnDefinitions.Count - 1);
            _content.Children.Add(element);
        }

        foreach (var t in new[] { _rank, _name, _crit, _max, _gearScore, _primary, _secondary, _pct })
            t.FontSize = m.FontSize;
        _primary.FontSize = m.NumberSize + (micro ? 0 : 0.5);
        _you.Visibility = Visibility.Collapsed;

        if (options.ShowRank) Col(m.RankWidth, _rank);
        if (micro)
        {
            _strip.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ClassBrush(_class < 0 ? CharacterClass.Unknown : _class));
            Col(8, _strip);
        }
        else if (options.ShowClassEmblem)
        {
            _emblem.Width = _emblem.Height = m.Emblem;
            _emblem.Margin = new Thickness(1, 0, 6, 0);
            Col(m.Emblem + 7, _emblem);
        }
        else Col(4, null);

        // The name trims while the badges stay right after it.
        foreach (var el in new FrameworkElement[] { _name, _dead, _you })
            if (el.Parent is Panel old) old.Children.Remove(el);
        var nameHost = new TrimPanel { VerticalAlignment = VerticalAlignment.Center };
        nameHost.Children.Add(_name);
        nameHost.Children.Add(_dead);
        if (!micro) nameHost.Children.Add(_you);
        Col(-1, nameHost);

        double numW = options.RowSize switch { RowSize.Normal => 56, RowSize.Compact => 52, _ => 46 };
        if (options.ShowGearScore) Col(numW - 8, _gearScore);
        if (!micro && options.ShowCritRate) Col(numW - 10, _crit);
        if (!micro && options.ShowMaxHit) Col(numW, _max);
        Col(numW, _primary);
        if (!micro && options.ShowTotal) Col(numW, _secondary);
        if (options.ShowContribution) Col(micro ? 40 : 46, _pct);
        _barTarget = -1;
    }

    public void Update(RowDisplay d, bool isPinned, bool animate)
    {
        _display = d;
        var r = d.Row;
        if (_class != r.Class)
        {
            _class = r.Class;
            _emblem.Class = r.Class;
            _strip.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ClassBrush(r.Class));
            _bar.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ClassBrush(r.Class));
        }
        Set(_rank, d.Rank.ToString(CultureInfo.InvariantCulture));
        Set(_name, r.Name.Length == 0 ? "Unknown" : r.Name);
        if (_options?.RowSize == RowSize.Micro && r.IsLocal) Set(_name, "▸ " + r.Name);
        _you.Visibility = r.IsLocal && _options?.RowSize != RowSize.Micro ? Visibility.Visible : Visibility.Collapsed;
        _localStripe.Visibility = r.IsLocal ? Visibility.Visible : Visibility.Collapsed;
        _dead.Visibility = r.IsDead ? Visibility.Visible : Visibility.Collapsed;
        _name.Opacity = r.IsDead ? 0.6 : 1;
        Set(_crit, r.Hits > 0 ? Fmt.Percent(r.CritRate) : Fmt.Dash);
        Set(_max, r.MaxHit > 0 ? Fmt.Abbrev(r.MaxHit) : Fmt.Dash);
        Set(_gearScore, r.GearScore is { } gs ? gs.ToString(CultureInfo.InvariantCulture) : Fmt.Dash);
        _gearScore.ToolTip = r.GearScore is { } gear ? $"Gear score: {gear.ToString(CultureInfo.InvariantCulture)}" : "Gear score unknown";
        Set(_primary, Fmt.Abbrev(d.Primary));
        Set(_secondary, Fmt.Abbrev(d.Secondary));
        Set(_pct, d.Pct is { } p && p > 0 ? Fmt.Percent(p) : Fmt.Dash);
        SetPinned(isPinned);

        double target = Math.Clamp(d.Bar, 0, 1);
        if (Math.Abs(target - _barTarget) > 0.001)
        {
            _barTarget = target;
            if (animate)
            {
                var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(240)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim, HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _barScale.ScaleX = target;
            }
        }
    }

    private static void Set(TextBlock tb, string text)
    {
        if (!string.Equals(tb.Text, text, StringComparison.Ordinal)) tb.Text = text;
    }

    private object BuildTooltip()
    {
        var r = _display.Row;
        var sp = new StackPanel { MinWidth = 200 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var em = new ClassEmblem(16) { Class = r.Class, Margin = new Thickness(0, 0, 6, 0) };
        head.Children.Add(em);
        head.Children.Add(Ui.Text(r.Name, ThemeKeys.Text, 13, FontWeights.SemiBold));
        head.Children.Add(Ui.Text($"  {ClassInfo.ShortName(r.Class)}{(r.IsLocal ? " · you" : r.IsPartyMember ? " · party" : "")}", ThemeKeys.TextMuted, 11.5));
        sp.Children.Add(head);
        var lines = new List<(string, string)>
        {
            ("Gear score", r.GearScore is { } gs ? Fmt.Exact((long)gs) : Fmt.Dash),
            ("Damage", Fmt.Exact(r.Damage)),
            ("DPS", Fmt.Exact(r.Dps)),
            ("Contribution", r.Contribution > 0 ? Fmt.Percent(r.Contribution, 2) : Fmt.Dash),
            ("Share of party", r.DamageShare > 0 ? Fmt.Percent(r.DamageShare, 2) : Fmt.Dash),
            ("Crit rate", r.Hits > 0 ? $"{Fmt.Percent(r.CritRate, 1)} of {Fmt.Exact(r.Hits)} hits" : Fmt.Dash),
            ("Max hit", r.MaxHit > 0 ? Fmt.Exact(r.MaxHit) : Fmt.Dash),
            ("Healing", r.Healing > 0 ? Fmt.Exact(r.Healing) : Fmt.Dash),
            ("Damage taken", r.DamageTaken > 0 ? Fmt.Exact(r.DamageTaken) : Fmt.Dash),
        };
        var grid = Ui.KeyValueGrid(lines.Select(l => l.Item1), out var values, 110);
        foreach (var (k, v) in lines) values[k].Text = v;
        sp.Children.Add(grid);
        var hint = Ui.Text("Click: breakdown · Ctrl+click two rows: compare", ThemeKeys.TextMuted, 10.5);
        hint.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(hint);
        return sp;
    }
}
