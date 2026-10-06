using System.Windows.Media.Imaging;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;

namespace Aion2Dps.App.Rendering;

/// <summary>Renders WPF elements to bitmaps without showing any window (must run on an STA thread).</summary>
public static class OffscreenRenderer
{
    /// <summary>Wraps <paramref name="content"/> in a root that carries a full theme dictionary (palette + control styles).</summary>
    public static Border Themed(FrameworkElement content, ThemeDefinition theme, AppearanceSettings? custom = null, Brush? backdrop = null, Thickness? padding = null)
    {
        var root = new Border
        {
            Child = content,
            Padding = padding ?? new Thickness(0),
            Resources = ThemeManager.BuildFull(theme, custom),
            Background = backdrop ?? Brushes.Transparent,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        return root;
    }

    /// <summary>A neutral "game scene" gradient used behind translucent overlays.</summary>
    public static Brush SceneBackdrop()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x56, 0x6E, 0x7C), 0),
                new GradientStop(Color.FromRgb(0x3B, 0x4A, 0x3E), 0.45),
                new GradientStop(Color.FromRgb(0x2A, 0x25, 0x2E), 1),
            },
        };
        b.Freeze();
        return b;
    }

    /// <summary>Measures/arranges <paramref name="element"/> at <paramref name="width"/> (height = desired unless given) and renders it.</summary>
    public static BitmapSource Render(FrameworkElement element, double width, double? height = null, double scale = 1.5)
    {
        element.Measure(new Size(width, height ?? double.PositiveInfinity));
        double h = height ?? Math.Ceiling(element.DesiredSize.Height);
        element.Arrange(new Rect(0, 0, width, h));
        element.UpdateLayout();
        // Second pass: SizeChanged handlers (e.g. table bars) may have changed widths.
        element.Measure(new Size(width, height ?? double.PositiveInfinity));
        h = height ?? Math.Ceiling(element.DesiredSize.Height);
        element.Arrange(new Rect(0, 0, width, h));
        element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));

        int pw = Math.Max(1, (int)Math.Ceiling(width * scale));
        int ph = Math.Max(1, (int)Math.Ceiling(h * scale));
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(element);
        rtb.Freeze();
        return rtb;
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static string RenderToPng(FrameworkElement element, double width, string path, double? height = null, double scale = 1.5)
    {
        SavePng(Render(element, width, height, scale), path);
        return path;
    }
}
