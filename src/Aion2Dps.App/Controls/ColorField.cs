using Aion2Dps.App.Theming;

namespace Aion2Dps.App.Controls;

/// <summary>Colour picker: swatch + hex box + preset swatches + "theme default" reset. Null value = use the theme's colour.</summary>
public sealed class ColorField : StackPanel
{
    private static readonly string[] Presets =
        ["#D9A441", "#E8C872", "#FF7A3D", "#E5605A", "#D23C64", "#B07CF0", "#5B8FD6", "#7FD8F7", "#38BFC7", "#6CCB7E", "#EFE9DC", "#121418"];

    private readonly Border _swatch = new() { Width = 30, Height = 24, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _hex = new() { Width = 92 };
    private readonly string _fallbackKey;
    private bool _suppress;

    public ColorField(string? value, string fallbackKey, Action<string?> changed)
    {
        _fallbackKey = fallbackKey;
        Orientation = Orientation.Vertical;
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        _swatch.Ref(Border.BorderBrushProperty, ThemeKeys.Border);
        top.Children.Add(_swatch);
        _hex.ToolTip = "#RRGGBB or #AARRGGBB";
        top.Children.Add(_hex);
        Children.Add(top);
        var presets = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var p in Presets)
        {
            var b = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand, Background = ColorUtil.Brush(p), ToolTip = p, BorderThickness = new Thickness(1) };
            b.Ref(Border.BorderBrushProperty, ThemeKeys.Border);
            string captured = p;
            b.MouseLeftButtonUp += (_, _) => { Set(captured); changed(captured); };
            presets.Children.Add(b);
        }
        Children.Add(presets);
        var reset = new Button { Content = "Theme default", Padding = new Thickness(8, 2, 8, 2), MinHeight = 24, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Use the theme's colour" };
        reset.Click += (_, _) => { Set(null); changed(null); };
        top.Children.Add(reset);
        _hex.TextChanged += (_, _) =>
        {
            if (_suppress) return;
            if (ColorUtil.TryParse(_hex.Text, out _)) { UpdateSwatch(_hex.Text); changed(_hex.Text.Trim()); }
        };
        Set(value);
    }

    public void Set(string? value)
    {
        _suppress = true;
        _hex.Text = value ?? "";
        _suppress = false;
        UpdateSwatch(value);
    }

    private void UpdateSwatch(string? value)
    {
        if (ColorUtil.TryParse(value, out var c)) _swatch.Background = ColorUtil.Brush(c);
        else _swatch.Ref(Border.BackgroundProperty, _fallbackKey);
    }
}
