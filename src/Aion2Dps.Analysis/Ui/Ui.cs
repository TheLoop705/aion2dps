using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>Small factory helpers that build themed elements (all colours through ThemeKeys resource references).</summary>
internal static class Ui
{
    public const double CaptionSize = 10.5;

    public static T Res<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    /// <summary>Applies the root font, size, foreground and background of an analysis view.</summary>
    public static void ApplyRootTheme(Control root, string backgroundKey = ThemeKeys.WindowBackground)
    {
        root.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontFamily);
        root.SetResourceReference(Control.FontSizeProperty, ThemeKeys.FontSize);
        root.SetResourceReference(Control.ForegroundProperty, ThemeKeys.Text);
        root.SetResourceReference(Control.BackgroundProperty, backgroundKey);
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        root.UseLayoutRounding = true;
        root.SnapsToDevicePixels = true;
    }

    /// <summary>
    /// Keeps <paramref name="content"/> as wide as the scroll viewport (but never below its MinWidth), so a
    /// horizontally scrollable view lays out like a normal stretched page and only scrolls when it is narrower than
    /// MinWidth. Without this, the ScrollViewer measures with infinite width and wrap panels never wrap.
    /// </summary>
    public static void FitWidth(ScrollViewer scroll, FrameworkElement content)
    {
        void Apply()
        {
            double vw = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
            if (vw <= 0) return;
            double w = Math.Max(content.MinWidth, vw - content.Margin.Left - content.Margin.Right);
            if (double.IsNaN(content.Width) || Math.Abs(content.Width - w) > 0.5) content.Width = w;
        }
        scroll.SizeChanged += (_, _) => Apply();
        scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0) Apply();
        };
    }

    /// <summary>Re-runs <paramref name="rebuild"/> when the game-data language changes while the view is loaded.</summary>
    public static void RebuildOnLanguageChange(FrameworkElement view, Func<IGameData?> gameData, Action rebuild)
    {
        IGameData? hooked = null;
        void Handler() => view.Dispatcher.BeginInvoke(rebuild);
        view.Loaded += (_, _) =>
        {
            if (hooked is not null) return;
            hooked = gameData();
            if (hooked is not null) hooked.LanguageChanged += Handler;
        };
        view.Unloaded += (_, _) =>
        {
            if (hooked is not null) hooked.LanguageChanged -= Handler;
            hooked = null;
        };
    }

    public static TextBlock Text(string text, double size = 0, string fg = ThemeKeys.Text, FontWeight? weight = null,
        bool mono = false, string? tip = null, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (size > 0) tb.FontSize = size;
        if (weight is { } w) tb.FontWeight = w;
        tb.SetResourceReference(TextBlock.ForegroundProperty, fg);
        if (mono) tb.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.MonoFontFamily);
        if (tip is not null) tb.ToolTip = tip;
        return tb;
    }

    public static TextBlock Caption(string text, string fg = ThemeKeys.TextMuted) =>
        Text(text.ToUpperInvariant(), CaptionSize, fg, FontWeights.SemiBold);

    public static TextBlock Number(string text, string? tip = null, string fg = ThemeKeys.Text, FontWeight? weight = null, double size = 0)
    {
        var tb = Text(text, size, fg, weight, mono: true, tip: tip);
        tb.HorizontalAlignment = HorizontalAlignment.Right;
        tb.TextAlignment = TextAlignment.Right;
        return tb;
    }

    /// <summary>A panel card with an optional title row.</summary>
    public static Border Card(UIElement content, string? title = null, UIElement? headerRight = null, string? subtitle = null,
        Thickness? padding = null)
    {
        var stack = new DockPanel { LastChildFill = true };
        if (title is not null || headerRight is not null)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            if (headerRight is not null)
            {
                DockPanel.SetDock(headerRight, Dock.Right);
                head.Children.Add(headerRight);
            }
            var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (title is not null) titles.Children.Add(Text(title, 13.5, ThemeKeys.Text, FontWeights.SemiBold));
            if (subtitle is not null)
            {
                var sub = Text(subtitle, 11.5, ThemeKeys.TextMuted);
                sub.Margin = new Thickness(10, 1, 0, 0);
                titles.Children.Add(sub);
            }
            head.Children.Add(titles);
            DockPanel.SetDock(head, Dock.Top);
            stack.Children.Add(head);
        }
        stack.Children.Add(content);
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(14, 12, 14, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Surface);
        card.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Border);
        card.SetResourceReference(Border.CornerRadiusProperty, ThemeKeys.CornerRadius);
        return card;
    }

    /// <summary>Stat tile: caption, big value, optional sub line.</summary>
    public static Border Tile(string caption, string value, string? sub = null, string? tip = null,
        string valueFg = ThemeKeys.Text, double valueSize = 19)
    {
        var sp = new StackPanel();
        sp.Children.Add(Caption(caption));
        var v = Text(value, valueSize, valueFg, FontWeights.SemiBold, mono: true, tip: tip);
        v.Margin = new Thickness(0, 3, 0, 0);
        sp.Children.Add(v);
        if (sub is not null)
        {
            var s = Text(sub, 11, ThemeKeys.TextMuted);
            s.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(s);
        }
        var b = new Border
        {
            Padding = new Thickness(12, 9, 12, 10),
            Margin = new Thickness(0, 0, 8, 8),
            Child = sp,
            ToolTip = tip,
        };
        b.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceAlt);
        b.SetResourceReference(Border.CornerRadiusProperty, ThemeKeys.CornerRadius);
        return b;
    }

    /// <summary>Tiles laid out in equal columns (wrapping into rows when there are more than <paramref name="columns"/>).</summary>
    public static UniformGrid TileGrid(int columns, IEnumerable<UIElement> tiles)
    {
        var g = new UniformGrid { Columns = Math.Max(1, columns), Margin = new Thickness(0, 0, -8, 4) };
        foreach (var t in tiles) g.Children.Add(t);
        return g;
    }

    /// <summary>Pill badge: tinted background with coloured text (or solid when <paramref name="solid"/>).</summary>
    public static FrameworkElement Badge(string text, string colorKey, bool solid = false, string? tip = null)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = tip };
        var bg = new Border { CornerRadius = new CornerRadius(4), Opacity = solid ? 1 : 0.18 };
        bg.SetResourceReference(Border.BackgroundProperty, colorKey);
        var tb = Text(text, 10, solid ? ThemeKeys.AccentText : colorKey, FontWeights.Bold);
        tb.Margin = new Thickness(6, 1.5, 6, 2);
        tb.TextTrimming = TextTrimming.None;
        grid.Children.Add(bg);
        grid.Children.Add(tb);
        return grid;
    }

    public static Button Button(string text, Action onClick, string? tip = null, string? styleKey = null, FrameworkElement? styleSource = null)
    {
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (styleKey is not null) b.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Warning strip (e.g. capture gaps).</summary>
    public static FrameworkElement Banner(string text, string colorKey = ThemeKeys.Warning)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        var bg = new Border { CornerRadius = new CornerRadius(5), Opacity = 0.14 };
        bg.SetResourceReference(Border.BackgroundProperty, colorKey);
        var edge = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
        edge.SetResourceReference(Border.BorderBrushProperty, colorKey);
        var tb = Text(text, 0, colorKey, FontWeights.SemiBold, wrap: true);
        tb.Margin = new Thickness(12, 8, 12, 8);
        grid.Children.Add(bg);
        grid.Children.Add(edge);
        grid.Children.Add(tb);
        return grid;
    }

    public static FrameworkElement Empty(string text)
    {
        var tb = Text(text, 0, ThemeKeys.TextMuted, wrap: true);
        tb.Margin = new Thickness(4, 10, 4, 10);
        tb.HorizontalAlignment = HorizontalAlignment.Center;
        return tb;
    }

    /// <summary>Skill name cell: tile + name + optional tags.</summary>
    public static FrameworkElement SkillCell(string name, uint skillId, CharacterClass tileClass,
        IEnumerable<(string Text, string ColorKey)>? tags = null, double tileSize = 20)
    {
        var dp = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        var tile = new SkillTile { Width = tileSize, Height = tileSize, Margin = new Thickness(0, 0, 8, 0) };
        tile.SetSkill(name, skillId, tileClass);
        DockPanel.SetDock(tile, Dock.Left);
        dp.Children.Add(tile);
        if (tags is not null)
        {
            foreach (var (t, key) in tags.Reverse())
            {
                var badge = Badge(t, key);
                badge.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(badge, Dock.Right);
                dp.Children.Add(badge);
            }
        }
        dp.Children.Add(Text(name, 0, ThemeKeys.Text, tip: $"{name}  (id {skillId})"));
        return dp;
    }

    /// <summary>Number with an inline bar below/beside it.</summary>
    public static FrameworkElement BarCell(string text, double fraction, string? tip = null, CharacterClass? cls = null,
        string? fillKey = null, string fg = ThemeKeys.Text)
    {
        var g = new Grid { VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var bar = new MiniBar { Value = fraction, Class = cls, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (fillKey is not null) bar.SetResourceReference(MiniBar.FillProperty, fillKey);
        var num = Number(text, null, fg);
        Grid.SetColumn(num, 1);
        g.Children.Add(bar);
        g.Children.Add(num);
        return g;
    }

    public static FrameworkElement Emblem(CharacterClass cls, double size = 22) =>
        new ClassEmblem { Class = cls, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Thin separator line.</summary>
    public static Rectangle Separator(double margin = 8)
    {
        var r = new Rectangle { Height = 1, Margin = new Thickness(0, margin, 0, margin), SnapsToDevicePixels = true };
        r.SetResourceReference(Shape.FillProperty, ThemeKeys.Border);
        return r;
    }

    /// <summary>Inline run list "label value · label value" for compact header facts.</summary>
    public static TextBlock Facts(params (string Label, string Value)[] facts)
    {
        var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        tb.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextMuted);
        bool first = true;
        foreach (var (label, value) in facts)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (!first) tb.Inlines.Add(new Run("  ·  "));
            first = false;
            if (!string.IsNullOrEmpty(label)) tb.Inlines.Add(new Run(label + " "));
            var v = new Run(value) { FontWeight = FontWeights.SemiBold };
            v.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.Text);
            tb.Inlines.Add(v);
        }
        return tb;
    }

    /// <summary>Segmented single-choice control built from RadioButtons.</summary>
    public static WrapPanel Segments<T>(IEnumerable<(string Label, T Value)> options, T selected, Action<T> onChange, string group)
    {
        var wp = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = Equals(value, selected), Tag = value };
            rb.SetResourceReference(FrameworkElement.StyleProperty, "A2.Segment");
            rb.Checked += (_, _) => onChange(value);
            wp.Children.Add(rb);
        }
        return wp;
    }
}
