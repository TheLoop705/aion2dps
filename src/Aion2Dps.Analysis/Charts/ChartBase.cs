using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>A row of a chart's hover tooltip.</summary>
public readonly record struct TooltipLine(Brush? Swatch, string Label, string Value, bool Emphasis = false);

/// <summary>
/// Base for the custom-drawn charts. Every theme value is a dependency property bound to the app theme with
/// <see cref="FrameworkElement.SetResourceReference"/>, so a runtime theme swap re-renders the chart. When a key is
/// missing the property keeps a neutral dark-theme default.
/// </summary>
public abstract class ChartBase : FrameworkElement
{
    private static DependencyProperty Reg(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(ChartBase),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = Reg(nameof(TextBrush), typeof(Brush), SkillVisuals.Hex("#E6E8EC"));
    public static readonly DependencyProperty MutedBrushProperty = Reg(nameof(MutedBrush), typeof(Brush), SkillVisuals.Hex("#8A93A3"));
    public static readonly DependencyProperty GridBrushProperty = Reg(nameof(GridBrush), typeof(Brush), SkillVisuals.Hex("#2C323C"));
    public static readonly DependencyProperty SurfaceBrushProperty = Reg(nameof(SurfaceBrush), typeof(Brush), SkillVisuals.Hex("#1A1E25"));
    public static readonly DependencyProperty SurfaceAltBrushProperty = Reg(nameof(SurfaceAltBrush), typeof(Brush), SkillVisuals.Hex("#232831"));
    public static readonly DependencyProperty AccentBrushProperty = Reg(nameof(AccentBrush), typeof(Brush), SkillVisuals.Hex("#D9A441"));
    public static readonly DependencyProperty CritBrushProperty = Reg(nameof(CritBrush), typeof(Brush), SkillVisuals.Hex("#F2C14E"));
    public static readonly DependencyProperty DotBrushProperty = Reg(nameof(DotBrush), typeof(Brush), SkillVisuals.Hex("#A66BFF"));
    public static readonly DependencyProperty HealBrushProperty = Reg(nameof(HealBrush), typeof(Brush), SkillVisuals.Hex("#4CC38A"));
    public static readonly DependencyProperty PositiveBrushProperty = Reg(nameof(PositiveBrush), typeof(Brush), SkillVisuals.Hex("#4CC38A"));
    public static readonly DependencyProperty NegativeBrushProperty = Reg(nameof(NegativeBrush), typeof(Brush), SkillVisuals.Hex("#E5534B"));
    public static readonly DependencyProperty WarningBrushProperty = Reg(nameof(WarningBrush), typeof(Brush), SkillVisuals.Hex("#E3A33B"));
    public static readonly DependencyProperty HpHighBrushProperty = Reg(nameof(HpHighBrush), typeof(Brush), SkillVisuals.Hex("#4CC38A"));
    public static readonly DependencyProperty HpMidBrushProperty = Reg(nameof(HpMidBrush), typeof(Brush), SkillVisuals.Hex("#E3A33B"));
    public static readonly DependencyProperty HpLowBrushProperty = Reg(nameof(HpLowBrush), typeof(Brush), SkillVisuals.Hex("#E5534B"));
    public static readonly DependencyProperty BarTrackBrushProperty = Reg(nameof(BarTrackBrush), typeof(Brush), SkillVisuals.Hex("#2A303A"));
    public static readonly DependencyProperty BarFillBrushProperty = Reg(nameof(BarFillBrush), typeof(Brush), SkillVisuals.Hex("#D9A441"));
    public static readonly DependencyProperty ChartFontFamilyProperty = Reg(nameof(ChartFontFamily), typeof(FontFamily), new FontFamily("Segoe UI"));
    public static readonly DependencyProperty MonoFontFamilyProperty = Reg(nameof(MonoFontFamily), typeof(FontFamily), new FontFamily("Consolas"));
    public static readonly DependencyProperty BaseFontSizeProperty = Reg(nameof(BaseFontSize), typeof(double), 12.0);

    private static readonly Dictionary<CharacterClass, DependencyProperty> ClassBrushProperties = new();

    static ChartBase()
    {
        foreach (var c in ClassInfo.All.Prepend(CharacterClass.Unknown))
            ClassBrushProperties[c] = Reg("ClassBrush" + c, typeof(Brush), SkillVisuals.Hex(ClassInfo.DefaultColor(c)));
    }

    protected ChartBase()
    {
        SetResourceReference(TextBrushProperty, ThemeKeys.Text);
        SetResourceReference(MutedBrushProperty, ThemeKeys.TextMuted);
        SetResourceReference(GridBrushProperty, ThemeKeys.Border);
        SetResourceReference(SurfaceBrushProperty, ThemeKeys.Surface);
        SetResourceReference(SurfaceAltBrushProperty, ThemeKeys.SurfaceAlt);
        SetResourceReference(AccentBrushProperty, ThemeKeys.Accent);
        SetResourceReference(CritBrushProperty, ThemeKeys.Crit);
        SetResourceReference(DotBrushProperty, ThemeKeys.Dot);
        SetResourceReference(HealBrushProperty, ThemeKeys.Heal);
        SetResourceReference(PositiveBrushProperty, ThemeKeys.Positive);
        SetResourceReference(NegativeBrushProperty, ThemeKeys.Negative);
        SetResourceReference(WarningBrushProperty, ThemeKeys.Warning);
        SetResourceReference(HpHighBrushProperty, ThemeKeys.HpHigh);
        SetResourceReference(HpMidBrushProperty, ThemeKeys.HpMid);
        SetResourceReference(HpLowBrushProperty, ThemeKeys.HpLow);
        SetResourceReference(BarTrackBrushProperty, ThemeKeys.BarTrack);
        SetResourceReference(BarFillBrushProperty, ThemeKeys.BarFill);
        SetResourceReference(ChartFontFamilyProperty, ThemeKeys.FontFamily);
        SetResourceReference(MonoFontFamilyProperty, ThemeKeys.MonoFontFamily);
        SetResourceReference(BaseFontSizeProperty, ThemeKeys.FontSize);
        foreach (var (c, dp) in ClassBrushProperties) SetResourceReference(dp, ThemeKeys.ClassBrush(c));
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public Brush TextBrush => (Brush)GetValue(TextBrushProperty);
    public Brush MutedBrush => (Brush)GetValue(MutedBrushProperty);
    public Brush GridBrush => (Brush)GetValue(GridBrushProperty);
    public Brush SurfaceBrush => (Brush)GetValue(SurfaceBrushProperty);
    public Brush SurfaceAltBrush => (Brush)GetValue(SurfaceAltBrushProperty);
    public Brush AccentBrush => (Brush)GetValue(AccentBrushProperty);
    public Brush CritBrush => (Brush)GetValue(CritBrushProperty);
    public Brush DotBrush => (Brush)GetValue(DotBrushProperty);
    public Brush HealBrush => (Brush)GetValue(HealBrushProperty);
    public Brush PositiveBrush => (Brush)GetValue(PositiveBrushProperty);
    public Brush NegativeBrush => (Brush)GetValue(NegativeBrushProperty);
    public Brush WarningBrush => (Brush)GetValue(WarningBrushProperty);
    public Brush HpHighBrush => (Brush)GetValue(HpHighBrushProperty);
    public Brush HpMidBrush => (Brush)GetValue(HpMidBrushProperty);
    public Brush HpLowBrush => (Brush)GetValue(HpLowBrushProperty);
    public Brush BarTrackBrush => (Brush)GetValue(BarTrackBrushProperty);
    public Brush BarFillBrush => (Brush)GetValue(BarFillBrushProperty);
    public FontFamily ChartFontFamily => (FontFamily)GetValue(ChartFontFamilyProperty);
    public FontFamily MonoFontFamily => (FontFamily)GetValue(MonoFontFamilyProperty);
    public double BaseFontSize => GetValue(BaseFontSizeProperty) is double d && d > 4 ? d : 12.0;

    /// <summary>Theme brush of a class (falls back to the default palette).</summary>
    public Brush ClassBrush(CharacterClass c) =>
        ClassBrushProperties.TryGetValue(c, out var dp) && GetValue(dp) is Brush b ? b
        : (Brush)GetValue(ClassBrushProperties[CharacterClass.Unknown]);

    /// <summary>Height used when the element is not given one.</summary>
    protected virtual double DefaultHeight => 200;

    protected override Size MeasureOverride(Size availableSize)
    {
        double h = double.IsInfinity(availableSize.Height) ? DefaultHeight : Math.Min(DefaultHeight, availableSize.Height);
        return new Size(0, h);
    }

    // ───────────── Hover tracking ─────────────

    /// <summary>Mouse position while hovering, else null.</summary>
    protected Point? HoverPoint { get; private set; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        HoverPoint = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverPoint = null;
        InvalidateVisual();
    }

    /// <summary>Simulates hovering (used by offscreen renders and tests); null clears it.</summary>
    public void SetHover(Point? point)
    {
        HoverPoint = point;
        InvalidateVisual();
    }

    // ───────────── Drawing helpers ─────────────

    protected double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    protected FormattedText Text(string text, double size, Brush brush, FontWeight? weight = null, bool mono = false)
    {
        var tf = new Typeface(mono ? MonoFontFamily : ChartFontFamily, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, brush, PixelsPerDip);
    }

    protected static Pen MakePen(Brush brush, double thickness, double[]? dashes = null)
    {
        var p = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dashes is not null) p.DashStyle = new DashStyle(dashes, 0);
        p.Freeze();
        return p;
    }

    protected static Brush Fade(Brush b, double opacity) => SkillVisuals.WithOpacity(b, opacity);

    /// <summary>Transparent background so the whole area receives mouse events.</summary>
    protected void DrawHitArea(DrawingContext dc) => dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

    protected void DrawCentered(DrawingContext dc, string message)
    {
        var ft = Text(message, BaseFontSize, MutedBrush);
        dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
    }

    /// <summary>Draws a themed tooltip box next to <paramref name="anchor"/>, kept inside the element.</summary>
    protected void DrawTooltip(DrawingContext dc, Point anchor, string title, IReadOnlyList<TooltipLine> lines)
    {
        double fs = BaseFontSize * 0.95;
        const double pad = 8, swatch = 8, rowGap = 3;
        var titleFt = Text(title, fs, TextBrush, FontWeights.SemiBold);
        var rows = lines.Select(l => (l,
            label: Text(l.Label, fs, l.Emphasis ? TextBrush : MutedBrush, l.Emphasis ? FontWeights.SemiBold : FontWeights.Normal),
            value: Text(l.Value, fs, TextBrush, l.Emphasis ? FontWeights.Bold : FontWeights.SemiBold, mono: true))).ToList();
        double labelW = rows.Count == 0 ? 0 : rows.Max(r => r.label.Width + (r.l.Swatch is null ? 0 : swatch + 6));
        double valueW = rows.Count == 0 ? 0 : rows.Max(r => r.value.Width);
        double w = Math.Max(titleFt.Width, labelW + 16 + valueW) + pad * 2;
        double rowH = Math.Max(fs * 1.35, rows.Count == 0 ? 0 : rows.Max(r => r.value.Height));
        double h = pad * 2 + titleFt.Height + (rows.Count > 0 ? 4 + rows.Count * (rowH + rowGap) - rowGap : 0);

        double x = anchor.X + 14;
        if (x + w > ActualWidth - 2) x = anchor.X - 14 - w;
        if (x < 2) x = 2;
        double y = Math.Clamp(anchor.Y - h / 2, 2, Math.Max(2, ActualHeight - h - 2));
        var rect = new Rect(x, y, w, h);
        dc.DrawRoundedRectangle(SurfaceBrush, MakePen(GridBrush, 1), rect, 5, 5);
        dc.DrawText(titleFt, new Point(x + pad, y + pad));
        double ry = y + pad + titleFt.Height + 4;
        foreach (var (l, label, value) in rows)
        {
            double lx = x + pad;
            if (l.Swatch is not null)
            {
                dc.DrawRoundedRectangle(l.Swatch, null, new Rect(lx, ry + (rowH - swatch) / 2, swatch, swatch), 2, 2);
                lx += swatch + 6;
            }
            dc.DrawText(label, new Point(lx, ry + (rowH - label.Height) / 2));
            dc.DrawText(value, new Point(x + w - pad - value.Width, ry + (rowH - value.Height) / 2));
            ry += rowH + rowGap;
        }
    }

    /// <summary>Horizontal grid lines with abbreviated labels; returns the plot's left edge.</summary>
    protected double MeasureAxisLabelWidth(IEnumerable<double> ticks, Func<double, string> format)
    {
        double max = 0;
        foreach (var t in ticks) max = Math.Max(max, Text(format(t), BaseFontSize * 0.85, MutedBrush, mono: true).Width);
        return max;
    }

    protected void DrawYAxis(DrawingContext dc, Rect plot, IReadOnlyList<double> ticks, double niceMax, Func<double, string> format)
    {
        var gridPen = MakePen(Fade(GridBrush, 0.8), 1, [2, 3]);
        foreach (var t in ticks)
        {
            double y = plot.Bottom - t / niceMax * plot.Height;
            y = Math.Round(y) + 0.5;
            dc.DrawLine(t == 0 ? MakePen(GridBrush, 1) : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text(format(t), BaseFontSize * 0.85, MutedBrush, mono: true);
            dc.DrawText(ft, new Point(plot.Left - 6 - ft.Width, y - ft.Height / 2));
        }
    }

    protected void DrawTimeAxis(DrawingContext dc, Rect plot, double durationSeconds)
    {
        if (durationSeconds <= 0) return;
        double step = SeriesMath.TimeStep(durationSeconds, plot.Width);
        var tickPen = MakePen(GridBrush, 1);
        for (double t = 0; t <= durationSeconds + 1e-6; t += step)
        {
            double x = plot.Left + t / durationSeconds * plot.Width;
            dc.DrawLine(tickPen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            var ft = Text(Fmt.Duration(t), BaseFontSize * 0.85, MutedBrush, mono: true);
            double lx = Math.Clamp(x - ft.Width / 2, plot.Left - 4, plot.Right - ft.Width + 4);
            dc.DrawText(ft, new Point(lx, plot.Bottom + 6));
        }
    }

    /// <summary>Draws a one-row (wrapping) legend; returns its height.</summary>
    protected double DrawLegend(DrawingContext dc, double left, double top, double width,
        IEnumerable<(string Name, Brush Brush, bool Strong)> items)
    {
        double fs = BaseFontSize * 0.9;
        double x = left, y = top, rowH = fs * 1.5;
        foreach (var (name, brush, strong) in items)
        {
            var ft = Text(name, fs, strong ? TextBrush : MutedBrush, strong ? FontWeights.SemiBold : FontWeights.Normal);
            double itemW = 10 + 5 + ft.Width + 14;
            if (x + itemW > left + width && x > left)
            {
                x = left;
                y += rowH;
            }
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y + (rowH - 10) / 2, 10, 10), 2.5, 2.5);
            dc.DrawText(ft, new Point(x + 15, y + (rowH - ft.Height) / 2));
            x += itemW;
        }
        return y - top + rowH;
    }
}
