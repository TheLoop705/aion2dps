using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Aion2Dps.App.Controls;

/// <summary>A small rounded badge filled with the class brush (<see cref="ThemeKeys.ClassBrush"/>) carrying the class glyph.</summary>
public sealed class ClassEmblem : Border
{
    private readonly Path _fill = new() { Stretch = Stretch.Uniform };
    private readonly Path _stroke = new() { Stretch = Stretch.Uniform, StrokeThickness = 1.4, Fill = null };
    private CharacterClass _class = (CharacterClass)(-1);

    public ClassEmblem(double size = 16)
    {
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(Math.Max(2, size / 5));
        SnapsToDevicePixels = true;
        var glyphBrush = new SolidColorBrush(Color.FromArgb(0xD8, 0x10, 0x10, 0x14));
        glyphBrush.Freeze();
        _fill.Fill = glyphBrush;
        _stroke.Stroke = glyphBrush;
        var grid = new Grid { Margin = new Thickness(size * 0.16) };
        grid.Children.Add(_fill);
        grid.Children.Add(_stroke);
        Child = grid;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public CharacterClass Class
    {
        get => _class;
        set
        {
            if (_class == value) return;
            _class = value;
            SetResourceReference(BackgroundProperty, ThemeKeys.ClassBrush(value));
            var g = Glyphs.ForClass(value);
            _fill.Data = g.Fill;
            _stroke.Data = g.Stroke;
            _stroke.Visibility = g.Stroke is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
