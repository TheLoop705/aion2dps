using System.Windows.Media.Imaging;

namespace Aion2Dps.App.Controls;

/// <summary>The app emblem (a gold hexagon with a rising bar chart), generated in code: window icons, tray icon, sidebar.</summary>
public static class AppIcon
{
    private static ImageSource? _image;

    public static Geometry Hex { get; } = Frozen("M16,1 L29,8.5 L29,23.5 L16,31 L3,23.5 L3,8.5 Z");
    public static Geometry Bars { get; } = Frozen("M9,21 L12,21 L12,17 L9,17 Z M14.5,21 L17.5,21 L17.5,12 L14.5,12 Z M20,21 L23,21 L23,8.5 L20,8.5 Z");

    /// <summary>Drawing in a 32×32 box.</summary>
    public static DrawingGroup Drawing(Color? fill = null, Color? glyph = null)
    {
        var g = new DrawingGroup();
        var gold = new LinearGradientBrush(Color.FromRgb(0xF2, 0xC8, 0x6A), fill ?? Color.FromRgb(0xB7, 0x83, 0x2C), 90);
        g.Children.Add(new GeometryDrawing(gold, new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x40, 0x14)), 1.2), Hex));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(glyph ?? Color.FromRgb(0x1B, 0x16, 0x0C)), null, Bars));
        g.Freeze();
        return g;
    }

    public static ImageSource ImageSource => _image ??= CreateImage();

    private static ImageSource CreateImage()
    {
        var img = new DrawingImage(Drawing());
        img.Freeze();
        return img;
    }

    /// <summary>Rasterizes the emblem (for the tray icon).</summary>
    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
            dc.DrawDrawing(Drawing());
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static Geometry Frozen(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }
}
