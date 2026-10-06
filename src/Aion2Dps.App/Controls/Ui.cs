using System.Windows.Documents;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Aion2Dps.App.Controls;

/// <summary>Small factory helpers for code-built, theme-aware UI (everything references palette keys dynamically).</summary>
public static class Ui
{
    // Segoe MDL2 Assets glyphs
    public const string IconReset = "";
    public const string IconCopy = "";
    public const string IconSettings = "";
    public const string IconHide = "";
    public const string IconClose = "";
    public const string IconStopwatch = "";
    public const string IconLock = "";
    public const string IconUnlock = "";
    public const string IconLeft = "";
    public const string IconRight = "";
    public const string IconMeter = "";
    public const string IconHistory = "";
    public const string IconTrends = "";
    public const string IconCharacter = "";
    public const string IconAppearance = "";
    public const string IconAbout = "";
    public const string IconRecord = "";
    public const string IconStop = "";
    public const string IconFolder = "";
    public const string IconWarning = "";
    public const string IconStar = "";
    public const string IconLink = "";
    public const string IconPlug = "";

    public static T Ref<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static T RefC<T>(this T element, DependencyProperty property, string key) where T : FrameworkContentElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static TextBlock Text(string text, string brushKey = ThemeKeys.Text, double size = 12, FontWeight? weight = null, bool mono = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        if (mono) Mono(tb);
        return tb;
    }

    public static TextBlock Mono(TextBlock tb)
    {
        tb.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.MonoFontFamily);
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    public static TextBlock Icon(string glyph, string brushKey = ThemeKeys.TextMuted, double size = 12)
    {
        var tb = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons"),
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    public static Button IconButton(string glyph, string tooltip, Action onClick, double fontSize = 11)
    {
        var b = new Button { Content = glyph, ToolTip = tooltip, FontSize = fontSize };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Style.IconButton");
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    public static Button Chip(string text, string tooltip, Action onClick)
    {
        var b = new Button { Content = text, ToolTip = tooltip };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Style.ChipButton");
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    public static Button Button(string text, Action onClick, bool accent = false, string? icon = null)
    {
        object content = text;
        if (icon is not null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(ic);
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 0) };
        if (accent) b.SetResourceReference(FrameworkElement.StyleProperty, "Style.AccentButton");
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A titled card container.</summary>
    public static Border Card(string? title, UIElement content, string? subtitle = null)
    {
        var sp = new StackPanel();
        if (title is not null)
        {
            var t = new TextBlock { Text = title.ToUpperInvariant() };
            t.SetResourceReference(FrameworkElement.StyleProperty, "Style.CardTitle");
            sp.Children.Add(t);
        }
        if (subtitle is not null)
        {
            var s = Text(subtitle, ThemeKeys.TextMuted, 12);
            s.TextWrapping = TextWrapping.Wrap;
            s.Margin = new Thickness(0, -4, 0, 10);
            sp.Children.Add(s);
        }
        sp.Children.Add(content);
        var card = new Border { Child = sp };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Style.Card");
        return card;
    }

    public static TextBlock PageTitle(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(FrameworkElement.StyleProperty, "Style.PageTitle");
        return t;
    }

    public static TextBlock PageSubtitle(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(FrameworkElement.StyleProperty, "Style.PageSubtitle");
        return t;
    }

    /// <summary>Two-column label/value grid; returns the value TextBlocks so callers can update them.</summary>
    public static Grid KeyValueGrid(IEnumerable<string> labels, out Dictionary<string, TextBlock> values, double labelWidth = 150)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        values = new Dictionary<string, TextBlock>();
        int row = 0;
        foreach (var label in labels)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Text(label, ThemeKeys.TextMuted, 12.5);
            l.Margin = new Thickness(0, 3, 8, 3);
            Grid.SetRow(l, row);
            var v = Text(Formatting.Fmt.Dash, ThemeKeys.Text, 12.5, mono: true);
            v.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            g.Children.Add(l);
            g.Children.Add(v);
            values[label] = v;
            row++;
        }
        return g;
    }

    /// <summary>A label above a control (settings forms).</summary>
    public static StackPanel Field(string label, UIElement control, string? hint = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var l = Text(label, ThemeKeys.Text, 12.5, FontWeights.SemiBold);
        l.Margin = new Thickness(0, 0, 0, 5);
        sp.Children.Add(l);
        sp.Children.Add(control);
        if (hint is not null)
        {
            var h = Text(hint, ThemeKeys.TextMuted, 11.5);
            h.TextWrapping = TextWrapping.Wrap;
            h.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(h);
        }
        return sp;
    }

    public static Border Divider(double margin = 8)
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, margin, 0, margin) };
        b.SetResourceReference(Border.BackgroundProperty, "App.Divider");
        return b;
    }

    public static Ellipse Dot(double size, string brushKey)
    {
        var e = new Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        e.SetResourceReference(Shape.FillProperty, brushKey);
        return e;
    }

    public static TextBlock LinkText(string text, string url, double size = 12)
    {
        var tb = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap };
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += (_, e) => { OpenUrl(e.Uri.ToString()); e.Handled = true; };
        tb.Inlines.Add(link);
        return tb;
    }

    /// <summary>Opens a URL or folder in the shell (user-initiated only).</summary>
    public static void OpenUrl(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn("UI", $"Could not open {target}: {ex.Message}"); }
    }

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Label)> items, T selected, Action<T> onChanged, double width = 220)
    {
        var cb = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (value, label) in items)
            cb.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        foreach (ComboBoxItem it in cb.Items)
            if (Equals(it.Tag, selected)) { cb.SelectedItem = it; break; }
        if (cb.SelectedItem is null && cb.Items.Count > 0) cb.SelectedIndex = 0;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is ComboBoxItem { Tag: T v }) onChanged(v);
        };
        return cb;
    }

    public static CheckBox Check(string text, bool value, Action<bool> onChanged, string? tooltip = null)
    {
        var c = new CheckBox { Content = text, IsChecked = value, ToolTip = tooltip };
        c.Checked += (_, _) => onChanged(true);
        c.Unchecked += (_, _) => onChanged(false);
        return c;
    }

    public static Slider Slider(double min, double max, double value, Action<double> onChanged, double width = 220, double tick = 0)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        if (tick > 0) { s.TickFrequency = tick; s.IsSnapToTickEnabled = true; }
        s.ValueChanged += (_, e) => onChanged(e.NewValue);
        return s;
    }

    public static Border Pill(string text, string backgroundKey, string foregroundKey, double size = 9.5)
    {
        var t = Text(text, foregroundKey, size, FontWeights.Bold);
        var b = new Border { Child = t, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        return b;
    }
}
