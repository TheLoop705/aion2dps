using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>Renders elements to bitmaps (the "copy as image" feature and offscreen previews).</summary>
public static class ImageExport
{
    /// <summary>
    /// Renders <paramref name="parts"/> stacked vertically at their full laid-out size, margins included. Content
    /// scrolled out of view is included because each part is drawn through a <see cref="VisualBrush"/> rather than
    /// through its clipping parent.
    /// </summary>
    public static BitmapSource RenderStacked(Brush? background, double dpi, params FrameworkElement[] parts)
    {
        var items = parts.Where(p => p.ActualWidth > 0 && p.ActualHeight > 0 && p.Visibility == Visibility.Visible)
            .Select(p => LayoutBrush(p, includeMargin: true)).ToList();
        double width = items.Count == 0 ? 1 : items.Max(i => i.Size.Width);
        double height = Math.Max(1, items.Sum(i => i.Size.Height));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (background is not null) dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            double y = 0;
            foreach (var (brush, size) in items)
            {
                dc.DrawRectangle(brush, null, new Rect(0, y, size.Width, size.Height));
                y += size.Height;
            }
        }
        return ToBitmap(dv, width, height, dpi);
    }

    /// <summary>Renders one laid-out element at its full size (ignoring clipping by its parents).</summary>
    public static BitmapSource Render(FrameworkElement element, double dpi = 96)
    {
        var (brush, size) = LayoutBrush(element, includeMargin: false);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(size));
        return ToBitmap(dv, size.Width, size.Height, dpi);
    }

    private static BitmapSource ToBitmap(Visual visual, double width, double height, double dpi)
    {
        double scale = dpi / 96.0;
        var rtb = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// A brush showing exactly the element's layout rectangle. A VisualBrush's content includes the visual's own
    /// offset inside its parent and its default viewbox is the content bounding box (which would drop padding), so
    /// the viewbox is set explicitly in that offset coordinate space.
    /// </summary>
    private static (VisualBrush Brush, Size Size) LayoutBrush(FrameworkElement element, bool includeMargin)
    {
        var offset = VisualTreeHelper.GetOffset(element);
        var m = includeMargin ? element.Margin : new Thickness();
        var box = new Rect(offset.X - m.Left, offset.Y - m.Top,
            element.ActualWidth + m.Left + m.Right, element.ActualHeight + m.Top + m.Bottom);
        var brush = new VisualBrush(element)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = box,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
        };
        return (brush, box.Size);
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Puts the bitmap on the clipboard; false when the clipboard is busy.</summary>
    public static bool CopyToClipboard(BitmapSource bitmap)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetImage(bitmap);
                return true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
            {
                AppLog.Debug("Analysis", "Clipboard busy: " + ex.Message);
                Thread.Sleep(40);
            }
        }
        AppLog.Warn("Analysis", "Could not copy the image to the clipboard.");
        return false;
    }
}
