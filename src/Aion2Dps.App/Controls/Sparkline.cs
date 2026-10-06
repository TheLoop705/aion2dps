namespace Aion2Dps.App.Controls;

/// <summary>Minimal line/area chart drawn in OnRender (theme brushes looked up at render time).</summary>
public sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double> _values = [];

    public IReadOnlyList<double> Values
    {
        get => _values;
        set { _values = value; InvalidateVisual(); }
    }

    /// <summary>Palette key for the line (default accent).</summary>
    public string BrushKey { get; set; } = ThemeKeys.Accent;

    /// <summary>Optional horizontal reference line value (e.g. median).</summary>
    public double? Reference { get; set; }

    /// <summary>Fixed minimum (default: 0).</summary>
    public double? Min { get; set; } = 0;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;
        var line = TryFindResource(BrushKey) as SolidColorBrush ?? Brushes.Goldenrod;
        var muted = TryFindResource(ThemeKeys.TextMuted) as Brush ?? Brushes.Gray;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_values.Count == 0) return;
        double max = _values.Max(), min = Min ?? _values.Min();
        if (Reference is { } r) max = Math.Max(max, r);
        if (max - min < 1e-9) max = min + 1;
        double X(int i) => _values.Count == 1 ? w / 2 : i * (w - 4) / (_values.Count - 1) + 2;
        double Y(double v) => h - 3 - (v - min) / (max - min) * (h - 6);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(X(0), h), true, true);
            for (int i = 0; i < _values.Count; i++) ctx.LineTo(new Point(X(i), Y(_values[i])), true, true);
            ctx.LineTo(new Point(X(_values.Count - 1), h), true, false);
        }
        geo.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, line.Color.R, line.Color.G, line.Color.B), Color.FromArgb(0x05, line.Color.R, line.Color.G, line.Color.B), 90);
        dc.DrawGeometry(fill, null, geo);

        var pen = new Pen(line, 1.6) { LineJoin = PenLineJoin.Round };
        for (int i = 1; i < _values.Count; i++)
            dc.DrawLine(pen, new Point(X(i - 1), Y(_values[i - 1])), new Point(X(i), Y(_values[i])));
        if (Reference is { } rv)
        {
            var dash = new Pen(muted, 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(dash, new Point(0, Y(rv)), new Point(w, Y(rv)));
        }
        if (_values.Count <= 40)
            for (int i = 0; i < _values.Count; i++)
                dc.DrawEllipse(line, null, new Point(X(i), Y(_values[i])), 2.2, 2.2);
    }
}
