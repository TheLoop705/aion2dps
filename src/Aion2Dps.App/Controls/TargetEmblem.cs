using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Aion2Dps.App.Controls;

/// <summary>
/// The target "portrait": a hexagonal crest whose fill drains from the top as the target loses HP (a second
/// at-a-glance HP gauge). The fill colour is set by the overlay to match the HP bar. The glyph is drawn dark over the
/// filled part and light over the drained part so it stays readable at any HP.
/// </summary>
public sealed class TargetEmblem : Grid
{
    private static readonly Geometry Hex = Freeze(Geometry.Parse("M17,1 L31,9 L31,25 L17,33 L3,25 L3,9 Z"));

    private readonly Path _track = new() { Data = Hex, Stretch = Stretch.Fill, StrokeThickness = 0 };
    private readonly Path _fill = new() { Data = Hex, Stretch = Stretch.Fill, StrokeThickness = 0 };
    private readonly Path _outline = new() { Data = Hex, Stretch = Stretch.Fill, StrokeThickness = 1.5, Fill = null };
    private readonly Path _glyphOnFill = new() { Stretch = Stretch.Uniform };
    private readonly Path _glyphOnEmpty = new() { Stretch = Stretch.Uniform, Opacity = 0.8 };
    private readonly RectangleGeometry _fillClip = new();
    private readonly RectangleGeometry _emptyClip = new();
    private double _fraction = 1;

    public TargetEmblem(double size = 36)
    {
        Width = size;
        Height = size;
        _track.SetResourceReference(Shape.FillProperty, ThemeKeys.BarTrack);
        _outline.SetResourceReference(Shape.StrokeProperty, ThemeKeys.Accent);
        _glyphOnEmpty.SetResourceReference(Shape.FillProperty, ThemeKeys.Text);
        var dark = new SolidColorBrush(Color.FromArgb(0xE0, 0x0E, 0x0E, 0x12));
        dark.Freeze();
        _glyphOnFill.Fill = dark;
        _fill.Clip = _fillClip;
        _glyphOnFill.Clip = _fillClip;
        _glyphOnEmpty.Clip = _emptyClip;
        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_outline);
        // Glyphs share the emblem's coordinate space so one pair of clip rectangles serves both.
        var pad = size * 0.26;
        _glyphOnFill.Margin = _glyphOnEmpty.Margin = new Thickness(pad);
        Children.Add(_glyphOnEmpty);
        Children.Add(_glyphOnFill);
        Glyph = Glyphs.Boss;
        SizeChanged += (_, _) => UpdateClip();
        UpdateClip();
    }

    public Glyphs.Glyph Glyph
    {
        set
        {
            _glyphOnFill.Data = value.Fill;
            _glyphOnEmpty.Data = value.Fill;
        }
    }

    /// <summary>Fill brush (current HP colour).</summary>
    public Brush? FillBrush
    {
        get => _fill.Fill;
        set => _fill.Fill = value;
    }

    /// <summary>0..1 HP remaining; null = unknown (shown full, dimmed).</summary>
    public double? Fraction
    {
        set
        {
            _fraction = Math.Clamp(value ?? 1, 0, 1);
            _fill.Opacity = value is null ? 0.45 : 1;
            UpdateClip();
        }
    }

    private void UpdateClip()
    {
        double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(w) || double.IsNaN(h)) return;
        double top = h * (1 - _fraction);
        _fillClip.Rect = new Rect(-2, top, w + 4, Math.Max(0, h - top) + 2);
        _emptyClip.Rect = new Rect(-2, -2, w + 4, top + 2);
        // The glyph paths are offset by their margin, so translate their clips into the path's own space.
        double pad = _glyphOnFill.Margin.Left;
        var t = new TranslateTransform(-pad, -pad);
        t.Freeze();
        var gf = new RectangleGeometry(_fillClip.Rect) { Transform = t };
        var ge = new RectangleGeometry(_emptyClip.Rect) { Transform = t };
        _glyphOnFill.Clip = gf;
        _glyphOnEmpty.Clip = ge;
    }

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }
}
